using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.Ownership
{
    /// <summary>
    /// WP-D (scratchpad/saa/final.md §3/§7/§8) — the frame-budget shedder
    /// <see cref="ZoneClaimSweep.ComputeCandidateZones"/> (this module's other file, same "O11 failbacks"
    /// concern, near-field gate: <c>if (LoadShedder.IsZoneShed(zone)) continue;</c>) has been calling since SAA
    /// WP-A shipped — previously always <c>false</c> (this file's own prior 13-line placeholder). <see
    /// cref="IsZoneShed"/> is the per-zone answer <c>ZoneClaimSweep</c> reads; <see cref="Tick"/> — the one new
    /// call site this task's file scope grants, in <c>NoodlesHeartbeat.Update()</c> — is what keeps it current,
    /// and also drives <see cref="FrameBudget"/> and <see cref="ModeQuarantine"/> (this task's other two owned
    /// files: one new heartbeat line was budgeted, so this is the single fan-out point for all three).
    ///
    /// <b>Mechanism (final.md §3, "v1's frame-budget shedder design... carried forward, with data-loss critic's
    /// Finding 3 applied")</b>: while <see cref="FrameBudget.IsOverBudget"/> is true, once per
    /// <see cref="ZoneClaimSweep.ActiveInterval"/> — the same cadence O6's own claim pass uses, read here only
    /// as a throttle for this file's own round-robin work, never by hooking O6's actual tick — round-robin
    /// through the zones O6 currently holds (<see cref="ServerZoneMembership.Snapshot"/>) that are ALSO
    /// near-field (peer-overlapped): a purely far-band held zone was never near-field-only cost to begin with
    /// (it would be held at <c>SharedFar</c> too), so shedding it would relieve nothing. Up to
    /// <see cref="MaxZonesShedPerCycle"/> NEW zones are marked shed per cycle (mirrors
    /// <c>ZoneClaimSweep.MaxZonesClaimedPerCycle</c>'s own round-robin cap shape); a zone that fails the
    /// physical liveness check below is skipped — deferred to the next-priority zone, NOT counted against the
    /// cap — rather than shed anyway.
    ///
    /// Marking a zone shed does not itself release it — <c>ZoneClaimSweep.RunClaimCycle</c>'s own existing
    /// release-diffing loop (unconditional, every cycle, already comparing <c>ServerZoneMembership.Snapshot()</c>
    /// against that cycle's candidate set) drops the zone from that bookkeeping on its own very next tick, the
    /// instant <c>ComputeCandidateZones</c> stops listing a shed zone as a candidate — reused rather than
    /// duplicated, per this codebase's own "don't reimplement vanilla-adjacent decision logic" discipline.
    /// <b>CORRECTED (round-2 review, "quarantine" lens major finding — the identical overstatement was also
    /// flagged, and fully corrected, in <see cref="ModeQuarantine"/>'s own remarks; see that file for the full
    /// explanation).</b> That diffing loop, verified against the current file this pass, only clears
    /// <c>ServerZoneMembership</c> bookkeeping — it does not itself hand back any object's real
    /// <c>ZDO.m_owner</c>. A shed zone's ZDOs keep their existing owner (the server) until either a connected
    /// peer's own TRUE (unpadded, vanilla) active area naturally reaches them via vanilla's normal per-peer
    /// <c>ReleaseNearbyZDOS</c> sweep, or every peer leaves even the WIDER padded band too, re-qualifying the
    /// zone as genuine far-band and letting O6 re-claim (and re-book-keep) it normally. Harmless for THIS
    /// file's own purpose — shedding only needs to stop the server being treated as artificially "present" for
    /// load-relief purposes, which the bookkeeping-only drop already achieves — but "does that" should not be
    /// read as "the ZDO changes owner immediately." No code change follows from this correction in this file;
    /// the actual per-object release gap it describes lives in <c>ZoneClaimSweep.cs</c>, a file this task does
    /// not own (see <see cref="ModeQuarantine"/>'s remarks for the precise fix needed there).
    ///
    /// <b>Liveness check, run HERE (before shedding), not through <c>ReclaimGate</c> (data-loss critic's
    /// Finding 3, final.md §3)</b>: <c>ReclaimGate.AllowReclaim</c>'s safe default is "refuse everything"
    /// whenever no guard is active/on, which would make load-shedding — a safety VALVE — silently impossible
    /// whenever the relevant <c>Gameplay.HandoverGuard</c> sub-flags happen to be off. That gate answers a
    /// different question ("may we take this from a PRESENT PLAYER") than this one ("would releasing this zone
    /// orphan something physically mid-use right now") — so <see cref="IsZoneBusy"/> checks the same underlying
    /// physical signals <c>HandoverGuard</c>'s own fast paths use (<c>Container.IsInUse()</c>,
    /// <c>Vagon.InUse()</c>, <c>Sadle.HaveValidUser()</c>, <c>Ship.m_players.Count&gt;0</c>) directly,
    /// unconditionally, regardless of which Gameplay flags are on.
    ///
    /// Recovery (<see cref="FrameBudget.IsOverBudget"/> false again) clears every shed zone at once, not
    /// round-robin — unlike shedding (an active, deliberate release with a physical-safety check to run),
    /// un-shedding just stops EXCLUDING a zone from candidacy; <c>ZoneClaimSweep</c>'s own existing
    /// <c>MaxZonesClaimedPerCycle</c> cap still governs how fast anything is actually RE-claimed, so there is no
    /// unbounded-cost risk in lifting every exclusion in one step.
    /// </summary>
    internal static class LoadShedder
    {
        // --- Config ------------------------------------------------------------------------------------------

        private static ConfigEntry<int> s_maxZonesShedPerCycle;

        // --- State (main-thread only -- Tick() only ever runs from NoodlesHeartbeat.Update(), the same thread
        // every other per-tick Ownership state in this module already assumes) ------------------------------

        private static readonly HashSet<Vector2s> s_shed = new HashSet<Vector2s>();
        private static readonly List<Vector2s> s_scratchCandidates = new List<Vector2s>(); // reused per cycle, avoids per-cycle allocation churn
        private static readonly List<ZDO> s_scratchScan = new List<ZDO>(); // reused per zone liveness check
        private static int s_shedRotation;
        private static float s_nextCycleAtRealtime;

        /// <summary>Read by <see cref="ZoneClaimSweep.ComputeCandidateZones"/>. True only while frame budget is
        /// over AND this specific zone was picked by this file's own shedding pass; false for everything else,
        /// including every zone O6 has never held at all -- this file only ever excludes what it explicitly
        /// decided to shed, never anything by omission or default.</summary>
        internal static bool IsZoneShed(Vector2s zone) => s_shed.Contains(zone);

        // --- Reflection: private Ship.m_players -- the exact field Gameplay.HandoverGuard's own
        // CheckVehicleAndContainer already reads this same way; a SEPARATE FieldRef here (not a shared one),
        // since that one is private to that class and this module's "one DLL, many independently-built files"
        // discipline doesn't put a cross-file private accessor in a shared location for a two-line lookup. ---

        private static readonly AccessTools.FieldRef<Ship, List<Player>> s_shipPlayers =
            AccessTools.FieldRefAccess<Ship, List<Player>>("m_players");

        /// <summary>
        /// The one call site <c>NoodlesHeartbeat.Update()</c> gets. Fans out to <see cref="FrameBudget.Tick"/>
        /// and <see cref="ModeQuarantine.Tick"/> (this task's other two owned files) each behind their own
        /// try/catch, so a throw in one WP-D/WP-E mechanism can never silently stop a sibling failback from
        /// running the same frame -- a deliberately higher bar than "one try/catch around the whole line",
        /// since the entire point of this code is to be the failback when something else has already gone
        /// wrong.
        /// </summary>
        internal static void Tick()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            try { FrameBudget.Tick(); }
            catch (Exception e) { NoodlesLog.PatchException("LoadShedder FrameBudget.Tick", e); }

            try { ModeQuarantine.Tick(); }
            catch (Exception e) { NoodlesLog.PatchException("LoadShedder ModeQuarantine.Tick", e); }

            try { TickShedCycle(); }
            catch (Exception e) { NoodlesLog.PatchException("LoadShedder TickShedCycle", e); }
        }

        private static void TickShedCycle()
        {
            if (!Features_Ownership.OwnershipAuthority.Active)
            {
                // Nothing to shed if O6 isn't claiming anything at all -- gauge still published so a dashboard
                // watching this key doesn't see it go stale/disappear across an OwnershipAuthority toggle.
                Telemetry.SetGauge("ownership.loadshed.shedZones", s_shed.Count);
                return;
            }

            if (!FrameBudget.IsOverBudget)
            {
                if (s_shed.Count > 0)
                {
                    int released = s_shed.Count;
                    s_shed.Clear();
                    Telemetry.GetCounter("ownership.loadshed.zonesRestoredTotal").Add(released);
                    NoodlesLog.Info($"O-LoadShed: frame budget recovered -- {released} previously-shed zone(s) " +
                        "are eligible for near-field claiming again (actual re-claim pace still governed by " +
                        "O6's own MaxZonesClaimedPerCycle).");
                }
                Telemetry.SetGauge("ownership.loadshed.shedZones", s_shed.Count);
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (now < s_nextCycleAtRealtime)
            {
                Telemetry.SetGauge("ownership.loadshed.shedZones", s_shed.Count);
                return;
            }
            s_nextCycleAtRealtime = now + Mathf.Max(0.1f, ZoneClaimSweep.ActiveInterval());

            RunShedCycle();
            Telemetry.SetGauge("ownership.loadshed.shedZones", s_shed.Count);
        }

        // MAJOR FIX (round-2 review, "shedder" lens): ceiling on how many candidate zones one RunShedCycle call
        // EXAMINES (IsZoneBusy() -- a real FindSectorObjects sector scan plus up to six GetComponent calls per
        // live object it returns), independent of MaxZonesShedPerCycle. Without this, `examined` was only ever
        // bounded by `candidateCount` -- a busy zone's `continue` (by design; see the loop comment below) never
        // advances `shedThisCycle`, so a cycle where most/all candidates are BUSY (exactly the case when
        // players are actively fighting/building nearby -- the same load that trips FrameBudget.IsOverBudget in
        // the first place) never hits the shed cap and would scan the ENTIRE candidate list on the main thread
        // in one frame. Ceiling matches ZoneClaimSweep.MaxZonesClaimedPerCycle's own acceptable-range max (32)
        // -- same order of magnitude as the sibling cap this class already mirrors elsewhere -- deliberately
        // independent of (and normally larger than) the shed cap itself, since busy zones must not count
        // against that cap for the round-robin to make real progress under sustained load. A plain constant,
        // not a new config entry -- an internal cost bound, not a policy knob, same reasoning final.md §7
        // applies to this class's other unconditional correctness fixes.
        private const int MaxZonesExaminedPerCycle = 32;

        private static void RunShedCycle()
        {
            s_scratchCandidates.Clear();
            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            foreach (Vector2s held in ServerZoneMembership.Snapshot())
            {
                if (s_shed.Contains(held)) continue; // already shed -- not a candidate again until restored
                // Far-band-held: shedding it relieves nothing (see class remarks) -- only near-field (peer-
                // overlapped) held zones are the actual near-field-claiming cost this valve exists to relieve.
                if (!ZoneClaimSweep.AnyPeerWithinPaddedActiveArea(held, peers)) continue;
                s_scratchCandidates.Add(held);
            }
            if (s_scratchCandidates.Count == 0) return;

            int cap = Mathf.Clamp(s_maxZonesShedPerCycle.Value, 1, 16);
            int shedThisCycle = 0;
            int candidateCount = s_scratchCandidates.Count;
            int start = s_shedRotation % candidateCount;
            int examined = 0;
            int maxExamine = Mathf.Min(candidateCount, MaxZonesExaminedPerCycle);

            // Priority-order state machine (final.md WP-D acceptance criterion): round-robin starting where
            // last cycle left off, so a busy server with more shed-eligible zones than `cap` eventually reaches
            // all of them across several cycles rather than always re-evaluating the same first few. A zone
            // that fails the liveness check below is skipped -- tried again from wherever the rotation lands
            // next time, not retried immediately -- rather than counted against this cycle's cap.
            //
            // `examined` (loop iterations actually run this cycle), NOT `candidateCount` (the full list size),
            // is what the rotation advances by below -- BUG FIX (self-review): (start + candidateCount) %
            // candidateCount is always exactly `start` again, since start < candidateCount already, so
            // advancing by the full count would silently never move the rotation at all, and once the cap is
            // reached before the whole list is examined, every zone beyond position `cap` in the candidate list
            // would starve (never examined on any later cycle either). Advancing by `examined` instead means
            // next cycle picks up exactly where this one's scan left off.
            //
            // MAJOR FIX (round-2 review, "shedder" lens): loop bound is `maxExamine`, NOT `candidateCount` --
            // see MaxZonesExaminedPerCycle's own remarks above. The remainder of a large candidate list is
            // still reached, just over more cycles, exactly like ZoneClaimSweep's own claim loop degrades under
            // a large candidate set instead of scanning it all in one frame.
            for (; examined < maxExamine && shedThisCycle < cap; examined++)
            {
                Vector2s zone = s_scratchCandidates[(start + examined) % candidateCount];
                if (IsZoneBusy(zone))
                {
                    Telemetry.GetCounter("ownership.loadshed.skippedBusy").Increment();
                    NoodlesLog.DetailRateLimited("ownership.loadshed.skippedBusy",
                        $"O-LoadShed: zone {zone} skipped this shed cycle (physically in use) -- deferred to " +
                        "the next-priority zone.", firstN: 10, windowSeconds: 60f);
                    continue;
                }

                s_shed.Add(zone);
                shedThisCycle++;
                Telemetry.GetCounter("ownership.loadshed.zonesShedTotal").Increment();
                NoodlesLog.DetailRateLimited("ownership.loadshed.zoneShed",
                    $"O-LoadShed: shed zone {zone} back to vanilla near-field ownership (frame budget).",
                    firstN: 10, windowSeconds: 60f);
            }

            s_shedRotation = (start + examined) % candidateCount;
        }

        /// <summary>
        /// Data-loss critic's Finding 3 (final.md §3): the SAME physical liveness checks
        /// <c>Gameplay.HandoverGuard</c>'s own fast paths use, run directly against every object currently in
        /// the zone -- never through <c>ReclaimGate</c> (see class remarks for why). A zone with nothing
        /// physically in use returns false (safe to shed) even if e.g. <c>StructureHandoverGuard</c> is off;
        /// this check is about physical safety, never about the Gameplay module's own reclaim policy.
        ///
        /// <b>BLOCKER FIX (round-2 review, "shedder" lens).</b> final.md §3's own prescription for this method
        /// (Container/Vagon/Sadle/Ship) is the v1 checklist carried forward verbatim -- but every one of those
        /// four is gated <c>OwnershipAuthorityMode.All</c>-only (Ship additionally needs <c>AllowShipClaim</c>)
        /// per <see cref="NearFieldClaimPolicy.IsClassAllowedForMode"/>, so under the actually-shipped default
        /// (<c>Mode=FarOrShared</c>) none of the four can ever be near-field-held in the first place -- this
        /// method's original four checks were a no-op against the types this shedder actually holds at
        /// <c>FarOrShared</c> (final.md §4's own table): WearNTear/buildings, production (<c>Smelter</c>), and
        /// creatures (<c>Character</c>-bearing <c>BaseAI</c>/<c>MonsterAI</c>/<c>Tameable</c>). Added the same
        /// Smelter/Character pair <c>Gameplay.HandoverGuard.CheckProductionAndCreature</c> already uses
        /// (<c>LivenessBookkeeper.TickedRecently</c> local fast path, falling back to
        /// <c>QuiescenceTracker.IsQuiescent</c>'s DataRevision backstop for a remote/unknown owner) so an
        /// actively-ticking production building or creature is no longer shed out from under its own live
        /// simulation. The original four checks are kept as harmless defense-in-depth for a possible future
        /// <c>Mode=All</c>. Deliberately NOT extended to WearNTear itself: <c>HandoverGuard</c>'s own
        /// <c>StructureHandoverGuard</c> branch runs no liveness check at all for WearNTear -- an explicitly
        /// documented, ACCEPTED residual mid-<c>UpdateSupport()</c> race, not a gap this file should newly
        /// invent a check for -- and the other <c>FarOrShared</c> classes (Fermenter/Beehive/CookingStation/
        /// Windmill/CraftingStation/MineRock/TreeBase/Door/TerrainComp) have no "in use" signal anywhere in
        /// this codebase either, same scope <c>HandoverGuard</c> itself already settled on.
        /// </summary>
        private static bool IsZoneBusy(Vector2s zone)
        {
            if (ZDOMan.instance == null || ZNet.instance == null || ZNetScene.instance == null) return true; // can't check -- fail toward "don't shed", never toward "shed blindly"

            s_scratchScan.Clear();
            SimulationDistance synced = ZNet.instance.GetSyncedSimulationDistance();
            var nearOnly = new SimulationDistance(synced.NearSimulationDistance, 0, synced.IsClassic);
            ZDOMan.instance.FindSectorObjects(zone, nearOnly, s_scratchScan);

            // Same config key HandoverGuard.CheckProductionAndCreature reads for the identical Smelter/
            // Character checks below (Features_Gameplay lives in the Gameplay module -- fully qualified, no new
            // `using`, since this is this file's first-ever cross-module call and the boundary is worth keeping
            // explicit at the call site).
            float quiescence = NoodlesMcDoodles.Gameplay.Features_Gameplay.QuiescenceSecondsConfig.Value;

            foreach (ZDO zdo in s_scratchScan)
            {
                ZNetView live = ZNetScene.instance.FindInstance(zdo);
                if (live == null) continue; // not instantiated here -- nothing live to be "in use"

                Container container = live.GetComponent<Container>();
                if (container != null && container.IsInUse()) return true;

                Vagon vagon = live.GetComponent<Vagon>();
                if (vagon != null && vagon.InUse()) return true;

                Sadle sadle = live.GetComponent<Sadle>();
                if (sadle != null && sadle.HaveValidUser()) return true;

                Ship ship = live.GetComponent<Ship>();
                if (ship != null)
                {
                    List<Player> aboard = s_shipPlayers(ship);
                    if (aboard != null && aboard.Count > 0) return true;
                }

                // BLOCKER FIX (round-2 review, "shedder" lens) -- see method remarks above. Mirrors
                // Gameplay.HandoverGuard.CheckProductionAndCreature's own Smelter/Character branches exactly:
                // locally-ticking (fast path) OR not-yet-quiescent (backstop, covers a remote/unknown owner)
                // both mean "physically mid-use right now", not just a local-owner signal.
                Smelter smelter = live.GetComponent<Smelter>();
                if (smelter != null &&
                    (NoodlesMcDoodles.Gameplay.LivenessBookkeeper.TickedRecently(zdo) ||
                     !NoodlesMcDoodles.Gameplay.QuiescenceTracker.IsQuiescent(zdo, quiescence)))
                {
                    return true;
                }

                Character character = live.GetComponent<Character>();
                if (character != null &&
                    (NoodlesMcDoodles.Gameplay.LivenessBookkeeper.TickedRecently(zdo) ||
                     !NoodlesMcDoodles.Gameplay.QuiescenceTracker.IsQuiescent(zdo, quiescence)))
                {
                    return true;
                }
            }
            return false;
        }

        // --- Kill switch / quarantine hygiene -------------------------------------------------------------------

        /// <summary>Releases this file's own bookkeeping only -- never calls <c>ServerZoneMembership.Release</c>
        /// itself; a shed zone was never released BY this file (see class remarks: <c>ZoneClaimSweep</c>'s own
        /// release-diffing does that), so clearing the shed set here just stops excluding anything, the same
        /// safe direction <c>ServerZoneMembership.ClearAll</c>'s own kill-switch flush already takes for the
        /// zones themselves.</summary>
        internal static void ClearAllState()
        {
            s_shed.Clear();
            s_shedRotation = 0;
            s_nextCycleAtRealtime = 0f;
            FrameBudget.ResetState();
        }

        [NoodlesModuleInit]
        internal static void Init()
        {
            s_maxZonesShedPerCycle = NoodlesConfig.File.Bind("OwnershipAuthority.LoadShed", "MaxZonesShedPerCycle", 4,
                new ConfigDescription("WP-D (scratchpad/saa/final.md) -- how many NEW held near-field zones this " +
                    "shedder marks shed per ZoneClaimSweep.ActiveInterval cycle while frame budget is over " +
                    "(FrameBudget.IsOverBudget). No vanilla equivalent. Mirrors " +
                    "OwnershipAuthority.MaxZonesClaimedPerCycle's own round-robin cap shape -- the remainder " +
                    "round-robins across subsequent cycles rather than shedding everything at once.",
                    new AcceptableValueRange<int>(1, 16)));

            // Per-feature quarantine hygiene note (this file holds no independent hold of its own -- see class
            // remarks: ServerZoneMembership/ZoneClaimSweep own the actual zone release): ZoneClaimSweep.cs's own
            // OnForcedOff wiring on the shared "OwnershipAuthority" flag is what actually releases every held
            // zone on a force-off; once that runs, ServerZoneMembership.Snapshot() is empty and this file's own
            // shed set (always a subset of that snapshot) is meaningless leftover bookkeeping -- harmless
            // whether or not it's cleared, but cleared anyway below for hygiene via the kill-switch flush.
            KillSwitch.RegisterFlushCallback("Ownership.LoadShedder", ClearAllState);
        }
    }
}
