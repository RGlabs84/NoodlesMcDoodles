using System;
using System.Collections.Generic;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.Ownership
{
    /// <summary>
    /// Real loss signals for server-held ZDOs (DESIGN.md Lead decision 4; CORE-API.md seam <c>LossSignals</c>):
    /// this module's own ownership churn (O6/O7/O8/O9) is exactly the kind of activity that could, if buggy,
    /// silently lose or duplicate world state, so it is also the module responsible for watching for signals
    /// Lead decision 4 names. Both watchers are pure observation (no vanilla-behaviour change), gated on
    /// <see cref="KillSwitch.Enabled"/> and <see cref="Features_Ownership.OwnershipAuthority"/>'s own
    /// <c>.Active</c> (LEAD FIX: the original version gated on the kill switch alone), and designed specifically
    /// to stay silent through ordinary gameplay (a player chopping a tree, killing a creature, picking an item
    /// in a held zone) — a single false <c>LossSignals.Report</c> switches the WHOLE mod off (AutoStandDown).
    ///
    /// 1. <see cref="ScanHeldZonesForRevisionRegression"/> — the one REAL loss signal here. <c>DataRevision</c>/
    ///    <c>OwnerRevision</c> (both confirmed <c>public</c> auto-properties on <c>ZDO</c>, SERVER:73539/73541)
    ///    going backwards on a ZDO the server currently owns. Vanilla's own receive-side merge
    ///    (<c>ZDOMan.RPC_ZDOData</c>, SERVER:77123-77186, confirmed this pass) only ever WRITES a higher
    ///    <c>DataRevision</c>, or leaves it unchanged and only raises <c>OwnerRevision</c> — never lowers either
    ///    field through the normal network path. A decrease on a ZDO this module currently holds should
    ///    therefore be structurally unreachable through ordinary play; observing one anyway is real evidence of
    ///    a bug. (GetOffMyLawn's own <c>Claim()</c> deliberately JUMPS <c>DataRevision</c> forward by 4096 to win
    ///    a race against a stale packet — a legitimate, large, forward-only change this check never flags, since
    ///    it only ever looks for decreases.)
    /// 2. <see cref="Patch_ZDOMan_DestroyZDO_LossWatch"/> — LEAD FIX (data-loss + runtime reviews, both
    ///    CONFIRMED against the decompiles): the original version reported a <c>Persistent</c> ZDO destroyed
    ///    while server-held with no peer within 128m as a THIRD real loss signal via <c>LossSignals.Report</c>.
    ///    That was wrong: every zone this module holds is, by construction, >=~160m from any connected peer
    ///    (G3-N1's own padding on top of ZoneClaimSweep's ~96m base active-area radius — see
    ///    <c>ZoneClaimSweep.AnyPeerWithinPaddedActiveArea</c>), so "no peer within 128m" is ALWAYS true for
    ///    anything the server legitimately holds, and server-owned simulation legitimately destroys persistent
    ///    ZDOs there constantly with nobody watching: <c>Plant.Grow</c> (SERVER:136704, a grown plant
    ///    replaces/destroys its own sprout ZDO), creature-vs-creature kills, fire/environmental damage,
    ///    <c>WearNTear</c> collapse, raid creatures despawning, <c>ItemDrop</c> timeouts. Reporting this via
    ///    <c>LossSignals.Report</c> would trip AutoStandDown on ordinary gameplay the instant the server held
    ///    ANY zone doing ANY of the above — telemetry-only now (a counter plus a rate-limited, prefab-and-
    ///    position-naming warning), never a kill-switch trip. See that patch class's own remarks.
    /// </summary>
    internal static class OwnershipLossWatch
    {
        // --- 1: revision regression -----------------------------------------------------------------------------

        private static readonly Dictionary<ZDOID, (uint dataRev, ushort ownerRev)> s_lastKnownRevision =
            new Dictionary<ZDOID, (uint, ushort)>();
        private static readonly List<ZDO> s_scratch = new List<ZDO>();
        private static readonly HashSet<ZDOID> s_seenScratch = new HashSet<ZDOID>(); // reused across calls -- see fix note below

        // O10 EXTENSION (experimental, default off): combines ServerZoneMembership's (O6/OwnershipAuthority)
        // whole-zone holds with SharedAreaZoneHold's (O10) refcounted holds into one scratch set per scan --
        // reused across calls same as s_seenScratch above, since the per-zone scan body below is identical
        // either way.
        private static readonly HashSet<Vector2s> s_combinedHeldZones = new HashSet<Vector2s>();

        // LEAD FIX (runtime review): single-zone gather (matches the zone-sim module's own ObjectFeed pattern),
        // not a full near-ring scan -- this module only ever needs to watch objects INSIDE zones it itself
        // holds, not their neighbours (a neighbour's own regression is caught when ITS OWN zone is scanned, if
        // it's also held; if it isn't held, it's out of this watcher's scope entirely). The original version's
        // `new SimulationDistance(synced.NearSimulationDistance, 0, ...)` re-scanned each held zone's WHOLE
        // simulation-distance ring -- O(heldZones x ringSize), heavily re-visiting the same overlapping
        // neighbours from every held zone's own scan -- where O(heldZones) is all this watcher needs. A
        // `SimulationDistance` is a value type (no heap allocation), so caching this doesn't matter for GC, but
        // it's held `static readonly` anyway since it's always the same value.
        private static readonly SimulationDistance s_singleZoneOnly = new SimulationDistance(0, 0, false);

        internal static void ScanHeldZonesForRevisionRegression()
        {
            if (ZDOMan.instance == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;

            // O10 EXTENSION (experimental, default off, redesign proposals[1].changes "OwnershipLossWatch.cs"):
            // O10-held objects (SharedAreaZoneHold, a REFCOUNTED registry -- distinct from this O6/
            // OwnershipAuthority whole-zone membership) get the identical watchdog coverage O6-held zones
            // already have, combined into one set since the per-zone scan body is identical either way.
            s_combinedHeldZones.Clear();
            foreach (Vector2s z in ServerZoneMembership.Snapshot()) s_combinedHeldZones.Add(z);
            foreach (Vector2s z in SharedAreaZoneHold.Snapshot()) s_combinedHeldZones.Add(z);

            if (s_combinedHeldZones.Count == 0)
            {
                if (s_lastKnownRevision.Count > 0) s_lastKnownRevision.Clear();
                return;
            }

            long sessionId = ZDOMan.GetSessionID();

            // LEAD FIX (runtime review): reused across calls instead of `new HashSet<ZDOID>()` every tick --
            // this method only ever runs once per ActiveInterval now (see the Prefix/__state gate below), but a
            // reused set is still free and avoids a per-tick allocation regardless.
            s_seenScratch.Clear();
            foreach (Vector2s zone in s_combinedHeldZones)
            {
                s_scratch.Clear();
                ZDOMan.instance.FindSectorObjects(zone, s_singleZoneOnly, s_scratch);

                foreach (ZDO zdo in s_scratch)
                {
                    if (!zdo.Persistent) continue;
                    if (zdo.GetOwner() != sessionId) continue; // only watch what the server itself currently holds
                    s_seenScratch.Add(zdo.m_uid);

                    uint dataRev = zdo.DataRevision;
                    ushort ownerRev = zdo.OwnerRevision;

                    if (s_lastKnownRevision.TryGetValue(zdo.m_uid, out var last))
                    {
                        if (dataRev < last.dataRev)
                        {
                            LossSignals.Report("dataRevisionRegression",
                                $"{zdo.m_uid} DataRevision {last.dataRev} -> {dataRev} while server-held in zone {zone}");
                        }
                        if (IsSuspiciousOwnerRevisionRegression(last.ownerRev, ownerRev))
                        {
                            LossSignals.Report("ownerRevisionRegression",
                                $"{zdo.m_uid} OwnerRevision {last.ownerRev} -> {ownerRev} while server-held in zone {zone}");
                        }
                    }
                    s_lastKnownRevision[zdo.m_uid] = (dataRev, ownerRev);
                }
            }

            // Prune entries for ZDOs no longer observed as server-held (released, moved on, destroyed) so this
            // never grows unbounded across a long session.
            if (s_lastKnownRevision.Count > s_seenScratch.Count)
            {
                List<ZDOID> stale = null;
                foreach (ZDOID id in s_lastKnownRevision.Keys)
                {
                    if (!s_seenScratch.Contains(id)) (stale ??= new List<ZDOID>()).Add(id);
                }
                if (stale != null) foreach (ZDOID id in stale) s_lastKnownRevision.Remove(id);
            }
        }

        /// <summary><c>OwnerRevision</c> is a <c>ushort</c> (wraps at 65536) — a decrease from near the ceiling back toward zero is a plausible wraparound, not a real regression, and must not be flagged.</summary>
        private static bool IsSuspiciousOwnerRevisionRegression(ushort oldRev, ushort newRev)
        {
            if (newRev >= oldRev) return false;
            const ushort wrapTolerance = 4096;
            if (oldRev >= ushort.MaxValue - wrapTolerance) return false; // plausible wraparound
            return true;
        }

        internal static void ClearRevisionState() => s_lastKnownRevision.Clear();

        [HarmonyPatch(typeof(ZDOMan), "ReleaseZDOS")]
        internal static class Patch_ZDOMan_ReleaseZDOS_RevisionScan
        {
            // BLOCKER FIX (lead review): ReleaseZDOS runs every frame -- see ZoneClaimSweep.WillPerformReleaseTick's
            // own remarks (shared by this postfix too now). LEAD FIX (runtime review, fix #3): also gated on
            // OwnershipAuthority.Active now, not only the kill switch -- ServerZoneMembership.Snapshot() is
            // always empty when that's off anyway (this module is its only writer), so this is a cheap, correct
            // narrowing, not a behaviour change.
            static void Prefix(ZDOMan __instance, float dt, out bool __state)
            {
                // O10 EXTENSION (experimental, default off): O10 needs neither OwnershipAuthority nor O6 active
                // at all (full independence, matching O9's own design intent) -- this scan must therefore also
                // run whenever O10 alone is on, or an O10-held object would get no revision-regression watchdog
                // coverage at all. ServerZoneMembership.Snapshot()/SharedAreaZoneHold.Snapshot() are always both
                // empty when both flags are off anyway, so this is a cheap, correct narrowing either way.
                //
                // WP3 FIX (safety-net spec.json WP3 item 4 / auditB.md "three unguarded Prefixes on
                // ZDOMan.ReleaseZDOS" -- this is the one explicitly named there): this was the one confirmed-
                // unguarded Prefix in this file. An uncaught exception here would abort the whole ReleaseZDOS
                // call for that frame (Harmony Prefix semantics -- worse than vanilla, not a fallback to it),
                // and never reach NoodlesLog.PatchException, so AutoStandDown/FeatureStandDown would never see
                // it either. Fails toward "skip this tick's revision scan" (__state = false), never toward
                // crashing the outer vanilla call.
                try
                {
                    __state = KillSwitch.Enabled &&
                        (Features_Ownership.OwnershipAuthority.Active || O10SharedAreaOwnership.Active) &&
                        ZoneClaimSweep.WillPerformReleaseTick(__instance, dt);
                }
                catch (Exception e)
                {
                    __state = false;
                    NoodlesLog.PatchException(nameof(Patch_ZDOMan_ReleaseZDOS_RevisionScan) + ".Prefix", e);
                }
            }

            static void Postfix(ZDOMan __instance, bool __state)
            {
                if (!__state) return;
                try { ScanHeldZonesForRevisionRegression(); }
                catch (Exception e) { NoodlesLog.PatchException(nameof(Patch_ZDOMan_ReleaseZDOS_RevisionScan), e); }
            }
        }

        // --- 2: persistent ZDO destroyed while isolated (telemetry only -- see class remarks) ------------------

        private const float NearbyPeerRadiusMeters = 128f; // deliberately generous -- wider than the active-area check itself; err toward NOT reporting
        private const float WarnRateLimitSeconds = 30f; // this fires routinely in ordinary play (plant growth, kills, decay) -- a log line per occurrence would spam
        private static float s_lastWarnAt = float.NegativeInfinity;

        private static bool AnyPeerNear(Vector3 position)
        {
            if (ZNet.instance == null) return true; // fail toward "someone might be near" (no report) on an uncertain/shutting-down state
            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            if (peers == null) return true;
            foreach (ZNetPeer peer in peers)
            {
                if (peer == null) continue;
                // 1.0.11 (review): horizontal distance, and the live avatar as a second source, so a player in a
                // dungeon above a piece, or one whose reported position lags a teleport, still counts as near.
                if (Utils.DistanceXZ(peer.GetRefPos(), position) <= NearbyPeerRadiusMeters) return true;
                if (peer.m_characterID != ZDOID.None && ZDOMan.instance != null)
                {
                    ZDO avatar = ZDOMan.instance.GetZDO(peer.m_characterID);
                    if (avatar != null && Utils.DistanceXZ(avatar.GetPosition(), position) <= NearbyPeerRadiusMeters) return true;
                }
            }
            return false;
        }

        // 1.0.11 (live Proxima 2026-10-01: INIGO's new portal_wood was destroyed while server-held and was filed
        // under this watcher's "expected" line, one per 30 s, with no clue to what destroyed it). A PLAYER-BUILT
        // object (ZDOVars.s_creator != 0) destroyed while server-held with nobody near is never routine, so it
        // gets its own counter and an unthrottled warning (capped per session) naming the caller chain.
        // Telemetry only, never LossSignals.Report: a hammer removal right after a teleport (refPos not yet
        // updated) can look identical, and a false stand-down switches the whole mod off. Growing crops
        // (Plant.Grow replaces its sapling, which carries the planter as creator) are excluded.
        private const int PlayerBuiltWarnCap = 200; // per hour (review: a session-long cap could be used up by benign events)
        private static int s_playerBuiltWarned;
        private static float s_playerBuiltWindowStart = float.NegativeInfinity;
        private static readonly Counter s_playerBuiltDestroyed = Telemetry.GetCounter("ownership.playerBuiltDestroyWhileServerHeld");

        private static string CallerChain()
        {
            var st = new System.Diagnostics.StackTrace(2, false);
            var sb = new System.Text.StringBuilder();
            int n = 0;
            foreach (System.Diagnostics.StackFrame f in st.GetFrames() ?? new System.Diagnostics.StackFrame[0])
            {
                System.Reflection.MethodBase m = f.GetMethod();
                if (m == null) continue;
                string t = m.DeclaringType != null ? m.DeclaringType.Name : "dyn";
                if (t.StartsWith("Patch_ZDOMan_DestroyZDO_LossWatch", StringComparison.Ordinal)) continue;
                if (n > 0) sb.Append(" < ");
                sb.Append(t).Append('.').Append(m.Name);
                if (++n >= 14) break;
            }
            return sb.ToString();
        }

        private static float NearestPeerMeters(Vector3 position)
        {
            float best = float.PositiveInfinity;
            List<ZNetPeer> peers = ZNet.instance != null ? ZNet.instance.GetPeers() : null;
            if (peers == null) return best;
            foreach (ZNetPeer peer in peers)
            {
                if (peer == null) continue;
                float d = Utils.DistanceXZ(peer.GetRefPos(), position);
                if (d < best) best = d;
                if (peer.m_characterID != ZDOID.None && ZDOMan.instance != null)
                {
                    ZDO avatar = ZDOMan.instance.GetZDO(peer.m_characterID);
                    if (avatar != null) best = Mathf.Min(best, Utils.DistanceXZ(avatar.GetPosition(), position));
                }
            }
            return best;
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.DestroyZDO))]
        internal static class Patch_ZDOMan_DestroyZDO_LossWatch
        {
            // Prefix, not the Postfix below: the object's data (creator, prefab) is read while it still exists.
            // Observational only (void, never skips the original). Same scope as the Postfix: persistent,
            // server-owned, in a server-held zone, no peer within NearbyPeerRadiusMeters.
            static void Prefix(ZDO zdo)
            {
                if (!KillSwitch.Enabled || zdo == null) return;
                try
                {
                    if (!zdo.Persistent) return;
                    if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                    if (!zdo.IsOwner()) return;
                    long creator = zdo.GetLong(ZDOVars.s_creator, 0L);
                    if (creator == 0L) return;

                    Vector3 pos = zdo.GetPosition();
                    Vector2s zone = ZoneSystem.GetZone(pos);
                    if (!ServerZoneMembership.IsServerHeld(zone) && !SharedAreaZoneHold.IsHeld(zone)) return;
                    if (AnyPeerNear(pos)) return;

                    GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
                    if (prefab != null && prefab.GetComponent<Plant>() != null) return; // crop growth, see remarks above

                    s_playerBuiltDestroyed.Increment();
                    if (Time.time - s_playerBuiltWindowStart > 3600f) { s_playerBuiltWindowStart = Time.time; s_playerBuiltWarned = 0; }
                    if (s_playerBuiltWarned >= PlayerBuiltWarnCap) return;
                    s_playerBuiltWarned++;
                    string name = prefab != null ? prefab.name : $"hash {zdo.GetPrefab()}";
                    NoodlesLog.Warning($"PLAYER-BUILT DESTROY: '{name}' ({zdo.m_uid}, creator {creator}) was destroyed " +
                        $"while server-held and isolated (zone {zone}, position {pos}, nearest player " +
                        $"{NearestPeerMeters(pos):F0} m). Not expected; caller: {CallerChain()}");
                }
                catch (Exception e)
                {
                    NoodlesLog.WarningRateLimited("lossWatch.playerBuilt", $"OwnershipLossWatch player-built check threw: {e.GetType().Name}: {e.Message}", 3, 300f);
                }
            }

            static void Postfix(ZDO zdo)
            {
                if (!KillSwitch.Enabled) return;
                if (zdo == null) return;
                try
                {
                    if (!zdo.Persistent) return;
                    if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                    if (!zdo.IsOwner()) return; // only meaningful when THIS server's own session is the current owner

                    Vector2s zone = ZoneSystem.GetZone(zdo.GetPosition());
                    // O10 EXTENSION (experimental, default off): also scope to a zone holding an O10 object --
                    // never vanilla's own (inert, pinned) self-pass ownership either way.
                    if (!ServerZoneMembership.IsServerHeld(zone) && !SharedAreaZoneHold.IsHeld(zone)) return;

                    if (AnyPeerNear(zdo.GetPosition())) return; // a nearby player is a legitimate possible cause (chopping/combat/pickup) -- not investigated further

                    // LEAD FIX (data-loss + runtime reviews, both CONFIRMED against the decompiles): this is NOT
                    // a real loss signal. Every server-held zone is, by construction, >=~160m from any connected
                    // peer (G3-N1's own padding on top of ZoneClaimSweep's ~96m base active-area radius), so "no
                    // peer within 128m" is ALWAYS true for anything the server legitimately holds -- and
                    // server-owned simulation legitimately destroys persistent ZDOs there constantly with nobody
                    // watching: Plant.Grow (SERVER:136704), creature-vs-creature kills, fire/environmental
                    // damage, WearNTear collapse, raid despawns, ItemDrop timeouts. Reporting this via
                    // LossSignals.Report (the original version's behaviour) would trip AutoStandDown on ordinary
                    // gameplay the moment the server held any zone doing any of the above. Telemetry-only:
                    // signal 1 (revision regression, above) remains the real LossSignals.Report in this file --
                    // vanilla's own network merge structurally never lowers a revision through legitimate play,
                    // so it has no equivalent false-positive path.
                    Telemetry.GetCounter("ownership.persistentDestroyWhileIsolated").Increment();

                    float now = Time.time;
                    if (now - s_lastWarnAt >= WarnRateLimitSeconds)
                    {
                        s_lastWarnAt = now;
                        string prefabName = ZNetScene.instance != null
                            ? (ZNetScene.instance.GetPrefab(zdo.GetPrefab())?.name ?? $"hash {zdo.GetPrefab()}")
                            : $"hash {zdo.GetPrefab()}";
                        NoodlesLog.Warning(NoodlesLogLevel.Verbose, $"a persistent '{prefabName}' ({zdo.m_uid}) was destroyed while " +
                            $"server-held and isolated (zone {zone}, position {zdo.GetPosition()}) -- expected " +
                            "from ordinary server-side simulation (plant growth, creature kills, decay, " +
                            "despawns), not investigated further; see ownership.persistentDestroyWhileIsolated " +
                            "for the running count (rate-limited to one log line per " + WarnRateLimitSeconds + "s).");
                    }
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_ZDOMan_DestroyZDO_LossWatch), e);
                }
            }
        }

        // --- Init --------------------------------------------------------------------------------------------------

        [NoodlesModuleInit]
        internal static void Init()
        {
            // No feature flag of its own: pure observation, same reasoning as TelemetryHooks.cs (gates on
            // KillSwitch.Enabled directly) -- and both watchers are naturally inert whenever ServerZoneMembership
            // is empty, i.e. whenever OwnershipAuthority.Active is off, with no separate check needed.
            PatchRegistry.Register("OwnershipLossWatch.RevisionScan", typeof(Patch_ZDOMan_ReleaseZDOS_RevisionScan), PatchSide.Both);
            PatchRegistry.Register("OwnershipLossWatch.DestroyWatch", typeof(Patch_ZDOMan_DestroyZDO_LossWatch), PatchSide.Both);

            KillSwitch.RegisterFlushCallback("Ownership.LossWatch", ClearRevisionState);
        }
    }
}
