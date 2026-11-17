using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.Ownership
{
    /// <summary>
    /// H4 (docs/design/03-ownership-and-handover.md) — O9's RTT source. <c>ZRpc.UpdatePing</c>/<c>ReceivePing</c>
    /// (SERVER:83908/83927, confirmed this pass) are core, unmodified <c>ZRpc</c> keepalive: every connection,
    /// vanilla or modded, sends a bare <c>{0,true}</c> ping roughly every <c>m_pingInterval</c> (1s) and replies
    /// <c>{0,false}</c> to any received ping. This needs no client mod at all — it observes traffic a vanilla
    /// client already generates and computes a real RTT from the SERVER's own side only (server-only: the mod
    /// only needs the server's view of RTT to each peer, per docs/design/03's own "Side" note for H4).
    ///
    /// Gated on <see cref="Features_Ownership.O9"/> alone (independent of <c>OwnershipAuthority.Active</c>, per
    /// the Configuration reference) so this can run standalone, exactly as O9 is designed to.
    /// </summary>
    internal static class RttTracker
    {
        // --- Config --------------------------------------------------------------------------------------------

        private static ConfigEntry<float> s_rttEwmaAlpha;

        // --- Reflection: private ZRpc.m_pingTimer (instance) / m_pingInterval (static) --------------------------

        // Both confirmed private this pass: `private float m_pingTimer;` SERVER:83855, `private static float
        // m_pingInterval = 1f;` SERVER:83859. Read-only use (a threshold comparison mirroring vanilla's own
        // `m_pingTimer > m_pingInterval` gate, SERVER:83911) -- this is geometry-grade arithmetic, not a
        // reimplementation of any DECISION vanilla itself makes; vanilla's own UpdatePing body still runs
        // completely untouched either way.
        private static readonly AccessTools.FieldRef<ZRpc, float> s_pingTimerRef =
            AccessTools.FieldRefAccess<ZRpc, float>("m_pingTimer");
        private static readonly FieldInfo s_pingIntervalField = AccessTools.Field(typeof(ZRpc), "m_pingInterval");

        // AccessTools.StaticFieldRefAccess<F>(Type,string) returns `ref F` directly (a byref, call-site-only --
        // see Timeouts.cs's own RpcTimeoutController for the same established pattern), not a cacheable
        // FieldRef<F> delegate, so unlike s_pingTimerRef above this can't be stored as a field; read via plain
        // FieldInfo.GetValue instead (this is READ-ONLY here, never written, so the extra reflection call's cost
        // is irrelevant at ~1 call/second/peer).
        private static float PingInterval => s_pingIntervalField != null ? (float)s_pingIntervalField.GetValue(null) : 1f;

        // --- State (server main-thread only) ----------------------------------------------------------------------

        // Keyed by ZRpc (the outgoing-ping timestamp is per raw connection, resolved to a peer only when a
        // sample completes) and by ZNetPeer reference for the smoothed result (matches PeerState's own "a
        // reconnect is a new object, nothing to carry over" convention, docs/impl/CORE-API.md).
        private static readonly Dictionary<ZRpc, float> s_pingSentAtRealtime = new Dictionary<ZRpc, float>();

        private sealed class RttState
        {
            public float SmoothedMs = -1f; // -1 = no sample yet
        }
        private static readonly Dictionary<ZNetPeer, RttState> s_stateByPeer = new Dictionary<ZNetPeer, RttState>();

        /// <summary>Smoothed RTT in ms for a peer, or -1f if no sample has landed yet (treated as "unknown", never as 0/best).</summary>
        internal static float Smoothed(ZNetPeer peer)
        {
            return s_stateByPeer.TryGetValue(peer, out var s) ? s.SmoothedMs : -1f;
        }

        private static void RecordSample(ZRpc rpc)
        {
            if (!s_pingSentAtRealtime.TryGetValue(rpc, out float sentAt)) return; // no matching outgoing ping tracked -- skip, not an error (e.g. first pong after this feature was just turned on)
            s_pingSentAtRealtime.Remove(rpc);

            float rttMs = (Time.realtimeSinceStartup - sentAt) * 1000f;
            if (rttMs < 0f) return; // defensive only -- realtimeSinceStartup is monotonic, this should be unreachable

            ZNetPeer peer = ZNet.instance?.GetPeers()?.Find(p => p.m_rpc == rpc);
            if (peer == null) return; // disconnected between ping and pong

            if (!s_stateByPeer.TryGetValue(peer, out var state))
            {
                state = new RttState();
                s_stateByPeer[peer] = state;
            }

            // O9 adjudication point 5: discard a single sample more than 5x the current smoothed value (a GC
            // pause or a mispredicted late ACK, not a real latency shift) -- only once a smoothed baseline exists.
            if (state.SmoothedMs > 0f && rttMs > state.SmoothedMs * 5f)
            {
                Telemetry.GetCounter("ownership.o9.rttOutliersDiscarded").Increment();
                return;
            }

            float alpha = Mathf.Clamp(s_rttEwmaAlpha.Value, 0.05f, 0.5f);
            state.SmoothedMs = state.SmoothedMs < 0f ? rttMs : (alpha * rttMs + (1f - alpha) * state.SmoothedMs);

            Telemetry.GetPerPeerCounter("ownership.o9.rttSamples").Increment(peer);
            Telemetry.GetPerPeerCounter("ownership.o9.rttSmoothed").Set(peer, (long)state.SmoothedMs);
        }

        internal static void ForgetPeer(ZNetPeer peer)
        {
            if (peer != null) s_stateByPeer.Remove(peer);
        }

        // --- Patches ---------------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(ZRpc), "UpdatePing")]
        internal static class Patch_ZRpc_UpdatePing_RecordSend
        {
            static void Prefix(ZRpc __instance, float dt)
            {
                if (!Features_Ownership.O9.Active) return;
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                try
                {
                    // Mirrors vanilla's own about-to-send condition (SERVER:83910-83911: `m_pingTimer += dt; if
                    // (m_pingTimer > m_pingInterval) { ... send ... }`) without touching the field's value --
                    // read-only, vanilla's own Postfix-free body still does the real increment/reset itself.
                    float timer = s_pingTimerRef(__instance);
                    float interval = PingInterval;
                    if (timer + dt > interval)
                    {
                        s_pingSentAtRealtime[__instance] = Time.realtimeSinceStartup;
                    }
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_ZRpc_UpdatePing_RecordSend), e);
                }
            }
        }

        /// <summary>
        /// RED-TEAM FIX (docs/design/03 RT2 minor, carried over): the discriminator is just "was the bool false"
        /// (a pong/reply) -- the original sketch's extra `package.GetPos() == 0` check is dead code, confirmed
        /// this pass too (<c>ZRpc.HandlePackage</c>, SERVER:83952-83970, always consumes 4 bytes via
        /// <c>ReadInt()</c> before dispatching here when that int is 0, so <c>GetPos()</c> is always 4 by the
        /// time this runs, never 0). A plain <c>void</c> prefix that peeks-and-restores position, never a
        /// bool-returning one -- vanilla's own reply-or-reset body always still runs, untouched either way.
        /// </summary>
        [HarmonyPatch(typeof(ZRpc), "ReceivePing")]
        internal static class Patch_ZRpc_ReceivePing_RecordSample
        {
            static void Prefix(ZRpc __instance, ZPackage package)
            {
                if (!Features_Ownership.O9.Active) return;
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                try
                {
                    int pos = package.GetPos();
                    bool isPing = package.ReadBool();
                    package.SetPos(pos); // restore -- vanilla's own ReceivePing body must read this exact byte itself
                    if (!isPing) RecordSample(__instance); // false == a pong (reply) landed
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_ZRpc_ReceivePing_RecordSample), e);
                }
            }
        }

        /// <summary>Cleanup, mirroring Core's own PeerState disconnect patch (a separate, independent postfix on the same vanilla method -- Harmony supports this, and this module owns its own cleanup rather than depending on Core's).</summary>
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect), new[] { typeof(ZNetPeer) })]
        internal static class Patch_ZNet_Disconnect_RttCleanup
        {
            static void Postfix(ZNetPeer peer)
            {
                try
                {
                    if (peer?.m_rpc != null) s_pingSentAtRealtime.Remove(peer.m_rpc);
                    ForgetPeer(peer);
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_ZNet_Disconnect_RttCleanup), e);
                }
            }
        }

        // --- Init --------------------------------------------------------------------------------------------------

        [NoodlesModuleInit]
        internal static void Init()
        {
            s_rttEwmaAlpha = NoodlesConfig.File.Bind("O9", "RttEwmaAlpha", 0.2f,
                new ConfigDescription("Smoothing factor for the per-peer RTT EWMA O9 decides switches on. No " +
                    "vanilla equivalent (vanilla never smooths ping at all). 0.2 is a standard, moderate EWMA " +
                    "constant -- responsive to a real latency shift within a few samples without over-reacting " +
                    "to single-sample jitter; not a load-scaling knob, so \"not overkill\" doesn't apply the same " +
                    "way it does to a cadence/cap value.",
                    new AcceptableValueRange<float>(0.05f, 0.5f)));

            PatchRegistry.Register("O9", typeof(Patch_ZRpc_UpdatePing_RecordSend), PatchSide.Both);
            PatchRegistry.Register("O9", typeof(Patch_ZRpc_ReceivePing_RecordSample), PatchSide.Both);
            PatchRegistry.Register("O9", typeof(Patch_ZNet_Disconnect_RttCleanup), PatchSide.Both);

            if (s_pingTimerRef == null || s_pingIntervalField == null)
            {
                NoodlesLog.Error("RttTracker: ZRpc.m_pingTimer/m_pingInterval did not resolve via reflection -- " +
                    "O9 has no RTT source. Forcing O9 off.");
                Features_Ownership.O9?.ForceOff("ZRpc.m_pingTimer/m_pingInterval failed to resolve at Awake");
            }
        }
    }
}
