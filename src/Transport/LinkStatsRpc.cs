using System;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using Steamworks;
using UnityEngine;

namespace NoodlesMcDoodles.Transport
{
    /// <summary>
    /// 1.0.3: the server's link numbers for each modded client, for that client's F6 panel. A client's download is
    /// the server's send rate to it, which only the server sets (T9, per player), so the client cannot know it on
    /// its own. Server: every 2 s, one small package per negotiated peer that advertised
    /// <see cref="Capabilities.LinkStats"/> (never a vanilla peer, never an older client). Client: keeps the latest
    /// values in <see cref="ServerLinkStats"/>. Reporting only: nothing here changes a rate, a queue or ownership.
    /// </summary>
    internal static class LinkStatsRpc
    {
        private const string RpcName = "NoodlesMcDoodles.v1.LinkStats";
        private const int WireVersion = 1; // a later server may append fields; readers take what they know
        private const float SendIntervalSeconds = 2f;
        private static float s_lastSend;

        [NoodlesModuleInit]
        internal static void Init()
        {
            Capabilities.Advertise(Capabilities.LinkStats);
            PatchRegistry.Register("Negotiation", typeof(Patch_ZNet_OnNewConnection_RegisterLinkStats), PatchSide.ClientOnly);
            PatchRegistry.Register("Negotiation", typeof(Patch_ZNet_Update_SendLinkStats), PatchSide.Both);
        }

        /// <summary>Client: handler registered the moment the connection opens (see Negotiation's early Hello for why).</summary>
        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        internal static class Patch_ZNet_OnNewConnection_RegisterLinkStats
        {
            static void Postfix(ZNet __instance, ZNetPeer peer)
            {
                if (!Features.Negotiation.Active) return;
                try
                {
                    if (__instance == null || __instance.IsServer() || peer?.m_rpc == null) return; // client side only
                    ServerLinkStats.Clear();
                    peer.m_rpc.Register<ZPackage>(RpcName, RPC_LinkStats);
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_ZNet_OnNewConnection_RegisterLinkStats), e);
                }
            }
        }

        private static void RPC_LinkStats(ZRpc rpc, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() < 1) return;
                ServerLinkStats.RateNowBps = pkg.ReadInt();
                ServerLinkStats.RateCeilingBps = pkg.ReadInt();
                ServerLinkStats.SentBps = pkg.ReadInt();
                ServerLinkStats.QueueBytes = pkg.ReadInt();
                ServerLinkStats.QueueAllowanceBytes = pkg.ReadInt();
                ServerLinkStats.Stalled = pkg.ReadBool();
                ServerLinkStats.PingMs = pkg.ReadInt();
                ServerLinkStats.ReceivedAtRealtime = Time.realtimeSinceStartup;
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(RPC_LinkStats), e);
            }
        }

        /// <summary>Server (dedicated or listen host): every 2 s, to each negotiated peer that asked for it.</summary>
        [HarmonyPatch(typeof(ZNet), "Update")]
        internal static class Patch_ZNet_Update_SendLinkStats
        {
            static void Postfix(ZNet __instance)
            {
                if (!Features.Negotiation.Active) return;
                try
                {
                    if (__instance == null || !__instance.IsServer()) return;
                    float now = Time.realtimeSinceStartup;
                    if (now - s_lastSend < SendIntervalSeconds) return;
                    s_lastSend = now;

                    bool dedicated = __instance.IsDedicated();
                    foreach (ZNetPeer peer in __instance.GetConnectedPeers())
                    {
                        if (peer == null || !peer.IsReady() || peer.m_rpc == null) continue;
                        if (!PeerState.TryGet(peer, out PeerState state) || !state.HasCapability(Capabilities.LinkStats)) continue;
                        Send(peer, dedicated);
                    }
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_ZNet_Update_SendLinkStats), e);
                }
            }
        }

        private static void Send(ZNetPeer peer, bool dedicated)
        {
            int ping = -1;
            if (SteamSockets.TryGet(peer.m_socket, out ZSteamSocket sock) &&
                AdaptiveRate.TryGetStatus(dedicated, sock, out SteamNetConnectionRealTimeStatus_t status))
            {
                ping = status.m_nPing;
            }

            // The same two per-peer values QueueWindow.Extend sums for this connection's in-flight window.
            long q3 = Telemetry.GetPerPeerCounter("transport_q3_budget_bytes").Get(peer);
            long q13 = Telemetry.GetPerPeerCounter("transport_q13_allowance_bytes").Get(peer);
            int allowance = q3 > 0 ? (int)Math.Min(q3 + q13, SharedBudget.HardCeilingBytes) : 0;

            var pkg = new ZPackage();
            pkg.Write(WireVersion);
            pkg.Write(AdaptiveRate.CurrentRateBps(peer));
            pkg.Write(AdaptiveRate.CeilingBps());
            pkg.Write(AdaptiveRate.LastSentBps(peer));
            pkg.Write(peer.m_socket != null ? peer.m_socket.GetSendQueueSize() : 0);
            pkg.Write(allowance);
            pkg.Write(AdaptiveRate.IsStalled(peer));
            pkg.Write(ping);
            peer.m_rpc.Invoke(RpcName, pkg);
        }
    }
}
