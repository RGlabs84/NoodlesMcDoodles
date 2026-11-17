using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using Steamworks;
using UnityEngine;

namespace NoodlesMcDoodles.Transport
{
    /// <summary>
    /// Hook 1 (T1, T3) — global Steam SendRateMin/SendRateMax + TimeoutConnected, applied once per process at
    /// <c>ZNet.Start()</c> (docs/design/01 Hook 1). Not a patch on <c>ZSteamSocket.RegisterGlobalCallbacks</c>:
    /// that method's own body is guarded by <c>if (m_statusChanged == null)</c> so vanilla's effect only
    /// happens once per process, but the method itself runs from all four <c>ZSteamSocket</c> constructors
    /// (once per CONNECTION), so a postfix there would leak a GCHandle per join. <c>ZNet.Start()</c> runs
    /// exactly once per session on both builds (SERVER:79301-79304 / CLIENT:79448-79451, both confirmed this
    /// pass to open with <c>ZRpc.SetLongTimeout(enable: false)</c> as their literal first statement) — the
    /// same "once per session" cadence T4's init leg (Timeouts.cs) also needs, and why that lever hooks the
    /// same method.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), "Start")]
    internal static class Patch_ZNet_Start_SteamRateAndTimeout
    {
        static void Postfix(ZNet __instance)
        {
            try
            {
                SteamRateController.ApplyGlobalConfig(__instance);
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(Patch_ZNet_Start_SteamRateAndTimeout), e);
            }
        }
    }

    internal static class SteamRateController
    {
        // Vanilla's own values. LEAD-CORRECTED: vanilla DOES set these, once per process, in
        // ZSteamSocket.RegisterGlobalCallbacks (SERVER:86261-86274, guarded by `m_statusChanged == null`, reached
        // from every ZSteamSocket constructor) via numeric casts — (ESteamNetworkingConfigValue)25 = 30000f,
        // 10 and 11 = 153600 — which is why a grep for the enum NAMES finds nothing. On the server the first
        // socket is created by ZNet.OpenServer inside ZNet.Start, so vanilla's write lands before our Start
        // postfix. Named here so every revert-to-vanilla write shares one source of truth.
        internal const int VanillaGlobalRateBps = 153600;
        internal const float VanillaTimeoutConnectedMs = 30000f;

        // True once ZNet.Start() has run this session — Resync() is a no-op before this (nothing to write to
        // yet) and this never resets (ZNet.Start() runs exactly once per session on both builds).
        private static bool s_started;

        public static void ApplyGlobalConfig(ZNet znet)
        {
            if (s_started) return; // cheap insurance against a theoretical second Start() this session
            s_started = true;
            Resync();
        }

        /// <summary>
        /// RED-TEAM FIX (DATA-LOSS REVIEW, major): re-reads T1/T3's CURRENT Active/config state and writes the
        /// resulting value (raised, or vanilla's own literal) to Steam's GLOBAL SendRateMin/Max/TimeoutConnected
        /// — called once from <see cref="ApplyGlobalConfig"/> at boot, and again from TransportInit's own
        /// SettingChanged handlers (T1.Active, T3.Active) and its KillSwitch flush callback, so a live toggle in
        /// EITHER direction actually takes effect with no restart, not just the initial apply. Previously
        /// <c>ApplyGlobalConfig</c> only ever ran once, ever, and only ever wrote the RAISED value (never
        /// vanilla's) — so disabling T1/T3 (or the whole kill switch) left the already-applied non-vanilla
        /// global Steam config in place indefinitely, the same "flag off but native state stays put" gap as
        /// T2's per-connection case. SetConfigValue can be called at any time — T2's own per-connection
        /// apply/revert (below) already proves this at runtime — so there is no structural reason the GLOBAL
        /// scope has to be write-once; only <see cref="ApplyGlobalConfig"/>'s BOOT-time entry point is (matching
        /// ZNet.Start()'s own once-per-session cadence).
        /// </summary>
        public static void Resync()
        {
            if (!s_started) return; // ZNet.Start() hasn't run yet this session; nothing to (re)apply until it does
            ZNet znet = ZNet.instance;
            if (znet == null) return; // session already tore down; no live socket layer to write to

            // Same isAuthoritative/Q10 gate as before (RT1) — a genuine remote client without the explicit Q10
            // opt-in must never have ITS OWN local Steam config touched at all, not even to write a
            // vanilla-equivalent value, so this still bails out entirely rather than falling through to a write.
            bool isAuthoritative = znet.IsServer();
            if (!isAuthoritative && !TransportLeverConfig.Q10Active) return;

            bool dedicated = znet.IsDedicated(); // still needed even when isAuthoritative, to pick the right Steam API class

            // With T9 on, T9 sets each connection's rate itself (starting at vanilla and rising only on evidence),
            // so the global value, which every connection T9 has not yet reached inherits, stays at vanilla. T1's
            // value is then T9's ceiling. With T9 off, T1 pins every connection at its fixed rate, as designed.
            int rate = TransportLeverConfig.T1.Active && !TransportLeverConfig.T9Active
                ? TransportLeverConfig.T1GlobalRateBps : VanillaGlobalRateBps;
            GCHandle rateHandle = GCHandle.Alloc(rate, GCHandleType.Pinned);
            try
            {
                bool raising = rate >= s_lastGlobalRate;
                SetConfig(dedicated, raising ? ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax : ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin,
                    ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero,
                    ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, rateHandle.AddrOfPinnedObject());
                SetConfig(dedicated, raising ? ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin : ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax,
                    ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero,
                    ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, rateHandle.AddrOfPinnedObject());
                s_lastGlobalRate = rate;
            }
            finally { rateHandle.Free(); } // never leak the pinned handle even if SetConfigValue itself throws/fails natively

            Telemetry.GetCounter(TransportLeverConfig.T1.Active ? "transport_t1_applied" : "transport_t1_reverted").Increment();
            Telemetry.SetGauge("transport_t1_rate_bps", rate);

            float timeoutMs = TransportLeverConfig.T3.Active ? TransportLeverConfig.T3_TimeoutConnectedMs.Value : VanillaTimeoutConnectedMs;
            GCHandle timeoutHandle = GCHandle.Alloc(timeoutMs, GCHandleType.Pinned);
            try
            {
                SetConfig(dedicated, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutConnected,
                    ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero,
                    ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Float, timeoutHandle.AddrOfPinnedObject());
            }
            finally { timeoutHandle.Free(); }

            Telemetry.GetCounter(TransportLeverConfig.T3.Active ? "transport_t3_applied" : "transport_t3_reverted").Increment();
            Telemetry.SetGauge("transport_t3_timeout_connected_ms", timeoutMs);
        }

        /// <summary>Shared by T1/T3 (global scope) and T2 (connection scope, below) — picks the GameServer vs. client Steam API class the same way vanilla itself does throughout ZSteamSocket.</summary>
        internal static bool SetConfig(bool dedicated, ESteamNetworkingConfigValue value, ESteamNetworkingConfigScope scope,
            IntPtr scopeObj, ESteamNetworkingConfigDataType dataType, IntPtr arg)
        {
            return dedicated
                ? SteamGameServerNetworkingUtils.SetConfigValue(value, scope, scopeObj, dataType, arg)
                : SteamNetworkingUtils.SetConfigValue(value, scope, scopeObj, dataType, arg);
        }

        // Last global rate written (vanilla's own value until we write one), so writes can follow the facts
        // file's order: raise the maximum before the minimum, lower the minimum before the maximum.
        private static int s_lastGlobalRate = VanillaGlobalRateBps;
    }

    /// <summary>
    /// Hook 2 (T2) — per-connection join burst (docs/design/01 Hook 2). Hooked on <c>RPC_PeerInfo</c>
    /// (SERVER:79821 / CLIENT:80002), the first point after the version/auth handshake where
    /// <c>peer.IsReady()</c> is guaranteed true and <c>peer.m_socket</c> is live — the same point vanilla
    /// itself uses to call <c>VersionMatch()</c> and start sending player lists.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
    internal static class Patch_ZNet_RPCPeerInfo_JoinBurst
    {
        static void Postfix(ZNet __instance, ZRpc rpc)
        {
            try
            {
                if (!TransportLeverConfig.T2.Active) return;
                // Same isAuthoritative/Q10 gate as Hook 1 (RED-TEAM FIX RT1) — RPC_PeerInfo runs on whichever
                // side RECEIVES the RPC, including a genuine remote client receiving the server's own reply as
                // part of its own join handshake.
                if (!__instance.IsServer() && !TransportLeverConfig.Q10Active) return;

                ZNetPeer peer = ZNetHelpers.GetPeer(__instance, rpc); // same private lookup vanilla itself just did; Core's own cached+validated wrapper
                if (peer == null || !peer.IsReady()) return; // handshake didn't succeed on this call
                if (!SteamSockets.TryGet(peer.m_socket, out _)) return; // T2 is only meaningful for the Steam transport (PlayFab has no per-connection Steam config)
                if (TransportLeverConfig.T9Active) return; // T9 owns per-connection rates; T2's value is its join ceiling

                JoinBurstController.TryApply(peer, __instance.IsDedicated());
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(Patch_ZNet_RPCPeerInfo_JoinBurst), e);
            }
        }
    }

    /// <summary>
    /// Once-per-second revert scan (docs/design/01 Hook 2's own "not shown" tick), piggybacked on vanilla's own
    /// <c>ZNet.Update()</c> rather than a second timer/coroutine (CORE-API.md: don't start a new periodic driver
    /// when an existing hot-path postfix can self-throttle instead).
    ///
    /// RED-TEAM FIX (DATA-LOSS REVIEW, blocker): this scan must run UNCONDITIONALLY, never gated on
    /// <c>TransportLeverConfig.T2.Active</c>. The apply side (<see cref="Patch_ZNet_RPCPeerInfo_JoinBurst"/>) is
    /// correctly gated on T2.Active — never START a new burst when the lever is off — but if the REVERT sweep
    /// used the same gate, flipping T2.Active off (or the whole kill switch, or `noodles disable`) mid-burst
    /// would make this Postfix return before ever calling <see cref="JoinBurstController.TickReverts"/> again,
    /// leaving that connection's native per-connection Steam rate pinned at up to 8x T1's rate for the rest of
    /// its lifetime — exactly the scenario this fix closes. <see cref="JoinBurstController.TickReverts"/> itself
    /// only ever REVERTS (never applies), so running it regardless of T2/Core.Enabled is always safe.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), "Update")]
    internal static class Patch_ZNet_Update_JoinBurstRevertTick
    {
        private static float s_lastTickRealtime;

        static void Postfix(ZNet __instance)
        {
            try
            {
                float now = Time.realtimeSinceStartup;
                if (now - s_lastTickRealtime < 1f) return; // Update() runs every frame; this lever only needs ~1 Hz
                s_lastTickRealtime = now;
                JoinBurstController.TickReverts(__instance);
                JoinBurstController.TickStallGuardWhileT9Off(__instance);
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(Patch_ZNet_Update_JoinBurstRevertTick), e);
            }
        }
    }

    internal static class JoinBurstController
    {
        internal sealed class BurstState
        {
            public float AppliedAtRealtime;
            public bool Reverted;
        }

        // Keyed by ZNetPeer OBJECT IDENTITY, not uid — docs/design/01 Hook 2's re-entry hazard: vanilla's own
        // IsConnected(uid) check reads the uid fresh off the packet, so a replayed PeerInfo on the SAME
        // already-established connection with a DIFFERENT claimed uid sails past it. Keying by object
        // reference makes a replay on the same ZNetPeer a no-op here regardless. Entries need no explicit
        // disconnect cleanup: once `peer` is dereferenced by ZNet's own peer-removal path the entry becomes
        // unreachable and is GC'd; a reconnect is a brand-new ZNetPeer object, so the burst logic correctly
        // runs again from a clean slate.
        private static readonly ConditionalWeakTable<ZNetPeer, BurstState> s_state = new ConditionalWeakTable<ZNetPeer, BurstState>();

        public static void TryApply(ZNetPeer peer, bool dedicated)
        {
            if (s_state.TryGetValue(peer, out _)) return; // idempotent — already bursting/burst-done for this connection

            // RATE GUARD (docs/impl/INTEGRATION-REVIEW.md "TheWall outage 17:02 and the client mod"): T9's own
            // per-tick raises are gated against a stalled connection in AdaptiveRate.cs; this is the twin check
            // for the ONLY other place a per-connection rate is ever raised — T2's own burst, which only runs
            // at all while T9 is off (see the caller's own T9Active guard). A one-shot "quality unknown right
            // now" read is all a JOIN needs (there is no history yet to run LinkGuard's own hysteresis against):
            // never hand a connection that already looks broken a higher ceiling than T1's own rate.
            if (SteamSockets.TryGet(peer.m_socket, out ZSteamSocket stallCheckSock) &&
                AdaptiveRate.TryGetStatus(dedicated, stallCheckSock, out SteamNetConnectionRealTimeStatus_t stallStatus) &&
                stallStatus.m_flConnectionQualityLocal < 0f)
            {
                Telemetry.GetPerPeerCounter("transport_t2_burst_skipped_stalled").Increment(peer);
                return; // stays at T1's own rate for this session; safe, never revisited (RPC_PeerInfo won't re-fire)
            }

            var state = new BurstState { AppliedAtRealtime = Time.realtimeSinceStartup };
            s_state.Add(peer, state);

            if (!ApplyRate(peer, dedicated, EffectiveBurstBps(), raising: true)) return; // ApplyRate already logged its own failure

            Telemetry.GetPerPeerCounter("transport_t2_burst_applied").Increment(peer);
        }

        /// <summary>Clamped to [T1's live value, 8x T1's live value] per docs/design/01's Configuration section — a dynamic bound BepInEx's own AcceptableValueRange can't express, so it's enforced here instead of at bind time.</summary>
        private static int EffectiveBurstBps()
        {
            int t1Reference = TransportLeverConfig.T1.Active ? TransportLeverConfig.T1GlobalRateBps : SteamRateController.VanillaGlobalRateBps;
            int configured = TransportLeverConfig.T2_JoinBurstBps.Value;
            return Math.Max(t1Reference, Math.Min(configured, t1Reference * 8));
        }

        /// <summary>
        /// Once-per-second scan (docs/design/01 Hook 2 lifecycle step 3): revert to T1's global rate the moment
        /// the connection's own send queue drains below Q4's congestion floor, or after T2's hard time cap,
        /// whichever comes first.
        /// </summary>
        public static void TickReverts(ZNet znet)
        {
            bool dedicated = znet.IsDedicated();
            int t1Reference = TransportLeverConfig.T1.Active ? TransportLeverConfig.T1GlobalRateBps : SteamRateController.VanillaGlobalRateBps;
            int congestionFloor = TransportLeverConfig.Q4_CongestionFloorBytes.Value;
            float maxBurstSeconds = TransportLeverConfig.T2_MaxBurstSeconds.Value;

            foreach (ZNetPeer peer in znet.GetConnectedPeers())
            {
                if (!s_state.TryGetValue(peer, out BurstState state) || state.Reverted) continue;
                if (!SteamSockets.TryGet(peer.m_socket, out _)) continue; // defensive only — only Steam peers ever get a state at all
                if (TransportLeverConfig.T9Active) { state.Reverted = true; continue; } // T9 took this connection over and writes its own rate

                bool drained = peer.m_socket.GetSendQueueSize() < congestionFloor;
                bool timedOut = Time.realtimeSinceStartup - state.AppliedAtRealtime > maxBurstSeconds;
                if (!drained && !timedOut) continue;

                if (ApplyRate(peer, dedicated, t1Reference, raising: false))
                {
                    state.Reverted = true;
                    Telemetry.GetPerPeerCounter(drained ? "transport_t2_burst_reverted_drained" : "transport_t2_burst_reverted_timeout").Increment(peer);
                }
                // else: ApplyRate already logged; leave state un-reverted so the next tick retries.
            }
        }

        /// <summary>
        /// RED-TEAM FIX (DATA-LOSS REVIEW, blocker): force-reverts every currently-tracked, un-reverted bursting
        /// connection to T1's rate IMMEDIATELY, regardless of drain/timeout state — used by the KillSwitch flush
        /// callback (`noodles disable`, AutoStandDown) and T2's own SettingChanged-to-off, so those two paths
        /// don't have to wait for <see cref="TickReverts"/>'s next ~1s tick. Safe to call at any time, including
        /// when T2 has no bursting connections at all (the loop is then a no-op).
        /// </summary>
        public static void RevertAll()
        {
            ZNet znet = ZNet.instance;
            if (znet == null) return; // session already tore down; no live connections to revert

            bool dedicated = znet.IsDedicated();
            int t1Reference = TransportLeverConfig.T1.Active ? TransportLeverConfig.T1GlobalRateBps : SteamRateController.VanillaGlobalRateBps;

            foreach (ZNetPeer peer in znet.GetConnectedPeers())
            {
                if (!s_state.TryGetValue(peer, out BurstState state) || state.Reverted) continue;
                if (!SteamSockets.TryGet(peer.m_socket, out _)) continue;
                if (TransportLeverConfig.T9Active) { state.Reverted = true; continue; } // T9 owns this connection's rate now

                if (ApplyRate(peer, dedicated, t1Reference, raising: false))
                {
                    state.Reverted = true;
                    Telemetry.GetPerPeerCounter("transport_t2_burst_reverted_forced").Increment(peer);
                }
                // else: ApplyRate already logged; leave state un-reverted so the next TickReverts pass retries.
            }
        }

        /// <summary>
        /// RED-TEAM FIX (fixer pass, major — "TheWall outage 17:02" finding, send-rate side): while T9 is off,
        /// T1's fixed global rate and T2's join burst were the only things that ever raise a connection's rate,
        /// and neither had any ONGOING stall awareness for a connection that stalls after joining — <see
        /// cref="TryApply"/>'s own one-shot check (below) only covers the instant of joining, and <see
        /// cref="TickReverts"/> only manages previously-bursted connections' drain-based revert, so it silently
        /// skips a peer that was never bursted (or already reverted) and later stalls. Left alone, that
        /// connection keeps inheriting T1's elevated GLOBAL rate indefinitely — the same rate-side shape as the
        /// original incident, just without T9's own raise loop driving it.
        ///
        /// Runs every second from the same unconditional tick as <see cref="TickReverts"/> (both called from
        /// <see cref="Patch_ZNet_Update_JoinBurstRevertTick"/>'s Postfix), independently of any peer's <see
        /// cref="BurstState"/> — this checks EVERY connected peer, tracked or not — and is a pure no-op the
        /// instant T9 is on (T9's own per-tick loop in AdaptiveRate.cs already runs this same <see
        /// cref="LinkGuard"/> check for every connection it manages). Same isAuthoritative/Q10 opt-in gate as
        /// <see cref="SteamRateController.Resync"/> — a remote client without the explicit Q10 opt-in must
        /// never have its own local Steam config touched, not even to pin it at vanilla.
        ///
        /// Re-asserts vanilla every tick while stalled (cheap, ~1 Hz) rather than only on the onset edge, so
        /// this can never lose a same-tick write race against <see cref="TickReverts"/> reverting a burst to
        /// T1's rate; on recovery it CLEARS the override (rather than writing a value) so the connection falls
        /// back to whatever the global scope / a still-live burst state would otherwise give it.
        /// </summary>
        private static readonly ConditionalWeakTable<ZNetPeer, OffGuardState> s_offGuard = new ConditionalWeakTable<ZNetPeer, OffGuardState>();

        private sealed class OffGuardState { public LinkGuardState Guard; }

        public static void TickStallGuardWhileT9Off(ZNet znet)
        {
            if (TransportLeverConfig.T9Active) return; // T9 owns every managed connection's stall handling itself

            bool isAuthoritative = znet.IsServer();
            if (!isAuthoritative && !TransportLeverConfig.Q10Active) return; // never touch an opted-out client's own connection

            bool dedicated = znet.IsDedicated();
            foreach (ZNetPeer peer in znet.GetConnectedPeers())
            {
                if (peer == null || !peer.IsReady() || !SteamSockets.TryGet(peer.m_socket, out ZSteamSocket sock)) continue;
                if (!AdaptiveRate.TryGetStatus(dedicated, sock, out SteamNetConnectionRealTimeStatus_t status)) continue;

                OffGuardState holder = s_offGuard.GetValue(peer, _ => new OffGuardState());
                long queueBytes = sock.GetSendQueueSize();
                LinkGuardDecision guard = LinkGuard.Evaluate(ref holder.Guard, 1f, status.m_flConnectionQualityLocal,
                    status.m_flInBytesPerSec, queueBytes, SteamRateController.VanillaGlobalRateBps);

                if (guard.Stalled)
                {
                    if (ApplyRate(peer, dedicated, SteamRateController.VanillaGlobalRateBps, raising: false) && guard.JustStalled)
                        Telemetry.GetPerPeerCounter("transport_t2_offguard_stalled").Increment(peer);
                }
                else if (guard.JustRecovered)
                {
                    ClearConnectionOverride(dedicated, sock);
                    Telemetry.GetPerPeerCounter("transport_t2_offguard_recovered").Increment(peer);
                }
            }
        }

        /// <summary>Releases this connection's per-connection SendRateMin/Max override (null clears back to the
        /// parent/global scope, facts file §3) — the T9-off stall guard's own recovery path, so a peer that was
        /// never tracked by <see cref="BurstState"/> falls back to whatever the global rate / a still-live burst
        /// would otherwise give it, rather than being left pinned at vanilla forever.</summary>
        private static void ClearConnectionOverride(bool dedicated, ZSteamSocket sock)
        {
            try
            {
                HSteamNetConnection con = AccessTools.FieldRefAccess<ZSteamSocket, HSteamNetConnection>(sock, "m_con");
                IntPtr conPtr = (IntPtr)(long)con.m_HSteamNetConnection;
                bool ok = SteamRateController.SetConfig(dedicated, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin,
                    ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, conPtr, ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, IntPtr.Zero);
                ok &= SteamRateController.SetConfig(dedicated, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax,
                    ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, conPtr, ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, IntPtr.Zero);
                if (!ok) NoodlesLog.Warning("T9-off stall guard: could not clear a connection's rate override; it stays pinned at vanilla until the next recovery check.");
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(JoinBurstController) + ".ClearConnectionOverride", e);
            }
        }

        /// <summary>Shared apply/revert helper — both are the same Steam per-connection SendRateMin/Max write, just a different target value.</summary>
        private static bool ApplyRate(ZNetPeer peer, bool dedicated, int rateBps, bool raising)
        {
            try
            {
                if (!SteamSockets.TryGet(peer.m_socket, out ZSteamSocket sock)) return false; // 1.0.3: was a hard cast
                // m_con is a private field with no accessor (SERVER:86175) — AccessTools.FieldRefAccess bypasses
                // the Publicizer's compile-time-only visibility the same defensive way Core's own ZNetHelpers
                // does for GetPeer, rather than relying on direct field syntax working against the real
                // (unmodified, still-private) runtime assembly.
                HSteamNetConnection con = AccessTools.FieldRefAccess<ZSteamSocket, HSteamNetConnection>(sock, "m_con");

                GCHandle h = GCHandle.Alloc(rateBps, GCHandleType.Pinned);
                try
                {
                    IntPtr conPtr = (IntPtr)(long)con.m_HSteamNetConnection;
                    // Facts file §3: raise the maximum before the minimum, lower the minimum before the maximum.
                    SteamRateController.SetConfig(dedicated, raising ? ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax : ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin,
                        ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, conPtr,
                        ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, h.AddrOfPinnedObject());
                    SteamRateController.SetConfig(dedicated, raising ? ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin : ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax,
                        ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, conPtr,
                        ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, h.AddrOfPinnedObject());
                }
                finally { h.Free(); }
                return true;
            }
            catch (Exception e)
            {
                // docs/design/01 Data-loss analysis #3: fail closed (skip this one connection's burst/revert,
                // never throw into RPC_PeerInfo's own call chain, which would abort vanilla's own
                // SendPlayerList/AddPeer calls that follow the postfix point).
                NoodlesLog.PatchException(nameof(JoinBurstController) + ".ApplyRate", e);
                return false;
            }
        }
    }
}
