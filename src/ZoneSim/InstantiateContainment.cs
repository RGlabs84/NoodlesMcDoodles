using System;
using System.Collections.Generic;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.ZoneSim
{
    /// <summary>
    /// Containment for third-party mod components during server-side instantiation (task brief "LIVE-SERVER
    /// CONTEXT" — not itself a docs/design/04 lever, but a mandatory addition this module's Z3/Hook 2 needs to
    /// be safe on Proxima's real ~47-mod stack). Before this module existed, <c>ZNetScene.CreateObject</c>'s
    /// <c>Instantiate</c> call (<c>SERVER:82042-82067</c>/<c>CLIENT:...</c>, no try/catch of its own) only ever
    /// ran for content near the server's one permanently-void-ocean reference position — nothing a player ever
    /// built. Z3 is the first thing that makes the SERVER instantiate arbitrary players' real bases, which
    /// means every other mod's own <c>MonoBehaviour</c> on those prefabs now runs its <c>Awake</c>/<c>Start</c>
    /// on a headless process for the first time too — no camera, no <c>AudioListener</c>,
    /// <c>Player.m_localPlayer</c> permanently null.
    ///
    /// Two distinct hazards this file closes:
    /// <list type="number">
    /// <item><b>Stuck static flags.</b> <c>CreateObject</c> sets <c>ZNetView.m_useInitZDO</c>/<c>m_initZDO</c>
    /// (both <c>public static</c>, <c>SERVER:82591</c>/<c>82593</c>) before calling <c>Instantiate</c>, and only
    /// clears them in the lines AFTER it (<c>SERVER:82060-82066</c>). If <c>Instantiate</c> — or any component's
    /// <c>Awake</c>/<c>Start</c> it triggers — throws a .NET exception that propagates out, those two clears
    /// never run, and the NEXT <c>ZNetView.Awake()</c> ANYWHERE in the process (a totally unrelated object) could
    /// pick up the stale <c>m_initZDO</c> and bind to the wrong ZDO. This is closed unconditionally, even with
    /// the feature off, since a caught exception here always means vanilla's own cleanup lines were skipped.</item>
    /// <item><b>Repeated destroy-branch exposure.</b> <c>CreateObjectsSorted</c>/<c>CreateDistantObjects</c>
    /// treat "CreateObject returned null" as "invalid prefab" and, server-side, call
    /// <c>item.SetOwner(...); ZDOMan.instance.DestroyZDO(item);</c> — with NO <c>Persistent</c> check
    /// (<c>SERVER:82184-82188</c>/<c>82220-82224</c>, the same branch Hook 2's mandatory missing-prefab guard
    /// already prevents this module from reaching for a genuinely unresolvable prefab). A prefab whose
    /// component throws on instantiate but DOES otherwise resolve would hit that same branch, and — unlike a
    /// missing prefab — could recur every ~33ms (30Hz) tick, and could in principle be a <c>Persistent</c> ZDO
    /// (a player-placed structure using a third-party prefab). So this Finalizer must NEVER turn an exception
    /// into a null result: that would make the server permanently delete the object over one component's bug
    /// (lead review 2026-09-28: the first version did exactly that). It rethrows instead. A propagating
    /// exception aborts the caller's loop BEFORE its destroy branch, which is vanilla's own behaviour; the object
    /// is simply tried again later. Poisoning the prefab and quarantining the zone stop this module's own feed
    /// from offering it again, so any retries (and their log noise) come only from vanilla's own list.</item>
    /// </list>
    ///
    /// Whether a third-party <c>MonoBehaviour</c>'s <c>Awake</c>/<c>Start</c> exception during
    /// <c>UnityEngine.Object.Instantiate</c> actually propagates as a catchable .NET exception to the caller,
    /// or is isolated internally by Unity's own per-component engine dispatch, is Unity engine runtime
    /// behaviour, not visible in Valheim's own IL — not resolvable from the decompile alone. This Finalizer is
    /// defensive insurance that costs nothing on the (expected-common) success path and closes both hazards
    /// above if it DOES propagate; see docs/impl/ZONESIM-IMPL.md "known risks" for the live-test-only residual.
    /// </summary>
    [HarmonyPatch(typeof(ZNetScene), "CreateObject")]
    internal static class Patch_ZNetScene_CreateObject_Containment
    {
        [HarmonyFinalizer]
        internal static Exception Finalizer(ZDO zdo, Exception __exception)
        {
            if (__exception == null) return null; // success path — nothing to do, zero added cost

            // Hazard 1: always restore the static flags, REGARDLESS of Features.ServerZoneSim — a stuck flag
            // corrupts the next Awake() anywhere in the process, whether or not this module is the one that
            // triggered the original instantiate.
            try
            {
                ZNetView.m_useInitZDO = false;
                ZNetView.m_initZDO = null;
            }
            catch (Exception cleanupEx)
            {
                // Writing two static fields should never itself throw; caught anyway per this mod's absolute
                // "never let a patch body escape an exception" rule.
                NoodlesLog.PatchException(nameof(Patch_ZNetScene_CreateObject_Containment) + ".StaticFieldCleanup", cleanupEx);
            }

            // V1 FIX (deployment-modes review, BLOCKER, 2026-09-28): runtime authority required alongside the
            // flag for this hazard-2 branch specifically (hazard-1's static-field cleanup above stays
            // unconditional on purpose -- a stuck flag corrupts the next Awake() anywhere in the process,
            // regardless of side). ZNetScene.CreateObject instantiates on a plain client too (ordinary local
            // world generation); without the authority check, a client with the flag on (its own default per
            // the runtime-authority rule) would run ZoneHealth.RecordFailure/telemetry that only means something
            // for THIS module's own server-side feed.
            if (!ZoneSimConfig.ServerZoneSimFlag.Active || !ZoneSimConfig.IsAuthoritativeProcess())
            {
                return __exception; // feature off (or not authoritative): re-throw, matching vanilla's own (unknown) behaviour
            }

            try
            {
                int prefabHash = 0;
                Vector2s zone = default;
                try { prefabHash = zdo != null ? zdo.GetPrefab() : 0; zone = zdo != null ? zdo.GetSector() : default; }
                catch { /* best-effort attribution only — never let this secondary read fail the containment path */ }

                Telemetry_ZoneSim.InstantiateExceptionsCaught.Increment();
                ZoneHealth.RecordFailure(prefabHash, zone, __exception);
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(Patch_ZNetScene_CreateObject_Containment), e);
            }

            // Rethrow, never swallow: a null result here would make the server's caller DestroyZDO the object
            // (see the class remarks). Propagating aborts that caller before its destroy branch, as in vanilla.
            return __exception;
        }
    }

    /// <summary>
    /// Tracks prefabs that have ever thrown during server-side instantiation, and the zones that contain them.
    /// Poisoning is immediate (threshold of ONE failure, not "keeps failing") — deliberately more conservative
    /// than the task brief's own "if a prefab keeps failing" phrasing: the cost of a false-positive quarantine
    /// is low (that zone simply isn't server-simulated; <see cref="ZoneSimGate"/> already treats "not
    /// simulated" as the safe default) while the benefit is the tightest possible bound on how many times a
    /// potentially-destructive retry can ever be attempted for the same prefab — see
    /// <see cref="Patch_ZNetScene_CreateObject_Containment"/>'s class remarks, hazard 2.
    ///
    /// Sticky for the process lifetime, matching <c>FeatureFlag.ForceOff</c>'s own "unlike the kill switch,
    /// nothing auto-clears it" philosophy (CORE-API.md) — a prefab that broke once on this content-mod stack
    /// is not expected to un-break itself without an admin fixing the mod stack and restarting.
    /// </summary>
    internal static class ZoneHealth
    {
        private static readonly HashSet<int> s_poisonedPrefabs = new HashSet<int>();
        private static readonly HashSet<Vector2s> s_quarantinedZones = new HashSet<Vector2s>();
        private static readonly HashSet<int> s_loggedPrefabs = new HashSet<int>();
        private static readonly object s_lock = new object();

        public static void RecordFailure(int prefabHash, Vector2s zone, Exception cause)
        {
            bool firstTimeForPrefab;
            lock (s_lock)
            {
                firstTimeForPrefab = s_poisonedPrefabs.Add(prefabHash);
                s_quarantinedZones.Add(zone);
            }

            if (firstTimeForPrefab || s_loggedPrefabs.Add(prefabHash))
            {
                NoodlesLog.Warning($"instantiate exception from prefab {prefabHash.StringFromHash()} in zone " +
                    $"{zone} — poisoning this prefab and quarantining this zone from server-side simulation " +
                    $"(logged once per prefab; see zonesim.instantiateExceptionsCaught for the running count): {cause}");
            }

            Telemetry_ZoneSim.SetPrefabsPoisoned(PoisonedCount);
            Telemetry_ZoneSim.SetZonesQuarantined(QuarantinedCount);
        }

        public static bool IsPoisoned(int prefabHash)
        {
            lock (s_lock) return s_poisonedPrefabs.Contains(prefabHash);
        }

        public static bool IsQuarantined(Vector2s zone)
        {
            lock (s_lock) return s_quarantinedZones.Contains(zone);
        }

        public static int PoisonedCount { get { lock (s_lock) return s_poisonedPrefabs.Count; } }
        public static int QuarantinedCount { get { lock (s_lock) return s_quarantinedZones.Count; } }
    }
}
