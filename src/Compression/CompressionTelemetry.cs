using System;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.Compression
{
    /// <summary>
    /// Telemetry names for D1/D2 (docs/design/06 "Telemetry" section, plus the ABSOLUTE RULES instruction to
    /// count compress/decompress CPU time and bytes saved PER PEER, not just in aggregate). See
    /// docs/impl/COMPRESSION-IMPL.md for the full catalog and how to read each one.
    ///
    /// <see cref="Core.PerPeerCounter"/> only supports Increment/Max/Set (no cumulative Add — confirmed
    /// against Telemetry.cs) — so the exact cumulative "bytes saved" proof lives on the GLOBAL counters
    /// (<c>Counter.Add</c> does exist there), and the per-peer breakdown ABSOLUTE RULES asks for is reported
    /// as, per peer: a running COUNT of batches/exceptions (Increment), the PEAK single-event cost (Max — the
    /// most actionable "which peer is costing me the most CPU" signal), and the MOST RECENT batch's raw/
    /// compressed size (Set). Together with the global cumulative totals this gives a complete, honest picture
    /// within what Core's actual telemetry primitives support, without inventing a second, unintegrated
    /// bookkeeping structure that <c>Telemetry.FlushAndLog</c> would never pick up.
    /// </summary>
    internal static class CompressionTelemetry
    {
        public static void BatchSent(ZNetPeer peer, int rawBytes, int compressedBytes, double compressMs)
        {
            Telemetry.GetCounter("compression.batchesSent").Increment();
            Telemetry.GetCounter("compression.bytesRawTotal").Add(rawBytes);
            Telemetry.GetCounter("compression.bytesCompressedTotal").Add(compressedBytes);
            Telemetry.SetGauge("compression.sendCpuMsPerBatch", compressMs); // global last-batch reading (D2's own name)

            Telemetry.GetPerPeerCounter("compression.batchesSent").Increment(peer);
            Telemetry.GetPerPeerCounter("compression.bytesRawLast").Set(peer, rawBytes);
            Telemetry.GetPerPeerCounter("compression.bytesCompressedLast").Set(peer, compressedBytes);
            Telemetry.GetPerPeerCounter("compression.sendCpuMsPeak").Max(peer, (long)Math.Ceiling(compressMs));
        }

        public static void OversizedEnvelopeSkipped(int compressedBytes)
        {
            Telemetry.GetCounter("compression.oversizedEnvelopeSkipped").Increment();
            Telemetry.SetGauge("compression.lastOversizedEnvelopeBytes", compressedBytes);
        }

        // Callers also report through NoodlesLog.PatchException separately (CORE-API.md "Exceptions inside a
        // patch" — the one call site that logs, feeds the generic PatchExceptions counter, AND feeds
        // AutoStandDown's exception-burst trigger). These are the specific, additionally-named counters
        // DESIGN.md's "Telemetry and the zero-data-loss proof" section calls out by name.
        public static void SendException() => Telemetry.GetCounter("compression.sendException").Increment();

        /// <summary>
        /// Receive-side aggregate-per-tick sum (docs/design/06 RED-TEAM FIX RT2 major #3: receive-side is
        /// unthrottled — every confirmed peer with pending data decompresses in the SAME tick, unlike the
        /// round-robined send side — so a per-batch average would hide the real worst-case single-frame cost).
        /// Detected reactively from <see cref="UnityEngine.Time.frameCount"/> transitions between successive
        /// calls (all from the main thread, inside the same per-tick RPC drain) rather than a separate timer —
        /// see CompressedRpc's own remarks for why no Core-level periodic hook is needed for this.
        /// </summary>
        private static int s_recvTickFrame = -1;
        private static double s_recvTickAccumMs;

        public static void RecvDecompress(ZNetPeer peer, double decompressMs)
        {
            int frame = Time.frameCount;
            if (frame != s_recvTickFrame)
            {
                if (s_recvTickFrame != -1) Telemetry.SetGauge("compression.recvCpuMsPerTick", s_recvTickAccumMs);
                s_recvTickFrame = frame;
                s_recvTickAccumMs = 0;
            }
            s_recvTickAccumMs += decompressMs;

            Telemetry.GetCounter("compression.recvCpuMsTotal").Add((long)Math.Ceiling(decompressMs));
            Telemetry.GetPerPeerCounter("compression.recvCpuMsPeak").Max(peer, (long)Math.Ceiling(decompressMs));
        }

        public static void DecompressOrParseException() => Telemetry.GetCounter("compression.decompressOrParseExceptions").Increment();

        public static void AckSent(ZNetPeer peer) => Telemetry.GetCounter("compression.acksSent").Increment();
        public static void NackSent(ZNetPeer peer) => Telemetry.GetCounter("compression.nacksSent").Increment();
        public static void AckReceived(ZNetPeer peer) => Telemetry.GetCounter("compression.acksReceived").Increment();
        public static void NackReceived(ZNetPeer peer) => Telemetry.GetCounter("compression.nacksReceived").Increment();

        public static void AckTimeout(ZNetPeer peer)
        {
            Telemetry.GetCounter("compression.ackTimeouts").Increment();
            Telemetry.GetPerPeerCounter("compression.ackTimeouts").Increment(peer);
        }

        public static void InFlightGauge(ZNetPeer peer, int count) =>
            Telemetry.GetPerPeerCounter("compression.inFlightUnacked").Set(peer, count);

        /// <summary>
        /// 1.0.8 — guard 7's own per-round verdict for a ZDO that IS currently in flight, split in two (see
        /// <see cref="CompressionState.IsInFlightUnacked"/>): this one is the ORIGINAL guard-7 case, an
        /// unchanged duplicate revision held back pending its batch's own ack. Both counters exist so an
        /// operator can see the fix actually engaging live (a healthy connection should show
        /// supersedingSends rising with real movement traffic and heldBackSameRevision staying low-ish,
        /// not the reverse) without needing a packet capture.
        /// </summary>
        public static void HeldBackSameRevision(ZNetPeer peer)
        {
            Telemetry.GetCounter("compression.heldBackSameRevision").Increment();
            Telemetry.GetPerPeerCounter("compression.heldBackSameRevision").Increment(peer);
        }

        /// <summary>1.0.8 — the NEW guard-7 case: a newer revision sent again while an older revision of the same id was still an unacked in-flight batch. See <see cref="CompressionState.IsInFlightUnacked"/>.</summary>
        public static void SupersedingSend(ZNetPeer peer)
        {
            Telemetry.GetCounter("compression.supersedingSends").Increment();
            Telemetry.GetPerPeerCounter("compression.supersedingSends").Increment(peer);
        }
    }
}
