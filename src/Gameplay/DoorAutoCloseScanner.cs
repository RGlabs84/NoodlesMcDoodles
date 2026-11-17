using System;
using System.Collections.Generic;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.Gameplay
{
    /// <summary>
    /// S8 new feature (docs/design/05, Hook H8) — server-side door auto-close. DEFAULT ON (2026-09-28, all-on
    /// safety-net spec WP4, overriding Decision 4's original off-by-default) — see
    /// <see cref="Features_Gameplay.DoorAutoClose"/>'s own config description for why this was judged safe to
    /// flip.
    /// The user's question: can the server auto-close doors, should it be player-placed only, and is it safe?
    /// <b>Yes; yes, player-placed-only (non-configurable, see the <c>ZDOVars.s_creator</c> check in
    /// <see cref="Scan"/>); yes, safe</b> — the design needs no live GameObject at all, only ZDO fields the
    /// server already has once it owns the door.
    /// <para>
    /// <c>Door.RPC_UseDoor(long, bool)</c> (SERVER:123807) writes state purely at the ZDO level:
    /// <c>ZDOVars.s_state == 0</c> means closed, nonzero means open (direction-signed). This scanner makes the
    /// exact same write — <c>zdo.Set(ZDOVars.s_state, 0)</c> — through the identical replication path, without
    /// needing the door's GameObject instantiated anywhere: every peer's own <c>Door.UpdateState()</c> (already
    /// polling <c>DataRevision</c> at 0.2s, SERVER:123657) picks it up and animates it locally, unmodified.
    /// </para>
    /// <para>
    /// <c>Piece.m_creator</c> is itself ZDO-persisted (<c>ZDOVars.s_creator</c>, SERVER:136141) and is only
    /// ever set from the hammer-placement path (<c>SetCreator</c>, SERVER:136308-136317, called from
    /// SERVER:12503-12506 when a player finishes placing a piece) — a Location/dungeon door never goes through
    /// that path, so it reads back the same default (0) a freshly-instantiated ZDO starts with. This is the
    /// confirmed, non-configurable signal for "player-placed": <c>zdo.GetLong(ZDOVars.s_creator, 0L) != 0L</c>.
    /// </para>
    /// <para>
    /// <b>Reviewer note (2026-09-28, MINOR, not a fix — a fact to state plainly):</b> this writes
    /// <c>ZDOVars.s_state</c> DIRECTLY on the ZDO, not by calling <c>Door.Interact</c>/<c>RPC_UseDoor</c> —
    /// unlike a real player closing that same door, this scan never runs <c>PrivateArea.CheckAccess(...)</c>
    /// (the warded-base access check <c>Door.UseItem</c>'s own keyed-door branch performs, SERVER: the
    /// <c>m_checkGuardStone</c> guard) and never passes through any OTHER installed mod's own Harmony patches on
    /// <c>Door.Interact</c>/<c>RPC_UseDoor</c> (a ward/permission mod's own gate, for example). This is
    /// intentional, not an oversight: the scanner only ever touches a ZDO the SERVER PROCESS ITSELF already
    /// owns, scoped to server-held zones (<see cref="ServerZoneMembership.IsServerHeld"/>) — it is a privileged
    /// server housekeeping action, not a simulated player interaction, so player-facing access rules were never
    /// going to apply to it either way. It carries no data-loss risk (single ZDO field write through vanilla's
    /// own replication path, same as always) — the only thing worth knowing is that a ward mod's own protection
    /// against a stranger closing your door does not extend to this specific server action.
    /// </para>
    /// <para>
    /// <b>Tier-2 detail logging (2026-09-28, all-on safety-net spec WP4).</b> Every close, and every one of the
    /// three skip reasons already counted via <see cref="Telemetry"/>, also fires a rate-limited
    /// <see cref="NoodlesLog.DetailRateLimited"/> call into <c>detail.log</c> (firstN=5/window=60s each, keyed
    /// per counter name) — the first few occurrences of a given reason in any 60s window log in full (ZDOID,
    /// and for a close, zone + idle duration); the rest of that window are absorbed into one "+K more
    /// suppressed" line. Deliberately not one line per door: a busy server's own scan (<see cref="Scan"/>) can
    /// walk many doors — mostly skipped ones — in a single pass, and WP1's own existing rate-limiter is the
    /// aggregation this needs, not a bespoke one.
    /// </para>
    /// </summary>
    internal sealed class DoorAutoCloseScanner : MonoBehaviour
    {
        private static DoorAutoCloseScanner s_instance;
        private readonly Dictionary<ZDOID, float> _openSince = new Dictionary<ZDOID, float>();
        // Prefab hash -> Door template component, or null if that prefab isn't a Door at all. Avoids a
        // GetComponent<Door>() per ZDO instance for every one of what may be many instances of the same prefab.
        private readonly Dictionary<int, Door> _doorPrefabCache = new Dictionary<int, Door>();

        private float _nextScan;

        internal static void EnsureCreated()
        {
            if (s_instance != null) return;
            var go = new GameObject("NoodlesMcDoodles.DoorAutoCloseScanner");
            UnityEngine.Object.DontDestroyOnLoad(go);
            s_instance = go.AddComponent<DoorAutoCloseScanner>();
        }

        /// <summary>Kill-switch flush: clears in-memory state; writes no ZDOs by itself and needs no hand-back (H8 Flag-off text).</summary>
        internal static void FlushOnDisable()
        {
            if (s_instance != null) s_instance._openSince.Clear();
        }

        // FIX (cpu-memory finding, this pass): single-zone FindSectorObjects (SERVER:77189-77257, confirmed
        // this pass) instead of a whole-ZDOMan-dictionary walk -- see Scan()'s own remarks for the full
        // rationale. NearSimulationDistance=0/TotalSimulationDistance=0 makes FindSectorObjects visit exactly
        // one sector (FindObjects(sector, ...), SERVER:77414-77429: `m_objectsBySector[sectorIndex.Sector]`,
        // an O(objects-in-that-one-sector) lookup, never the whole world) -- same constant OwnershipLossWatch
        // already uses (src/Ownership/OwnershipLossWatch.cs) for the identical "just this one held zone" shape.
        private static readonly SimulationDistance s_singleZoneOnly = new SimulationDistance(0, 0, false);

        // Reused across scans, never reallocated per zone/per tick -- same shape as OwnershipLossWatch.s_scratch,
        // ZoneClaimSweep.s_scratchScan, PickableRelease.s_scratch.
        private readonly List<ZDO> _scratch = new List<ZDO>();

        private void Update()
        {
            if (!Features_Gameplay.DoorAutoClose.Active) { _openSince.Clear(); return; }
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            float now = Time.realtimeSinceStartup;
            if (now < _nextScan) return;
            _nextScan = now + Mathf.Max(1f, Features_Gameplay.DoorAutoCloseScanIntervalSeconds.Value);

            try
            {
                Scan(now);
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(DoorAutoCloseScanner), e);
            }
        }

        private void Scan(float now)
        {
            if (ZDOMan.instance == null || ZNetScene.instance == null) return;

            float idleSeconds = Features_Gameplay.DoorAutoCloseIdleSeconds.Value;
            var stillOpen = new HashSet<ZDOID>();

            // FIX (cpu-memory finding, this pass): scoped to zones the Ownership module has actually claimed
            // (ServerZoneMembership.Snapshot()) via the PUBLIC ZDOMan.FindSectorObjects, instead of walking
            // ZDOMan's own m_objectsByID -- every persisted ZDO on the server, ~1.58M on Proxima -- once per
            // ScanIntervalSeconds (default 30s). That walk's cost scaled with TOTAL WORLD ZDO COUNT, not door
            // count, loaded-object count, or online-player count, and was never chunked or bounded across
            // frames -- unlike every sibling scan in this codebase (ZoneCap's MaxObjectsGlobal/PerPeer caps,
            // ZoneClaimSweep's round-robin MaxZonesClaimedPerCycle cap, PickableRelease's/OwnershipLossWatch's
            // own held-zones-only FindSectorObjects scans).
            //
            // Not a scope change: the removed per-ZDO "Scope check" below already excluded every door outside
            // a server-held zone before ever touching it (the blueprint's own stated scope for this feature,
            // quoted in this class's own doc comment above), so the SET of doors ever eligible to be acted on
            // is identical -- only the cost of getting there changes, from O(total world ZDOs) to O(objects in
            // zones the server actually holds). FindSectorObjects with NearSimulationDistance=0/
            // TotalSimulationDistance=0 (SERVER:77189-77257, confirmed this pass) visits exactly the one
            // sector asked for, via ZDOMan's own m_objectsBySector per-sector index (SERVER:77414-77429) -- an
            // O(objects-in-that-one-sector) lookup, never another whole-dictionary walk. Exact pattern
            // OwnershipLossWatch.ScanHeldZonesForRevisionRegression already uses (src/Ownership/
            // OwnershipLossWatch.cs) for its own identical "just the zones we hold" shape.
            //
            // Accepted side effect (same tradeoff OwnershipLossWatch's own zone-scoped design already made in
            // this codebase): a door the server happens to own via vanilla's own default ownership assignment
            // OUTSIDE any zone the Ownership module has claimed (OwnershipAuthority.Active off, or a zone not
            // yet claimed) is no longer visited at all, so the old "skippedOwnershipBoundary" telemetry line --
            // which needed exactly the whole-world walk this fix removes to compute -- is retired; the ACTION
            // taken (whether a door is ever auto-closed) does not change, since such a door was never eligible
            // to be closed either way, only counted as skipped. (docs/impl/GAMEPLAY-IMPL.md's own telemetry
            // list and docs/impl/INTEGRATION-REVIEW.md:127's "walks every ZDO" note both predate this fix and
            // are stale now -- not owned by this pass, flagging for a docs update.)
            foreach (Vector2s zone in ServerZoneMembership.Snapshot())
            {
                _scratch.Clear();
                ZDOMan.instance.FindSectorObjects(zone, s_singleZoneOnly, _scratch);
                if (_scratch.Count == 0) continue;

                // Iterating our OWN scratch list (a copy FindSectorObjects filled), never ZDOMan's own live
                // map -- calling zdo.Set() below (ZDO.Set edits an existing ZDO's fields in place, never
                // touches ZDOMan's dictionary or sector index) is exactly as safe against this list as it was
                // against the live foreach this replaced.
                foreach (ZDO zdo in _scratch)
                {
                    if (zdo == null) continue;

                    // Cheapest, most selective filter first: only doors THIS process currently, actually owns.
                    // Writing a ZDO this process does not own would violate vanilla's own single-writer
                    // replication model — the exact hazard this whole module exists to avoid, not something
                    // H8 may relax. Kept even though every zone reached here is already server-held: a zone's
                    // claim and one specific ZDO's own current ownership are tracked separately (a handover in
                    // flight, an excluded object type, etc.), so this remains the real safety gate -- unlike
                    // the old per-ZDO zone recheck this loop shape makes redundant by construction (every ZDO
                    // here already came from a zone in ServerZoneMembership.Snapshot()).
                    if (!zdo.IsOwner()) continue;

                    Door door = ResolveDoorTemplate(zdo);
                    if (door == null) continue;

                    if (zdo.GetLong(ZDOVars.s_creator, 0L) == 0L) // NOT player-placed — hard, non-configurable exclusion
                    {
                        Telemetry.GetCounter("gameplay.doorAutoClose.skippedNotPlayerPlaced").Increment();
                        NoodlesLog.DetailRateLimited("gameplay.doorAutoClose.skippedNotPlayerPlaced",
                            $"DoorAutoClose: skipped {zdo.m_uid} (not player-placed -- a Location/dungeon door).",
                            firstN: 5, windowSeconds: 60f);
                        continue;
                    }

                    // Door.CanInteract() (SERVER:123684-123687) treats a keyed OR canNotBeClosed door
                    // identically while it's open: neither can be closed via the normal RPC_UseDoor path in
                    // that state, so a player standing at their own keyed door couldn't close it that way
                    // either — the scanner must not bypass that via a direct ZDO write.
                    if (door.m_canNotBeClosed || door.m_keyItem != null)
                    {
                        Telemetry.GetCounter("gameplay.doorAutoClose.skippedCanNotBeClosed").Increment();
                        NoodlesLog.DetailRateLimited("gameplay.doorAutoClose.skippedCanNotBeClosed",
                            $"DoorAutoClose: skipped {zdo.m_uid} (keyed door or m_canNotBeClosed, same as " +
                            "Door.CanInteract()'s own treatment of an open keyed/canNotBeClosed door).",
                            firstN: 5, windowSeconds: 60f);
                        continue;
                    }

                    int state = zdo.GetInt(ZDOVars.s_state, 0);
                    if (state == 0) { _openSince.Remove(zdo.m_uid); continue; } // already closed

                    stillOpen.Add(zdo.m_uid);
                    if (!_openSince.TryGetValue(zdo.m_uid, out float since))
                    {
                        _openSince[zdo.m_uid] = now;
                        continue;
                    }
                    if (now - since < idleSeconds) continue;

                    zdo.Set(ZDOVars.s_state, 0); // same write RPC_UseDoor's own "closed" branch makes
                    _openSince.Remove(zdo.m_uid);
                    Telemetry.GetCounter("gameplay.doorAutoClose.closed").Increment();
                    NoodlesLog.DetailRateLimited("gameplay.doorAutoClose.closed",
                        $"DoorAutoClose: closed {zdo.m_uid} in zone {zone} after " +
                        $"{now - since:F0}s idle (IdleSeconds={idleSeconds:F0}).",
                        firstN: 5, windowSeconds: 60f);
                }
            }

            // Forget anything we were timing that's no longer open/owned/in-scope this scan (closed by a
            // player, handed back to another owner, zone released, etc.) — bounds memory and matches "not a
            // sticky flag" the rest of this module already insists on elsewhere.
            if (_openSince.Count > 0)
            {
                var stale = new List<ZDOID>();
                foreach (ZDOID id in _openSince.Keys) if (!stillOpen.Contains(id)) stale.Add(id);
                foreach (ZDOID id in stale) _openSince.Remove(id);
            }
        }

        private Door ResolveDoorTemplate(ZDO zdo)
        {
            int prefabHash = zdo.GetPrefab();
            if (_doorPrefabCache.TryGetValue(prefabHash, out Door cached)) return cached;

            GameObject prefab = ZNetScene.instance.GetPrefab(prefabHash);
            Door door = prefab != null ? prefab.GetComponent<Door>() : null;
            _doorPrefabCache[prefabHash] = door; // caches the "not a door" null result too — same cost either way
            return door;
        }
    }
}
