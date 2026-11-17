using System.Collections.Generic;

namespace NoodlesMcDoodles.ZoneSim
{
    /// <summary>
    /// Hook 5 (Z9) — docs/design/04: object-count budget with hysteresis, consulted by <see cref="Patch_ZNetScene_CreateObjects_ObjectFeed"/>
    /// (Hook 2) before appending a zone's objects. No vanilla method patched — pure new mod-owned bookkeeping.
    ///
    /// RED-TEAM FIX (RT1 major #2): the estimate is rebuilt from scratch every tick (<see cref="BeginTick"/>,
    /// called once from <see cref="Patch_ZNetScene_CreateObjects_ObjectFeed"/>'s prefix before its candidate-zone loop) rather than
    /// accumulated across the server's lifetime. The original design only ever incremented a running total when
    /// a zone was newly included and never decremented it when a zone stopped being fed — on a 24/7 server
    /// (archserver, alongside two other Valheim servers) that total could only grow, eventually pinning
    /// <see cref="CanInclude"/> at <c>false</c> for EVERY zone, including already-included ones, permanently,
    /// with no recovery short of a restart. Rebuilding fresh every tick makes the estimate always exactly "what
    /// is included this tick," so demand dropping (peers leaving, a zone aging out) is reflected immediately.
    ///
    /// Deviation from docs/design/04's own code sketch: the per-zone object-count estimate is passed IN by the
    /// caller (<see cref="Patch_ZNetScene_CreateObjects_ObjectFeed"/>, which already called <c>ZDOMan.FindSectorObjects</c> for that zone to
    /// build its actual near/distant append lists) rather than computed here via a second, redundant
    /// <c>FindSectorObjects</c> call per zone per tick. Same behaviour, one fewer sector query per zone per
    /// tick — see docs/impl/ZONESIM-IMPL.md "deviations" for the full note.
    /// </summary>
    internal static class ZoneCap
    {
        private static readonly HashSet<Vector2s> s_includedThisTick = new HashSet<Vector2s>();
        private static int s_globalObjectEstimate;

        /// <summary>Called once, from <see cref="Patch_ZNetScene_CreateObjects_ObjectFeed"/>'s prefix, before any <see cref="CanInclude"/> call this tick.</summary>
        public static void BeginTick()
        {
            s_includedThisTick.Clear();
            s_globalObjectEstimate = 0;
            Telemetry_ZoneSim.SetCapCurrentlyOverBudget(false);
        }

        /// <summary>
        /// True if <paramref name="zone"/> may be fed this tick. Hysteresis: a zone already included this pass
        /// stays included up to <c>CapExcludeThresholdPct</c> of the global budget; a zone not yet seen this
        /// pass only joins below <c>CapIncludeThresholdPct</c>. Both branches always account the zone into the
        /// running total on success, so the total is always exactly "what's included this tick," never a
        /// lifetime-growing sum (RED-TEAM FIX above). Never destroys anything as enforcement — "over budget"
        /// only ever means "refuse this new work."
        /// </summary>
        public static bool CanInclude(Vector2s zone, int zoneObjectEstimate)
        {
            int perPeerCap = ZoneSimConfig.MaxObjectsPerPeer.Value; // see below — applied as a per-zone-pass sanity bound too
            int globalCap = ZoneSimConfig.MaxObjectsGlobal.Value;
            float includePct = ZoneSimConfig.CapIncludeThresholdPct.Value;
            float excludePct = System.Math.Max(ZoneSimConfig.CapExcludeThresholdPct.Value, includePct); // exclude >= include, always

            bool alreadyIncluded = s_includedThisTick.Contains(zone);
            long projected = (long)s_globalObjectEstimate + zoneObjectEstimate;
            bool underIncludeThreshold = projected < globalCap * includePct;
            bool underExcludeThreshold = projected < globalCap * excludePct;
            bool include = alreadyIncluded ? underExcludeThreshold : underIncludeThreshold;

            // A single zone's own estimate should never itself exceed the PER-PEER cap in isolation — this is a
            // belt-and-suspenders sanity bound (a zone is shared across overlapping peer rings, so it is not
            // itself "one peer's budget", but a zone alone exceeding a whole peer's budget is always a sign of
            // an extreme outlier area, e.g. research/04's 39,354-object farm-field case) rather than the load-
            // bearing check — the global hysteresis above is.
            if (zoneObjectEstimate > perPeerCap) include = false;

            if (include)
            {
                s_includedThisTick.Add(zone);
                s_globalObjectEstimate += zoneObjectEstimate;
            }
            else
            {
                Telemetry_ZoneSim.CapHitCount.Increment();
                Telemetry_ZoneSim.SetCapCurrentlyOverBudget(true);
            }

            Telemetry_ZoneSim.SetGlobalObjectEstimate(s_globalObjectEstimate);
            return include;
        }

        /// <summary>
        /// Read by <see cref="ZoneSimConfig"/>'s <c>ZoneSimGate.CanHoldProvider</c> — true only for a zone that
        /// was actually fed THIS tick (in some ready peer's ring, already real, not quarantined, and under
        /// budget). See that method's own remarks for why this single check already implies every condition
        /// the seam's contract requires.
        /// </summary>
        public static bool WasIncludedThisTick(Vector2s zone) => s_includedThisTick.Contains(zone);
    }
}
