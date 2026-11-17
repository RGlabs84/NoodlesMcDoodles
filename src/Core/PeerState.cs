using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace NoodlesMcDoodles.Core
{
    /// <summary>
    /// Per-connection handshake/capability/flag state. Every capability-gated feature in every module checks
    /// this (via <see cref="TryGet"/>/<see cref="IsNegotiatedWithCapability"/>) before doing anything that
    /// needs the OTHER side to also be running NoodlesMcDoodles.
    ///
    /// Keyed by <see cref="ZNetPeer"/> OBJECT REFERENCE, deliberately not by <c>m_uid</c> or connection handle
    /// (doc 02's own Data-loss analysis, "Crash / disconnect / reconnect mid-handshake": "PeerState is keyed
    /// by ZNetPeer object reference ... not by connection handle or uid value; a reconnect starts the
    /// handshake from zero on both sides — no state is carried over, so there's nothing to lose"). This also
    /// sidesteps G1-L1's own duplicate-peer hazard (doc 02 Hook 1): a re-entrant PeerInfo reuses the SAME
    /// ZNetPeer object (vanilla's `GetPeer(rpc)` returns the one already tracked for that ZRpc), so it can
    /// never accidentally look like two different peers to this dictionary.
    /// </summary>
    internal sealed class PeerState
    {
        public bool HelloSent { get; internal set; }
        public bool Negotiated { get; internal set; }

        /// <summary>
        /// This peer's own negotiation window timed out with no Hello/HelloAck ever completing — the remote
        /// side is treated as running plain vanilla (or, from a client's point of view, the SERVER is vanilla)
        /// for the rest of this connection. Bookkeeping only, never a disconnect trigger (doc 02 Hook 2).
        /// Read by <see cref="DeploymentLog"/>/<see cref="Telemetry"/> for the deployment-mode logging and the
        /// server-wide <c>deployment_mode</c> telemetry tag (mixed-and-host runtime review, 2026-09-28: this
        /// used to be write-only — nothing ever read it).
        /// </summary>
        public bool KnownVanilla { get; internal set; }

        /// <summary><see cref="Time.realtimeSinceStartup"/> at the moment Hello was sent — kept for the resend-guard at
        /// <c>Negotiation.cs</c>'s own idempotency check (<see cref="HelloSent"/>). NOT used for the timeout scan any more —
        /// see <see cref="NegotiationWindowStartedAtRealtime"/>.</summary>
        public float HelloSentAtRealtime { get; internal set; }

        /// <summary>
        /// True once THIS side's own half of the negotiation window has started — server: the moment its Hello
        /// was sent; client: the moment it registered to receive one (<c>Negotiation.cs</c>'s
        /// <c>Patch_RPC_PeerInfo_Handshake.Postfix</c>, both branches). Generic across both sides so
        /// <see cref="CheckTimeouts"/> can run one shared scan instead of a server-only one — RUNTIME REVIEW FIX
        /// (major, 2026-09-28): a client's own half of this window never started before this field existed
        /// (only <see cref="HelloSent"/>, server-branch-only, gated the old scan), so a client connected to a
        /// vanilla/unresponsive server had no timeout, no counter, and no log line, ever.
        /// </summary>
        public bool NegotiationWindowStarted { get; internal set; }

        /// <summary><see cref="Time.realtimeSinceStartup"/> at the moment <see cref="NegotiationWindowStarted"/> became true.</summary>
        public float NegotiationWindowStartedAtRealtime { get; internal set; }

        public int RemoteProtocolVersion { get; internal set; }
        public long RemoteCapabilities { get; internal set; }
        public string RemoteModVersion { get; internal set; }

        /// <summary>0 = never pushed. Server-side only; read/written by <see cref="ConfigSyncRpc"/>.</summary>
        public float LastConfigSyncPushRealtime { get; internal set; }

        public bool HasCapability(long bit) => Negotiated && (RemoteCapabilities & bit) != 0;

        private static readonly Dictionary<ZNetPeer, PeerState> s_states = new Dictionary<ZNetPeer, PeerState>();
        private static readonly object s_lock = new object();

        /// <summary>Creates the entry on first touch if needed — this IS "created on connect": the earliest
        /// legitimate touch point for any peer is negotiation's own RPC_PeerInfo postfix, which runs once per
        /// successful connect (doc 02 Hook 2).</summary>
        public static PeerState GetOrCreate(ZNetPeer peer)
        {
            lock (s_lock)
            {
                if (!s_states.TryGetValue(peer, out var state))
                {
                    state = new PeerState();
                    s_states[peer] = state;
                }
                return state;
            }
        }

        public static bool TryGet(ZNetPeer peer, out PeerState state)
        {
            lock (s_lock)
            {
                return s_states.TryGetValue(peer, out state);
            }
        }

        /// <summary>Called from the disconnect cleanup patch below. Never leaves a stale entry for a peer that's gone.</summary>
        internal static void Remove(ZNetPeer peer)
        {
            lock (s_lock)
            {
                s_states.Remove(peer);
            }
            Telemetry.RemovePeerFromAllCounters(peer);
        }

        /// <summary>
        /// Snapshot for the negotiation timeout scan (<see cref="NoodlesHeartbeat"/>) — copied out under the
        /// lock so the scan itself can run lock-free and can't deadlock against a concurrent connect/disconnect.
        /// </summary>
        internal static List<KeyValuePair<ZNetPeer, PeerState>> Snapshot()
        {
            lock (s_lock)
            {
                return new List<KeyValuePair<ZNetPeer, PeerState>>(s_states);
            }
        }

        /// <summary>
        /// Every frame (cheap even so, given vanilla's own 10-player cap, SERVER's `GetNrOfPlayers() >= 10`
        /// join gate) — a peer whose negotiation window is still open past <c>Handshake.TimeoutSeconds</c> with
        /// no Hello/HelloAck ever completing is marked <see cref="PeerState.KnownVanilla"/> so later code stops
        /// re-checking it (doc 02 Hook 2: "purely local bookkeeping state, never a disconnect trigger").
        ///
        /// RUNTIME REVIEW FIX (major, 2026-09-28): one shared scan for BOTH sides now, keyed on
        /// <see cref="PeerState.NegotiationWindowStarted"/> (set by both branches of
        /// <c>Negotiation.Patch_RPC_PeerInfo_Handshake.Postfix</c>) instead of the server-only
        /// <see cref="PeerState.HelloSent"/> — a client connected to a vanilla/unresponsive server previously had
        /// no timeout path at all (<c>HelloSent</c> is set only in the server branch), so it just waited forever
        /// with no counter and no log line. <see cref="DeploymentLog"/> turns each transition here into the
        /// per-peer (server) / per-connection (client) log line the deployment-mode logging requirement asks for.
        /// </summary>
        internal static void CheckTimeouts()
        {
            bool isServer = ZNet.instance != null && ZNet.instance.IsServer();
            float now = Time.realtimeSinceStartup;
            float timeout = NoodlesConfig.HandshakeTimeoutSeconds.Value;
            // ZNet.GetPeers() hands back its own live m_peers List<ZNetPeer> reference (SERVER/CLIENT confirmed
            // identical, `return m_peers;` — no copy), so reading it once per scan is a field read, not an
            // allocation; null only between world sessions (no ZNet.instance yet/any more).
            List<ZNetPeer> livePeers = ZNet.instance?.GetPeers();
            foreach (var kv in Snapshot())
            {
                ZNetPeer peer = kv.Key;
                PeerState state = kv.Value;

                // STALE-ENTRY PRUNE (root cause of the live peers_vanilla/peers_modded telemetry leak,
                // 2026-09-29 runtime review): the only intended removal path, Patch_ZNet_Disconnect_PeerStateCleanup
                // below, turned out to have no PatchRegistry.Register call anywhere in this file — see
                // PeerState.Init — so Harmony never installed it and Remove(peer) was unreachable dead code from
                // the day this file was written. A peer that finished negotiation (Negotiated or KnownVanilla
                // true) sails straight past the early-out two lines below on every later frame, forever, so this
                // check must run BEFORE that early-out, not after it — otherwise a settled peer's entry, exactly
                // the case that leaked live (peers_vanilla=1.0 read forever after the vanilla peer's "Disposing
                // socket"/"Closing socket <steamid>"), would never reach here at all. Belt-and-suspenders even
                // now that the patch IS registered: this self-heals within one frame if that registration is
                // ever lost again, independent of whether ZNet.Disconnect's postfix ran for this peer at all.
                // Safe to call Remove() while iterating — Snapshot() above is a copy taken under the lock, and
                // Remove() takes the lock again on the real s_states, so there's no concurrent-modification or
                // deadlock hazard.
                if (livePeers != null && !livePeers.Contains(peer))
                {
                    Remove(peer);
                    continue;
                }

                if (!state.NegotiationWindowStarted || state.Negotiated || state.KnownVanilla) continue;
                if ((now - state.NegotiationWindowStartedAtRealtime) <= timeout) continue;

                state.KnownVanilla = true;
                try
                {
                    if (isServer)
                    {
                        Telemetry.Counters.HandshakeTimedOutAsVanilla.Increment();
                        DeploymentLog.LogServerPeerVanilla(peer, state);
                    }
                    else
                    {
                        DeploymentLog.LogClientServerVanilla(state, timeout);
                    }
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(CheckTimeouts) + ".DeploymentLog", e);
                }
            }
        }

        /// <summary>
        /// Registers <see cref="Patch_ZNet_Disconnect_PeerStateCleanup"/> — ROOT CAUSE of the live
        /// peers_vanilla/peers_modded telemetry leak (2026-09-29 runtime review): this file had no
        /// <see cref="NoodlesModuleInitAttribute"/>-tagged method at all, so <see cref="PatchRegistry.Register"/>
        /// was never called for that patch class. <see cref="NoodlesPlugin"/> deliberately never calls Harmony's
        /// own <c>PatchAll()</c> (see <see cref="PatchRegistry.ApplyAll"/>'s remarks: "Deliberately NEVER
        /// harmony.PatchAll()") — <see cref="PatchRegistry.ApplyAll"/> only ever patches classes present in its
        /// own registry, so a class Harmony was never TOLD about is simply never installed, silently, with no
        /// exception and no log line anywhere, because Harmony has no way to know the class exists. Confirmed
        /// via the "PatchRegistry: N feature(s) patched" boot line and a grep of every <c>PatchRegistry.Register</c>
        /// call in the assembly: this feature name appeared nowhere. <c>ZNet.Disconnect(ZNetPeer)</c> therefore
        /// ran with none of this mod's postfixes attached, so <see cref="Remove"/> was unreachable dead code from
        /// the moment this file was written — every peer that ever completed a handshake stayed in
        /// <see cref="s_states"/>, Negotiated/KnownVanilla and all, for the rest of the process. Own feature
        /// name, deliberately not folded into "Negotiation" or any other existing group: <see cref="PatchRegistry.ApplyAll"/>
        /// unpatches every sibling of a feature the moment ANY one of that feature's classes fails to apply, and
        /// this cleanup must never be taken down as collateral damage from an unrelated patch failure, or the
        /// exact same leak returns under a different name.
        /// </summary>
        [NoodlesModuleInit(10)]
        internal static void Init()
        {
            PatchRegistry.Register("PeerStateCleanup", typeof(Patch_ZNet_Disconnect_PeerStateCleanup), PatchSide.Both);
        }
    }

    /// <summary>
    /// <c>ZNet.Disconnect(ZNetPeer peer)</c> — SERVER/CLIENT confirmed identical, `ClearPlayerData(peer);
    /// m_peers.Remove(peer); peer.Dispose();`. Postfix-only cleanup so <see cref="PeerState"/> and per-peer
    /// telemetry never leak an entry for a peer that's gone, regardless of Core.Enabled or any feature flag —
    /// removing a dictionary entry has no gameplay effect, so there's nothing to gate: this runs unconditionally,
    /// the same way it would if the mod were fully disabled, because "disabled" must never mean "leaks memory
    /// for the rest of the session" either.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect), new[] { typeof(ZNetPeer) })]
    internal static class Patch_ZNet_Disconnect_PeerStateCleanup
    {
        static void Postfix(ZNetPeer peer)
        {
            try
            {
                if (peer != null) PeerState.Remove(peer);
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(Patch_ZNet_Disconnect_PeerStateCleanup), e);
            }
        }
    }
}
