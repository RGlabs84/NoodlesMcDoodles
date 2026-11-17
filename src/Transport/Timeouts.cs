using System;
using HarmonyLib;
using NoodlesMcDoodles.Core;

namespace NoodlesMcDoodles.Transport
{
    /// <summary>
    /// Hook 3 (T4) — <c>ZRpc.m_pingInterval</c>/<c>m_timeout</c> reassertion (docs/design/01 Hook 3). Both are
    /// process-wide <c>private static float</c> fields (SERVER:83859/83861 — <c>ZRpc</c> carries no
    /// build-specific branch anywhere in this doc's re-read, confirmed byte-identical on the client build) with
    /// exactly one other writer in the whole assembly, <c>ZRpc.SetLongTimeout(bool)</c> (SERVER:84201-84210):
    /// called with <c>false</c> (-&gt;30s) as the literal first statement of <c>ZNet.Start()</c>, and with
    /// <c>true</c> (-&gt;90s) from <c>ZPlayFabSocket.OnNetworkJoined</c> (SERVER:91357-91365) and
    /// <c>ZPlayFabSocket.Accept</c> (SERVER:91712-91719) — both of which fire on EVERY crossplay join/accept,
    /// not just the first. Since the field is <c>static</c>, any of these three vanilla call sites silently
    /// clobbers this mod's configured 60s value process-wide; reasserting after each is the only way to keep a
    /// stable configured value across a session that includes any PlayFab peer.
    ///
    /// Guard (per research/10-LEVERS.md's own T4 note): this only ever reasserts ONE configured process-wide
    /// value — it must never be called reactively for a single struggling peer, since the field isn't
    /// per-connection.
    /// </summary>
    internal static class RpcTimeoutController
    {
        // Vanilla's own field initializer (SERVER:83859, confirmed byte-identical on the client build per this
        // class's own doc comment) — the value m_pingInterval holds whenever nothing has ever reasserted it.
        private const float VanillaPingIntervalSec = 1f;

        public static void Reassert()
        {
            if (!TransportLeverConfig.T4.Active) return;

            // RED-TEAM FIX (DATA-LOSS REVIEW, major — missing isAuthoritative/Q10 gate, same shape as every
            // sibling Transport lever: SteamRateController.Resync/RT1, AdaptiveRate.EligibleHere,
            // SharedBudget.EligibleForThisProcess, ClientSendLoopModule.ShouldApplyOnThisProcess). Without this,
            // a plain non-authoritative client with no Q10 opt-in still had its own process-wide
            // ZRpc.m_pingInterval/m_timeout overwritten by this mod's configured value every time ZNet.Start()
            // ran or it made/accepted a PlayFab connection — regardless of the remote peer's build. A genuine
            // remote client without the explicit Q10 opt-in must never have this touched at all.
            bool isAuthoritative = ZNet.instance != null && ZNet.instance.IsServer();
            if (!isAuthoritative && !TransportLeverConfig.Q10Active) return;

            // Private static fields, no public accessor — AccessTools.StaticFieldRefAccess bypasses the
            // Publicizer's compile-time-only visibility the same defensive way Core's own ZNetHelpers does for
            // GetPeer, rather than relying on direct field syntax against the real (still-private) runtime DLL.
            AccessTools.StaticFieldRefAccess<float>(typeof(ZRpc), "m_pingInterval") = TransportLeverConfig.T4_PingIntervalSec.Value;
            AccessTools.StaticFieldRefAccess<float>(typeof(ZRpc), "m_timeout") = TransportLeverConfig.T4_TimeoutSec.Value;

            Telemetry.GetCounter("transport_t4_reasserted").Increment();
        }

        /// <summary>
        /// RED-TEAM FIX (DATA-LOSS REVIEW, major — same root cause as T1/T3 in SteamRates.cs): T4 previously had
        /// no revert path — <see cref="Reassert"/> simply stopped being called once T4/Core.Enabled went off,
        /// leaving whichever configured value was last written in these process-wide static fields indefinitely.
        /// Called from TransportInit's KillSwitch flush callback and T4's own SettingChanged-to-off handler.
        /// <c>ZRpc.SetLongTimeout</c> is vanilla's own public method (SERVER:84201-84210) — the exact call
        /// <c>ZNet.Start()</c> itself makes with <c>enable: false</c> as its literal first statement — so this is
        /// not a guessed "vanilla-equivalent" value, it is vanilla's own reset path, reused rather than
        /// reimplemented.
        /// </summary>
        public static void RevertToVanilla()
        {
            AccessTools.StaticFieldRefAccess<float>(typeof(ZRpc), "m_pingInterval") = VanillaPingIntervalSec;
            ZRpc.SetLongTimeout(false); // vanilla's own call; resets m_timeout to 30f, matching ZNet.Start()'s own literal

            Telemetry.GetCounter("transport_t4_reverted_to_vanilla").Increment();
            NoodlesLog.Info("Transport T4: reverted ZRpc.m_pingInterval/m_timeout to vanilla's own 1.0s/30s.");
        }
    }

    /// <summary>T4's init leg — same target method as Hook 1 (T1/T3), a separate postfix class so each lever gets its own independent PatchRegistry feature-name/force-off isolation (multiple postfixes on one vanilla method is explicitly supported).</summary>
    [HarmonyPatch(typeof(ZNet), "Start")]
    internal static class Patch_ZNet_Start_RpcTimeoutInit
    {
        static void Postfix()
        {
            try { RpcTimeoutController.Reassert(); }
            catch (Exception e) { NoodlesLog.PatchException(nameof(Patch_ZNet_Start_RpcTimeoutInit), e); }
        }
    }

    [HarmonyPatch(typeof(ZPlayFabSocket), "OnNetworkJoined")]
    internal static class Patch_ZPlayFabSocket_OnNetworkJoined_ReassertTimeout
    {
        static void Postfix()
        {
            try { RpcTimeoutController.Reassert(); }
            catch (Exception e) { NoodlesLog.PatchException(nameof(Patch_ZPlayFabSocket_OnNetworkJoined_ReassertTimeout), e); }
        }
    }

    [HarmonyPatch(typeof(ZPlayFabSocket), "Accept")]
    internal static class Patch_ZPlayFabSocket_Accept_ReassertTimeout
    {
        static void Postfix()
        {
            try { RpcTimeoutController.Reassert(); }
            catch (Exception e) { NoodlesLog.PatchException(nameof(Patch_ZPlayFabSocket_Accept_ReassertTimeout), e); }
        }
    }
}
