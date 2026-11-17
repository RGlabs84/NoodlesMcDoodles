using System.Collections.Generic;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.Ownership
{
    /// <summary>
    /// SAA WP-A/WP-B (<c>scratchpad/saa/final.md</c> §1/§2/§4) — the "pickables are never taken" invariant
    /// (USER DECISIONS), for the one category vanilla itself can't be trusted to keep off the server without
    /// help: <c>Pickable.RPC_Pick</c> (SERVER:71071-71078, confirmed this session) runs on the ZDO's current
    /// OWNER and unconditionally reads <c>Player.m_localPlayer</c> for its pick-effect credit — permanently
    /// null on a dedicated server, so a server-owned Pickable is a live NRE risk for the very next player who
    /// tries to harvest it (mitigated independently, belt-and-suspenders, by <c>Gameplay/NREGuards.cs</c>'s own
    /// <c>Patch_Pickable_RPC_Pick</c> — this file's job is to make sure the precondition itself stays rare-to-
    /// never, not to rely on that backstop).
    ///
    /// Two call sites, both consumed by <see cref="NearFieldClaimPolicy"/> ("saa:core", a different file/owner):
    /// <list type="bullet">
    /// <item><see cref="IsNearLivePlayer"/> — the claim-time veto: never let a near-field bulk claim
    /// (<c>NearFieldClaimVeto</c>'s <c>ZDO.SetOwner</c> prefix) take a Pickable in the first place.</item>
    /// <item><see cref="RunReleasePass"/> — the retroactive corrective pass a claim-time veto alone can't cover
    /// (a Pickable claimed on an EARLIER cycle, before a peer walked up, or claimed via a completely different
    /// path such as O10's far-regime server-candidacy, which does not consult <c>NearFieldClaimPolicy</c> at
    /// all): called once per claim cycle by <c>ZoneClaimSweep.RunClaimCycle</c>, unconditionally hands EVERY
    /// server-held Pickable found in a scan of any connected peer's own zone plus its immediate neighbours back
    /// (owner 0) through <see cref="OwnershipApi"/> — regardless of which mechanism put it there, and regardless
    /// of exact distance from that peer's live position within the scanned area (MAJOR FIX, lead review: no
    /// longer additionally gated on <see cref="IsNearLivePlayer"/>/<see cref="NearFieldClaimPolicy.PickableReleaseRadius"/>
    /// — "pickables are never taken" is the user's own unconditional decision, not a radius-bounded one, and this
    /// is the only backstop for a path, like O10's, that <see cref="NearFieldClaimPolicy.IsClaimable"/>'s own now-
    /// categorical Pickable exclusion cannot reach). <see cref="IsNearLivePlayer"/> itself stays as-is and is
    /// still used by <c>NearFieldClaimPolicy.Evaluate</c>'s own defense-in-depth check.</item>
    /// </list>
    ///
    /// Both read a peer's LIVE character position — the connected peer's own <c>ZNetPeer.m_characterID</c> ZDO
    /// position — never <c>ZNetPeer.GetRefPos()</c>, which <c>ZoneClaimSweep.cs</c>'s own remarks (G3-N1) put at
    /// up to ~3 s / ~45 m stale under ordinary movement: adequate padding for a ~96 m zone-eligibility check, but
    /// far too coarse against <see cref="NearFieldClaimPolicy.PickableReleaseRadius"/>'s own default 12 m, itself
    /// sized just above vanilla's 5 m interact range (<c>Humanoid.m_maxInteractDistance</c>, SERVER:9577,
    /// confirmed this session) plus one claim-cycle's worth of running-speed movement.
    ///
    /// Server-only (both functions no-op off the server); allocation-free once warm on the "nothing to do" path
    /// — <c>ZNet.GetPeers()</c> returns the live <c>m_peers</c> list directly (SERVER:81676-81679, confirmed —
    /// no copy), and <see cref="RunReleasePass"/>'s <c>FindSectorObjects</c> target list is a reused
    /// <c>static readonly</c> scratch list, matching <c>ZoneClaimSweep.s_scratchScan</c>'s own established shape.
    /// Never preloads/instantiates anything — reads only zones/ZDOs that already exist in <c>ZDOMan</c>'s own
    /// index, exactly like every other read-only scan in this module.
    /// </summary>
    internal static class PickableRelease
    {
        // --- Live character position, shared by both entry points below --------------------------------------

        /// <summary>
        /// The peer's own character ZDO position — never <see cref="ZNetPeer.GetRefPos"/> (see class remarks).
        /// False (no position) for a peer that hasn't spawned a character yet (<c>ZDOID.None</c>) or whose
        /// character ZDO can't currently be resolved (e.g. mid-teleport/respawn) — never a guessed fallback
        /// position, since a wrong "near" answer here could either wrongly protect an object that isn't
        /// actually near anyone (harmless — see below) or wrongly release-decide against one that is (also
        /// harmless: release only ever unclaims, it never destroys or reassigns to the wrong owner). Both
        /// failure directions are safe by construction, which is why "just skip this peer" is an acceptable
        /// default rather than something needing its own fail-open/fail-closed deliberation.
        /// </summary>
        private static bool TryGetLiveCharacterPosition(ZNetPeer peer, out Vector3 position)
        {
            position = default;
            if (peer == null || peer.m_characterID == ZDOID.None) return false;
            ZDO zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(peer.m_characterID) : null;
            if (zdo == null) return false;
            position = zdo.GetPosition();
            return true;
        }

        // --- Claim-time veto: NearFieldClaimPolicy.Evaluate asks this once per Pickable candidate -------------

        /// <summary>
        /// True when <paramref name="position"/> is within <see cref="NearFieldClaimPolicy.PickableReleaseRadius"/>
        /// of any connected peer's LIVE character position. Checked against EVERY connected peer, not just
        /// whichever one a caller happens to be iterating around — <see cref="RunReleasePass"/> relies on this
        /// exact property to stay correct even when the peer whose zone-scan surfaced a candidate isn't the
        /// same peer who is actually standing next to it (two peers in neighbouring zones).
        /// </summary>
        internal static bool IsNearLivePlayer(Vector3 position)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return false;

            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            if (peers == null || peers.Count == 0) return false;

            float radius = NearFieldClaimPolicy.PickableReleaseRadius;
            foreach (ZNetPeer peer in peers)
            {
                if (!TryGetLiveCharacterPosition(peer, out Vector3 livePos)) continue;
                if (Vector3.Distance(livePos, position) <= radius) return true;
            }
            return false;
        }

        // --- Retroactive release pass: ZoneClaimSweep.RunClaimCycle calls this once per claim cycle ----------

        // Zone + immediate neighbours only (NearSimulationDistance=1, no additional far/ghost ring: far=0) —
        // exactly "each peer's own zone and neighbours", matching final.md §2's own sizing ("Pickable-radius <<
        // zone width, so this is bounded and cheap — a handful of zones per peer, not the whole held set").
        // FindSectorObjects (SERVER:77189-77223, confirmed this session) dedups shared sectors between the
        // near-ring loop and its own trailing "distant" loop via one shared visited-set, so passing far=0 here
        // never double-walks a zone even though NearSimulationDistance and TotalSimulationDistance are equal.
        private static readonly SimulationDistance s_peerZoneAndNeighbours = new SimulationDistance(1, 0, false);

        // Reused across calls, never reallocated per tick/per zone — same shape as ZoneClaimSweep.s_scratchScan
        // and OwnershipLossWatch.s_scratch.
        private static readonly List<ZDO> s_scratch = new List<ZDO>();

        private static readonly Counter s_pickableRetroactiveReleases =
            Telemetry.GetCounter("ownership.o11.pickableRetroactiveReleases");
        private static readonly Counter s_treeRetroactiveReleases =
            Telemetry.GetCounter("ownership.o11.treeRetroactiveReleases");
        private static readonly Counter s_freshBuildReleases =
            Telemetry.GetCounter("ownership.o11.freshBuildReleases");
        private static readonly Counter s_tombstoneReleases =
            Telemetry.GetCounter("ownership.o11.tombstoneReleases");

        /// <summary>
        /// For each connected peer, scans that peer's own zone plus its immediate neighbours and hands any
        /// server-held Pickable inside <see cref="NearFieldClaimPolicy.PickableReleaseRadius"/> of a live peer
        /// back to unclaimed (owner 0) via <see cref="OwnershipApi"/> — regardless of how it came to be
        /// server-held (the near-field claim-time veto above covers only O6's own bulk-claim path; this pass is
        /// the general backstop for any other path, e.g. O10's far-regime server-candidacy, which does not
        /// consult <see cref="IsNearLivePlayer"/> at all). Scales with peer count, not held-zone count (final.md
        /// §2), so it stays cheap regardless of world size or how many zones the server currently holds.
        /// </summary>
        // 1.0.11: the same pass also hands back, near each live player, server-held trees and logs, tombstones
        // (not at Mode=All), and pieces a still-connected player placed this session -- see the cases in the loop
        // below. Capped at MaxReleasesPerCycle, with a back-off for releases another mod refuses.
        internal static void RunReleasePass()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (ZDOMan.instance == null || ZNetScene.instance == null) return;

            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            if (peers == null || peers.Count == 0) return;

            long sessionId = ZDOMan.GetSessionID();
            s_releasesThisCycle = 0;
            PruneRefused();

            foreach (ZNetPeer peer in peers)
            {
                if (s_releasesThisCycle >= MaxReleasesPerCycle) break; // review: never an unbounded burst in one frame
                if (!TryGetLiveCharacterPosition(peer, out Vector3 livePos)) continue;

                Vector2s zone = ZoneSystem.GetZone(livePos);
                s_scratch.Clear();
                ZDOMan.instance.FindSectorObjects(zone, s_peerZoneAndNeighbours, s_scratch);
                if (s_scratch.Count == 0) continue;

                foreach (ZDO zdo in s_scratch)
                {
                    if (zdo == null) continue;
                    if (zdo.GetOwner() != sessionId) continue; // not server-held right now -- nothing to release
                    if (s_releasesThisCycle >= MaxReleasesPerCycle) break;
                    if (s_refusedUntil.Count > 0 && s_refusedUntil.TryGetValue(zdo.m_uid, out float until) && Time.time < until) continue;
                    switch (KindOf(zdo.GetPrefab()))
                    {
                        case ReleaseKind.Pickable:
                            // MAJOR FIX (lead review, dl-saa-compat #2): previously gated on IsNearLivePlayer (the
                            // tight ~12m PickableReleaseRadius), which only ever covers a Pickable a player is right
                            // on top of. The user's own binding decision is unconditional ("pickables are never
                            // taken"), and this pass is the ONLY backstop for a Pickable claimed via a path that
                            // doesn't consult NearFieldClaimPolicy.IsClaimable at all (O10's far-regime server-
                            // candidacy -- see class remarks). Every server-held Pickable this scan finds is
                            // released, not only the ones within interact-adjacent range of a live player. The scan
                            // stays bounded to each peer's own zone + immediate neighbours.
                            if (TryRelease(zdo, "O11 proactive pickable release"))
                                s_pickableRetroactiveReleases.Increment();
                            break;

                        case ReleaseKind.Tombstone:
                            // 1.0.11: a tombstone the server holds near a live player goes back. This mod never
                            // claims one (Container is an all-only class, refused at every distance), but ZenMap
                            // 1.11.0's MapTrack claims every new tombstone for the server on a dedicated server that
                            // instantiates it (see the Piece case). Server-side, TombStone.PositionCheck only lifts
                            // it back above ground where the server's terrain raycast hits, and a tombstone was found
                            // at y=-400 on live (2026-10-01). Released, vanilla gives it to the nearby player, whose
                            // game has that ground loaded -- as in vanilla.
                            // review: at Mode=All, O6 itself claims containers near players by design, so a release
                            // here would only fight it; the opt-in mode keeps its own behaviour.
                            if (NearFieldClaimPolicy.EffectiveMode == NearFieldClaimPolicy.OwnershipAuthorityMode.All) break;
                            if (TryRelease(zdo, "1.0.11 tombstone release near a player"))
                                s_tombstoneReleases.Increment();
                            break;

                        case ReleaseKind.TreeOrLog:
                            // 1.0.11 (live Proxima 2026-10-01: "trees vanish before they touch the ground", "a stump
                            // vanished"): trees and logs near a live player go back to vanilla ownership -- see
                            // NearFieldClaimPolicy.IsFarOrSharedClass's 1.0.11 remarks. The far band may have claimed
                            // them before the player arrived; released here, vanilla's own pass gives them to the
                            // player within ~2 s, long before the player can reach them (this 3x3 reaches 64 m+ past
                            // the player's own zone edge). Near-field they are never re-claimed (ModeNotAllowed).
                            if (TryRelease(zdo, "1.0.11 proactive tree/log release near a player"))
                                s_treeRetroactiveReleases.Increment();
                            break;

                        case ReleaseKind.Piece:
                            // 1.0.11: a piece a still-connected player placed this session, held by the server near
                            // a live player, goes back. NearFieldClaimPolicy's FreshBuild veto stops O6 from claiming
                            // it, but ZenMap 1.11.0's MapTrack calls ZNetView.ClaimOwnership() in a ZNetView.Awake
                            // postfix the first time it sees a portal/cart/ship/tombstone/map table/bonfire without
                            // its GUID, and on a dedicated server running this mod the server instantiates those
                            // (mirror repro r4: a fresh portal went phantom -> server inside
                            // ZNetScene.CreateObject). ZenMap claims once per object (GUID set): no cycling.
                            if (NearFieldClaimPolicy.IsFreshBuildOfConnectedPeer(zdo) &&
                                TryRelease(zdo, "1.0.11 fresh-build release near a player"))
                                s_freshBuildReleases.Increment();
                            break;
                    }
                }
            }
        }

        // review (1.0.11): at most this many releases per cycle (the rest go next cycle, ~1 s later), so arriving at
        // a far-band-held forest never releases thousands of objects in one frame.
        private const int MaxReleasesPerCycle = 200;
        private static int s_releasesThisCycle;

        // review (1.0.11): a release another mod refuses (e.g. a door GetOffMyLawn's DoorCloser holds) is not
        // retried for RefusedBackoffSeconds. 1.0.12: a refused release (a hand-back to nobody) no longer counts toward
        // ownership.setOwnerVetoed at all (see SetOwnerHoldGuard.AuthorizedSetOwner): on live Proxima 57 refused
        // releases in 60 s (Wonderland holding INIGO's harvested onions) tripped ModeQuarantine and left the server in
        // SharedFar for a day.
        private const float RefusedBackoffSeconds = 120f;
        private static readonly Dictionary<ZDOID, float> s_refusedUntil = new Dictionary<ZDOID, float>();
        private static readonly List<ZDOID> s_pruneScratch = new List<ZDOID>();

        private static bool TryRelease(ZDO zdo, string reason)
        {
            s_releasesThisCycle++;
            if (OwnershipApi.SetOwner(zdo, 0, reason)) return true;
            s_refusedUntil[zdo.m_uid] = Time.time + RefusedBackoffSeconds;
            return false;
        }

        private static void PruneRefused()
        {
            if (s_refusedUntil.Count == 0) return;
            float now = Time.time;
            s_pruneScratch.Clear();
            foreach (var kv in s_refusedUntil) if (now >= kv.Value) s_pruneScratch.Add(kv.Key);
            foreach (ZDOID id in s_pruneScratch) s_refusedUntil.Remove(id);
        }

        private enum ReleaseKind : byte { None, Pickable, Tombstone, TreeOrLog, Piece }

        // 1.0.11: one GetComponent sweep per prefab hash for the whole session instead of up to five per server-
        // held object per cycle. Precedence matches the pre-1.0.11 pass: a Pickable is a Pickable first.
        private static readonly Dictionary<int, ReleaseKind> s_kindByPrefab = new Dictionary<int, ReleaseKind>();

        private static ReleaseKind KindOf(int prefabHash)
        {
            if (s_kindByPrefab.TryGetValue(prefabHash, out ReleaseKind kind)) return kind;
            GameObject prefab = ZNetScene.instance.GetPrefab(prefabHash);
            kind = ReleaseKind.None;
            if (prefab != null)
            {
                if (prefab.GetComponent<Pickable>() != null) kind = ReleaseKind.Pickable;
                else if (prefab.GetComponent<TombStone>() != null) kind = ReleaseKind.Tombstone;
                else if (prefab.GetComponent<TreeBase>() != null || prefab.GetComponent<TreeLog>() != null) kind = ReleaseKind.TreeOrLog;
                else if (NearFieldClaimPolicy.IsPlayerBuiltStructure(prefab)) kind = ReleaseKind.Piece;
                s_kindByPrefab[prefabHash] = kind; // an unresolved hash is not cached, so a late-registered prefab is still classified
            }
            return kind;
        }
    }
}
