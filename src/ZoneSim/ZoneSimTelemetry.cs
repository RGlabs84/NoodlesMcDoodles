using NoodlesMcDoodles.Core;

namespace NoodlesMcDoodles.ZoneSim
{
    /// <summary>
    /// Dotted-access convenience wrappers over <see cref="Telemetry"/>, matching CORE-API.md's own guidance
    /// ("add your own alongside it in your own module's static class") and the established sibling-module
    /// naming (<c>Telemetry_Transport</c> in <c>DestroyChunking.cs</c>). <see cref="Telemetry"/> itself is a
    /// non-partial Core static class this module must not edit, so a nested <c>Telemetry.ZoneSim</c> (the
    /// dotted style docs/design/04's own Telemetry section describes in prose) isn't reachable from here — the
    /// underlying counter/gauge NAMES below (all prefixed <c>zonesim.</c>, grep-able, matching the
    /// <c>ownership.*</c> precedent in <c>Core/Contracts.cs</c>) are what actually appears in the log line;
    /// this class only wraps them for readable call sites.
    /// </summary>
    internal static class Telemetry_ZoneSim
    {
        // --- Hook 1 (Z2) ------------------------------------------------------------------------------------
        public static Counter ZonesPokedTotal => Telemetry.GetCounter("zonesim.zonesPokedTotal");
        public static void SetZonesRealActive(int count) => Telemetry.SetGauge("zonesim.zonesRealActive", count);
        public static void SetZoneBuildsThisTick(int count) => Telemetry.SetGauge("zonesim.zoneBuildsThisTick", count);
        // Held-band redesign synthesis, 2026-09-28: the currently-applied ZoneSim.HeldBandRingBonus term (0-2),
        // set once per real driver tick alongside the other Hook 1 gauges — the periodic-telemetry answer to
        // "at which ring", alongside DeploymentLog's own startup-line answer.
        public static void SetHeldBandRingBonus(int bonus) => Telemetry.SetGauge("zonesim.heldBandRingBonus", bonus);

        // --- Hook 2 (Z3) + containment -----------------------------------------------------------------------
        public static Counter ObjectsAppendedTotal => Telemetry.GetCounter("zonesim.objectsAppendedTotal");
        public static Counter ObjectsSkippedMissingPrefab => Telemetry.GetCounter("zonesim.objectsSkippedMissingPrefab");
        public static Counter ObjectsDestroyedOnUnfeed => Telemetry.GetCounter("zonesim.objectsDestroyedOnUnfeed");
        public static Counter InstantiateExceptionsCaught => Telemetry.GetCounter("zonesim.instantiateExceptionsCaught");
        public static void SetPrefabsPoisoned(int count) => Telemetry.SetGauge("zonesim.prefabsPoisoned", count);
        public static void SetZonesQuarantined(int count) => Telemetry.SetGauge("zonesim.zonesQuarantined", count);

        // --- Hook 3 (Z5) --------------------------------------------------------------------------------------
        public static void SetGhostSpawnsThisTick(int count) => Telemetry.SetGauge("zonesim.ghostSpawnsThisTick", count);

        // --- Hook 4 (Z4) --------------------------------------------------------------------------------------
        public static void SetZonesTtlDestroyedThisTick(int count) => Telemetry.SetGauge("zonesim.zonesTtlDestroyedThisTick", count);

        // --- Hook 5 (Z9) --------------------------------------------------------------------------------------
        public static Counter CapHitCount => Telemetry.GetCounter("zonesim.capHitCount");
        public static void SetCapCurrentlyOverBudget(bool over) => Telemetry.SetGauge("zonesim.capCurrentlyOverBudget", over ? 1 : 0);
        public static void SetGlobalObjectEstimate(int count) => Telemetry.SetGauge("zonesim.globalObjectEstimateThisTick", count);

        // --- Hook 7 (Z7) --------------------------------------------------------------------------------------
        public static void SetSnapDeferredCount(int count) => Telemetry.SetGauge("zonesim.snapDeferredCount", count);
        public static Counter SnapRetriedCount => Telemetry.GetCounter("zonesim.snapRetriedCount");

        // --- Hook 8 (Z8/G6-N2) --------------------------------------------------------------------------------
        public static Counter TerrainCompDuplicatesObserved => Telemetry.GetCounter("zonesim.terrainCompDuplicatesObserved");

        // --- 1.0.9 (frame-stall fix) -- Hook 6 create-loop time budget -----------------------------------------
        // create backlog (objects waiting), created-per-tick and budget-limited ticks -- task brief's own
        // telemetry ask, set/incremented by CreateObjectsBudget.cs's new Patch_ZNetScene_CreateObjects_TimeBudgetTick.
        public static void SetCreateBacklog(int count) => Telemetry.SetGauge("zonesim.createBacklog", count);
        public static void SetCreatedThisTick(int count) => Telemetry.SetGauge("zonesim.createdThisTick", count);
        public static Counter CreateBudgetLimitedTicksTotal => Telemetry.GetCounter("zonesim.createBudgetLimitedTicksTotal");

        // --- 1.0.9 (frame-stall fix) -- Hook 1 zone-build pacing ----------------------------------------------
        // zone builds deferred -- task brief's own telemetry ask, set/incremented by PokeRingDriver.RunTick.
        public static void SetZoneBuildsDeferredThisTick(int count) => Telemetry.SetGauge("zonesim.zoneBuildsDeferredThisTick", count);
        public static Counter ZoneBuildsDeferredTotal => Telemetry.GetCounter("zonesim.zoneBuildsDeferredTotal");
        public static void SetLastZoneBuildMs(double ms) => Telemetry.SetGauge("zonesim.lastZoneBuildMs", ms);

        // --- General (Open question 2's real per-tick cost — actually measured, not left as a gap) ------------
        // Two separate gauges rather than one combined figure: Hook 1 and Hook 2 run on different, independent
        // cadences (the configurable UpdateTickSeconds driver vs. ZNetScene's own fixed 1/30s), so a single
        // "per tick" number would conflate two different tick lengths. Hook 6's own incremental cost is not
        // separately isolated — it runs inlined inside vanilla's own CreateObjectsSorted/CreateDistantObjects
        // loop, which this module does not wrap a timer around.
        public static void SetPokeRingTickMs(double ms) => Telemetry.SetGauge("zonesim.pokeRingTickMs", ms);
        public static void SetObjectFeedMs(double ms) => Telemetry.SetGauge("zonesim.objectFeedMs", ms);
    }
}
