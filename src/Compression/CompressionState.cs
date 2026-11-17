using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.Compression
{
    /// <summary>
    /// The thin, <see cref="ZNetPeer"/>/<see cref="ZDOMan.ZDOPeer"/>-specific glue around
    /// <see cref="InFlightTracker{TKey,TItem}"/>, which is where guard 3 / guard 7's actual algorithm lives
    /// (see that class's own remarks for why it's factored out, and for the ABSOLUTE RULE it exists to
    /// satisfy). This class supplies the real clock (<see cref="UnityEngine.Time.realtimeSinceStartup"/>) and
    /// the real vanilla write (<c>zdoPeer.m_zdos[...] = PeerZDOInfo(...)</c>) that the generic tracker itself
    /// deliberately knows nothing about.
    ///
    /// Keyed by <see cref="ZNetPeer"/> object reference, matching <see cref="PeerState"/>'s own convention
    /// (docs 02: "a reconnect is a new object, so there's nothing stale to carry over") — <c>ZDOMan.AddPeer</c>
    /// allocates a brand-new <c>ZDOPeer</c> (and therefore a brand-new empty <c>m_zdos</c>) on every call
    /// (SERVER:76814-76820, confirmed), so a reconnect mid-flight is moot the instant the old peer object is
    /// torn down (docs/design/06 Data-loss #12) — this class's own disconnect cleanup below mirrors that.
    /// </summary>
    internal static class CompressionState
    {
        private static readonly ConcurrentDictionary<ZNetPeer, PeerCompressionState> s_states =
            new ConcurrentDictionary<ZNetPeer, PeerCompressionState>();

        private static PeerCompressionState GetOrCreate(ZNetPeer peer) =>
            s_states.GetOrAdd(peer, _ => new PeerCompressionState());

        /// <summary>Next per-connection sequence number. Main-thread only (SendZDOs' whole call chain is).</summary>
        internal static int NextSequence(ZNetPeer peer) => GetOrCreate(peer).NextSeq++;

        /// <summary>
        /// Records a just-sent batch as in-flight (guard 7), deferring the vanilla-equivalent
        /// <c>peer.m_zdos</c> write (guard 3) until <see cref="CommitInFlight"/>. <paramref name="items"/> must
        /// carry the EXACT DataRevision/OwnerRevision that were serialized into this batch, not whatever the
        /// live ZDO holds at ack time (which may have already moved on) — matching vanilla's own "mark exactly
        /// what was sent" semantics (SERVER:77111).
        /// </summary>
        internal static void TrackInFlight(ZNetPeer peer, int seq, List<InFlightItem> items)
        {
            var state = GetOrCreate(peer);
            var keys = items.Select(i => i.Id).ToArray();
            state.Tracker.Track(seq, keys, items.ToArray(), Time.realtimeSinceStartup);
            CompressionTelemetry.InFlightGauge(peer, state.Tracker.Count);
        }

        /// <summary>
        /// H-D1c's own question: is this ZDO currently withheld awaiting an ack? See
        /// <see cref="InFlightTracker{TKey,TItem}.IsInFlightUnacked"/> for the lazy-expiry mechanics.
        ///
        /// 1.0.8: takes the live <see cref="ZDO"/> now, not just its id — guard 7's ORIGINAL purpose was only
        /// ever to stop re-offering an UNCHANGED duplicate while its one outstanding batch is still unacked;
        /// it was never meant to hold back a ZDO that changed AGAIN since that batch was built. Before this
        /// version it could not tell the two apart (it only ever asked "is this id in ANY unacked batch"), so a
        /// ZDO that changes every send round (a moving creature/player) could only ever reach the peer once per
        /// ack round-trip (RTT-bound, ~75-300ms) instead of once per send round (~66ms on a 30fps server) — the
        /// stutter this version fixes. Now: if the tracker's newest in-flight item for this id is EXACTLY what
        /// the live ZDO still holds, it's the same old duplicate — stay suppressed (guard 7's real purpose,
        /// unchanged). If the live ZDO has moved past it (either revision field), this is a superseding send:
        /// return false (not suppressed) so <see cref="Patch_ZDOPeer_ShouldSend"/> lets vanilla's own answer
        /// through, and the older, now-superseded batch is left exactly as it was — still tracked, still
        /// resolves normally by its own ack/nack/timeout later (see InFlightTracker's own remarks on why that's
        /// safe) — this call never touches it.
        /// </summary>
        internal static bool IsInFlightUnacked(ZNetPeer peer, ZDO zdo)
        {
            if (!s_states.TryGetValue(peer, out var state)) return false;
            bool inFlight = state.Tracker.IsInFlightUnacked(zdo.m_uid, Time.realtimeSinceStartup,
                CompressionModule.EffectiveAckTimeoutSeconds, out InFlightItem item, out bool justExpired);
            if (justExpired) CompressionTelemetry.AckTimeout(peer);
            if (!inFlight) return false;

            // Mirrors vanilla ShouldSend's own comparison shape (SERVER:76050-76061): OwnerRevision strictly
            // ahead of the in-flight snapshot always supersedes, regardless of DataRevision; otherwise it comes
            // down to DataRevision alone. A live ZDO's own revisions can never be BEHIND a past snapshot of
            // itself, so "not ahead on either field" and "unchanged" are the same statement here.
            bool supersedes = IsSupersedingRevision(zdo.OwnerRevision, item.OwnerRevision, zdo.DataRevision, item.DataRevision);
            if (supersedes)
            {
                CompressionTelemetry.SupersedingSend(peer);
                return false;
            }
            CompressionTelemetry.HeldBackSameRevision(peer);
            return true;
        }

        /// <summary>
        /// 1.0.9 (compression follow-up from the 1.0.8 review): the two-field supersede comparison above,
        /// extracted into a pure function of four plain numbers — no <c>ZDO</c>/Unity dependency — so
        /// <c>tools/zip-sim</c> can load and call it directly by reflection against the real built DLL (like
        /// <c>CompressionCodec.Compress</c>/<c>InFlightTracker</c> already do) instead of only being exercised
        /// indirectly through <see cref="IsInFlightUnacked"/>'s own live-ZDO-dependent scenarios. Behaviour is
        /// unchanged — this is a pure extraction, not a logic change.
        /// </summary>
        internal static bool IsSupersedingRevision(ushort liveOwnerRevision, ushort snapshotOwnerRevision,
            uint liveDataRevision, uint snapshotDataRevision) =>
            liveOwnerRevision > snapshotOwnerRevision || liveDataRevision > snapshotDataRevision;

        /// <summary>
        /// Every peer's own send round (H-D1b runs periodically per that peer's round-robin turn) sweeps ITS
        /// batches for expiry too, not just whichever ZDOs happen to be re-queried via ShouldSend — closes the
        /// residual gap where an object leaves relevance (destroyed, out of range) right as its only in-flight
        /// batch's ack is lost, so its bookkeeping isn't left dangling until disconnect. Cheap: bounded by how
        /// many batches are genuinely in flight for one peer (small; guard 7 exists precisely to keep this
        /// small under real RTT). No separate timer needed (CORE-API.md: "don't start your own timer" — this
        /// piggybacks on a hot path this module already owns instead).
        /// </summary>
        internal static void SweepExpired(ZNetPeer peer)
        {
            if (!s_states.TryGetValue(peer, out var state)) return;
            var expired = state.Tracker.SweepExpired(Time.realtimeSinceStartup, CompressionModule.EffectiveAckTimeoutSeconds);
            for (int i = 0; i < expired.Count; i++) CompressionTelemetry.AckTimeout(peer);
        }

        /// <summary>
        /// Ack received: apply every vanilla-equivalent write this design defers until delivery is actually
        /// proven, for every item in this batch, using the values captured AT SEND TIME (not re-read live —
        /// they may have moved on since). Unknown/duplicate/already-resolved sequence numbers are ignored
        /// (idempotent — a duplicate ack, or one that arrives after this batch already timed out and was
        /// dropped, is a no-op, never an error).
        /// <para>
        /// LEAD FIX (data-loss review, confirmed): as of this fix, this is ALSO the release point for
        /// <c>zdoPeer.m_forceSend</c>/<c>ZDOMan.m_clientChangeQueue</c> removal — <see cref="Patch_ZDOMan_SendZDOs"/>
        /// no longer removes an item from either set while building the batch (see that file's own remarks on
        /// why: Guard 8's fail-open catch could otherwise let vanilla's own fallback SendZDOs run for the same
        /// call with the force-send mark already gone, permanently orphaning a ZDO reachable only via
        /// force-send, e.g. an out-of-range portal relink). A nack or ack-timeout (<see cref="DropInFlight"/> or
        /// the tracker's own lazy/swept expiry) never reaches this method, so neither set is ever touched for a
        /// failed batch — nothing to explicitly restore; a force-sent or client-owned ZDO in a failed batch is
        /// simply still marked exactly as it was before this batch was ever built.
        /// </para>
        /// <para>
        /// 1.0.8 (zero-data-loss, "never lie to vanilla bookkeeping" / "never regress a revision the receiver
        /// already proved it has"): two changes, both made necessary by superseding sends (see
        /// <see cref="IsInFlightUnacked"/> and <see cref="InFlightTracker{TKey,TItem}"/>'s own remarks) letting
        /// more than one batch for the same id be in flight at once, resolvable in EITHER order:
        /// <list type="number">
        /// <item>The <c>m_zdos</c> write below now takes the MAX of the existing entry and this item, per
        /// field, instead of overwriting outright. Without this, a late-arriving ack for an OLDER, already-
        /// superseded batch (its NEWER sibling having already committed first) would silently regress this
        /// peer's own bookkeeping to a stale revision, and vanilla's own <c>ShouldSend</c> would then think a
        /// perfectly up-to-date field needs sending again — wasteful, and a laundered violation of "only ever
        /// record what the receiver ack'd", even though the receiver in this exact case did genuinely ack both.
        /// </item>
        /// <item>The removals from <c>m_forceSend</c>/<c>m_clientChangeQueue</c> are no longer unconditional —
        /// only once this commit's OWN revision is confirmed to still be the ZDO's CURRENT one (no newer local
        /// change outstanding, whether already in a still-unacked newer batch or not yet even offered). Removing
        /// either mark on a stale commit while a newer change is genuinely still pending would silently stop
        /// that id from ever being reconsidered — permanently, for <c>m_clientChangeQueue</c> specifically,
        /// since <c>CreateSyncList</c>'s client branch (CLIENT:77432-77446) iterates ONLY that set, unlike the
        /// server's range-based scan which would eventually rediscover the ZDO by other means.
        /// </item>
        /// </list>
        /// </para>
        /// </summary>
        internal static void CommitInFlight(ZDOMan.ZDOPeer zdoPeer, int seq)
        {
            if (zdoPeer?.m_peer == null) return;
            if (!s_states.TryGetValue(zdoPeer.m_peer, out var state)) return;

            InFlightItem[] items = state.Tracker.Commit(seq, out _);
            if (items == null) return; // unknown/duplicate/already-timed-out seq — idempotent no-op, not an error

            bool isServer = ZNet.instance != null && ZNet.instance.IsServer();
            ZDOMan zdoMan = ZDOMan.instance;
            foreach (var item in items)
            {
                // 1.0.8: max per field, never a plain overwrite — see remarks above.
                uint mergedData = item.DataRevision;
                ushort mergedOwner = item.OwnerRevision;
                float mergedTime = item.BatchGameTime;
                if (zdoPeer.m_zdos.TryGetValue(item.Id, out var existing))
                {
                    if (existing.m_dataRevision > mergedData) mergedData = existing.m_dataRevision;
                    if (existing.m_ownerRevision > mergedOwner) mergedOwner = existing.m_ownerRevision;
                    if (existing.m_syncTime > mergedTime) mergedTime = existing.m_syncTime;
                }
                zdoPeer.m_zdos[item.Id] = new ZDOMan.ZDOPeer.PeerZDOInfo(mergedData, mergedOwner, mergedTime);

                // 1.0.8: only retire the force-send mark / client-change-queue membership once this item's own
                // revision is still exactly the ZDO's current one — see remarks above.
                ZDO liveZdo = zdoMan?.GetZDO(item.Id);
                bool stillCurrent = liveZdo != null && liveZdo.DataRevision == item.DataRevision && liveZdo.OwnerRevision == item.OwnerRevision;
                if (stillCurrent)
                {
                    zdoPeer.m_forceSend.Remove(item.Id);
                    if (!isServer) zdoMan.m_clientChangeQueue.Remove(item.Id); // SERVER:77101/CLIENT:77243 equivalent, deferred — see remarks above
                }
            }

            CompressionTelemetry.InFlightGauge(zdoPeer.m_peer, state.Tracker.Count);
        }

        /// <summary>Nack, or the send-side kill-switch flush: drop a batch WITHOUT marking anything known — the caller's ShouldSend naturally reselects it next round.</summary>
        internal static void DropInFlight(ZNetPeer peer, int seq)
        {
            if (!s_states.TryGetValue(peer, out var state)) return;
            state.Tracker.Drop(seq);
            CompressionTelemetry.InFlightGauge(peer, state.Tracker.Count);
        }

        // Not in the blueprint (added here — see CompressedRpc's own remarks): without SOME way to stop
        // retrying a peer whose compression channel can never succeed (a schema mismatch between two
        // different mod builds, or a deterministic parse bug), the sender would keep recompressing and
        // resending the SAME ZDOs to that one peer forever while never falling back to plain ZDOData for it —
        // not a corruption/rollback risk (guard 7 always lets vanilla ShouldSend reselect), but a real
        // "this peer's world view silently stops updating" risk, which the ABSOLUTE ZERO-DATA-LOSS posture of
        // this project treats as seriously as an outright drop. A schema mismatch downgrades immediately (it
        // can never self-heal without a reconnect); a run of ordinary parse/decompress nacks downgrades after
        // a threshold, mirroring Core.AutoStandDown.ExceptionBurstLimit's own "repeated ~= broken" reasoning.
        private const int ConsecutiveNackDowngradeThreshold = 5;

        internal static bool IsDowngraded(ZNetPeer peer) =>
            s_states.TryGetValue(peer, out var state) && state.Downgraded;

        internal static void RecordAckOutcome(ZNetPeer peer, bool success, bool schemaMismatch)
        {
            var state = GetOrCreate(peer);
            bool justDowngraded = false;
            bool nacked = false;
            int nackCount = 0;
            lock (state.OutcomeLock)
            {
                if (success)
                {
                    state.ConsecutiveNacks = 0;
                }
                else if (!state.Downgraded)
                {
                    state.ConsecutiveNacks++;
                    nacked = true;
                    nackCount = state.ConsecutiveNacks;
                    if (schemaMismatch || state.ConsecutiveNacks >= ConsecutiveNackDowngradeThreshold)
                    {
                        state.Downgraded = true;
                        justDowngraded = true;
                    }
                }
            }

            // WP5 Tier-2 detail logging ("compression downgrade-to-plain-ZDOData... reason: nack streak vs
            // schema mismatch"): the individual nacks building toward a downgrade were previously invisible
            // outside a telemetry counter (compression.nacksReceived, aggregate-only, no per-peer streak). The
            // downgrade TRANSITION ITSELF keeps its existing Tier-1 line below, unchanged — a one-time,
            // per-connection state change an operator should see once in the main log, matching NoodlesLog's
            // own Tier-1/Tier-2 split (same discipline Q2YieldGuard's yield/un-yield logging already sets).
            if (nacked)
            {
                NoodlesLog.DetailRateLimited($"zdoc_nack_{peer.m_uid}",
                    $"ZdoCompression: peer uid={peer.m_uid} nack #{nackCount} " +
                    $"({(schemaMismatch ? "schema mismatch" : "transient decode/parse")}).", 5, 60f);
            }

            if (justDowngraded)
            {
                NoodlesLog.Warning($"ZdoCompression: peer uid={peer.m_uid} permanently downgraded to plain " +
                    $"ZDOData for the rest of this connection ({(schemaMismatch ? "schema mismatch" : $"{ConsecutiveNackDowngradeThreshold} consecutive nacks")}).");
                Telemetry.GetCounter("compression.downgrades").Increment();
            }
        }

        /// <summary>Data-loss #13 / the local SettingChanged handler: resolve every peer's in-flight state at once, immediately, never leaving anything stuck.</summary>
        internal static void ClearAllPeers()
        {
            foreach (var peer in s_states.Keys) ClearPeer(peer);
        }

        internal static void ClearPeer(ZNetPeer peer)
        {
            if (peer == null) return;
            s_states.TryRemove(peer, out _);
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect), new[] { typeof(ZNetPeer) })]
        internal static class Patch_ZNet_Disconnect_CompressionCleanup
        {
            // Unconditional, like Core's own PeerState cleanup (PeerState.cs remarks): removing a dictionary
            // entry has no gameplay effect, so "disabled" must never mean "leaks memory for the rest of the
            // session" either. Never touches m_zdos/SetOwner/any ZDO — pure bookkeeping cleanup.
            static void Postfix(ZNetPeer peer)
            {
                try { if (peer != null) ClearPeer(peer); }
                catch (Exception e) { NoodlesLog.PatchException(nameof(Patch_ZNet_Disconnect_CompressionCleanup), e); }
            }
        }
    }

    internal sealed class PeerCompressionState
    {
        public readonly InFlightTracker<ZDOID, InFlightItem> Tracker = new InFlightTracker<ZDOID, InFlightItem>();
        public int NextSeq;

        // Separate from Tracker's own internal lock — guards the two plain fields below only.
        public readonly object OutcomeLock = new object();
        public bool Downgraded;
        public int ConsecutiveNacks;
    }

    /// <summary>One ZDO's revision numbers exactly as serialized into a batch — must be captured at send time, not re-read live at ack time (see TrackInFlight).</summary>
    internal readonly struct InFlightItem
    {
        public readonly ZDOID Id;
        public readonly uint DataRevision;
        public readonly ushort OwnerRevision;
        public readonly float BatchGameTime; // Time.time snapshot — matches vanilla PeerZDOInfo.m_syncTime's clock, not this module's own realtime clock

        public InFlightItem(ZDOID id, uint dataRevision, ushort ownerRevision, float batchGameTime)
        {
            Id = id;
            DataRevision = dataRevision;
            OwnerRevision = ownerRevision;
            BatchGameTime = batchGameTime;
        }
    }
}
