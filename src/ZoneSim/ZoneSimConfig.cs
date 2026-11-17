using System;
using BepInEx.Configuration;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.ZoneSim
{
    /// <summary>
    /// Server zone simulation (docs/design/04-server-zone-simulation.md): config binding, the module's one
    /// master flag (<c>Features.ServerZoneSim</c>, covering every Z-lever in this doc per its own Hooks
    /// preamble), patch registration, and the <see cref="ZoneSimGate"/> seam Ownership (docs/design/03) polls
    /// before holding a zone.
    ///
    /// DEFAULTS POLICY (coordinator instruction, 2026-09-28: "a great improvement over vanilla but not
    /// overkill"): every numeric default below targets an ordinary home-hosted 2-10 player server, picked as
    /// the smallest value that captures most of the gain rather than the largest value still believed safe.
    /// Where DESIGN.md's Configuration reference already gives a default, it is kept as-is UNLESS noted
    /// otherwise (search below for "DEVIATION") — every keep or change states the vanilla comparison point.
    /// The full reasoning, plus a consolidated Defaults table, lives in docs/impl/ZONESIM-IMPL.md.
    /// </summary>
    internal static class ZoneSimConfig
    {
        // --- Config entries -----------------------------------------------------------------------------------
        public static ConfigEntry<int> PokeRingRadius;
        public static ConfigEntry<int> MaxZoneBuildsPerTick;
        public static ConfigEntry<float> ZoneTTLSeconds;
        public static ConfigEntry<float> UpdateTickSeconds;
        public static ConfigEntry<int> MaxGhostSpawnsPerTick;
        public static ConfigEntry<int> MaxCreatedPerFrame;
        public static ConfigEntry<int> CreateDestroyHz;
        public static ConfigEntry<float> CreateBudgetMs;
        public static ConfigEntry<int> MinCreatedPerTick;
        public static ConfigEntry<float> ZoneBuildBudgetMs;
        public static ConfigEntry<int> ObjectUnfeedGraceTicks;
        public static ConfigEntry<int> MaxObjectsPerPeer;
        public static ConfigEntry<int> MaxObjectsGlobal;
        public static ConfigEntry<float> CapIncludeThresholdPct;
        public static ConfigEntry<float> CapExcludeThresholdPct;
        public static ConfigEntry<int> HeldBandRingBonus;

        public static FeatureFlag ServerZoneSimFlag { get; private set; }

        // Vanilla's own literals, restated here once so every "off -> vanilla" fallback and every ConfigDescription
        // below cites the SAME constant rather than a hand-copied duplicate that could drift out of sync.
        internal const float VanillaZoneUpdateTickSeconds = 0.1f;   // SERVER:113800/CLIENT:114611 gate; also UpdateTTL's own SERVER:113806/CLIENT:114617 dt argument
        internal const float VanillaZoneTTLSeconds = 4f;            // SERVER:113087/CLIENT:113898 m_zoneTTL field initializer
        internal const int VanillaMaxCreatedPerFrame = 100;         // SERVER:82142/CLIENT:82372 — the branch a dedicated server ALREADY always takes (InLoadingScreen() permanently true server-side)
        internal const float VanillaCreateDestroyIntervalSeconds = 1f / 30f; // SERVER:82305/CLIENT:82535, ZNetScene.Update's own m_createDestroyTimer gate
        internal const int VanillaGhostSpawnsPerTick = 1;           // vanilla's own single-build-per-reference-position-per-tick discipline (no cap needed with 1 anchor)

        [NoodlesModuleInit]
        internal static void Init()
        {
            ConfigFile cfg = NoodlesConfig.File;

            var activeCfg = cfg.Bind("Features", "ServerZoneSim.Active", true,
                "Master switch for server zone simulation (docs/design/04): gives the server real terrain and " +
                "objects around every connected peer, instead of only its one permanently-void-ocean reference " +
                "position. No vanilla equivalent (vanilla simulates nothing away from that dead point). Does " +
                "NOT move ownership or change what any peer receives by itself (docs/design/04, 'What this " +
                "fixes ... and what it does not') — it only gives the Ownership module something real to hold. " +
                "DEFAULT ON (user decision, ownership redesign, 2026-09-28: 'Fix and do all, default to on, " +
                "Proxima is our real testing ground') — turned on TOGETHER with [3 - Server World] ServerRunsWorld " +
                "(also default on now) and ZoneSim.HeldBandRingBonus=1, the combination this flag alone used to " +
                "warn was missing (see the cross-flag warning below): on its own, with OwnershipAuthority off, " +
                "this still only makes the server build every player's surroundings without taking any of them " +
                "over, which costs server CPU and memory for no gain — that misconfiguration now gets its own " +
                "loud warning instead of shipping silently. RUNTIME AUTHORITY: every actual patch body this " +
                "flag gates also requires ZoneSimConfig.IsAuthoritativeProcess() (ZNet.instance.IsServer()), " +
                "never this flag alone — a listen host IS the server and must get this; a plain remote client, " +
                "whose own copy of this config is also true now, must not (docs/impl/CORE-API.md 'Runtime " +
                "authority, not the flag alone').");
            ServerZoneSimFlag = Features.Register("ServerZoneSim", activeCfg);
            activeCfg.SettingChanged += (_, __) => CrossFlagWarnings.CheckServerZoneSimWithoutOwnershipAuthority();

            PokeRingRadius = cfg.Bind("ZoneSim", "PokeRingRadius", 2,
                new ConfigDescription(
                    "Z2 — how many zones out from each peer's own position the server keeps real (Heightmap+" +
                    "MeshCollider), clamped at read time to the negotiated NearSimulationDistance. Vanilla value: " +
                    "2 (ZNet's own dead reference position already uses SimulationDistance.OriginalDistance, " +
                    "Near=2 — a 5x5 block) — this default gives every peer the SAME ring size vanilla's one " +
                    "unreachable point already gets, not a multiple of it. Safe max: 5 (matches vanilla's top " +
                    "simulation-distance slider level) — raising it scales zone count, and therefore cost, " +
                    "roughly as (2n+1)^2, so treat values above 2 as deliberate opt-in headroom for a server " +
                    "that has confirmed spare CPU/RAM.",
                    new AcceptableValueRange<int>(1, 5)));

            // HeldBandRingBonus (ownership redesign synthesis "fix-the-band", 2026-09-28 — full spec at
            // scratchpad redesign/redesign-result.json, resolution recorded in docs/impl/INTEGRATION-REVIEW.md's
            // "server-held zones are inert at default geometry" OPEN entry): the ONE ring value PokeRingDriver
            // already builds/feeds is also the ONE value O6 (ZoneClaimSweep) tests for claim-eligibility — see
            // PokeRingDriver.EffectiveRingRadius's own remarks for why a zone can never be claimable without
            // having been fed real objects this exact tick, by construction. At 0 this reproduces the ring O6
            // has always seen (byte-for-byte the same claim-eligibility set as before this config key existed);
            // at 1 (the shipped default, user decision 2026-09-28: "Fix and do all, default to on, Proxima is
            // our real testing ground") the ring widens to reach a real, already-simulated held band roughly
            // 160-224m out from each peer at default PokeRingRadius/PeerRefPosPadding — territory the server
            // previously only "held" in name (docs/design/04's Z2 Levers table used to say a wider ring "buys
            // nothing"; corrected there — that was only ever true for a PEER-anchored call, not O6's own
            // zone-centre-anchored one). 2 is opt-in headroom (<288m). Pair a nonzero value with
            // MaxObjectsPerPeer/MaxObjectsGlobal (see those keys' own descriptions) — this module's own
            // ZoneCap hysteresis fails safe (stops appending, never destroys) if the wider ring's real object
            // count outgrows the configured budget, but under-delivers the benefit silently if so.
            HeldBandRingBonus = cfg.Bind("ZoneSim", "HeldBandRingBonus", 1,
                new ConfigDescription(
                    "Widens the SAME ring value Hook 1's terrain build AND Hook 2's object feed AND O6's claim " +
                    "test all read (PokeRingDriver.EffectiveRingRadius = min(PokeRingRadius, negotiated near) + " +
                    "this value) — never a second, independently-tuned radius; that single-ring property is what " +
                    "makes a wider held band safe (no zone can ever be claimed without having been fed). No " +
                    "vanilla equivalent. 0 = today's exact ring/cost/behaviour, byte-for-byte. 1 (default, user " +
                    "decision 2026-09-28) reaches the real held band (~160-224m at default PokeRingRadius=2/" +
                    "PeerRefPosPadding=64) that O6 could never actually hold before this key existed — only " +
                    "matters once [3 - Server World] ServerRunsWorld is also on (also default on now). 2 is opt-in " +
                    "headroom (~288m) for a server with confirmed spare CPU/RAM. Pair a nonzero value with " +
                    "MaxObjectsPerPeer/MaxObjectsGlobal (see those keys) — ZoneCap's own hysteresis fails safe " +
                    "but silently under-delivers the benefit if the budget wasn't sized for the wider ring.",
                    new AcceptableValueRange<int>(0, 2)));

            // 1.0.9 (frame-stall fix, SP/harness108 1.0.7 evidence): MonoUpdaters.LateUpdate (Heightmap's own
            // deferred terrain mesh/collider rebuild for newly built zones, Heightmap.CustomLateUpdate) was
            // 15.6-21.8% of slow-frame time during the walk/explore phases -- consistent with several zone
            // builds queued in the SAME UpdateTickSeconds tick landing their deferred Heightmap.Regenerate()
            // in the same LateUpdate and stacking there. Lowered default 4 -> 1 (knee of the curve, not the
            // largest still-safe value) directly stops that stacking at the count level; ZoneBuildBudgetMs
            // below stops it at the time level too, for whenever an admin raises this back up.
            MaxZoneBuildsPerTick = cfg.Bind("ZoneSim", "MaxZoneBuildsPerTick", 1,
                new ConfigDescription(
                    "New cap (Hook 1's own per-tick cost bound, RED-TEAM FIX) on real zone CONSTRUCTIONS " +
                    "(Instantiate+Heightmap+MeshCollider) per UpdateTickSeconds tick, across every peer combined. " +
                    "No vanilla equivalent (vanilla never builds more than its one static zone, once). DEFAULT " +
                    "LOWERED 4 -> 1 (1.0.9 frame-stall fix, knee-of-curve): measured evidence (SP/harness108) " +
                    "attributed 15.6-21.8% of slow-frame time to Heightmap.CustomLateUpdate's deferred terrain " +
                    "rebuild for newly built zones, consistent with several builds from one tick stacking their " +
                    "deferred rebuild into the same LateUpdate; 1 per tick at UpdateTickSeconds=0.05s (20Hz) " +
                    "still activates a held zone well ahead of a player walking at run speed (~7 m/s) -- see " +
                    "ZoneBuildBudgetMs's own description for the measured activation-lead-time margin. Safe max: " +
                    "16 — raise only if telemetry (zonesim.zoneBuildsThisTick / zonesim.zoneBuildsDeferredThisTick " +
                    "pinned at the cap with peers still waiting on terrain) shows real, sustained demand, and " +
                    "raise ZoneBuildBudgetMs alongside it.",
                    new AcceptableValueRange<int>(1, 16)));

            ZoneBuildBudgetMs = cfg.Bind("ZoneSim", "ZoneBuildBudgetMs", 6f,
                new ConfigDescription(
                    "1.0.9 (frame-stall fix) — Hook 1's own per-tick WALL-CLOCK budget on a SECOND-OR-LATER NEW " +
                    "zone construction competing for the same tick (PokeRingDriver's own PokeLocalZone(Full) " +
                    "calls; never the free TTL-refresh poke of an already-loaded zone, which stays O(1) and " +
                    "uncapped), on top of MaxZoneBuildsPerTick's existing count cap. No vanilla equivalent. " +
                    "ROUND 2 finding (SP/harness108, first 1.0.9 run): this alone barely moved MonoUpdaters." +
                    "LateUpdate's slow-frame share (1053ms/336ms slow vs 1.0.7's 958ms/315ms) because " +
                    "zonesim.zoneBuildsDeferredTotal showed a build wanted almost every tick already -- " +
                    "MaxZoneBuildsPerTick=1 was already isolating builds to one/tick in practice, so this budget " +
                    "was rarely the thing doing the limiting; the remaining cost is ONE Ashlands zone's own " +
                    "Heightmap+MeshCollider build, ~16-26ms, which no cap on ADDITIONAL builds can shrink. " +
                    "PokeRingDriver.cs's own RunTick remarks cover the companion fix (a recent-frame-health " +
                    "check, reusing Core/FrameTiming.RecentMeanMs, that ALSO defers a second-or-later build once " +
                    "recent frames are already running hot -- ROUND 3 safety fix: round 2's first cut applied " +
                    "that check to the tick's FIRST build too and produced a real ARMED_WAIT FAIL (zero held " +
                    "zones for 300s) on a busy mod stack whose own background hitches alone could keep recent " +
                    "frames looking hot indefinitely; a lone newly-entered zone now always gets its one build " +
                    "attempt this tick regardless, exactly as before round 2 -- this key's own 6ms value still " +
                    "governs the second-or-later-build case unchanged). Safe max 20ms — raise only alongside " +
                    "MaxZoneBuildsPerTick.",
                    new AcceptableValueRange<float>(2f, 20f)));

            ZoneTTLSeconds = cfg.Bind("ZoneSim", "ZoneTTLSeconds", 15f,
                new ConfigDescription(
                    "Z4 — how long an empty, unpoked zone's terrain root survives before being torn down. " +
                    "Vanilla value: 4s. This default is the user's own explicit numeric target (DESIGN.md Lead " +
                    "decisions/Configuration reference), kept as-is: it buys slack for Hook 1's ring driver to " +
                    "miss a tick without a full terrain re-bake, and only ever protects EMPTY zones (vanilla's " +
                    "own HaveInstanceInSector gate — a zone with any live instance is never touched regardless " +
                    "of TTL). ~3.75x vanilla, not an unbounded increase. Hard-clamped 4-20s.",
                    new AcceptableValueRange<float>(4f, 20f)));

            UpdateTickSeconds = cfg.Bind("ZoneSim", "UpdateTickSeconds", 0.05f,
                new ConfigDescription(
                    "Z5 — ZoneSystem's own update cadence (terrain/ghost-zone tick), and Hook 1's poke-ring " +
                    "driver's cadence. Vanilla value: 0.1s. This default is the user's own explicit numeric " +
                    "target, kept as-is: a moderate 2x vanilla's rate, made safe specifically by the mandatory " +
                    "MaxGhostSpawnsPerTick cap and the UpdateTTL dt-accuracy fix this doc ships alongside it " +
                    "(without both, 0.05s alone would be a foot-gun — see docs/design/04 Hook 3). Range keeps it " +
                    "at or faster than vanilla only; there is no headroom above vanilla's own 0.1s offered here.",
                    new AcceptableValueRange<float>(0.05f, 0.1f)));

            MaxGhostSpawnsPerTick = cfg.Bind("ZoneSim", "MaxGhostSpawnsPerTick", 1,
                new ConfigDescription(
                    "Hook 3's mandatory companion cap on first-time zone GENERATION (SpawnZone Ghost, full " +
                    "PlaceLocations/PlaceVegetation cost) per tick, across every peer, serviced round-robin so " +
                    "no single peer is starved. Vanilla value: 1 (implicitly — vanilla only ever has one " +
                    "reference position generating at a time). Default matches vanilla's own existing discipline " +
                    "exactly, not a multiple. Safe max: 4 — trades a bigger worst-case per-tick hitch for faster " +
                    "cold-world population; raise only for a fresh-world/large-group-expedition server.",
                    new AcceptableValueRange<int>(1, 4)));

            MaxCreatedPerFrame = cfg.Bind("ZoneSim", "MaxCreatedPerFrame", VanillaMaxCreatedPerFrame,
                new ConfigDescription(
                    "Z6 — GameObject instantiation budget per ZNetScene create/destroy tick. Vanilla value: 100 " +
                    "— a dedicated server ALREADY always takes this branch (Player.m_localPlayer is permanently " +
                    "null server-side, so InLoadingScreen() is permanently true; the '10' literal a client can " +
                    "hit is a dead branch on a real dedicated server). This default therefore changes NOTHING " +
                    "versus an unmodded server; it exists so an admin can opt into more if telemetry shows the " +
                    "budget is the bottleneck. Safe max: 400 (4x) — CreateObjectsSorted already self-scales past " +
                    "~100x the configured value for a large backlog, so values much above 400 buy little extra.",
                    new AcceptableValueRange<int>(100, 400)));

            CreateDestroyHz = cfg.Bind("ZoneSim", "CreateDestroyHz", 30,
                new ConfigDescription(
                    "Hook 6's companion cadence knob for the create/destroy pass itself (ZNetScene.Update's own " +
                    "1/30s gate). Vanilla value: 30 (Hz). Default matches vanilla exactly — 60 is a straightforward " +
                    "2x CPU cost for that one pass and is left as explicit opt-in only, not the default, since it " +
                    "was not one of the user's selected numeric targets.",
                    new AcceptableValueList<int>(30, 60)));

            CreateBudgetMs = cfg.Bind("ZoneSim", "CreateBudgetMs", 8f,
                new ConfigDescription(
                    "1.0.9 (frame-stall fix) — Z6's own per-tick WALL-CLOCK budget on ZNetScene's create loop " +
                    "(CreateObjectsSorted + CreateDistantObjects combined), on top of MaxCreatedPerFrame's " +
                    "existing COUNT cap — whichever is reached first stops this tick's object creation (never " +
                    "below MinCreatedPerTick, see that key; also short-circuited to an immediate cut once " +
                    "Core.FrameTiming.RecentMeanMs is already running hot — see CreateObjectsBudget.cs's own " +
                    "EffectiveCreateCap remarks). No vanilla equivalent (vanilla's cap is count-only, sized for a " +
                    "dedicated server's one permanently-void reference position, which never presents the multi-" +
                    "thousand-object backlog a freshly-ring-entered ZoneSim zone can). ROUND 2 default 7ms -> " +
                    "3ms (measured on the first harness108 1.0.9 run): 7ms barely moved Phase B's CreateObject " +
                    "call count because several remaining slow frames showed 1.4-7.4ms/object average — cost " +
                    "concentrated in occasional expensive INDIVIDUAL objects, not a steady rate a small window " +
                    "could reliably cap at a handful of objects. ROUND 4 default 3ms -> 8ms: round 3 cut walking " +
                    "stalls ~80% but left the create backlog draining at only ~60/s (~10,900 objects measured, " +
                    "several minutes to drain) — the server's own measured baseline (this mod's ZoneSim work " +
                    "aside) is ~30fps / ~5ms mean frame, so a 3ms create budget was tighter than the frame's own " +
                    "steady-state margin and too timid against the round-4 priority (drain the backlog several " +
                    "times faster while keeping walking stalls close to round 3's result) — see round 4's own " +
                    "harness108 A/B/C/D measurements for the knee actually picked from this range. HARD RULE " +
                    "(never violated by this budget): ZNetScene.CreateObject is always called for every item the " +
                    "loop reaches — this only makes the loop's own existing break condition trip earlier, exactly " +
                    "where vanilla's own count cap already knows how to stop, so 'Destroyed invalid prefab ZDO' " +
                    "can never fire because of this budget. Raise further toward 16ms only if " +
                    "zonesim.createBudgetLimitedTicksTotal stays pinned with zonesim.createBacklog not draining.",
                    new AcceptableValueRange<float>(2f, 16f)));

            MinCreatedPerTick = cfg.Bind("ZoneSim", "MinCreatedPerTick", 2,
                new ConfigDescription(
                    "1.0.9 (frame-stall fix) — the floor CreateBudgetMs (and the recent-frame-health gate) can " +
                    "never cut below: this many objects (near+distant combined) always get created in a tick's " +
                    "create loop even if the budget is already spent (or recent frames are already hot) when the " +
                    "tick starts, so a held zone's backlog always makes forward progress every tick — never a " +
                    "permanent stall. No vanilla equivalent. ROUND 2 default 4 -> 2 (same harness108 1.0.9 run " +
                    "noted on CreateBudgetMs): an UNCONDITIONAL floor is, by construction, exposed to whatever " +
                    "the single most expensive object among its N slots costs (a per-iteration budget check can " +
                    "only stop MORE objects from being attempted, never shrink one already in flight) — halving " +
                    "the floor halves how many chances per tick an occasional expensive individual object has to " +
                    "land in the unconditionally-allowed slots, while 2 still guarantees real progress every " +
                    "tick (never a permanent stall) exactly as 4 did. Safe max 20 — raise only alongside " +
                    "CreateBudgetMs.",
                    new AcceptableValueRange<int>(1, 20)));

            ObjectUnfeedGraceTicks = cfg.Bind("ZoneSim", "ObjectUnfeedGraceTicks", 3,
                new ConfigDescription(
                    "Hook 2's grace-hold: extra ticks a fed, non-Persistent, server-owned ZDO stays earmarked " +
                    "after its zone drops from the feed, before RemoveObjects's ordinary sweep may reach it. No " +
                    "vanilla equivalent (new mod-owned bookkeeping). Default (~150ms at the 0.05s tick default) " +
                    "absorbs ordinary ring-boundary churn without materially delaying real cleanup. 0 restores " +
                    "immediate-sweep (pre-fix) behaviour; safe max 20 (~1s) — raise only if " +
                    "zonesim.objectsDestroyedOnUnfeed shows real churn in play.",
                    new AcceptableValueRange<int>(0, 20)));

            // MaxObjectsPerPeer / MaxObjectsGlobal — RESIZED for HeldBandRingBonus's default-on wider ring
            // (user decision 2026-09-28, held-band redesign synthesis): the prior 2,500/15,000 defaults were
            // sized for the ring O6 could see BEFORE HeldBandRingBonus existed (PokeRingRadius alone, ring=2).
            // See docs/impl/ZONESIM-IMPL.md "Defaults" table for the full measured-baseline reasoning; summary:
            // the closest available "already proven acceptable" comparison is what a VANILLA CLIENT already
            // instantiates for itself in ordinary play — research/04's cost model puts a full default-radius
            // (5x5, ring=2) ring at ~2,500 objects in ordinary terrain, and this project's own live probe
            // (baseline/2026-09-28-probe-solo) measured ~1,500 mean / 2,184 max objects instantiated around one
            // real player on Proxima Maxi, also at ring=2. The redesign's own re-derivation (scratchpad
            // redesign/redesign-result.json "spec" §2) found the real circular zone COUNT within radius scales
            // 21 -> 37 zones (+76%) going from ring=2 to ring=3 (HeldBandRingBonus=1's default), so this project
            // linearly scales the same measured baseline by that same +76% rather than re-guessing: ~1,500 mean
            // -> ~2,640, ~2,184 max -> ~3,850. KNEE-OF-THE-CURVE default (coordinator instruction, "a great
            // improvement over vanilla, not overkill" — the smallest value that captures most of the gain, not
            // the largest still-safe one): 4,000 sits just above the scaled MAX (not just the scaled mean),
            // preserving the ORIGINAL 2,500 default's own relationship to its baseline (it also sat above both
            // the old mean and max, not merely at the mean) — so an isolated/far peer's own widened held band is
            // fully covered at default settings, exactly the far-player case this whole feature exists for, not
            // just partially covered until an operator notices and raises it by hand.
            MaxObjectsPerPeer = cfg.Bind("ZoneSim", "MaxObjectsPerPeer", 4000,
                new ConfigDescription(
                    "Z9 — per-peer object-count budget (Hook 5's ZoneCap), enforced with 90%/100% hysteresis, " +
                    "'stop appending, never destroy' on hit. No direct vanilla equivalent (the server simulates " +
                    "0 real objects per peer today); the nearest comparison is what a vanilla CLIENT already " +
                    "instantiates for itself — research/04's cost model: ~2,500 for a full default-radius (5x5, " +
                    "ring=2) ring in ordinary terrain; this project's own live probe " +
                    "(baseline/2026-09-28-probe-solo/FINDINGS.md) measured ~1,500 mean / 2,184 max objects " +
                    "around one real player on Proxima Maxi, also at ring=2. RESIZED (2026-09-28, held-band " +
                    "redesign) for ZoneSim.HeldBandRingBonus's new default of 1: the real zone count in the ring " +
                    "scales 21 -> 37 (+76%, redesign/redesign-result.json), so that same measured baseline scales " +
                    "to ~2,640 mean / ~3,850 max — default 4,000 sits just above the SCALED max, the same " +
                    "margin the original 2,500 default kept above its own (ring=2) baseline max of 2,184, so an " +
                    "isolated/far peer's own held band is fully covered without hitting the cap. A player parked " +
                    "at a large base (probe/design notes observed up to ~7,800 near a dense base) will hit the " +
                    "cap for the excess only — fails safe (that excess simply isn't server-simulated), never " +
                    "destructive. Raise toward the safe max of 10,000 (kept below research/04's own 'clearly too " +
                    "large' line of 10,000-20,000 for a single dense area) only after confirming spare RAM on " +
                    "the host via zonesim.globalObjectEstimateThisTick.",
                    new AcceptableValueRange<int>(500, 10000)));

            MaxObjectsGlobal = cfg.Bind("ZoneSim", "MaxObjectsGlobal", 24000,
                new ConfigDescription(
                    "Z9 — server-wide object-count budget across every peer combined, same hysteresis/fail-safe " +
                    "behaviour as MaxObjectsPerPeer. No vanilla equivalent. Default = MaxObjectsPerPeer x 6 " +
                    "(24,000, following MaxObjectsPerPeer's own 2026-09-28 resize for HeldBandRingBonus=1): 6 is " +
                    "a deliberately modest 'typically online together' planning figure for the target 2-10 " +
                    "player ordinary server (not the theoretical 10-player join ceiling), so most sessions never " +
                    "approach this cap even before any single peer hits their own per-peer cap. Safe max 60,000 " +
                    "(10,000 hard-clamped per-peer x 6) — raise together with MaxObjectsPerPeer, and only after " +
                    "live RSS/CPU headroom is confirmed on the host (archserver: 31GB RAM / 12 cores shared with " +
                    "2 other Valheim servers; baseline vanilla Proxima usage: 3.6GB RSS / 13% of one core at " +
                    "~1.13M ZDOs with one player, per baseline/2026-09-28-lan/BASELINE.md).",
                    new AcceptableValueRange<int>(2000, 100000)));

            CapIncludeThresholdPct = cfg.Bind("ZoneSim", "CapIncludeThresholdPct", 0.90f,
                new ConfigDescription(
                    "Z9 hysteresis: a zone not already included this tick's pass only joins the feed below this " +
                    "fraction of the global budget. No vanilla equivalent. Fixed at 0.90 per docs/design/04.",
                    new AcceptableValueRange<float>(0.5f, 0.99f)));
            CapExcludeThresholdPct = cfg.Bind("ZoneSim", "CapExcludeThresholdPct", 1.00f,
                new ConfigDescription(
                    "Z9 hysteresis: a zone already included this tick's pass stays included up to this fraction " +
                    "of the global budget (must be >= CapIncludeThresholdPct; clamped up to it if configured " +
                    "lower). No vanilla equivalent. Fixed at 1.00 per docs/design/04.",
                    new AcceptableValueRange<float>(0.5f, 1.0f)));

            RegisterPatches();
            CreateObjectsBudget.LogActiveBranchOnce();

            // WP2 (scratchpad/safety/spec.json, held-band preservation guards) -- fully self-contained (own
            // flag/config binding, own PatchRegistry.Register calls, see that file), wired here rather than its
            // own NoodlesModuleInit so every ZoneSim-domain flag stays bound from this one place.
            HeldBandPreservationGuard.Init();

            // Not a Harmony patch: a plain MonoBehaviour driver on its own GameObject, exactly like Core's own
            // NoodlesHeartbeat (see NoodlesPlugin.Awake) — started unconditionally on every process now (RUNTIME
            // REVIEW FIX, major, mixed-and-host, 2026-09-28: used to be gated here on Side.IsDedicatedServerProcess,
            // which wrongly excluded a listen-server host; PokeRingDriver.Tick()'s own ZNet.instance.IsServer()
            // check already makes this correctly inert on a plain client, no ZNet.Awake hook of its own needed).
            PokeRingDriver.EnsureStarted();

            ZoneSimGate.CanHoldProvider = CanHoldZone;

            // Held-band synthesis: DeploymentLog (Core) reports the ring bonus on its own startup line without a
            // compile-time reference to this module — see HeldBandGate's own remarks (Core/Contracts.cs).
            HeldBandGate.RingBonusProvider = () => Mathf.Clamp(HeldBandRingBonus.Value, 0, 2);

            // Boot-time check: SimulationDistanceCeiling.Init (Ownership module) always runs before this Init
            // (both default NoodlesModuleInit order 100, ties broken by declaring-type full name — "Ownership"
            // sorts before "ZoneSim" — confirmed via NoodlesPlugin.RunAllModuleInits), so OwnershipAuthority's
            // flag is already registered and reflects the config file's own boot-time value by the time this
            // runs. A LIVE flip of either flag re-checks via each one's own SettingChanged handler (this file's
            // activeCfg above; OwnershipAuthority's own s_ownershipAuthorityActive in SimulationDistanceCeiling.cs).
            CrossFlagWarnings.CheckServerZoneSimWithoutOwnershipAuthority();

            NoodlesLog.Verbose($"ZoneSim: configured PokeRingRadius={PokeRingRadius.Value} " +
                $"HeldBandRingBonus={HeldBandRingBonus.Value} MaxObjectsPerPeer={MaxObjectsPerPeer.Value} " +
                $"MaxObjectsGlobal={MaxObjectsGlobal.Value} UpdateTickSeconds={UpdateTickSeconds.Value} " +
                $"ZoneTTLSeconds={ZoneTTLSeconds.Value} MaxZoneBuildsPerTick={MaxZoneBuildsPerTick.Value} " +
                $"ZoneBuildBudgetMs={ZoneBuildBudgetMs.Value} MaxCreatedPerFrame={MaxCreatedPerFrame.Value} " +
                $"CreateBudgetMs={CreateBudgetMs.Value} MinCreatedPerTick={MinCreatedPerTick.Value}.");
        }

        private static void RegisterPatches()
        {
            // RUNTIME REVIEW FIX (major, mixed-and-host, 2026-09-28): these four were PatchSide.ServerOnly,
            // which excluded a listen-server host entirely (a client-build process never even got these patches
            // offered to it) even though each one's own body already independently checks
            // ZNet.instance.IsServer() at runtime (Containment's Finalizer's hazard-2 branch, ObjectFeed's own
            // Prefix, and — after the Effective*() fix below — the two CreateObjectsBudget transpiler targets).
            // Now Both, matching the runtime-authority pattern this project already uses elsewhere (Transport.Q2,
            // Transport.Q6, Ownership.O1 — see those classes' own doc comments).
            PatchRegistry.Register("ServerZoneSim", typeof(Patch_ZNetScene_CreateObject_Containment), PatchSide.Both);
            PatchRegistry.Register("ServerZoneSim", typeof(Patch_ZNetScene_CreateObjects_ObjectFeed), PatchSide.Both);

            PatchRegistry.Register("ServerZoneSim", typeof(UpdateCadence.Patch_ZoneSystem_Update_Cadence), PatchSide.Both);
            PatchRegistry.Register("ServerZoneSim", typeof(UpdateCadence.Patch_ZoneSystem_CreateGhostZones_RoundRobinCap), PatchSide.Both);

            PatchRegistry.Register("ServerZoneSim", typeof(ZoneTtl.Patch_ZNet_Awake_ApplyZoneTtl), PatchSide.Both);
            PatchRegistry.Register("ServerZoneSim", typeof(ZoneTtl.Patch_ZoneSystem_UpdateTTL_BacklogDrain), PatchSide.Both);

            PatchRegistry.Register("ServerZoneSim", typeof(CreateObjectsBudget.Patch_ZNetScene_CreateObjects_FrameBudget), PatchSide.Both);
            PatchRegistry.Register("ServerZoneSim", typeof(CreateObjectsBudget.Patch_ZNetScene_Update_CreateDestroyCadence), PatchSide.Both);

            // 1.0.9 (frame-stall fix): Z6's time-budgeted instantiation -- see CreateObjectsBudget.cs's own
            // remarks on each of these three for the full design (Stopwatch reset + backlog/telemetry Postfix,
            // then the two loop-break transpilers).
            PatchRegistry.Register("ServerZoneSim", typeof(CreateObjectsBudget.Patch_ZNetScene_CreateObjects_TimeBudgetTick), PatchSide.Both);
            PatchRegistry.Register("ServerZoneSim", typeof(CreateObjectsBudget.Patch_ZNetScene_CreateObjectsSorted_TimeBudget), PatchSide.Both);
            PatchRegistry.Register("ServerZoneSim", typeof(CreateObjectsBudget.Patch_ZNetScene_CreateDistantObjects_TimeBudget), PatchSide.Both);

            // Round 4 (zone-load-stall fix): create near players first -- see CreateNearFirstOrder.cs's own
            // remarks. Independent patch class, same target method as the time-budget transpiler above (this
            // module's own established pattern for multiple independent transpilers sharing one Harmony
            // target); each is free to touch a different, non-overlapping call site in the method body.
            PatchRegistry.Register("ServerZoneSim", typeof(CreateNearFirstOrder.Patch_ZNetScene_CreateObjectsSorted_NearFirst), PatchSide.Both);

            PatchRegistry.Register("ServerZoneSim", typeof(SnapGuard.Patch_SnapToGround_SnappAll_Defer), PatchSide.Both);

            PatchRegistry.Register("ServerZoneSim", typeof(TerrainReadinessGate.Patch_TerrainComp_TryCleanInvalidTCs_Canary), PatchSide.Both);
        }

        /// <summary>
        /// RUNTIME REVIEW FIX (minor, client-only audit, 2026-09-28): every <c>Effective*()</c> getter below
        /// used to read only <see cref="ServerZoneSimFlag"/>.Active, with no side check at all — so a tester who
        /// copied a server's ServerZoneSim preset onto their own (plain, non-hosting) client profile would
        /// silently double their OWN local ZoneSystem tick rate and engage SnapGuard/TerrainReadinessGate on
        /// their own local terrain, despite the config's own doc string promising "does NOT ... change what any
        /// peer receives by itself". <c>ZNet.instance.IsServer()</c> (not <c>Side.IsDedicatedServerProcess</c>)
        /// is deliberately the check here: it reads true for a listen-server host too (mixed-and-host's own
        /// major finding on <see cref="ZoneSim.PokeRingDriver"/> — this feature must actually run there), while
        /// still falling back to the vanilla literal for a plain remote client. Unlike <c>SavePolicy</c>'s
        /// Game.Awake-timing hazard, these transpiler targets only ever fire once <c>ZoneSystem</c>/
        /// <c>ZNetScene</c> exist and are ticking — long after <c>ZNet.Awake</c> — so there is no
        /// before-ZNet-exists case to special-case here.
        /// </summary>
        // V1 FIX (deployment-modes review, now BLOCKER since ServerZoneSim is about to default ON, 2026-09-28):
        // made internal so every OTHER file in this module (SnapGuard, UpdateCadence, InstantiateContainment,
        // ZoneTtl) can require this alongside ServerZoneSimFlag.Active in its own patch bodies, exactly as this
        // file's own Effective*()/CanHoldZone already do below — see each call site's own remarks for why
        // ServerZoneSimFlag.Active alone was not enough on a plain (non-authoritative) client.
        internal static bool IsAuthoritativeProcess() => ZNet.instance != null && ZNet.instance.IsServer();

        /// <summary>Transpiler substitute for ZoneSystem.Update's two 0.1f literals (Hook 3) — see UpdateCadence.cs.</summary>
        internal static float EffectiveUpdateTickSeconds() =>
            ServerZoneSimFlag.Active && IsAuthoritativeProcess() ? Mathf.Clamp(UpdateTickSeconds.Value, 0.05f, 0.1f) : VanillaZoneUpdateTickSeconds;

        /// <summary>
        /// Inserted by <see cref="UpdateCadence.Patch_ZoneSystem_Update_Cadence"/>'s transpiler to run exactly
        /// once per REAL ZoneSystem.Update tick (past the early-return gate), regardless of how often the
        /// message-pump calls Update() itself. The one per-real-tick reset this module currently needs:
        /// the ghost-spawn round-robin cap's counter (report last tick's value to telemetry, then zero it
        /// before this tick's CreateGhostZones calls happen).
        /// </summary>
        internal static void OnRealZoneTick()
        {
            // V1 FIX: runtime authority, not the flag alone -- see IsAuthoritativeProcess's own remarks. This is
            // inserted unconditionally by the transpiler (UpdateCadence.cs) into every real ZoneSystem.Update
            // tick, including a plain client's own local one.
            if (!ServerZoneSimFlag.Active || !IsAuthoritativeProcess()) return;
            try { UpdateCadence.Patch_ZoneSystem_CreateGhostZones_RoundRobinCap.ResetTickCounter(); }
            catch (Exception e) { NoodlesLog.PatchException(nameof(OnRealZoneTick) + ".GhostSpawnReset", e); }

            // Hook 7's mandatory periodic re-trigger (docs/design/04) — same cadence as this tick.
            try { SnapGuard.RetryDeferred(); }
            catch (Exception e) { NoodlesLog.PatchException(nameof(OnRealZoneTick) + ".SnapRetry", e); }
        }

        /// <summary>Transpiler substitute for ZNetScene.CreateObjects's '100' literal (Hook 6) — see CreateObjectsBudget.cs.</summary>
        internal static int EffectiveMaxCreatedPerFrame() =>
            ServerZoneSimFlag.Active && IsAuthoritativeProcess() ? Mathf.Clamp(MaxCreatedPerFrame.Value, 100, 400) : VanillaMaxCreatedPerFrame;

        /// <summary>Transpiler substitute for ZNetScene.Update's '1f/30f' literal (Hook 6) — see CreateObjectsBudget.cs.</summary>
        internal static float EffectiveCreateDestroyIntervalSeconds()
        {
            if (!ServerZoneSimFlag.Active || !IsAuthoritativeProcess()) return VanillaCreateDestroyIntervalSeconds;
            int hz = CreateDestroyHz.Value == 60 ? 60 : 30; // AcceptableValueList already enforces this; defensive re-check anyway
            return 1f / hz;
        }

        /// <summary>
        /// ZoneSimGate.CanHold (Core/Contracts.cs) — Ownership polls this before holding a zone AND every claim
        /// cycle for zones already held. "Included in this tick's object feed" is exactly the seam's own
        /// contract ("zone inside a peer's server-simulated ring, terrain ready, objects instantiated, within
        /// ZoneCap budget") by construction: ZoneCap.WasIncludedThisTick(zone) is only ever true for a zone
        /// that was (a) in some ready peer's ring, (b) already real (m_zones.ContainsKey), (c) not quarantined
        /// (see InstantiateContainment), and (d) under budget this exact tick — see ObjectFeed.RunFeed. A
        /// residual, benign timing looseness (objects fed this tick may still be mid-instantiation if Z6's
        /// per-frame budget hasn't caught up) is documented in ZONESIM-IMPL.md "known risks" — Ownership's own
        /// re-poll-every-claim-cycle contract is the intended mitigation, not something this module needs to
        /// solve more precisely.
        /// </summary>
        private static bool CanHoldZone(Vector2s zone)
        {
            if (!ServerZoneSimFlag.Active || !IsAuthoritativeProcess()) return false;
            return ZoneCap.WasIncludedThisTick(zone);
        }
    }
}
