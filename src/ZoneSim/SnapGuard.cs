using System;
using System.Collections.Generic;
using HarmonyLib;
using NoodlesMcDoodles.Core;

namespace NoodlesMcDoodles.ZoneSim
{
    /// <summary>
    /// Hook 7 (Z7) — docs/design/04: <c>SnapToGround.SnappAll()</c> defer-instead-of-drain guard.
    ///
    /// <c>SnapToGround.SnappAll()</c> — public static, <c>SERVER:142635-142649</c>/<c>CLIENT:143551-143565</c>,
    /// confirmed identical body both builds: unconditionally <c>Heightmap.ForceGenerateAll()</c>, then iterates
    /// the entire private static <c>m_allSnappers</c> list calling <c>Snap()</c> on every entry and
    /// <c>Clear()</c>s the list. <c>Snap()</c> (public) samples ground height at the snapper's OWN position via
    /// <c>ZoneSystem.instance.GetGroundHeight</c> — if that zone's terrain isn't real yet, this reads whatever
    /// the (possibly not-yet-baked) heightmap returns, mis-placing the object. <c>HaveUnsnapped()</c> has zero
    /// callers anywhere in either assembly (confirmed by grep this session) — nothing today polls "are there
    /// still deferred snappers waiting," which is exactly why this hook needs its own periodic re-trigger.
    ///
    /// Under vanilla this rarely matters (one static reference position, terrain-before-snap ordering usually
    /// already holds by the time anything calls <c>SnappAll</c>). Under this module, Hook 1's poke-ring driver
    /// triggers world-gen far more often and across far more of the map — every trader, every placed structure,
    /// anywhere any peer's ring sweeps past mid-terrain-bake, is a mis-snap candidate specifically because of
    /// this module.
    /// </summary>
    internal static class SnapGuard
    {
        // Static field, no by-ref caching needed here (unlike an instance field, there's only ever one to
        // resolve) — a plain cached FieldInfo + GetValue(null) each call, matching ZoneSystemAccess's own style.
        private static readonly System.Reflection.FieldInfo s_allSnappersField =
            AccessTools.Field(typeof(SnapToGround), "m_allSnappers");

        [HarmonyPatch(typeof(SnapToGround), "SnappAll")]
        internal static class Patch_SnapToGround_SnappAll_Defer
        {
            [HarmonyPrefix]
            internal static bool Prefix()
            {
                // V1 FIX (deployment-modes review, BLOCKER, 2026-09-28): ServerZoneSimFlag.Active alone used to
                // gate this -- and since Features.ServerZoneSim.Active now defaults ON, every modded PLAYER's own
                // client config has it true too. SnappAll() is NOT server-only (unlike CreateGhostZones): a plain
                // client's own local ZoneSystem calls it in ordinary play (SpawnLocation, SERVER:115203/
                // CLIENT:116014 area, confirmed both builds -- any local location generation as that client
                // explores). With the flag alone, this prefix would replace vanilla's own drain-all on that
                // client too, and a replayed Snap() can SetPosition on the CLIENT'S OWN ZDOs -- runtime authority
                // is required in addition, never the flag alone (see ZoneSimConfig.IsAuthoritativeProcess).
                if (!ZoneSimConfig.ServerZoneSimFlag.Active || !ZoneSimConfig.IsAuthoritativeProcess()) return true; // vanilla's own unconditional drain-all

                try
                {
                    return !RunDeferredSnap();
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_SnapToGround_SnappAll_Defer), e);
                    return true; // fail open to vanilla's own drain-all rather than leave snappers stuck
                }
            }
        }

        /// <summary>Returns true if this module handled the call (caller should skip vanilla's own body).</summary>
        private static bool RunDeferredSnap()
        {
            if (s_allSnappersField == null) return false; // reflection target missing — fail open to vanilla
            var all = (List<SnapToGround>)s_allSnappersField.GetValue(null);
            if (all == null) return false;

            if (all.Count == 0) return true; // matches vanilla's own early return; nothing to do

            Heightmap.ForceGenerateAll(); // vanilla's own unconditional first line — kept as-is

            for (int i = all.Count - 1; i >= 0; i--)
            {
                SnapToGround snapper = all[i];
                if (snapper == null) { all.RemoveAt(i); continue; } // destroyed since queued — drop silently, matches vanilla's own eventual Clear()

                if (!IsZoneReady(snapper)) continue; // leave deferred — RetryDeferred below re-enters this same path

                snapper.Snap();
                SetInList(snapper, false); // mirrors vanilla's own per-entry m_inList=false before it Clear()s the whole list
                all.RemoveAt(i);
                Telemetry_ZoneSim.SnapRetriedCount.Increment();
            }

            Telemetry_ZoneSim.SetSnapDeferredCount(all.Count);
            return true;
        }

        private static bool IsZoneReady(SnapToGround snapper)
        {
            if (ZoneSystem.instance == null || !ZoneSystemAccess.IsReady) return false;
            Vector2s zone = ZoneSystem.GetZone(snapper.transform.position);
            // m_zones.ContainsKey, never the stricter public IsZoneLoaded — see ZoneSystemAccess's own remarks;
            // the same OOM-lesson distinction applies here as it does to Hook 2's readiness gate.
            return ZoneSystemAccess.ContainsZone(ZoneSystem.instance, zone);
        }

        private static void SetInList(SnapToGround snapper, bool value)
        {
            AccessTools.FieldRefAccess<SnapToGround, bool>(snapper, "m_inList") = value;
        }

        /// <summary>
        /// Mandatory periodic re-trigger (docs/design/04's own explicit requirement — without this, a deferred
        /// entry could sit permanently un-snapped, since nothing else ever calls <c>SnappAll</c> again on its
        /// own once <c>HaveUnsnapped</c> has zero callers). Called from <see cref="ZoneSimConfig.OnRealZoneTick"/>,
        /// the same cadence as <c>UpdateTickSeconds</c> — re-entering the patched <c>SnappAll</c> above is
        /// exactly equivalent to any other caller invoking it.
        /// </summary>
        internal static void RetryDeferred()
        {
            // V1 FIX: same runtime-authority requirement as the Prefix above -- see its own remarks.
            if (!ZoneSimConfig.ServerZoneSimFlag.Active || !ZoneSimConfig.IsAuthoritativeProcess()) return;
            try { SnapToGround.SnappAll(); }
            catch (Exception e) { NoodlesLog.PatchException(nameof(RetryDeferred), e); }
        }
    }
}
