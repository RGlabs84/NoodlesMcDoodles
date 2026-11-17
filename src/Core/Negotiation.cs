using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace NoodlesMcDoodles.Core
{
    /// <summary>
    /// The Hello handshake (doc 02, Hooks 1 &amp; 2), reshaped into a two-message exchange to honor DESIGN.md
    /// Lead decision 3, which the per-module blueprint's own single-symmetric-message sketch predates and
    /// does not implement: "A NoodlesMcDoodles client never sends a NoodlesMcDoodles RPC to a server until it
    /// has received the server's Hello" — because `Wubarrk-Wonderland`'s `ModEnforcementPatches` can kick a
    /// non-admin client for sending an RPC hash outside its vanilla allowlist, and the only way a client can
    /// know a server has that allowlisted (or Wonderland isn't even installed there) is to have already heard
    /// from it first.
    ///
    /// Wire shape:
    /// - <c>"NoodlesMcDoodles.v1.Hello"</c> — SERVER -&gt; CLIENT only. Sent once, right after a successful
    ///   <c>RPC_PeerInfo</c>. The client only ever REGISTERS a handler for this name from its own
    ///   <c>RPC_PeerInfo</c> postfix — registering sends nothing (a local dictionary entry, confirmed
    ///   `ZRpc.Register` does Remove-then-Add, never throws — SERVER:83981 area), so this is safe before any
    ///   confirmation the peer is modded.
    /// - <c>"NoodlesMcDoodles.v1.HelloAck"</c> — CLIENT -&gt; SERVER only, and ONLY sent from inside the
    ///   client's own Hello handler, i.e. strictly after the server's Hello has actually arrived and been
    ///   protocol-version-checked. This is the client's first-ever NoodlesMcDoodles send, satisfying Lead
    ///   decision 3 by construction rather than by convention.
    ///
    /// Both payloads carry the same shape (ProtocolVersion:int, Capabilities:long, ModVersion:string) so
    /// either side ends up with a fully negotiated <see cref="PeerState"/> once both messages have crossed.
    /// </summary>
    internal static class Negotiation
    {
        public const int ProtocolVersion = 1; // Handshake.ProtocolVersion — compiled constant, not config (doc 02).
        private const string HelloRpcName = "NoodlesMcDoodles.v1.Hello";
        private const string HelloAckRpcName = "NoodlesMcDoodles.v1.HelloAck";

        /// <summary>
        /// L7 (deployment-modes review, 2026-09-28): <c>ModVersion</c> is wire data from the OTHER side — a
        /// hostile or simply buggy peer, not necessarily this mod's own client, could send an arbitrarily long
        /// or control-character-laden string, and it's stored (<see cref="PeerState.RemoteModVersion"/>) and
        /// logged verbatim by <see cref="DeploymentLog"/>. Clamp to a sane display length and replace anything
        /// outside printable ASCII before it's ever stored, so it can never bloat or inject into a log line.
        /// </summary>
        private static string SanitizeRemoteModVersion(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            if (raw.Length > 32) raw = raw.Substring(0, 32);

            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw) sb.Append(c >= 0x20 && c < 0x7F ? c : '?');
            return sb.ToString();
        }

        private static void SendHandshakePayload(ZRpc rpc, string rpcName)
        {
            var pkg = new ZPackage();
            pkg.Write(ProtocolVersion);
            pkg.Write(Capabilities.Mask);
            pkg.Write(PluginInfo.PLUGIN_VERSION);
            rpc.Invoke(rpcName, pkg);
        }

        /// <summary>
        /// Client: a Hello that arrived before this client processed the server's PeerInfo, kept until
        /// <see cref="Patch_RPC_PeerInfo_Handshake"/> replays it. At most one server connection exists, so
        /// <see cref="Patch_ZNet_OnNewConnection_RegisterEarlyHello"/> clears it on every new connection.
        /// </summary>
        private static readonly Dictionary<ZRpc, ZPackage> s_earlyHello = new Dictionary<ZRpc, ZPackage>();

        /// <summary>
        /// Client: registered from <c>OnNewConnection</c>, replaced by <see cref="RPC_NoodlesHello"/> in the
        /// client's own RPC_PeerInfo postfix. Only keeps the package, so the ack still leaves strictly after this
        /// client has processed the server's PeerInfo, the same order as before 1.0.2.
        /// </summary>
        private static void RPC_NoodlesHelloEarly(ZRpc rpc, ZPackage pkg)
        {
            try
            {
                if (rpc == null || pkg == null) return;
                s_earlyHello[rpc] = pkg; // ZRpc hands each handler its own ZPackage (ReadPackage copies), safe to keep.
                Telemetry.GetCounter("handshake_hello_early").Increment();
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(RPC_NoodlesHelloEarly), e);
            }
        }

        /// <summary>Client-side handler for the server's Hello. First point a modded client is allowed to send anything (Lead decision 3).</summary>
        private static void RPC_NoodlesHello(ZRpc rpc, ZPackage pkg)
        {
            try
            {
                int remoteVersion = pkg.ReadInt();
                long remoteCaps = pkg.ReadLong();
                string remoteModVersion = pkg.ReadString();

                if (remoteVersion != ProtocolVersion)
                {
                    NoodlesLog.Warning($"received Hello with protocol version {remoteVersion} != {ProtocolVersion}; " +
                        "treating the server as unnegotiated (vanilla-equivalent) and not replying.");
                    Telemetry.Counters.HandshakeVersionMismatch.Increment();
                    return; // fail closed: never read further fields on a version we have no compat entry for.
                }

                var peer = ZNetHelpers.GetPeer(ZNet.instance, rpc);
                if (peer == null) return;

                var state = PeerState.GetOrCreate(peer);
                if (state.Negotiated) return; // idempotent — don't re-ack a Hello we've already handled this connection.

                state.RemoteProtocolVersion = remoteVersion;
                state.RemoteCapabilities = remoteCaps;
                state.RemoteModVersion = SanitizeRemoteModVersion(remoteModVersion);
                state.Negotiated = true;
                Telemetry.Counters.HandshakeNegotiated.Increment();
                DeploymentLog.LogClientServerNegotiated(state);

                SendHandshakePayload(rpc, HelloAckRpcName);
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(RPC_NoodlesHello), e);
            }
        }

        /// <summary>Server-side handler for the client's ack. Completes negotiation from the server's point of view.</summary>
        private static void RPC_NoodlesHelloAck(ZRpc rpc, ZPackage pkg)
        {
            try
            {
                int remoteVersion = pkg.ReadInt();
                long remoteCaps = pkg.ReadLong();
                string remoteModVersion = pkg.ReadString();

                var peer = ZNetHelpers.GetPeer(ZNet.instance, rpc);
                if (peer == null) return;
                var state = PeerState.GetOrCreate(peer);
                if (state.Negotiated) return; // idempotent — matches RPC_NoodlesHello's own guard; a stray resend never double-logs.

                if (remoteVersion != ProtocolVersion)
                {
                    NoodlesLog.Warning($"peer uid={peer.m_uid} HelloAck protocol version {remoteVersion} != " +
                        $"{ProtocolVersion}; leaving unnegotiated (vanilla-equivalent).");
                    Telemetry.Counters.HandshakeVersionMismatch.Increment();
                    return;
                }

                state.RemoteProtocolVersion = remoteVersion;
                state.RemoteCapabilities = remoteCaps;
                state.RemoteModVersion = SanitizeRemoteModVersion(remoteModVersion);
                state.Negotiated = true;
                Telemetry.Counters.HandshakeNegotiated.Increment();
                DeploymentLog.LogServerPeerNegotiated(peer, state);
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(RPC_NoodlesHelloAck), e);
            }
        }

        /// <summary>
        /// Hook 2 (doc 02) — negotiation kickoff, reshaped for Lead decision 3. Separate patch class from the
        /// re-entry guard below so either can be flagged off independently (doc 02's own stated design), and
        /// this patch's own idempotency (<c>PeerState.HelloSent</c>) is self-contained rather than borrowed
        /// from the guard — Harmony runs every postfix regardless of what a sibling prefix returned, so a
        /// rejected re-entrant call still reaches here and must no-op on its own terms.
        /// </summary>
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_PeerInfo))]
        internal static class Patch_RPC_PeerInfo_Handshake
        {
            static void Postfix(ZNet __instance, ZRpc rpc, ZPackage pkg)
            {
                if (!Features.Negotiation.Active) return;
                try
                {
                    var peer = ZNetHelpers.GetPeer(__instance, rpc);
                    if (peer == null || !peer.IsReady()) return; // version-mismatch/rejected path — nothing to negotiate yet.

                    var state = PeerState.GetOrCreate(peer);

                    if (__instance.IsServer())
                    {
                        if (state.HelloSent) return; // idempotent against a resend RPC_PeerInfo (see G1-L1 below).
                        rpc.Register<ZPackage>(HelloAckRpcName, RPC_NoodlesHelloAck);
                        SendHandshakePayload(rpc, HelloRpcName);
                        state.HelloSent = true;
                        state.HelloSentAtRealtime = Time.realtimeSinceStartup;
                        state.NegotiationWindowStarted = true;
                        state.NegotiationWindowStartedAtRealtime = state.HelloSentAtRealtime;
                        Telemetry.Counters.HandshakeSent.Increment();
                    }
                    else
                    {
                        // Client: register-only. Sends nothing — see class remarks and Lead decision 3. Still
                        // starts THIS side's own negotiation window (RUNTIME REVIEW FIX, major, 2026-09-28) —
                        // previously nothing ever did, so a client connected to a vanilla/unresponsive server had
                        // no timeout path at all; see PeerState.CheckTimeouts.
                        rpc.Register<ZPackage>(HelloRpcName, RPC_NoodlesHello);
                        if (!state.NegotiationWindowStarted)
                        {
                            state.NegotiationWindowStarted = true;
                            state.NegotiationWindowStartedAtRealtime = Time.realtimeSinceStartup;
                        }

                        // 1.0.2: the server's Hello can overtake its own PeerInfo (see
                        // Patch_ZNet_OnNewConnection_RegisterEarlyHello); handle it now that PeerInfo is done.
                        if (s_earlyHello.TryGetValue(rpc, out ZPackage early))
                        {
                            s_earlyHello.Remove(rpc);
                            RPC_NoodlesHello(rpc, early);
                        }
                    }
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_RPC_PeerInfo_Handshake), e);
                }
            }
        }

        /// <summary>
        /// 1.0.2: registers the client's early Hello handler the moment the connection opens (where vanilla
        /// registers PeerInfo itself). ServerSync, embedded in dozens of mods on a typical modded server, swaps the
        /// joining peer's socket during the server's RPC_PeerInfo for one that holds back only PeerInfo, RoutedRPC
        /// and ZDOData until its configs have gone out; every other RPC, our Hello included, is sent at once. So
        /// the Hello reached the client BEFORE the server's PeerInfo, and <c>ZRpc.HandlePackage</c> silently drops
        /// a hash with no handler: live on Proxima 2026-09-29 the server logged handshake_sent=1 and both sides
        /// timed out as vanilla. Registering sends nothing, so Lead decision 3 still holds.
        /// </summary>
        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        internal static class Patch_ZNet_OnNewConnection_RegisterEarlyHello
        {
            static void Postfix(ZNet __instance, ZNetPeer peer)
            {
                if (!Features.Negotiation.Active) return;
                try
                {
                    if (__instance == null || __instance.IsServer() || peer?.m_rpc == null) return; // client side only
                    s_earlyHello.Clear();
                    peer.m_rpc.Register<ZPackage>(HelloRpcName, RPC_NoodlesHelloEarly);
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_ZNet_OnNewConnection_RegisterEarlyHello), e);
                }
            }
        }

        /// <summary>
        /// Hook 1 (doc 02, G1-L1) — reject a re-entrant <c>RPC_PeerInfo</c> from a peer that's already ready.
        /// Server-side only (client build's <c>IsServer()</c> is hardcoded false, so this is a guaranteed
        /// no-op there — Side.cs remarks). <c>[HarmonyPriority(Priority.First)]</c> per doc 02's "Same-method
        /// conflicts" #1: not strictly required (every other patch on this method carries its own independent
        /// idempotency guard), but keeps this the first prefix Harmony evaluates among the ~34 other mods'
        /// own patches on this exact method (see `research/13-mod-compat-census.md`) — belt, not suspenders.
        /// </summary>
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_PeerInfo))]
        [HarmonyPriority(Priority.First)]
        internal static class Patch_RPC_PeerInfo_Reentry
        {
            static bool Prefix(ZNet __instance, ZRpc rpc, ZPackage pkg)
            {
                if (!Features.HandshakeReentryGuard.Active) return true;
                if (!__instance.IsServer()) return true;

                try
                {
                    var peer = ZNetHelpers.GetPeer(__instance, rpc);
                    if (peer != null && peer.IsReady())
                    {
                        Telemetry.Counters.HandshakeRejectedReentry.Increment();
                        NoodlesLog.Warning(NoodlesLogLevel.Normal, $"rejected re-entrant PeerInfo from already-ready peer uid={peer.m_uid}");
                        return false; // skip vanilla body entirely — connection stays exactly as it was.
                    }
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_RPC_PeerInfo_Reentry), e);
                }
                return true;
            }
        }

        [NoodlesModuleInit(10)]
        internal static void Init()
        {
            PatchRegistry.Register("HandshakeReentryGuard", typeof(Patch_RPC_PeerInfo_Reentry), PatchSide.Both);
            PatchRegistry.Register("Negotiation", typeof(Patch_RPC_PeerInfo_Handshake), PatchSide.Both);
            PatchRegistry.Register("Negotiation", typeof(Patch_ZNet_OnNewConnection_RegisterEarlyHello), PatchSide.ClientOnly);
        }
    }
}
