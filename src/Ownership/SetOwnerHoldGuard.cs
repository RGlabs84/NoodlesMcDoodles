using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.Ownership
{
    /// <summary>
    /// O7 / H7 (docs/design/03-ownership-and-handover.md) — <c>ZDO.SetOwner(long)</c>'s hold-guard
    /// (SERVER:74674-74680, confirmed this pass: <c>if (ZDOExtraData.GetOwner(m_uid) != uid) { SetOwnerInternal(uid); IncreaseOwnerRevision(); }</c>)
    /// and this module's installation of <see cref="OwnershipApi.SetOwnerProvider"/>. Every OTHER hook in this
    /// module that changes ownership (O6's claim pass, O8's handover, O9's reassignment) routes its writes
    /// through <see cref="AuthorizedSetOwner"/> (single ZDO) or <see cref="RunAuthorizedBulkClaim"/> (O6's bulk
    /// claim call into vanilla's own <c>ReleaseNearbyZDOS</c>) rather than calling <c>zdo.SetOwner</c> directly.
    ///
    /// <b>Ships in "Shared groundwork" (DESIGN.md M2), not gated on the zone-claim engine</b> — <c>ZDO.SetOwner</c>
    /// runs in unmodified vanilla regardless of whether the server has ever claimed anything, so this file has
    /// no functional dependency on <see cref="ZoneClaimSweep"/>; O9 (also M2) depends on it, and Phase B core
    /// (M3, <see cref="ZoneClaimSweep"/>/O8) reuses it rather than re-implementing hold semantics.
    ///
    /// <b>Two confirmed, already-installed third-party skip-capable prefixes on this exact method</b> — both the
    /// user's own mods, both decompiled directly against Proxima Maxi's real plugin DLLs this pass (not just
    /// cited from research/13-mod-compat-census.md):
    /// <list type="bullet">
    /// <item><c>Wubarrk-GetOffMyLawn</c> <c>OwnershipHold.SetOwnerGuard.Prefix</c> — vetoes (<c>return false</c>)
    /// iff GetOffMyLawn currently holds THIS SPECIFIC ZDOID in its own <c>_holds</c> dictionary AND the target
    /// <c>uid</c> is not the caller's own session id. Concretely: <c>if (_holds.Count == 0 || _releasing ||
    /// ZDOMan.instance == null) return true; if (uid == ZDOMan.GetSessionID()) return true; return
    /// !_holds.ContainsKey(__instance.m_uid);</c> — GetOffMyLawn NEVER blocks a transfer TO the server's own
    /// session id (its own <c>Claim()</c> always targets the server first), only a transfer AWAY from the
    /// server while it independently holds that exact ZDO (its own timed hand-back path, <c>Update()</c>,
    /// <c>_releasing=true</c> around its own call). No <c>[HarmonyPriority]</c> attribute — default priority
    /// (400).</item>
    /// <item><c>Wubarrk-Wonderland</c> <c>ItemFlow.ZdoSetOwnerPatch.Prefix</c> — vetoes iff ANY of four
    /// sub-engines' own <c>ShouldBlockOwnerChange(zdo, uid)</c> returns true: <c>WaterBuoyancyEngine</c>
    /// (floating dropped items, only when <c>AllItemsFloatEnabled</c>), <c>VacuumEngine</c> (item vacuuming,
    /// only within ~3s of its own hold), <c>Core.Data.HullBorrow</c> (ship-hull building-permission borrow,
    /// only while a borrow is pending handback) and <c>Security.TerrainGuard</c> (anti-cheat terrain-write
    /// judging, only within its own short judging window). ALL FOUR confirmed this pass to explicitly return
    /// <c>false</c> (never block) whenever the target uid is <c>ZDOMan.GetSessionID()</c> — Wonderland, like
    /// GetOffMyLawn, never blocks a transfer TO the server, only away from it, and only while that specific
    /// sub-engine's own narrow, timed hold on that exact ZDO is active. No <c>[HarmonyPriority]</c> on
    /// <c>ZdoSetOwnerPatch</c> either.</item>
    /// </list>
    /// <b>Net effect for this module</b>: every write this module makes THAT TARGETS THE SERVER'S OWN SESSION ID
    /// (O6's claims, O9's server-mediated re-derivation is N/A here since O9 never targets the server — see
    /// LatencyAwareOwnership.cs) is structurally never vetoed by either mod. The real collision surface is O8's
    /// hand-BACK (target = a player uid, away from the server) and O9's reassignment (target = a different
    /// player uid, away from the current player owner) landing on a ZDO one of these mods' own narrow, timed
    /// holds happens to cover at that exact moment — rare, real, and exactly what
    /// <see cref="AuthorizedSetOwner"/>'s postcondition check exists to catch (never assumed away). Release to
    /// nobody (uid 0: the unload release, ClaimScanner's stray release, PickableRelease) is the commonest such
    /// collision, because those holds refuse every non-server target, 0 included. That is a hold doing its job, so
    /// since 1.0.12 <see cref="AuthorizedSetOwner"/> counts it as <c>ownership.o11.releaseRefusedByHolder</c>, not as a
    /// <see cref="ModeQuarantine"/> veto.
    /// </summary>
    internal static class SetOwnerHoldGuard
    {
        // --- Config --------------------------------------------------------------------------------------------

        private static ConfigEntry<float> s_holdTimeoutAlert;

        // --- Bypass state (both main-thread only -- every authorized call site in this module runs from a
        // Harmony patch on a server-tick method, never off-thread) ----------------------------------------------

        // RED-TEAM FIX carried over from docs/design/03 H7 (RT1 minor #8): scoped to the specific ZDOID being
        // transferred, not a blanket "a call is in flight" bool -- a reentrant SetOwner on a DIFFERENT ZDO during
        // the same authorized call must still be vetoed if that different ZDO is separately held.
        [ThreadStatic] private static ZDOID? s_authorizedZDOID;

        // Deliberately a DIFFERENT, call-scoped (not ZDOID-scoped) bypass -- see RunAuthorizedBulkClaim's own
        // remarks for exactly why a blanket flag is safe here despite the RT1 minor #8 fix above choosing
        // per-ZDOID scoping for the single-object case.
        [ThreadStatic] private static bool s_authorizedBulkClaimInFlight;

        internal static bool AuthorizedBulkClaimInFlight => s_authorizedBulkClaimInFlight;

        // --- Hold set: HashSet<ZDOID> + touch time (docs/design/03 H7/H8 sketches both reference
        // OwnershipHold.IsHeld/Release as a real, queryable set, not merely a bypass flag) ------------------------

        private static readonly Dictionary<ZDOID, float> s_holds = new Dictionary<ZDOID, float>();

        internal static bool IsHeld(ZDOID id) => s_holds.ContainsKey(id);

        internal static void Release(ZDOID id) => s_holds.Remove(id);

        private static void Claim(ZDOID id)
        {
            if (!s_holds.ContainsKey(id)) s_holds[id] = Time.time;
        }

        /// <summary>
        /// Called once per <see cref="ZoneClaimSweep"/> cycle (~O3 cadence -- see that file's RunClaimCycle) --
        /// not a second periodic driver (CORE-API.md "don't start your own timer"), reusing a tick this module
        /// already registers for a different purpose. Every hold this file itself takes is released in the SAME
        /// synchronous try/finally that took it (no `await`, no cross-frame gap anywhere in this module), so in
        /// normal operation this should never find anything -- it exists purely as the paranoid, observable
        /// backstop docs/design/03 asks for ("bound total hold duration, alert on any hold outliving a sane
        /// timeout"), not a mechanism this module's own normal path relies on.
        /// </summary>
        internal static void SweepStaleHolds()
        {
            if (s_holds.Count == 0) return;
            float timeout = Mathf.Clamp(s_holdTimeoutAlert.Value, 5f, 60f);
            float now = Time.time;
            List<ZDOID> stale = null;
            foreach (var kv in s_holds)
            {
                if (now - kv.Value > timeout) (stale ??= new List<ZDOID>()).Add(kv.Key);
            }
            if (stale == null) return;
            foreach (var id in stale)
            {
                s_holds.Remove(id);
                Telemetry.GetCounter("ownership.deadZoneStalls").Increment();
                Telemetry.GetCounter("ownership.holdOutliers").Increment();
                NoodlesLog.Warning($"O7: hold on {id} outlived {timeout:F0}s (should be sub-millisecond in normal " +
                    "operation) -- force-cleared. This is a stall, not data loss, but indicates a bug in this " +
                    "module's own claim/handover code (an exception between a hold's Claim and Release that " +
                    "somehow escaped its own try/finally) and should be investigated.");
            }
        }

        internal static void ReleaseAllHolds()
        {
            int count = s_holds.Count;
            s_holds.Clear();
            if (count > 0)
            {
                Telemetry.GetCounter("ownership.killSwitch.holdsFlushed").Add(count);
                NoodlesLog.Info($"SetOwnerHoldGuard: flushed {count} held ZDOID(s) on kill-switch disable.");
            }
        }

        // --- Authorized writers ----------------------------------------------------------------------------------

        private static readonly Counter s_releaseRefusedByHolder =
            Telemetry.GetCounter(OwnershipApi.ReleaseRefusedCounterName);

        /// <summary>
        /// The ONE place this module (O6 excepted -- see <see cref="RunAuthorizedBulkClaim"/>) changes a ZDO's
        /// owner. Takes a hold, authorizes exactly this ZDOID, calls vanilla's own <c>ZDO.SetOwner</c>, releases
        /// both, then verifies the write actually landed rather than assuming success -- RED-TEAM FIX (docs/design/03
        /// RT2 major, see class remarks): a third-party skip-capable prefix can silently no-op the call.
        /// Installed as <see cref="OwnershipApi.SetOwnerProvider"/> (CORE-API.md cross-module seam) so every OTHER
        /// module that changes ownership gets this same guarantee for free.
        /// <para>1.0.12: the direction of a refused write decides what it means. A hand-back to nobody (newUid 0)
        /// that a mod refuses is that mod holding the object on purpose (Wonderland's ground-stack hold while a
        /// stack settles, GetOffMyLawn's door hold): not a mod fighting near-field claiming, and
        /// <see cref="ModeQuarantine"/>'s retreat to SharedFar cannot make it let go. Those count as
        /// <c>ownership.o11.releaseRefusedByHolder</c>. Every other refused write (a claim to the server, a
        /// handover to a peer) stays <c>ownership.setOwnerVetoed</c>, which feeds ModeQuarantine. Live Proxima
        /// 2026-10-01 14:38: 57 refused releases of harvested onions in 60 s latched the server into SharedFar for a
        /// day.</para>
        /// </summary>
        internal static bool AuthorizedSetOwner(ZDO zdo, long newUid, string reason)
        {
            if (zdo == null) return false;
            ZDOID id = zdo.m_uid;

            Claim(id);
            s_authorizedZDOID = id;
            try
            {
                zdo.SetOwner(newUid);
            }
            finally
            {
                s_authorizedZDOID = null;
                Release(id);
            }

            bool ok = zdo.GetOwner() == newUid;
            if (!ok)
            {
                bool release = newUid == 0L;
                if (release) s_releaseRefusedByHolder.Increment();
                else Telemetry.GetCounter("ownership.setOwnerVetoed").Increment();
                // 1.0.3: names the prefab and the owner it kept (live Proxima 2026-09-29: 12 unnamed release refusals in
                // one second), rate-limited so a big base's release can't flood the log; the counters count all.
                GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
                string what = $"AuthorizedSetOwner({id} prefab={(prefab != null ? prefab.name : zdo.GetPrefab().ToString())} " +
                    $"-> {newUid}, kept by {zdo.GetOwner()}, reason={reason})";
                if (!release)
                {
                    NoodlesLog.WarningRateLimited(NoodlesLogLevel.Verbose, "authorizedSetOwner.refused", what + " did not take -- likely vetoed by " +
                        "GetOffMyLawn's or Wonderland's own ZDO.SetOwner prefix (see class remarks).", 10, 60f);
                }
                else if (prefab != null && (prefab.GetComponent<Pickable>() != null || prefab.GetComponent<TombStone>() != null))
                {
                    // A Pickable should never be server-held ("pickables are never taken": the next harvest would run
                    // Pickable.RPC_Pick on a server with no local player), and a server-held tombstone is the 1.0.11
                    // y=-400 symptom (the dead player's items). No known holder refuses either, so a refusal is an
                    // anomaly and stays a Tier-1 warning.
                    NoodlesLog.WarningRateLimited("authorizedSetOwner.protectedReleaseRefused", what + " refused: a Pickable " +
                        "or TombStone stays server-held because another mod holds it (not counted toward ModeQuarantine).", 5, 60f);
                }
                else
                {
                    NoodlesLog.DetailRateLimited("authorizedSetOwner.releaseRefused", what + " refused: another mod holds " +
                        "this object on purpose (expected; not counted toward ModeQuarantine).", 10, 60f);
                }
            }
            return ok;
        }

        /// <summary>
        /// O6's own bulk claim call (<see cref="ZoneClaimSweep"/>) invokes vanilla's <c>ReleaseNearbyZDOS</c>
        /// directly, which calls <c>tempNearObject.SetOwner(uid)</c> itself, per-object, inside its own loop
        /// (SERVER:76969) -- there is no single ZDOID to authorize in advance the way
        /// <see cref="AuthorizedSetOwner"/> does. A CALL-SCOPED (not ZDOID-scoped) bypass is deliberately used
        /// here instead, safely, for reasons the single-object RT1 minor #8 fix does NOT undermine: this
        /// delegate call is one single, synchronous, non-reentrant vanilla method with no side call into any
        /// OTHER SetOwner-touching code anywhere in its body -- every ZDO it can possibly touch is, by
        /// construction, already scoped to the one zone <see cref="ZoneClaimSweep.ClaimZone"/> is claiming (the
        /// exact set <see cref="ZoneClaimSweep.TryPrepareClaim"/> already pre-scanned and ReclaimGate-approved),
        /// so there is no OTHER, unrelated ZDO that could "accidentally" ride this bypass window the way RT1
        /// minor #8 worried about for a naive blanket flag on the single-object path.
        /// </summary>
        internal static void RunAuthorizedBulkClaim(Action bulkCall)
        {
            s_authorizedBulkClaimInFlight = true;
            try
            {
                bulkCall();
            }
            finally
            {
                s_authorizedBulkClaimInFlight = false;
            }
        }

        // --- Patch -----------------------------------------------------------------------------------------------

        /// <summary>
        /// <c>[HarmonyPriority(Priority.First)]</c> per docs/design/03's own explicit recommendation ("belt, not
        /// suspenders") -- Harmony ANDs every bool-returning prefix's result regardless of execution order, so
        /// this does NOT change whether GetOffMyLawn's or Wonderland's own prefix vetoes a given call (see class
        /// remarks: that depends only on THEIR OWN hold state, not on ours), only keeps this the first prefix
        /// Harmony evaluates among the crowd on this method, matching the same defensive hygiene G1-L1
        /// (Negotiation.cs) already applies to <c>RPC_PeerInfo</c>.
        /// </summary>
        [HarmonyPatch(typeof(ZDO), nameof(ZDO.SetOwner))]
        [HarmonyPriority(Priority.First)]
        internal static class Patch_ZDO_SetOwner_HoldGuard
        {
            private static bool s_compatCheckLogged;

            static bool Prefix(ZDO __instance, long uid)
            {
                // CLIENT-REVIEW FIX (minor, client-dll-review.json): every sibling OwnershipAuthority-tagged
                // patch in this module (SimulationDistanceCeiling, ZoneClaimSweep, OwnershipLossWatch,
                // ClientRefPosCadence, OwnershipHandoverRpc) also checks runtime authority, not just the
                // config flag -- this one didn't. Behaviorally a no-op on a plain client either way (every
                // writer of the state this reads -- Claim()/s_authorizedZDOID/s_authorizedBulkClaimInFlight --
                // is itself gated on ZNet.instance.IsServer() at its own call site), but without this it still
                // ran its full body -- a ThreadStatic compare, a Dictionary<ZDOID,float> lookup, a try/catch,
                // and once a Harmony.GetPatchInfo scan -- on every single ZDO.SetOwner call in the client
                // process, including every time the player's own character/inventory/pickup ownership
                // changes, for no reason. Default-ON flags are also on in players' own configs (binding rule),
                // so this must not rely on OwnershipAuthority.Active alone.
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return true;

                // FIXER FIX (early-ownership / dl-ownership-gameplay, both major -- same root cause, same fix):
                // AuthorizedSetOwner (this file, below) takes its hold unconditionally for EVERY writer that
                // calls through OwnershipApi.SetOwner, including O9's own RunReassignmentPass and O10's own
                // RunO10HandbackCheck (LatencyAwareOwnership.cs) -- both of which run whenever
                // "O9.Active || O10SharedAreaOwnership.Active", independent of OwnershipAuthority.Active by
                // explicit design (LatencyAwareOwnership.cs class remarks: "independent of
                // OwnershipAuthority.Active -- this is the interim mitigation... standalone"). Gating this
                // Prefix's entire hold-check body on OwnershipAuthority.Active ALONE meant that whenever it was
                // off (a supported admin config, or FeatureStandDown force-off'ing the many patch classes
                // registered under that one feature name while O9/O10 -- separate feature names -- stayed
                // healthy) every hold O9/O10 took via AuthorizedSetOwner went completely unprotected: a
                // reentrant/competing ZDO.SetOwner on that exact ZDOID would never even reach the
                // s_authorizedZDOID/IsHeld checks below. Every sibling OwnershipAuthority-tagged patch in this
                // module already ORs in O10SharedAreaOwnership.Active for the identical reason (ZoneClaimSweep.cs's
                // G3-L1 postfix, OwnershipLossWatch.cs's revision scan) -- this Prefix is the one place that
                // pattern was missing, and it additionally needs O9.Active explicitly (not just
                // O10SharedAreaOwnership.Active like those siblings) because O9's own player-to-player
                // reassignment targets a ZDO that is never server-owned, so no "is this zone server-held" check
                // would ever cover it.
                if (!Features_Ownership.OwnershipAuthority.Active && !Features_Ownership.O9.Active &&
                    !O10SharedAreaOwnership.Active) return true; // all three off -> vanilla runs unmodified

                if (!s_compatCheckLogged)
                {
                    // First real call only (deferred from Init/Awake -- see class remarks: by the time any
                    // SetOwner call happens a world is loaded, which means every other BepInEx plugin, whatever
                    // its own load order relative to this one, has already finished its own Awake() and applied
                    // its own Harmony patches).
                    s_compatCheckLogged = true;
                    try { LogForeignPatches(); }
                    catch (Exception e) { NoodlesLog.PatchException("SetOwnerHoldGuard compat check", e); }
                }

                try
                {
                    if (s_authorizedBulkClaimInFlight) return true; // O6's own bulk claim (see RunAuthorizedBulkClaim)
                    if (s_authorizedZDOID == __instance.m_uid) return true; // this module's own authorized transfer of THIS zdo
                    if (IsHeld(__instance.m_uid)) return false; // held by someone else's call -- veto every other caller
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_ZDO_SetOwner_HoldGuard), e);
                }
                return true;
            }

            private static void LogForeignPatches()
            {
                var info = Harmony.GetPatchInfo(AccessTools.Method(typeof(ZDO), nameof(ZDO.SetOwner)));
                if (info?.Prefixes == null || info.Prefixes.Count == 0) return;

                var foreign = info.Prefixes.Where(p => p.owner != PluginInfo.PLUGIN_GUID).ToList();
                if (foreign.Count == 0) return;

                string names = string.Join(", ", foreign.Select(p => $"{p.owner} (priority {p.priority})"));
                NoodlesLog.Verbose($"O7: {foreign.Count} foreign patch(es) already present on ZDO.SetOwner: {names} -- " +
                    "expected (GetOffMyLawn/Wonderland, see SetOwnerHoldGuard.cs class remarks for exactly what " +
                    "each does and when it can veto us). AuthorizedSetOwner's own postcondition check " +
                    "(ownership.setOwnerVetoed for a claim or handover, ownership.o11.releaseRefusedByHolder for a " +
                    "hand-back to nobody) is what actually detects a real veto, not this log line.");
            }
        }

        // --- Init --------------------------------------------------------------------------------------------------

        [NoodlesModuleInit]
        internal static void Init()
        {
            s_holdTimeoutAlert = NoodlesConfig.File.Bind("OwnershipAuthority", "O7HoldTimeoutAlert", 10.0f,
                new ConfigDescription("Ops alert threshold only -- not a behaviour change. A normal hold clears " +
                    "in well under a millisecond (see SweepStaleHolds remarks); this just bounds how long a bug " +
                    "could leave one ZDOID un-ownable before an operator is warned in the log.",
                    new AcceptableValueRange<float>(5f, 60f)));

            PatchRegistry.Register("OwnershipAuthority", typeof(Patch_ZDO_SetOwner_HoldGuard), PatchSide.Both);

            // CORE-API.md cross-module seam: every OTHER module that changes ownership (and O8/O9 in this one)
            // gets the hold-guard + postcondition-check guarantee through this, not by calling zdo.SetOwner directly.
            OwnershipApi.SetOwnerProvider = AuthorizedSetOwner;

            KillSwitch.RegisterFlushCallback("Ownership.SetOwnerHoldGuard", ReleaseAllHolds);
        }
    }
}
