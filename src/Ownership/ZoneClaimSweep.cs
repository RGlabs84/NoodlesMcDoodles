using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.Ownership
{
    /// <summary>
    /// G3-L1 + O3 + O6 (docs/design/03-ownership-and-handover.md, Hooks H1/H2/H3) — the server-owns-zones
    /// primitive itself: which zones the server is allowed to hold, how it makes a claim durable against
    /// vanilla's own per-peer reassignment sweep, and how fast the whole sweep runs. Kept in one file because
    /// H2 (docs/design/03) treats O3's cadence and O6's claim pass as one hook on one vanilla method, sharing
    /// the same candidate-zone computation.
    ///
    /// - <b>G3-L1 (H1)</b>: postfix on <c>ZDOMan.IsInPeerActiveArea(Vector3, long)</c> (SERVER:76934-76946,
    ///   confirmed this pass) — vanilla's own predicate always tests the server's pinned, permanently-off-map
    ///   reference position for the server's own "presence", so a server-claimed object gets handed straight
    ///   back to the first peer in range within one <c>ReleaseZDOS</c> tick. This postfix makes the predicate
    ///   return <c>true</c> for the server's own held zones instead, closing that gap structurally.
    /// - <b>O3 (H2 transpiler)</b>: swaps <c>ReleaseZDOS</c>'s hardcoded <c>2f</c> cadence literal (SERVER:76921,
    ///   confirmed this pass: <c>if (!(m_releaseZDOTimer &gt; 2f)) return;</c>) for a configurable field.
    /// - <b>O6 (H2 postfix + H3)</b>: once per (now-configurable) <c>ReleaseZDOS</c> tick, re-derives which
    ///   zones the server may hold — <see cref="ZoneSimGate.CanHold"/> (seam: zone-sim module, docs/design/04,
    ///   built in parallel — no provider means no hold, ever, by design), no connected peer's G3-N1-padded
    ///   active area overlapping the zone — and reuses vanilla's own <c>ReleaseNearbyZDOS</c> (SERVER:76948-76971,
    ///   unmodified, NOT reimplemented — the whole doc is explicit that reimplementing this loop is unnecessary
    ///   and risks drift) to actually make the claim.
    /// </summary>
    internal static class ZoneClaimSweep
    {
        // --- O3: cadence -----------------------------------------------------------------------------------

        // SERVER:76921, confirmed this pass. The transpiler below reads ActiveInterval (a live static field, not
        // a captured local) on every call, so a config/kill-switch change takes effect on the very next tick —
        // the shared-infra contract's "transpiler reads a static field that holds the vanilla literal when
        // disabled" rule (DESIGN.md Architecture).
        internal const float VanillaReleaseZdosInterval = 2f;
        private static float s_activeInterval = VanillaReleaseZdosInterval;

        /// <summary>Read by the transpiler's inserted call. Recomputed on every config change and on every kill-switch flip via the same path every other lever in this codebase uses (live read, no caching).</summary>
        internal static float ActiveInterval()
        {
            s_activeInterval = Features_Ownership.OwnershipAuthority.Active
                ? Mathf.Clamp(s_releaseZdosInterval.Value, 0.5f, 2.0f)
                : VanillaReleaseZdosInterval;
            return s_activeInterval;
        }

        // ZRpc's own `m_releaseZDOTimer` -- private instance field on ZDOMan, SERVER:76921's own gate.
        private static readonly AccessTools.FieldRef<ZDOMan, float> s_releaseZdoTimerRef =
            AccessTools.FieldRefAccess<ZDOMan, float>("m_releaseZDOTimer");

        /// <summary>
        /// BLOCKER FIX (lead review): <c>ReleaseZDOS</c> itself is called every frame (<c>ZNet.Update -&gt;
        /// ZDOMan.Update -&gt; ReleaseZDOS</c>, SERVER:80089/76864) — vanilla's own cadence gate
        /// (<c>m_releaseZDOTimer &gt; threshold</c>) is INSIDE the method body, so any bare postfix on this
        /// method runs on every frame, not once per configured interval. Shared by every postfix on this method
        /// in this module (this file's own claim pass, LatencyAwareOwnership's O9 policy, OwnershipLossWatch's
        /// revision scan) so each does its real work at most once per <see cref="ActiveInterval"/>.
        ///
        /// Called from a PREFIX, before vanilla's own body has run its <c>+= dt</c>/reset — computed from the
        /// timer's PRE-call value plus <paramref name="dt"/> against the SAME threshold the (transpiled) body
        /// itself will compare against, so it is exact regardless of frame-time size: a before/after comparison
        /// of the field's own value would miss the case where the timer starts at/near 0 and a single long
        /// frame's <paramref name="dt"/> alone exceeds the threshold (both before and after would read ~0, since
        /// vanilla resets to 0f on the very call that ticks) — this avoids that entirely by never comparing
        /// before vs. after, only predicting what the body is about to do.
        /// </summary>
        internal static bool WillPerformReleaseTick(ZDOMan instance, float dt)
        {
            if (s_releaseZdoTimerRef == null || instance == null) return true; // fail open: can't tell -> don't suppress real work
            float current = s_releaseZdoTimerRef(instance);
            return (current + dt) > ActiveInterval();
        }

        // --- Config ------------------------------------------------------------------------------------------

        private static ConfigEntry<float> s_releaseZdosInterval;
        private static ConfigEntry<float> s_peerRefPosPadding;
        private static ConfigEntry<float> s_claimGraceWindow;

        // --- Reflection: private ZoneSystem.m_zones / ZDOMan.ReleaseNearbyZDOS --------------------------------

        // ZoneSystem.ZoneData (SERVER:112639) is `private class` -- can't be named in our own source (CS0122,
        // same constraint PeersPerTick.cs/SendPriority.cs already document for ZDOMan's own private nested
        // types) -- so m_zones is read as a plain IDictionary; we only ever need its KEYS (Vector2s).
        private static readonly FieldInfo s_zonesField = AccessTools.Field(typeof(ZoneSystem), "m_zones");

        // 1.0.10 (claim-pass redesign): ReleaseNearbyZDOS(Vector3, long) -- SERVER:76948 -- is no longer called
        // from this file at all. ClaimScanner.cs replaces H3's "reuse vanilla's own bulk delegate" approach with
        // a targeted, per-object ZDO.SetOwner call for exactly the objects TryPrepareClaim's successor
        // (ClaimScanner's own per-object walk) already knows need to change hands -- see that file's class
        // remarks for the full equivalence argument (both the claim/branch-2 and the release/branch-1 side).
        // The reflection lookup + force-off-on-failure this used to need is gone with it.

        // --- State (main-thread only: both patches below only ever fire from ZDOMan.Update's server-only path,
        // i.e. the same single-threaded tick loop every other Update-driven vanilla system runs on; no lock
        // needed, unlike ServerZoneMembership which is read from other modules/threads too) -----------------

        // round-3 install-gate fix (MAJOR, flagged by 4 reviewers): plain internal bound, not a config key --
        // same reasoning as LoadShedder.MaxZonesExaminedPerCycle (an internal cost bound, not a policy knob).
        // Caps how many zones losing claim candidacy get a real ReleaseZoneOwnership scan+SetOwner pass in one
        // ReleaseZDOS tick, so a mass event (a ModeQuarantine trip, a ZoneCap contraction, a player teleporting
        // off a large near-field-held base) degrades to a bounded release rate instead of one synchronous hitch.
        private const int MaxZonesReleasedPerCycle = 32;

        // --- H3 guard (b) + G3-N1: peer-overlap test, expanded by the configured padding ----------------------

        /// <summary>
        /// Deliberately does NOT reimplement <c>ZNetScene.PointInsideActiveArea</c>'s exact per-NearSimulationDistance
        /// branching (SERVER:82344-82365: 1.5x zoneSize in the common case, 1x at NearSimulationDistance==1, an
        /// additional circular narrowing at ==2) — that's real vanilla decision logic doc 03 warns against
        /// reimplementing. Instead this uses the COMMON-CASE constant (1.5x zoneSize == 96m at the default 64m
        /// zone size), which is exactly correct for NearSimulationDistance &gt;= 3 (the upper half of O1's own
        /// practical 0-4 band) and, for the two smaller special cases, MORE conservative than vanilla's own
        /// (smaller) true active-area radius -- i.e. this never excludes a zone vanilla itself would still call
        /// "in active area", only ever the safe direction. G3-N1's own configured padding is added on top of
        /// that base, as a plain Chebyshev (matches vanilla's own <c>point.y = 0f;</c> flattening and max(|dx|,|dz|)
        /// shape at SERVER:82355) distance check -- not a call to `Utils.ChebyshevDistance` itself, since that
        /// helper lives in `assembly_utils.dll` with unconfirmed accessibility; the 3-line reimplementation here
        /// is pure geometry, not game decision logic, so it carries none of the drift risk the "don't reimplement"
        /// rule is actually about.
        ///
        /// <b>SAA WP-A (scratchpad/saa/final.md §2):</b> made <c>internal</c>, not <c>private</c> -- the one
        /// signature change this task's design calls for. Reused as a pure predicate by
        /// <see cref="NearFieldClaimPolicy.IsClaimable"/> to decide, per OBJECT (against that object's OWN
        /// zone, not necessarily the zone currently being claimed), whether it's in the always-unchanged
        /// far-band or in near-field territory this task's own Mode/exclusion logic governs. Body and
        /// behaviour below are byte-for-byte unchanged by that visibility change.
        /// </summary>
        internal static bool AnyPeerWithinPaddedActiveArea(Vector2s zone, List<ZNetPeer> peers)
        {
            float baseRadius = ZoneSystem.instance.m_zoneSize * 1.5f;
            float padding = Mathf.Clamp(s_peerRefPosPadding.Value, 0f, 192f);
            float effectiveRadius = baseRadius + padding;

            Vector3 zoneCenter = ZoneSystem.GetZonePos(zone);
            foreach (var peer in peers)
            {
                if (peer == null) continue;
                Vector3 refPos = peer.GetRefPos();
                float dx = Mathf.Abs(zoneCenter.x - refPos.x);
                float dz = Mathf.Abs(zoneCenter.z - refPos.z);
                if (Mathf.Max(dx, dz) <= effectiveRadius) return true;
            }
            return false;
        }

        // --- H3 guard (a) + (b) + seam #2: candidate zones this cycle is even allowed to consider -------------

        // 1.0.10: widened private -> internal so ClaimScanner.cs's own FinishActiveZone (the successor to this
        // file's old ClaimZone/RunClaimCycle release-diffing loop) can tell a TTL eviction apart from an
        // ordinary candidacy-lost release, exactly as this method's one pre-1.0.10 call site already did. Same
        // "visibility-only, behaviour unchanged" precedent as AnyPeerWithinPaddedActiveArea's own widening above.
        internal static bool StillInZoneSystem(Vector2s zone)
        {
            var dict = ReadZonesDict();
            return dict != null && dict.Contains(zone);
        }

        private static IDictionary ReadZonesDict()
        {
            if (s_zonesField == null || ZoneSystem.instance == null) return null;
            return (IDictionary)s_zonesField.GetValue(ZoneSystem.instance);
        }

        private static List<Vector2s> ComputeCandidateZones(List<ZNetPeer> peers)
        {
            var result = new List<Vector2s>();
            var dict = ReadZonesDict();
            if (dict == null) return result;

            // SAA WP-A (scratchpad/saa/final.md §2): at OwnershipAuthorityMode.SharedFar (including the
            // quarantined-down effective value), this loop's own behaviour is byte-for-byte identical to
            // pre-SAA code -- a peer-overlapped zone is disqualified exactly as before. At FarOrShared/All, a
            // peer-overlapped zone becomes a CANDIDATE too ("near-field eligibility") unless the load shedder
            // has it shed for this cycle. This method only decides which zones even get a real
            // ReleaseNearbyZDOS sweep attempted at all -- the actual per-OBJECT decision of what's safe to
            // claim inside a near-field zone is NearFieldClaimVeto/NearFieldClaimPolicy's job (a separate
            // enforcement point on ZDO.SetOwner itself), never this method's.
            bool nearFieldEligible = NearFieldClaimPolicy.EffectiveMode != NearFieldClaimPolicy.OwnershipAuthorityMode.SharedFar;

            foreach (Vector2s zone in dict.Keys)
            {
                // Seam #2 (CORE-API.md): no provider (the zone-sim module isn't built/active yet in this parallel
                // build) means CanHold is false for everything -- O6 claims nothing until that module registers
                // a provider. This is the documented, correct safe default, not a bug in this file.
                if (!ZoneSimGate.CanHold(zone)) continue;

                bool peerNearby = AnyPeerWithinPaddedActiveArea(zone, peers);
                if (peerNearby)
                {
                    if (!nearFieldEligible) continue;          // SharedFar: unchanged -- still disqualifies
                    if (LoadShedder.IsZoneShed(zone)) continue; // shed zone: never near-field-claim it either
                }
                result.Add(zone);
            }
            return result;
        }

        // CROSS-MODULE HAZARD -- FIXED (integrator pass, 2026-09-28): a peer-overlapped zone that becomes a
        // near-field CANDIDATE above (peerNearby==true, nearFieldEligible) and is then successfully claimed
        // lands in the exact same ServerZoneMembership store (ClaimZone, below) as a genuine far-band
        // (no-peer-anywhere-near) claim -- this store no longer means "always >=~160m from every peer" the
        // instant near-field claiming is live (the shipped FarOrShared default). HeldBandPreservationGuard.
        // TryGetHeldBandZone used to assume that invariant and read ONLY ServerZoneMembership.IsServerHeld,
        // which would have wrongly frozen WearNTear damage and wildlife-vs-tame combat for a near-field-claimed
        // zone right next to a player -- contradicting "where players are present, gameplay is normal." Fixed
        // THERE (not here): TryGetHeldBandZone now also requires
        // !ZoneClaimSweep.AnyPeerWithinPaddedActiveArea(zone, ZNet.instance.GetPeers()) (this method, already
        // internal) alongside IsServerHeld, so it only fires for the genuinely empty held ring.

        // --- H3 guard (c), reframed: see class remarks on ClaimGraceTracker below ----------------------------

        /// <summary>
        /// H3 guard (c) as originally specified ("no object in the zone is within O6's grace window of a
        /// just-granted RPC_Request* handshake") has no concrete hook anywhere in docs/design/03's own H1-H10 --
        /// the container-open grant/response pair it refers to belongs to <c>Container.RPC_RequestOpen</c>, which
        /// is out of `src/Ownership/**` scope (docs/design/05, the Gameplay module's own territory) and doc 03
        /// never actually adds a patch for it. Rather than invent an out-of-scope hook for a race that seam #1
        /// (<see cref="ReclaimGate"/>) already dominates -- its safe default refuses reclaiming ANY currently-owned
        /// object at all until the Gameplay module (M4) registers a real guard, which is strictly more conservative
        /// than a 5s grace window -- <see cref="OwnershipAuthority.ClaimGraceWindow"/> is implemented here as a
        /// real, self-contained cooldown: this module will not re-claim or re-reassign a ZDO within the configured
        /// window of ITS OWN last claim/reassignment of that exact ZDO. This closes a genuinely different, real
        /// hazard entirely within this module's own reach (rapid self-thrash right at a zone or RTT boundary,
        /// which nothing else in this file otherwise bounds) rather than leaving the config key unconsumed.
        /// </summary>
        internal static class ClaimGraceTracker
        {
            private static readonly Dictionary<ZDOID, float> s_lastTouch = new Dictionary<ZDOID, float>();

            public static bool IsWithinGraceWindow(ZDOID id)
            {
                if (!s_lastTouch.TryGetValue(id, out float touchedAt)) return false;
                return (Time.time - touchedAt) < Mathf.Clamp(s_claimGraceWindow.Value, 3f, 10f);
            }

            public static void MarkTouched(ZDOID id) => s_lastTouch[id] = Time.time;

            // Unbounded-growth guard: entries are only ever meaningful for ~10s: (Time.time - touchedAt) both
            // sides of the comparison monotonically increase, so a stale entry just always reads "not within
            // window" forever after -- harmless correctness-wise, but the dictionary would otherwise grow for
            // the life of the process on a busy server. Swept opportunistically from the same tick O6 already
            // runs on, no separate timer (CORE-API.md "don't start a second periodic driver").
            public static void SweepStale()
            {
                if (s_lastTouch.Count < 4096) return;
                float cutoff = Time.time - 10f;
                var stale = new List<ZDOID>();
                foreach (var kv in s_lastTouch) { if (kv.Value < cutoff) stale.Add(kv.Key); }
                foreach (var id in stale) s_lastTouch.Remove(id);
            }

            internal static void Clear() => s_lastTouch.Clear();
        }

        /// <summary>
        /// 1.0.7 (claim churn fix): how much of O6's claiming is re-claiming. Every verified claim is a real
        /// ownership change to the server (TryPrepareClaim never expects an object the server already owns), so a
        /// claim of an object the server already claimed within the last minute means it left the server in
        /// between. ownership.o11.claims only counts near-field attempts; these count every zone.
        /// </summary>
        internal static class ClaimChurnTracker
        {
            private const float WindowSeconds = 60f;
            private static readonly Dictionary<ZDOID, float> s_lastClaim = new Dictionary<ZDOID, float>();
            private static readonly Counter s_claimsVerified = Telemetry.GetCounter("ownership.o6.claimsVerified");
            private static readonly Counter s_reclaimedWithinMinute = Telemetry.GetCounter("ownership.o6.reclaimedWithinMinute");
            private static float s_nextSweep;

            internal static void RecordClaim(ZDOID id)
            {
                s_claimsVerified.Increment();
                float now = Time.time;
                if (s_lastClaim.TryGetValue(id, out float at) && now - at < WindowSeconds) s_reclaimedWithinMinute.Increment();
                s_lastClaim[id] = now;
            }

            // Same unbounded-growth guard as ClaimGraceTracker.SweepStale, on the same tick, at most every 10 s.
            internal static void SweepStale()
            {
                if (s_lastClaim.Count < 4096 || Time.time < s_nextSweep) return;
                s_nextSweep = Time.time + 10f;
                float cutoff = Time.time - WindowSeconds;
                var stale = new List<ZDOID>();
                foreach (var kv in s_lastClaim) { if (kv.Value < cutoff) stale.Add(kv.Key); }
                foreach (var id in stale) s_lastClaim.Remove(id);
            }

            internal static void Clear() => s_lastClaim.Clear();
        }

        // --- H2 postfix: the per-tick REFRESH (1.0.10 claim-pass redesign) -------------------------------------
        //
        // Pre-1.0.10, this method ITSELF did the heavy lifting: a round-robin loop calling ClaimZone (below, now
        // removed) for up to MaxZonesClaimedPerCycle zones synchronously, each a full vanilla ReleaseNearbyZDOS
        // 5x5-sector sweep -- all in the ONE frame this tick happened to land on. That was the confirmed source
        // of steady ~1/s 50-100ms server frames on Proxima: with nothing new to claim, ClaimZone still ran
        // vanilla's call every time, producing ~12,000/s vetoed SetOwner attempts (ownership.o6.
        // outOfZoneClaimSkipped + selfReleaseKept + expectedClaimRefusedByPolicy) for zero real effect.
        //
        // 1.0.10 (coordinator design change, "a fast coroutine that ties in to the main tick only when needed"):
        // the actual per-object claim/release work moved to ClaimScanner.cs, a continuous scanner that spends at
        // most a small configured time budget PER FRAME (ClaimScanBudgetMs), stepping one already-known ZDO at a
        // time, calling ZDO.SetOwner only for an object that actually needs to change hands -- never vanilla's
        // ReleaseNearbyZDOS, never a 5x5 sweep. This method's remaining job is just the CHEAP, infrequent half:
        // recompute which zones are even eligible (unchanged from pre-1.0.10: ComputeCandidateZones, still O(zone
        // count) but a simple dictionary-keys walk with delegate/peer-overlap checks, no ZDO scanning), and hand
        // that set to ClaimScanner.RefreshZoneRoles, which derives each zone's role (claim candidate vs.
        // release-only -- see that method's remarks, including the release-diffing/candidacy-lost case this
        // method used to handle inline) and marks anything newly relevant "due" for an early turn. Still gated
        // the exact same way (Patch_ZDOMan_ReleaseZDOS_ClaimPass's own Prefix/__state, i.e. once per
        // ActiveInterval), so nothing about WHEN this refresh happens changed, only what it does.
        private static void RunClaimCycle()
        {
            // SAA WP-A: cheap (one enum compare unless a real transition happened) -- doubles as the
            // "start-up/deployment line states the effective Mode" requirement's live-update half; the
            // one-time startup line itself is logged from NearFieldClaimPolicy.Init().
            NearFieldClaimPolicy.LogEffectiveModeIfChanged();

            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            List<Vector2s> candidates = ComputeCandidateZones(peers);

            // Cheap zone-set/role bookkeeping only -- see ClaimScanner.RefreshZoneRoles for the release-diffing
            // (a zone losing claim candidacy) and stray-ring (HARD REQUIREMENT (a) equivalence) logic this method
            // used to run inline. No ZDO is touched here; PumpFrame (every frame, time-budgeted) does that.
            ClaimScanner.RefreshZoneRoles(candidates);

            ClaimGraceTracker.SweepStale();
            ClaimChurnTracker.SweepStale();
            SetOwnerHoldGuard.SweepStaleHolds(); // O7's own paranoid backstop -- see that file's remarks; reuses this tick rather than starting a second driver

            // SAA WP-A (final.md §2): the retroactive corrective pass -- a claim-time veto alone can't undo a
            // Pickable claimed on an EARLIER pass, before a peer walked up. Once per refresh (this tick's
            // ActiveInterval), independent of ClaimScanner's own per-frame cadence -- "saa:compat-pickable" owns
            // the actual scan/release logic in PickableRelease.cs; this is just the one call site, same as every
            // other per-cycle housekeeping call in this method.
            PickableRelease.RunReleasePass();

            // 1.0.10: repurposed from "zones actually claimed this cycle" (meaningless now that claiming is
            // continuous, not cyclic) to "zones currently eligible to claim" -- still a useful at-a-glance
            // number for the same log line, just a different question. ClaimScanner's own new
            // ownership.o6.scanObjectsPerSec/setOwnerCallsPerSec/claimSliceMsMax gauges are the real throughput
            // signals for this redesign.
            Telemetry.SetGauge("ownership.zonesProcessedPerCycle", candidates.Count);
            Telemetry.SetGauge("ownership.serverHeldZones", ServerZoneMembership.Count);
            // round-3 install-gate fix (BLOCKER 2c): piggybacks this already-scheduled per-cycle gauge line
            // rather than adding a second one on the every-frame ReleaseZDOS postfix -- see
            // TickPendingZoneReleaseRetries's own remarks for why that path stays gauge-free.
            Telemetry.SetGauge("ownership.pendingZoneReleaseCount", s_pendingRelease.Count);
        }

        /// <summary>"The server holds this zone" -- G3-L1's own test, shared with NearFieldClaimVeto's 1.0.7 release
        /// check so both always agree on what "held" means.</summary>
        internal static bool IsHeldForServer(Vector2s zone) =>
            ServerZoneMembership.IsServerHeld(zone) || SharedAreaZoneHold.IsHeld(zone);

        // --- Patches -------------------------------------------------------------------------------------------

        /// <summary>G3-L1 (H1). Both sides registered (see Init's own remarks) but inert on a real client — the
        /// vanilla method this postfixes is only ever called from ReleaseZDOS's own IsServer()-gated body.</summary>
        [HarmonyPatch(typeof(ZDOMan), "IsInPeerActiveArea")]
        internal static class Patch_ZDOMan_IsInPeerActiveArea_ServerHold
        {
            static void Postfix(Vector3 point, long uid, ref bool __result)
            {
                // O10 EXTENSION (experimental, default off, redesign proposals[1].mechanics "Hold"): widened to
                // also protect an O10-held object -- O10 needs neither OwnershipAuthority nor O6 active at all
                // (full independence, matching O9's own design intent), so this gate can't be OwnershipAuthority
                // alone any more.
                if (!Features_Ownership.OwnershipAuthority.Active && !O10SharedAreaOwnership.Active) return;
                if (__result) return; // vanilla already says "yes" -- nothing to add
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                try
                {
                    if (uid != ZDOMan.GetSessionID()) return; // only the server's own claim is ever G3-L1's business
                    if (IsHeldForServer(ZoneSystem.GetZone(point))) __result = true;
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_ZDOMan_IsInPeerActiveArea_ServerHold), e);
                }
            }
        }

        /// <summary>O3 (H2 transpiler). Swaps the one `2f` cadence literal for a call to <see cref="ActiveInterval"/>.</summary>
        [HarmonyPatch(typeof(ZDOMan), "ReleaseZDOS")]
        internal static class Patch_ZDOMan_ReleaseZDOS_Cadence
        {
            private static readonly MethodInfo s_activeIntervalGetter =
                AccessTools.Method(typeof(ZoneClaimSweep), nameof(ActiveInterval));

            [HarmonyTranspiler]
            internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            {
                var m = new CodeMatcher(instructions, generator);
                m.MatchStartForward(new CodeMatch(OpCodes.Ldc_R4, VanillaReleaseZdosInterval));
                if (!m.IsValid)
                {
                    NoodlesLog.Error("O3: ReleaseZDOS's 2f cadence literal not found -- leaving ReleaseZDOS " +
                        "completely unpatched by this transpiler (O3 has no effect; O6's claim pass, a separate " +
                        "patch class, is unaffected).");
                    Features_Ownership.OwnershipAuthority?.ForceOff("O3 transpiler anchor missing");
                    return instructions;
                }

                var old = m.Instruction;
                var replacement = new CodeInstruction(OpCodes.Call, s_activeIntervalGetter);
                if (old.labels != null && old.labels.Count > 0) replacement.labels.AddRange(old.labels);
                if (old.blocks != null && old.blocks.Count > 0) replacement.blocks.AddRange(old.blocks);
                m.SetInstruction(replacement);

                return m.InstructionEnumeration();
            }
        }

        /// <summary>O6 (H2 postfix). Separate patch class from O3's transpiler (same target method, independent
        /// PatchRegistry feature-name/force-off isolation -- matches this codebase's own established pattern,
        /// e.g. SendPriority.cs/Timeouts.cs sharing ZNet.Start).</summary>
        [HarmonyPatch(typeof(ZDOMan), "ReleaseZDOS")]
        internal static class Patch_ZDOMan_ReleaseZDOS_ClaimPass
        {
            // BLOCKER FIX (lead review): see WillPerformReleaseTick's own remarks -- ReleaseZDOS is called every
            // frame; this predicts (before vanilla's own body runs) whether THIS call is a real release tick, so
            // the claim cycle below runs at most once per ActiveInterval, not once per frame.
            static void Prefix(ZDOMan __instance, float dt, out bool __state)
            {
                // KNOWN-RISK FIX carried over (docs/impl/OWNERSHIP-IMPL.md "Known risks" -- "the third
                // unguarded ZDOMan.ReleaseZDOS Prefix", explicitly flagged for whoever next owns this file):
                // matches the try/catch already applied to LatencyAwareOwnership's and OwnershipLossWatch's
                // own ReleaseZDOS Prefixes. An uncaught throw here would abort THIS ENTIRE PATCHED CALL,
                // including vanilla's own release/reassignment body for the frame -- worse than vanilla, not a
                // fallback to it -- and never reaches NoodlesLog.PatchException, so it would feed neither
                // AutoStandDown nor FeatureStandDown either.
                try
                {
                    __state = Features_Ownership.OwnershipAuthority.Active && WillPerformReleaseTick(__instance, dt);
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_ZDOMan_ReleaseZDOS_ClaimPass) + ".Prefix", e);
                    __state = false; // skip this tick's claim pass; vanilla's own body still runs untouched
                }
            }

            static void Postfix(ZDOMan __instance, bool __state)
            {
                // round-3 install-gate fix (BLOCKER 2c): unconditional, and BEFORE the __state/Active-gated
                // block below, on purpose -- a partially-released zone's hand-back must keep retrying even
                // while OwnershipAuthority/the kill switch is off (see TickPendingZoneReleaseRetries's own
                // remarks: that is exactly when ReleaseAllHeldZones is normally called, and this postfix's own
                // __state is false so RunClaimCycle below would not otherwise run at all). Own try/catch so
                // neither this nor the claim-cycle call below can ever skip the other.
                try
                {
                    TickPendingZoneReleaseRetries();
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_ZDOMan_ReleaseZDOS_ClaimPass) + ".PendingReleaseRetry", e);
                }

                if (!__state) return;
                if (__instance == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
                try
                {
                    RunClaimCycle();
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_ZDOMan_ReleaseZDOS_ClaimPass), e);
                }
            }
        }

        // --- Kill switch: release every held zone, per the shared-infra flush-callback contract ----------------

        /// <summary>
        /// BLOCKER FIX (lead review, dl-saa-failbacks): the previous version only cleared
        /// <see cref="ServerZoneMembership"/>'s bookkeeping (a bare <c>HashSet.Remove</c>, confirmed in that
        /// class's own <c>Release</c>) — the real <c>ZDO.m_owner</c> field for every object the server actually
        /// claimed in that zone was never touched, so ownership silently stayed with the server even though both
        /// callers of this function (the per-feature <c>OwnershipAuthority.OnForcedOff</c> quarantine below, and
        /// the global <c>KillSwitch</c> flush) document themselves as releasing "every held zone... not merely
        /// stop granting new ones." That gap matters concretely: <c>ZoneSim.HeldBandPreservationGuard</c> keys
        /// its WearNTear/Ship/ItemDrop/tame-combat protection solely off <c>ServerZoneMembership.IsServerHeld</c>
        /// — the moment that flips false here without the underlying ZDOs actually changing owner, those
        /// objects would still be server-simulated with zero players present, but no longer recognized as
        /// held-band, un-suppressing decay/despawn/damage on exactly the population WP2 exists to protect. Now
        /// hands back every server-owned ZDO in each snapshotted zone (same <c>OwnershipApi.SetOwner(zdo, 0, ...)</c>
        /// pattern <see cref="PickableRelease.RunReleasePass"/> already uses for O11's own retroactive release)
        /// BEFORE clearing that zone's membership entry, so a consumer reading either signal at any point during
        /// this flush sees a state that's at worst "still held, being handed back," never "not held, but still
        /// server-owned."
        ///
        /// round-3 install-gate fix (BLOCKER 2c): "being handed back" could still silently mean "never actually
        /// handed back at all" if a third-party SetOwner veto (see <see cref="ZoneReleaseOutcome"/>/
        /// <see cref="ReleaseZoneOwnership"/> remarks) ate the write -- this used to clear
        /// <see cref="ServerZoneMembership"/> right after regardless. Now only a zone <see cref="ReleaseZoneOwnership"/>
        /// verifies as FULLY handed back gets released; a partial (or throwing) zone stays held/protected and is
        /// queued in <see cref="s_pendingRelease"/> for the short, bounded, self-driving retry
        /// (<see cref="TickPendingZoneReleaseRetries"/>, below) that keeps working even after this flush returns
        /// and OwnershipAuthority/the kill switch is off.
        /// </summary>
        internal static void ReleaseAllHeldZones()
        {
            int zoneCount = 0;
            int zdoCount = 0;
            int partialCount = 0;
            long sessionId = ZDOMan.GetSessionID();

            foreach (Vector2s zone in ServerZoneMembership.Snapshot())
            {
                bool threw = false;
                ZoneReleaseOutcome outcome = default;
                try
                {
                    outcome = ReleaseZoneOwnership(zone, sessionId);
                }
                catch (Exception e)
                {
                    threw = true;
                    // One bad zone must never abort every OTHER held zone's release -- a partial release is
                    // strictly better than a whole-flush abort that leaves everything else stuck server-owned
                    // too (the exact "robust error correction, no failbacks that make things worse" the user's
                    // own instruction asks for). This is a plain internal helper, not a Harmony patch body, so
                    // NoodlesLog.PatchException's patch-type-name lookup wouldn't resolve it to a feature
                    // anyway -- log directly instead.
                    Telemetry.GetCounter("ownership.zoneReleaseErrors").Increment();
                    NoodlesLog.Warning($"ZoneClaimSweep.ReleaseAllHeldZones: releasing zone {zone} threw " +
                        $"({e.GetType().Name}: {e.Message}) -- continuing with every other held zone regardless; " +
                        "this zone's membership entry is left held (protected), not cleared, and queued for retry.");
                }

                // round-3 install-gate fix (BLOCKER 2c): only drop this zone's membership -- and with it, its
                // WP2 held-band protection -- once every server-owned ZDO ReleaseZoneOwnership found is verified
                // gone. A throw counts as "unverified", exactly like a partial release: never release membership
                // on anything less than a confirmed full hand-back (mirrors ClaimZone's own anyVerified gate,
                // and (b)'s identical gate in RunClaimCycle's own release-diffing loop above).
                if (!threw && outcome.FullyReleased)
                {
                    ServerZoneMembership.Release(zone);
                    zoneCount++;
                    zdoCount += outcome.Released;
                    s_pendingRelease.Remove(zone); // in case an earlier flush had already queued this same zone
                    continue;
                }

                partialCount++;
                if (!s_pendingRelease.TryGetValue(zone, out PendingZoneRelease pending))
                {
                    // FirstAttemptAt is set once, here, and never refreshed by a later flush call -- the ~60s
                    // bound (TickPendingZoneReleaseRetries, below) is measured from the first time this zone was
                    // ever found stuck, not restarted every time OnForcedOff/the kill switch happens to fire again.
                    pending = new PendingZoneRelease { FirstAttemptAt = Time.time };
                    s_pendingRelease[zone] = pending;
                }
                pending.LastAttemptAt = Time.time;

                if (!threw)
                {
                    zdoCount += outcome.Released; // partial credit -- some of this zone's ZDOs DID come back
                    NoodlesLog.WarningRateLimited(NoodlesLogLevel.Normal, "ownership.zoneReleasePartialFlush",
                        $"ZoneClaimSweep.ReleaseAllHeldZones: zone {zone} partially released ({outcome.Released}/" +
                        $"{outcome.Found} server-owned ZDO(s) handed back, {outcome.StillOwned} still server-owned " +
                        "-- third-party SetOwner veto). Keeping this zone held/protected, queued for a short " +
                        "bounded retry.", 5, 60f);
                }
            }

            ClaimGraceTracker.Clear();
            ClaimChurnTracker.Clear();

            if (zoneCount > 0 || partialCount > 0)
            {
                Telemetry.GetCounter("ownership.killSwitch.zonesFlushed").Add(zoneCount);
                Telemetry.GetCounter("ownership.killSwitch.zdosReleased").Add(zdoCount);
                if (partialCount > 0) Telemetry.GetCounter("ownership.killSwitch.zonesPendingRetry").Add(partialCount);
                NoodlesLog.Info($"ZoneClaimSweep: released {zoneCount} held zone(s) in full, handed back " +
                    $"{zdoCount} server-owned ZDO(s), on kill-switch/force-off flush." +
                    (partialCount > 0
                        ? $" {partialCount} zone(s) only partially released -- left held/protected, queued for a " +
                          $"bounded retry (every {PendingReleaseRetryInterval:F0}s, up to {PendingReleaseGiveUpAfter:F0}s)."
                        : ""));
            }
        }

        // --- round-3 install-gate fix (BLOCKER 2c): bounded, self-driving retry for a zone ReleaseAllHeldZones
        // could only PARTIALLY hand back. The zone stays in ServerZoneMembership (never released -- that is
        // what keeps its WP2 protection on) AND goes in here so this short retry keeps trying the hand-back on
        // later ticks. Has to keep working EVEN WHILE OwnershipAuthority/the kill switch is off -- that is
        // exactly when ReleaseAllHeldZones is normally called in the first place, and RunClaimCycle's own
        // natural per-cycle retry (its release-diffing loop above) stops running the moment that happens. Driven
        // from Patch_ZDOMan_ReleaseZDOS_ClaimPass's own postfix, below, deliberately placed OUTSIDE that
        // postfix's Active-gated block so neither the feature nor the kill switch being off can ever stop it. ---

        private sealed class PendingZoneRelease
        {
            public float FirstAttemptAt;
            public float LastAttemptAt;
        }

        // Main-thread only (same reasoning as every other piece of state in this file -- see the class remarks'
        // own "main-thread only" note) -- only ever touched from ReleaseAllHeldZones (kill-switch/OnForcedOff
        // flush, both documented main-thread-only callers) and TickPendingZoneReleaseRetries (this file's own
        // ReleaseZDOS postfix).
        private static readonly Dictionary<Vector2s, PendingZoneRelease> s_pendingRelease = new Dictionary<Vector2s, PendingZoneRelease>();
        private static int s_pendingRetryRotation;
        private static float s_lastPendingRetryAttempt;

        private const float PendingReleaseRetryInterval = 2f; // "every ~2s" per the round-3 install-gate fix brief
        private const float PendingReleaseGiveUpAfter = 60f;  // "for up to ~60s" -- after this, stop actively retrying (see below), but NEVER release membership

        /// <summary>
        /// Called unconditionally, every frame, from <see cref="Patch_ZDOMan_ReleaseZDOS_ClaimPass"/>'s own
        /// postfix (see that class's own remarks on why a bare postfix on this method fires every frame) --
        /// deliberately never gated on <c>Features_Ownership.OwnershipAuthority.Active</c> or
        /// <see cref="KillSwitch.Enabled"/>, since <see cref="ReleaseAllHeldZones"/> is normally called BECAUSE
        /// one of those just went false. Cheap on the overwhelming majority of calls (an empty-dictionary check)
        /// and self-rate-limited to <see cref="PendingReleaseRetryInterval"/>, so calling it every frame is
        /// safe; never throws (its own try/catch per zone, same reasoning as every other per-zone loop in this
        /// file). A zone that never fully releases within <see cref="PendingReleaseGiveUpAfter"/> just stops
        /// being actively retried -- loudly logged -- and stays held/protected in
        /// <see cref="ServerZoneMembership"/> forever after: the safe direction per "zero data loss ever." Never
        /// destroys or otherwise modifies the still-owned ZDOs themselves, whether it succeeds or gives up.
        /// </summary>
        private static void TickPendingZoneReleaseRetries()
        {
            if (s_pendingRelease.Count == 0) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null) return;

            float now = Time.time;
            // Own cadence, independent of ActiveInterval/WillPerformReleaseTick -- those key off config this
            // module may not even be reading right now if OwnershipAuthority is inactive, which is exactly the
            // case this exists for. Stays "every ~2s" regardless of feature state.
            if (now - s_lastPendingRetryAttempt < PendingReleaseRetryInterval) return;
            s_lastPendingRetryAttempt = now;

            long sessionId = ZDOMan.GetSessionID();
            var zones = new List<Vector2s>(s_pendingRelease.Keys); // snapshot -- the loop body below mutates the dictionary
            List<Vector2s> gaveUp = null;

            // Round-3 install-gate review: capped like the release-diff loop (MaxZonesReleasedPerCycle per pass,
            // rotating start), so a kill-switch flush that left many zones pending cannot hitch every retry pass.
            int total = zones.Count;
            int take = Math.Min(total, MaxZonesReleasedPerCycle);
            int start = s_pendingRetryRotation % total;
            s_pendingRetryRotation = start + take;
            for (int i = 0; i < take; i++)
            {
                Vector2s zone = zones[(start + i) % total];
                ZoneReleaseOutcome outcome;
                try
                {
                    outcome = ReleaseZoneOwnership(zone, sessionId);
                }
                catch (Exception e)
                {
                    Telemetry.GetCounter("ownership.zoneReleaseErrors").Increment();
                    NoodlesLog.Warning($"ZoneClaimSweep: pending-release retry for zone {zone} threw " +
                        $"({e.GetType().Name}: {e.Message}) -- left held/protected and untouched; retrying again " +
                        "next pass.");
                    continue; // unknown outcome -- never treat as success; FirstAttemptAt is untouched, still
                              // subject to the same overall bound.
                }

                if (outcome.FullyReleased)
                {
                    s_pendingRelease.Remove(zone);
                    ServerZoneMembership.Release(zone);
                    Telemetry.GetCounter("ownership.pendingZoneReleaseResolved").Increment();
                    NoodlesLog.Info($"ZoneClaimSweep: pending-release retry succeeded for zone {zone} " +
                        $"({outcome.Released} ZDO(s)) -- fully handed back, membership cleared.");
                    continue;
                }

                PendingZoneRelease pending = s_pendingRelease[zone];
                pending.LastAttemptAt = now;
                if (now - pending.FirstAttemptAt >= PendingReleaseGiveUpAfter)
                    (gaveUp ??= new List<Vector2s>()).Add(zone);
            }

            if (gaveUp == null) return;
            foreach (Vector2s zone in gaveUp) s_pendingRelease.Remove(zone); // stop actively retrying -- ServerZoneMembership is untouched, so the zone stays held/protected indefinitely
            Telemetry.GetCounter("ownership.pendingZoneReleaseGaveUp").Add(gaveUp.Count);
            NoodlesLog.Warning($"ZoneClaimSweep: gave up retrying the hand-back for {gaveUp.Count} zone(s) after " +
                $"{PendingReleaseGiveUpAfter:F0}s of retries every {PendingReleaseRetryInterval:F0}s -- at least " +
                "one server-owned ZDO in each is still being vetoed by a third-party ZDO.SetOwner prefix " +
                "(Wonderland/GetOffMyLawn, see SetOwnerHoldGuard.cs class remarks). Left exactly as-is -- " +
                "untouched, never destroyed or modified -- and still marked server-held so WP2 held-band " +
                "protection stays on; a later successful claim-then-release cycle can still clear these.");
        }

        /// <summary>
        /// round-3 install-gate fix (BLOCKER 2a): <see cref="ReleaseZoneOwnership"/>'s report to its callers --
        /// the release-side mirror of ClaimZone's own anyVerified gate above. <see cref="Found"/> is how many
        /// server-owned ZDOs this pass saw; <see cref="Released"/> is how many of those
        /// <see cref="OwnershipApi.SetOwner"/> itself already verified are no longer server-owned afterward --
        /// that call's own contract (Contracts.cs: "true iff the ZDO's owner is newOwner afterwards") IS the postcondition check
        /// the claim side has to re-derive by hand for its bulk-delegate path (vanilla's <c>ReleaseNearbyZDOS</c>
        /// has no per-ZDO return value to trust); here every write goes through that single-ZDO, already-
        /// verified call, so counting only the ones it confirmed is enough. <see cref="FullyReleased"/> is what
        /// every caller below gates <see cref="ServerZoneMembership.Release"/> on: a still-owned ZDO must keep
        /// its zone's protection on.
        /// </summary>
        private readonly struct ZoneReleaseOutcome
        {
            internal readonly int Found;
            internal readonly int Released;

            internal ZoneReleaseOutcome(int found, int released)
            {
                Found = found;
                Released = released;
            }

            internal bool FullyReleased => Released >= Found;
            internal int StillOwned => Found - Released;
        }

        /// <summary>
        /// Hands back every ZDO this session currently owns in <paramref name="zone"/>, and reports whether that
        /// hand-back was actually verified complete (round-3 install-gate fix (BLOCKER 2a): see
        /// <see cref="ZoneReleaseOutcome"/>). Reuses the exact same <c>FindSectorObjects</c> + <c>SimulationDistance</c>
        /// shape <see cref="TryPrepareClaim"/> already uses to DECIDE what O6 may claim (SERVER:76952-76954's own
        /// call shape) -- releasing the same population a claim could touch, via the PUBLIC enumeration API,
        /// never re-walking `ZoneSystem.m_zones` or reimplementing vanilla's own sector indexing. A fresh local
        /// list (not the shared per-tick <see cref="s_scratchScan"/>) -- round-3 install-gate fix: called from
        /// up to three places now (the capped release-diffing loop in <see cref="RunClaimCycle"/>,
        /// <see cref="ReleaseAllHeldZones"/>'s flush, and <see cref="TickPendingZoneReleaseRetries"/>'s bounded
        /// retry), each already bounded/rate-limited by its own caller, so the per-call allocation here stays
        /// the same order of magnitude it always was.
        /// </summary>
        private static ZoneReleaseOutcome ReleaseZoneOwnership(Vector2s zone, long sessionId)
        {
            if (ZDOMan.instance == null || ZNet.instance == null) return default; // nothing found, nothing owed -- vacuously FullyReleased

            SimulationDistance synced = ZNet.instance.GetSyncedSimulationDistance();
            var nearOnly = new SimulationDistance(synced.NearSimulationDistance, 0, synced.IsClassic);
            var zoneObjects = new List<ZDO>();
            ZDOMan.instance.FindSectorObjects(zone, nearOnly, zoneObjects);

            int found = 0, released = 0;
            foreach (ZDO zdo in zoneObjects)
            {
                if (zdo == null) continue;
                if (zdo.GetOwner() != sessionId) continue; // not server-held right now -- nothing to release
                found++;
                if (OwnershipApi.SetOwner(zdo, 0, "Ownership.ZoneClaimSweep force-off/kill-switch release"))
                    released++;
            }
            return new ZoneReleaseOutcome(found, released);
        }

        // --- Init ------------------------------------------------------------------------------------------------

        [NoodlesModuleInit]
        internal static void Init()
        {
            ConfigFile config = NoodlesConfig.File;

            // "Great improvement over vanilla, not overkill" (coordinator instruction, 2026-09-28): vanilla's own
            // ReleaseZDOS cadence is a fixed 2.0s. DESIGN.md Decision 2 already rejected the user's original 0.6s
            // ask in favour of 1.0s -- a clean 2x, the smallest step that meaningfully halves worst-case handover
            // latency without the unmeasured bandwidth risk 0.6s (3.3x) would add (every claim bumps OwnerRevision
            // and forces a resend, G2-N2's own cross-reference). 0.5s stays reachable as opt-in headroom for an
            // admin who has since measured their own bandwidth can take it.
            s_releaseZdosInterval = config.Bind("OwnershipAuthority", "ReleaseZdosInterval", 1.0f,
                new ConfigDescription("O3 -- ZDOMan.ReleaseZDOS's sweep cadence (claims + vanilla's own per-peer " +
                    "reassignment both ride this same timer). Vanilla: fixed 2.0s. Default 1.0s halves worst-case " +
                    "handover latency for a clearly noticeable gain on a small cross-country server, at roughly " +
                    "2x vanilla's own SetOwner/resend cost -- not the user's originally-requested 0.6s, which " +
                    "DESIGN.md's own Decision 2 defers until telemetry (ownership.zonesProcessedPerCycle, " +
                    "ownership.claimsPerMinute) shows handover latency, not bandwidth, is the real bottleneck at " +
                    "1.0s. 0.5s remains available as opt-in headroom once that measurement exists.",
                    new AcceptableValueRange<float>(0.5f, 2.0f)));

            // CORRECTED (held-band redesign synthesis, 2026-09-28): previously justified against this mod's own
            // negotiated ClientRefPos.Interval cadence (0.5s default) -- but a PLAIN VANILLA peer never
            // negotiates that faster cadence at all (it only applies once the client mod is installed and
            // negotiates); an unmodded client keeps vanilla's own fixed ~2.0s SendServerSyncPlayerData report
            // interval regardless (PokeRingDriver.cs's own PeerReadyState remarks cite CLIENT:80578-80614).
            // Re-derived against that TRUE worst case instead: ~2.0s report lag + up to ~1.0s ActiveInterval
            // claim cadence (this file, above) = ~3.0s combined worst-case staleness before a claim cycle sees a
            // peer's latest position, roughly 45m of movement at an ordinary running speed -- comfortably inside
            // the 64m default (one zone width) below, which stays the smallest padding that closes that window
            // without guessing narrower. The 64m VALUE itself is unchanged by this correction -- only its cited
            // justification was wrong.
            s_peerRefPosPadding = config.Bind("OwnershipAuthority", "PeerRefPosPadding", 64f,
                new ConfigDescription("G3-N1 -- extra radius added to the (~96m) active-area check before a zone " +
                    "counts as \"a peer might be near this\", to absorb reporting lag against a PEER'S OWN worst " +
                    "case, not this mod's own faster negotiated cadence: a plain vanilla peer keeps vanilla's " +
                    "fixed ~2.0s SendServerSyncPlayerData report interval regardless of this mod (see " +
                    "PokeRingDriver.cs's PeerReadyState remarks), plus up to ~1.0s of this file's own " +
                    "ActiveInterval claim cadence -- ~3.0s combined worst-case staleness, ~45m at an ordinary " +
                    "running speed. No vanilla equivalent. Default 64m (one zone width) comfortably covers that " +
                    "worst case without guessing narrower; 192m (3 zone widths) is available as opt-in headroom " +
                    "for a laggier link than the padding was sized against.",
                    new AcceptableValueRange<float>(0f, 192f)));

            s_claimGraceWindow = config.Bind("OwnershipAuthority", "ClaimGraceWindow", 5.0f,
                new ConfigDescription("How long this module waits before re-claiming or re-reassigning a ZDO it " +
                    "just touched itself (self-thrash guard -- see ClaimGraceTracker remarks in ZoneClaimSweep.cs " +
                    "for how this relates to the ReclaimGate seam). No vanilla equivalent. 5s matches the same " +
                    "order-of-magnitude window the container-open race this guard is modelled on uses.",
                    new AcceptableValueRange<float>(3f, 10f)));

            // 1.0.10: MaxZonesClaimedPerCycle removed -- claiming is no longer cyclic (see RunClaimCycle's own
            // remarks), so "how many zones per cycle" no longer means anything. Its role (bound total per-tick
            // cost) is now played by ClaimScanner's own ClaimScanBudgetMs, a wall-clock budget rather than a
            // zone count -- see that file's Init for the replacement config key.

            PatchRegistry.Register("OwnershipAuthority", typeof(Patch_ZDOMan_IsInPeerActiveArea_ServerHold), PatchSide.Both);
            PatchRegistry.Register("OwnershipAuthority", typeof(Patch_ZDOMan_ReleaseZDOS_Cadence), PatchSide.Both);
            PatchRegistry.Register("OwnershipAuthority", typeof(Patch_ZDOMan_ReleaseZDOS_ClaimPass), PatchSide.Both);

            if (s_zonesField == null)
            {
                NoodlesLog.Error("ZoneClaimSweep: ZoneSystem.m_zones did not resolve via reflection -- O6 can " +
                    "never find a candidate zone. Forcing OwnershipAuthority off.");
                Features_Ownership.OwnershipAuthority?.ForceOff("ZoneSystem.m_zones failed to resolve at Awake");
            }
            // 1.0.10: the ZDOMan.ReleaseNearbyZDOS reflection lookup + its own force-off-on-failure guard is gone
            // -- this file (and ClaimScanner.cs) no longer call that vanilla method at all (see RunClaimCycle's
            // own remarks and ClaimScanner's class remarks for the full equivalence argument).

            // Shared-infra contract (CORE-API.md "Flush callbacks"): Core.Enabled false->true->false must never
            // leave a held zone administratively stuck. ServerZoneMembership already registers its own
            // ClearAll() flush callback in NoodlesPlugin -- this one additionally clears this file's own
            // rotation/grace-window state, which ClearAll() has no reason to know about.
            KillSwitch.RegisterFlushCallback("Ownership.ZoneClaimSweep", ReleaseAllHeldZones);

            // WP1 per-feature quarantine: the SAME release paths already wired to the global kill-switch flush
            // above (this file's own zone-membership release, plus SetOwnerHoldGuard's hold release -- both
            // patch classes share this one "OwnershipAuthority" feature flag), now ALSO wired as this feature's
            // own OnForcedOff (CORE-API.md "Flush callbacks" / FeatureFlag.OnForcedOff remarks) -- a per-feature
            // quarantine (a patch's own apply/JIT failure, or FeatureStandDown's exception-burst trip) must
            // release every held zone/hold exactly like a global kill-switch flush does, not merely stop
            // granting new ones. BLOCKER FIX (lead review, dl-saa-failbacks): ReleaseAllHeldZones() itself now
            // actually does that (see its own remarks) -- both this per-feature path and the global kill-switch
            // flush above share that one corrected implementation, so this comment's claim is true of both.
            FeatureFlag ownershipAuthorityFlag = Features_Ownership.OwnershipAuthority;
            if (ownershipAuthorityFlag != null)
            {
                ownershipAuthorityFlag.OnForcedOff = () =>
                {
                    ReleaseAllHeldZones();
                    SetOwnerHoldGuard.ReleaseAllHolds();
                    ClaimScanner.ClearAllState(); // 1.0.10: this file's own cursor/queues -- see that method's remarks
                };
            }
            else
            {
                NoodlesLog.Warning("ZoneClaimSweep: OwnershipAuthority flag not yet registered -- OnForcedOff " +
                    "release callback not wired (should not happen; check module Init order).");
            }
        }
    }
}
