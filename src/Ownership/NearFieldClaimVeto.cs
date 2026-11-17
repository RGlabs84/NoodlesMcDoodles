using System;
using System.Collections.Generic;
using HarmonyLib;
using NoodlesMcDoodles.Core;

namespace NoodlesMcDoodles.Ownership
{
    /// <summary>
    /// SAA WP-A (<c>scratchpad/saa/final.md</c> §2) — the actual per-object enforcement point v1's design was
    /// missing: a <c>ZDO.SetOwner</c> prefix, active ONLY while O6's own bulk claim
    /// (<see cref="SetOwnerHoldGuard.RunAuthorizedBulkClaim"/>, called from <see cref="ZoneClaimSweep.ClaimZone"/>)
    /// is in flight, and ONLY for a claim targeting the server's own session id — every other call (a plain
    /// client's own ownership churn, O8's hand-back, O9's reassignment, anything targeting a player uid) is
    /// untouched, returning <c>true</c> immediately.
    ///
    /// 1.0.7: it also keeps the server's own objects in held zones when vanilla's <c>ReleaseNearbyZDOS</c> would
    /// drop them (<c>SetOwner(0)</c> from its release branch, during O6's bulk claim or vanilla's own server pass,
    /// see <see cref="InServerNearbyPass"/>), and during the bulk claim refuses claims outside the zone being
    /// claimed. See the Prefix for the live numbers that led here.
    ///
    /// Registered ALONGSIDE (never instead of) <see cref="SetOwnerHoldGuard.Patch_ZDO_SetOwner_HoldGuard"/> —
    /// Harmony ANDs every bool-returning prefix's result on one target method regardless of registration
    /// order, so this composes safely with that hold-guard prefix and with GetOffMyLawn's/Wonderland's own
    /// third-party prefixes on the same method (confirmed, <see cref="SetOwnerHoldGuard"/>'s own class remarks:
    /// neither ever blocks a transfer TO the server, only away from it, so this veto — which only ever fires
    /// for a claim TO the server — never interacts with either).
    /// </summary>
    [HarmonyPatch(typeof(ZDO), nameof(ZDO.SetOwner))]
    internal static class NearFieldClaimVeto
    {
        // Round-3 install-gate fix (quarantine miscount): the objects THIS prefix refused by policy during the
        // current O6 bulk claim. ZoneClaimSweep.ClaimZone's post-claim check uses it so our own deliberate
        // refusals (pickables, item holders, classes the Mode does not allow) are no longer counted as
        // third-party vetoes -- ownership.setOwnerVetoed feeds ModeQuarantine's burst trip, which was firing
        // on our own refusals within a minute of boot on a real world. Cleared at the start of every bulk
        // claim, so it only ever holds one zone's refusals. Main thread only.
        private static readonly HashSet<ZDOID> s_policyRefused = new HashSet<ZDOID>();

        // Same fix, second half: every object vanilla's ReleaseNearbyZDOS actually TRIED to hand to the server
        // during the current bulk claim (Harmony runs every prefix even after another one returned false, so a
        // third-party veto still passes through here first or after). An expected object that never shows up
        // here was simply not attempted by vanilla (e.g. a present player still owns it) -- not a veto either.
        private static readonly HashSet<ZDOID> s_attempted = new HashSet<ZDOID>();

        // 1.0.7 (claim churn fix): the zone the current bulk claim is for. Vanilla's ReleaseNearbyZDOS works on the
        // claimed zone's whole block, not the one zone; ZoneClaimSweep.ClaimZone passes the zone in so Prefix below
        // can keep the call to it.
        private static Vector2s s_bulkZone;

        private static readonly Counter s_selfReleaseKept = Telemetry.GetCounter("ownership.o6.selfReleaseKept");
        private static readonly Counter s_outOfZoneClaimSkipped = Telemetry.GetCounter("ownership.o6.outOfZoneClaimSkipped");

        // Round 4 (zone-load-stall safety fix): round 3 made object instantiation time-budgeted
        // (ZoneSim/CreateObjectsBudget.cs) -- a freshly-fed zone's backlog can now take minutes to fully
        // Create (measured ~10,900 objects draining at ~60/s). ZoneSimGate.CanHold only requires the zone to
        // be real and in this tick's feed (ZoneCap.WasIncludedThisTick), NOT that every one of its objects has
        // finished Instantiate-ing -- see that method's own "residual, benign timing looseness" remark. Without
        // this check, O6 could claim a ZDO with no ZNetView on this process yet: a server-owned creature nobody
        // simulates (frozen from every client's point of view) and a routed RPC (damage on a tree/rock/piece, a
        // door, a smelter) that finds no local instance and is silently lost -- exactly what "zero data loss"
        // forbids.
        private static readonly Counter s_claimDeferredNotCreated = Telemetry.GetCounter("ownership.o6.claimDeferredNotCreated");

        internal static void BeginBulkClaim(Vector2s zone) { s_policyRefused.Clear(); s_attempted.Clear(); s_bulkZone = zone; }

        internal static bool WasRefusedByPolicy(ZDOID id) => s_policyRefused.Contains(id);

        internal static bool WasAttempted(ZDOID id) => s_attempted.Contains(id);

        static bool Prefix(ZDO __instance, long uid)
        {
            // RUNTIME AUTHORITY, not just the ThreadStatic flag below (binding rule; matches every sibling
            // patch in this module, e.g. SetOwnerHoldGuard.cs's own "CLIENT-REVIEW FIX" on this exact method).
            // Structurally redundant today (AuthorizedBulkClaimInFlight can only ever be true from
            // server-only-gated code), but cheap, and it's the pattern this exact codebase has already been
            // dinged for omitting once on this exact method — keep it explicit rather than rely on a transitive
            // guarantee three call frames away.
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return true;

            bool inBulkClaim = SetOwnerHoldGuard.AuthorizedBulkClaimInFlight;
            if (!inBulkClaim && !InServerNearbyPass) return true; // only O6's own bulk claim, or vanilla's own server pass
            long sessionId = ZDOMan.GetSessionID();

            // 1.0.7 (claim churn fix, from live Proxima telemetry 2026-09-29): ClaimZone runs vanilla's
            // ReleaseNearbyZDOS from the claimed zone's centre, and its first branch (SERVER:76962-76966) drops the
            // caller's own objects that it found (the whole 5x5 block at Proxima's near=2 classic) but that sit
            // outside the zone's 3x3 active area. For a player that is housekeeping; for the server it released
            // everything it held two zones out, held zones included, on every claim, and the next claims took it
            // back: ~75 near-field re-claims a second for one player, a third of zone claims deferred by our own
            // grace window, and the player picking up pieces the server had just let go. Vanilla's ReleaseZDOS also
            // runs the same method for the server itself around ZNet.GetReferencePosition() (Vector3.zero on a
            // dedicated server, so world spawn), which did the same to any held zone two zones from spawn -- hence
            // InServerNearbyPass as well as the bulk-claim flag. Ownership in a zone the server holds changes
            // only through this module's own release paths (ReleaseZoneOwnership, ReleaseAllHeldZones), never as a
            // side effect of a nearby pass. A server-owned object outside every held zone is still released exactly
            // as vanilla would.
            if (uid == 0L)
            {
                if (__instance.GetOwner() != sessionId) return true;
                try
                {
                    if (ZoneClaimSweep.IsHeldForServer(ZoneSystem.GetZone(__instance.GetPosition())))
                    {
                        s_selfReleaseKept.Increment();
                        return false;
                    }
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(NearFieldClaimVeto), e); // fail open: vanilla's release goes ahead
                }
                return true;
            }

            if (!inBulkClaim) return true;     // vanilla's own server pass claims exactly as vanilla does
            if (uid != sessionId) return true; // only a claim TO the server

            s_attempted.Add(__instance.m_uid);

            // 1.0.7 (same fix): claim only the zone being claimed. The same vanilla call also takes free objects in
            // the neighbouring zones of the 3x3, which left server-owned objects in zones the server does not hold
            // (no G3-L1 protection, so a nearby player's pass took them straight back) and fed the release above.
            // Every candidate zone gets its own ClaimZone turn in the rotation. Checked before the policy's
            // component lookups, so it is also the cheap path for the ~8 in 9 objects the call hands us.
            if (ZoneSystem.GetZone(__instance.GetPosition()) != s_bulkZone)
            {
                s_outOfZoneClaimSkipped.Increment();
                s_policyRefused.Add(__instance.m_uid);
                return false;
            }

            // Round 4 (zone-load-stall safety fix, see s_claimDeferredNotCreated's own remarks above): never
            // claim a ZDO that has not finished instantiating on this process (ZDO.Created, decompile
            // SERVER:73465 -- a DataFlags bit set true only by ZNetScene.AddInstance when a ZNetView actually
            // registers for this ZDO, and cleared by ZNetView.ResetZDO). Placed after the own-zone check
            // (cheap, already computed) and before NearFieldClaimPolicy's own component lookups, which assume a
            // live instance may exist. Safe in every direction: TryPrepareClaim/ClaimZone's own anyVerified gate
            // (ZoneClaimSweep.cs) means a zone with nothing verified this cycle is simply retried next cycle
            // (never marked held on nothing); ReleaseNearbyZDOS's own claim branch (SERVER:76962-76971) only
            // ever calls SetOwner(uid) for an object NOT already owned by uid, so this can never refuse a
            // legitimate re-affirmation of an object the server already owns; and this veto only ever fires for
            // a claim TO THE SERVER (see the uid checks above) -- an unowned, uninstantiated object stays
            // claimable by a nearby player's own vanilla per-peer pass exactly as if this mod did not exist.
            if (!__instance.Created)
            {
                s_claimDeferredNotCreated.Increment();
                s_policyRefused.Add(__instance.m_uid);
                return false;
            }

            try
            {
                bool claimable = NearFieldClaimPolicy.IsClaimable(__instance, uid);
                if (!claimable) s_policyRefused.Add(__instance.m_uid);
                return claimable;
            }
            catch (Exception e)
            {
                s_policyRefused.Add(__instance.m_uid);
                NoodlesLog.PatchException(nameof(NearFieldClaimVeto), e);
                // FAIL CLOSED here, deliberately the OPPOSITE of this codebase's usual "skip the new thing on
                // an exception" convention (e.g. ZoneClaimSweep's own release-tick Prefix defaults __state to
                // false, meaning "don't do the extra work"). For THIS specific prefix, "the new thing" the
                // usual convention would skip is the VETO itself, not the claim — and this veto is the only
                // thing standing between O6's own zone-eligibility bypass (also this task's own change, in
                // ZoneClaimSweep.cs) and an unfiltered near-field claim of whatever vanilla's own
                // ReleaseNearbyZDOS loop happens to be touching, including a live player's own avatar in the
                // one case its own structural exclusion above this catch never gets to run. Returning false
                // costs nothing but a missed claim this cycle (retried automatically next cycle); returning
                // true on a throw here could reintroduce exactly the near-field-claims-anything-vanilla-would-
                // reassign behaviour this whole file exists to prevent. Zero data loss either way — this
                // choice is about which SAFE outcome to prefer, not about avoiding an unsafe one.
                return false;
            }
        }

        // 1.0.7 (review follow-up): true while vanilla's ReleaseNearbyZDOS runs for the server's own uid -- O6's
        // ClaimZone call and vanilla ReleaseZDOS's own server pass alike. Set by
        // Patch_ZDOMan_ReleaseNearbyZDOS_ServerPassMark (below this class). That method sets no owner except
        // through its own two branches, so nothing else can land inside the mark. Main thread only.
        internal static bool InServerNearbyPass;

        [NoodlesModuleInit]
        internal static void Init()
        {
            PatchRegistry.Register("OwnershipAuthority", typeof(NearFieldClaimVeto), PatchSide.Both);
            PatchRegistry.Register("OwnershipAuthority", typeof(Patch_ZDOMan_ReleaseNearbyZDOS_ServerPassMark), PatchSide.Both);
        }
    }

    [HarmonyPatch(typeof(ZDOMan), "ReleaseNearbyZDOS")]
    internal static class Patch_ZDOMan_ReleaseNearbyZDOS_ServerPassMark
    {
        static void Prefix(long uid)
        {
            NearFieldClaimVeto.InServerNearbyPass = false;
            try
            {
                NearFieldClaimVeto.InServerNearbyPass =
                    ZNet.instance != null && ZNet.instance.IsServer() && uid == ZDOMan.GetSessionID();
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(Patch_ZDOMan_ReleaseNearbyZDOS_ServerPassMark), e);
            }
        }

        static Exception Finalizer(Exception __exception)
        {
            NearFieldClaimVeto.InServerNearbyPass = false;
            return __exception;
        }
    }
}
