using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using NoodlesMcDoodles.Transport;

namespace NoodlesMcDoodles.Compression
{
    /// <summary>
    /// H-D1b (docs/design/06) — send-side. Full-replace prefix on <c>ZDOMan.SendZDOs</c>
    /// (SERVER:77055-77120/CLIENT:77197-77259, confirmed byte-identical this pass) for a compression-confirmed,
    /// non-PlayFab peer; falls through to vanilla (<c>return true</c>) for everyone else, including whenever
    /// <see cref="CompressionModule.EffectiveActive"/> is false — matching the ABSOLUTE RULE "Flag off or kill
    /// switch = exactly vanilla ZDOData with no restart".
    ///
    /// Builds the EXACT SAME wire content vanilla's own loop would (same per-item framing, same
    /// <c>ZDO.Serialize</c> calls, same <c>CreateSyncList</c>/<c>m_forceSend</c>/<c>m_clientChangeQueue</c>
    /// bookkeeping — see body), then compresses the finished package and sends it over a new RPC instead of
    /// "ZDOData". The one deliberate behavioural difference from vanilla: the <c>peer.m_zdos[...]</c> "this peer
    /// now knows this revision" write is DEFERRED until an application-level ack (guard 3,
    /// <see cref="CompressionState.CommitInFlight"/>) instead of happening optimistically at send time
    /// (SERVER:77111) — mandatory per the ABSOLUTE RULE that a ZDO is marked sent only after ack.
    ///
    /// 1.0.8: <c>s_scratchSyncList</c> (built by the unmodified vanilla <c>CreateSyncList</c> call below) can
    /// now legitimately contain a ZDO that ALREADY has an older, still-unacked batch in flight for this same
    /// peer — <see cref="Patch_ZDOPeer_ShouldSend"/> only keeps such a ZDO suppressed while its live revision
    /// still matches that older batch exactly; once it's moved on, vanilla's own answer (true) is left standing
    /// and it's selected again here, same as any other candidate. This method itself needed no change for
    /// that: <see cref="CompressionState.TrackInFlight"/>/<c>InFlightTracker.Track</c> already re-point the id
    /// at THIS new batch (last-write-wins), and the older batch is left alone to resolve on its own later —
    /// see that class's own remarks for why both can coexist safely.
    /// </summary>
    [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.SendZDOs))]
    internal static class Patch_ZDOMan_SendZDOs
    {
        // Reused scratch buffers, main-thread-only (SendZDOs' whole call chain is), matching Transport's own
        // Q12Guard/InvalidSectorGuard pattern of one reusable instance rather than a per-call allocation.
        private static readonly List<ZDO> s_scratchSyncList = new List<ZDO>();
        private static readonly ZPackage s_scratchItemPackage = new ZPackage();

        static bool Prefix(ZDOMan __instance, ZDOMan.ZDOPeer peer, bool flush, ref bool __result)
        {
            if (!CompressionModule.EffectiveActive)
            {
                NoteNotCompressing(peer?.m_peer, "compression is not active on this side ([2 - Network] CompressWorldSync, kill switch or forced off)");
                return true;
            }
            if (peer?.m_peer?.m_rpc == null) return true;
            if (SteamSockets.IsPlayFab(peer.m_peer.m_socket)) return true; // Guard 4 — see Patch_ZDOMan_AddPeer remarks (1.0.3: looks through ServerSync wrappers)
            if (!IsConfirmedForSend(peer.m_peer))
            {
                // 1.0.9 (compression follow-up from the 1.0.8 review): this early return used to skip the
                // SweepExpired call below entirely (that call is only reached once a peer IS confirmed) — a
                // peer that downgrades mid-connection (repeated nacks or a schema mismatch,
                // CompressionState.IsDowngraded) or drops out of negotiation lands HERE on every subsequent
                // round-robin turn instead, so any batch already in flight for it at the moment of downgrade
                // never got swept and just sat in the tracker/gauge until disconnect. Bookkeeping only — guard 7
                // always lets vanilla's own ShouldSend reselect regardless, so nothing was ever lost — but now
                // it clears within one EffectiveAckTimeoutSeconds of the downgrade, same as every other peer's
                // ordinary per-round sweep, instead of lingering for the rest of the connection.
                try { CompressionState.SweepExpired(peer.m_peer); }
                catch (Exception) { /* opportunistic bookkeeping only; never let it affect the send */ }
                NoteNotCompressing(peer.m_peer, WhyNotConfirmed(peer.m_peer));
                return true;
            }

            try
            {
                // Cheap, opportunistic: sweep this peer's own expired in-flight batches before deciding whether
                // there's anything new to build (CompressionState's own remarks on why this needs no timer).
                CompressionState.SweepExpired(peer.m_peer);

                int sendQueueSize = peer.m_peer.m_socket.GetSendQueueSize();
                // SAME live Q3/Q5 value Transport already computes for the SendZDOs call in progress — as of
                // Transport's PeerBudget.cs, this is now T9-rate-derived and per-peer, set by a Priority.First
                // void prefix on this same method (PeerBudget.Patch_ZDOMan_SendZDOs_PeerBudget) that always
                // runs before this one (ours is default/Normal priority — never claim Priority.First or higher
                // here, or this reads stale/zero and silently falls back to vanilla's flat 10240). Read once,
                // like vanilla's own `num`, never cached across calls or peers.
                int outerGate = SharedBudget.InnerBudgetBytes;
                if (!flush && sendQueueSize > outerGate) { __result = false; return false; }
                int budget = outerGate - sendQueueSize;
                if (budget < SharedBudget.CongestionFloorBytes) { __result = false; return false; }

                s_scratchSyncList.Clear();
                __instance.CreateSyncList(peer, s_scratchSyncList); // unchanged vanilla call (now public post-publicize);
                                                                      // includes Q6's own sort and force-send merge already
                if (s_scratchSyncList.Count == 0 && peer.m_invalidSector.Count == 0) { __result = false; return false; }

                var zPackage = new ZPackage();

                // RED-TEAM FIX RT1 blocker #2 (docs/design/06): reuse Transport's own SharedBudget-driven
                // chunking helper for m_invalidSector rather than vanilla's unconditional unbounded write —
                // this prefix skips the transpiled vanilla body outright (Harmony Prefix `return false`), so
                // Hook 6's G2-N1 guard, which lives only inside that transpiled body, would otherwise never run
                // for a single compression-confirmed peer, no matter which patch ships first.
                bool wroteInvalidSector = InvalidSectorGuard.WriteChunk(peer, zPackage);

                float gameTimeAtSend = UnityEngine.Time.time;
                var sentItems = new List<InFlightItem>();

                foreach (ZDO item in s_scratchSyncList)
                {
                    // RED-TEAM FIX RT1 blocker #2: same reused helper Q3/Q4/Q5/Q12/G2-N1's own transpiler calls
                    // — proceed / stop (budget exhausted) / skip-this-one (too large to ever fit).
                    int decision = Q12Guard.Evaluate(zPackage, budget, item);
                    if (decision == Q12Guard.Break) break;
                    if (decision == Q12Guard.SkipItem) continue;

                    // LEAD FIX (data-loss review, confirmed): do NOT remove from peer.m_forceSend /
                    // m_clientChangeQueue here, unlike vanilla's own unconditional per-item removal
                    // (SERVER:77101/CLIENT:77243). Vanilla's removal and its RPC invoke are one un-throwable
                    // stretch, so there's nothing to reconcile if it fails partway. Ours is not: if anything
                    // throws between here and the RPC invoke actually completing (compression, the envelope
                    // build, the invoke itself), Guard 8's catch fails OPEN and lets vanilla's own SendZDOs body
                    // run for this SAME call right afterward — and a ZDO reachable ONLY via force-send (a
                    // portal relink outside the peer's normal FindSectorObjects range, ForceSendZDO(ZDOID),
                    // SERVER ~100520/100535) would then never be re-offered by that fallback run either, since
                    // AddForceSendZdos reads m_forceSend and it would already be empty for that id — permanently
                    // orphaning it. Deferred to CompressionState.CommitInFlight instead, applied only once the
                    // peer's ack proves this exact batch was actually decoded and applied — same release point
                    // as the m_zdos write below, same reasoning. A nack or ack-timeout therefore leaves both
                    // sets untouched, so a force-sent/client-owned ZDO in a failed batch is automatically still
                    // force-sent/still-queued next round — nothing to explicitly restore.
                    zPackage.Write(item.m_uid);
                    zPackage.Write(item.OwnerRevision);
                    zPackage.Write(item.DataRevision);
                    zPackage.Write(item.GetOwner());
                    zPackage.Write(item.GetPosition());
                    s_scratchItemPackage.Clear();
                    item.Serialize(s_scratchItemPackage);
                    zPackage.Write(s_scratchItemPackage);

                    // DEVIATION FROM VANILLA (mandatory, guard 3): do NOT write peer.m_zdos here — deferred to
                    // CompressionState.CommitInFlight, released only once this batch is acked.
                    sentItems.Add(new InFlightItem(item.m_uid, item.DataRevision, item.OwnerRevision, gameTimeAtSend));
                }
                zPackage.Write(ZDOID.None);

                if (sentItems.Count == 0 && !wroteInvalidSector) { __result = false; return false; }

                var sw = Stopwatch.StartNew();
                byte[] compressed = CompressionCodec.Compress(zPackage.GetArray(), CompressionModule.EffectiveCompressionLevel);
                sw.Stop();

                // RED-TEAM FIX RT1 blocker #2: explicit hard-ceiling check on the COMPRESSED envelope, mirroring
                // Q12's own "skip-and-log, never truncate" guard. LEAD NOTE (reviewer, confirmed): with Q3's own
                // hard clamp on SharedBudget.InnerBudgetBytes now 131072 (SharedBudget.HardCeilingBytes), the
                // ordinary multi-item accumulation path Q12Guard.Evaluate bounds via `budget` can never build an
                // uncompressed package anywhere near 512 KiB in the first place, so THIS check is effectively
                // unreachable through that path today — GZip only shrinks structured ZDO/TerrainComp data, it
                // doesn't multiply it. Kept anyway as cheap, correct defense in depth: (a) Q12Guard's own
                // single-oversized-item path is bounded by AbsoluteSteamCeilingBytes independently of `budget`,
                // not by Q3's 131072 clamp, so one very large item (a heavily edited TerrainComp) can still push
                // the uncompressed size well past 131072 before compression; (b) nothing here assumes Q3's
                // clamp, or GZip's own behavior on adversarial input, can never change.
                int envelopeBytes = 4 + 4 + 4 + compressed.Length; // schema(4) + seq(4) + length-prefix(4) + payload
                if (envelopeBytes + 64 >= SharedBudget.AbsoluteSteamCeilingBytes)
                {
                    CompressionTelemetry.OversizedEnvelopeSkipped(compressed.Length);
                    // WP5 (spec's own note): this should stay ~0 through ordinary play — unlike T9/Q3's own
                    // routine, expected-in-normal-play changes (Tier-2, rate-limited), any occurrence here is
                    // notable enough to be visible in the MAIN log immediately, not buried in detail.log or
                    // throttled. Previously telemetry-only (invisible to an operator watching the main log).
                    NoodlesLog.Warning($"ZdoCompression: peer uid={peer.m_peer.m_uid}'s compressed envelope " +
                        $"({compressed.Length} bytes compressed) would not fit under Steam's reliable-message " +
                        "ceiling even alone; skipping this round only (guard 7 lets ShouldSend naturally " +
                        "reselect these ZDOs — nothing lost, nothing marked in-flight for this round).");
                    __result = false;
                    return false; // skip this round only — nothing in sentItems was marked in-flight yet
                }

                int seq = CompressionState.NextSequence(peer.m_peer);
                CompressionState.TrackInFlight(peer.m_peer, seq, sentItems); // guard 7

                var envelope = new ZPackage();
                envelope.Write(CompressionModule.SchemaVersion);
                envelope.Write(seq);
                envelope.Write(compressed);
                peer.m_peer.m_rpc.Invoke(CompressionModule.ZdocRpcName, envelope); // NEVER also invoke "ZDOData" this round

                CompressionTelemetry.BatchSent(peer.m_peer, zPackage.Size(), compressed.Length, sw.Elapsed.TotalMilliseconds);

                __result = true;
                return false;
            }
            catch (Exception ex)
            {
                // Guard 8 (mandatory — docs/design/06 RT1 blocker #1): SendZDOs is called from
                // SendZDOToPeers2 <- ZDOMan.Update <- ZNet.Update(), immediately before UpdateSave(), with no
                // enclosing try/catch anywhere above this hook (SERVER:76859-76907, 80090-80091, confirmed). An
                // uncaught exception here skips SendZDOToPeers2's own m_nextSendPeer++/wraparound, freezing the
                // round-robin on this peer forever and silently halting world saves and ZDO sync for every
                // connected peer, not just this one, until a manual restart. Fail open to vanilla for this call.
                CompressionTelemetry.SendException();
                NoodlesLog.PatchException(nameof(Patch_ZDOMan_SendZDOs), ex);
                return true;
            }
        }

        /// <summary>
        /// D1's actual negotiation gate: local willingness (<see cref="CompressionModule.EffectiveActive"/>,
        /// checked separately by the caller) AND proof the remote peer's own NoodlesMcDoodles build advertised
        /// the capability during Core's existing Hello/HelloAck exchange. <see cref="PeerState.HasCapability"/>
        /// already folds in <c>Negotiated</c> — a peer that hasn't completed the handshake (vanilla, or still
        /// mid-connect) never has ANY capability bit set, so this alone is sufficient to exclude it.
        /// </summary>
        /// <summary>
        /// 1.0.3 diagnostic: live on Proxima 2026-09-29 a negotiated client compressed its own sends (about 2:1) but
        /// the server sent it zero compressed batches, with no counter saying why. For a negotiated peer only (a
        /// vanilla peer is simply never compressed to), names the failing check once per peer per 5 minutes in
        /// detail.log and counts it; never changes what is sent.
        /// </summary>
        private static void NoteNotCompressing(ZNetPeer netPeer, string why)
        {
            try
            {
                if (netPeer == null || !PeerState.TryGet(netPeer, out var state) || !state.Negotiated) return;
                Telemetry.GetCounter("compression.sendNotCompressed").Increment();
                string who = string.IsNullOrEmpty(netPeer.m_playerName) ? $"peer uid={netPeer.m_uid}" : $"{netPeer.m_playerName} (peer uid={netPeer.m_uid})";
                NoodlesLog.DetailRateLimited($"compression_not_{netPeer.m_uid}",
                    $"compression: not compressing to negotiated {who}: {why}.", 1, 300f);
            }
            catch (Exception)
            {
                // diagnostic only; never let it affect the send
            }
        }

        private static string WhyNotConfirmed(ZNetPeer netPeer)
        {
            if (SteamSockets.IsPlayFab(netPeer.m_socket)) return "PlayFab socket";
            if (CompressionState.IsDowngraded(netPeer)) return "downgraded for this peer (schema mismatch or repeated nacks)";
            if (!PeerState.TryGet(netPeer, out var s)) return "no handshake state for this peer";
            if (!s.Negotiated) return "handshake not complete";
            return $"the peer did not advertise compression (its capabilities: 0x{s.RemoteCapabilities:X})";
        }

        internal static bool IsConfirmedForSend(ZNetPeer netPeer)
        {
            if (netPeer == null) return false;
            if (SteamSockets.IsPlayFab(netPeer.m_socket)) return false; // 1.0.3: a ServerSync wrapper is not PlayFab
            if (CompressionState.IsDowngraded(netPeer)) return false; // schema mismatch / repeated nacks — see CompressionState
            return PeerState.TryGet(netPeer, out var state) && state.HasCapability(Capabilities.ZdoCompression);
        }
    }
}
