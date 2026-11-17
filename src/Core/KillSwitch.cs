using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace NoodlesMcDoodles.Core
{
    /// <summary>
    /// The global kill switch. Not a game hook itself (doc 02 Hook 5) — every OTHER hook in the whole mod
    /// reads <see cref="Enabled"/> (via <see cref="FeatureFlag.Active"/>) first, and a supervisor here can
    /// flip it off on its own (<see cref="AutoStandDown"/>).
    ///
    /// Three ways this changes, all converging on the same <see cref="NoodlesConfig.CoreEnabled"/> config
    /// entry so there is exactly one code path that reacts to a transition (never three separate ones that
    /// could disagree):
    /// 1. <see cref="SetEnabled"/> — the "noodles enable|disable" console command, or an AutoStandDown trip.
    /// 2. A live edit to the .cfg file picked up by <see cref="ConfigWatcher"/>'s <c>Config.Reload()</c>.
    /// 3. BepInEx's own config GUI/F1 menu, if the operator has one installed (not assumed, but not excluded).
    ///
    /// All three end up writing <see cref="NoodlesConfig.CoreEnabled"/>, which fires
    /// <c>ConfigEntry.SettingChanged</c> — the ONE place a false->true or true->false transition is detected
    /// and acted on (flush callbacks, log, telemetry tag). This is deliberately not "SetEnabled runs the
    /// flush itself" — a bare `Config.Reload()` from a headless-operator file edit never calls
    /// <see cref="SetEnabled"/> at all, so the flush logic has to live at the point every path actually
    /// converges: the config entry's own change event.
    /// </summary>
    internal static class KillSwitch
    {
        public static bool Enabled => NoodlesConfig.CoreEnabled.Value;

        private static bool s_lastKnownEnabled = true;
        private static readonly List<(string owner, Action callback)> s_flushCallbacks = new List<(string, Action)>();

        internal static void Init()
        {
            s_lastKnownEnabled = NoodlesConfig.CoreEnabled.Value;
            // RUNTIME REVIEW FIX (major): BepInEx does NOT guarantee ConfigEntry.SettingChanged fires on the
            // main Unity thread (MainThreadDispatcher's own class doc, and AutoStandDown.RecordPatchException's
            // doc comment below, both call this out), and HandleEnabledTransition runs every registered flush
            // callback synchronously — callbacks the shared-infra contract expects to touch ZDOMan/ZNet/
            // ownership state safely. ConfigWatcher.OnDebounceElapsed and SimulationDistanceCeiling's own
            // SettingChanged handler already marshal through MainThreadDispatcher.Enqueue for exactly this
            // reason; KillSwitch — the one place every OTHER SettingChanged-triggered, ownership-touching path
            // in this mod ultimately funnels through — must do the same, so a SetEnabled call from any thread
            // (including AutoStandDown's own exception-burst trigger, explicitly documented as possibly
            // off-thread) always runs its flush callbacks on the main thread.
            NoodlesConfig.CoreEnabled.SettingChanged += (_, __) => MainThreadDispatcher.Enqueue(HandleEnabledTransition);
        }

        /// <summary>
        /// Ownership-holding (or any other live-authority-changing) features must call this with their own
        /// revert-to-vanilla logic — per the shared-infra contract (DESIGN.md Architecture, doc 02's P4 x
        /// G6-N3 coupling): "disable" means every held object/state is actively released, not merely
        /// "stop granting new holds". Every callback runs even if an earlier one throws.
        /// </summary>
        public static void RegisterFlushCallback(string ownerName, Action callback)
        {
            s_flushCallbacks.Add((ownerName, callback));
        }

        /// <summary>
        /// The one place any code (console command, AutoStandDown) asks for the switch to change state.
        /// Writing the config value is what does the real work — see the class remarks for why the actual
        /// flush logic lives in <see cref="HandleEnabledTransition"/> instead of here.
        /// </summary>
        public static void SetEnabled(bool value, string reason, bool automatic = false)
        {
            NoodlesLog.Notice($"[1 - General] Enabled -> {value} ({(automatic ? "automatic" : "manual")}): {reason}");
            if (automatic)
            {
                Telemetry.Counters.AutoStandDownEvents.Increment();
                Telemetry.SetTag("auto_standdown", reason.Length > 60 ? reason.Substring(0, 60) : reason);
            }
            NoodlesConfig.CoreEnabled.Value = value;
        }

        private static void HandleEnabledTransition()
        {
            bool now = NoodlesConfig.CoreEnabled.Value;
            if (now == s_lastKnownEnabled) return; // re-set to the same value / duplicate event — not a real transition
            s_lastKnownEnabled = now;

            if (!now)
            {
                NoodlesLog.Warning("[1 - General] Enabled is now OFF — every NoodlesMcDoodles hook reverts to vanilla " +
                    "behaviour; running all registered flush callbacks.");
                foreach (var (owner, callback) in s_flushCallbacks)
                {
                    try { callback(); }
                    catch (Exception e) { NoodlesLog.PatchException($"kill-switch flush callback ({owner})", e); }
                }
            }
            else
            {
                NoodlesLog.Notice("[1 - General] Enabled is now ON — NoodlesMcDoodles behaviours re-armed. Any feature " +
                    "individually forced off (see 'noodles status') stays off until that condition is fixed.");
            }

            Telemetry.SetTag("kill_switch", now ? "on" : "off");

            // L6 (deployment-modes review, 2026-09-28): the per-peer "features:" lines are one-shot, logged
            // once at negotiation time -- a flip here changes what's actually applied for every already-
            // negotiated peer at once, with nothing else to re-log against, so it gets its own short line.
            try { DeploymentLog.LogKillSwitchTransition(now); }
            catch (Exception e) { NoodlesLog.PatchException(nameof(DeploymentLog.LogKillSwitchTransition), e); }
        }

        /// <summary>Human-readable summary for "noodles status" — never throws, safe to call from any state.</summary>
        public static string StatusLine()
        {
            var forcedOff = new List<string>();
            foreach (var f in Features.All)
            {
                if (f.IsForcedOff) forcedOff.Add($"{f.Name} ({f.ForcedOffReason})");
            }

            string forcedOffText = forcedOff.Count == 0 ? "none" : string.Join(", ", forcedOff);
            return $"NoodlesMcDoodles {PluginInfo.PLUGIN_VERSION} — Enabled={(Enabled ? "on" : "off")}, " +
                   $"AutoStandDown={(NoodlesConfig.AutoStandDownEnabled.Value ? "on" : "off")}, " +
                   $"forced-off features: {forcedOffText}";
        }

        /// <summary>
        /// AutoStandDown trigger #3 (exception burst) lives here rather than in <see cref="NoodlesLog"/>
        /// itself, since it needs to actually flip the switch. Trigger #1 (ZDO mismatch) is driven from
        /// <see cref="Telemetry.FlushAndLog"/> (it needs two consecutive periods, which only the telemetry
        /// timer tracks). Trigger #2 (disk space, twice in a row) lives in <see cref="SafeToggle"/> — it's the
        /// one caller of the forced pre-toggle save and already owns that state.
        /// </summary>
        public static class AutoStandDown
        {
            // Wall-clock (not UnityEngine.Time) so this is safe to call from any thread without a main-thread
            // dependency — a patch exception could in principle be caught somewhere off the main Unity thread.
            private static readonly ConcurrentQueue<DateTime> s_exceptionTimestamps = new ConcurrentQueue<DateTime>();

            public static void RecordPatchException(string patchName)
            {
                if (!NoodlesConfig.AutoStandDownEnabled.Value) return;

                var now = DateTime.UtcNow;
                s_exceptionTimestamps.Enqueue(now);
                while (s_exceptionTimestamps.TryPeek(out var oldest) && (now - oldest).TotalSeconds > 60)
                {
                    s_exceptionTimestamps.TryDequeue(out _);
                }

                int limit = NoodlesConfig.AutoStandDownExceptionBurstLimit.Value;
                if (s_exceptionTimestamps.Count > limit)
                {
                    SetEnabled(false,
                        $"exception burst: {s_exceptionTimestamps.Count} caught patch exceptions in the last 60s " +
                        $"(limit {limit}), most recently from '{patchName}'", automatic: true);
                }
            }
        }
    }
}
