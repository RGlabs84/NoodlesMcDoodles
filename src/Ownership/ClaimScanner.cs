using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Configuration;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.Ownership
{
    /// <summary>
    /// 1.0.10 claim-pass redesign (coordinator design change, 2026-09-29 -- the user's own words: "why don't we
    /// have this pass running on a fast coroutine that ties in to the main tick only when needed"). Replaces the
    /// old once-per-<see cref="ZoneClaimSweep.ActiveInterval"/> burst -- <c>ZoneClaimSweep.RunClaimCycle</c> used
    /// to call a since-removed <c>ClaimZone</c> synchronously for up to the old <c>MaxZonesClaimedPerCycle</c>
    /// zones, in the ONE frame the cycle happened to land on, each a full vanilla <c>ReleaseNearbyZDOS</c>
    /// 5x5-sector sweep -- with a continuous, time-sliced scanner: a hand-rolled step machine driven every frame
    /// from <see cref="Core.NoodlesHeartbeat"/>.Update (<see cref="PumpFrame"/>), doing at most
    /// <see cref="s_frameBudgetMs"/> worth of wall-clock work per frame, one already-known object at a time.
    ///
    /// <b>MAIN THREAD ONLY, always.</b> <c>ZDO</c>/<c>ZDOMan</c> and their sector indices are not thread-safe (no
    /// lock anywhere in vanilla's own implementation), so this never uses a worker thread. <see cref="PumpFrame"/>
    /// is a plain method call from <c>NoodlesHeartbeat.Update</c> (itself only ever invoked by Unity's own
    /// single-threaded player loop), so every read/write here happens on exactly the same thread vanilla's own
    /// zone/ZDO code runs on -- same "no lock needed" invariant <see cref="ZoneClaimSweep"/> itself already
    /// documents for its own state. A real Unity <c>IEnumerator</c> coroutine would ALSO satisfy "main thread
    /// only" (it resumes between frames on the main thread too), but is deliberately not used here: an uncaught
    /// exception inside a coroutine iterator silently and permanently kills that coroutine with no re-entry,
    /// unlike a plain method called every frame from inside a try/catch (this module's own established idiom,
    /// e.g. <c>SetOwnerHoldGuard.SweepStaleHolds</c>/<c>ZoneClaimSweep.TickPendingZoneReleaseRetries</c>), which
    /// keeps retrying on the very next frame after a single bad tick. <see cref="PumpFrame"/> IS that "coroutine"
    /// in every practical sense the coordinator's brief asked for (a resumable step machine tied to the main
    /// tick, doing a little work per frame, idle when there is nothing due) -- it just isn't literally built on
    /// <c>StartCoroutine</c>.
    ///
    /// <b>Safety while the world changes under a paused scan</b> (coordinator's own explicit ask -- three things
    /// can happen to an object between the moment its zone's snapshot is taken and the moment this scanner
    /// actually gets to it, possibly several frames later):
    /// <list type="bullet">
    /// <item>it can be <b>destroyed</b> -- the snapshot stores <c>ZDOID</c>s only, never a live <c>ZDO</c>
    /// reference (exactly the "re-fetch by id, null means gone" idiom <c>ZoneClaimSweep</c>'s own pre-1.0.10
    /// <c>ClaimZone</c>/<c>ReleaseZoneOwnership</c> already used for a ZDO that might vanish between two calls);
    /// <see cref="ZDOMan.GetZDO(ZDOID)"/> returning null is treated as "moot, not a veto", never an error;</item>
    /// <item>it can <b>move to a different zone</b> -- re-checked against the zone this pass is actually FOR
    /// (<see cref="ZoneSystem.GetZone(Vector3)"/> on its CURRENT, freshly-read position) immediately before
    /// acting; a mismatch is simply skipped -- its real, current zone gets its own turn if/when the cursor
    /// reaches it, exactly as if vanilla's own claim loop had simply seen it in its new location on ITS next
    /// sweep;</item>
    /// <item>a <b>brand new object can appear</b> in the zone after the snapshot was taken -- missed by THIS
    /// pass, caught by the zone's very next one: immediately if this pass changed anything (the zone stays
    /// "due"), otherwise within <see cref="SettledRescanSeconds"/> of going quiet (see <see cref="MarkSettled"/>)
    /// -- a bounded staleness window, never indefinite.</item>
    /// </list>
    /// No snapshot is ever mutated while in use (a fresh list per zone, cleared and refilled only when that
    /// zone's own turn starts) and no index is ever read out of bounds (<see cref="StepActiveZone"/> checks
    /// before every read). At most one zone is ever "in flight" at a time -- the whole point of a single cursor.
    ///
    /// <b>HARD REQUIREMENT (a) equivalence -- stray release survives.</b> Vanilla's pre-1.0.10 call path
    /// (<c>ReleaseNearbyZDOS</c>'s own branch 1, SERVER:76962-76966) released a server-owned object outside the
    /// claimed zone's 3x3 active area on EVERY <c>ClaimZone</c> turn -- effectively sweeping a 5x5-minus-3x3 ring
    /// around every candidate zone, every round-robin pass. This scanner never calls that method (the actual
    /// source of the ~12,000/s vetoed <c>SetOwner</c> calls this redesign removes), so that coverage is
    /// reproduced explicitly in <see cref="RefreshZoneRoles"/> instead: every zone within Chebyshev distance 2 of
    /// any CURRENTLY HELD zone that isn't already a claim candidate gets a <see cref="ZoneRole.ReleaseOnly"/>
    /// entry, and <see cref="StepReleaseOnly"/> releases any server-owned object it finds there unconditionally.
    /// This is a strict SUPERSET of vanilla's own footprint (vanilla left the inner ring, distance 1, alone --
    /// this stray-checks it too) -- safe, never MORE data-lossy, only more diligent: releasing a server-owned
    /// object back to unowned never destroys it, never touches a ZDO, and only ever hands ownership to nobody in
    /// particular, exactly what happens to any object vanilla itself would eventually stop tracking as "near" a
    /// caller. <see cref="ZoneClaimSweep"/>'s own release-diffing case (a zone that LOSES claim candidacy while
    /// still in <c>ServerZoneMembership</c>) is folded into the SAME <see cref="ZoneRole.ReleaseOnly"/> handling,
    /// replacing that file's old inline loop one-for-one (same <c>ZoneReleaseOutcome</c>-style FullyReleased
    /// gate, same partial-release retry, same counters).
    /// </summary>
    internal static class ClaimScanner
    {
        // --- Config -------------------------------------------------------------------------------------------

        private static ConfigEntry<float> s_frameBudgetMs;

        // --- Role bookkeeping: rebuilt once per RefreshZoneRoles call (ZoneClaimSweep.RunClaimCycle's own
        // ~ActiveInterval cadence -- unchanged from pre-1.0.10). Main-thread only, same as every field below. ---

        private enum ZoneRole { Candidate, ReleaseOnly }

        private static Dictionary<Vector2s, ZoneRole> s_role = new Dictionary<Vector2s, ZoneRole>();

        // --- Scheduling: a FIFO of zones due for a fresh full pass (deduped), plus a settle timer for zones
        // whose last full pass found nothing to do. "Dirty zones go first" (coordinator's own ask) falls out
        // naturally: PumpFrame only ever drains s_dueQueue; a settled zone re-enters it only once its own quiet
        // period (SettledRescanSeconds) has elapsed, checked (cheaply) from RefreshZoneRoles. ---------------------

        private static readonly Queue<Vector2s> s_dueQueue = new Queue<Vector2s>();
        private static readonly HashSet<Vector2s> s_inDueQueue = new HashSet<Vector2s>();
        private static readonly Dictionary<Vector2s, float> s_settledUntil = new Dictionary<Vector2s, float>();

        // Candidate-role zones pulled off the due queue while SimulationDistanceCeiling.OwnershipAuthorityArmed
        // is false (LEAD FIX carried over from pre-1.0.10 RunClaimCycle: "only actually CLAIM anything while
        // armed") -- requeued in one shot the moment PumpFrame observes arming resume, rather than busy-cycling
        // through them every single frame while unarmed. ReleaseOnly-role zones are NEVER deferred this way: the
        // pre-1.0.10 code's own release-diffing loop explicitly ran regardless of Armed, and this preserves that.
        // 1.0.10 review fix (MINOR 6): a set, not a list -- a zone re-queued by every refresh while unarmed must be
        // held here once, not once per refresh.
        private static readonly HashSet<Vector2s> s_deferredWhileUnarmed = new HashSet<Vector2s>();

        // Internal bound, not a config key -- same reasoning as ZoneClaimSweep.MaxZonesReleasedPerCycle's own
        // remarks ("an internal cost bound, not a policy knob"). How long a zone whose last full pass found
        // nothing to do waits before its next one; bounds how stale a create/destroy/move this scanner doesn't
        // push-notify about can ever get (see class remarks, "a brand new object can appear").
        private const float SettledRescanSeconds = 2f;

        // --- The one zone currently "in flight". Only ever one at a time -- the whole point of a single cursor
        // spending a small time budget per frame instead of doing a whole zone (or eight) at once. -------------

        private static Vector2s? s_activeZone;
        private static ZoneRole s_activeRole;
        private static readonly List<ZDOID> s_activeSnapshot = new List<ZDOID>();
        private static int s_activeIndex;
        private static bool s_activeAnyVerified, s_activeAnyAttempted, s_activePlayerKeptAny, s_activeServerOwnsAny, s_activeDeferred;
        private static int s_activeReleaseFound, s_activeReleaseVerified;

        // Reused FindSectorObjects target for StartZone -- single-zone only (near=0, far=0), never vanilla's
        // 5x5 block (contrast the pre-1.0.10 TryPrepareClaim, which matched ReleaseNearbyZDOS's own 5x5 call
        // shape on purpose because it fed that call; this scanner never calls ReleaseNearbyZDOS at all).
        private static readonly List<ZDO> s_scratchFetch = new List<ZDO>();

        private static readonly Stopwatch s_sliceStopwatch = new Stopwatch();
        private const int HardStepGuard = 20000; // paranoid infinite-loop guard; should never realistically be reached

        // --- Telemetry: rate/high-water gauges, published once per Telemetry.FlushAndLog and reset there (see
        // PublishTelemetryAndReset + Telemetry.RegisterPeriodicPublisher) -- the coordinator's own new asks. ----

        private static readonly Counter s_scanObjectsTotal = Telemetry.GetCounter("ownership.o6.scanObjectsTotal");
        private static readonly Counter s_setOwnerCallsTotal = Telemetry.GetCounter("ownership.o6.setOwnerCallsTotal");
        private static readonly Counter s_zonesCompletedTotal = Telemetry.GetCounter("ownership.o6.zonesCompletedTotal");
        private static readonly Counter s_strayOrCandidacyLostReleased = Telemetry.GetCounter("ownership.o6.strayOrCandidacyLostReleased");
        // 1.0.10 review fix (MINOR 4): the per-object counters are cached like the four above instead of looked up
        // by name on every step.
        private static readonly Counter s_graceWindowBlocks = Telemetry.GetCounter("ownership.o6.graceWindowBlocks");
        private static readonly Counter s_reclaimGateRefused = Telemetry.GetCounter("ownership.o6.reclaimGateRefused");
        private static readonly Counter s_claimDeferredNotCreated = Telemetry.GetCounter("ownership.o6.claimDeferredNotCreated");
        private static readonly Counter s_expectedClaimRefusedByPolicy = Telemetry.GetCounter("ownership.o6.expectedClaimRefusedByPolicy");
        private static readonly Counter s_expectedClaimNotAttempted = Telemetry.GetCounter("ownership.o6.expectedClaimNotAttempted");
        private static readonly Counter s_setOwnerVetoed = Telemetry.GetCounter("ownership.setOwnerVetoed");
        private static double s_sliceMsMaxThisWindow;
        // 1.0.10 review (MAJOR 3): the slice budget is checked between steps, so one slow step overruns it. The
        // slowest single step per window, plus a rate-limited detail line for any step over SlowStepLogMs naming
        // what it was, shows where a claimSliceMsMax spike came from.
        private static double s_stepMsMaxThisWindow;
        private const double SlowStepLogMs = 5.0;
        private static string s_lastStepKind = "none";
        private static int s_lastStepPrefab;
        private static long s_lastScanObjects, s_lastSetOwnerCalls;
        private static float s_lastPublishRealtime;

        // ============================================================================================
        // Refresh: cheap, infrequent (ZoneClaimSweep.RunClaimCycle's own ~ActiveInterval cadence). Never
        // touches a single ZDO -- only decides WHICH zones are relevant and WHAT ROLE each one has.
        // ============================================================================================

        /// <summary>
        /// Rebuilds the zone-role map from scratch every call (never additively grown -- same "fully re-derived,
        /// never additive" discipline <c>ZoneClaimSweep.RunClaimCycle</c>'s own pre-1.0.10 candidate diff used),
        /// and marks anything newly relevant (new to tracking, or whose role just changed) "due" for an early
        /// turn. <paramref name="candidates"/> is <c>ZoneClaimSweep.ComputeCandidateZones</c>'s own unchanged
        /// output -- this method owns only what happens to that set from here on.
        /// </summary>
        internal static void RefreshZoneRoles(List<Vector2s> candidates)
        {
            var candidateSet = new HashSet<Vector2s>(candidates);
            List<Vector2s> held = ServerZoneMembership.Snapshot();

            var newRole = new Dictionary<Vector2s, ZoneRole>(candidateSet.Count + held.Count);
            foreach (Vector2s z in candidateSet) newRole[z] = ZoneRole.Candidate;
            // Candidacy-lost case (pre-1.0.10 RunClaimCycle's own inline release-diffing loop, now folded in
            // here): a zone still in ServerZoneMembership but no longer a candidate gets ReleaseOnly -- ITS OWN
            // full-release gate lives in FinishActiveZone, mirroring ReleaseZoneOwnership's FullyReleased check
            // one-for-one. 1.0.10 review fix (MAJOR 2, a gap inherited from 1.0.9): skipped while O10 also holds
            // the zone. StepReleaseOnly cannot tell O6's objects from O10's (both are server-owned), so releasing
            // here would undo O10's holds; the zone stays held until O10 lets go, then releases on a later refresh.
            foreach (Vector2s z in held)
                if (!newRole.ContainsKey(z) && !SharedAreaZoneHold.IsHeld(z)) newRole[z] = ZoneRole.ReleaseOnly;

            // Stray ring -- HARD REQUIREMENT (a) equivalence, see class remarks. Pure zone-grid arithmetic, no
            // ZDO scanning: cheap even for a large held set (dominated by the held CLUSTER's perimeter once
            // interior ring cells collapse into already-tracked entries via the newRole.ContainsKey checks).
            foreach (Vector2s h in held)
            {
                for (int dy = -2; dy <= 2; dy++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        var z = new Vector2s(h.x + dx, h.y + dy);
                        if (newRole.ContainsKey(z)) continue;
                        // O10 protection, carried over from the pre-1.0.10 NearFieldClaimVeto uid==0 branch's own
                        // IsHeldForServer check (ServerZoneMembership.IsServerHeld OR SharedAreaZoneHold.IsHeld):
                        // never treat a zone O10 independently holds as a stray-release target, or this scanner
                        // would release an object a completely different ownership mechanism is legitimately
                        // holding. Simplest safe rule for a zone this scanner has no other reason to touch: leave
                        // it alone entirely, exactly as today's G3-L1/IsHeldForServer already does.
                        if (SharedAreaZoneHold.IsHeld(z)) continue;
                        newRole[z] = ZoneRole.ReleaseOnly;
                    }
                }
            }

            // Dirty on: newly tracked, or role changed since the last refresh.
            foreach (var kv in newRole)
                if (!s_role.TryGetValue(kv.Key, out ZoneRole oldRole) || oldRole != kv.Value) MarkDue(kv.Key);

            // Zones no longer tracked at all: drop their settle timer. A zone currently mid-flight (s_activeZone)
            // keeps running under the role it STARTED with regardless -- see StepActiveZone/FinishActiveZone
            // remarks -- so removing it from s_role here cannot corrupt that in-flight pass, only stop a FUTURE
            // StartZone call for it until/unless it reappears in a later refresh.
            foreach (Vector2s z in s_role.Keys) if (!newRole.ContainsKey(z)) s_settledUntil.Remove(z);

            s_role = newRole;

            // Promote any settled zone whose quiet period has already elapsed back onto the due queue.
            float now = Time.time;
            List<Vector2s> due = null;
            foreach (var kv in s_settledUntil) if (kv.Value <= now) (due ??= new List<Vector2s>()).Add(kv.Key);
            if (due != null) foreach (Vector2s z in due) { s_settledUntil.Remove(z); MarkDue(z); }
        }

        private static void MarkDue(Vector2s zone)
        {
            s_settledUntil.Remove(zone);
            if (s_inDueQueue.Add(zone)) s_dueQueue.Enqueue(zone);
        }

        private static void MarkSettled(Vector2s zone)
        {
            s_settledUntil[zone] = Time.time + SettledRescanSeconds;
        }

        // ============================================================================================
        // PumpFrame: called every frame from NoodlesHeartbeat.Update -- "ties in to the main tick".
        // ============================================================================================

        internal static void PumpFrame()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!Features_Ownership.OwnershipAuthority.Active) return;

            bool armed = SimulationDistanceCeiling.OwnershipAuthorityArmed;
            if (armed && s_deferredWhileUnarmed.Count > 0)
            {
                foreach (Vector2s z in s_deferredWhileUnarmed) MarkDue(z);
                s_deferredWhileUnarmed.Clear();
            }

            float budgetMs = Mathf.Clamp(s_frameBudgetMs.Value, 0.1f, 5f);
            s_sliceStopwatch.Restart();
            int steps = 0;
            while (true)
            {
                double stepStartMs = s_sliceStopwatch.Elapsed.TotalMilliseconds;
                int gcBefore = GC.CollectionCount(0);
                bool startedZone = false;
                try
                {
                    // Zone start is inside the try too (1.0.10 review fix): a throw there must go through the same
                    // recovery as a throw mid-pass.
                    if (s_activeZone == null)
                    {
                        if (!TryStartNextZone(armed)) break;
                        startedZone = true;
                    }
                    StepActiveZone();
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(ClaimScanner) + ".StepActiveZone", e);
                    RecoverAfterException();
                    break; // at most one exception per frame, even if the same one would repeat
                }

                double stepMs = s_sliceStopwatch.Elapsed.TotalMilliseconds - stepStartMs;
                if (stepMs > s_stepMsMaxThisWindow) s_stepMsMaxThisWindow = stepMs;
                if (stepMs >= SlowStepLogMs) LogSlowStep(stepMs, startedZone, GC.CollectionCount(0) - gcBefore);

                steps++;
                if (steps >= HardStepGuard) break;
                if (s_sliceStopwatch.Elapsed.TotalMilliseconds >= budgetMs) break;
            }

            double ms = s_sliceStopwatch.Elapsed.TotalMilliseconds;
            if (ms > s_sliceMsMaxThisWindow) s_sliceMsMaxThisWindow = ms;
        }

        /// <summary>
        /// 1.0.10 review fix (BLOCKER 1). The old catch dropped the in-flight zone without running
        /// <see cref="FinishActiveZone"/> and without re-queueing it. Nothing else ever re-queues a zone whose role is
        /// unchanged, so objects already claimed in that pass stayed server-owned in a zone missing from
        /// ServerZoneMembership: no G3-L1 protection, no kill-switch release, never revisited. Now:
        /// <list type="bullet">
        /// <item>A throw on one object: <c>s_activeIndex++</c> runs before any per-object work, so that object is
        /// already consumed. The pass carries on with the next object and still ends in FinishActiveZone.</item>
        /// <item>A throw at the zone boundary (StartZone, or FinishActiveZone itself) would repeat on every step.
        /// The pass is dropped and the zone goes on its settle timer, so a fresh pass re-finds anything already
        /// claimed (the <c>GetOwner()==sessionId</c> branch) and settles its membership normally.</item>
        /// </list>
        /// </summary>
        private static void RecoverAfterException()
        {
            if (s_activeZone == null) return;
            if (s_activeIndex < s_activeSnapshot.Count) return;
            Vector2s zone = s_activeZone.Value;
            s_activeZone = null;
            MarkSettled(zone);
        }

        private static void LogSlowStep(double stepMs, bool startedZone, int gcRuns)
        {
            try
            {
                string kind = startedZone ? "start+" + s_lastStepKind : s_lastStepKind;
                GameObject prefab = s_lastStepPrefab != 0 && ZNetScene.instance != null
                    ? ZNetScene.instance.GetPrefab(s_lastStepPrefab) : null;
                NoodlesLog.DetailRateLimited("ownership.o6.slowScanStep",
                    $"ClaimScanner: one step took {stepMs:F1} ms (kind={kind}, zone={s_activeZone?.ToString() ?? "none"}, " +
                    $"role={s_activeRole}, object {s_activeIndex}/{s_activeSnapshot.Count}, " +
                    $"prefab={(prefab != null ? prefab.name : s_lastStepPrefab.ToString())}, gcRuns={gcRuns}).", 10, 60f);
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(ClaimScanner) + ".LogSlowStep", e);
            }
        }

        private static bool TryStartNextZone(bool armed)
        {
            while (s_dueQueue.Count > 0)
            {
                Vector2s z = s_dueQueue.Dequeue();
                s_inDueQueue.Remove(z);
                if (!s_role.TryGetValue(z, out ZoneRole role)) continue; // dropped from tracking since it was queued -- nothing to do

                if (role == ZoneRole.Candidate && !armed)
                {
                    s_deferredWhileUnarmed.Add(z);
                    continue;
                }

                StartZone(z, role);
                return true;
            }
            return false;
        }

        private static void StartZone(Vector2s zone, ZoneRole role)
        {
            s_activeZone = zone;
            s_activeRole = role;
            s_activeIndex = 0;
            s_activeAnyVerified = s_activeAnyAttempted = s_activePlayerKeptAny = s_activeServerOwnsAny = s_activeDeferred = false;
            s_activeReleaseFound = s_activeReleaseVerified = 0;

            // Coordinator's explicit ask: ONLY this zone's own sector objects (near=0, far=0), never vanilla's
            // 5x5 block -- see class remarks on why this scanner has no reason to match ReleaseNearbyZDOS's own
            // call shape the way the pre-1.0.10 TryPrepareClaim deliberately did.
            // Snapshot cleared before the fetch (1.0.10 review fix): if the fetch throws, this pass must see an empty
            // snapshot, never the previous zone's ids.
            s_activeSnapshot.Clear();
            bool classic = ZNet.instance.GetSyncedSimulationDistance().IsClassic;
            s_scratchFetch.Clear();
            ZDOMan.instance.FindSectorObjects(zone, new SimulationDistance(0, 0, classic), s_scratchFetch);

            foreach (ZDO zdo in s_scratchFetch) if (zdo != null) s_activeSnapshot.Add(zdo.m_uid);
            s_scanObjectsTotal.Add(s_activeSnapshot.Count);
        }

        /// <summary>One micro-step: acts on at most one already-snapshotted object (re-fetched live by id --
        /// see class remarks), or finishes the active zone once the snapshot is exhausted. Never processes more
        /// than one ZDO per call, so <see cref="PumpFrame"/>'s time budget is checked at the finest grain this
        /// scanner offers.</summary>
        private static void StepActiveZone()
        {
            Vector2s zone = s_activeZone.Value;
            if (s_activeIndex >= s_activeSnapshot.Count) { s_lastStepKind = "finish"; s_lastStepPrefab = 0; FinishActiveZone(zone); return; }

            s_lastStepKind = "object";
            s_lastStepPrefab = 0;
            ZDOID id = s_activeSnapshot[s_activeIndex++];
            ZDO zdo = ZDOMan.instance.GetZDO(id);
            if (zdo == null) return; // destroyed since the snapshot was taken -- moot, not a veto
            s_lastStepPrefab = zdo.GetPrefab();

            // Still actually in the zone this pass is for? A mover can leave between the snapshot and this step
            // -- see class remarks (2). Its real, current zone gets its own turn if/when the cursor reaches it.
            if (ZoneSystem.GetZone(zdo.GetPosition()) != zone) return;

            long sessionId = ZDOMan.GetSessionID();
            if (s_activeRole == ZoneRole.ReleaseOnly) StepReleaseOnly(zdo, sessionId);
            else StepCandidate(zone, id, zdo, sessionId);
        }

        /// <summary>Branch-1 equivalent (stray release) AND the candidacy-lost release-diffing case, unified:
        /// both are "this zone is not where this server-owned object should still be" -- see class remarks and
        /// <see cref="RefreshZoneRoles"/>'s own remarks on why both land in <see cref="ZoneRole.ReleaseOnly"/>.
        /// Deliberately no <c>Persistent</c> filter -- mirrors <c>ZoneClaimSweep.ReleaseZoneOwnership</c>'s own
        /// filter exactly (that method never checked it either), which this replaces one-for-one for the
        /// candidacy-lost case.
        ///
        /// EVIDENCE-BASED FIX (found on this task's own harness cycle 1, DLL 07d3345331e8 -- see the 1.0.10 fix
        /// report's "anything you did not verify"/findings section for the numbers): deliberately does NOT wrap
        /// this call in <c>NearFieldClaimVeto.BeginBulkClaim</c>/<c>SetOwnerHoldGuard.RunAuthorizedBulkClaim</c>,
        /// even though <see cref="StepCandidate"/>'s claim direction does (and even though the coordinator's own
        /// brief asked for every scanner SetOwner call to stay in that scope) -- calling
        /// <c>OwnershipApi.SetOwner(zdo, 0, ...)</c> DIRECTLY instead, exactly matching
        /// <c>ZoneClaimSweep.ReleaseZoneOwnership</c>'s own long-established, never-wrapped pattern (the same
        /// call <c>ReleaseAllHeldZones</c>/<c>TickPendingZoneReleaseRetries</c> already use). Reason: a
        /// candidacy-lost zone is, by construction, STILL <c>ServerZoneMembership.IsServerHeld</c>==true at the
        /// moment this runs (membership is only dropped in <c>FinishActiveZone</c> AFTER every found ZDO is
        /// verified released) -- wrapping the call sets <c>AuthorizedBulkClaimInFlight</c>, which makes
        /// <c>NearFieldClaimVeto.Prefix</c>'s own <c>uid==0</c> branch consult <c>IsHeldForServer</c> on this
        /// exact object's own (still-held) zone and VETO ITS OWN RELEASE (<c>ownership.o6.selfReleaseKept</c>) --
        /// a genuine self-veto, observed live: cycle 1 hit it for every object in five zones (0/72, 0/112, 0/177,
        /// 0/78, 0/141 released) within one second, which alone burst past ModeQuarantine's VetoBurstLimit and
        /// quarantined the whole feature down to SharedFar. That self-veto exists to stop vanilla's OWN
        /// branch-1/spawn-sweep from undoing a hold it doesn't know about -- it was never meant to block THIS
        /// module's own deliberate, intentional hand-back of a zone it is actively letting go of, and the
        /// pre-1.0.10 <c>ReleaseZoneOwnership</c> never triggered it for exactly this reason (it was never
        /// wrapped either). Unwrapped, every OTHER real protection still applies unconditionally: the sibling
        /// hold-guard (<c>Patch_ZDO_SetOwner_HoldGuard</c>) is satisfied by <c>AuthorizedSetOwner</c>'s own
        /// per-ZDOID <c>s_authorizedZDOID</c> scoping regardless of the bulk-claim flag, and the three foreign
        /// prefixes (GetOffMyLawn/Wonderland/TortalPortalLite) key off their OWN independent hold state, never
        /// ours -- a genuine third-party veto is still caught and counted below exactly as
        /// <c>ReleaseZoneOwnership</c> always has.</summary>
        private static void StepReleaseOnly(ZDO zdo, long sessionId)
        {
            if (zdo.GetOwner() != sessionId) return; // not server-held right now -- nothing to release
            s_activeReleaseFound++;

            s_setOwnerCallsTotal.Increment();
            bool released = OwnershipApi.SetOwner(zdo, 0,
                "Ownership.ClaimScanner O6 stray/candidacy-lost release (own zone not held)");
            if (released) { s_activeReleaseVerified++; s_strayOrCandidacyLostReleased.Increment(); }
        }

        /// <summary>Branch-2 equivalent (claim), per object -- the exact same eligibility chain the pre-1.0.10
        /// <c>TryPrepareClaim</c> used to pre-compute into an <c>expected</c> list, now evaluated inline one
        /// object at a time. The actual claim call and its 3-way verification/classification
        /// (verified / refused-by-policy / not-attempted / third-party-vetoed) is <c>ZoneClaimSweep.ClaimZone</c>'s
        /// old post-loop, unchanged in substance -- see HARD REQUIREMENT (b) in the 1.0.10 fix report.</summary>
        private static void StepCandidate(Vector2s zone, ZDOID id, ZDO zdo, long sessionId)
        {
            if (!zdo.Persistent) return; // mirrors vanilla's own first check, SERVER:76951
            if (zdo.GetOwner() == sessionId) { s_activeServerOwnsAny = true; return; } // already ours -- nothing to gate or verify

            ZNetPeer ownerPeer = zdo.HasOwner() ? ZNet.instance.GetPeer(zdo.GetOwner()) : null;
            if (ownerPeer != null && ZNetScene.InActiveArea(zdo.GetPosition(), ownerPeer.GetRefPos()))
            { s_activePlayerKeptAny = true; return; } // an in-range connected owner keeps it -- vanilla's own claim loop never takes it either

            if (ZoneClaimSweep.ClaimGraceTracker.IsWithinGraceWindow(id))
            {
                s_graceWindowBlocks.Increment();
                DeferActiveZone(); // whole-zone defer -- stops attempting anything else THIS pass, same trigger as the pre-1.0.10 TryPrepareClaim's own "return false" (see FinishActiveZone's own remarks for the one place the incremental redesign must still differ: a claim already verified earlier in this same pass stays protected)
                return;
            }
            if (zdo.HasOwner() && ownerPeer != null && !ReclaimGate.AllowReclaim(zdo, out string _))
            {
                s_reclaimGateRefused.Increment();
                DeferActiveZone();
                return;
            }

            // Equivalent to "would have been added to the pre-1.0.10 `expected` list" from here on -- every
            // ZoneClaimSweep-level eligibility gate has passed, regardless of whether the actual claim below
            // succeeds (matches "expected != null" for the playerKeptOnly/Claim(zone) gate at FinishActiveZone).
            s_activeAnyAttempted = true;

            // EVIDENCE-BASED FIX (found on this task's own harness cycle 1, same run as StepReleaseOnly's own
            // fix above): round-4's 1.0.9 !zdo.Created refusal (NearFieldClaimVeto.Prefix, "never claim a ZDO
            // that has not finished instantiating on this process") used to only ever run AFTER vanilla's bulk
            // ReleaseNearbyZDOS had already attempted the SetOwner call -- cheap there, since the 5x5
            // FindSectorObjects scan (and vanilla's own per-object attempts across the WHOLE 3x3) dominated the
            // cost regardless. This scanner made the per-object SetOwner call itself the main event, so
            // attempting it BLIND on every still-loading object was the single biggest cost cycle 1 actually
            // measured: setOwnerCallsPerSec peaked at 41,419/s, with ownership.o6.expectedClaimRefusedByPolicy
            // (which the !Created refusal feeds, same as any other policy refusal) climbing by up to ~2,000,000
            // in one telemetry window while ZoneSim's own create backlog was still draining (1.0.9's own harness
            // evidence: ~11,000 objects at ~60/s -- multiple minutes' worth of the SAME objects re-attempted
            // every single pass) -- i.e. this redesign had reproduced the exact problem it exists to fix, just
            // under a different counter name and at a materially WORSE rate, because every attempt here also
            // pays for dispatching through the three foreign mods' own ZDO.SetOwner prefixes (GetOffMyLawn/
            // Wonderland/TortalPortalLite), not only ours. Checked here instead, cheaply, mirroring the grace-
            // window/ReclaimGate checks just above (know the answer, count it, skip the call) -- never a
            // whole-zone defer (unlike those two): every OTHER object in this same zone still gets its own turn
            // this pass, exactly as vanilla's bulk claim used to attempt every object in `expected` individually
            // regardless of one single entry's own Created state.
            if (!zdo.Created)
            {
                s_claimDeferredNotCreated.Increment();
                s_expectedClaimRefusedByPolicy.Increment();
                return;
            }

            // 1.0.10 (user: "skip doomed claims"): NearFieldClaimVeto's prefix asks NearFieldClaimPolicy.IsClaimable
            // inside the SetOwner call, after the call has already been dispatched through our prefix and the three
            // foreign ones. Ask it first. A refused object (mostly pickables near players, ~1,500/s on the mirror)
            // then costs one policy check instead of a SetOwner dispatch. IsClaimable records its own
            // o11.vetoed.* reason once either way, so every counter keeps its meaning. A yes still goes through the
            // prefix, which asks again with the same inputs in the same frame.
            bool claimable;
            try { claimable = NearFieldClaimPolicy.IsClaimable(zdo, sessionId); }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(ClaimScanner) + ".IsClaimable", e);
                claimable = false; // fail closed, as the prefix does
            }
            if (!claimable)
            {
                s_expectedClaimRefusedByPolicy.Increment();
                return;
            }

            NearFieldClaimVeto.BeginBulkClaim(zone); // every SetOwner prefix still applies (ours + the 3 foreign ones) -- coordinator's explicit ask
            SetOwnerHoldGuard.RunAuthorizedBulkClaim(() =>
            {
                s_setOwnerCallsTotal.Increment();
                zdo.SetOwner(sessionId);
            });

            if (zdo.GetOwner() == sessionId)
            {
                ZoneClaimSweep.ClaimGraceTracker.MarkTouched(id);
                ZoneClaimSweep.ClaimChurnTracker.RecordClaim(id);
                s_activeAnyVerified = true;
            }
            else if (NearFieldClaimVeto.WasRefusedByPolicy(id))
            {
                // Our OWN deliberate policy refusal (pickable, item holder, a class the Mode does not allow, or
                // the round-4 !zdo.Created refusal) -- never a third-party veto, must not feed ModeQuarantine.
                s_expectedClaimRefusedByPolicy.Increment();
            }
            else if (!NearFieldClaimVeto.WasAttempted(id))
            {
                // Should not happen for a direct, own-zone SetOwner call the way it could for vanilla's wider
                // 3x3 attempt -- kept for parity with the pre-1.0.10 classification's own third bucket.
                s_expectedClaimNotAttempted.Increment();
            }
            else
            {
                // A third-party ZDO.SetOwner prefix (GetOffMyLawn/Wonderland) silently no-opped this write.
                s_setOwnerVetoed.Increment();
                NoodlesLog.DetailRateLimited("ownership.o6.vetoSample",
                    $"O6 claim vetoed by another mod: {id} owner={zdo.GetOwner()}", 10, 60f);
            }
        }

        private static void DeferActiveZone()
        {
            s_activeDeferred = true;
            s_activeIndex = s_activeSnapshot.Count; // skip the rest of this zone's objects this pass
        }

        private static void FinishActiveZone(Vector2s zone)
        {
            s_zonesCompletedTotal.Increment();

            if (s_activeRole == ZoneRole.Candidate)
            {
                // anyVerified/playerKeptOnly gate -- the same test ZoneClaimSweep.ClaimZone used to apply after
                // its batch verification loop, just accumulated per object instead of over a pre-built `expected`
                // list: !anyAttempted here IS "expected == null" there. Applied EVEN IF this pass was cut short
                // by a grace-window/ReclaimGate defer (s_activeDeferred): unlike the pre-1.0.10 all-or-nothing
                // TryPrepareClaim (which decided eligibility for the WHOLE zone before attempting anything, so an
                // abort meant literally zero claims happened), this scanner claims incrementally, one object per
                // micro-step, possibly across several frames -- by the time it hits a blocked object, EARLIER
                // objects in this SAME zone may already have been genuinely, verifiably claimed. Skipping this
                // gate on a defer would leave those already-real ownership changes unprotected by
                // ServerZoneMembership/G3-L1 (Patch_ZDOMan_IsInPeerActiveArea_ServerHold) for however long the
                // retry takes -- a real, if narrow, protection gap this redesign must not introduce. The defer
                // itself never causes anything EXTRA to be claimed (the blocked object, and everything after it
                // in this pass, was never attempted) -- it only ever means "there is more to check soon", which
                // is what the MarkDue below (rather than MarkSettled) actually captures.
                bool playerKeptOnly = !s_activeAnyAttempted && s_activePlayerKeptAny && !s_activeServerOwnsAny;
                if ((!s_activeAnyAttempted && !playerKeptOnly) || s_activeAnyVerified)
                    ServerZoneMembership.Claim(zone);

                // A defer always means "retry soon" regardless of what was or wasn't attempted before it hit;
                // otherwise a pass that only ever saw already-owned/player-kept objects (or nothing at all) is
                // genuinely quiet and can settle.
                if (s_activeDeferred || s_activeAnyAttempted) MarkDue(zone); else MarkSettled(zone);
            }
            else // ReleaseOnly
            {
                if (s_activeReleaseVerified >= s_activeReleaseFound)
                {
                    if (ServerZoneMembership.IsServerHeld(zone))
                    {
                        ServerZoneMembership.Release(zone);
                        if (!ZoneClaimSweep.StillInZoneSystem(zone))
                            Telemetry.GetCounter("ownership.staleHeldZoneEvictions").Increment();
                        else
                            Telemetry.GetCounter("ownership.candidacyLostHeldZoneReleases").Increment();
                    }
                    MarkSettled(zone);
                }
                else
                {
                    Telemetry.GetCounter("ownership.candidacyLostHeldZonePartialReleases").Increment();
                    NoodlesLog.WarningRateLimited(NoodlesLogLevel.Verbose, "ownership.candidacyLostHeldZonePartialReleases",
                        $"ClaimScanner: zone {zone} only had {s_activeReleaseVerified}/{s_activeReleaseFound} " +
                        "server-owned ZDO(s) released (third-party SetOwner veto) -- keeping held/protected if " +
                        "it still is, retrying soon.", 5, 60f);
                    MarkDue(zone); // retry promptly -- matches the pre-1.0.10 partial-release retry semantics
                }
            }

            s_activeZone = null;
        }

        // ============================================================================================
        // Telemetry publish (once per Telemetry.FlushAndLog) + kill-switch/force-off flush.
        // ============================================================================================

        private static void PublishTelemetryAndReset()
        {
            float now = Time.realtimeSinceStartup;
            float elapsed = s_lastPublishRealtime <= 0f ? 0f : now - s_lastPublishRealtime;
            s_lastPublishRealtime = now;

            Telemetry.SetGauge("ownership.o6.claimSliceMsMax", s_sliceMsMaxThisWindow);
            s_sliceMsMaxThisWindow = 0;
            Telemetry.SetGauge("ownership.o6.scanStepMsMax", s_stepMsMaxThisWindow);
            s_stepMsMaxThisWindow = 0;

            if (elapsed > 0.01f)
            {
                long objs = s_scanObjectsTotal.Value;
                long calls = s_setOwnerCallsTotal.Value;
                Telemetry.SetGauge("ownership.o6.scanObjectsPerSec", (objs - s_lastScanObjects) / elapsed);
                Telemetry.SetGauge("ownership.o6.setOwnerCallsPerSec", (calls - s_lastSetOwnerCalls) / elapsed);
                s_lastScanObjects = objs;
                s_lastSetOwnerCalls = calls;
            }
        }

        /// <summary>
        /// Kill-switch/force-off flush: hands back what an in-flight pass had already claimed in a zone not yet
        /// held (<see cref="HandBackInFlightClaims"/>, 1.0.10 review fix), then drops every bit of THIS FILE's OWN
        /// scheduling state (cursor, queues, settle timers, role map). Never touches <see cref="ServerZoneMembership"/> --
        /// <c>ZoneClaimSweep.ReleaseAllHeldZones</c> (unchanged by this redesign) is what actually hands zones
        /// back; this only clears bookkeeping that is NOT re-derived by that call, so a stale in-flight zone or
        /// a due/settled backlog computed under the OLD role set can't survive into a fresh
        /// <c>OwnershipAuthority.Active=true</c> afterwards. Same reasoning as
        /// <c>ZoneClaimSweep.ClaimGraceTracker.Clear</c>/<c>ClaimChurnTracker.Clear</c>. Registered both as a
        /// global <see cref="KillSwitch"/> flush callback and (from <c>ZoneClaimSweep.Init</c>) as part of
        /// <c>OwnershipAuthority</c>'s own per-feature <c>OnForcedOff</c> -- the same dual-wiring
        /// <c>ReleaseAllHeldZones</c>/<c>SetOwnerHoldGuard.ReleaseAllHolds</c> already use.
        /// </summary>
        internal static void ClearAllState()
        {
            HandBackInFlightClaims();
            s_activeZone = null;
            s_dueQueue.Clear();
            s_inDueQueue.Clear();
            s_settledUntil.Clear();
            s_deferredWhileUnarmed.Clear();
            s_role.Clear();
        }

        /// <summary>
        /// 1.0.10 review fix (BLOCKER 1, kill-switch variant). A Candidate pass cut off by the kill switch or a
        /// force-off may already have claimed objects in a zone that is not yet in ServerZoneMembership
        /// (FinishActiveZone is where membership is decided). ReleaseAllHeldZones walks membership only, so those
        /// objects would stay server-owned with nothing tracking them. This hands back every server-owned object in
        /// that zone's snapshot. It runs the same whichever of the two flush callbacks goes first, and it is skipped
        /// when the zone is already held (ReleaseAllHeldZones covers it) or O10 holds it (O10's own flush does).
        /// </summary>
        private static void HandBackInFlightClaims()
        {
            try
            {
                if (s_activeZone == null || s_activeRole != ZoneRole.Candidate || !s_activeAnyVerified) return;
                if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null) return;
                Vector2s zone = s_activeZone.Value;
                if (ServerZoneMembership.IsServerHeld(zone) || SharedAreaZoneHold.IsHeld(zone)) return;

                long sessionId = ZDOMan.GetSessionID();
                int found = 0, released = 0;
                foreach (ZDOID id in s_activeSnapshot)
                {
                    ZDO zdo = ZDOMan.instance.GetZDO(id);
                    if (zdo == null || zdo.GetOwner() != sessionId) continue;
                    if (ZoneSystem.GetZone(zdo.GetPosition()) != zone) continue;
                    found++;
                    if (OwnershipApi.SetOwner(zdo, 0, "Ownership.ClaimScanner in-flight pass handed back on flush")) released++;
                }
                if (found > 0)
                    NoodlesLog.Info($"ClaimScanner: flush handed back {released}/{found} object(s) the in-flight pass " +
                        $"had claimed in zone {zone} before it was recorded as held.");
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(ClaimScanner) + ".HandBackInFlightClaims", e);
            }
        }

        [NoodlesModuleInit]
        internal static void Init()
        {
            s_frameBudgetMs = NoodlesConfig.File.Bind("OwnershipAuthority", "ClaimScanBudgetMs", 0.5f,
                new ConfigDescription(
                    "1.0.10 claim-pass redesign -- wall-clock time budget PER FRAME for the continuous " +
                    "claim/release scanner (ClaimScanner), replacing the old once-a-second burst pass that ran " +
                    "up to the old MaxZonesClaimedPerCycle zones' full vanilla ReleaseNearbyZDOS sweep " +
                    "synchronously in the one frame the cycle landed on -- the confirmed source of steady ~1/s " +
                    "50-100ms server frames on Proxima (ownership.o6.outOfZoneClaimSkipped/selfReleaseKept/" +
                    "expectedClaimRefusedByPolicy summing to roughly 12,000 vetoed SetOwner calls/s with nothing " +
                    "new to claim). Knee-of-curve default: 0.5ms/frame at a typical 20-60fps dedicated-server " +
                    "tick rate is 10-30ms of total scanner budget per second -- comfortably enough to keep up " +
                    "with a realistic held+candidate zone set's own (small) per-zone object counts, at the " +
                    "measured per-object cost, without ever costing a single frame more than this. Always makes " +
                    "at least one unit of progress per frame regardless of this value (never livelocks on a " +
                    "pathologically tiny budget). Safe max 5ms -- above that this one subsystem is back to " +
                    "spending a meaningful slice of a 33ms frame, the same class of problem this redesign exists " +
                    "to fix; No vanilla equivalent.",
                    new AcceptableValueRange<float>(0.1f, 5f)));

            Telemetry.RegisterPeriodicPublisher(PublishTelemetryAndReset);
            KillSwitch.RegisterFlushCallback("Ownership.ClaimScanner", ClearAllState);
        }
    }
}
