namespace NoodlesMcDoodles.Smoothing
{
    /// <summary>
    /// Pure per-instance interpolation state for C1 — the actual buffered-interpolation rule, with no
    /// <c>UnityEngine</c>/<c>ZDO</c>/Harmony dependency anywhere in this file (see <see cref="SmoothMath"/>'s
    /// remarks: this is what makes <c>tools/smooth-sim</c> able to load <c>bin/NoodlesMcDoodles.dll</c> by
    /// reflection and drive this exact class with a synthetic update stream, no game process required).
    /// <see cref="Smoothing.SmoothState"/> is the thin Unity/ZDO-aware wrapper that feeds this class from the
    /// Harmony prefix.
    ///
    /// Design (docs/design/06 H-C1 + Red-team reconciliation, research/11 Recommended design): buffer recent raw
    /// samples (position, rotation, velocity-at-that-instant, wall-clock arrival time) and render the pose at
    /// <c>now - playbackDelay</c> by interpolating between whichever two ADJACENT buffered samples actually
    /// bracket that instant — replacing vanilla's single-sample, fixed-rate 0.2/0.5 lerp/slerp
    /// (CLIENT:88012/88076, SERVER:87764/87828) with a proper time-based blend.
    ///
    /// <b>DEVIATION FROM THE BLUEPRINT'S "last two samples" phrasing (see SMOOTHING-IMPL.md):</b> the blueprint's
    /// own text describes buffering exactly two samples. Built and offline-tested that way first — <c>tools/
    /// smooth-sim</c> caught a real staircase artifact from it: the instant a literal 2-sample buffer shifts
    /// (oldest evicted, newest becomes the new "older"), any playback instant that had fallen BEFORE the new
    /// older sample — which happens on every ingest whenever <c>playbackDelaySeconds</c> exceeds the update
    /// interval, an ordinary, common case, not a corner case — has nowhere left to interpolate from and clamps
    /// to the older sample's raw position instead of continuing the smooth trend, producing a visible
    /// "hold-then-jump" pattern synced to the update rate. A small RING of recent samples (see
    /// <see cref="Capacity"/>) fixes this by always searching for the pair that actually brackets the playback
    /// instant, which is what "interpolate at now - playbackDelay" needs to mean whenever more than one update
    /// interval separates the two nearest samples. The buffer is still exactly two SELECTED samples doing the
    /// actual blending on any given call — this only widens how far back the search for THAT pair may look.
    ///
    /// Never touches a ZDO or any Unity object — <see cref="Ingest"/> and <see cref="TryGetRenderPose"/> take
    /// and return plain floats only, so this class cannot itself be the thing that violates "smoothing never
    /// writes a ZDO" (there is no ZDO reference in this file to write through in the first place).
    /// </summary>
    internal sealed class SmoothBuffer
    {
        private struct Sample
        {
            public float PX, PY, PZ;
            public float QX, QY, QZ, QW;
            public float VX, VY, VZ;
            public float ArrivalTime;
        }

        // 24 slots comfortably covers ClientSmoothing.PlaybackDelayMs's full configurable range (0-300ms) even
        // at a fast ~15ms per-object update interval (24*15ms = 360ms > 300ms) with headroom to spare at the
        // realistic rates this project's own send-loop tuning targets. Fixed size, allocated once per instance
        // (the array below), never resized — Ingest/TryGetRenderPose write into existing slots only, so the hot
        // path (every visible non-owned object, every frame) allocates nothing.
        private const int Capacity = 24;

        private readonly Sample[] _ring = new Sample[Capacity];
        private int _head;  // index of the OLDEST valid sample
        private int _count; // how many slots hold real data, 0..Capacity

        private uint _lastRevision;
        private bool _haveRevision;

        // True until the next Ingest, which then starts a brand-new single-sample buffer instead of trying to
        // interpolate against whatever was buffered before — used for "just constructed" AND for "just came
        // back from a suspended (parented-object) period" (research/11 zero-data-loss guard 3b / Data-loss #3b:
        // a long parented excursion must never leave a stale pre-excursion sample for interpolation to blend
        // against once control returns here).
        private bool _needsFreshStart = true;

        /// <summary>Diagnostic only: how many real samples are currently buffered, 0..<see cref="Capacity"/>.</summary>
        public int SampleCount => _count;

        public void NoteSuspended() => _needsFreshStart = true;

        private Sample At(int logicalIndex) => _ring[(_head + logicalIndex) % Capacity];

        /// <summary>
        /// Feed the CURRENT ZDO's revision/position/rotation/velocity — safe and cheap to call every tick even
        /// when nothing changed: this no-ops (returns false, buffer untouched) unless <paramref name="revision"/>
        /// differs from the last one actually buffered, or a fresh start is pending.
        /// <para>
        /// Returns true iff this ingest is a teleport: distance >= <paramref name="snapThresholdMeters"/> from
        /// the immediately PRECEDING RAW sample (never from any interpolated/rendered value — RED-TEAM FIX,
        /// H-C1 Guard 3 / Data-loss #3: checking at ingest time against the raw sample pair is what vanilla's
        /// own per-tick check effectively does; checking against <see cref="TryGetRenderPose"/>'s blended
        /// output instead can never see the full jump once it's spread across the buffered interval). On a
        /// teleport, the buffer is reset to hold ONLY the new sample — the caller must snap its own rendered
        /// transform straight to the raw (px,py,pz)/(qx,qy,qz,qw) passed in THIS call; there is nothing left to
        /// interpolate against until the next real update arrives.
        /// </para>
        /// </summary>
        public bool Ingest(
            uint revision,
            float px, float py, float pz,
            float qx, float qy, float qz, float qw,
            float vx, float vy, float vz,
            float arrivalTime,
            float snapThresholdMeters)
        {
            bool freshStart = _needsFreshStart;
            if (!freshStart && _haveRevision && revision == _lastRevision)
                return false; // nothing new since the last tick that actually buffered a sample

            _lastRevision = revision;
            _haveRevision = true;

            var next = new Sample
            {
                PX = px, PY = py, PZ = pz,
                QX = qx, QY = qy, QZ = qz, QW = qw,
                VX = vx, VY = vy, VZ = vz,
                ArrivalTime = arrivalTime,
            };

            bool teleport = false;
            if (!freshStart && _count > 0)
            {
                Sample newest = At(_count - 1);
                float d = SmoothMath.Distance(newest.PX, newest.PY, newest.PZ, px, py, pz);
                teleport = d >= snapThresholdMeters;
            }

            if (freshStart || teleport)
            {
                _head = 0;
                _count = 1;
                _ring[0] = next;
                _needsFreshStart = false;
                return teleport;
            }

            int writeIndex = (_head + _count) % Capacity;
            _ring[writeIndex] = next;
            if (_count < Capacity) _count++;
            else _head = (_head + 1) % Capacity; // full: drop the oldest to make room
            return false;
        }

        /// <summary>
        /// Computes the render pose at <c>now - playbackDelaySeconds</c>. Returns false ("not ready" — the
        /// caller must fall back to vanilla's own <c>SyncPosition</c> body for this tick) until two real
        /// samples are buffered, e.g. an object that just spawned or just came into range
        /// (research/11 Recommended design step 4 / Per-tick logic, "fewer than two samples yet").
        /// <para>
        /// When the playback instant falls between two buffered samples, blends between the specific ADJACENT
        /// pair that brackets it (see this class's remarks for why that pair is not always simply "the last
        /// two received"). When it falls AFTER the newest sample — a real gap, not ordinary jitter, because
        /// updates have stopped arriving for longer than the playback delay — falls through to the same
        /// velocity-based dead-reckoning vanilla itself does from its newest known sample
        /// (CLIENT:88001-88006/SERVER:87753-87758 pattern: <c>position += velocity * timer</c>, timer capped),
        /// except bounded by the caller's own configurable <paramref name="extrapolationCapSeconds"/> (C3)
        /// instead of vanilla's hardcoded 2s, and only when <paramref name="allowExtrapolation"/> — the
        /// caller's live <c>zdo.HasOwner()</c> read, mirroring vanilla's own <c>if (zdo.HasOwner())</c> gate on
        /// the identical dead-reckoning step. When it falls before the OLDEST buffered sample (only possible
        /// right after a fresh start, or a playback delay configured larger than this buffer's covered history
        /// at an unusually slow update rate), holds at the oldest known sample rather than extrapolate
        /// backward in time.
        /// </para>
        /// </summary>
        public bool TryGetRenderPose(
            float now, float playbackDelaySeconds, float extrapolationCapSeconds, bool allowExtrapolation,
            out float px, out float py, out float pz,
            out float qx, out float qy, out float qz, out float qw)
        {
            px = py = pz = 0f;
            qx = qy = qz = 0f; qw = 1f;
            if (_count < 2) return false;

            float playbackTime = now - playbackDelaySeconds;
            Sample newest = At(_count - 1);

            if (playbackTime > newest.ArrivalTime)
            {
                float gap = playbackTime - newest.ArrivalTime;
                if (gap > extrapolationCapSeconds) gap = extrapolationCapSeconds;
                float ex = allowExtrapolation ? gap : 0f;

                px = newest.PX + newest.VX * ex;
                py = newest.PY + newest.VY * ex;
                pz = newest.PZ + newest.VZ * ex;
                qx = newest.QX; qy = newest.QY; qz = newest.QZ; qw = newest.QW;
                return true;
            }

            Sample oldest = At(0);
            if (playbackTime <= oldest.ArrivalTime)
            {
                px = oldest.PX; py = oldest.PY; pz = oldest.PZ;
                qx = oldest.QX; qy = oldest.QY; qz = oldest.QZ; qw = oldest.QW;
                return true;
            }

            // Linear scan for the adjacent pair (a, b) with a.ArrivalTime <= playbackTime <= b.ArrivalTime.
            // Capacity is small (24) and fixed, so this is a cheap, allocation-free, bounded-cost search —
            // the oldest/newest checks above guarantee such a pair exists in the loop below.
            for (int i = 1; i < _count; i++)
            {
                Sample b = At(i);
                if (b.ArrivalTime < playbackTime) continue;
                Sample a = At(i - 1);

                float span = b.ArrivalTime - a.ArrivalTime;
                float t = span > 0.0001f ? (playbackTime - a.ArrivalTime) / span : 1f;
                if (t < 0f) t = 0f;
                else if (t > 1f) t = 1f;

                px = SmoothMath.Lerp(a.PX, b.PX, t);
                py = SmoothMath.Lerp(a.PY, b.PY, t);
                pz = SmoothMath.Lerp(a.PZ, b.PZ, t);
                SmoothMath.SlerpQuat(a.QX, a.QY, a.QZ, a.QW, b.QX, b.QY, b.QZ, b.QW, t, out qx, out qy, out qz, out qw);
                return true;
            }

            // Unreachable given the oldest/newest bounds checked above — kept only so every `out` parameter is
            // definitely assigned on every path (the compiler cannot see the loop is exhaustive).
            px = newest.PX; py = newest.PY; pz = newest.PZ;
            qx = newest.QX; qy = newest.QY; qz = newest.QZ; qw = newest.QW;
            return true;
        }
    }
}
