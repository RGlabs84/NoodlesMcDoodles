using System;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using Steamworks;

namespace NoodlesMcDoodles.Transport
{
    /// <summary>
    /// Hook 5 (T8) — server-side diagnostics stub fixes (docs/design/01 Hook 5). Two independent, additive
    /// pieces, both server-build only.
    /// </summary>

    /// <summary>
    /// <c>ZNet.GetNetStats</c> (SERVER:81567-81574) is a dead 5-out-param zeroed stub on the server build only —
    /// the CLIENT build (CLIENT:81759-81799) already has a real implementation that also correctly handles a
    /// listen-server host via its own <c>IsServer()</c> branch. This postfix gives the DEDICATED server the
    /// exact same algorithm the client build already runs for that branch: average quality/ping across every
    /// ready peer, sum the byte rates, via the polymorphic <c>ISocket.GetConnectionQuality</c> (works for
    /// <c>ZSteamSocket</c> AND <c>ZPlayFabSocket</c> — the latter inherits a rate-based, exception-free base
    /// implementation from <c>ZNetStats</c>, SERVER:82414-82464 — no <c>is ZSteamSocket</c> type-guard needed
    /// here, unlike <c>GetCurrentSendRate</c>, which PlayFab's own override throws
    /// <c>NotImplementedException</c> from and which this method never calls).
    /// </summary>
    [HarmonyPatch(typeof(ZNet), "GetNetStats")]
    internal static class Patch_ZNet_GetNetStats_ServerTelemetry
    {
        static void Postfix(ZNet __instance, out float localQuality, out float remoteQuality, out int ping, out float outByteSec, out float inByteSec)
        {
            localQuality = 0f; remoteQuality = 0f; ping = 0; outByteSec = 0f; inByteSec = 0f; // vanilla's own zeroed shape — the fallback for every early-return below
            try
            {
                if (!TransportLeverConfig.T8.Active || !__instance.IsDedicated()) return;

                int counted = 0;
                foreach (ZNetPeer peer in __instance.GetConnectedPeers())
                {
                    if (peer?.m_socket == null || !peer.IsReady()) continue;
                    counted++;
                    peer.m_socket.GetConnectionQuality(out float lq, out float rq, out int p, out float o, out float i);
                    localQuality += lq;
                    remoteQuality += rq;
                    ping += p;
                    outByteSec += o;
                    inByteSec += i;
                }
                if (counted > 0)
                {
                    localQuality /= counted;
                    remoteQuality /= counted;
                    ping /= counted;
                    // outByteSec/inByteSec stay SUMMED across peers (matches CLIENT:81759-81799's own algorithm exactly).
                }

                Telemetry.GetCounter("transport_t8_net_stats_computed").Increment();
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(Patch_ZNet_GetNetStats_ServerTelemetry), e);
                localQuality = 0f; remoteQuality = 0f; ping = 0; outByteSec = 0f; inByteSec = 0f; // fail back to vanilla's own shape, never a half-computed result
            }
        }
    }

    /// <summary>
    /// <c>ZSteamSocket.GetConnectionQuality</c> (server build, SERVER:86673-86696) calls the CLIENT Steam API
    /// class (<c>SteamNetworkingSockets.GetConnectionRealTimeStatus</c>) instead of
    /// <c>SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus</c> — inconsistent with every sibling
    /// method in the same class/build (<c>GetSendQueueSize</c> SERVER:86620, <c>GetCurrentSendRate</c>
    /// SERVER:86648, both confirmed this pass to use the GameServer class). Unreachable in vanilla
    /// (<c>ZNet.GetNetStats</c>'s own stub never calls it on the server, per the sibling patch above), so this
    /// fix cannot regress existing behavior — it only matters once that stub starts calling it for real.
    /// </summary>
    [HarmonyPatch(typeof(ZSteamSocket), "GetConnectionQuality")]
    internal static class Patch_ZSteamSocket_GetConnectionQuality_Fix
    {
        static bool Prefix(ZSteamSocket __instance, out float localQuality, out float remoteQuality, out int ping, out float outByteSec, out float inByteSec)
        {
            localQuality = 0f; remoteQuality = 0f; ping = 0; outByteSec = 0f; inByteSec = 0f;
            if (!TransportLeverConfig.T8.Active) return true; // flag off: let vanilla's (buggy, unreachable) body run untouched

            try
            {
                // m_con is a private field with no accessor — same AccessTools.FieldRefAccess pattern as T2's
                // ApplyRate (SteamRates.cs), for the same reason (bypass the Publicizer's compile-time-only
                // visibility rather than rely on direct field syntax against the real, still-private runtime DLL).
                HSteamNetConnection con = AccessTools.FieldRefAccess<ZSteamSocket, HSteamNetConnection>(__instance, "m_con");
                var status = default(SteamNetConnectionRealTimeStatus_t);
                var laneStatus = default(SteamNetConnectionRealTimeLaneStatus_t);

                if (SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(con, ref status, 0, ref laneStatus) == EResult.k_EResultOK)
                {
                    localQuality = status.m_flConnectionQualityLocal;
                    remoteQuality = status.m_flConnectionQualityRemote;
                    ping = status.m_nPing;
                    outByteSec = status.m_flOutBytesPerSec;
                    inByteSec = status.m_flInBytesPerSec;
                }
                // else: connection not in a state with real-time status yet — leave the zeros above, matching
                // vanilla's own "else" branch shape for this method.

                Telemetry.GetCounter("transport_t8_connection_quality_fixed").Increment();
                return false; // skip vanilla's wrong-Steam-API-class body entirely
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(Patch_ZSteamSocket_GetConnectionQuality_Fix), e);
                return true; // fail open: let vanilla's own (buggy but harmless/unreachable) body run instead
            }
        }
    }
}
