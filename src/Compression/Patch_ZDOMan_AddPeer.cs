using System;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using NoodlesMcDoodles.Transport;

namespace NoodlesMcDoodles.Compression
{
    /// <summary>
    /// H-D1a (docs/design/06), REPURPOSED per this task's ABSOLUTE RULES: "Negotiation already exists in
    /// Core... Use Capabilities.ZdoCompression (advertise it) and PeerState.HasCapability instead of the
    /// blueprint's separate capability RPC / AddPeer postfix unless you find a hard need."
    ///
    /// DEVIATION FROM THE BLUEPRINT (explained, per that same rule): the blueprint's H-D1a sent its own
    /// "NMD_CAP" Hello/Ack/Downgrade round-trip to answer "does this peer support compression" — that question
    /// is now answered by Core's own Hello/HelloAck handshake (`PeerState.HasCapability(Capabilities
    /// .ZdoCompression)`), which already exists, already respects Lead decision 3 (a client never sends before
    /// the server's Hello), and already gets exercised by every other capability-gated feature. Building a
    /// second, parallel capability round-trip here would duplicate that machinery for no benefit.
    ///
    /// What THIS hook still does, and why it's still needed even with Core's negotiation covering capability:
    /// register the two RPC handler NAMES this feature actually sends payloads over — "NMD_ZDOC" (the
    /// compressed batch) and "NMD_ZDOC_ACK" (per-batch application-level ack/nack, H-D1c/d's own concern, which
    /// Core's one-time Hello/HelloAck cannot answer since it never carries a per-batch sequence number).
    /// Registration is unconditional (not gated on Features.ZdoCompression.Active) and always cheap/harmless
    /// even if never used — <c>ZRpc.HandlePackage</c> silently no-ops on an unrecognized hash
    /// (SERVER:83952-83971, confirmed), and a peer that never sends either name is completely unaffected.
    /// Registering regardless of the local flag also means a peer whose flag flips off mid-session can still
    /// safely RECEIVE (decompress/apply/ack) an already-in-flight compressed batch from a partner who hasn't
    /// caught up yet, rather than silently dropping it — see CompressedRpc's own remarks.
    ///
    /// RETARGETED FROM RPC_PeerInfo TO ZDOMan.AddPeer (kept from the blueprint's own RT2 major #1 fix, and
    /// matches DESIGN.md's Hook table row for D1/H-D1a): a Harmony Postfix on <c>RPC_PeerInfo</c> fires on
    /// every return path, including ~6 rejection branches before a real connection succeeds (SERVER:79827,
    /// 79869, 79881, 79897, 79921, 79936); <c>AddPeer</c> (SERVER:76814-76820/CLIENT:76956-76962) is reached
    /// only from RPC_PeerInfo's single success path (SERVER:79989/CLIENT:80182), so this only ever registers
    /// for a connection that actually joined.
    /// </summary>
    [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.AddPeer))]
    internal static class Patch_ZDOMan_AddPeer
    {
        static void Postfix(ZNetPeer netPeer)
        {
            try
            {
                if (netPeer?.m_rpc == null) return;
                // Guard 4 (docs/design/06): ZPlayFabSocket already compresses every byte once VersionMatch()
                // fires (SERVER:91578-91580) — never even offer a second, redundant compression pass on it.
                if (SteamSockets.IsPlayFab(netPeer.m_socket)) return; // 1.0.3: looks through ServerSync wrappers

                // Long, namespaced names (G1-N1 guard, matches this project's existing Hello/HelloAck/ConfigSync
                // RPCs): ZRpc.Register<T> does a silent Remove-then-Add on a 32-bit hash collision
                // (SERVER:83981-83986, confirmed) — never an exception, so a short/common name risks a silent
                // last-write-wins overwrite against another mod's RPC.
                netPeer.m_rpc.Register<ZPackage>(CompressionModule.ZdocRpcName, CompressedRpc.RPC_ZDODataCompressed);
                netPeer.m_rpc.Register<ZPackage>(CompressionModule.AckRpcName, CompressedRpc.RPC_ZDODataAck);
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(Patch_ZDOMan_AddPeer), e);
            }
        }
    }
}
