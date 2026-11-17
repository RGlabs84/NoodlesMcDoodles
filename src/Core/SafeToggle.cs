using System;

namespace NoodlesMcDoodles.Core
{
    /// <summary>
    /// P3 (doc 02, Hook 9): the forced-save primitive every later-phase live-authority toggle (Phase B's
    /// ownership claim, most likely) must call through before flipping a behaviour that could otherwise lose
    /// a ZDO if the process crashed moments later with no fresh snapshot on disk. No caller exists in M1
    /// itself (no M1 lever flips a live-authority toggle) — this ships now as a primitive so Phase B doesn't
    /// have to invent it under schedule pressure, per the module doc's own stated milestone split.
    ///
    /// Not a Harmony patch — a call-site wrapper around vanilla's own public <c>ZNet.Save</c> API.
    /// <c>PrepareSave()</c> (called synchronously, inside <c>Save()</c>, before any background save thread
    /// exists — SERVER:80567-80581) is why <see cref="MainThreadDispatcher.AssertMainThread"/> is asserted
    /// first: a <c>ConfigFile.SettingChanged</c>/<see cref="ConfigWatcher"/> callback is not guaranteed to run
    /// on the main thread, and calling this off-thread would race unsynchronized against the real main
    /// thread's concurrent <c>ZDOMan.Update</c>/RPC writes.
    /// </summary>
    internal static class SafeToggle
    {
        private static int s_diskGuardConsecutiveFailures;

        /// <summary>
        /// Forces a synchronous, verified save, then runs <paramref name="enableAction"/> only if that save
        /// provably happened. Returns false (and runs nothing) if disk space is low or the save was silently
        /// skipped by one of <c>ZNet.Save</c>'s own latches — never assumes success from a <c>void</c> return.
        /// </summary>
        public static bool TryEnable(Action enableAction, string toggleName)
        {
            MainThreadDispatcher.AssertMainThread();

            if (ZNet.instance == null)
            {
                NoodlesLog.Warning($"SafeToggle: refusing to enable '{toggleName}' — ZNet.instance is null " +
                    "(no world loaded yet).");
                return false;
            }

            if (!ZNet.instance.EnoughDiskSpaceAvailable(out _))
            {
                s_diskGuardConsecutiveFailures++;
                NoodlesLog.Error($"SafeToggle: refusing to enable '{toggleName}' — low disk space " +
                    $"({s_diskGuardConsecutiveFailures} consecutive failure(s)).");
                Telemetry.Counters.SafeToggleRefusedDiskSpace.Increment();

                // AutoStandDown trigger #2 (doc 02 Hook 5): trip only on the SECOND consecutive failure, not
                // the first — archserver also autosaves two other Valheim servers, so a single I/O blip must
                // not stand down the mod that's supposed to be fixing the Arizona problem.
                if (s_diskGuardConsecutiveFailures >= 2)
                {
                    KillSwitch.SetEnabled(false, $"disk space guard before '{toggleName}' (2 in a row)", automatic: true);
                }
                return false;
            }
            s_diskGuardConsecutiveFailures = 0;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            ZNet.instance.Save(sync: true, saveOtherPlayerProfiles: true, waitForNextFrame: false); // blocks
            stopwatch.Stop();

            // ZNet.Save is void and has FOUR silent-skip paths of its own (confirmed SERVER:80221-80231 plus
            // the earlier DontSaveWorld check at SERVER:80223-80227) — all four are public/static, so this
            // checks the exact same state Save() itself just consulted, rather than trusting the void return.
            if (ZNet.m_loadError
                || ZoneSystem.instance.SkipSaving()
                || DungeonDB.instance.SkipSaving()
                || SaveSystem.HasSessionFlag(SaveSystemSessionFlags.DontSaveWorld))
            {
                NoodlesLog.Error($"SafeToggle: refusing to enable '{toggleName}' — the pre-toggle safety save " +
                    "was silently skipped (ZoneSystem/DungeonDB SkipSaving, a load error, or a DontSaveWorld " +
                    "session flag — e.g. an admin ran nextseed/a location test this session). Restart the " +
                    "server to clear the latch before retrying.");
                Telemetry.Counters.SafeToggleRefusedSkippedSave.Increment();
                Telemetry.Counters.SavesSkipped.Increment();
                return false; // do NOT run enableAction() on an unconfirmed save.
            }

            Telemetry.Counters.SavesCompleted.Increment();
            Telemetry.SetGauge("save_duration_ms", stopwatch.Elapsed.TotalMilliseconds);

            enableAction();
            Telemetry.Counters.SafeToggleEnables.Increment();
            return true;
        }
    }
}
