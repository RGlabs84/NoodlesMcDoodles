using System;

namespace NoodlesMcDoodles.Transport
{
    /// <summary>
    /// Per-connection state <see cref="LinkGuard.Evaluate"/> needs between ticks. Plain data, no game types,
    /// no allocation (a value type, stored inline as a field of <see cref="RateState"/> in AdaptiveRate.cs).
    /// </summary>
    internal struct LinkGuardState
    {
        /// <summary>True while this connection is being held at the floor/vanilla budget.</summary>
        public bool Stalled;
        /// <summary>Seconds accumulated with no inbound data (in bytes/s &lt;= 0); reset the instant inbound resumes.</summary>
        public float NoInboundSeconds;
        /// <summary>Seconds accumulated with none of the three stall conditions true; reset the instant one fires.</summary>
        public float HealthySeconds;
        /// <summary>The send queue size (bytes) observed last tick; <see cref="HasLastQueue"/> guards the first call.</summary>
        public long LastQueueBytes;
        public bool HasLastQueue;
        /// <summary>The largest queue size seen so far during the CURRENT stall episode; 0 when not stalled.</summary>
        public long PeakQueueWhileStalled;
    }

    /// <summary>What the guard wants this tick — either "nothing to do, T9/Q3 run as normal" (<see cref="Stalled"/>
    /// false) or "pin the rate at the floor and the budget at vanilla" (true).</summary>
    internal struct LinkGuardDecision
    {
        public bool Stalled;
        /// <summary>True only on the tick this connection FIRST became stalled (edge, for logging/telemetry).</summary>
        public bool JustStalled;
        /// <summary>True only on the tick it stopped being stalled (edge, for logging/telemetry).</summary>
        public bool JustRecovered;
        /// <summary>Only meaningful when <see cref="Stalled"/> is true: the rate the caller must pin to.</summary>
        public int RateCeilingBps;
        /// <summary>Only meaningful when <see cref="Stalled"/> is true: the SendZDOs budget the caller must pin to.</summary>
        public int QueueBudgetCapBytes;
    }

    /// <summary>
    /// The link-health guard behind T9 (rate) and Q3 (queue budget) — docs/impl/INTEGRATION-REVIEW.md "TheWall
    /// outage 17:02 and the client mod". A connection that has stopped delivering data must never be RAISED
    /// further just because a backlog is building up behind it: that backlog is the symptom, not evidence the
    /// line can take more. Three independent stall signals (any one is enough), each picked because the real
    /// incident's own probe CSV proves it never fires by accident during 7 minutes of healthy 84-115 KB/s
    /// upload, and at least one fires within a second of the real failure:
    ///
    /// (a) <c>qLocal &lt; 0</c> — Steam's own "connection quality, as measured locally" has become unknown.
    ///     Never negative once a connection is a few seconds old and healthy (confirmed against the full
    ///     probe session); goes negative and stays there once the link actually dies.
    /// (b) no inbound data for &gt;= <see cref="NoInboundStallSeconds"/> — the peer has stopped delivering
    ///     anything at all. Slower to trip than (c) in the real incident (Steam's own byte-rate reporting
    ///     lags by a second or so), but a robust, independent backstop for a stall that (c) would miss (e.g.
    ///     nothing new was queued to notice a growth spike in).
    /// (c) the send queue grew by more than <see cref="QueueGrowthStallThresholdBytes"/> bytes in one tick —
    ///     "grew while nothing was acknowledged": nothing legitimately draining ever produces this much
    ///     growth in a single ~1s tick (measured max across the whole healthy session was under 1.4 vanilla
    ///     budgets; every healthy spike drained back down within a tick or two), but the real failure's very
    ///     first bad second queued 4.8 vanilla budgets that never drained again. This is what fires FIRST in
    ///     the real trace (the same second the NIC actually died), before (a) or (b) catch up.
    ///
    /// Recovery is deliberately slower than onset, and deliberately harder than just "no NEW trigger fired":
    /// once any condition fires, <see cref="Stalled"/> stays true until the queue has actually drained back to
    /// at most half of whatever it peaked at during this stall episode, continuously, for
    /// <see cref="RecoverySeconds"/> straight (never a single good sample). Checking against the STALL'S OWN
    /// peak rather than a fixed byte count is scale-free — it works the same whether this connection's normal
    /// budget is vanilla's 10 KB or T9's own elevated tens-of-KB — and it is not redundant with condition (c)
    /// above: a real incident traced against this guard's very first design showed the queue growing only a
    /// few KB per tick for several ticks in the middle of the same stall (each individual tick's growth well
    /// under the (c) threshold, so (c) alone never re-fires), which "no NEW trigger this tick" alone would have
    /// misread as recovered while the backlog was still climbing throughout. Requiring it to fall to half its
    /// own peak catches that: a queue that is still growing, or has only inched down, is never "half its peak."
    ///
    /// Pure and allocation-free: no Steam/Unity/BepInEx types anywhere in this file, so it is exercised
    /// directly — bit-for-bit the same code, not a re-implementation — by the throwaway CSV replay harness
    /// (scratchpad, never shipped) as well as by the real per-second tick in AdaptiveRate.cs.
    /// </summary>
    internal static class LinkGuard
    {
        /// <summary>Nothing received for this long (seconds) counts as stalled even if the queue looks fine.</summary>
        public const float NoInboundStallSeconds = 2f;

        /// <summary>How long the link must look healthy, continuously (not just on one sample), before T9/Q3
        /// resume normal behaviour. Deliberately longer than the fastest onset check above — "a few seconds"
        /// per the design brief — so a single good sample right after a stall can never look like recovery.</summary>
        public const float RecoverySeconds = 3f;

        /// <summary>
        /// A single tick's send-queue growth beyond this is never produced by a connection that is actually
        /// draining. Grounded in two measurements from the same probe session that motivated this guard
        /// (docs/impl/INTEGRATION-REVIEW.md): the largest single-second growth across ~7 minutes of steady,
        /// healthy 84-115 KB/s upload was 13,815 bytes (1.35 vanilla budgets), always followed by a drain
        /// within a tick or two; the 2026-09-28 17:02 stall's very first bad second queued 49,588 bytes (4.84
        /// vanilla budgets) that never drained again. Three vanilla budgets sits with better than 2x margin on
        /// both sides of that gap, so ordinary jitter never trips it but the real failure trips it immediately.
        /// </summary>
        public const int QueueGrowthStallThresholdBytes = 3 * SharedBudget.VanillaInnerBudgetBytes;

        /// <summary>
        /// Advances <paramref name="state"/> by one tick (nominally <paramref name="dt"/> seconds, whatever the
        /// caller's own tick cadence is) and returns this tick's decision. <paramref name="stallFloorBps"/> is
        /// the rate to pin to while stalled — the caller picks it (vanilla's own rate, or lower per its own
        /// config, "RATE GUARD... drop at once to the floor... or lower if the connection's configured vanilla
        /// value is lower") so this file never needs to know about Steam/config types at all.
        /// </summary>
        public static LinkGuardDecision Evaluate(ref LinkGuardState state, float dt, float qLocal, float inBytesPerSec,
            long queueBytes, int stallFloorBps)
        {
            if (dt < 0f) dt = 0f;

            bool qualityUnknown = qLocal < 0f;

            if (inBytesPerSec > 0f) state.NoInboundSeconds = 0f;
            else state.NoInboundSeconds += dt;
            bool noInbound = state.NoInboundSeconds >= NoInboundStallSeconds;

            bool queueRunaway = state.HasLastQueue && (queueBytes - state.LastQueueBytes) > QueueGrowthStallThresholdBytes;
            state.LastQueueBytes = queueBytes;
            state.HasLastQueue = true;

            bool stalledNow = qualityUnknown || noInbound || queueRunaway;
            bool wasStalled = state.Stalled;

            if (stalledNow)
            {
                // Fresh episode: start the peak from here. Still stalled from before: extend it (the queue may
                // keep climbing tick to tick even once nothing NEW triggers, see the recovery remarks above).
                state.PeakQueueWhileStalled = wasStalled ? Math.Max(state.PeakQueueWhileStalled, queueBytes) : queueBytes;
                state.HealthySeconds = 0f;
                state.Stalled = true;
            }
            else
            {
                if (wasStalled) state.PeakQueueWhileStalled = Math.Max(state.PeakQueueWhileStalled, queueBytes);

                // Draining, not just "quiet this tick": the backlog left behind by a stall must have actually
                // fallen to at most half of whatever it peaked at before counting toward recovery (see remarks).
                bool drainingBackToNormal = state.PeakQueueWhileStalled == 0 || queueBytes <= state.PeakQueueWhileStalled / 2;
                if (drainingBackToNormal)
                {
                    state.HealthySeconds += dt;
                    if (state.Stalled && state.HealthySeconds >= RecoverySeconds)
                    {
                        state.Stalled = false;
                        state.PeakQueueWhileStalled = 0;
                    }
                }
                else
                {
                    state.HealthySeconds = 0f;
                }
            }

            LinkGuardDecision d = default;
            d.Stalled = state.Stalled;
            d.JustStalled = d.Stalled && !wasStalled;
            d.JustRecovered = !d.Stalled && wasStalled;
            d.RateCeilingBps = stallFloorBps;
            d.QueueBudgetCapBytes = SharedBudget.VanillaInnerBudgetBytes;
            return d;
        }
    }
}
