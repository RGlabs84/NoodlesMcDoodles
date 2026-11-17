using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using NoodlesMcDoodles.Core.ConfigFiles;
using Steamworks;
using UnityEngine;

namespace NoodlesMcDoodles.Transport
{
    /// <summary>
    /// T9 — adaptive per-connection send rate (research/10-LEVERS.md T9; DESIGN.md Lead decision 5).
    ///
    /// In this GameNetworkingSockets build the Steam send rate is a FIXED rate, not a ceiling: SendRateMin is the
    /// rate and there is no bandwidth estimation (libs-Tools/VALHEIM-1.0.15-STEAM-SEND-RATE-FACTS.md §3, measured:
    /// raising only SendRateMax changed nothing). So T1 alone (every connection pinned at 1 MiB/s) is a clear win
    /// for players with fast downloads but pushes data faster than a slow line can take, which that player feels
    /// as lag spikes. This controller gives each connection its own rate instead:
    /// - it starts at the floor (vanilla's 153600 B/s), so no player is ever below what vanilla gives them;
    /// - it raises the rate only on evidence, checked once a second: data is waiting for the pacer, Steam reports
    ///   clean delivery to that player, and their ping is steady;
    /// - it backs off on packet loss or ping growth (data queueing somewhere on the path), then holds for a while.
    /// Ceilings: T1's value normally, T2's for the first T2_MaxBurstSeconds after a join, and a server-wide total on
    /// what is actually being sent, a safety net so the whole box (archserver also runs other servers) is never
    /// oversubscribed. Q3's per-call send budget follows the rate set here (PeerBudget.cs).
    ///
    /// Only the server needs the mod: the signals come from Steam's own per-connection stats, which exist for
    /// vanilla clients too. A client that opts in with Q10 runs the same loop on its own upload to the server.
    /// </summary>
    internal static class AdaptiveRate
    {
        public static ConfigEntry<int> FloorBps;
        /// <summary>Main file [2 - Network] ServerUploadLimitMiB (MiB/s; v1's T9_ServerTotalBps in bytes/s).</summary>
        public static ConfigEntry<int> ServerUploadLimitMiB;

        /// <summary>T9's server-wide total in bytes/s (= <see cref="ServerUploadLimitMiB"/> x 1048576).</summary>
        public static long ServerTotalBps => (long)ServerUploadLimitMiB.Value * ConfigSchema.BytesPerMiB;
        public static ConfigEntry<int> PingInflationMs;
        public static ConfigEntry<float> LossThreshold;
        public static FeatureFlag T9;

        private const float TickSeconds = 1f;

        // Keyed by ZNetPeer object (a reconnect is a new object, so nothing stale carries over), same as T2.
        private static readonly ConditionalWeakTable<ZNetPeer, RateState> s_state = new ConditionalWeakTable<ZNetPeer, RateState>();
        private static readonly AccessTools.FieldRef<ZSteamSocket, HSteamNetConnection> s_con =
            AccessTools.FieldRefAccess<ZSteamSocket, HSteamNetConnection>("m_con");
        private static bool s_anyManaged;
        private static float s_lastTick;

        [NoodlesModuleInit]
        internal static void Init()
        {
            ConfigFile cfg = NoodlesConfig.File;
            FloorBps = cfg.Bind("Transport", "T9_FloorBps", SteamRateController.VanillaGlobalRateBps,
                new ConfigDescription("T9: the rate every connection starts at and never drops below. Vanilla's fixed " +
                    "rate is 153600, so the default means no player ever gets less than vanilla gives them.",
                    new AcceptableValueRange<int>(SteamRateController.VanillaGlobalRateBps, 1048576)));
            // USER DECISION (2026-09-28 ~20:30): 10 MiB/s = ~40 players x T1's 256 KiB/s default (was 8 MiB/s).
            // 1.1.0: main file, MiB/s (was [Transport] T9_ServerTotalBps = 10485760 bytes/s, range 1048576..104857600).
            ServerUploadLimitMiB = NoodlesConfig.ServerUploadLimitMiB;
            PingInflationMs = cfg.Bind("Transport", "T9_PingInflationMs", 30,
                new ConfigDescription("T9: how far a player's ping may rise above their recent minimum (ms) before the " +
                    "rate backs off. The margin used is the larger of this and half the minimum ping.",
                    new AcceptableValueRange<int>(10, 200)));
            LossThreshold = cfg.Bind("Transport", "T9_LossThreshold", 0.98f,
                new ConfigDescription("T9: Steam's delivery ratio to the player below which the rate backs off " +
                    "(1.0 = every packet arrives). Below 0.95 it backs off immediately.",
                    new AcceptableValueRange<float>(0.90f, 0.999f)));

            // 1.1.0: main file (was [Features] Transport.T9.Active).
            ConfigEntry<bool> active = NoodlesConfig.AdaptiveRate;
            T9 = Features.Register("Transport.T9", active);

            PatchRegistry.Register("Transport.T9", typeof(Patch_ZNet_Update_AdaptiveRateTick), PatchSide.Both);
            KillSwitch.RegisterFlushCallback("Transport.T9", ReleaseAll);
            active.SettingChanged += (_, __) => MainThreadDispatcher.Enqueue(() =>
            {
                if (!T9.Active) ReleaseAll();
                SteamRateController.Resync(); // T1's global value depends on whether T9 manages connections
            });

            // WP5 per-feature quarantine wiring (this lever previously lacked it): T9 holds real native
            // state (per-connection Steam SendRateMin/Max overrides) that Tick()'s own top-of-body `!T9.Active`
            // check already releases on its very next ~1s tick regardless (the Postfix below always runs,
            // Active or not — see Tick()'s own remarks), so this was never a leak, only a delayed one. Wiring
            // OnForcedOff here removes even that ~1s window and makes a FeatureStandDown exception-burst
            // trip release its native overrides in the SAME synchronous call as every other quarantined
            // lever (T1-T4 above), rather than the one lever in this file relying on a different mechanism.
            T9.OnForcedOff = ReleaseAll;
        }

        /// <summary>Once a second from ZNet.Update. Always runs, so turning T9 off releases every override.</summary>
        [HarmonyPatch(typeof(ZNet), "Update")]
        internal static class Patch_ZNet_Update_AdaptiveRateTick
        {
            static void Postfix(ZNet __instance)
            {
                try
                {
                    float now = Time.realtimeSinceStartup;
                    if (now - s_lastTick < TickSeconds) return;
                    s_lastTick = now;
                    Tick(__instance, now);
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_ZNet_Update_AdaptiveRateTick), e);
                }
            }
        }

        private static bool EligibleHere(ZNet znet) => znet.IsServer() || TransportLeverConfig.Q10Active;

        private static int Ceiling() =>
            TransportLeverConfig.T1.Active ? TransportLeverConfig.T1GlobalRateBps : SteamRateController.VanillaGlobalRateBps;

        private static int JoinCeiling(int ceiling) =>
            TransportLeverConfig.T2.Active
                ? Math.Max(ceiling, Math.Min(TransportLeverConfig.T2_JoinBurstBps.Value, ceiling * 8))
                : ceiling;

        private static void Tick(ZNet znet, float now)
        {
            int floor = FloorBps.Value;
            int ceiling = Ceiling();
            if (T9 == null || !T9.Active || !EligibleHere(znet) || ceiling <= floor)
            {
                if (s_anyManaged) ReleaseAll(); // off, not ours to manage here, or nothing to adapt
                return;
            }

            bool dedicated = znet.IsDedicated();
            bool applyTotal = znet.IsServer();
            int joinCeiling = JoinCeiling(ceiling);
            float joinSeconds = TransportLeverConfig.T2_MaxBurstSeconds.Value;

            // The total cap counts what connections actually sent last second, not the rates they were granted:
            // T9 never lowers an idle connection's rate, so summing grants would let players who loaded earlier and
            // went quiet block every new player from being raised.
            long granted = 0, sent = 0;
            foreach (ZNetPeer peer in znet.GetConnectedPeers())
            {
                if (peer == null || !s_state.TryGetValue(peer, out RateState s)) continue;
                if (s.RateBps > 0) granted += s.RateBps;
                sent += (long)s.LastOutBps;
            }

            int managed = 0;
            foreach (ZNetPeer peer in znet.GetConnectedPeers())
            {
                if (peer == null || !peer.IsReady()) continue;
                // 1.0.3: ServerSync can leave its BufferingSocket wrapper(s) on top of the Steam socket (SteamSockets.cs).
                if (!SteamSockets.TryGet(peer.m_socket, out ZSteamSocket sock, out int wrapLayers))
                {
                    NoodlesLog.DetailRateLimited($"t9_nosteam_{peer.m_uid}", $"T9: {PeerLabel(peer)} is on a " +
                        $"{peer.m_socket?.GetType().Name ?? "missing"} socket, not Steam: left at vanilla rates.", 1, 300f);
                    continue; // PlayFab has no Steam config
                }
                if (wrapLayers > 0)
                {
                    NoodlesLog.DetailRateLimited($"t9_wrapped_{peer.m_uid}", $"T9: {PeerLabel(peer)}'s socket is wrapped " +
                        $"by {wrapLayers} ServerSync layer(s); managing the Steam connection underneath.", 1, 3600f);
                }
                RateState st = s_state.GetValue(peer, _ => new RateState { FirstSeen = now });
                if (!TryGetStatus(dedicated, sock, out SteamNetConnectionRealTimeStatus_t status))
                {
                    NoodlesLog.DetailRateLimited($"t9_nostatus_{peer.m_uid}", $"T9: could not read Steam's status for " +
                        $"{PeerLabel(peer)}; leaving its rate as it is this tick.", 1, 300f);
                    continue;
                }
                if (status.m_eState != ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected) continue;

                // RATE GUARD (docs/impl/INTEGRATION-REVIEW.md "TheWall outage 17:02 and the client mod"):
                // never let a stalled connection get raised just because a backlog is piling up behind it —
                // pin it at the floor instead, and stay there until LinkGuard has seen it healthy for a few
                // seconds straight. Pure decision, proven offline against the real incident's probe CSV
                // (scratchpad linkguard-test, never shipped) — see LinkGuard.cs's own remarks.
                long queueBytes = sock.GetSendQueueSize();
                LinkGuardDecision guard = LinkGuard.Evaluate(ref st.Guard, TickSeconds, status.m_flConnectionQualityLocal,
                    status.m_flInBytesPerSec, queueBytes, Math.Min(floor, SteamRateController.VanillaGlobalRateBps));
                string who = znet.IsServer() ? PeerLabel(peer) : "the server";
                if (guard.JustStalled)
                {
                    NoodlesLog.WarningRateLimited(NoodlesLogLevel.Normal, $"link_stall_{peer.m_uid}",
                        $"T9: link to {who} stalled: upload held at vanilla rate, queue capped at 10 KB.", 1, 60f);
                    Telemetry.GetCounter("transport.linkStalls").Increment();
                }
                else if (guard.JustRecovered)
                {
                    NoodlesLog.Info($"T9: link to {who} recovered: resuming normal upload/queue behaviour.");
                }
                if (guard.Stalled) Telemetry.GetCounter("transport.stallSecondsTotal").Add((long)TickSeconds);
                Telemetry.GetPerPeerCounter("transport_link_stalled").Set(peer, guard.Stalled ? 1 : 0);

                int rate = st.RateBps > 0 ? st.RateBps : floor;
                bool joining = now - st.FirstSeen < joinSeconds;
                int target;
                if (guard.Stalled)
                {
                    // Never raise while stalled (also covers T2's join ceiling: "joining" is simply not
                    // consulted on this branch), and stay cautious for a while after recovery too — the extra
                    // HoldAfterDecreaseSeconds this buys once st.LastDecrease is reasserted every stalled tick
                    // means a freshly-recovered connection still has to prove itself again, from the floor,
                    // rather than jump straight back to wherever it was before the stall.
                    target = guard.RateCeilingBps;
                    st.HealthyStreak = 0;
                    st.UnhealthyStreak = 0;
                    st.LastDecrease = now;
                }
                else
                {
                    target = RateRule.Decide(st, status.m_nPing, status.m_flConnectionQualityRemote,
                        status.m_usecQueueTime.m_SteamNetworkingMicroseconds, status.m_cbPendingReliable + status.m_cbPendingUnreliable,
                        status.m_flOutBytesPerSec, rate, now, floor, joining ? joinCeiling : ceiling,
                        applyTotal ? ServerTotalBps - (sent - (long)st.LastOutBps) : long.MaxValue,
                        joining, PingInflationMs.Value, LossThreshold.Value);
                }
                st.LastOutBps = status.m_flOutBytesPerSec;

                if (st.RateBps == 0 || target != rate)
                {
                    if (!WriteConnectionRate(dedicated, sock, target, st.RateBps > 0 ? st.RateBps : SteamRateController.VanillaGlobalRateBps))
                        continue; // logged; try again next tick
                    granted += target - (st.RateBps > 0 ? st.RateBps : 0);
                    if (st.RateBps > 0)
                    {
                        bool raised = target > rate;
                        Telemetry.GetPerPeerCounter(raised ? "transport_t9_raised" : "transport_t9_lowered").Increment(peer);

                        // WP5 Tier-2 detail logging ("rate changes... rate-limited"): every raise/lower was
                        // previously telemetry-only (a counter increment with no text anywhere). Coarse but
                        // honest reason, derived from context already on hand here rather than threading a
                        // reason string out of RateRule.Decide (which tools/t9-sim exercises directly offline
                        // and must stay free of any change that isn't purely additive to its own return value).
                        string reason = guard.Stalled ? "stalled: pinned to floor"
                            : raised ? "healthy delivery + demand" : "loss/ping-inflation backoff";
                        NoodlesLog.DetailRateLimited($"t9_rate_{peer.m_uid}",
                            $"T9: {who} rate {rate}->{target} bytes/s ({reason}).", 5, 60f);
                    }
                    st.RateBps = target;
                    s_anyManaged = true;
                }
                managed++;
                Telemetry.GetPerPeerCounter("transport_t9_rate_bps").Set(peer, st.RateBps);
                Telemetry.GetPerPeerCounter("transport_t9_ping_ms").Set(peer, status.m_nPing);
            }
            Telemetry.SetGauge("transport_t9_managed", managed);
            Telemetry.SetGauge("transport_t9_total_bps", granted);
            Telemetry.SetGauge("transport_t9_sent_bps", sent);
        }

        /// <summary>The rate T9 has set on this connection (bytes/s), or 0 if it is not managing it.</summary>
        /// <summary>1.0.3, for LinkStatsRpc: the per-connection ceiling T9 raises toward (T1, or vanilla's rate if T1 is off).</summary>
        internal static int CeilingBps() => Ceiling();

        /// <summary>1.0.3, for LinkStatsRpc: what Steam reported sending on this connection last tick, bytes/s (0 = not tracked).</summary>
        internal static int LastSentBps(ZNetPeer peer) =>
            peer != null && s_state.TryGetValue(peer, out RateState st) ? (int)st.LastOutBps : 0;

        internal static int CurrentRateBps(ZNetPeer peer)
        {
            if (peer == null || T9 == null || !T9.Active) return 0;
            return s_state.TryGetValue(peer, out RateState st) ? st.RateBps : 0;
        }

        /// <summary>
        /// Q13 (QueueWindow.cs): this connection's lowest ping over the last two minutes (ms, Steam's round-trip
        /// figure), or -1 when T9 is not managing it or has no sample yet. Same clock as <see cref="Tick"/>'s samples.
        /// </summary>
        internal static int BaselinePingMs(ZNetPeer peer)
        {
            if (peer == null || T9 == null || !T9.Active) return -1;
            return s_state.TryGetValue(peer, out RateState st) ? RateRule.Baseline(st, Time.realtimeSinceStartup) : -1;
        }

        /// <summary>
        /// QUEUE GUARD's own read of the same per-connection stall flag <see cref="Tick"/> maintains (PeerBudget.cs):
        /// true while this connection is stalled, so Q3's budget is pinned to vanilla regardless of T9's proven
        /// rate. False (never stalled, or T9 not managing/active) is always the safe default.
        /// </summary>
        internal static bool IsStalled(ZNetPeer peer)
        {
            if (peer == null || T9 == null || !T9.Active) return false;
            return s_state.TryGetValue(peer, out RateState st) && st.Guard.Stalled;
        }

        /// <summary>Same label style as Core/DeploymentLog.cs's PeerLabel — duplicated here (three lines) rather
        /// than adding a cross-file reference for one log string.</summary>
        private static string PeerLabel(ZNetPeer peer) =>
            string.IsNullOrEmpty(peer.m_playerName) ? $"peer uid={peer.m_uid}" : $"{peer.m_playerName} (peer uid={peer.m_uid})";

        /// <summary>Internal (not private): reused by SteamRates.cs's JoinBurstController for its own one-shot
        /// "is this connection already stalled at join" check on the T9-off path.</summary>
        internal static bool TryGetStatus(bool dedicated, ZSteamSocket sock, out SteamNetConnectionRealTimeStatus_t status)
        {
            status = default;
            var lanes = default(SteamNetConnectionRealTimeLaneStatus_t);
            HSteamNetConnection con = s_con(sock);
            EResult r = dedicated
                ? SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(con, ref status, 0, ref lanes)
                : SteamNetworkingSockets.GetConnectionRealTimeStatus(con, ref status, 0, ref lanes);
            return r == EResult.k_EResultOK;
        }

        /// <summary>
        /// Sets this connection's fixed rate (SendRateMin == SendRateMax, connection scope 4). Order per the facts
        /// file: raise the maximum before the minimum, lower the minimum before the maximum.
        /// </summary>
        private static bool WriteConnectionRate(bool dedicated, ZSteamSocket sock, int newRate, int oldRate)
        {
            try
            {
                IntPtr con = (IntPtr)(long)s_con(sock).m_HSteamNetConnection;
                GCHandle h = GCHandle.Alloc(newRate, GCHandleType.Pinned);
                try
                {
                    var first = newRate >= oldRate ? ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax : ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin;
                    var second = newRate >= oldRate ? ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin : ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax;
                    bool ok = SteamRateController.SetConfig(dedicated, first, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, con,
                        ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, h.AddrOfPinnedObject());
                    ok &= SteamRateController.SetConfig(dedicated, second, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, con,
                        ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, h.AddrOfPinnedObject());
                    if (!ok) NoodlesLog.Warning($"T9: Steam refused rate {newRate} for a connection; leaving it as it was.");
                    return ok;
                }
                finally { h.Free(); }
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(AdaptiveRate) + ".WriteConnectionRate", e);
                return false;
            }
        }

        /// <summary>
        /// Clears every connection override T9 wrote, so each connection falls back to the global rate. A null value
        /// pointer clears a setting back to the parent scope (facts file §3). Overrides are never below the global
        /// rate (the floor is at least vanilla), so clearing lowers: minimum first, then maximum.
        /// Used when T9 turns off, by the kill switch, and when this process stops being eligible.
        /// </summary>
        internal static void ReleaseAll()
        {
            ZNet znet = ZNet.instance;
            s_anyManaged = false;
            if (znet == null) return;
            bool dedicated = znet.IsDedicated();
            foreach (ZNetPeer peer in znet.GetConnectedPeers())
            {
                if (peer == null || !SteamSockets.TryGet(peer.m_socket, out ZSteamSocket sock)) continue;
                if (!s_state.TryGetValue(peer, out RateState st) || st.RateBps == 0) continue;
                try
                {
                    IntPtr con = (IntPtr)(long)s_con(sock).m_HSteamNetConnection;
                    bool ok = SteamRateController.SetConfig(dedicated, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin,
                        ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, con, ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, IntPtr.Zero);
                    ok &= SteamRateController.SetConfig(dedicated, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax,
                        ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, con, ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, IntPtr.Zero);
                    if (!ok)
                    {
                        // Could not clear: pin the vanilla rate explicitly instead, so the connection is never left raised.
                        ok = WriteConnectionRate(dedicated, sock, SteamRateController.VanillaGlobalRateBps, st.RateBps);
                    }
                    if (ok)
                    {
                        st.RateBps = 0;
                        Telemetry.GetPerPeerCounter("transport_t9_rate_bps").Set(peer, 0);
                    }
                    else s_anyManaged = true; // retry on the next tick
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(AdaptiveRate) + ".ReleaseAll", e);
                    s_anyManaged = true;
                }
            }
        }
    }

    /// <summary>Per-connection state for T9. Plain data, no game types.</summary>
    internal sealed class RateState
    {
        public int RateBps;                 // rate currently written on this connection; 0 = no override
        public float LastOutBps;            // what Steam reported sending last tick (bytes/s), for the server total
        public float FirstSeen;
        public float LastDecrease = -1000f;
        public int HealthyStreak;
        public int UnhealthyStreak;
        public LinkGuardState Guard;
        public readonly int[] BucketMin = new int[RateRule.BaselineBuckets];
        public readonly long[] BucketId = new long[RateRule.BaselineBuckets];
        public RateState() { for (int i = 0; i < RateRule.BaselineBuckets; i++) BucketId[i] = -1; }
    }

    /// <summary>
    /// T9's control rule, kept free of Steam, Unity and BepInEx types so it can be exercised offline
    /// (tools/t9-sim). Inputs are one second's worth of Steam connection stats; output is the rate to set.
    /// </summary>
    internal static class RateRule
    {
        public const float StepUp = 1.25f;
        public const float JoinStepUp = 1.5f;
        public const float StepDown = 0.7f;
        public const int MinStepBps = 32 * 1024;
        public const float HoldAfterDecreaseSeconds = 5f;
        public const float GoodQuality = 0.99f;
        public const float SevereQuality = 0.95f;
        public const long DemandQueueMicros = 10_000; // 10 ms of data waiting for Steam's pacer
        public const int BaselineBuckets = 12;        // windowed minimum ping over 12 x 10 s = 2 minutes
        public const float BucketSeconds = 10f;

        /// <summary>Returns the rate this connection should have now.</summary>
        public static int Decide(RateState st, int ping, float qRemote, long queueMicros, int pending, float outBytesPerSec,
            int rate, float now, int floor, int cap, long totalHeadroom, bool joining, int pingInflationMs, float lossThreshold)
        {
            bool pingKnown = ping >= 0;
            if (pingKnown) RecordPing(st, now, ping);
            int baseline = Baseline(st, now);

            bool qKnown = qRemote >= 0f; // share of our packets the player received; < 0 = not known yet
            bool lossSevere = qKnown && qRemote < SevereQuality;
            bool lossBad = qKnown && qRemote < lossThreshold;
            bool inflated = pingKnown && baseline >= 0 && ping > baseline + Math.Max(pingInflationMs, baseline / 2);
            bool calm = pingKnown && baseline >= 0 && ping <= baseline + Math.Max(15, baseline / 4);

            // Back off: at once on severe loss, otherwise after two bad samples in a row (single samples are noisy).
            if (lossSevere || ((lossBad || inflated) && ++st.UnhealthyStreak >= 2))
            {
                st.UnhealthyStreak = 0;
                st.HealthyStreak = 0;
                st.LastDecrease = now;
                return Math.Max(floor, Math.Min(cap, (int)(rate * StepDown)));
            }
            if (!lossBad && !inflated) st.UnhealthyStreak = 0;

            // Raise only on evidence: data is waiting for the pacer, delivery is clean, ping is steady, and we have
            // not backed off recently. Unknown signals never count as good.
            bool demand = queueMicros > DemandQueueMicros || pending > rate / 50 || outBytesPerSec >= 0.85f * rate;
            bool healthy = qKnown && qRemote >= GoodQuality && calm;
            int target = rate;
            if (healthy && demand && now - st.LastDecrease >= HoldAfterDecreaseSeconds)
            {
                // Joining is when the most data flows: raise on every clean second then (loss and ping are still
                // checked every second, so an overshoot is caught at once); afterwards, two clean seconds per step.
                if (++st.HealthyStreak >= (joining ? 1 : 2))
                {
                    st.HealthyStreak = 0;
                    int up = Math.Max((int)(rate * (joining ? JoinStepUp : StepUp)), rate + MinStepBps);
                    target = (int)Math.Min(Math.Min(up, cap), Math.Max(rate, totalHeadroom));
                }
            }
            else st.HealthyStreak = 0;

            if (target > cap) target = cap; // the ceiling can drop under a connection (join phase over, config lowered)
            return Math.Max(floor, target);
        }

        public static void RecordPing(RateState st, float now, int ping)
        {
            long bucket = (long)(now / BucketSeconds);
            int i = (int)(bucket % BaselineBuckets);
            if (st.BucketId[i] != bucket) { st.BucketId[i] = bucket; st.BucketMin[i] = ping; }
            else if (ping < st.BucketMin[i]) st.BucketMin[i] = ping;
        }

        /// <summary>Lowest ping seen in the last two minutes, or -1 if none yet.</summary>
        public static int Baseline(RateState st, float now)
        {
            long bucket = (long)(now / BucketSeconds);
            int best = -1;
            for (int i = 0; i < BaselineBuckets; i++)
            {
                if (st.BucketId[i] < 0 || bucket - st.BucketId[i] >= BaselineBuckets) continue;
                if (best < 0 || st.BucketMin[i] < best) best = st.BucketMin[i];
            }
            return best;
        }
    }
}
