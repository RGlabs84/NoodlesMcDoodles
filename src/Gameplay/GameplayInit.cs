using BepInEx.Configuration;
using NoodlesMcDoodles.Core;
using NoodlesMcDoodles.Core.ConfigFiles;

namespace NoodlesMcDoodles.Gameplay
{
    /// <summary>
    /// Config binding, feature-flag registration and patch registration for the whole Gameplay module
    /// (docs/design/05-gameplay-under-server-authority.md, levers S1-S8). One <see cref="NoodlesModuleInitAttribute"/>
    /// entry point per `docs/impl/CORE-API.md` "Registering your module".
    /// <para>
    /// <b>H0 substitution (documented deviation, not a silent one).</b> The blueprint's own H0 proposes a
    /// hand-rolled lazy-patch mechanism (a tiny always-installed <c>ZNet.Awake</c> postfix that calls
    /// <c>harmony.PatchAll(...)</c> filtered by a custom attribute, gated on <c>ZNet.instance.IsDedicated()</c>)
    /// specifically so H1/H3/H4's patches are never installed on a client process, including a listen-server
    /// host. <c>docs/impl/CORE-API.md</c> (written after the blueprint, "added by the lead before the M2-M7
    /// build") already solves the identical problem, earlier and more simply: <see cref="PatchRegistry.Register"/>'s
    /// <c>PatchSide.ServerOnly</c> is decided once, at plugin <c>Awake</c> — before <c>ZNet.instance</c> even
    /// exists — from <see cref="Side.IsDedicatedServerProcess"/> (the same "SystemInfo.graphicsDeviceType ==
    /// Null" signal, true only for a real <c>-batchmode -nographics</c> dedicated process, false for a listen-
    /// server host exactly like <c>ZNet.IsDedicated()</c> itself). Registering every server-only patch class in
    /// this module with <c>PatchSide.ServerOnly</c> gives the EXACT property H0 exists to guarantee — these
    /// patches are never even offered to Harmony on a client process — through the mod's own sanctioned, single
    /// patch-application path, without a second, redundant patch-application mechanism and without the
    /// forbidden direct <c>harmony.PatchAll()</c> call H0's own sketch would have required. H2 (combat stat
    /// credit) is registered <c>PatchSide.Both</c>, matching the blueprint's own note that its `IsServer()` gate
    /// makes it "harmless either way".
    /// </para>
    /// </summary>
    internal static class GameplayInit
    {
        [NoodlesModuleInit]
        internal static void Init()
        {
            ConfigFile cfg = NoodlesConfig.File;

            // === S1 — ServerSpawning =========================================================================
            var serverSpawningActiveCfg = cfg.Bind("Features", "ServerSpawning.Active", true,
                "S1: removes the Player.m_localPlayer==null term from SpawnSystem.UpdateSpawning's owner " +
                "gate, so mob spawning can run in a zone the SERVER owns (vanilla: spawning silently never " +
                "runs on a dedicated server at all, since that field is permanently null there — no vanilla " +
                "equivalent to compare against). DEFAULT ON (2026-09-28, all-on safety-net spec, WP4): no " +
                "longer dormant — OwnershipAuthority and ServerZoneSim are both live by default now, so the " +
                "server actually holds zones with wildlife spawning in them. The one real hazard that opens " +
                "(wandering wildlife killing a player's own tame parked in an empty held-band zone nobody is " +
                "near) is closed by src/ZoneSim/HeldBandPreservationGuard.cs's Character.RPC_Damage prefix " +
                "(WP2): it suppresses damage to a Tameable defender from a non-Player attacker there, without " +
                "disabling ordinary wild-vs-wild culling and without ever making a tame invincible against an " +
                "actual player.");
            Features_Gameplay.ServerSpawning = Features.Register("Gameplay.ServerSpawning", serverSpawningActiveCfg);

            var maxZonesCfg = cfg.Bind("Features", "ServerSpawning.MaxZonesScannedPerSecond", 8,
                new ConfigDescription(
                    "S1: caps how many server-owned zones' UpdateSpawning may run in the same real second (no " +
                    "vanilla equivalent — vanilla pays this cost per-CLIENT, one zone each; only a server " +
                    "holding many zones at once needs a cap at all). Default 8 is already a small slice of the " +
                    "safe maximum (64) and only matters once ServerSpawning.Active is also on — for an ordinary " +
                    "2-10 player home server that's comfortably above the handful of zones likely to have a " +
                    "player standing in them at once. Raise toward 64 only if telemetry (gameplay.s1.*) shows " +
                    "zones are actually being skipped for budget on a busier server.",
                    new AcceptableValueRange<int>(1, 64)));
            Features_Gameplay.ServerSpawningMaxZonesPerSecond = maxZonesCfg;

            // === S3 — ServerCombatStats ======================================================================
            var combatStatsCfg = cfg.Bind("Features", "ServerCombatStats.Active", true,
                "S3: detects (telemetry only in this build, see docs/impl/GAMEPLAY-IMPL.md) hits against a " +
                "server-owned target that vanilla's own PlayerHits/EnemyHits stat credit silently misses " +
                "(vanilla: stat credit already runs, just always misses once a target is server-owned, " +
                "since it keys off the client-only Player.m_localPlayer singleton). DEFAULT ON (2026-09-28, " +
                "all-on safety-net spec, WP4): purely cosmetic/telemetry either way (Data-loss analysis: not " +
                "a data-loss path, independently reconfirmed this pass) — no guard needed for this one.");
            Features_Gameplay.ServerCombatStats = Features.Register("Gameplay.ServerCombatStats", combatStatsCfg);

            // === S5 — HeadlessBiomeWind =======================================================================
            var biomeWindCfg = cfg.Bind("Features", "HeadlessBiomeWind.Active", true,
                "S5: gives EnvMan a proxy camera so GetBiome()/UpdateEnvironment() stop bailing to " +
                "EmptyMeadows/no-op on a dedicated server (vanilla: both methods read Camera.main, always " +
                "null in batch mode, so headless biome/wind is always the empty default — no vanilla " +
                "equivalent). Affects Ship.GetSailForce/Windmill.GetPowerOutput fidelity only once server-" +
                "held zones with real occupants exist. DEFAULT ON (2026-09-28, all-on safety-net spec, " +
                "WP4): read-only camera proxy, audited and confirmed safe, already live-verified in HANDOFF " +
                "— field-tested, no longer withheld pending that test.");
            Features_Gameplay.HeadlessBiomeWind = Features.Register("Gameplay.HeadlessBiomeWind", biomeWindCfg);

            // === S6 — HeadlessNRESweep (recommended ON — strictly protective bug fix, not a tunable) ==========
            var nreSweepCfg = cfg.Bind("Features", "HeadlessNRESweep.Active", true,
                "S6: six null-guard fixes for a dedicated-server-only crash/duplication/broadcast-drop bug " +
                "in CookingStation/Pickable/Valkyrie/Leviathan/MusicVolume/Ship sail changes (vanilla: an unguarded " +
                "Player.m_localPlayer read NREs on a dedicated server in all six — no vanilla equivalent, " +
                "this fixes a bug rather than tuning a value). Recommended ON: never changes gameplay when " +
                "nothing would have crashed, only prevents a real duplication/broadcast-loss path — this is " +
                "the one Gameplay flag this module ships enabled out of the box.");
            Features_Gameplay.HeadlessNRESweep = Features.Register("Gameplay.HeadlessNRESweep", nreSweepCfg);

            // === UpdateIsolation (1.0.5, recommended ON — strictly protective, not a tunable) ===================
            var updateIsolationCfg = cfg.Bind("Features", "UpdateIsolation.Active", true,
                "1.0.5: on the dedicated server, runs each object in Valheim's four batched update loops " +
                "(MonoUpdatersExtra CustomFixedUpdate/CustomUpdate/CustomLateUpdate/UpdateAI) inside its own " +
                "try/catch, so one object's exception no longer stops every other object's update (vanilla: the " +
                "exception escapes the loop, skips the rest of that frame's passes -- every creature's AI and " +
                "movement -- and leaves the loop's shared list uncleared for the next frame). The first exception of " +
                "each kind is still logged by Unity with its stack trace; repeats are counted " +
                "(gameplay.updateIsolation.caught) and summarised once a minute. Recommended ON: changes nothing " +
                "unless something throws, and this mod's server-side zone simulation runs far more objects on the " +
                "server than vanilla does, so a bug that only shows on a headless server would otherwise stall " +
                "the whole world's simulation.");
            Features_Gameplay.UpdateIsolation = Features.Register("Gameplay.UpdateIsolation", updateIsolationCfg);

            // === S4 — VehicleContainerHandover ===============================================================
            var vehicleHandoverCfg = cfg.Bind("Features", "VehicleContainerHandover.Active", true,
                "S4: HandoverGuard participation for Container/Vagon/Sadle/Ship — refuses to let the " +
                "Ownership module reclaim one of these from a present player while it's actually in use " +
                "(no vanilla equivalent; only matters once the Ownership module can reclaim objects at " +
                "all). DEFAULT ON (2026-09-28, ownership redesign decision 1): OwnershipAuthority now " +
                "defaults on too, and its O6 reclaim gate (ZoneClaimSweep) needs this guard active to ever " +
                "reclaim a currently-owned Container/Vagon/Sadle/Ship at all — see HandoverGuard's own " +
                "'fail closed per category' remarks: leaving this off wouldn't just skip protecting these, " +
                "it would refuse reclaiming them outright, which is safe but defeats the point of turning " +
                "OwnershipAuthority on. Turn off only to intentionally exclude this whole category from " +
                "server-side reclaim.");
            Features_Gameplay.VehicleContainerHandover = Features.Register("Gameplay.VehicleContainerHandover", vehicleHandoverCfg);

            var quiescenceCfg = cfg.Bind("Features", "HandoverGuard.QuiescenceSeconds", 5f,
                new ConfigDescription(
                    "S4/S2/S7: how long a Container/Vagon/production/creature ZDO's DataRevision must sit " +
                    "unchanged before QuiescenceTracker calls it idle enough to reclaim (no vanilla equivalent " +
                    "— this is this mod's own safety margin, not a performance knob). Default 5s is the " +
                    "blueprint's own reasoned minimum: comfortably above Smelter's 1s accumulator gap, the " +
                    "tightest known per-tick write interval among guarded types, with margin. Needs live " +
                    "measurement before field use, same open-question class as O3 in the Ownership module.",
                    new AcceptableValueRange<float>(2f, 60f)));
            Features_Gameplay.QuiescenceSecondsConfig = quiescenceCfg;

            // === S2/S7 — ProductionHandoverGuard =============================================================
            // NOTE: DESIGN.md's Configuration reference lists only ONE flag for this pair
            // ("Features.ProductionHandoverGuard.Active (S7)") even though docs/design/05's own Hook H6 text
            // describes one shared LivenessBookkeeper/QuiescenceTracker mechanism covering BOTH S2 (creature
            // AI liveness, BaseAI.UpdateAI) and S7 (production, Smelter.UpdateSmelter). Implemented here under
            // that one documented flag for both — see docs/impl/GAMEPLAY-IMPL.md for this reading, spelled out.
            var productionHandoverCfg = cfg.Bind("Features", "ProductionHandoverGuard.Active", true,
                "S2 (creature AI) + S7 (production): HandoverGuard participation for live creatures and " +
                "Smelter — refuses to let the Ownership module reclaim one mid-fight or mid-brew (no " +
                "vanilla equivalent; only matters once the Ownership module can reclaim objects at all). " +
                "Covers BOTH S2 and S7 under DESIGN.md's single documented flag for this pair. DEFAULT ON " +
                "(2026-09-28, ownership redesign decision 1) alongside VehicleContainerHandover — same " +
                "reasoning: OwnershipAuthority's O6 reclaim gate needs this guard active to ever reclaim a " +
                "currently-owned Smelter or live creature at all; leaving it off refuses reclaiming this " +
                "whole category outright rather than leaving it unprotected.");
            Features_Gameplay.ProductionHandoverGuard = Features.Register("Gameplay.ProductionHandoverGuard", productionHandoverCfg);

            // RUNTIME REVIEW FIX (2026-09-28, MAJOR): LivenessBookkeeper's two tables (fast-path ticks,
            // double-drop sightings) are fed exclusively by patches gated on this one flag — previously only a
            // full kill-switch flush ever cleared them, so flipping JUST this flag off at runtime left both
            // tables' existing entries alive (harmless once the flag is off, since nothing reads or writes them
            // again, but unnecessarily holding memory until the next kill-switch trip or a slow prune-by-count).
            // Marshalled through MainThreadDispatcher per CORE-API.md ("don't touch shared state from a
            // ConfigFile.SettingChanged callback directly") — SettingChanged is not guaranteed to run on the
            // main thread, and these Dictionaries are also written from Harmony postfixes that do run there.
            productionHandoverCfg.SettingChanged += (_, __) => MainThreadDispatcher.Enqueue(() =>
            {
                if (!Features_Gameplay.ProductionHandoverGuard.Active) LivenessBookkeeper.Clear();
            });

            // === S8 correctness — StructureHandoverGuard =====================================================
            // WP4 (2026-09-28, all-on safety-net spec): explicitly NOT flipped by this pass, unlike the four
            // levers above. Per spec.json's own cannotEnable analysis: this is the one flag in the whole spec
            // where "on" is the LESS safe direction for the exact invariant the user named — off refuses every
            // WearNTear reclaim outright (fail-closed, current default); on accepts a genuinely open, still-
            // deferred race (H7, see the class remarks below and docs/impl/GAMEPLAY-IMPL.md) with no ZDO-
            // replicated signal fine-grained enough yet to detect it. Revisit only once that sub-second guard
            // actually exists — not part of this pass.
            var structureHandoverCfg = cfg.Bind("Features", "StructureHandoverGuard.Active", false,
                "S8 correctness: WearNTear/Door/RPC_ClearCachedSupport routing is confirmed already safe " +
                "regardless of current owner and needs no patch (H7). CORRECTED 2026-09-28: this is NOT a pure " +
                "status marker — HandoverGuard refuses to let the Ownership module reclaim ANY WearNTear piece " +
                "(wall, beam, floor, etc.) while this is off, the safest default. Turning it ON is what makes a " +
                "WearNTear piece reclaimable at all, and by doing so explicitly ACCEPTS the one residual the " +
                "blueprint leaves deferred, not solved: a force-claim mid-UpdateSupport() recalculation can race " +
                "a destroy-with-drops decision (duplicate drops) — no ZDO-replicated signal fine-grained enough " +
                "to catch that sub-second race exists yet (QuiescenceTracker is deliberately coarse, seconds not " +
                "sub-second). No vanilla equivalent. STAYS OFF by default even though VehicleContainerHandover/ " +
                "ProductionHandoverGuard now default on (2026-09-28, ownership redesign decision 1 — this flag " +
                "was explicitly excluded from that decision, not merely unreached by it): the deferred " +
                "duplicate-drops race above is a real, still-unclosed residual this flag alone signs off on, and " +
                "the other two guards' own in-use checks are unaffected by leaving it off.");
            Features_Gameplay.StructureHandoverGuard = Features.Register("Gameplay.StructureHandoverGuard", structureHandoverCfg);

            // === S8 new — DoorAutoClose =======================================================================
            // 1.1.0: main file [3 - Server World] DoorAutoClose (was [Features] DoorAutoClose.Active). S8: server closes
            // a player-placed door (never a keyed or Location/dungeon door — hard exclusion, docs/impl/GAMEPLAY-IMPL.md)
            // left open past IdleSeconds, writing ZDOVars.s_state DIRECTLY (as RPC_UseDoor does) on a ZDO the server
            // already owns, in server-held zones only — so PrivateArea.CheckAccess and other mods' Door patches do not
            // see it. Audited: zero data-loss risk (DoorAutoCloseScanner.cs). DEFAULT ON (2026-09-28, WP4).
            var doorAutoCloseCfg = NoodlesConfig.DoorAutoClose;
            Features_Gameplay.DoorAutoClose = Features.Register("Gameplay.DoorAutoClose", doorAutoCloseCfg);

            var idleSecondsCfg = cfg.Bind("Features", "DoorAutoClose.IdleSeconds", 300,
                new ConfigDescription(
                    "S8 new: how long a player-placed door must sit open before auto-closing (no vanilla " +
                    "equivalent). Default 300s (5 min) is the blueprint's own recommendation — reads as tidying " +
                    "an abandoned base, not doors slamming behind a player mid-walk-through; the only new risk " +
                    "this introduces is UX (identical in kind to another player closing a door on you, which " +
                    "vanilla already allows), never data loss.",
                    new AcceptableValueRange<int>(30, 3600)));
            Features_Gameplay.DoorAutoCloseIdleSeconds = idleSecondsCfg;

            // RULE (2026-09-28 coordinator note, "great improvement, not overkill"): blueprint's own default
            // was 5s. Lowered in cost here to 30s, not raised in default IdleSeconds — at the time this
            // default was set, the scan (this scanner's own cost) was a full server-ZDO walk, and 5s bought
            // 60x finer timing resolution than IdleSeconds (300s) can even use; 30s kept the close within 10%
            // of IdleSeconds (i.e., still reads as "closed because it sat open ~5 minutes", not noticeably
            // later) while cutting that walk's cost 6x relative to the blueprint value.
            // FIX (cpu-memory finding, this pass): DoorAutoCloseScanner.Scan() no longer walks the whole
            // ZDOMan dictionary at all — it now visits only zones ServerZoneMembership.Snapshot() reports as
            // server-held, via ZDOMan.FindSectorObjects (see that method's own remarks in DoorAutoCloseScanner.cs),
            // so cost scales with objects-in-held-zones, not total world ZDO count. The 5s->30s interval
            // tradeoff above is kept as-is here (not retuned) pending a live measurement at Proxima scale —
            // this fix removes the dependency on total world ZDO count, it does not by itself justify a
            // different default.
            // WP4 (2026-09-28): DoorAutoClose.Active now defaults ON (see its own config description above),
            // so this value is no longer opt-in-only — it is what every server pays by default; kept at 30s
            // rather than lowered further for exactly that reason. See docs/impl/GAMEPLAY-IMPL.md "Defaults"
            // table (stale as of this fix re: scan cost — still describes the pre-fix whole-dictionary-walk
            // cost model and the now-retired skippedOwnershipBoundary counter; docs/impl/INTEGRATION-REVIEW.md:127
            // is stale the same way — neither file is owned by this pass, flagging both for a docs update).
            var scanIntervalCfg = cfg.Bind("Features", "DoorAutoClose.ScanIntervalSeconds", 30,
                new ConfigDescription(
                    "S8 new: how often the server walks its own ZDOs looking for a player-placed door to close " +
                    "(no vanilla equivalent). Blueprint default was 5s; changed to 30s here (see class remarks) " +
                    "— still well under 10% of the 300s IdleSeconds default, so closes are not noticeably later, " +
                    "while scanning 6x less often. Lower toward 1s only if a busy, heavily-built server wants " +
                    "closer-to-the-second timing and can afford scanning that often; the hard clamp stays 1-60s.",
                    new AcceptableValueRange<int>(1, 60)));
            Features_Gameplay.DoorAutoCloseScanIntervalSeconds = scanIntervalCfg;

            // WP1 per-feature quarantine wiring (2026-09-28, WP4 all-on pass): each flag's own OnForcedOff
            // release callback, so a feature-level exception burst (FeatureStandDown, WP1) or a patch-apply
            // failure (PatchRegistry) actually releases what that feature was holding, not just stops granting
            // new holds. HandoverGuard itself holds no state of its own (a pure function of ZDO + the trackers
            // below), but is the one release path both handover-guard flags share — matches its own
            // FlushOnDisable remarks ("the trackers it reads do [hold state]... must forget everything").
            Features_Gameplay.VehicleContainerHandover.OnForcedOff = HandoverGuard.FlushOnDisable;
            Features_Gameplay.ProductionHandoverGuard.OnForcedOff = HandoverGuard.FlushOnDisable;
            Features_Gameplay.DoorAutoClose.OnForcedOff = DoorAutoCloseScanner.FlushOnDisable;

            // === Registration =================================================================================
            HandoverGuard.Register();

            bool dedicated = Side.IsDedicatedServerProcess;
            RegisterPatches();
            if (dedicated) DoorAutoCloseScanner.EnsureCreated(); // server-only component — never created on a client process at all

            KillSwitch.RegisterFlushCallback("Gameplay.HandoverGuard", HandoverGuard.FlushOnDisable);
            KillSwitch.RegisterFlushCallback("Gameplay.HeadlessBiomeWind", HeadlessBiomeWind.FlushOnDisable);
            KillSwitch.RegisterFlushCallback("Gameplay.DoorAutoClose", DoorAutoCloseScanner.FlushOnDisable);
            // WP4 — ContainerLossWatch has no FeatureFlag of its own (see its own class remarks), so this global
            // kill-switch flush is its only release path; pure bookkeeping, safe to clear unconditionally.
            KillSwitch.RegisterFlushCallback("Gameplay.ContainerLossWatch", ContainerLossWatch.Clear);
        }

        private static void RegisterPatches()
        {
            // S1
            PatchRegistry.Register("Gameplay.ServerSpawning", typeof(SpawnSystemGate.Patch_SpawnSystem_UpdateSpawning), PatchSide.ServerOnly);

            // S3 — postfix is already IsServer()-gated internally; harmless (inert) either side, matches H2's own note.
            PatchRegistry.Register("Gameplay.ServerCombatStats", typeof(CombatStatCredit.Patch_Character_RPC_Damage_StatCredit), PatchSide.Both);

            // S5
            PatchRegistry.Register("Gameplay.HeadlessBiomeWind", typeof(HeadlessBiomeWind.Patch_EnvMan_GetBiome_HeadlessCamera), PatchSide.ServerOnly);
            PatchRegistry.Register("Gameplay.HeadlessBiomeWind", typeof(HeadlessBiomeWind.Patch_EnvMan_UpdateEnvironment_HeadlessCamera), PatchSide.ServerOnly);

            // S6 (six independent classes — each its own PatchRegistry.Register call, per CORE-API.md, even
            // though all six share one feature flag)
            PatchRegistry.Register("Gameplay.HeadlessNRESweep", typeof(NREGuards.Patch_CookingStation_SpawnItem), PatchSide.ServerOnly);
            PatchRegistry.Register("Gameplay.HeadlessNRESweep", typeof(NREGuards.Patch_Pickable_RPC_Pick), PatchSide.ServerOnly);
            PatchRegistry.Register("Gameplay.HeadlessNRESweep", typeof(NREGuards.Patch_Valkyrie_Awake), PatchSide.ServerOnly);
            PatchRegistry.Register("Gameplay.HeadlessNRESweep", typeof(NREGuards.Patch_Leviathan_RPC_Left), PatchSide.ServerOnly);
            PatchRegistry.Register("Gameplay.HeadlessNRESweep", typeof(NREGuards.Patch_MusicVolume_RPC_PlayMusic), PatchSide.ServerOnly);
            PatchRegistry.Register("Gameplay.HeadlessNRESweep", typeof(NREGuards.Patch_Ship_UpdateSailSize), PatchSide.ServerOnly);

            // 1.0.5 UpdateIsolation (one class per MonoUpdatersExtra loop, all four under one flag)
            PatchRegistry.Register("Gameplay.UpdateIsolation", typeof(UpdateIsolation.Patch_MonoUpdatersExtra_CustomFixedUpdate), PatchSide.ServerOnly);
            PatchRegistry.Register("Gameplay.UpdateIsolation", typeof(UpdateIsolation.Patch_MonoUpdatersExtra_CustomUpdate), PatchSide.ServerOnly);
            PatchRegistry.Register("Gameplay.UpdateIsolation", typeof(UpdateIsolation.Patch_MonoUpdatersExtra_CustomLateUpdate), PatchSide.ServerOnly);
            PatchRegistry.Register("Gameplay.UpdateIsolation", typeof(UpdateIsolation.Patch_MonoUpdatersExtra_UpdateAI), PatchSide.ServerOnly);

            // S4 (ship hand-back transpiler + its own postcondition telemetry pair)
            PatchRegistry.Register("Gameplay.VehicleContainerHandover", typeof(ShipHandback.Patch_Ship_UpdateOwner_NullBypass), PatchSide.ServerOnly);
            PatchRegistry.Register("Gameplay.VehicleContainerHandover", typeof(ShipHandback.Patch_Ship_UpdateOwner_OwnerChangeTelemetry), PatchSide.ServerOnly);

            // S2/S7 (liveness fast-path + double-drop watch, all under the one ProductionHandoverGuard flag).
            // RUNTIME REVIEW FIX (major/blocker, mixed-and-host, 2026-09-28): all four were PatchSide.ServerOnly,
            // decided from Side.IsDedicatedServerProcess (a build/process-type heuristic fixed at plugin Awake,
            // before ZNet.instance exists — see Side.cs remarks) — so a listen-server host (a client-build
            // process, Side.IsDedicatedServerProcess == false there, but ZNet.instance.IsServer() == true once
            // ZNet exists) never even got these patches installed, regardless of ProductionHandoverGuard.Active
            // (DEFAULT ON per decision 1) and regardless of the listen host's real runtime authority — exactly
            // the RUNTIME-AUTHORITY RULE hazard this pass's own sweep already fixed in ZoneSimConfig.cs,
            // SimulationDistanceCeiling.cs, SendPriority.cs and TelemetryHooks.cs, but had not yet reached here.
            // For the two DoubleDropWatch patches this is the live safety net (LossSignals.Report -> AutoStandDown)
            // for exactly the dual-owner/InvokeRepeating drop race ProductionHandoverGuard exists to guard
            // against, silently absent on a listen host without this fix. Now Both, with each postfix body
            // (LivenessBookkeeper.cs) itself gated on ZNet.instance.IsServer() in addition to the existing
            // feature-flag check, matching the pattern already used throughout this project (ObjectFeed.cs,
            // ZoneTtl.cs, CombatStatCredit.cs, etc.) — inert on a plain client, installed and correctly gated on
            // both a dedicated server and a listen host.
            PatchRegistry.Register("Gameplay.ProductionHandoverGuard", typeof(LivenessBookkeeper.Patch_BaseAI_UpdateAI_LivenessPostfix), PatchSide.Both);
            PatchRegistry.Register("Gameplay.ProductionHandoverGuard", typeof(LivenessBookkeeper.Patch_Smelter_UpdateSmelter_LivenessPostfix), PatchSide.Both);
            PatchRegistry.Register("Gameplay.ProductionHandoverGuard", typeof(LivenessBookkeeper.Patch_CharacterDrop_OnDeath_DoubleDropWatch), PatchSide.Both);
            PatchRegistry.Register("Gameplay.ProductionHandoverGuard", typeof(LivenessBookkeeper.Patch_Smelter_QueueProcessed_DoubleDropWatch), PatchSide.Both);

            // WP4 — Real Loss Signal #2 (container item-count drop), telemetry-only. No FeatureFlag of its own —
            // matches Ownership/OwnershipLossWatch.cs's own precedent for a pure background safety-net watcher:
            // this should simply always run, not be a tunable behaviour (see ContainerLossWatch.cs remarks).
            // PatchSide.Both + an internal ZNet.instance.IsServer() gate for the same runtime-authority reason as
            // ServerCombatStats/ProductionHandoverGuard above (a listen host is a client-build process by
            // Side.IsDedicatedServerProcess but a real server once ZNet.instance exists).
            PatchRegistry.Register("Gameplay.ContainerLossWatch", typeof(ContainerLossWatch.Patch_Container_Save_ItemCountWatch), PatchSide.Both);
            PatchRegistry.Register("Gameplay.ContainerLossWatch", typeof(ContainerLossWatch.Patch_Container_OnDestroyed_ForgetBeforeDrop), PatchSide.Both);
        }
    }

    /// <summary>
    /// Dotted-access convenience wrapper (`docs/impl/CORE-API.md`'s own suggested pattern, e.g.
    /// <c>Features_Transport.Q2</c>) for this module's flags and raw tunables.
    /// </summary>
    internal static class Features_Gameplay
    {
        public static FeatureFlag ServerSpawning;
        public static ConfigEntry<int> ServerSpawningMaxZonesPerSecond;

        public static FeatureFlag ServerCombatStats;
        public static FeatureFlag HeadlessBiomeWind;
        public static FeatureFlag HeadlessNRESweep;
        public static FeatureFlag UpdateIsolation;

        public static FeatureFlag VehicleContainerHandover;
        public static ConfigEntry<float> QuiescenceSecondsConfig;

        public static FeatureFlag ProductionHandoverGuard;
        public static FeatureFlag StructureHandoverGuard;

        public static FeatureFlag DoorAutoClose;
        public static ConfigEntry<int> DoorAutoCloseIdleSeconds;
        public static ConfigEntry<int> DoorAutoCloseScanIntervalSeconds;
    }
}
