using System;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using NoodlesMcDoodles.Core;
using Steamworks;

namespace NoodlesMcDoodles.Transport
{
    /// <summary>
    /// Q13: a round-trip allowance on top of Q3's per-call budget, so distance stops cutting a player's speed.
    /// <para>
    /// Vanilla's <c>ZSteamSocket.GetSendQueueSize</c> counts bytes already sent but not yet acknowledged
    /// (<c>m_cbSentUnackedReliable</c>), so the 10 KB SendZDOs gate is really a 10 KB window per round trip: ~165 KB/s
    /// at LAN ping (measured, 18:45 session), a fraction of that for a player 110-160 ms away (queue-unlock sim: 84% of
    /// a 256 KiB/s rate at 1 ms, 20% at 160 ms). Q3's budget (PeerBudget.cs) sizes the WAITING part to one send tick at
    /// the rate T9 has proven; this adds, for the same call, up to one round trip's worth of the bytes already on the
    /// wire: <c>min(unacked, rate x baseline RTT x Q13_RttSlack, cap)</c>. Waiting data, which every small RPC queued
    /// after it has to wait behind, stays at Q3's size; only bytes already in flight are discounted, so a hit or an
    /// item pickup waits no longer than before while a far player gets the same throughput as a near one.
    /// </para>
    /// <para>
    /// Given only on a Steam connection T9 is managing with a known 2-minute minimum ping (never PlayFab, whose queue
    /// size is a different heuristic), withheld while the current ping is inflated, while the server's recent mean
    /// frame time is above <c>Q13_FrameLoadMs</c>, on a flush, and whenever LinkGuard has the link stalled (Q3 itself
    /// returns vanilla's 10240 before this is reached). On a link that dies the allowance cannot grow: once acks stop,
    /// unacked passes the allowance and the gate closes at Q3's size plus it. What that costs is catch-up time after a
    /// blip (Steam re-sends up to one allowance before new data), not lost state: whatever was not delivered when a
    /// link dies is the same set of changes with or without it (queue-unlock sim + model-redteam, 2026-09-28).
    /// </para>
    /// <para>
    /// USER DECISION (2026-09-28 ~20:30, "unlock the queue, even a small bump can make all the difference"; the far
    /// player "would need more to stabilize and get the same throughput"): applies to the server's sends AND to a
    /// modded client's own upload (<c>Transport.Q13.Client</c>), replacing the unconditional client pin at 10240 that
    /// followed the 17:02 outage. The client side keeps a lower total ceiling (<c>Q13_ClientCeilingBytes</c>).
    /// </para>
    /// </summary>
    internal static class QueueWindow
    {
        public static FeatureFlag Q13;       // the allowance on an authoritative process's sends; master switch
        public static FeatureFlag Q13Client; // a plain client's own upload: Q3's waiting budget plus the allowance
        public static ConfigEntry<float> RttSlack;
        public static ConfigEntry<int> ServerAllowanceCapBytes;
        public static ConfigEntry<int> ClientCeilingBytes;
        public static ConfigEntry<float> FrameLoadMs;

        private enum Withheld { None, FrameLoad, PingInflated }

        private sealed class PeerWindow
        {
            public Withheld Last = Withheld.None;
        }

        private static readonly ConditionalWeakTable<ZNetPeer, PeerWindow> s_window = new ConditionalWeakTable<ZNetPeer, PeerWindow>();

        [NoodlesModuleInit]
        internal static void Init()
        {
            ConfigFile cfg = NoodlesConfig.File;
            RttSlack = cfg.Bind("Transport", "Q13_RttSlack", 1.0f,
                new ConfigDescription("Q13: the round-trip allowance is rate x the player's 2-minute minimum ping x this. " +
                    "1.0 covers the round trip itself; above that only adds catch-up time after a blip.",
                    new AcceptableValueRange<float>(0.5f, 2.0f)));
            ServerAllowanceCapBytes = cfg.Bind("Transport", "Q13_ServerAllowanceCapBytes", 49152,
                new ConfigDescription("Q13: the most bytes already on the wire the server may discount per player. " +
                    "48 KiB covers T1's 256 KiB/s at ~190 ms of ping. 0 = off on the server.",
                    new AcceptableValueRange<int>(0, 131072)));
            ClientCeilingBytes = cfg.Bind("Transport", "Q13_ClientCeilingBytes", 49152,
                new ConfigDescription("Q13: a modded client's own upload budget in total (waiting data + round-trip " +
                    "allowance). The waiting part never exceeds half of it. 48 KiB lets a player ~110 ms away upload at " +
                    "T1's 256 KiB/s. 10240 = vanilla.",
                    new AcceptableValueRange<int>(SharedBudget.VanillaInnerBudgetBytes, 65536)));
            FrameLoadMs = cfg.Bind("Transport", "Q13_FrameLoadMs", 50f,
                new ConfigDescription("Q13: while the server's (or listen host's) recent mean frame time is above this " +
                    "(ms), the allowance is withheld on every connection, so a busy server builds smaller packets.",
                    new AcceptableValueRange<float>(20f, 250f)));

            ConfigEntry<bool> active = cfg.Bind("Features", "Transport.Q13.Active", true,
                "Q13: round-trip allowance. On: a player far away gets the same send speed as a near one, because bytes " +
                "already on the wire (waiting only for the other end's acknowledgement) stop counting against the send " +
                "queue, up to one round trip's worth. Off: the queue counts them, as vanilla does.");
            Q13 = Features.Register("Transport.Q13", active);
            Q13.OnForcedOff = ReleaseAll;

            ConfigEntry<bool> client = cfg.Bind("Features", "Transport.Q13.Client.Active", true,
                "Q13 on a player's own upload (needs Transport.Q10 on and T9 managing the connection): the upload " +
                "queue follows the connection's proven speed plus the round-trip allowance, up to " +
                "Q13_ClientCeilingBytes, instead of vanilla's fixed 10 KB. Back to 10 KB while the link is stalled.");
            Q13Client = Features.Register("Transport.Q13.Client", client);
            Q13Client.OnForcedOff = ReleaseAll;
        }

        /// <summary>True when a plain client's own upload budget may exceed vanilla's (PeerBudget.For's client branch).</summary>
        internal static bool ClientBranchActive => Q13 != null && Q13.Active && Q13Client != null && Q13Client.Active;

        /// <summary>A client's waiting-data ceiling: half its total ceiling, never below vanilla's 10240.</summary>
        internal static int ClientWaitCeilingBytes =>
            Math.Max(SharedBudget.VanillaInnerBudgetBytes, (ClientCeilingBytes?.Value ?? SharedBudget.VanillaInnerBudgetBytes) / 2);

        /// <summary>
        /// Q3's budget for this call plus the round-trip allowance. Called from PeerBudget.For only once the
        /// connection has a T9 rate (so: Steam, T9 on, not stalled). Never throws: an exception stands this side's
        /// flag down and returns <paramref name="waitBudget"/> unchanged.
        /// </summary>
        internal static int Extend(ZNetPeer peer, int waitBudget, int rateBps, bool authoritative, bool flush)
        {
            if (flush || rateBps <= 0 || Q13 == null || !Q13.Active) return waitBudget;
            if (!authoritative && !ClientBranchActive) return waitBudget;
            try
            {
                int allowance = Allowance(peer, waitBudget, rateBps, authoritative, out Withheld withheld, out int pingNow, out int baseRtt);
                PeerWindow w = s_window.GetValue(peer, _ => new PeerWindow());
                if (withheld != w.Last) // strings are built only on a change of state, never per call
                {
                    if (withheld != Withheld.None)
                    {
                        Telemetry.GetCounter("transport.q13Withheld").Increment();
                        string why = withheld == Withheld.FrameLoad
                            ? $"server frame load {FrameTiming.RecentMeanMs:0} ms"
                            : $"ping {pingNow} ms vs {baseRtt} ms minimum";
                        NoodlesLog.DetailRateLimited($"q13_{peer.m_uid}",
                            $"Q13: round-trip allowance for peer uid={peer.m_uid} withheld ({why}).", 5, 60f);
                    }
                    w.Last = withheld;
                }
                Telemetry.GetPerPeerCounter("transport_q13_allowance_bytes", resetEachPeriod: true).Max(peer, allowance);
                // Never above Q3's own hard clamp, whatever an admin sets the two caps to (pretest q13-review).
                return Math.Min(waitBudget + allowance, SharedBudget.HardCeilingBytes);
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(QueueWindow), e);
                (authoritative ? Q13 : Q13Client)?.ForceOff($"exception in the round-trip allowance: {e.GetType().Name}");
                return waitBudget;
            }
        }

        private static int Allowance(ZNetPeer peer, int waitBudget, int rateBps, bool authoritative, out Withheld withheld,
            out int pingNow, out int baseRtt)
        {
            withheld = Withheld.None;
            pingNow = -1;
            baseRtt = -1;
            if (!SteamSockets.TryGet(peer.m_socket, out ZSteamSocket sock)) return 0; // PlayFab's queue size is a different heuristic
            int cap = authoritative
                ? ServerAllowanceCapBytes.Value
                : Math.Max(0, ClientCeilingBytes.Value - waitBudget);
            if (cap <= 0) return 0;

            baseRtt = AdaptiveRate.BaselinePingMs(peer);
            if (baseRtt <= 0) return 0; // no ping sample yet (or LAN at 0 ms: nothing in flight to discount)

            if (authoritative && FrameTiming.RecentMeanMs > FrameLoadMs.Value)
            {
                withheld = Withheld.FrameLoad;
                return 0;
            }

            ZNet znet = ZNet.instance;
            if (znet == null || !AdaptiveRate.TryGetStatus(znet.IsDedicated(), sock, out SteamNetConnectionRealTimeStatus_t status)) return 0;
            if (status.m_eState != ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected) return 0;

            // Same inflation margin T9 backs its rate off at (RateRule.Decide): a filling buffer somewhere on the path
            // shows up here first, so stop discounting in-flight bytes the moment it does.
            pingNow = status.m_nPing;
            if (pingNow > baseRtt + Math.Max(AdaptiveRate.PingInflationMs.Value, baseRtt / 2))
            {
                withheld = Withheld.PingInflated;
                return 0;
            }

            int unacked = status.m_cbSentUnackedReliable;
            if (unacked <= 0) return 0;
            long target = (long)(rateBps * (baseRtt / 1000.0) * RttSlack.Value);
            return (int)Math.Min(unacked, Math.Min(target, cap));
        }

        /// <summary>Per-connection state is only the last withheld reason; clearing it keeps a re-enable's log honest.</summary>
        internal static void ReleaseAll()
        {
            ZNet znet = ZNet.instance;
            if (znet == null) return;
            foreach (ZNetPeer peer in znet.GetConnectedPeers())
                if (peer != null) s_window.Remove(peer);
        }
    }
}
