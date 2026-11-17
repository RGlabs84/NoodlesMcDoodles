using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using NoodlesMcDoodles.Core.ConfigFiles;
using UnityEngine;

namespace NoodlesMcDoodles.Ownership
{
    /// <summary>
    /// O9 (docs/design/03-ownership-and-handover.md, Hooks H9 + H10) — latency-aware ownership: among
    /// PLAYER-HELD objects several connected peers can currently see, hand the object to whichever one has the
    /// lower measured RTT (<see cref="RttTracker"/>) instead of leaving it to vanilla's own iteration-order
    /// luck. Default OFF (<c>O9.Active=false</c>), independent of <c>OwnershipAuthority.Active</c> — this is
    /// the interim mitigation DESIGN.md ships ahead of the full server-held-zones primitive, standalone.
    ///
    /// - <b>H10 (new)</b>: postfix on <c>ZDOMan.ReleaseNearbyZDOS(Vector3, long)</c> (SERVER:76948-76971) — the
    ///   only point in the call graph where a given peer pass's near-object set exists before vanilla's own
    ///   shared, private <c>m_tempNearObjects</c> field (SERVER:76140) gets cleared and overwritten by the NEXT
    ///   call. Captures, read-only, which persistent ZDOs each connected peer's own pass saw in range this tick.
    /// - <b>H9</b>: O9's OWN, separate postfix on <c>ZDOMan.ReleaseZDOS(float)</c> — reads H10's finished
    ///   per-tick accumulator (after the outer method's entire body, every pass, has already run) and decides,
    ///   for every multi-peer-sighted candidate, whether a lower-RTT reassignment is warranted.
    ///
    /// RED-TEAM FIX (docs/design/03 RT1 major #3 / RT2 blocker, both already applied in the blueprint): O9 does
    /// NOT reuse O6's own <c>ReleaseZDOS</c> postfix or hold — it has its own hook (this file) and its own hold
    /// usage (<see cref="SetOwnerHoldGuard.AuthorizedSetOwner"/>, single-ZDO, same as everything else in this
    /// module) precisely so it has no functional dependency on <see cref="ZoneClaimSweep"/>/G3-L1/O6/O3/O4/G3-N1/
    /// G4-N3/O8 at all.
    ///
    /// O10 (EXPERIMENTAL, default off, docs/design/03 "server-in-shared-areas" — see
    /// <see cref="O10SharedAreaOwnership"/> for the full design/cost-derivation) EXTENDS this exact pass rather
    /// than adding a third hook: <see cref="RunReassignmentPass"/>'s per-ZDOID loop now also considers the
    /// SERVER a legitimate candidate owner (via <c>candidateUid == sessionId</c>) whenever every sighting peer
    /// is currently far, and <see cref="RunO10HandbackCheck"/> handles the reverse (server -&gt; player) case
    /// <c>RunReassignmentPass</c> used to short-circuit entirely. With <c>O10.Active=false</c> every new branch
    /// below reproduces the pre-O10 code's behaviour byte-for-byte — see each one's own remarks.
    /// </summary>
    internal static class LatencyAwareOwnership
    {
        // --- Config ----------------------------------------------------------------------------------------------

        internal static FeatureFlag O9Flag { get; private set; }
        private static ConfigEntry<bool> s_o9Active;
        private static ConfigEntry<float> s_rttMarginMs;
        private static ConfigEntry<float> s_rttDwellSeconds;

        /// <summary>
        /// LEAD FIX (CORE-API.md "Live-authority toggles: SafeToggle") — O9 moves real ownership (a reassignment
        /// away from the current player owner) just like OwnershipAuthority does, and is explicitly independent
        /// of it, so it needs its own armed gate rather than piggybacking on
        /// <see cref="SimulationDistanceCeiling.OwnershipAuthorityArmed"/>. Same shape as that one: a live
        /// post-boot flip arms only via <see cref="SafeToggle.TryEnable"/>; boot-time (already on in the config
        /// file) arms directly, no forced save — see <see cref="Init"/>'s own remarks.
        /// </summary>
        internal static bool O9Armed { get; private set; }

        // --- Reflection: private ZDOMan.m_tempNearObjects ---------------------------------------------------------

        // SERVER:76140, confirmed private `readonly List<ZDO>` this pass -- same field H3 (ZoneClaimSweep, via
        // FindSectorObjects) reads through the PUBLIC enumeration API instead; this one specifically needs the
        // PRIVATE field itself, because it must observe the exact set ReleaseNearbyZDOS's OWN internal call
        // (not a fresh scan of our own) populated for THIS SPECIFIC uid's pass.
        private static readonly AccessTools.FieldRef<ZDOMan, List<ZDO>> s_tempNearObjectsRef =
            AccessTools.FieldRefAccess<ZDOMan, List<ZDO>>("m_tempNearObjects");

        // --- H10: per-tick sightings accumulator (main-thread only, cleared by H9 after each tick) ----------------

        // ZDOID -> distinct peer uids that saw it in range this ReleaseZDOS tick.
        private static readonly Dictionary<ZDOID, List<long>> s_sightingsThisTick = new Dictionary<ZDOID, List<long>>();

        [HarmonyPatch(typeof(ZDOMan), "ReleaseNearbyZDOS")]
        internal static class Patch_ZDOMan_ReleaseNearbyZDOS_SightingCapture
        {
            static void Postfix(ZDOMan __instance, long uid)
            {
                // O10 EXTENSION (experimental, default off): O10 shares this exact capture pass (H10) rather
                // than duplicating it -- see O10SharedAreaOwnership's own class remarks for why it's an O9
                // extension, never a second hook. With O10 off this reads identically to "O9.Active" alone.
                if (!Features_Ownership.O9.Active && !O10SharedAreaOwnership.Active) return;
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                if (uid == ZDOMan.GetSessionID()) return; // self-pass / O6's own claim-pass calls are not peer sightings
                try
                {
                    List<ZDO> nearObjects = s_tempNearObjectsRef(__instance);
                    if (nearObjects == null) return;
                    foreach (ZDO zdo in nearObjects)
                    {
                        if (!zdo.Persistent) continue; // mirrors vanilla's own first check, SERVER:76951
                        if (!s_sightingsThisTick.TryGetValue(zdo.m_uid, out var uids))
                        {
                            uids = new List<long>(2);
                            s_sightingsThisTick[zdo.m_uid] = uids;
                        }
                        if (!uids.Contains(uid)) uids.Add(uid);
                    }
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_ZDOMan_ReleaseNearbyZDOS_SightingCapture), e);
                }
            }
        }

        // --- H9: dwell tracking + the reassignment decision -------------------------------------------------------

        private sealed class DwellState
        {
            public long CandidateUid;
            public float QualifyingSinceRealtime;
        }
        private static readonly Dictionary<ZDOID, DwellState> s_dwell = new Dictionary<ZDOID, DwellState>();

        // O10 FINDING FIX (blocker, O10 findings review): SharedAreaZoneHold is a per-ZONE refcount with no way
        // to find "which live hold belongs to a ZDO that just vanished" on its own -- this is that missing
        // ZDOID->zone map, owned here (not in SharedAreaZoneHold itself, which stays a pure zone registry) since
        // only this module ever knows which ZDOID a given claim was for. Populated exactly where
        // SharedAreaZoneHold.Claim is called below, consumed by RunO10HandbackCheck's own Release call (using
        // the CLAIM-time zone, not a recomputed current position -- correct even if the object crossed a zone
        // boundary while server-held, which the original recompute-at-release-time code would have gotten
        // wrong) and by ReconcileStaleO10Holds below, and cleared alongside every other kill-switch-adjacent
        // flush (ClearAllState here, plus O10SharedAreaOwnership.ClearO10HeldZoneTracking on a live Active-off
        // flip -- see that call site's own remarks for why the live-toggle case needs it too).
        private static readonly Dictionary<ZDOID, Vector2s> s_o10HeldZones = new Dictionary<ZDOID, Vector2s>();

        private static bool IsExcluded(ZDO zdo)
        {
            // Container in-use: a real ZDO field (SERVER:121873/121877, `m_nview.GetZDO().Set/GetInt(ZDOVars.s_inUse, ...)`)
            // -- readable with no live GameObject/component needed, works even on a bare headless server with
            // nothing instantiated (O9's whole point is to run standalone, before the zone-sim module exists).
            if (zdo.GetInt(ZDOVars.s_inUse, 0) == 1) return true;

            // Sadle ridden: also a real ZDO field (SERVER:18236-18242, `RPC_RequestControl` writes ZDOVars.s_user
            // to the riding player's id on grant). Read directly rather than via Sadle.HaveValidUser() (a live
            // component method) for the same "works with no instance" reason.
            if (zdo.GetLong(ZDOVars.s_user, 0L) != 0L) return true;

            // WP3 FIX (Finding 4, CRITICAL -- safety-net spec.json WP3 / SAA final.md WP-0, ranked above O11's
            // own work): confirmed via RunReassignmentPass's own sighting loop that, with no check here at all,
            // two players standing near each other put BOTH avatars into s_sightingsThisTick with 2+ sighting
            // uids -- if the current owner (one of the players) has materially worse RTT than the other, O9
            // would, after dwell, propose reassigning a LIVE CONNECTED PLAYER'S OWN AVATAR ZDO to a different
            // player's session. General, durable layer: the owner is a currently-connected peer AND this ZDO
            // is inside THAT PEER'S OWN currently-reported active area -- reimplements vanilla's own private
            // `ZDOMan.IsInPeerActiveArea`'s peer branch (SERVER:76934-76946: `GetPeer(uid)` -> `null` ->
            // `false`; else `ZNetScene.InActiveArea(point, peer.GetRefPos())`) via the PUBLIC InActiveArea
            // overload, since the private method itself isn't reachable from here. A player's own active area
            // always covers their own position, so this alone already excludes their avatar whenever they're
            // genuinely connected and not badly stale -- and it generalizes for free to any object a present
            // peer is actively near, not just their own body. Categorical belt-and-suspenders companion just
            // below (prefab.GetComponent<Player>()) covers the residual gap this leaves open: a brief
            // disconnect-race window where GetPeer(owner) returns null, or a stale/degraded refPos that
            // transiently reports "not in active area" for what is, in fact, this exact live player's own
            // body -- neither should assume "safe to reassign" for a Player ZDO merely because the presence
            // check above didn't fire this one tick.
            long owner = zdo.GetOwner();
            if (owner != 0L)
            {
                ZNetPeer ownerPeer = ZNet.instance?.GetPeer(owner);
                if (ownerPeer != null && ZNetScene.InActiveArea(zdo.GetPosition(), ownerPeer.GetRefPos()))
                {
                    Telemetry.GetCounter("ownership.o9.ownerActiveAreaExclusions").Increment();
                    return true;
                }
            }

            // Ship (m_players, SERVER:140293) and Vagon (InUse() -> m_container.IsInUse() || IsAttached(),
            // SERVER:147563-147569) both confirmed this pass to track "in use" ONLY in live, in-memory component
            // state populated via OnTriggerEnter/Exit or a live Joint reference -- NOT backed by any ZDO field at
            // all. A bare dedicated server running O9 standalone (no zone-sim instantiation) has no such live
            // instance for an un-instantiated object, so that state is simply unreadable here. Rather than treat
            // "unreadable" as "safe to reassign" (the wrong direction for a zero-data-loss project), every Ship
            // and every Vagon is categorically excluded from O9's candidate set -- more conservative than the
            // blueprint's literal "only while crewed/attached" text, but safe by construction and cheap (a prefab
            // component check, which DOES work with no live instance -- prefab templates are loaded once, globally,
            // regardless of whether any world instance of them exists). Documented as a deliberate, known
            // limitation in docs/impl/OWNERSHIP-IMPL.md; relaxable once the zone-sim module gives the server a
            // live instance to read the real state from.
            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
            if (prefab != null)
            {
                // WP3 FIX (Finding 4): categorical, unconditional -- a Player ZDO is NEVER a reassignment
                // candidate, full stop, matching this file's own established categorical-exclusion shape for
                // Ship/Vagon/TerrainComp one line below. Checked first (before those) precisely because this
                // is the invariant that actually matters here: the owner-active-area check above is the
                // primary, durable protection, but it depends on live peer/refPos state; this one depends on
                // nothing but the prefab itself, so it still holds even in the disconnect-race/stale-refPos
                // edge cases that check's own remarks name.
                if (prefab.GetComponent<Player>() != null)
                {
                    Telemetry.GetCounter("ownership.o9.playerExclusions").Increment();
                    return true;
                }

                if (prefab.GetComponent<Ship>() != null) return true;
                if (prefab.GetComponent<Vagon>() != null) return true;
                // TerrainComp: "excluded entirely from O9's candidate set (never a target)" per the O9
                // adjudication -- G6-N2 (zone-sim module) owns handover-atomicity for this class.
                if (prefab.GetComponent<TerrainComp>() != null) return true;

                // FIXER FIX (dl-saa-core, BLOCKER): the O6/O11 near-field claim path (NearFieldClaimPolicy.Evaluate)
                // has its own hard exclusions for Pickable (PickableRelease.IsNearLivePlayer), the transfer-on-
                // interact classes that never move to the server at the default Mode=FarOrShared (Container,
                // ItemStand, ArmorStand, Turret, Sign, ItemDrop -- NearFieldClaimPolicy.IsClassAllowedForMode's
                // own "allOnlyClass" list; Vagon is handled above alongside Ship, and Sadle deliberately is not
                // repeated here -- see the ROUND-2 FIXER FIX note below), and Smelter/Fireplace
                // (CompatClaimExclusions -- the fix for a previously-REAL "the
                // smelter ate my silver" item-loss bug against Wubarrk-Wonderland's ProductionSupplyEngine).
                // None of that ever ran for O9/O10's own SetOwner path (OwnershipApi.SetOwner ->
                // SetOwnerHoldGuard.AuthorizedSetOwner) -- so O10's far-regime branch could hand any of these
                // straight to the SERVER the instant every sighting peer reads as far, directly reopening the
                // user's own binding SAA decision ("ships and pickables are never taken"; "chests, carts, item
                // stands, signs and ground items stay with whoever uses them") and the Wonderland item-loss bug.
                // Deliberately unconditional here (no IsNearLivePlayer-style proximity gate, unlike the O6/O11
                // veto) -- O9/O10's own sighting radius is a peer's whole active area, far wider than
                // NearFieldClaimPolicy.PickableReleaseRadius, so a proximity-gated version could still let one
                // of these slip through when sighted-but-not-adjacent; the categorical exclusion is strictly
                // safer and costs nothing (none of these classes gain any latency benefit from O9/O10 once
                // excluded from consideration either way). Container/ItemStand/ArmorStand/Turret/Sign/ItemDrop
                // are checked directly (matching this function's own established unconditional-component-check
                // style, one line up) rather than reusing NearFieldClaimPolicy.IsClassAllowedForMode wholesale -- that
                // function also allows many classes O9/O10 SHOULD keep touching today (WearNTear, creatures,
                // production buildings, etc., all fine to reassign/hand to the server, exactly SAA's own point)
                // and inverting it would wrongly exclude every prefab type not named in either of its two lists.
                // Smelter/Fireplace (plus BlightedWorldHeart, for free) instead delegate to
                // CompatClaimExclusions.IsExcluded itself -- reusing that exact list rather than maintaining a
                // second, divergent copy of it, per this finding's own recommendation; the log line that call
                // emits says "near-field claim" even when reached from here (an O9/O10 latency reassignment,
                // not an O6/O11 bulk claim) -- a minor, cosmetic-only phrasing imprecision accepted rather than
                // forking CompatClaimExclusions' own logging to stay byte-for-byte generic.
                if (prefab.GetComponent<Pickable>() != null) return true;
                if (prefab.GetComponent<Container>() != null) return true;
                if (prefab.GetComponent<ItemStand>() != null) return true;

                // ROUND-2 FIXER FIX (ownership, blocker -- exclusions lens): this block's own comment above
                // already claimed to reuse "the transfer-on-interact classes" / NearFieldClaimPolicy's
                // allOnlyClass set in full, but ArmorStand and Turret (both members of that same allOnlyClass
                // list -- NearFieldClaimPolicy.cs's own allOnlyClass check) were missing here, so O9 (player<->
                // player RTT reassignment) or O10 (far-regime candidacy -- the server becomes owner once every
                // sighting peer reads RTT-far) could still reassign/claim one: an ArmorStand (item-holding --
                // equipped armor/weapon display, ZDO-held via s_itemKeyHashes, decompile-confirmed
                // Awake/RPC_RequestOwn/RPC_DropItem*) or a Turret (item-holding -- ammo count/type via
                // ZDOVars.s_ammo/s_ammoType, decompile-confirmed UseItem/RPC_AddAmmo/SetTargets ->
                // m_nview.ClaimOwnership()) reaching the SERVER or a different player the instant RTT/far
                // conditions are met -- the same "server takes an item-holding object" violation as the
                // NearFieldClaimPolicy finding, reached via the O9/O10 path instead of O6/O11. Both are ON by
                // default (O9Flag / O10's own s_o10Active both bind true) so this was live on Proxima at the
                // shipped defaults. HandoverGuard.cs (IsTransferOnInteractClass, ArmorStand/Turret quiescence
                // backstops) already treats both the same as ItemStand/Sign elsewhere in this codebase, so this
                // brings IsExcluded in line with that existing, established treatment rather than introducing a
                // new category. Sadle is deliberately NOT added here: it always co-occurs with Tameable/BaseAI/
                // MonsterAI on the same rideable-creature ZDO, which is legitimately server-claimable per the
                // user's own "tames" decision, and the active-rider case (the only state that should ever block
                // a claim) is already covered unconditionally above by the ZDOVars.s_user check -- adding a bare
                // GetComponent<Sadle>() categorical exclusion here would wrongly block every unridden tamed
                // mount from ever being claimed, defeating that decision.
                if (prefab.GetComponent<ArmorStand>() != null) return true;
                if (prefab.GetComponent<Turret>() != null) return true;

                if (prefab.GetComponent<Sign>() != null) return true;
                if (prefab.GetComponent<ItemDrop>() != null) return true;
                if (CompatClaimExclusions.IsExcluded(zdo, prefab, out _)) return true;
            }

            return false;
        }

        private static void RunReassignmentPass()
        {
            long sessionId = ZDOMan.GetSessionID();
            float marginMs = Mathf.Clamp(s_rttMarginMs.Value, 10f, 100f);
            float requiredDwell = Mathf.Clamp(s_rttDwellSeconds.Value, 2f, 30f);
            float now = Time.realtimeSinceStartup;

            foreach (var kv in s_sightingsThisTick)
            {
                ZDOID zdoid = kv.Key;
                List<long> sightingUids = kv.Value;

                ZDO zdo = ZDOMan.instance.GetZDO(zdoid);
                if (zdo == null) { s_dwell.Remove(zdoid); continue; } // destroyed meanwhile

                long currentOwner = zdo.GetOwner();
                if (currentOwner == 0L) { s_dwell.Remove(zdoid); continue; } // unowned -- not O9/O10's business

                // O10 EXTENSION (experimental, default off): today's code merged this case into the check above
                // ("currentOwner == 0L || currentOwner == sessionId"), both of which just removed dwell and
                // continued. O10 needs its OWN branch here -- the server->player handback direction (see
                // O10SharedAreaOwnership's class remarks + RunO10HandbackCheck below) -- so it's split out.
                // Deliberately checked BEFORE the "sightingUids.Count < 2" test below (which is specific to the
                // CLAIM direction's "several far players together" scenario): a server-held object should be
                // reconsidered for handback the moment even ONE sighting peer returns/gets cheap, not only once
                // 2+ do. With O10 off, RunO10HandbackCheck's own first line reproduces the old merged behaviour
                // (remove dwell, continue) byte-for-byte, regardless of sighting count.
                if (currentOwner == sessionId)
                {
                    RunO10HandbackCheck(zdoid, zdo, sightingUids, requiredDwell, now);
                    continue;
                }

                if (sightingUids.Count < 2) { s_dwell.Remove(zdoid); continue; } // O9/O10-claim only matter when SEVERAL peers share the area
                if (!sightingUids.Contains(currentOwner)) { s_dwell.Remove(zdoid); continue; } // current owner isn't one of this tick's sighting peers -- let vanilla's own release logic handle it

                if (ZoneClaimSweep.ClaimGraceTracker.IsWithinGraceWindow(zdoid)) continue; // recently touched by this module -- don't reconsider yet, but keep any existing dwell progress
                if (IsExcluded(zdo)) { s_dwell.Remove(zdoid); continue; }

                ZNetPeer currentOwnerPeer = ZNet.instance.GetPeer(currentOwner);
                float currentOwnerRtt = currentOwnerPeer != null ? RttTracker.Smoothed(currentOwnerPeer) : -1f;
                if (currentOwnerRtt < 0f) { s_dwell.Remove(zdoid); continue; } // no RTT data for the current owner yet

                // O10 EXTENSION: "propose sessionId instead" (redesign proposals[1].mechanics) -- checked BEFORE
                // O9's own best-peer search below, so a genuine far-regime candidacy (every sighting peer far,
                // AND ZoneSim actually simulating this zone right now) takes priority over O9's ordinary margin
                // test for this same ZDO this tick. Whenever O10 doesn't propose anything (O10 off, not every
                // peer far, or ZoneSim not covering this zone), O9's own search runs completely unchanged --
                // this is exactly the "NJ players near Arizona: O10 deliberately does not engage" case the
                // redesign's playerBenefit section describes.
                long candidateUid = 0L;
                bool viaO10 = false;
                if (O10SharedAreaOwnership.Active)
                {
                    Vector2s zone = ZoneSystem.GetZone(zdo.GetPosition());
                    if (O10SharedAreaOwnership.ZoneSimReady(zone) &&
                        O10SharedAreaOwnership.AllSightingPeersFar(sightingUids, O10SharedAreaOwnership.FarRttThresholdMs))
                    {
                        candidateUid = sessionId;
                        viaO10 = true;
                        Telemetry.GetCounter("ownership.o10.farRegimeDetected").Increment();
                    }
                }

                // WP1 per-feature quarantine FIX (findings review): this player<->player branch is O9's OWN
                // logic, so it must stop the instant O9Flag is force-off'd (PatchRegistry apply failure,
                // FeatureStandDown's per-feature exception-burst trigger, or the reflection-missing ForceOff
                // in this file's own Init) even while O10 stays on -- the shared Prefix gate above
                // ("O9.Active || O10SharedAreaOwnership.Active") only decides whether this whole pass runs AT
                // ALL, never which direction's behaviour is live once it does. Without this explicit check,
                // O9's search (and the SAME exception path that got it force-off'd in the first place) kept
                // recurring every tick purely because O10 alone kept the pass alive -- the eventual SetOwner
                // call below is separately gated on O9Armed, but O9Armed is a config-bool-transition latch
                // never cleared by ForceOff/ClearForcedOff, so that alone did not actually stop this branch.
                if (!viaO10 && Features_Ownership.O9.Active)
                {
                    long bestUid = 0L;
                    float bestRtt = float.MaxValue;
                    foreach (long uid in sightingUids)
                    {
                        if (uid == currentOwner) continue;
                        ZNetPeer peer = ZNet.instance.GetPeer(uid);
                        if (peer == null) continue;
                        float rtt = RttTracker.Smoothed(peer);
                        if (rtt < 0f) continue;
                        if (rtt < bestRtt) { bestRtt = rtt; bestUid = uid; }
                    }
                    if (bestUid != 0L && currentOwnerRtt - bestRtt > marginMs) candidateUid = bestUid;
                }

                if (candidateUid == 0L) { s_dwell.Remove(zdoid); continue; } // margin not met, and no O10 candidacy either

                bool candidateIsServer = candidateUid == sessionId;

                if (!s_dwell.TryGetValue(zdoid, out var dwell) || dwell.CandidateUid != candidateUid)
                {
                    s_dwell[zdoid] = new DwellState { CandidateUid = candidateUid, QualifyingSinceRealtime = now };
                    Telemetry.GetCounter(candidateIsServer
                        ? "ownership.o10.switchesConsideredButSuppressed"
                        : "ownership.o9.switchesConsideredButSuppressed").Increment();
                    continue; // just started qualifying -- dwell timer resets on the WINNING candidate changing
                }

                if (now - dwell.QualifyingSinceRealtime < requiredDwell)
                {
                    Telemetry.GetCounter(candidateIsServer
                        ? "ownership.o10.switchesConsideredButSuppressed"
                        : "ownership.o9.switchesConsideredButSuppressed").Increment();
                    continue;
                }

                // LEAD FIX (CORE-API.md "Live-authority toggles"): only actually perform a reassignment while
                // armed (O9Armed / O10SharedAreaOwnership.O10Armed, matching which direction this candidate is)
                // -- dwell PROGRESS is kept either way (no s_dwell.Remove here), so a brief unarmed window (a
                // save refusal, or between boot and the world finishing its own load) doesn't throw away
                // hysteresis state that's otherwise still valid once armed catches up.
                if (candidateIsServer)
                {
                    if (!O10SharedAreaOwnership.O10Armed) continue;
                }
                else if (!O9Armed) continue;

                // Seam #1 (ReclaimGate): both O9 and O10's player->server direction are exactly the "take an
                // object from a PRESENT player" case this seam exists for (the current owner is, by
                // construction, one of this tick's sighting peers). With no active guard this always refuses.
                if (!ReclaimGate.AllowReclaim(zdo, out string reason))
                {
                    Telemetry.GetCounter(candidateIsServer
                        ? "ownership.o10.reclaimGateRefused"
                        : "ownership.o9.reclaimGateRefused").Increment();
                    s_dwell.Remove(zdoid);
                    continue;
                }

                bool ok = OwnershipApi.SetOwner(zdo, candidateUid,
                    candidateIsServer ? "O10 shared-area claim (far regime)" : "O9 latency-aware reassignment");
                s_dwell.Remove(zdoid);
                if (ok)
                {
                    ZoneClaimSweep.ClaimGraceTracker.MarkTouched(zdoid);
                    if (candidateIsServer)
                    {
                        Vector2s claimZone = ZoneSystem.GetZone(zdo.GetPosition());
                        SharedAreaZoneHold.Claim(claimZone);
                        s_o10HeldZones[zdoid] = claimZone;
                        Telemetry.GetCounter("ownership.o10.switchesToServer").Increment();
                    }
                    else
                    {
                        Telemetry.GetCounter("ownership.o9.switches").Increment();
                    }
                }
                // A `false` result already incremented ownership.setOwnerVetoed inside AuthorizedSetOwner itself.
            }

            // O10 FINDING FIX (blocker, O10 findings review): every ZDOID this loop just walked that's currently
            // sighted (a key of s_sightingsThisTick) and still server-owned already went through
            // RunO10HandbackCheck above, which either keeps the hold (still far-regime) or releases it properly.
            // A held ZDOID that ISN'T a key this tick never reached that check at all -- the two concrete gaps
            // the finding named (destroyed while held; every sighting peer left/disconnected) both look
            // identical from here: the object simply stopped appearing in this tick's sightings. Reconcile those
            // now rather than leave the hold (and the G3-L1 protection it grants) stuck until the object is
            // sighted again, which may be never.
            ReconcileStaleO10Holds();

            // Set every tick RunReassignmentPass itself runs (i.e. whenever O9 or O10 is active) -- 0 whenever
            // O10 has never claimed anything (O10 off, or on but never yet far-regime-eligible), matching
            // ownership.serverHeldZones' own "0 until the module has something to report" convention.
            Telemetry.SetGauge("ownership.o10.sharedAreaZonesHeld", SharedAreaZoneHold.Count);
        }

        /// <summary>
        /// O10 FINDING FIX (blocker, O10 findings review): releases any O10 hold this tick's sightings loop had
        /// no reason to visit at all -- i.e. a ZDOID <see cref="s_o10HeldZones"/> still tracks that is NOT a key
        /// of <see cref="s_sightingsThisTick"/> this tick. Two ways that happens, both real (see this file's own
        /// O10 class remarks / OWNERSHIP-IMPL.md's risk list): the object was destroyed by ordinary simulation
        /// while server-held, or every sighting peer disconnected/walked out of near-simulation-distance range.
        /// Either way, nothing else will ever call <see cref="RunO10HandbackCheck"/> for this ZDOID again on its
        /// own, so the hold (and the G3-L1 "server appears present in this zone" protection it grants) would
        /// otherwise never be released short of a full kill-switch flush or process restart. Releases using the
        /// CLAIM-time zone tracked in <see cref="s_o10HeldZones"/>, not a recomputed current position (the
        /// object may not even exist any more to recompute one from). Deliberately unconditional on whether the
        /// ZDO still exists or is still server-owned -- either way there is nothing left for the hold to
        /// protect, and Release() is itself idempotent/no-op-safe on an already-clear zone.
        /// </summary>
        private static void ReconcileStaleO10Holds()
        {
            if (s_o10HeldZones.Count == 0) return;

            List<ZDOID> stale = null;
            foreach (ZDOID zdoid in s_o10HeldZones.Keys)
            {
                if (s_sightingsThisTick.ContainsKey(zdoid)) continue; // sighted this tick -- RunO10HandbackCheck already ran for it above
                (stale ??= new List<ZDOID>()).Add(zdoid);
            }
            if (stale == null) return;

            foreach (ZDOID zdoid in stale)
            {
                if (!s_o10HeldZones.TryGetValue(zdoid, out Vector2s zone)) continue;
                SharedAreaZoneHold.Release(zone);
                s_o10HeldZones.Remove(zdoid);
                Telemetry.GetCounter("ownership.o10.staleHoldsReconciled").Increment();
            }
        }

        /// <summary>
        /// O10 EXTENSION (experimental, default off) — server-&gt;player handback, the direction today's code
        /// (pre-O10) short-circuited entirely for a server-held object seen by 2+ peers (see
        /// RunReassignmentPass's own remarks at its call site). Triggered the moment the BEST (lowest-RTT)
        /// sighting peer's own RTT drops below <see cref="O10SharedAreaOwnership.FarRttThresholdMs"/> — a cheap
        /// local player arrived, or the far peer(s) that made this a far-regime claim left/disconnected — held
        /// for the same dwell window O9's own player-to-player switches use. No <see cref="ReclaimGate"/> call:
        /// giving an object away is never "reclaiming from a present player" (the server is never a player
        /// ReclaimGate protects against reclaiming FROM) — same precedent O8's own ship-handover grant already
        /// established (<c>OwnershipHandoverRpc.cs</c>).
        /// </summary>
        private static void RunO10HandbackCheck(ZDOID zdoid, ZDO zdo, List<long> sightingUids, float requiredDwell, float now)
        {
            if (!O10SharedAreaOwnership.Active) { s_dwell.Remove(zdoid); return; } // O10 off -- exactly today's old (pre-O10) behaviour for a server-held sighted object

            // FIXER FIX (integration, major): this function must only ever un-do THIS module's OWN O10
            // far-regime claims. RunReassignmentPass's own call site above invokes this for ANY currently
            // server-owned, currently-sighted ZDO -- including one that became server-owned via O6/O11's
            // near-field SAA claim (ZoneClaimSweep/NearFieldClaimVeto, a completely different subsystem that
            // never touches s_o10HeldZones/SharedAreaZoneHold at all). Without this check, an O6/O11-claimed
            // WearNTear/creature/production object -- claimed specifically NEAR a player, so almost always well
            // under FarRttThresholdMs -- would reach the RTT-based handback decision below and, once dwell
            // passed, get handed straight back to that nearby player by O10, plus an unpaired
            // SharedAreaZoneHold.Release() on its zone (silently over-decrementing a refcount O10's own Claim
            // never incremented for this object) -- both defeating SAA's entire "server owns things near
            // players" point and risking zeroing out a zone's G3-L1 protection out from under a DIFFERENT,
            // genuinely O10-held object still relying on it. A server-owned ZDO that got here some other way is
            // left to that subsystem's own claim-cycle/ReclaimGate logic to decide if/when it goes back to a
            // player, never touched by this function or SharedAreaZoneHold's refcount at all.
            if (!s_o10HeldZones.ContainsKey(zdoid)) { s_dwell.Remove(zdoid); return; }

            if (ZoneClaimSweep.ClaimGraceTracker.IsWithinGraceWindow(zdoid)) return; // recently touched by this module -- keep dwell progress, don't reconsider yet
            if (IsExcluded(zdo)) { s_dwell.Remove(zdoid); return; }

            long bestUid = O10SharedAreaOwnership.BestSightingPeer(sightingUids, out float bestRtt);
            if (bestUid == 0L) { s_dwell.Remove(zdoid); return; } // no sighting peer has RTT data yet

            if (bestRtt >= O10SharedAreaOwnership.FarRttThresholdMs) { s_dwell.Remove(zdoid); return; } // still far-regime -- keep holding

            if (!s_dwell.TryGetValue(zdoid, out var dwell) || dwell.CandidateUid != bestUid)
            {
                s_dwell[zdoid] = new DwellState { CandidateUid = bestUid, QualifyingSinceRealtime = now };
                Telemetry.GetCounter("ownership.o10.switchesConsideredButSuppressed").Increment();
                return;
            }
            if (now - dwell.QualifyingSinceRealtime < requiredDwell)
            {
                Telemetry.GetCounter("ownership.o10.switchesConsideredButSuppressed").Increment();
                return;
            }

            if (!O10SharedAreaOwnership.O10Armed) return; // dwell progress kept, same reasoning as O9Armed above

            bool ok = OwnershipApi.SetOwner(zdo, bestUid, "O10 far-regime handback");
            s_dwell.Remove(zdoid);
            if (ok)
            {
                ZoneClaimSweep.ClaimGraceTracker.MarkTouched(zdoid);
                // O10 FINDING FIX: release using the CLAIM-time zone (s_o10HeldZones' own remarks) rather than
                // recomputing from the object's current position -- correct even if it crossed a zone boundary
                // while server-held. Falls back to a recompute only if tracking is somehow missing (should never
                // happen -- every successful claim above populates it), so a Release still isn't skipped outright.
                Vector2s releaseZone = s_o10HeldZones.TryGetValue(zdoid, out Vector2s trackedZone)
                    ? trackedZone
                    : ZoneSystem.GetZone(zdo.GetPosition());
                s_o10HeldZones.Remove(zdoid);
                SharedAreaZoneHold.Release(releaseZone);
                Telemetry.GetCounter("ownership.o10.switchesToPlayer").Increment();
            }
            // A `false` result already incremented ownership.setOwnerVetoed inside AuthorizedSetOwner itself.
        }

        [HarmonyPatch(typeof(ZDOMan), "ReleaseZDOS")]
        internal static class Patch_ZDOMan_ReleaseZDOS_O9Policy
        {
            // BLOCKER FIX (lead review): ReleaseZDOS itself runs every frame (ZNet.Update -> ZDOMan.Update ->
            // ReleaseZDOS, SERVER:80089/76864); vanilla's own cadence gate is INSIDE the method body, so a bare
            // postfix ran this pass every frame, not once per configured interval. The Prefix predicts whether
            // THIS call will actually perform a real release tick (see ZoneClaimSweep.WillPerformReleaseTick —
            // same helper O6's own claim pass and the loss-watch revision scan now share), computed from the
            // timer's PRE-call value plus dt, so it's exact even when a single long frame's dt alone crosses the
            // threshold from a timer already at/near 0.
            static void Prefix(ZDOMan __instance, float dt, out bool __state)
            {
                // O10 EXTENSION (experimental, default off): same reasoning as the sighting-capture postfix
                // above -- O10 needs this pass to run too, since RunReassignmentPass is where its own
                // far-regime claim/handback decision lives (RunReassignmentPass's own remarks).
                //
                // WP3 FIX (auditB.md "three unguarded Prefixes on ZDOMan.ReleaseZDOS" -- this is one of the
                // three): an uncaught exception in a Harmony Prefix aborts the ENTIRE patched call, including
                // vanilla's own release/reassignment body for this frame -- worse than vanilla, not a fallback
                // to it -- and, being uncaught, it never reaches NoodlesLog.PatchException, so it never feeds
                // AutoStandDown/FeatureStandDown either. WillPerformReleaseTick itself is low-risk today, but
                // wrapping this costs nothing and turns any future throw here into "skip this tick's O9/O10
                // pass" (== momentarily inert, same as Active=false) instead of "vanilla's own ReleaseZDOS
                // body silently doesn't run this frame."
                try
                {
                    __state = (Features_Ownership.O9.Active || O10SharedAreaOwnership.Active) &&
                        ZoneClaimSweep.WillPerformReleaseTick(__instance, dt);
                }
                catch (Exception e)
                {
                    __state = false;
                    NoodlesLog.PatchException(nameof(Patch_ZDOMan_ReleaseZDOS_O9Policy) + ".Prefix", e);
                }
            }

            static void Postfix(ZDOMan __instance, bool __state)
            {
                if (!__state) return;
                if (__instance == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
                try
                {
                    RunReassignmentPass();
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_ZDOMan_ReleaseZDOS_O9Policy), e);
                }
                finally
                {
                    // Cleared here, unconditionally (even if RunReassignmentPass threw) -- a stale sighting set
                    // must never leak into next tick's decision, per H9/H10's own "cleared by O9's own postfix
                    // after it consumes it" contract. Only reached on a real tick (__state), which is exactly
                    // when H10 could have populated it in the first place -- nothing to clear otherwise.
                    s_sightingsThisTick.Clear();
                }
            }
        }

        // --- Kill switch -------------------------------------------------------------------------------------------

        internal static void ClearAllState()
        {
            // FIXER FIX (dl-saa-failbacks, major): don't just drop the bookkeeping below -- best-effort hand
            // any still-server-owned O10 far-regime object back to a live connected peer FIRST, snapshotted
            // before any of the clears run. FeatureFlag.OnForcedOff's own documented contract (Core/Features.cs
            // -- "if this feature holds live state that must be actively released (not merely 'stop granting
            // new holds')...") -- clearing SharedAreaZoneHold/s_o10HeldZones alone only ever dropped this
            // module's own bookkeeping; the ZDO itself stayed exactly as server-owned as it was a moment
            // before, now with no G3-L1 protection, relying entirely on some peer eventually wandering back
            // into range for vanilla's own unmodified ReleaseNearbyZDOS to notice and reassign it (safe -- no
            // data loss, never worse than vanilla -- but needlessly stuck server-owned, at worse latency than a
            // direct per-peer assignment, for however long that takes -- which for the exact "every sighting
            // peer is far" scenario O10 exists for may be a very long time). Wrapped in its own try/catch so a
            // failure in this best-effort improvement can never skip the mandatory cleanup below (mirrors this
            // file's own Patch_ZDOMan_ReleaseZDOS_O9Policy Postfix, whose `finally` clears s_sightingsThisTick
            // even if RunReassignmentPass itself threw).
            try
            {
                HandBackAllO10Holds();
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(HandBackAllO10Holds), e);
            }

            s_sightingsThisTick.Clear();
            s_dwell.Clear();
            // O10 FINDING FIX: drop this module's own ZDOID->zone tracking alongside everything else on a full
            // kill-switch flush. SharedAreaZoneHold.ClearAll() is registered as O10's OWN separate flush callback
            // (O10SharedAreaOwnership.ClearAllState) so the refcounted hold registry itself is already emptied
            // either way -- clearing this dict too just keeps both structures in sync immediately rather than
            // relying on ReconcileStaleO10Holds to notice the drift later (harmless either way: Release() against
            // an already-empty zone is a no-op).
            //
            // WP1 per-feature quarantine FIX (findings review): this is ALSO wired as O9Flag's own OnForcedOff
            // (below), which — unlike the global kill-switch flush above — can run while O10 stays fully Active.
            // Clearing s_o10HeldZones alone in that case would throw away the ONLY record of which zones O10 had
            // claimed without ever releasing the matching SharedAreaZoneHold refcount, leaving that registry
            // stuck holding those zones for the rest of the session (ReconcileStaleO10Holds can't help either,
            // since it walks s_o10HeldZones itself). Calling SharedAreaZoneHold.ClearAll() here too closes that
            // gap and matches O10SharedAreaOwnership.ReconcileArmedState's own live Active-off disarm path, which
            // already clears both structures together for exactly this reason. Idempotent/harmless when this
            // runs as part of the global flush and O10SharedAreaOwnership.ClearAllState clears the same registry
            // right alongside it.
            SharedAreaZoneHold.ClearAll();
            s_o10HeldZones.Clear();
        }

        /// <summary>
        /// FIXER FIX (dl-saa-failbacks): best-effort hand-back for <see cref="ClearAllState"/>, called only from
        /// there, before any bookkeeping is cleared. Deliberately NOT RTT-based (unlike
        /// <see cref="RunO10HandbackCheck"/>) -- this runs on a flush/quarantine, not a normal per-tick
        /// decision, so there is no fresh <see cref="s_sightingsThisTick"/> data to pick a low-RTT sighting peer
        /// from (it may have been cleared many ticks ago, or never have included this exact object at all).
        /// Falls back to the nearest currently-connected peer BY LIVE POSITION instead -- the same live-
        /// character-position read <c>PickableRelease.TryGetLiveCharacterPosition</c> uses, inlined here rather
        /// than shared since that method is private to a different file/owner. Not RTT-optimal, but a safe,
        /// reasonable new owner: <see cref="OwnershipApi.SetOwner"/> is the same postcondition-checked writer
        /// every other authorized write in this module already uses, so a failed/vetoed write here is no worse
        /// than any other -- the object is simply left server-owned, exactly the fallback this function's own
        /// caller already documents as safe (no data loss either way).
        /// </summary>
        private static void HandBackAllO10Holds()
        {
            if (s_o10HeldZones.Count == 0) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null) return;

            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            if (peers == null || peers.Count == 0) return; // nobody connected to hand back to -- left server-owned

            long sessionId = ZDOMan.GetSessionID();

            // Snapshot first -- this loop's own SetOwner call runs synchronously through every other patch
            // registered on ZDO.SetOwner (hold guard, client-claim-grace tracker, ...), none of which touch
            // s_o10HeldZones today, but iterating a live dictionary while calling out of this function is
            // exactly the kind of thing this codebase's own established snapshot-before-mutate style (e.g.
            // ZoneClaimSweep's own release-diffing pass) avoids on principle rather than relying on that
            // staying true forever.
            var heldZdoids = new List<ZDOID>(s_o10HeldZones.Keys);

            int handedBack = 0;
            foreach (ZDOID zdoid in heldZdoids)
            {
                ZDO zdo = ZDOMan.instance.GetZDO(zdoid);
                if (zdo == null || zdo.GetOwner() != sessionId) continue; // destroyed, or already moved on some other way

                Vector3 pos = zdo.GetPosition();
                long bestUid = 0L;
                float bestDistSqr = float.MaxValue;
                foreach (ZNetPeer peer in peers)
                {
                    if (peer.m_characterID == ZDOID.None) continue;
                    ZDO charZdo = ZDOMan.instance.GetZDO(peer.m_characterID);
                    if (charZdo == null) continue;
                    float distSqr = (charZdo.GetPosition() - pos).sqrMagnitude;
                    if (distSqr < bestDistSqr) { bestDistSqr = distSqr; bestUid = peer.m_uid; }
                }

                if (bestUid != 0L && OwnershipApi.SetOwner(zdo, bestUid, "O9/O10 flush hand-back"))
                {
                    handedBack++;
                    // Same grace window every other successful SetOwner in this file already marks (e.g.
                    // RunO10HandbackCheck, two lines... up in this file) -- keeps O6/O11's own near-field claim
                    // pass from immediately re-claiming this exact object back to the server on the very next
                    // claim cycle purely because the peer we just handed it to happens to be standing in a
                    // near-field-eligible zone. Harmless if skipped (re-claiming it would still be safe, just
                    // one extra OwnerRevision bump of churn), but free to do and consistent with this file's own
                    // established idiom.
                    ZoneClaimSweep.ClaimGraceTracker.MarkTouched(zdoid);
                }
                // No live peer found, or the write didn't take (e.g. vetoed by a third-party SetOwner prefix) --
                // left server-owned, same safe fallback this function's own caller already documents.
            }

            if (handedBack > 0)
            {
                Telemetry.GetCounter("ownership.o10.flushHandbacks").Add(handedBack);
                NoodlesLog.Info($"O9/O10: handed {handedBack} of {heldZdoids.Count} server-held far-regime " +
                    "object(s) back to a live peer on flush/quarantine (any remainder left server-owned -- " +
                    "safe, no data loss -- until a peer next comes into range).");
            }
        }

        /// <summary>
        /// O10 FINDING FIX (blocker, O10 findings review): called from
        /// <see cref="O10SharedAreaOwnership"/>'s own live Active-off arming transition, alongside
        /// <see cref="SharedAreaZoneHold.ClearAll"/> -- see that call site's own remarks for why a live
        /// disarm (not just the kill switch) needs this too. Internal rather than folded into
        /// <see cref="ClearAllState"/> itself: this one path clears ONLY the O10 tracking dict, not O9's own
        /// sightings/dwell state, which a live O10-off/O9-still-on transition must not disturb.
        /// </summary>
        internal static void ClearO10HeldZoneTracking() => s_o10HeldZones.Clear();

        /// <summary>See <see cref="O9Armed"/>'s own remarks. Mirrors SimulationDistanceCeiling.ReconcileArmedState exactly, for O9's own independent flag.</summary>
        private static void ReconcileArmedState()
        {
            bool wantsOn = s_o9Active.Value;
            if (wantsOn == O9Armed) return;

            if (!wantsOn)
            {
                O9Armed = false;
                NoodlesLog.Info("O9: disarmed (Active turned off).");
                return;
            }

            bool armed = SafeToggle.TryEnable(() => O9Armed = true, "O9");
            if (armed)
            {
                NoodlesLog.Info("O9: armed (forced pre-arm save verified).");
            }
            else
            {
                Telemetry.GetCounter("ownership.o9.armFailed").Increment();
                NoodlesLog.Error("O9: Active is on but arming was REFUSED -- see SafeToggle's own log line " +
                    "above for why. No reassignment will happen until this succeeds; toggle Active off then on " +
                    "again once the underlying problem is fixed, or restart.");
            }
        }

        // --- Init --------------------------------------------------------------------------------------------------

        [NoodlesModuleInit]
        internal static void Init()
        {
            ConfigFile config = NoodlesConfig.File;

            // DEFAULT ON (user decision, 2026-09-28: "i want it all, all enabled full test goes live with first
            // version" -- safety-net spec.json's allOnTable, WP3). Was off pending the CRITICAL Finding-4 fix
            // (IsExcluded above); that fix is the precondition that made this safe to default on, not a
            // separate later decision.
            // 1.1.0: main file [3 - Server World] BestPingOwnsSharedObjects (was [O9] Active). Latency-aware
            // ownership: among player-held objects several connected peers can see, hand the object to whichever has
            // the lower measured RTT. Independent of OwnershipAuthority (ServerRunsWorld) -- can run standalone.
            s_o9Active = NoodlesConfig.BestPingOwnsSharedObjects;
            O9Flag = Features.Register("O9", s_o9Active);

            // LEAD FIX (CORE-API.md "Live-authority toggles") -- same shape as SimulationDistanceCeiling's own
            // OwnershipAuthority handling: a live post-boot flip arms only via a forced, verified save; boot-time
            // (already on in the config file) arms directly, since nothing has moved yet this session.
            s_o9Active.SettingChanged += (_, __) => MainThreadDispatcher.Enqueue(ReconcileArmedState);
            if (s_o9Active.Value)
            {
                O9Armed = true;
                NoodlesLog.Verbose("O9: armed at boot (already on in the config file) -- no forced save.");
            }

            s_rttMarginMs = config.Bind("O9", "RttMarginMs", 30f,
                new ConfigDescription("How much lower (ms, smoothed) a candidate peer's RTT must be than the " +
                    "current owner's before a switch is even considered. No vanilla equivalent. 30ms sits " +
                    "comfortably above ordinary jitter and well below the ~150-300ms an Arizona<->New Jersey gap " +
                    "actually shows -- a decision threshold, not a load-scaling knob.",
                    new AcceptableValueRange<float>(10f, 100f)));

            s_rttDwellSeconds = config.Bind("O9", "RttDwellSeconds", 5.0f,
                new ConfigDescription("How long a candidate's RTT margin must hold continuously before O9 " +
                    "actually switches owners. No vanilla equivalent. NEEDS-LIVE-TEST (docs/design/03): 5.0s is " +
                    "a reasoned starting point (long enough that one lucky ping doesn't trigger a switch, short " +
                    "enough to matter against O3's own default 1.0s sweep cadence), not yet tuned against real " +
                    "Arizona<->New Jersey jitter -- see ownership.o9.switches vs switchesConsideredButSuppressed.",
                    new AcceptableValueRange<float>(2f, 30f)));

            PatchRegistry.Register("O9", typeof(Patch_ZDOMan_ReleaseNearbyZDOS_SightingCapture), PatchSide.Both);
            PatchRegistry.Register("O9", typeof(Patch_ZDOMan_ReleaseZDOS_O9Policy), PatchSide.Both);

            // L2 (deployment-modes review, 2026-09-28): same seam as OwnershipAuthority's own registration in
            // SimulationDistanceCeiling.cs -- DeploymentLog checks this instead of O9Armed directly.
            ArmedStateGate.O9ArmedProvider = () => O9Armed;

            if (s_tempNearObjectsRef == null)
            {
                NoodlesLog.Error("LatencyAwareOwnership: ZDOMan.m_tempNearObjects did not resolve via reflection " +
                    "-- H10 has no data source. Forcing O9 off.");
                O9Flag.ForceOff("ZDOMan.m_tempNearObjects failed to resolve at Awake");
            }

            KillSwitch.RegisterFlushCallback("Ownership.O9", ClearAllState);

            // WP1 per-feature quarantine: same release path as the kill-switch flush above, now ALSO wired as
            // this feature's own OnForcedOff (CORE-API.md "Flush callbacks" / FeatureFlag.OnForcedOff remarks).
            O9Flag.OnForcedOff = ClearAllState;
        }
    }
}
