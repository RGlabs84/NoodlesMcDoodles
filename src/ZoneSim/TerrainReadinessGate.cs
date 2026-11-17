using System;
using HarmonyLib;
using NoodlesMcDoodles.Core;

namespace NoodlesMcDoodles.ZoneSim
{
    /// <summary>
    /// Hook 8 (Z8 residual / G6-N2) — docs/design/04, M5/B3: <c>TerrainComp</c> duplicate-race telemetry canary
    /// plus the readiness gate a future server-authored terrain mechanic must consult.
    ///
    /// Z8's originally-proposed mitigation ("never build real terrain under a peer's own feet") is withdrawn in
    /// the blueprint — the cross-machine duplicate race it was written to prevent does not exist
    /// (<c>TerrainComp.Awake</c>'s <c>FindTerrainCompiler</c> searches only this PROCESS's own
    /// <c>s_instances</c>/<c>s_duplicateInstances</c>, static fields local to one assembly's runtime). The real,
    /// narrower residual: a server-authored terrain edit (a future creature/projectile mechanic, not built by
    /// this doc) firing in a zone whose existing persistent compiler ZDO this process has not yet instantiated
    /// could create a genuinely duplicate compiler ZDO, and vanilla's own resolution for that
    /// (<c>TryCleanInvalidTCs</c>) is destructive — keeps whichever duplicate has more <c>m_operations</c>,
    /// unconditionally <c>Destroy</c>s the loser, with no <c>Persistent</c> check anywhere in the method
    /// (confirmed this session, <c>SERVER:143652-143681</c>/<c>CLIENT:144568-144597</c>).
    ///
    /// This doc does NOT claim this closes the race — terrain-before-objects ordering (already mandatory for
    /// Hooks 1->2) narrows the window to at most one 1/30s <c>ZNetScene</c> tick, never eliminates it by
    /// decompile-provable construction. Z8/G6-N2 keep their NEEDS-LIVE-TEST verdict; the canary below is what
    /// makes a real-world occurrence visible rather than silent.
    /// </summary>
    internal static class TerrainReadinessGate
    {
        /// <summary>
        /// <c>TerrainComp.TryCleanInvalidTCs()</c> — private instance, <c>SERVER:143652-143681</c>/
        /// <c>CLIENT:144568-144597</c>, called unconditionally from <c>Start()</c>. Counts only when vanilla
        /// will really delete a duplicate — in a correctly-ordered deployment this postfix should
        /// almost never fire once terrain-before-objects ordering is respected; a nonzero rate is the live
        /// signal that ordering is being violated somewhere, including by another mod's own terrain-editing
        /// feature. Purely observational (prefix, reads only, never skips the original) — kept running even with the feature flag
        /// off, per the catalog's own "harmless and additive" note; costs nothing to leave active.
        /// </summary>
        [HarmonyPatch(typeof(TerrainComp), "TryCleanInvalidTCs")]
        internal static class Patch_TerrainComp_TryCleanInvalidTCs_Canary
        {
            // LEAD FIX 2026-09-28: this was a postfix that incremented on EVERY call, but vanilla calls
            // TryCleanInvalidTCs from every TerrainComp.Start() and returns at once when there are no duplicates,
            // so the counter really counted loaded terrain-edit objects (a live client showed 5 with zero
            // "Removed duplicate terrain compiler" lines). Now a prefix that counts only what vanilla is about
            // to delete: it removes Count-1 compilers when this instance is in s_duplicateInstances
            // (CLIENT:144568-144592, the TryGetValue/position check reduces to set membership).
            [HarmonyPrefix]
            internal static void Prefix(TerrainComp __instance)
            {
                try
                {
                    var duplicates = TerrainComp.s_duplicateInstances;
                    if (duplicates.Count > 1 && duplicates.Contains(__instance))
                        Telemetry_ZoneSim.TerrainCompDuplicatesObserved.Add(duplicates.Count - 1);
                }
                catch (Exception e) { NoodlesLog.PatchException(nameof(Patch_TerrainComp_TryCleanInvalidTCs_Canary), e); }
            }
        }

        /// <summary>
        /// The gate a future server-authored <c>TerrainOp</c> mechanic (not built by this module — out of
        /// docs/design/04's own scope) must consult before running: true only once a zone's terrain root exists
        /// (<c>m_zones.ContainsKey</c>, via <see cref="PokeRingDriver"/>) AND <see cref="Patch_ZNetScene_CreateObjects_ObjectFeed"/>'s object
        /// feed has already appended every ZDO in that zone whose prefab is the <c>TerrainComp</c> prefab —
        /// i.e. any pre-existing compiler ZDO for that ground has already been instantiated in this process
        /// before anything is allowed to create a competing one. No pre-emptive reservation/lock primitive
        /// exists anywhere in the decompile for this class (no mutex, no "claim intent" ZDO field, nothing
        /// beyond the reactive, destructive cleanup in <c>Start()</c>) — this is the smallest hook that narrows
        /// the window without inventing new persisted state.
        /// </summary>
        public static bool IsReady(Vector2s zone)
        {
            if (ZoneSystem.instance == null || !ZoneSystemAccess.IsReady) return false;
            return ZoneSystemAccess.ContainsZone(ZoneSystem.instance, zone);
        }
    }
}
