using System;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace NoodlesMcDoodles.Core
{
    /// <summary>
    /// P7 (doc 02, Hook 6) — the postfix-only counters that turn "zero data loss" from a claim into log-grep
    /// evidence. Every postfix here gates its increment on <see cref="KillSwitch.Enabled"/> (not vanilla
    /// behaviour, so never a prefix; never skips anything) — matching Hook 6's own stated "Off behaviour":
    /// "postfixes no-op ... the periodic flush timer still fires but logs kill_switch=off and zeroes, so an
    /// operator can tell telemetry itself is alive even while everything else is stood down."
    ///
    /// One addition beyond doc 02's literal three-row table: a small prefix+postfix pair on <c>ZNet.Save</c>
    /// itself, server-only. Doc 02's own Telemetry section and "Save health" guidance promise
    /// <c>SavesCompleted</c>/<c>SavesSkipped</c>/<c>SaveDurationMs</c> as live, meaningful signals from day
    /// one ("SavesSkipped incrementing at all outside an expected SkipSaving() window ... needs investigation
    /// before trusting any subsequent SafeToggle-gated feature flip") — but <see cref="SafeToggle"/> has no
    /// caller anywhere in M1 (no M1 lever flips a live-authority toggle yet), so wiring those three counters
    /// only through <see cref="SafeToggle.TryEnable"/> would leave them silently at zero for the entire
    /// milestone regardless of how the server's REAL autosave/backup cycle is actually behaving. This postfix
    /// observes every <c>ZNet.Save</c> call (autosave, manual `save` command, a future SafeToggle caller —
    /// all of them), using the exact same four public skip-state checks <see cref="SafeToggle"/> already uses,
    /// so the counters are real from the first autosave rather than only once Phase B exists.
    /// </summary>
    internal static class TelemetryHooks
    {
        // private ZDO CreateNewZDO(ZDOID uid, Vector3 position, int prefabHashIn = 0) — SERVER:76731,
        // confirmed the counted overload (doc 02 Hook 6 table); the OTHER, public CreateNewZDO(Vector3, int)
        // at SERVER:76721 is a thin wrapper that calls straight into this one, so counting here (not both)
        // avoids double-counting every server-originated ZDO creation.
        [HarmonyPatch(typeof(ZDOMan), "CreateNewZDO", new[] { typeof(ZDOID), typeof(Vector3), typeof(int) })]
        internal static class Patch_ZDOMan_CreateNewZDO_Telemetry
        {
            static void Postfix()
            {
                if (!KillSwitch.Enabled) return;
                Telemetry.Counters.ZdosCreated.Increment();
            }
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.DestroyZDO))]
        internal static class Patch_ZDOMan_DestroyZDO_Telemetry
        {
            static void Postfix()
            {
                if (!KillSwitch.Enabled) return;
                Telemetry.Counters.ZdosDestroyed.Increment();
            }
        }

        /// <summary>
        /// RED-TEAM FIX carried over verbatim (doc 02 Hook 6): <c>SendZDOs</c>'s own <c>bool</c> return has
        /// THREE distinct false paths (queue-already-over-gate, the real near-miss, and "nothing queued to
        /// send" — the common, harmless idle-tick case), so a postfix that trusted <c>__result</c> alone would
        /// be dominated by the third, harmless path on nearly every idle tick per peer. This recomputes the
        /// near-miss condition itself from the peer's own live send-queue depth instead.
        /// </summary>
        [HarmonyPatch(typeof(ZDOMan), "SendZDOs")]
        internal static class Patch_ZDOMan_SendZDOs_Telemetry
        {
            static void Postfix(ZDOMan.ZDOPeer peer)
            {
                if (!KillSwitch.Enabled) return;
                try
                {
                    int sendQueueSize = peer.m_peer.m_socket.GetSendQueueSize();
                    // The live gate this call used (Q3/Q13 budget and Q4 floor; vanilla's 10240/2048 when those are
                    // off or not eligible here). PeerBudget.Current is still set: its finalizer runs after postfixes.
                    // Was hardcoded to 10240/2048, which went blind once real budgets exceeded vanilla's.
                    int budget = NoodlesMcDoodles.Transport.SharedBudget.InnerBudgetBytes;
                    int floor = NoodlesMcDoodles.Transport.SharedBudget.CongestionFloorBytes;
                    if (budget - sendQueueSize < floor) // the real near-miss condition, independent of __result.
                    {
                        Telemetry.Counters.SendBudgetNearMisses.Increment(peer.m_peer);
                    }
                    Telemetry.Counters.SteamSendQueueHighWaterBytes.Max(peer.m_peer, sendQueueSize);
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_ZDOMan_SendZDOs_Telemetry), e);
                }
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Save))]
        internal static class Patch_ZNet_Save_Telemetry
        {
            static void Prefix(out Stopwatch __state)
            {
                __state = Stopwatch.StartNew();
            }

            static void Postfix(Stopwatch __state)
            {
                if (!KillSwitch.Enabled) return;
                // RUNTIME REVIEW FIX (major, mixed-and-host, 2026-09-28): was PatchSide.ServerOnly (never even
                // offered to a client-build process), which silently excluded a listen-server host's own real
                // world saves. Now Both, with this explicit runtime check taking its place: only an
                // authoritative process's ZNet.Save (dedicated server OR listen host) should count toward
                // SavesCompleted/SavesSkipped/save_duration_ms — a plain remote client's own harmless ZNet.Save
                // no-op (Save() only actually saves the world when m_isServer, CLIENT:80414) must never inflate
                // those counters.
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                try
                {
                    __state.Stop();
                    bool skipped = ZNet.m_loadError
                        || ZoneSystem.instance.SkipSaving()
                        || DungeonDB.instance.SkipSaving()
                        || SaveSystem.HasSessionFlag(SaveSystemSessionFlags.DontSaveWorld);

                    if (skipped)
                    {
                        Telemetry.Counters.SavesSkipped.Increment();
                    }
                    else
                    {
                        Telemetry.Counters.SavesCompleted.Increment();
                        Telemetry.SetGauge("save_duration_ms", __state.Elapsed.TotalMilliseconds);
                    }
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_ZNet_Save_Telemetry), e);
                }
            }
        }

        [NoodlesModuleInit(10)]
        internal static void Init()
        {
            // Pure observation, no vanilla-behaviour change and no per-feature off-switch of its own (see
            // class remarks on why these gate on the kill switch directly) — registered under one umbrella
            // "Telemetry" name so a patch failure here is attributable in "noodles status" without implying a
            // config flag that doesn't exist in DESIGN.md's Configuration reference.
            PatchRegistry.Register("Telemetry.CreateNewZDO", typeof(Patch_ZDOMan_CreateNewZDO_Telemetry), PatchSide.Both);
            PatchRegistry.Register("Telemetry.DestroyZDO", typeof(Patch_ZDOMan_DestroyZDO_Telemetry), PatchSide.Both);
            PatchRegistry.Register("Telemetry.SendZDOs", typeof(Patch_ZDOMan_SendZDOs_Telemetry), PatchSide.Both);
            PatchRegistry.Register("Telemetry.Save", typeof(Patch_ZNet_Save_Telemetry), PatchSide.Both);
        }
    }
}
