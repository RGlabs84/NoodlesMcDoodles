using System;
using System.Collections.Generic;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.ZoneSim
{
    /// <summary>
    /// Hook 2 (Z3) — docs/design/04: per-peer object instantiation. Prefix on
    /// <c>ZNetScene.CreateObjects(List&lt;ZDO&gt; currentNearObjects, List&lt;ZDO&gt; currentDistantObjects)</c>
    /// (private, <c>SERVER:82139-82150</c>/<c>CLIENT:82369-82380</c>, confirmed identical signature and body
    /// both builds), <c>__runOriginal = true</c> always — this only ever ADDS candidates to the two lists vanilla's
    /// own body and the sibling <c>RemoveObjects</c> call already read, never skips or replaces vanilla's own work.
    ///
    /// Why appending here (not a prefix on <c>CreateDestroyObjects</c>): <c>CreateDestroyObjects</c>
    /// (<c>SERVER:82314-82321</c>) calls <c>CreateObjects(m_tempCurrentObjects, m_tempCurrentDistantObjects)</c>
    /// then <c>RemoveObjects(m_tempCurrentObjects, m_tempCurrentDistantObjects)</c> with the SAME two
    /// <c>List&lt;ZDO&gt;</c> references — appending here mutates those lists in place before either method
    /// reads them, so appended ZDOs are both instantiated (<c>CreateObjects</c>) and protected from the same-tick
    /// sweep (<c>RemoveObjects</c>'s earmark logic) for free.
    ///
    /// Mandatory guard (Lead item 3, re-verified this session against both <c>CreateObjectsSorted</c>
    /// <c>(SERVER:82178-82188)</c> AND <c>CreateDistantObjects</c> <c>(SERVER:82220-82224)</c> — both carry the
    /// identical "CreateObject returned null and IsServer() -> SetOwner+DestroyZDO" branch, with NO
    /// <c>Persistent</c> check): every ZDO this hook appends must first resolve via
    /// <c>ZNetScene.instance.GetPrefab(int)</c> (public, <c>SERVER:82091</c>) — skip and log/count instead of
    /// appending if it does not, so this module's own feed can never hand vanilla's destructive branch a ZDO it
    /// would otherwise not have reached. See <see cref="Patch_ZNetScene_CreateObject_Containment"/> for the companion guard
    /// against a prefab that RESOLVES but whose component throws during instantiate.
    /// </summary>
    [HarmonyPatch(typeof(ZNetScene), "CreateObjects")]
    internal static class Patch_ZNetScene_CreateObjects_ObjectFeed
    {
        private static readonly HashSet<ZDO> s_seen = new HashSet<ZDO>();
        private static readonly List<ZDO> s_tempNear = new List<ZDO>();
        private static readonly List<ZDO> s_tempDistant = new List<ZDO>();

        // "Just this one zone" — near=0/far=0 makes FindSectorObjects put exactly sector's own objects into the
        // near list and nothing into distant (confirmed this session, SERVER:77189-77223: the unconditional
        // FindObjects(sector,...) call happens regardless of NearSimulationDistance; the near-ring loop only
        // runs for i>=1; the distant loop's own start index (num2) never goes below 1, so with
        // NearSimulationDistance=0 it never executes either). Matches the blueprint's own per-zone gather.
        private static readonly SimulationDistance s_singleZoneDistance = new SimulationDistance(0, 0, classic: false);

        [HarmonyPrefix]
        internal static void Prefix(List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects)
        {
            if (!ZoneSimConfig.ServerZoneSimFlag.Active) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            try
            {
                RunFeed(currentNearObjects, currentDistantObjects);
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(Patch_ZNetScene_CreateObjects_ObjectFeed), e);
            }
        }

        private static void RunFeed(List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                RunFeedTimed(currentNearObjects, currentDistantObjects);
            }
            finally
            {
                Telemetry_ZoneSim.SetObjectFeedMs(stopwatch.Elapsed.TotalMilliseconds);
            }
        }

        private static void RunFeedTimed(List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects)
        {
            s_seen.Clear();
            foreach (var z in currentNearObjects) s_seen.Add(z);
            foreach (var z in currentDistantObjects) s_seen.Add(z);

            ZoneCap.BeginTick();

            foreach (Vector2s zone in PokeRingDriver.ActiveZones())
            {
                if (ZoneHealth.IsQuarantined(zone)) continue; // see InstantiateContainment — a prefab in this
                                                                // zone has already thrown once; never re-offer it.

                s_tempNear.Clear();
                s_tempDistant.Clear();
                ZDOMan.instance.FindSectorObjects(zone, s_singleZoneDistance, s_tempNear, s_tempDistant);

                int estimate = s_tempNear.Count + s_tempDistant.Count;
                if (!ZoneCap.CanInclude(zone, estimate)) continue;

                AppendFiltered(currentNearObjects, s_tempNear, isNear: true);
                AppendFiltered(currentDistantObjects, s_tempDistant, isNear: false);
            }

            GraceHold.TickAndReappend(s_seen, currentNearObjects, currentDistantObjects);
        }

        private static void AppendFiltered(List<ZDO> target, List<ZDO> candidates, bool isNear)
        {
            foreach (var zdo in candidates)
            {
                if (!s_seen.Add(zdo)) continue; // de-dup: already in vanilla's own list, or already appended this tick

                int prefabHash = zdo.GetPrefab();
                if (ZoneHealth.IsPoisoned(prefabHash))
                {
                    continue; // a component on this prefab already threw once this process — never re-offer it (no log/count here; InstantiateContainment already logged the one occurrence)
                }
                if (ZNetScene.instance.GetPrefab(prefabHash) == null)
                {
                    Telemetry_ZoneSim.ObjectsSkippedMissingPrefab.Increment();
                    MissingPrefabLog.WarnOnce(prefabHash, zdo.GetPosition());
                    continue;
                }

                target.Add(zdo);
                Telemetry_ZoneSim.ObjectsAppendedTotal.Increment();

                // Only a ZDO that is BOTH non-Persistent AND currently owned by this process can ever reach
                // RemoveObjects's destroy branch (SERVER:82267-82269) — that's the only category the grace-hold
                // needs to protect against ordinary churn; a Persistent or remotely-owned ZDO is never at risk.
                if (!zdo.Persistent && zdo.IsOwner()) GraceHold.Refresh(zdo, isNear);
            }
        }
    }

    /// <summary>Once-per-prefab-hash throttle for the "missing prefab" warning — shared shape with
    /// <see cref="ZoneHealth"/>'s own once-per-prefab logging, so a content-mod gap logs once at startup-of-
    /// discovery rather than every ~33ms this hook re-encounters the same unresolvable prefab.</summary>
    internal static class MissingPrefabLog
    {
        private static readonly HashSet<int> s_logged = new HashSet<int>();

        public static void WarnOnce(int prefabHash, Vector3 position)
        {
            lock (s_logged)
            {
                if (!s_logged.Add(prefabHash)) return;
            }
            NoodlesLog.Warning($"skipping unresolved prefab {prefabHash.StringFromHash()} near {position} — this " +
                "server is likely missing a content mod a client near it has (operational alert, not data loss: " +
                "the ZDO is simply never fed to this module's own instantiation, so vanilla's destructive " +
                "invalid-prefab branch is never reached for it).");
        }
    }
}
