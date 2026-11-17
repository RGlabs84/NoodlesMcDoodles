using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Configuration;

namespace NoodlesMcDoodles.Core
{
    /// <summary>
    /// A single gated behaviour. <see cref="Active"/> is the one thing every patch in every module should read
    /// (never the raw <see cref="ConfigEntry{T}"/> directly) — it's live (no caching), so a config edit or a
    /// live <see cref="ForceOff"/> takes effect on the very next call, matching the shared-infra contract's
    /// "every gated field a mutable read, never a transpiled literal" rule (DESIGN.md Architecture / doc 02 P4).
    ///
    /// <c>Active = Core.Enabled &amp;&amp; ownFlag &amp;&amp; !standDown</c> exactly per the task brief:
    /// "own flag" is the bound config bool; "not stood down" is <see cref="ForceOff"/>, a narrower switch than
    /// the global kill switch that lets <see cref="PatchRegistry"/> (a patch failed to apply/JIT) or a
    /// feature's own code (e.g. ConfigSync's RPC-name-collision guard, doc 02 Hook 3) disable just THIS
    /// feature without touching <see cref="KillSwitch"/>.
    /// </summary>
    internal sealed class FeatureFlag
    {
        private readonly ConfigEntry<bool> _config;
        private volatile bool _forcedOff;
        private string _forcedOffReason;

        public string Name { get; }

        /// <summary>The live, fully-resolved gate every patch should check.</summary>
        public bool Active => KillSwitch.Enabled && _config.Value && !_forcedOff;

        /// <summary>The raw config value, ignoring the kill switch and any force-off — for status/diagnostic display only.</summary>
        public bool ConfiguredOn => _config.Value;

        public bool IsForcedOff => _forcedOff;
        public string ForcedOffReason => _forcedOffReason;

        private Action _onForcedOff;

        /// <summary>
        /// This feature's own release callback (WP1, per-feature quarantine) — run once, synchronously, from
        /// inside <see cref="ForceOff"/>, right after <c>_forcedOff</c>/<c>_forcedOffReason</c> are already set
        /// and the standard "forced off" line is already logged. Mirrors <see cref="KillSwitch.RegisterFlushCallback"/>'s
        /// shape but scoped to just this one feature — and, unlike that one (many independent owners registering
        /// into a shared list), exactly ONE callback per feature: a feature owns exactly one release path. A
        /// second assignment is refused (logged, not thrown, and the FIRST callback keeps running) so two
        /// modules can never silently stomp on each other's release path for the same feature.
        ///
        /// Every current caller of <see cref="ForceOff"/> (<see cref="PatchRegistry"/>'s apply-time failures,
        /// <see cref="FeatureStandDown"/>'s exception-burst trip, and a feature's own code) runs on the main
        /// thread — Harmony patches on game methods never fire off-thread in this codebase (see
        /// <c>SetOwnerHoldGuard.cs</c>'s own remarks on this), unlike <c>ConfigFile.SettingChanged</c>/
        /// <c>FileSystemWatcher</c>, which is the ONLY reason <see cref="KillSwitch"/>'s own flush marshals
        /// through <see cref="MainThreadDispatcher"/>. So this is invoked directly, synchronously, with no
        /// marshalling of its own.
        /// </summary>
        public Action OnForcedOff
        {
            get => _onForcedOff;
            set
            {
                if (_onForcedOff != null)
                {
                    NoodlesLog.Warning($"feature '{Name}': OnForcedOff already has a release callback " +
                        "registered — ignoring a second registration (exactly one release callback per feature, by design).");
                    return;
                }
                _onForcedOff = value;
            }
        }

        internal FeatureFlag(string name, ConfigEntry<bool> config)
        {
            Name = name;
            _config = config;
        }

        /// <summary>
        /// Disable just this one feature (not the whole mod) until <see cref="ClearForcedOff"/> or a
        /// restart. Used by <see cref="PatchRegistry"/> when a feature's own patch class fails to apply or
        /// fails its post-patch JIT check, by <see cref="FeatureStandDown"/> when that one feature's own
        /// patches throw a burst of exceptions, and by any feature that detects its own unrecoverable runtime
        /// conflict (e.g. an RPC name hash collision).
        ///
        /// If this feature holds live state that must be actively released (not merely "stop granting new
        /// holds") — see <see cref="OnForcedOff"/> — that callback runs here, in its own try/catch, logging
        /// one more line either way. A throwing release callback never prevents the force-off itself from
        /// taking effect: <c>_forcedOff</c> is already set, above, before the callback ever runs.
        /// </summary>
        public void ForceOff(string reason)
        {
            _forcedOff = true;
            _forcedOffReason = reason;
            NoodlesLog.Warning($"feature '{Name}' forced off: {reason}");

            Action callback = _onForcedOff;
            if (callback == null) return;

            var sw = Stopwatch.StartNew();
            try
            {
                callback();
                NoodlesLog.Warning($"feature '{Name}' forced off: {reason} — release callback ran ({sw.ElapsedMilliseconds}ms)");
            }
            catch (Exception e)
            {
                NoodlesLog.Warning($"feature '{Name}' forced off: {reason} — release callback itself threw: {e.GetType().Name}");
            }
        }

        public void ClearForcedOff()
        {
            _forcedOff = false;
            _forcedOffReason = null;
        }
    }

    /// <summary>
    /// Registry every module (Core's own five, and every later module's Q2/O1/Z2/S1/C1/... flags) registers
    /// its feature flags into. One flag per behaviour, one config key per flag, bound to the shared
    /// <see cref="NoodlesConfig.File"/>. See `docs/impl/CORE-API.md` "Registering a feature flag" for the
    /// pattern a module should follow (typically: a static field holding the <see cref="FeatureFlag"/>, plus a
    /// thin wrapper class exposing <c>.Active</c> for readability at call sites, matching the dotted
    /// <c>Features.Transport.Q2.Active</c>-style names used throughout the design docs' own code sketches).
    /// </summary>
    internal static class Features
    {
        private static readonly Dictionary<string, FeatureFlag> s_flags = new Dictionary<string, FeatureFlag>();

        // Core's own five (doc 02) — every other module's flags are registered by that module itself.
        public static FeatureFlag HandshakeReentryGuard { get; private set; }
        public static FeatureFlag Negotiation { get; private set; }
        public static FeatureFlag ConfigSync { get; private set; }
        public static FeatureFlag SaveIntervalClamp { get; private set; }
        public static FeatureFlag BackupPolicy { get; private set; }

        internal static void InitCoreFlags()
        {
            HandshakeReentryGuard = Register("HandshakeReentryGuard", NoodlesConfig.FeatureHandshakeReentryGuard);
            Negotiation = Register("Negotiation", NoodlesConfig.FeatureNegotiation);
            ConfigSync = Register("ConfigSync", NoodlesConfig.FeatureConfigSync);
            SaveIntervalClamp = Register("SaveIntervalClamp", NoodlesConfig.FeatureSaveIntervalClamp);
            BackupPolicy = Register("BackupPolicy", NoodlesConfig.FeatureBackupPolicy);
        }

        /// <summary>
        /// Register a feature flag backed by an already-bound <see cref="ConfigEntry{T}"/> (the common case —
        /// most modules bind their own bool config entries the normal BepInEx way, then hand the resulting
        /// entry here so PatchRegistry/AutoStandDown/the status command can all see it uniformly).
        /// </summary>
        public static FeatureFlag Register(string name, ConfigEntry<bool> config)
        {
            var flag = new FeatureFlag(name, config);
            s_flags[name] = flag;
            return flag;
        }

        /// <summary>Look up a previously-registered flag by name (used by <see cref="PatchRegistry"/> and the admin command's status line). Null if never registered.</summary>
        public static FeatureFlag Find(string name) => s_flags.TryGetValue(name, out var f) ? f : null;

        public static IEnumerable<FeatureFlag> All => s_flags.Values;
    }
}
