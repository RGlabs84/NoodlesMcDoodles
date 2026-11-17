using System;
using System.Collections.Generic;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.ZoneSim
{
    /// <summary>
    /// Hook 1 (Z2) — docs/design/04: a per-peer real-terrain poke ring, generalizing
    /// <c>ZoneSystem.PokeLocalZone(Vector2s)</c> (private, <c>SERVER:113896-113913</c>/<c>CLIENT:114707-114724</c>,
    /// confirmed byte-identical both builds) from vanilla's one dead reference position to one ring per
    /// connected, position-ready peer.
    ///
    /// A plain <see cref="MonoBehaviour"/> on its own <see cref="GameObject"/> (started from
    /// <see cref="EnsureStarted"/>, called directly from <c>ZoneSimConfig.Init()</c> — not a Harmony patch,
    /// matching how Core's own <c>NoodlesHeartbeat</c> is added in <c>NoodlesPlugin.Awake</c>), server-only:
    /// <c>SpawnMode.Full</c> (the branch that can first-generate real terrain) requires
    /// <c>ZNet.instance.IsServer()</c> inside <c>SpawnZone</c> (<c>SERVER:113946-113955</c>) — running this on a
    /// client would silently downgrade to <c>SpawnMode.Client</c> and add nothing a vanilla client doesn't
    /// already have.
    /// </summary>
    internal sealed class PokeRingDriver : MonoBehaviour
    {
        private static bool s_started;

        internal static void EnsureStarted()
        {
            if (s_started) return; // idempotent — Init() only ever calls this once, but cheap to guard anyway
            // RUNTIME REVIEW FIX (major, mixed-and-host, 2026-09-28): used to bail on ANY client-build process
            // (Side.IsDedicatedServerProcess), which also silently excluded a listen-server host — but Tick()
            // below already has its own correct runtime-authority check (ZNet.instance.IsServer()), so a plain
            // remote client starting this driver is harmless (it just never does anything): no need to gate
            // component creation itself by build type at all.
            s_started = true;

            var go = new GameObject("NoodlesMcDoodles.ZoneSim.PokeRingDriver");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<PokeRingDriver>();
        }

        // RED-TEAM FIX (RT1 minor #4): FixedUpdate as a bare MonoBehaviour message runs at Unity's physics tick
        // rate, not at Config.UpdateTickSeconds — no code-level override of Time.fixedDeltaTime was found in
        // either decompile (Open question 7). Gate explicitly with our own accumulator rather than trusting the
        // message rate to match.
        private float m_updateTimer;

        private readonly HashSet<Vector2s> m_pokedThisTick = new HashSet<Vector2s>();

        // RUNTIME REVIEW FIX (major, perf, 2026-09-28): ActiveZones() (called from ObjectFeed's Prefix, on
        // ZNetScene's own create/destroy cadence — 30Hz default, up to 60Hz — independent of and generally
        // faster than this driver's own UpdateTickSeconds cadence, 0.05s default) used to call RingZones() fresh
        // for every peer on every single call: a new List<Vector2s> allocation plus the full (2*ring+1)^2 grid
        // scan plus an O(n log n) sort, every ~16-33ms, duplicating RunTick's own just-computed result for the
        // same peer/ring — and HeldBandRingBonus (this same redesign pass) widens that ring, so this cost only
        // grows. RingZones(center, ring) is a pure function of its two inputs (ZonesWithinRadius has no
        // ZoneSystem-instance-mutating side effect — confirmed against its own decompiled body), so memoizing by
        // (center, ring) is byte-identical to a fresh recompute, never a staleness trade-off: whenever a peer's
        // center or the effective ring actually changes, that is a different key and gets computed fresh, same
        // as before. Cleared at the top of every RunTick (below) so it never grows past roughly one entry per
        // distinct (center, ring) pair seen in a single ~UpdateTickSeconds window — the same small, connected-
        // peer-bounded order of magnitude as m_pokedThisTick just above, not an unbounded cache.
        // Plain struct key, not a language tuple: no other file in this project uses ValueTuple and this net472
        // target has no other dependency pulling that assembly in, so a hand-rolled IEquatable key avoids
        // introducing one for a single small cache.
        private readonly struct RingCacheKey : IEquatable<RingCacheKey>
        {
            public readonly Vector2s Center;
            public readonly int Ring;
            public RingCacheKey(Vector2s center, int ring) { Center = center; Ring = ring; }
            public bool Equals(RingCacheKey other) => Ring == other.Ring && Center.Equals(other.Center);
            public override bool Equals(object obj) => obj is RingCacheKey other && Equals(other);
            public override int GetHashCode() => (Center.GetHashCode() * 397) ^ Ring;
        }

        private static readonly Dictionary<RingCacheKey, List<Vector2s>> s_ringZoneCache =
            new Dictionary<RingCacheKey, List<Vector2s>>();

        private static List<Vector2s> RingZonesCached(Vector2s center, int ring)
        {
            var key = new RingCacheKey(center, ring);
            if (!s_ringZoneCache.TryGetValue(key, out List<Vector2s> zones))
            {
                zones = RingZones(center, ring);
                s_ringZoneCache[key] = zones;
            }
            return zones;
        }

        private void FixedUpdate()
        {
            try { Tick(); }
            catch (Exception e) { NoodlesLog.PatchException(nameof(PokeRingDriver), e); }
        }

        private void Tick()
        {
            if (!ZoneSimConfig.ServerZoneSimFlag.Active) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (ZoneSystem.instance == null || !ZoneSystemAccess.IsReady) return;

            m_updateTimer += Time.fixedDeltaTime;
            if (m_updateTimer < ZoneSimConfig.UpdateTickSeconds.Value) return;
            m_updateTimer = 0f;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            RunTick();
            Telemetry_ZoneSim.SetPokeRingTickMs(stopwatch.Elapsed.TotalMilliseconds);
        }

        // 1.0.9 (frame-stall fix, SP/harness108 1.0.7 evidence: MonoUpdaters.LateUpdate -- Heightmap's own
        // deferred terrain mesh/collider rebuild for newly built zones -- was 15.6-21.8% of slow-frame time
        // during the walk/explore phases) -- cumulative wall-clock time spent on NEW zone constructions (never
        // the free re-poke branch below) this tick, reset at the top of every RunTick alongside
        // m_pokedThisTick/s_ringZoneCache. This method IS the only caller of a NEW PokeLocalZone(Full) build, so
        // no transpile is needed for this half of the fix, unlike Hook 6's own object-creation budget which has
        // to intercept vanilla's own compiled loop (CreateObjectsBudget.cs) -- this is entirely this project's
        // own code, budgeted directly.
        private double m_zoneBuildMsThisTick;

        // 1.0.9 ROUND 2 (measured on the first harness108 1.0.9 run, SP/harness108/server-console.fix-1.0.9-
        // frame-stall.log): MaxZoneBuildsPerTick=1 + ZoneBuildBudgetMs=6ms alone barely moved MonoUpdaters.
        // LateUpdate's slow-frame contribution (1053ms/336ms slow vs 1.0.7's 958ms/315ms -- essentially
        // unchanged) -- zonesim.zoneBuildsDeferredTotal climbed by ~2700 over the run, meaning a build was
        // wanted almost every tick, so MaxZoneBuildsPerTick was already isolating builds to one-per-tick in
        // practice; the remaining ~16-26ms-per-slow-frame cost is the INHERENT cost of building ONE Ashlands
        // zone's Heightmap+MeshCollider, which no count/time cap on ADDITIONAL builds can shrink (see
        // CreateObjectsBudget.cs's own EffectiveCreateCap remarks for the identical "floor-exempt item can be
        // arbitrarily expensive" limitation on the create-loop side). Task brief's own third option ("skip
        // building when the previous frame was slow"): computed once per tick, reusing Core/FrameTiming.cs's
        // existing ~20-frame EMA rather than a second smoothing mechanism.
        //
        // ROUND 3 (SAFETY FIX, found on the round-2 run itself): round 2's first cut applied this to the tick's
        // FIRST build too, which produced "HARNESS108 RESULT ARMED_WAIT FAIL no held zones appeared within
        // 300s" -- ownership.serverHeldZones stuck at 0.0 for the entire run. Root cause: on a busy ~50-mod
        // stack, OTHER mods' own background hitches alone (visible in this same run's log before any ZoneSim
        // activity) can keep RecentMeanMs above RecentFrameHotMs for extended, possibly unbounded stretches; with
        // no floor exempting a tick's first build, EVERY zone stayed deferred forever, and a zone that never
        // builds can never become real or be held -- silently worse than the frame stall this fix exists to
        // remove. RunTick's own build-cap check now gates recentFramesHot the same way it already gated the
        // count/time caps: only a SECOND-OR-LATER build competing for the same tick is deferred by it; a lone
        // newly-entered zone always gets its one build attempt this tick, exactly as before round 2. Backlog is
        // never abandoned either way (RunTick re-scans every tick; a deferred zone is retried, never dropped),
        // and gating only the second-or-later build is strictly a spreading effect on an ALREADY multi-tick-deep
        // queue (per the round-2 measurement above), not a new source of delay against the ~23-32s geometric
        // activation-lead margin (ring radius / player speed) this hook still has to work with.
        private const double RecentFrameHotMs = 20.0;

        private void RunTick()
        {
            m_pokedThisTick.Clear();
            s_ringZoneCache.Clear(); // start a fresh memoization window for this tick — see field remarks above
            int zoneBuildsThisTick = 0;
            int zoneBuildsDeferredThisTick = 0;
            m_zoneBuildMsThisTick = 0.0;
            bool recentFramesHot = NoodlesMcDoodles.Core.FrameTiming.RecentMeanMs > RecentFrameHotMs;
            int ringRadius = EffectiveRingRadius();

            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (!PeerReadyState.HasFirstRealPosition(peer)) continue;

                Vector2s center = ZoneSystem.GetZone(peer.GetRefPos());
                foreach (Vector2s z in RingZonesCached(center, ringRadius))
                {
                    if (!m_pokedThisTick.Add(z)) continue; // de-dup across overlapping peer rings, this tick

                    // Never first-gen implicitly — only vanilla's own capped ghost-zone path (Hook 3) may ever
                    // trigger first-time world generation. A zone PokeLocalZone would otherwise SpawnMode.Full.
                    if (!ZoneSystemAccess.IsZoneGenerated(ZoneSystem.instance, z)) continue;

                    if (ZoneSystem.instance.IsZoneLoaded(z))
                    {
                        // REFINED FIX (held-band redesign synthesis, 2026-09-28 — tighter than the original
                        // "fix-the-band" proposal, closing a budget-competition gap neither writeup addressed):
                        // an already-loaded zone's poke is a cheap TryGetValue + m_ttl=0 field write internally
                        // (confirmed via PokeLocalZone's decompiled body, SERVER:113896-113900) — a free TTL
                        // refresh, not a real build — so it must never compete with MaxZoneBuildsPerTick's real
                        // per-tick construction budget. Without this split, HeldBandRingBonus's wider ring could
                        // let free re-pokes of already-loaded zones crowd out genuinely NEW builds under a cold-
                        // start/teleport catch-up load. Still called (not skipped, as before this fix), so the
                        // zone's TTL actually gets refreshed every tick, closing the pre-existing "content-free
                        // zone never gets re-poked, cycles build/TTL-expire/rebuild" gap that scales with the
                        // wider ring.
                        ZoneSystemAccess.PokeLocalZone(ZoneSystem.instance, z);
                        continue;
                    }

                    // 1.0.9 ROUND 3 (SAFETY FIX, found live on this same run): count cap OR the per-tick TIME
                    // cap OR recent-frame-health -- but ALL THREE only ever gate a SECOND-OR-LATER build
                    // competing for the SAME tick, never the tick's first. The first cut of this gate (round 2)
                    // let recentFramesHot alone defer even a lone first build -- on this exact run that produced
                    // "HARNESS108 RESULT ARMED_WAIT FAIL no held zones appeared within 300s" and
                    // ownership.serverHeldZones stuck at 0.0 the whole time: with a busy ~50-mod stack, OTHER
                    // mods' own background hitches (AzuAntiCheat/Wonderland's own slow-frame logging was visible
                    // in this same boot window, well before any ZoneSim work) can keep Core/FrameTiming.
                    // RecentMeanMs above RecentFrameHotMs indefinitely, and with no floor exempting the first
                    // build, EVERY zone stayed deferred forever -- a zone that never builds can never become
                    // real, so it can never be held, which is a materially WORSE outcome than the frame stall
                    // this whole fix exists to remove (data that should be simulated silently never is). Moved
                    // recentFramesHot inside the SAME zoneBuildsThisTick > 0 guard the time-budget already uses:
                    // a lone newly-entered zone always gets its one build attempt this tick regardless of recent
                    // frame health (restores the pre-round-2 guarantee), while a SECOND build competing for the
                    // same tick is still deferred if recent frames are already hot, on top of the existing count/
                    // time caps -- a strictly additive safety margin over round 1, never a path to zero progress.
                    if (zoneBuildsThisTick >= ZoneSimConfig.MaxZoneBuildsPerTick.Value ||
                        (zoneBuildsThisTick > 0 &&
                            (m_zoneBuildMsThisTick >= ZoneSimConfig.ZoneBuildBudgetMs.Value || recentFramesHot)))
                    {
                        zoneBuildsDeferredThisTick++;
                        continue; // retried automatically next tick, this loop re-scans fully every tick.
                    }

                    var buildStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    ZoneSystemAccess.PokeLocalZone(ZoneSystem.instance, z);
                    buildStopwatch.Stop();
                    m_zoneBuildMsThisTick += buildStopwatch.Elapsed.TotalMilliseconds;
                    zoneBuildsThisTick++;
                    Telemetry_ZoneSim.ZonesPokedTotal.Increment();
                    Telemetry_ZoneSim.SetLastZoneBuildMs(buildStopwatch.Elapsed.TotalMilliseconds);
                }
            }

            Telemetry_ZoneSim.SetZoneBuildsThisTick(zoneBuildsThisTick);
            Telemetry_ZoneSim.SetZoneBuildsDeferredThisTick(zoneBuildsDeferredThisTick);
            if (zoneBuildsDeferredThisTick > 0) Telemetry_ZoneSim.ZoneBuildsDeferredTotal.Add(zoneBuildsDeferredThisTick);
            Telemetry_ZoneSim.SetZonesRealActive(ZoneSystemAccess.ZoneCount(ZoneSystem.instance));
            Telemetry_ZoneSim.SetHeldBandRingBonus(Mathf.Clamp(ZoneSimConfig.HeldBandRingBonus.Value, 0, 2));
        }

        /// <summary>
        /// The ONE ring value this driver's terrain-build loop (<see cref="RunTick"/>) AND its object-feed
        /// candidate source (<see cref="ActiveZones"/>) AND Ownership's own O6 claim-eligibility test
        /// (<c>ZoneClaimSweep.ComputeCandidateZones</c>, via <c>ZoneSimGate.CanHold</c> -&gt;
        /// <c>ZoneCap.WasIncludedThisTick</c>) all read — never a second, independently-tuned radius for any of
        /// them. This is the load-bearing safety property behind <c>ZoneSim.HeldBandRingBonus</c> (held-band
        /// redesign synthesis, 2026-09-28): a zone can never be O6-claimable without having been fed real
        /// objects this exact tick, by construction, because both callers always agree on exactly this value.
        /// A future change to this system must keep that property — do not let claim-eligibility and the
        /// build/feed ring drift apart again.
        /// </summary>
        private static int EffectiveRingRadius()
        {
            int configured = ZoneSimConfig.PokeRingRadius.Value;
            int negotiatedCeiling = ZNet.instance.GetSyncedSimulationDistance().NearSimulationDistance;
            int heldBandBonus = Mathf.Clamp(ZoneSimConfig.HeldBandRingBonus.Value, 0, 2);
            return Mathf.Min(configured, negotiatedCeiling) + heldBandBonus;
        }

        /// <summary>
        /// Every zone within <paramref name="ring"/> of <paramref name="center"/>, using vanilla's own
        /// <c>ZonesWithinRadius</c> circular test (public, <c>SERVER:113889-113894</c>) — the same shape vanilla
        /// itself uses for <c>IsActiveAreaLoaded</c>/<c>FindSectorObjects</c>, so this module's rings cover
        /// exactly the same zone SET a peer's own negotiated simulation distance would, not a square
        /// approximation of it. Sorted nearest-first (held-band redesign synthesis, 2026-09-28 — was raster
        /// order, a pure accident of the dx/dy loop, which biased both <see cref="RunTick"/>'s build budget and
        /// <c>ZoneCap</c>'s hysteresis toward whichever edge the loop scanned first): under budget pressure, the
        /// nearer zones to the peer now win first, in both callers.
        /// </summary>
        private static List<Vector2s> RingZones(Vector2s center, int ring)
        {
            ZoneSystem zs = ZoneSystem.instance;
            var candidates = new List<Vector2s>();
            for (int dy = -ring; dy <= ring; dy++)
            {
                for (int dx = -ring; dx <= ring; dx++)
                {
                    var candidate = new Vector2s(center.x + dx, center.y + dy);
                    if (zs.ZonesWithinRadius(center, candidate, ring)) candidates.Add(candidate);
                }
            }
            candidates.Sort((a, b) => SquaredDistance(center, a).CompareTo(SquaredDistance(center, b)));
            return candidates;
        }

        private static int SquaredDistance(Vector2s a, Vector2s b)
        {
            int dx = a.x - b.x;
            int dy = a.y - b.y;
            return dx * dx + dy * dy;
        }

        /// <summary>
        /// Zones currently within some position-ready peer's ring AND already real (<c>m_zones.ContainsKey</c>
        /// — never the stricter public <c>IsZoneLoaded</c>, see <see cref="ZoneSystemAccess"/> remarks). Consumed
        /// by Hook 2 (<see cref="Patch_ZNetScene_CreateObjects_ObjectFeed"/>) as this tick's object-feed candidate
        /// set. The per-peer ring (<see cref="RingZonesCached"/>) is memoized by (center, ring) within the
        /// current <see cref="RunTick"/> window rather than recomputed from scratch on every call (RUNTIME REVIEW
        /// FIX, major, perf, 2026-09-28 — see the cache field's own remarks): Hook 2 runs on <c>ZNetScene</c>'s
        /// own 1/30s cadence, independent of and generally faster than this driver's configurable tick, so
        /// without memoizing, this call redid RunTick's own just-finished per-peer grid scan+sort every ~16-33ms.
        /// Never stale: a cache hit is only ever returned for the EXACT (center, ring) pair already computed
        /// this window, which is deterministic in those inputs, so this is always byte-identical to a fresh
        /// RingZones(center, ring) call — never a coarser/older answer.
        /// </summary>
        internal static IEnumerable<Vector2s> ActiveZones()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZoneSystem.instance == null || !ZoneSystemAccess.IsReady)
                yield break;

            var seen = new HashSet<Vector2s>();
            int ringRadius = EffectiveRingRadius();

            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (!PeerReadyState.HasFirstRealPosition(peer)) continue;
                Vector2s center = ZoneSystem.GetZone(peer.GetRefPos());
                foreach (Vector2s z in RingZonesCached(center, ringRadius))
                {
                    if (!seen.Add(z)) continue;
                    if (!ZoneSystemAccess.ContainsZone(ZoneSystem.instance, z)) continue;
                    yield return z;
                }
            }
        }
    }

    /// <summary>
    /// Z2 catalog item (7): a peer's <c>ZNetPeer.m_refPos</c> defaults to <c>Vector3.zero</c>
    /// (<c>SERVER:78837</c>/<c>CLIENT:78979</c>, public field) and is written ONLY by
    /// <c>ZNet.RPC_ServerSyncedPlayerData</c> (<c>SERVER:80441-80454</c>/<c>CLIENT:80634-...</c> —
    /// <c>peer.m_refPos = data.ReadVector3();</c>, confirmed the sole writer in either build this session, and
    /// independently by research/04's "Additions" section). The client only sends this RPC, on its own ~2s
    /// periodic timer (<c>SendServerSyncPlayerData</c>, <c>CLIENT:80578-80614</c>), once it has a real character
    /// position to report — so a peer that has connected but not yet sent one has a <c>GetRefPos()</c> of
    /// exactly the sentinel zero vector, never a legitimate in-world position (Valheim's world origin is void
    /// ocean, not a spawn point — no real player position is ever exactly <c>(0,0,0)</c>).
    ///
    /// A plain comparison against that sentinel, not a separate patch/flag: exactly as correct as hooking the
    /// RPC to set a bool, since the field already carries the "never set" state unambiguously, at a fraction of
    /// the surface area (no extra patch, no extra per-peer state to leak on disconnect).
    /// </summary>
    internal static class PeerReadyState
    {
        public static bool HasFirstRealPosition(ZNetPeer peer) => peer != null && peer.GetRefPos() != Vector3.zero;
    }
}
