using System.Collections.Generic;

namespace NoodlesMcDoodles.Core
{
    /// <summary>
    /// "Is the server itself simulating this zone" — DESIGN.md Lead decision 1: shipped as Core
    /// infrastructure in M1, always empty, so Q6 (doc 01, M1 — send-priority bias for server-held zones) can
    /// exist and compile against a real type from day one, while the only WRITER, the Ownership module
    /// (`docs/design/03`, M3's <c>G3-L1</c>/<c>O6</c>), doesn't land until three milestones later. Until then
    /// this is an honest O(1)-false lookup, not a mock — nothing in M1 ever calls <see cref="Claim"/>.
    ///
    /// <see cref="ZoneSystem.GetZone(UnityEngine.Vector3)"/> returns <c>Vector2s</c> (SERVER:115599,
    /// confirmed this pass), NOT <c>Vector2i</c> — this API matches that ground truth and doc 01's own Hook 8
    /// citation ("O(1) HashSet&lt;Vector2s&gt; lookup, owned by G3-L1/O6"), which is the type every caller of
    /// <see cref="IsServerHeld"/> will already have in hand from calling <c>ZoneSystem.GetZone</c> itself.
    /// </summary>
    internal static class ServerZoneMembership
    {
        private static readonly HashSet<Vector2s> s_held = new HashSet<Vector2s>();
        private static readonly object s_lock = new object();

        /// <summary>O(1), read from any thread. Always false until the Ownership module's claim pass exists and runs.</summary>
        public static bool IsServerHeld(Vector2s zone)
        {
            lock (s_lock)
            {
                return s_held.Contains(zone);
            }
        }

        /// <summary>Ownership module (M3) only. Idempotent.</summary>
        public static void Claim(Vector2s zone)
        {
            lock (s_lock)
            {
                s_held.Add(zone);
            }
        }

        /// <summary>Ownership module (M3) only. Idempotent — releasing a zone that isn't held is a no-op.</summary>
        public static void Release(Vector2s zone)
        {
            lock (s_lock)
            {
                s_held.Remove(zone);
            }
        }

        public static int Count
        {
            get { lock (s_lock) { return s_held.Count; } }
        }

        public static List<Vector2s> Snapshot()
        {
            lock (s_lock)
            {
                return new List<Vector2s>(s_held);
            }
        }

        /// <summary>
        /// Registered with the kill switch (see <see cref="NoodlesPlugin"/>) so a stale claim can never
        /// outlive Core.Enabled=false even once the Ownership module is the real writer — cheap, always-safe
        /// insurance for a data structure with no side effects of its own (unlike O7's SetOwner hold-set,
        /// which needs its own active hand-back, this is just a lookup table).
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
