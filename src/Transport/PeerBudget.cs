using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.Transport
{
    /// <summary>
    /// Q3's budget per connection: how many bytes of world data one <c>ZDOMan.SendZDOs</c> call may queue for a
    /// player (vanilla: 10240). M1 first used one fixed 32 KiB for everyone. On a connection still at vanilla's
    /// 150 KiB/s (a slow line, or T9 not raised yet) that keeps ~28 KiB queued in Steam, so everything sent after it
    /// waits ~190 ms instead of vanilla's ~43 ms: the players with the weakest lines got more lag, not less.
    /// <para>
    /// Now the budget follows the rate T9 has proven for the connection: enough to keep Steam's pacer fed until this
    /// player's next SendZDOs call (the measured gap between calls, with 30% slack), never below vanilla's 10240,
    /// never above <c>Q3_InnerBudgetBytes</c>. A line that only manages vanilla's rate keeps vanilla's budget; a line
    /// T9 has raised to 1 MiB/s gets ~64 KiB. Works under any send loop (vanilla, Q2, or Zen_ModLib's replacement),
    /// because the gap is measured rather than assumed.
    /// </para>
    /// <para>
    /// Q13 (QueueWindow.cs) adds up to one round trip of bytes already on the wire on top, because vanilla's queue size
    /// counts sent-but-unacknowledged bytes and would otherwise give a far player less speed than a near one at the
    /// same proven rate. Applies to a modded client's own upload too (lower ceiling), per the user's 2026-09-28 call.
    /// </para>
    /// </summary>
    internal static class PeerBudget
    {
        private const float Slack = 1.3f;
        private const float MinGapSeconds = 0.05f; // vanilla's send tick
        private const float MaxGapSeconds = 0.5f;

        // How long vanilla's own full 10240-byte queue takes to drain at vanilla's 153600 B/s: the longest a small
        // RPC waits behind world data in vanilla. The waiting budget covers at most the longer of this and one
        // measured send gap, so the 1.3 slack never makes an RPC wait longer than one send tick
        // (pretest q13-sim, 2026-09-28: at a 30 fps server, slack on an ~83 ms gap let a far player's RPCs wait 86 ms).
        private const float VanillaWaitSeconds = 10240f / 153600f;

        private sealed class Cadence
        {
            public float LastCall = -1f;
            public float Gap; // smoothed seconds between this peer's SendZDOs calls; 0 = not measured yet
            public int LastBudget;
        }

        private static readonly ConditionalWeakTable<ZNetPeer, Cadence> s_cadence = new ConditionalWeakTable<ZNetPeer, Cadence>();

        /// <summary>The budget for the SendZDOs call in progress (bytes); 0 outside one. Main thread only.</summary>
        internal static int Current;

        private static int For(ZNetPeer peer, bool flush)
        {
            // CLIENT SIDE. After the 17:02 outage a plain client's own upload was pinned to vanilla's 10240
            // unconditionally (docs/impl/INTEGRATION-REVIEW.md "TheWall outage 17:02 and the client mod": a raised
            // client budget let tens of KB of ZDO data sit ahead of another mod's small inventory RPC once the link
            // died). USER DECISION (2026-09-28 ~20:30, "unlock the queue, even a small bump can make all the
            // difference"; measured: the pin capped the user's upload at ~165 KB/s with T9 at 1 MiB/s unused for
            // 50 minutes) replaces that pin with Q13's client branch (QueueWindow.cs): the waiting part follows T9's
            // proven rate exactly as the server's does, capped at half of Q13_ClientCeilingBytes, plus the round-trip
            // allowance, the whole never above Q13_ClientCeilingBytes. What made the incident possible is still
            // closed: T9 no longer raises into a dead link (LinkGuard), a stalled link gets vanilla's 10240 below,
            // and the waiting part (what an RPC queues behind) stays one send tick at the proven rate. A flush (the
            // client's last send at disconnect) keeps vanilla's budget, and so does everything with Q13.Client off.
            bool authoritative = ZNet.instance != null && ZNet.instance.IsServer();
            if (!authoritative && (flush || !QueueWindow.ClientBranchActive || !TransportLeverConfig.T9Active))
                return SharedBudget.VanillaInnerBudgetBytes;

            // WP5 per-feature quarantine wiring: Q3 off must be numerically identical to vanilla, same rule
            // every sibling M1 lever already follows (SharedBudget.InnerBudgetBytes' own `!Q3.Active` check).
            // Also stops this method measuring Cadence / emitting the "Q3: budget changed" Tier-2 line below
            // for a call whose result is never actually read (InnerBudgetBytes ignores PeerBudget.Current
            // whenever Q3 is off) -- previously computed and logged anyway, harmless but misleading.
            if (!SharedBudget.Q3.Active) return SharedBudget.VanillaInnerBudgetBytes;

            // QUEUE GUARD (same doc): a stalled peer's budget is pinned to vanilla's own gate regardless of
            // T9's proven rate for it, so vanilla's own SendZDOs gate behaviour returns and a small critical
            // RPC queued behind us (another mod's inventory save, etc.) is never stuck behind tens of KB of
            // ZDO data while the link cannot even deliver what is already queued.
            if (AdaptiveRate.IsStalled(peer)) return SharedBudget.VanillaInnerBudgetBytes;

            int ceiling = SharedBudget.Clamp(SharedBudget.Q3InnerBudgetBytesConfig.Value);
            if (!authoritative) ceiling = Math.Min(ceiling, QueueWindow.ClientWaitCeilingBytes);
            if (!TransportLeverConfig.T9Active)
            {
                // CLIENT-DLL-REVIEW MINOR #4 (deferred there as out of scope for that pass's client-only
                // review; in scope now that this process can be authoritative under SAA): AdaptiveRate.IsStalled
                // above always reads false when T9 itself is inactive (T9.Tick() never runs, so no peer ever
                // gets a LinkGuardState at all) -- so with T9 off, an authoritative process had NO independent
                // stall check of its own and handed out the full elevated Q3 ceiling to a peer whose queue was
                // already backing up. Reuses the SAME Q4 congestion-floor test DestroyChunking.cs's own
                // AnyConnectedPeerNearCongestion already applies elsewhere (never a second, independently-
                // drifting threshold), rather than duplicating LinkGuard.cs's own three-signal algorithm into a
                // second place -- LinkGuard.cs itself is left untouched (WP5 instruction).
                if (peer.m_socket != null && peer.m_socket.GetSendQueueSize() >= SharedBudget.CongestionFloorBytes)
                    return SharedBudget.VanillaInnerBudgetBytes;
                return ceiling; // T9 off, link not already backing up: every connection has T1's fixed rate
            }

            Cadence c = s_cadence.GetValue(peer, _ => new Cadence());
            if (!flush) // a flush drains in a tight loop; its gaps say nothing about the send tick
            {
                float now = Time.realtimeSinceStartup;
                if (c.LastCall >= 0f)
                {
                    float gap = Mathf.Clamp(now - c.LastCall, MinGapSeconds, MaxGapSeconds);
                    c.Gap = c.Gap <= 0f ? gap : c.Gap * 0.8f + gap * 0.2f;
                }
                c.LastCall = now;
            }

            int rate = AdaptiveRate.CurrentRateBps(peer); // 0 = T9 is not managing this connection (yet, or not Steam)
            int result = SharedBudget.VanillaInnerBudgetBytes;
            if (rate > 0)
            {
                float gapSeconds = c.Gap > 0f ? c.Gap : MinGapSeconds;
                float feedSeconds = Math.Min(gapSeconds * Slack, Math.Max(VanillaWaitSeconds, gapSeconds));
                long budget = (long)(rate * feedSeconds);
                result = (int)Math.Max(SharedBudget.VanillaInnerBudgetBytes, Math.Min(ceiling, budget));
            }
            if (result != c.LastBudget)
            {
                int old = c.LastBudget;
                c.LastBudget = result;
                Telemetry.GetPerPeerCounter("transport_q3_budget_bytes").Set(peer, result);

                // WP5 Tier-2 detail logging ("budget changes... rate-limited"): previously telemetry-only.
                // `old==0` the first time a peer is measured is expected (Cadence starts blank), not a real
                // "change" an operator needs to see as often as later ones — still logged (rate-limited) since
                // it is the useful "here is this peer's very first proven budget" line.
                NoodlesLog.DetailRateLimited($"q3_budget_{peer.m_uid}",
                    $"Q3: budget for peer uid={peer.m_uid} {old}->{result} bytes (T9 rate {rate} B/s).", 5, 60f);
            }
            // Q13 (QueueWindow.cs): plus up to one round trip of bytes already on the wire, so a far player's
            // in-flight data stops shrinking what they can send. Only with a T9 rate (Steam, managed, not stalled).
            return rate > 0 ? QueueWindow.Extend(peer, result, rate, authoritative, flush) : result;
        }

        /// <summary>
        /// WP5 per-feature quarantine wiring (Q3.OnForcedOff, per the safety spec's own explicit call-out: "do
        /// it... for Q3 budget" — one of only two Transport levers named as holding real cross-tick state, the
        /// other being T2's already-wired join-burst). There is no NATIVE state to release here the way T9's
        /// Steam rate overrides need (<see cref="Current"/> is already reset to 0 at the top and bottom of every
        /// single SendZDOs call by the patch below, never left stale between calls) — this resets each
        /// currently-connected peer's own Cadence bookkeeping (gap/rate learning) so a later re-enable starts
        /// clean rather than resuming from a paused-and-possibly-stale measurement, matching AdaptiveRate.
        /// ReleaseAll's own "iterate connected peers, not the table" pattern (ConditionalWeakTable has no
        /// built-in "clear everything" on this project's target framework).
        /// </summary>
        internal static void ReleaseAll()
        {
            Current = 0;
            ZNet znet = ZNet.instance;
            if (znet == null) return;
            foreach (ZNetPeer peer in znet.GetConnectedPeers())
            {
                if (peer != null && s_cadence.TryGetValue(peer, out Cadence c))
                {
                    c.LastCall = -1f;
                    c.Gap = 0f;
                    c.LastBudget = 0;
                }
            }
        }

        /// <summary>Sets <see cref="Current"/> for the duration of each SendZDOs call.</summary>
        [HarmonyPatch(typeof(ZDOMan), "SendZDOs")]
        internal static class Patch_ZDOMan_SendZDOs_PeerBudget
        {
            // First, so any other prefix on SendZDOs that reads SharedBudget (Compression's) sees this call's budget.
            [HarmonyPriority(Priority.First)]
            static void Prefix(ZDOMan.ZDOPeer peer, bool flush)
            {
                Current = 0;
                try
                {
                    ZNetPeer znetPeer = peer?.m_peer;
                    if (znetPeer != null) Current = For(znetPeer, flush);
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_ZDOMan_SendZDOs_PeerBudget), e);
                }
            }

            static Exception Finalizer(Exception __exception)
            {
                Current = 0;
                return __exception;
            }
        }
    }
}
