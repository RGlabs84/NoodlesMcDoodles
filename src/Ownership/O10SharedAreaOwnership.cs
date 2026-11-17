using System.Collections.Generic;
using BepInEx.Configuration;
using NoodlesMcDoodles.Core;
using NoodlesMcDoodles.Core.ConfigFiles;
using UnityEngine;

namespace NoodlesMcDoodles.Ownership
{
    /// <summary>
    /// O10 (EXPERIMENTAL -- default ON as of the binding WP3 user decision, 2026-09-28: "every feature ON by
    /// default incl. O9, O10"; originally shipped default OFF per an earlier user decision 2, superseded).
    /// docs/design/03 "server-in-shared-areas": when 2+ connected peers all
    /// currently sight the same PLAYER-HELD object (via H10's own sighting capture,
    /// <see cref="LatencyAwareOwnership"/>) and EVERY one of those sighting peers is currently "far" (smoothed
    /// RTT &gt;= <see cref="FarRttThresholdMs"/>, sustained for <c>O9.RttDwellSeconds</c>, reused rather than a
    /// second dwell knob), the server becomes a legitimate candidate owner for that ONE object instead of
    /// whichever peer vanilla/O9 would otherwise pick.
    ///
    /// The point: for N peers who all see the same object, aggregate round-trip cost if a PLAYER P owns it =
    /// SUM(RTT_i, i != P) + (N-1)*RTT_P (everyone else double-hops through P; P pays 0). If the SERVER owns it,
    /// aggregate cost = SUM(RTT_i) (everyone pays exactly their own RTT, nobody doubles). Algebraically
    /// cost_player(P) = cost_server + (N-2)*RTT_P — the server never helps at N&lt;2 (structurally excluded
    /// below), ties in aggregate at N=2 while still lowering the WORST single round trip, and strictly wins,
    /// growing with N, at N&gt;=3. See scratchpad redesign/redesign-result.json proposals[1] for the full
    /// derivation, cost tables and judge review this class implements.
    ///
    /// Deliberately an O9 EXTENSION (shares H9/H10's sighting capture and
    /// <see cref="LatencyAwareOwnership.RunReassignmentPass"/>'s own per-ZDOID loop), never an O6 extension: O6's
    /// own candidate zones (<c>ZoneClaimSweep.ComputeCandidateZones</c>) are geometrically disjoint from O10's —
    /// every O10 candidate is, by construction, within some peer's own ~160m near-ring, i.e. exactly the
    /// territory O6's own exclusion test requires NOT to be true — so the two domains never contend for the same
    /// zone, and O10 needs neither <c>OwnershipAuthority</c> nor O6 active at all (full independence, matching
    /// O9's own "no functional dependency on ZoneClaimSweep" design intent).
    ///
    /// Every actually-risky primitive this touches — <see cref="ReclaimGate"/>, the Gameplay module's
    /// <c>HandoverGuard</c> category logic behind it, O7's <c>AuthorizedSetOwner</c>
    /// (<see cref="SetOwnerHoldGuard"/>), <see cref="ZoneSimGate"/> — is reused completely verbatim, never
    /// reimplemented; the only genuinely new thing is the DECISION of when to prefer the server, never how a
    /// claim/hold/release step is actually carried out. See <see cref="LatencyAwareOwnership.RunReassignmentPass"/>
    /// and its new <c>RunO10HandbackCheck</c> for the actual decision logic; <see cref="SharedAreaZoneHold"/> for
    /// the refcounted G3-L1-protection registry this claims/releases into.
    ///
    /// Still marked EXPERIMENTAL because the scenario this exists for (2+ simultaneously-connected far-RTT
    /// peers) has not yet been observed in any live session (both real sessions to date had exactly one far
    /// peer plus one near peer — precisely the mixed case O10 deliberately does NOT engage for) -- but
    /// default ON per the binding WP3 decision above; arms only through <see cref="SafeToggle"/>, same shape as
    /// <see cref="LatencyAwareOwnership.O9Armed"/> and
    /// <c>OwnershipAuthorityArmed</c> — O10 moves real ownership just like both of those, so it needs its own
    /// independent armed gate rather than piggybacking on either.
    /// </summary>
    internal static class O10SharedAreaOwnership
    {
        // --- Config --------------------------------------------------------------------------------------------

        internal static FeatureFlag O10Flag { get; private set; }
        private static ConfigEntry<bool> s_o10Active;
        private static ConfigEntry<float> s_farRttThresholdMs;

        internal static bool Active => O10Flag != null && O10Flag.Active;

        internal static bool O10Armed { get; private set; }

        internal static float FarRttThresholdMs => Mathf.Clamp(s_farRttThresholdMs.Value, 20f, 150f);

        // --- Shared helpers, called from LatencyAwareOwnership.RunReassignmentPass / RunO10HandbackCheck --------

        /// <summary>
        /// True only if EVERY sighting peer has a connected <c>ZNetPeer</c> AND a real (non-negative) smoothed
        /// RTT sample AND that RTT is at or above <paramref name="thresholdMs"/> — fails closed on any
        /// uncertainty (no peer, no RTT sample yet) rather than assuming "far" from missing data, matching this
        /// project's own zero-data-loss-adjacent posture for every other ownership decision.
        /// </summary>
        internal static bool AllSightingPeersFar(List<long> sightingUids, float thresholdMs)
        {
            foreach (long uid in sightingUids)
            {
                ZNetPeer peer = ZNet.instance != null ? ZNet.instance.GetPeer(uid) : null;
                if (peer == null) return false;
                float rtt = RttTracker.Smoothed(peer);
                if (rtt < 0f || rtt < thresholdMs) return false;
            }
            return true;
        }

        /// <summary>
        /// The lowest-RTT sighting peer with a real RTT sample, or 0 if none has one yet. Used by the
        /// server-&gt;player handback direction (hand back to whichever sighting peer is cheapest once any one
        /// drops below <see cref="FarRttThresholdMs"/>).
        /// </summary>
        internal static long BestSightingPeer(List<long> sightingUids, out float bestRtt)
        {
            long bestUid = 0L;
            bestRtt = float.MaxValue;
            foreach (long uid in sightingUids)
            {
                ZNetPeer peer = ZNet.instance != null ? ZNet.instance.GetPeer(uid) : null;
                if (peer == null) continue;
                float rtt = RttTracker.Smoothed(peer);
                if (rtt < 0f) continue;
                if (rtt < bestRtt) { bestRtt = rtt; bestUid = uid; }
            }
            return bestUid;
        }

        /// <summary>
        /// Reused verbatim: the SAME seam O6 uses ("no held-but-unfed zone is possible by construction",
        /// <see cref="ZoneSimGate.CanHold"/>, backed by <c>ZoneCap.WasIncludedThisTick</c>) — O10 additionally
        /// requires this at the moment of the switch itself, so it can never claim into a zone nothing is
        /// actually simulating (the exact defect that got the JIT-eligibility-ring-widening option refuted).
        /// Cross-module by name (<see cref="Features.Find"/>), matching <see cref="CrossFlagWarnings"/>'s own
        /// no-compile-time-reference convention — this module never references the ZoneSim module directly.
        /// </summary>
        internal static bool ZoneSimReady(Vector2s zone)
        {
            return Features.Find("ServerZoneSim")?.Active == true && ZoneSimGate.CanHold(zone);
        }

        // --- Arming (SafeToggle, same shape as LatencyAwareOwnership.O9Armed) ------------------------------------

        private static void ReconcileArmedState()
        {
            bool wantsOn = s_o10Active.Value;
            if (wantsOn == O10Armed) return;

            if (!wantsOn)
            {
                O10Armed = false;

                // BLOCKER FIX (O10 findings review): RunO10HandbackCheck's own very first line bails out
                // whenever O10.Active is off, WITHOUT releasing -- LatencyAwareOwnership.RunO10HandbackCheck's
                // own remarks -- so a live Active-off flip while an object is still server-held AND still
                // sighted (RunReassignmentPass keeps running off this flag alone whenever O9.Active stays on,
                // the realistic decision-1 default) would otherwise leave that hold stuck, forcing G3-L1 to
                // keep "protecting" the zone indefinitely -- exactly the gap the arm-failure log text's own
                // suggested remedy ("toggle Active off then on again") would trigger. Flush both the refcounted
                // hold registry and this module's own ZDOID->zone tracking here, mirroring the kill-switch
                // flush (SharedAreaZoneHold is ALSO registered as its own separate kill-switch callback below,
                // so this is the live-toggle path's equivalent of that, not a duplicate of it). The object
                // itself is left simply server-owned once its artificial G3-L1 protection is gone -- no data
                // loss either way -- and vanilla's own unmodified ReleaseNearbyZDOS reassigns it to a nearby
                // peer completely normally the next time one is actually near it.
                SharedAreaZoneHold.ClearAll();
                LatencyAwareOwnership.ClearO10HeldZoneTracking();
                NoodlesLog.Info("O10: disarmed (Active turned off) -- shared-area holds flushed.");
                return;
            }

            bool armed = SafeToggle.TryEnable(() => O10Armed = true, "O10");
            if (armed)
            {
                NoodlesLog.Info("O10: armed (forced pre-arm save verified).");
            }
            else
            {
                Telemetry.GetCounter("ownership.o10.armFailed").Increment();
                NoodlesLog.Error("O10: Active is on but arming was REFUSED -- see SafeToggle's own log line " +
                    "above for why (low disk space, or a silently-skipped save). No far-regime claim or handback " +
                    "will happen until this succeeds; toggle Active off then on again once the underlying " +
                    "problem is fixed, or restart.");
            }
        }

        // --- Kill switch -----------------------------------------------------------------------------------------

        internal static void ClearAllState()
        {
            // FIXER FIX (self-discovered while verifying/implementing this task's own listed findings, same
            // underlying bug class): this is wired as BOTH the "Ownership.O10" kill-switch flush AND
            // O10Flag.OnForcedOff -- the latter can fire with O9 fully healthy (O10 quarantined on its own),
            // in which case LatencyAwareOwnership.ClearAllState (which already clears s_o10HeldZones alongside
            // SharedAreaZoneHold -- see that file's own "WP1 per-feature quarantine FIX" remarks) never runs at
            // all. Without this, s_o10HeldZones would keep stale ZDOID->zone entries for anything still sighted
            // every tick after an O10-only force-off (ReconcileStaleO10Holds only evicts entries that stop being
            // sighted; RunO10HandbackCheck no-ops immediately once O10SharedAreaOwnership.Active is false,
            // before it would ever remove one) -- a bounded memory-only leak, never a correctness/data-loss
            // issue (SharedAreaZoneHold, the registry that actually matters for G3-L1 protection and O9/O10's
            // own reassignment decisions, is still correctly emptied by the line above either way). Mirrors
            // this exact file's own ReconcileArmedState live Active-off disarm path two screens up, which
            // already pairs these same two calls for the identical reason.
            SharedAreaZoneHold.ClearAll();
            LatencyAwareOwnership.ClearO10HeldZoneTracking();
        }

        // --- Init ------------------------------------------------------------------------------------------------

        [NoodlesModuleInit]
        internal static void Init()
        {
            ConfigFile config = NoodlesConfig.File;

            // 1.1.0: main file [3 - Server World] ServerHoldsForFarPlayers (was [O10] Active). EXPERIMENTAL: when 2+
            // connected peers all sight the same player-held object and EVERY one of them is far (FarRttThresholdMs),
            // sustained for O9.RttDwellSeconds, the server becomes the owner instead of whichever peer vanilla/O9
            // would pick, so no far player double-hops through another (cost_player(P) = cost_server + (N-2)*RTT_P;
            // better at N>=3, tied but fairer at N=2, never engages at N<2). Extends O9's reassignment pass, so it
            // needs O9 on too. Every ownership write still goes through ReclaimGate/HandoverGuard/O7, unmodified.
            // The 2+-far-peers scenario has not been exercised live yet. DEFAULT ON regardless (user override,
            // 2026-09-28 allOnTable); safe because it shares O9's fixed IsExcluded (Finding 4) and Finding 6 is fixed in
            // ZoneClaimSweep.Patch_ZDOMan_IsInPeerActiveArea_ServerHold.
            s_o10Active = NoodlesConfig.ServerHoldsForFarPlayers;
            O10Flag = Features.Register("O10", s_o10Active);

            // Same shape as O9Armed/OwnershipAuthorityArmed (docs/impl/CORE-API.md "Live-authority toggles"): a
            // live post-boot flip arms only via a forced, verified save; boot-time (already on in the config
            // file) arms directly, since nothing has moved yet this session.
            s_o10Active.SettingChanged += (_, __) => MainThreadDispatcher.Enqueue(ReconcileArmedState);
            if (s_o10Active.Value)
            {
                O10Armed = true;
                NoodlesLog.Verbose("O10: armed at boot (already on in the config file) -- no forced save.");
            }

            s_farRttThresholdMs = config.Bind("O10", "FarRttThresholdMs", 60f,
                new ConfigDescription("EXPERIMENTAL -- smoothed RTT (ms) a sighting peer must be AT OR ABOVE, " +
                    "sustained for O9.RttDwellSeconds, before O10 will even consider the server a candidate " +
                    "owner for an object that peer sees (and, for a server-held object, the RTT a peer must " +
                    "drop BELOW before it gets handed back to them). No vanilla equivalent. 60ms sits roughly " +
                    "half of Arizona's own observed 102-118ms live RTT, comfortably above a local/NJ peer's " +
                    "observed 5-20ms, and above O9's own RttMarginMs (30ms) floor so the two knobs never fight " +
                    "-- chosen so the trigger reliably fires for the flagship far+far scenario while never " +
                    "firing for an all-local group.",
                    new AcceptableValueRange<float>(20f, 150f)));

            // No new patch class: O10 rides H9/H10's existing two Harmony patches
            // (LatencyAwareOwnership.Patch_ZDOMan_ReleaseNearbyZDOS_SightingCapture /
            // Patch_ZDOMan_ReleaseZDOS_O9Policy, both now gated on "O9.Active || O10.Active") rather than a
            // second copy of the same [HarmonyPatch] targets -- registering the same patch CLASS a second time
            // under a second feature name would double-apply it via PatchRegistry.ApplyAll's own
            // one-CreateClassProcessor-call-per-registration contract. Same no-dedicated-patch-class shape this
            // codebase already uses for Gameplay.StructureHandoverGuard/Gameplay.DoorAutoClose (flags other
            // patch classes check inline).

            ArmedStateGate.O10ArmedProvider = () => O10Armed;

            KillSwitch.RegisterFlushCallback("Ownership.O10", ClearAllState);

            // WP1 per-feature quarantine: same release path as the kill-switch flush above, now ALSO wired as
            // this feature's own OnForcedOff (CORE-API.md "Flush callbacks" / FeatureFlag.OnForcedOff remarks).
            O10Flag.OnForcedOff = ClearAllState;
        }
    }
}
