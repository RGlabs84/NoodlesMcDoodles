using System;
using System.Diagnostics;
using NoodlesMcDoodles.Core;

namespace NoodlesMcDoodles.Compression
{
    /// <summary>
    /// H-D1d/e (docs/design/06) — the two new RPC handlers, registered by <see cref="Patch_ZDOMan_AddPeer"/>.
    ///
    /// HOW OTHER MODS' RPC_ZDOData PATCHES STILL SEE EVERY UPDATE (ABSOLUTE RULE — must be explained, not just
    /// asserted): <see cref="RPC_ZDODataCompressed"/> does not reimplement <c>ZDOMan.RPC_ZDOData</c>'s
    /// whole-ZDO merge/revision-gate parsing (SERVER:77123-77186/CLIENT:77265-77328, confirmed byte-identical)
    /// — it decompresses back to the exact bytes vanilla's own <c>SendZDOs</c> would have produced, then calls
    /// <c>ZDOMan.instance.RPC_ZDOData(rpc, inner)</c> DIRECTLY, the same public method vanilla's own
    /// <c>"ZDOData"</c> RPC dispatch calls. Harmony patches a method by rewriting the method's own compiled
    /// implementation (a prefix/transpiler/postfix chain installed once, at that method's own identity), not by
    /// rewriting individual call sites — so ANY caller that reaches this method after Harmony has patched it
    /// (a raw call, a delegate, reflection) runs the FULL patched chain, identically to vanilla's own dispatch.
    /// On the live server (research/13-mod-compat-census.md §5) that chain today is: ZenMap's
    /// <c>MapTrack.ZDOMan_RPC_ZDOData</c> transpiler (baked into the method body itself), then AzuAntiCheat's
    /// <c>PlayerActivityObserver/MarkClientData</c> Prefix+Finalizer and Wonderland's
    /// <c>TerrainGuardPatches/Patch_ZDOMan_RPC_ZDOData</c> Prefix+Finalizer wrapping the call. Every one of
    /// those still runs, in the same order Harmony would run them for a plain <c>"ZDOData"</c> message, because
    /// this is the SAME method, not a lookalike. This is also exactly why this project's OWN patches never
    /// touch <c>RPC_ZDOData</c> itself (no prefix/postfix/transpiler is registered on it anywhere in this
    /// module) — doing so would just be one more link in the same already-existing chain, not a bypass of it.
    /// The one timing requirement this relies on: every other mod's own Harmony patches must already be
    /// installed by the time a real compressed batch can arrive — guaranteed by construction, since real RPC
    /// traffic can only flow long after every plugin's own Awake()/patching phase (BepInEx loads and patches
    /// every plugin before any ZNet connection exists).
    ///
    /// RECEIVE SIDE IS DELIBERATELY NOT GATED ON <see cref="CompressionModule.EffectiveActive"/> (deviation
    /// from the blueprint's own sketch, explained): honoring an already-arrived, well-formed compressed batch
    /// — decompress, apply via the real merge logic, ack — is always safe regardless of THIS process's current
    /// willingness to originate NEW compressed sends, and refusing it would be strictly worse: the peer that
    /// sent it would just sit on an unacked batch until its own ack-timeout, then resend the SAME ZDOs via
    /// plain ZDOData anyway (guard 7 self-heals), so gating receipt buys nothing and only adds latency. The one
    /// thing that MUST gate receipt is the GLOBAL kill switch (<see cref="KillSwitch.Enabled"/>) — with
    /// <c>Core.Enabled=false</c> this mod must behave as if it were never installed, full stop, matching every
    /// other hook's own "off = vanilla" contract; by that point this process's own send side is already off
    /// too (EffectiveActive folds in KillSwitch.Enabled) and any of ITS OWN in-flight batches were already
    /// actively flushed (CompressionModule's kill-switch flush callback), so silently no-op'ing an incoming
    /// batch here is consistent with, not fighting against, that flush.
    /// </summary>
    internal static class CompressedRpc
    {
        private enum AckStatus
        {
            Ack = 0,
            NackTransient = 1,      // decompress/parse exception — worth retrying, no persistent signal
            NackSchemaMismatch = 2, // incompatible SchemaVersion — will never succeed without a reconnect/upgrade
        }

        public static void RPC_ZDODataCompressed(ZRpc rpc, ZPackage pkg)
        {
            if (!KillSwitch.Enabled) return; // global kill switch: behave as if this handler were never registered

            ZDOMan.ZDOPeer zdoPeer;
            try
            {
                zdoPeer = ZDOMan.instance?.FindPeer(rpc);
            }
            catch (Exception e) { NoodlesLog.PatchException(nameof(RPC_ZDODataCompressed) + ".FindPeer", e); return; }
            if (zdoPeer?.m_peer == null) return; // unknown host — matches vanilla's own RPC_ZDOData guard (SERVER:77125-77129)

            // RED-TEAM FIX (DATA-LOSS REVIEW, blocker, defense-in-depth): Patch_ZDOMan_AddPeer registers this
            // RPC name for every non-PlayFab peer the instant it joins — before any Hello/HelloAck negotiation
            // with THAT peer can possibly have completed (AddPeer fires from RPC_PeerInfo's own success path;
            // negotiation is a later, separate round trip) — so it can't gate registration itself on this
            // peer's capability without breaking every legitimate sender too. Gating HERE instead, mirroring
            // the send side's own IsConfirmedForSend (Patch_ZDOMan_SendZDOs.cs): by the time any REAL compressed
            // batch can arrive, both sides' negotiation is long done (CompressionModule's own remarks — this
            // mod's send side never invokes NMD_ZDOC before IsConfirmedForSend passes), so a peer that reaches
            // here without having negotiated Capabilities.ZdoCompression is not a compliant, fully-negotiated
            // NoodlesMcDoodles peer — dropping before decompressing removes the pre-negotiation exposure this
            // handler otherwise has to every connected peer, vanilla or modded, confirmed or not. Does not by
            // itself close the decompression-bomb hole (a confirmed peer's own client can still be buggy or
            // compromised) — see CompressionCodec.Decompress's own bounded-output fix for that.
            if (!PeerState.TryGet(zdoPeer.m_peer, out var senderState) || !senderState.HasCapability(Capabilities.ZdoCompression))
            {
                NoodlesLog.Warning($"NMD_ZDOC received from peer uid={zdoPeer.m_peer.m_uid} that never negotiated " +
                    "Capabilities.ZdoCompression; dropping without decompressing.");
                return;
            }

            int seq = -1;
            try
            {
                int version = pkg.ReadInt();
                seq = pkg.ReadInt();
                byte[] compressed = pkg.ReadByteArray();

                if (version != CompressionModule.SchemaVersion)
                {
                    NoodlesLog.Warning($"NMD_ZDOC schema version {version} != {CompressionModule.SchemaVersion} " +
                        $"from peer uid={zdoPeer.m_peer.m_uid}; nacking and permanently downgrading this " +
                        "connection to plain ZDOData (a reconnect after both sides match will retry negotiation).");
                    SendAckOrNack(zdoPeer.m_peer, seq, AckStatus.NackSchemaMismatch);
                    return;
                }

                var sw = Stopwatch.StartNew();
                byte[] raw = CompressionCodec.Decompress(compressed);
                var inner = new ZPackage(raw);

                // See class remarks: a direct call, not a reimplementation — runs the FULL patched
                // RPC_ZDOData chain (ZenMap/AzuAntiCheat/Wonderland and any other installed mod's patches),
                // identically to vanilla's own "ZDOData" dispatch.
                ZDOMan.instance.RPC_ZDOData(rpc, inner);
                sw.Stop();

                CompressionTelemetry.RecvDecompress(zdoPeer.m_peer, sw.Elapsed.TotalMilliseconds);
                SendAckOrNack(zdoPeer.m_peer, seq, AckStatus.Ack);
            }
            catch (Exception ex)
            {
                // Guard 6 (mandatory): wraps the WHOLE handler body (decompress + the RPC_ZDOData call), not
                // just Decompress() — docs/design/06 Data-loss #8: ZRpc.Update's own catch treats
                // EndOfStreamException specially (SERVER:83887-83897), turning an uncaught parse-layer
                // over-read into a process-wide `m_connectionStatus = ConnectionStatus.ErrorVersion` flip
                // (SERVER:80488-80490, a `private static` field — shared by the whole ZNet instance) with the
                // triggering connection left open, still re-throwing every tick, never cleanly disconnected.
                // Catching Exception generically here, around everything, is what prevents that.
                CompressionTelemetry.DecompressOrParseException();
                NoodlesLog.PatchException(nameof(RPC_ZDODataCompressed), ex);
                if (seq != -1) SendAckOrNack(zdoPeer.m_peer, seq, AckStatus.NackTransient);
            }
        }

        public static void RPC_ZDODataAck(ZRpc rpc, ZPackage pkg)
        {
            try
            {
                int seq = pkg.ReadInt();
                var status = (AckStatus)pkg.ReadInt();

                ZDOMan.ZDOPeer zdoPeer = ZDOMan.instance?.FindPeer(rpc);
                if (zdoPeer?.m_peer == null) return;

                if (status == AckStatus.Ack)
                {
                    CompressionTelemetry.AckReceived(zdoPeer.m_peer);
                    CompressionState.CommitInFlight(zdoPeer, seq); // guard 3's release point
                    CompressionState.RecordAckOutcome(zdoPeer.m_peer, success: true, schemaMismatch: false);
                }
                else
                {
                    CompressionTelemetry.NackReceived(zdoPeer.m_peer);
                    CompressionState.DropInFlight(zdoPeer.m_peer, seq); // never marked known — ShouldSend naturally reselects
                    CompressionState.RecordAckOutcome(zdoPeer.m_peer, success: false, schemaMismatch: status == AckStatus.NackSchemaMismatch);
                }
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(RPC_ZDODataAck), e);
            }
        }

        private static void SendAckOrNack(ZNetPeer peer, int seq, AckStatus status)
        {
            try
            {
                var pkg = new ZPackage();
                pkg.Write(seq);
                pkg.Write((int)status);
                peer.m_rpc.Invoke(CompressionModule.AckRpcName, pkg);
                if (status == AckStatus.Ack) CompressionTelemetry.AckSent(peer); else CompressionTelemetry.NackSent(peer);
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(SendAckOrNack), e);
            }
        }
    }
}
