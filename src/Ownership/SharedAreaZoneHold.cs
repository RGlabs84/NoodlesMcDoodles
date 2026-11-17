using System.Collections.Generic;

namespace NoodlesMcDoodles.Ownership
{
    /// <summary>
    /// O10 (EXPERIMENTAL, default off — docs/design/03, "server-in-shared-areas") — a REFCOUNTED zone-&gt;count
    /// registry for objects the server holds via O10's far-regime claim, distinct from O6/OwnershipAuthority's
    /// whole-zone <see cref="NoodlesMcDoodles.Core.ServerZoneMembership"/>: several DIFFERENT O10-held objects
    /// can share one zone (each independently claimed/released as its own sighting set's far-regime state
    /// changes over time), so a zone's G3-L1 protection must last until EVERY O10 hold on it has released, not
    /// just the first or the most recent — a plain <c>HashSet</c> (that class's own shape) cannot express that.
    /// Mirrors its Claim/Release/IsHeld/Count/Snapshot shape and lock-per-call pattern exactly (read from G3-L1's
    /// postfix, <see cref="ZoneClaimSweep.Patch_ZDOMan_IsInPeerActiveArea_ServerHold"/>, and from
    /// <see cref="OwnershipLossWatch"/>'s scans — the same two callers <c>ServerZoneMembership</c> already has),
    /// with Claim/Release now a refcounted increment/decrement instead of an idempotent add/remove.
    /// </summary>
    internal static class SharedAreaZoneHold
    {
        private static readonly Dictionary<Vector2s, int> s_held = new Dictionary<Vector2s, int>();
        private static readonly object s_lock = new object();

        /// <summary>O(1), read from any thread. False whenever nothing O10 has claimed is left in this zone.</summary>
        public static bool IsHeld(Vector2s zone)
        {
            lock (s_lock)
            {
                return s_held.TryGetValue(zone, out int count) && count > 0;
            }
        }

        /// <summary>One more O10-held object in this zone. O10 (this module) only. Main thread (same caller
        /// contract as <c>ServerZoneMembership.Claim</c> — this file only ever runs from
        /// <c>ZDOMan.ReleaseZDOS</c>'s server-only tick).</summary>
        public static void Claim(Vector2s zone)
        {
            lock (s_lock)
            {
                s_held.TryGetValue(zone, out int count);
                s_held[zone] = count + 1;
            }
        }

        /// <summary>
        /// One fewer O10-held object in this zone. Never goes below 0 — the entry is removed once it reaches 0
        /// so <see cref="Count"/>/<see cref="Snapshot"/> only ever report zones with a real hold, and so a stray
        /// extra Release (should never happen: every successful Claim is paired with exactly one eventual
        /// Release, along O10's own handback path or the kill-switch flush below) can't leave a permanent
        /// negative count that would mask a real future Claim's <see cref="IsHeld"/> answer.
        /// </summary>
        public static void Release(Vector2s zone)
        {
            lock (s_lock)
            {
                if (!s_held.TryGetValue(zone, out int count)) return;
                if (count <= 1) s_held.Remove(zone);
                else s_held[zone] = count - 1;
            }
        }

        /// <summary>Distinct zones currently holding at least one O10 object — NOT a sum of refcounts.</summary>
        public static int Count
        {
            get { lock (s_lock) { return s_held.Count; } }
        }

        public static List<Vector2s> Snapshot()
        {
            lock (s_lock)
            {
                return new List<Vector2s>(s_held.Keys);
            }
        }

        /// <summary>
        /// Registered with the kill switch (see <see cref="O10SharedAreaOwnership.Init"/>) so a stale O10 hold
        /// can never outlive <c>Core.Enabled=false</c> — same reasoning as
        /// <c>ServerZoneMembership.ClearAll</c>'s own remarks.
        /// </summary>
        internal static void ClearAll()
        {
            lock (s_lock)
            {
                s_held.Clear();
            }
        }
    }
}
