using System.Collections.Generic;

namespace NoodlesMcDoodles.Compression
{
    /// <summary>
    /// The actual state machine behind guard 3 / guard 7 (docs/design/06 Data-loss #10, #11 — "a ZDO is marked
    /// sent to a peer only after that peer's application-level ack; a failed decode is a nack and a resend,
    /// never a silent drop; unacked ZDOs are not re-offered until ack or timeout"), factored out as a pure,
    /// engine-independent class: every clock reading is a plain <see cref="float"/> the CALLER supplies, never
    /// read internally, and <typeparamref name="TKey"/>/<typeparamref name="TItem"/> are whatever the caller
    /// needs them to be (production: <c>ZDOID</c>/<see cref="InFlightItem"/>; <c>tools/zip-sim</c>: plain
    /// surrogate types) — this class has ZERO dependency on UnityEngine or the vanilla game assemblies.
    ///
    /// WHY THIS IS ITS OWN CLASS rather than being inlined into <see cref="CompressionState"/>: it is the one
    /// piece of this module's logic that is genuinely worth an offline, Unity-free correctness proof (lost ack,
    /// duplicate ack, out-of-order ack, reconnect/clear — see <c>tools/zip-sim</c>) — a bug here is a real
    /// zero-data-loss risk (an unacked ZDO that's never reselected, or a stale ack that resurrects already-
    /// superseded revision numbers), unlike the thin <c>ZDOID</c>/<c>ZNetPeer</c>-specific glue in
    /// <see cref="CompressionState"/> itself, which has nothing left to get wrong once this class is correct.
    /// <c>tools/zip-sim</c> loads THIS exact class (generic, closed over simple surrogate types) by reflection
    /// from the real built DLL and drives it directly — not a reimplementation of its algorithm.
    ///
    /// 1.0.8: a single key can now be recorded by MORE THAN ONE simultaneously in-flight batch. Before this
    /// version, guard 7 (<see cref="CompressionState.IsInFlightUnacked"/>) unconditionally suppressed re-send of
    /// any key with ANY unacked batch, so a key was never re-tracked until its one outstanding batch resolved —
    /// fine for a static ZDO, but for one that changes every send round (a moving creature/player) it meant the
    /// wire only ever caught up once per ack round-trip, not once per send round (the stutter bug this version
    /// fixes). Now the caller (<see cref="CompressionState.IsInFlightUnacked"/>) compares the CURRENT ZDO
    /// revision against whichever batch this class says is newest for that key, and — if the ZDO has moved on —
    /// lets it be sent again (a "superseding send") without waiting for the OLDER batch's own ack/nack/timeout.
    /// That older batch is still tracked here, still resolves normally on its own, and Track()'s own
    /// last-write-wins over <c>_keyToSeq</c> (callers only ever pass strictly-increasing <c>seq</c> values, so
    /// the newest Track() call for a key is always the one still holding its slot) already makes it the case
    /// that <c>_keyToSeq[key]</c> always points at the NEWEST batch that included the key. What changed instead
    /// is Commit/Drop/DropLocked below: they used to unconditionally erase <c>_keyToSeq[key]</c> for every key
    /// in the resolving batch — correct when a key could only ever be in one batch, but WRONG now: resolving
    /// the OLDER of two batches for a shared key must never clobber the NEWER batch's own claim on that key's
    /// index entry. See <see cref="RemoveKeyIndexIfCurrent"/>.
    /// </summary>
    internal sealed class InFlightTracker<TKey, TItem>
    {
        private struct Batch
        {
            public TKey[] Keys;
            public TItem[] Items;
            public float SentAtRealtime;

            // 1.0.8: O(1) key -> index into Keys/Items, so IsInFlightUnacked (called on nearly every
            // ShouldSend, i.e. every candidate ZDO on every send round) never has to linear-scan a batch to
            // find "the item this batch recorded for this key" — see class remarks.
            public Dictionary<TKey, int> KeyIndex;
        }

        private readonly Dictionary<int, Batch> _batches = new Dictionary<int, Batch>();
        private readonly Dictionary<TKey, int> _keyToSeq = new Dictionary<TKey, int>();
        private readonly object _lock = new object();

        /// <summary>Count of individual keys currently withheld across every in-flight batch (a telemetry gauge input).</summary>
        public int Count { get { lock (_lock) return _keyToSeq.Count; } }

        /// <summary>
        /// Records a just-sent batch as in-flight. A duplicate <paramref name="seq"/> overwrites (never expected
        /// in practice — sequence numbers are caller-monotonic). 1.0.8: <c>_keyToSeq[key] = seq</c> below is an
        /// unconditional overwrite, same as before this version — the only thing that made a shared key's OLDER
        /// batch vulnerable to being clobbered was Commit/Drop erasing that mapping unconditionally on the
        /// OLDER batch's own resolution; this method itself needed no change for superseding to work.
        /// </summary>
        public void Track(int seq, TKey[] keys, TItem[] items, float nowRealtime)
        {
            lock (_lock)
            {
                var keyIndex = new Dictionary<TKey, int>(keys.Length);
                for (int i = 0; i < keys.Length; i++) keyIndex[keys[i]] = i;
                _batches[seq] = new Batch { Keys = keys, Items = items, SentAtRealtime = nowRealtime, KeyIndex = keyIndex };
                foreach (var key in keys) _keyToSeq[key] = seq;
            }
        }

        /// <summary>
        /// True iff <paramref name="key"/> is currently withheld awaiting an ack — <paramref name="item"/> is
        /// set to that batch's OWN recorded item for this key (whichever batch <c>_keyToSeq[key]</c> currently
        /// points to, always the NEWEST one that included the key — see class remarks), <c>default</c>
        /// otherwise. 1.0.8: this class deliberately still does not know what a "newer revision" means for
        /// <typeparamref name="TItem"/> (kept Unity/vanilla-free, per the class's own founding remarks) — the
        /// CALLER (<see cref="CompressionState.IsInFlightUnacked"/> in production; the caller's own comparison
        /// in <c>tools/zip-sim</c>) is the one who compares <paramref name="item"/> against the live ZDO's
        /// current revision to decide "still the same offer, stay suppressed" vs "superseded, safe to send
        /// again". This method's own job is unchanged from before: "is there a live hold on this key right now,
        /// and if so, which batch is it."
        /// <para>
        /// Lazily expires (and fully purges, every key in the batch, not just this one) any batch older than
        /// <paramref name="timeoutSeconds"/> the moment it's next queried — no separate timer needed. A key
        /// whose batch just expired reads as "not in flight" (caller's own normal answer stands — naturally
        /// reselected, never a silent drop). Expiry mechanics are byte-for-byte what they were before 1.0.8;
        /// only <see cref="DropLocked"/>'s own OWN index cleanup changed (see its remarks).
        /// </para>
        /// </summary>
        public bool IsInFlightUnacked(TKey key, float nowRealtime, float timeoutSeconds, out TItem item, out bool justExpired)
        {
            justExpired = false;
            item = default;
            lock (_lock)
            {
                if (!_keyToSeq.TryGetValue(key, out int seq)) return false;
                if (!_batches.TryGetValue(seq, out var batch))
                {
                    _keyToSeq.Remove(key); // stale index entry (already committed/dropped elsewhere) — self-heal
                    return false;
                }
                if (nowRealtime - batch.SentAtRealtime > timeoutSeconds)
                {
                    DropLocked(seq, batch);
                    justExpired = true;
                    return false;
                }
                if (batch.KeyIndex.TryGetValue(key, out int idx)) item = batch.Items[idx];
                return true;
            }
        }

        /// <summary>Full sweep of THIS tracker's own batches (not just whichever key happens to be re-queried) — see CompressionState's own remarks on why this needs no periodic driver. Returns the sequence numbers that were dropped.</summary>
        public List<int> SweepExpired(float nowRealtime, float timeoutSeconds)
        {
            var expired = new List<int>();
            lock (_lock)
            {
                foreach (var kv in _batches)
                    if (nowRealtime - kv.Value.SentAtRealtime > timeoutSeconds) expired.Add(kv.Key);
                foreach (var seq in expired)
                    if (_batches.TryGetValue(seq, out var batch)) DropLocked(seq, batch);
            }
            return expired;
        }

        /// <summary>Ack: release a batch. Returns its items (for the caller to apply its own deferred "now known" write) or null if <paramref name="seq"/> is unknown — a duplicate ack, or an ack for a batch already resolved (committed or timed out), is an idempotent no-op, never an error.</summary>
        public TItem[] Commit(int seq, out TKey[] keys)
        {
            lock (_lock)
            {
                if (!_batches.TryGetValue(seq, out var batch)) { keys = null; return null; }
                RemoveKeyIndexIfCurrent(seq, batch); // 1.0.8: a NEWER batch may already own one of these keys — see remarks
                _batches.Remove(seq);
                keys = batch.Keys;
                return batch.Items;
            }
        }

        /// <summary>Nack, or an explicit kill-switch-style flush of one batch: drop WITHOUT marking anything known. Duplicate/unknown seq is a no-op.</summary>
        public void Drop(int seq)
        {
            lock (_lock)
            {
                if (_batches.TryGetValue(seq, out var batch)) DropLocked(seq, batch);
            }
        }

        /// <summary>Drops every batch at once, immediately — the kill-switch / local-flag-off flush path.</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _batches.Clear();
                _keyToSeq.Clear();
            }
        }

        private void DropLocked(int seq, Batch batch)
        {
            RemoveKeyIndexIfCurrent(seq, batch); // 1.0.8: a NEWER batch may already own one of these keys — see remarks
            _batches.Remove(seq);
        }

        /// <summary>
        /// 1.0.8: since a key can now be recorded by more than one simultaneously in-flight batch (a
        /// superseding send re-tracks it in a NEWER batch while an OLDER one for the same key is still
        /// unacked — see class remarks), resolving one batch (commit, nack, or timeout) must only clear
        /// <c>_keyToSeq[key]</c> if it still points at THIS <paramref name="seq"/>. If a newer batch already
        /// re-pointed it at itself, that newer mapping is the current truth for the key and must survive this
        /// (older) batch's own resolution untouched — erasing it here would make <see cref="IsInFlightUnacked"/>
        /// wrongly answer "not in flight" for a key that a genuinely still-unacked newer batch is holding,
        /// letting it be reselected and resent a third time for no reason (not a data-loss bug by itself, since
        /// guard 7's whole failure mode is "resend too much", never "send nothing" — but exactly the kind of
        /// needless resend loop this class's own remarks already call out for the pre-existing force-send
        /// exemption, now reachable through the ordinary superseding path too, not just that one narrow case).
        /// </summary>
        private void RemoveKeyIndexIfCurrent(int seq, Batch batch)
        {
            foreach (var key in batch.Keys)
                if (_keyToSeq.TryGetValue(key, out int owner) && owner == seq) _keyToSeq.Remove(key);
        }
    }
}
