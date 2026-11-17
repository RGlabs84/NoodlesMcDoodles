using System;
using System.Collections.Generic;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using NoodlesMcDoodles.Core.ConfigFiles;
using NoodlesMcDoodles.Ownership;
using UnityEngine;

namespace NoodlesMcDoodles.ZoneSim
{
    /// <summary>
    /// WP2 (scratchpad/safety/spec.json) — the empty-ring preservation guards. Findings 1-3 (Audit A), plus a
    /// 4th gap Audit A's own combat analysis never covered (creature-vs-creature only, never creature-vs-piece)
    /// and closed in a fixer pass (2026-09-28, see Finding 4 below): once <c>ServerZoneSim</c>+
    /// <c>OwnershipAuthority</c> instantiate and claim the held band (the ring of zones the server holds with no
    /// connected peer anywhere near, <c>ZoneSim.HeldBandRingBonus</c>), several vanilla per-object behaviours
    /// that implicitly assumed "if I'm ticking at all, a player must be nearby" (true in vanilla, never true for
    /// the held band) start firing for real: <c>WearNTear.UpdateWear</c>'s weather/ash/lava/support-loss damage,
    /// <c>Ship.TakeAshlandsDamage</c>, <c>ItemDrop</c>'s 1-hour despawn timer, and (once <c>ServerSpawning</c> is
    /// on) wildlife combat against a parked tame or a player-built piece — none of which vanilla would ever have
    /// run for an unvisited base. This file freezes exactly those destructive/despawn branches for exactly that
    /// band, nothing else (production/creature-AI ticking itself, e.g. <c>Plant.Grow</c>, Smelter/Beehive output,
    /// stays untouched — deliberate "housekeeping while you're away" behaviour, not a bug).
    ///
    /// <para>
    /// <b>Scope, by POSITION not by ownership (corrects Audit A's own recommendation, per HANDOFF.md's binding
    /// merge note and spec.json's "risks" section — the single most consequential correction in that pass):</b>
    /// every guard predicate below is exactly <c>ZNet.instance.IsServer() &amp;&amp;
    /// ServerZoneMembership.IsServerHeld(ZoneSystem.GetZone(position))</c> — <see cref="Core.ServerZoneMembership"/>
    /// (O6/held-band) ONLY, NEVER <c>SharedAreaZoneHold</c> (O10). An O10-held object is, by construction, within
    /// some real connected player's own near-ring (O10 exists precisely for a player who IS present but laggy);
    /// suppressing normal damage/despawn there would suppress real gameplay in front of an actual player, a
    /// regression against "never worse than vanilla." <see cref="Core.ServerZoneMembership"/> zones were, before
    /// near-field claiming existed, always ≥~160m from every connected peer by O6's own construction (G3-N1
    /// padding) alone. <b>That is no longer true</b> the instant near-field claiming is live (the shipped
    /// FarOrShared default): <c>Ownership.ZoneClaimSweep.ComputeCandidateZones</c> puts a genuinely
    /// peer-overlapped, near-field-claimed zone into this exact same store (a real player IS present there —
    /// SAA's own point). <see cref="TryGetHeldBandZone"/> below therefore re-derives peer presence itself
    /// (<see cref="Ownership.ZoneClaimSweep.AnyPeerWithinPaddedActiveArea"/>) rather than trusting
    /// <see cref="Core.ServerZoneMembership"/> alone to still imply it (integrator fix, 2026-09-28 — closes the
    /// "KNOWN CROSS-MODULE HAZARD" the ownership area's own fixer pass flagged in <c>ZoneClaimSweep.cs</c>,
    /// right after <c>ComputeCandidateZones</c>, for whoever next touched this file). The vanilla invariant this
    /// reproduces (<c>IsInPeerActiveArea</c>/<c>OutsideActiveArea</c>) is what "held band" means here now: no
    /// connected peer whose own vanilla active area contains the object (1.0.11; through 1.0.10 it was the padded
    /// claim radius, ~160 m, wider than any player's game runs -- see <see cref="AnyPeerSimulatesHere"/>), full
    /// stop, regardless of which store recorded the claim.
    /// </para>
    ///
    /// <para>
    /// <b>What's frozen, per Finding, and the exact vanilla methods (ground truth: assembly_valheim_SERVER.decompiled.cs):</b>
    /// <list type="bullet">
    /// <item>Finding 1 — <c>WearNTear.UpdateWear(float)</c> (public, SERVER:149623): vanilla's own
    /// <c>OutsideActiveArea</c> early-return (SERVER:149631-149638) already skips every damage accumulator AND
    /// the trailing <c>UpdateVisual</c>/snow-sync by returning from the WHOLE method — this guard reproduces that
    /// exact full early-return effect for a held-band piece.</item>
    /// <item>Finding 1 — <c>Ship.TakeAshlandsDamage(float)</c> (private, SERVER:140625; only ever called from the
    /// owner-gated <c>CustomFixedUpdate</c>, SERVER:140491-140496 — <c>if ((bool)m_nview &amp;&amp;
    /// !m_nview.IsOwner()) return;</c> — so this is already scoped to an owned ship, exactly the case O6/
    /// ObjectFeed create). <c>UpdateWaterForce</c>'s own water-impact damage is deliberately NOT patched — it is
    /// gated <c>if (m_players.Count &gt; 0)</c> (SERVER:140701) and a held-band ship has zero players aboard by
    /// construction, so that path is already structurally inert there (Audit A's inclusion of it was checked and
    /// dropped, per spec.json's risks section).</item>
    /// <item>Finding 2 — <c>ItemDrop.TimedDestruction()</c> (private, SERVER:70298, called from
    /// <c>SlowUpdate</c>'s <c>m_autoDestroy</c> branch, SERVER:70266-70280): the method's own condition includes
    /// <c>!Player.IsPlayerInRange(position, 25f)</c> (SERVER:70299) — a real gate, but a tiny 25m one, already
    /// structurally irrelevant at held-band range (≥~160m from every peer by O6's own construction, see class
    /// remarks above) — the same "already inert there" treatment this file gives <c>Ship.UpdateWaterForce</c>'s
    /// <c>m_players.Count &gt; 0</c> gate just above, not a claim that no gate exists at all. The real reason
    /// vanilla never reaches even that 25m check for a held-band item is upstream of TimedDestruction entirely:
    /// vanilla's own ZNetScene never instantiates anything that far from every player in the first place, so the
    /// item (and its SlowUpdate) simply never exists there to tick. The held band instantiates it anyway (it's
    /// <c>Persistent</c>), so without this guard the 1-hour timer fires on items vanilla would never have
    /// simulated at all.</item>
    /// <item>Finding 3 — <c>Character.RPC_Damage(long, HitData)</c> (private, base Character class, SERVER:2413;
    /// self-gated <c>if (!m_nview.IsOwner()) return;</c> at SERVER:2424): suppresses damage to a defender with a
    /// <see cref="Tameable"/> component from a non-Player attacker while unattended in the held band. Wild-vs-wild
    /// combat (no <see cref="Tameable"/> on the defender) is deliberately left untouched — an ecology choice
    /// (userDecisions: "protect tames only... without turning the empty ring into an inert zoo"), not a
    /// data-loss requirement. <b>A Player attacker is an UNCONDITIONAL bypass</b> — this is the one guard in this
    /// file touching live combat resolution; a player can always damage their own or any tame normally, exactly
    /// as before, since the guard predicate can never be true where a player is present anyway (O6 construction),
    /// but the attacker-is-Player check is kept explicit and first regardless, as the acceptance criterion demands.</item>
    /// <item>Finding 4 (fixer pass, 2026-09-28) — <c>WearNTear.ApplyDamage(float, HitData)</c> (public, SERVER:
    /// 150348): the single choke point every damage source funnels through before <c>Destroy</c> — both the
    /// call inside <c>RPC_Damage</c> (private, SERVER:150293-150346; itself reached from the public
    /// <c>Damage(HitData)</c>/<c>IDestructible</c> entry point, SERVER:150276, that a hostile creature's
    /// <c>Attack</c> ultimately calls via <c>MonsterAI</c>'s distance-ungated static-target selection;
    /// self-gated <c>if (!m_nview.IsOwner())</c> at SERVER:150295 so it only ever runs on the machine that
    /// already owns the piece — the server, for anything held) and <c>UpdateWear</c>'s own decay
    /// call (SERVER:149788: <c>ApplyDamage(damage)</c>, already unreachable in the held band via the Finding-1
    /// guard above; suppressing it a second time here is harmless defense-in-depth, not double jeopardy).
    /// Suppresses (zero damage applied — the acceptance criterion's "cannot damage," not merely "cannot
    /// destroy") whenever the attacker is null-or-non-Player (same UNCONDITIONAL Player-attacker-bypass-first
    /// shape as Finding 3) while unattended in the held band. Deliberately targets <c>ApplyDamage</c>, not the
    /// public <c>Damage(HitData)</c> wrapper: AzuAntiCheat's <c>ACWNT_RPCDamage.Prefix</c> is a skip-capable
    /// Harmony prefix on that wrapper/RPC pair (DESIGN.md's conflict table) — patching two calls deeper avoids
    /// that patch-ordering hazard entirely rather than requiring an explicit <c>HarmonyPriority</c> against a
    /// third-party mod's own patch.</item>
    /// <item>Defense-in-depth regression catcher — <b>prefix</b> on <c>WearNTear.Destroy(HitData, bool)</c>
    /// (private, SERVER:150388; fixer pass, 2026-09-28 — this was a <c>Postfix</c>, which could never fire at
    /// all: see <see cref="Patch_WearNTear_Destroy_NoAttackerLossWatch"/>'s own remarks for why): a player-built
    /// piece (<c>ZDOVars.s_creator != 0</c>, the same signal <c>DoorAutoCloseScanner</c> already uses) about to
    /// be destroyed with no attacker or a non-Player attacker (the two cases Findings 1 and 4's own guards above
    /// exist to have already prevented) while its zone is server-held should be provably UNREACHABLE once those
    /// guards are correct — reports straight to <see cref="LossSignals"/> (a hard AutoStandDown trip from day
    /// one, per spec.json's lossSignals section and the user's own "no player built object destroyed"
    /// instruction) rather than mere telemetry.</item>
    /// <item>Defense-in-depth regression catcher, round-2 fix (2026-09-28) — the catcher above was itself
    /// structurally blind to <c>WearNTear.Remove()</c> (the hammer's ordinary "Remove piece" action):
    /// <c>RPC_Remove</c> (private, SERVER:150380) always calls <c>Destroy(null, blockDrop)</c> — ground truth
    /// confirms <c>hitData</c> is unconditionally null on this path, for every caller, regardless of who removed
    /// the piece — so the catcher's own Player-attacker bypass (<c>hitData?.GetAttacker()</c>) could never see a
    /// player there, leaving <see cref="TryGetHeldBandZone"/>'s peer-proximity re-derivation as the sole
    /// remaining gate. That depends on a peer's <c>ZNetPeer.m_refPos</c>, refreshed only by an unmodded client's
    /// own ~2s periodic report (see <see cref="PokeRingDriver.PeerReadyState"/>'s remarks) — never on
    /// teleport/respawn — so a player who portals home and immediately hits "Remove" on their own piece, before
    /// their post-portal position has been reported even once, could trip a false
    /// <c>heldBandWearNTearDestroyedNoAttacker</c> loss signal over completely ordinary play and stand the whole
    /// mod down. <see cref="Patch_WearNTear_RPCRemove_MarkPlayerInitiated"/> now marks the exact
    /// <see cref="WearNTear"/> instance a genuine <c>RPC_Remove</c> call is about to destroy — a signal
    /// <c>HitData</c> can never carry for this path — and the catcher treats that the same as a Player attacker:
    /// an unconditional bypass, checked first, right after the real one.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Self-contained (spec.json WP2): its own flag, its own config binding, its own patch registrations — zero
    /// edits to any other file's LOGIC. Wired from <see cref="ZoneSimConfig"/>.Init() (a one-line call, see that
    /// file) rather than its own <see cref="NoodlesModuleInitAttribute"/>, so every ZoneSim-domain flag stays
    /// bound from the one place this module already does that. Holds no cross-tick state of its own (no
    /// dictionaries, no held resources) — every check re-derives its answer fresh from <see cref="ServerZoneMembership"/>
    /// on every call, so there is nothing to release on a kill-switch/force-off transition: the instant
    /// <see cref="FeatureFlag.Active"/> goes false, every prefix below returns straight to vanilla on its very
    /// next call, with no separate <c>OnForcedOff</c>/flush-callback wiring needed. One narrow exception (round-2
    /// fix, 2026-09-28): <c>s_pendingRemoveInstance</c>, a same-call handoff between
    /// <see cref="Patch_WearNTear_RPCRemove_MarkPlayerInitiated"/> and
    /// <see cref="Patch_WearNTear_Destroy_NoAttackerLossWatch"/> (see either one's own remarks for why it needs
    /// to exist at all) — gated by the same flag on both ends, and always either null or consumed straight back
    /// to null by the very next relevant call on either patch, so it never survives across a stand-down and
    /// needs no separate release either.
    /// </para>
    /// </summary>
    internal static class HeldBandPreservationGuard
    {
        private static FeatureFlag s_flag;

        // Rate-limiting shape (WP1): a handful in full to Tier-1 (main log), more to Tier-2 (detail.log), both
        // bounded per spec.json's own "tons of logging but not spammy" instruction — held-band population can run
        // into the thousands of objects, so an unbounded per-object line here would be exactly the flood WP1's
        // rate limiter exists to prevent. One shared KEY per event TYPE (not per-object/ZDOID) is deliberate: it
        // keeps both rate-limiter tables' own size at a fixed handful of entries (one per guard below) regardless
        // of how many distinct objects the held band ever contains.
        private const int Tier1FirstN = 3;
        private const int Tier2FirstN = 20;
        private const float RateLimitWindowSeconds = 60f;

        private static readonly Counter s_wearDamageSuppressed = Telemetry.GetCounter("zonesim.wearDamageSuppressedHeldBand");
        private static readonly Counter s_shipAshlandsDamageSuppressed = Telemetry.GetCounter("zonesim.shipAshlandsDamageSuppressedHeldBand");
        private static readonly Counter s_itemDespawnSuppressed = Telemetry.GetCounter("zonesim.itemDespawnSuppressedHeldBand");
        private static readonly Counter s_tameCombatSuppressed = Telemetry.GetCounter("gameplay.tameCombatSuppressedHeldBand");
        private static readonly Counter s_pieceCombatSuppressed = Telemetry.GetCounter("zonesim.pieceCombatSuppressedHeldBand");

        /// <summary>Called once from <see cref="ZoneSimConfig"/>.Init(). Binds the config, registers the flag, registers every patch below.</summary>
        internal static void Init()
        {
            // 1.1.0: main file [3 - Server World] PreserveEmptyAreas (was [Features] HeldBandPreservationGuard.Active).
            // WP2 (spec.json): freezes exactly the destructive/despawn vanilla code paths (WearNTear weather/ash/lava/
            // support-loss damage, Ship Ashlands damage, ItemDrop's 1-hour despawn, wildlife-vs-tame and
            // wildlife-vs-piece combat) that only fire because ServerZoneSim+OwnershipAuthority instantiate and claim
            // the empty held ring nobody is near -- vanilla never simulates that ring (Findings 1-4). SCOPE:
            // ServerZoneMembership (O6/held-band) ONLY, never SharedAreaZoneHold (O10). Turning this OFF is NOT the
            // safe fallback while OwnershipAuthority/ServerZoneSim stay on. The Player-attacker bypass in both combat
            // guards is unconditional.
            var activeCfg = NoodlesConfig.PreserveEmptyAreas;
            s_flag = Features.Register("HeldBandPreservationGuard", activeCfg);

            RegisterPatches();
        }

        private static void RegisterPatches()
        {
            PatchRegistry.Register("HeldBandPreservationGuard", typeof(Patch_WearNTear_UpdateWear_HeldBandFreeze), PatchSide.Both);
            PatchRegistry.Register("HeldBandPreservationGuard", typeof(Patch_Ship_TakeAshlandsDamage_HeldBandFreeze), PatchSide.Both);
            PatchRegistry.Register("HeldBandPreservationGuard", typeof(Patch_ItemDrop_TimedDestruction_HeldBandFreeze), PatchSide.Both);
            PatchRegistry.Register("HeldBandPreservationGuard", typeof(Patch_Character_RPC_Damage_TameFreeze), PatchSide.Both);
            PatchRegistry.Register("HeldBandPreservationGuard", typeof(Patch_WearNTear_ApplyDamage_HeldBandFreeze), PatchSide.Both);
            PatchRegistry.Register("HeldBandPreservationGuard", typeof(Patch_WearNTear_RPCRemove_MarkPlayerInitiated), PatchSide.Both);
            PatchRegistry.Register("HeldBandPreservationGuard", typeof(Patch_WearNTear_Destroy_NoAttackerLossWatch), PatchSide.Both);
        }

        // --- Shared guard predicate -------------------------------------------------------------------------------

        private static bool IsServerRuntime() => ZNet.instance != null && ZNet.instance.IsServer();

        /// <summary>
        /// The one guard predicate every patch below uses. Position resolved via the object's own
        /// <see cref="ZNetView"/>/<see cref="ZDO"/> (never the live transform, matching the acceptance
        /// criterion's own wording), never also checking <c>SharedAreaZoneHold</c> (see class remarks). False
        /// (with default out-params) on anything that isn't even a valid, server-runtime ZDO -- callers treat
        /// that as "let vanilla decide," never as a suppression.
        ///
        /// <para>
        /// <b>Integrator fix, 2026-09-28 (closes a confirmed cross-module hazard flagged by the ownership area's
        /// own fixer pass, comment in <c>Ownership/ZoneClaimSweep.cs</c> right after <c>ComputeCandidateZones</c>):
        /// </b> <c>ServerZoneMembership.IsServerHeld</c> alone is checked FIRST as a cheap pre-filter, then this
        /// re-derives "no connected peer nearby" directly via
        /// <see cref="Ownership.ZoneClaimSweep.AnyPeerWithinPaddedActiveArea"/> rather than trusting the store's
        /// old (now false under the shipped FarOrShared default) implicit invariant that everything in it is
        /// ≥~160m from every peer. Without this, a zone O6 near-field-claims right next to an actively-defending
        /// player would wrongly freeze WearNTear/Ship/ItemDrop/tame damage there -- contradicting "where players
        /// are present, gameplay is normal" (battles damage builds). A null peer list is treated the same as "no
        /// peer" (still held-band) -- the safe direction, matching every other guard in this file's fail-open
        /// posture in the opposite direction (guard bugs fail OPEN to vanilla; a genuinely absent peer list is
        /// not a bug, it's the common case when no clients are connected at all).
        /// </para>
        /// </summary>
        private static bool TryGetHeldBandZone(GameObject go, out Vector3 position, out Vector2s zone)
        {
            position = default;
            zone = default;
            if (!IsServerRuntime()) return false;
            ZNetView znv = go.GetComponent<ZNetView>();
            if (znv == null || !znv.IsValid()) return false;
            position = znv.GetZDO().GetPosition();
            zone = ZoneSystem.GetZone(position);
            if (!ServerZoneMembership.IsServerHeld(zone)) return false;

            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            return peers == null || !AnyPeerSimulatesHere(position, peers);
        }

        /// <summary>
        /// 1.0.11 (live Proxima 2026-10-01, the vanished-portal report): "attended" now means inside some
        /// connected player's own VANILLA active area (<c>ZNetScene.InActiveArea</c>, the exact test vanilla uses
        /// to decide what a player's own game simulates), measured from either the peer's reported position or
        /// its live avatar's position, whichever is fresher. It used to be the padded claim radius
        /// (<see cref="ZoneClaimSweep.AnyPeerWithinPaddedActiveArea"/>, ~160 m from the zone centre), which is
        /// wider than what any player's game ever runs: between ~96 m and ~160 m from a player the server kept
        /// applying non-player damage (falling logs, creatures, fire) that vanilla would never have simulated at
        /// all, and the Destroy catcher below, sharing this predicate, stayed silent there. INIGO's new
        /// portal_wood was destroyed while server-held with INIGO ~130-160 m away (128 m+ per OwnershipLossWatch,
        /// inside the padded radius per the silent catcher). Where a player's own game would simulate,
        /// gameplay stays fully normal, exactly as before.
        /// </summary>
        private static bool AnyPeerSimulatesHere(Vector3 position, List<ZNetPeer> peers)
        {
            foreach (ZNetPeer peer in peers)
            {
                if (peer == null) continue;
                if (ZNetScene.InActiveArea(position, peer.GetRefPos())) return true;
                if (peer.m_characterID != ZDOID.None && ZDOMan.instance != null)
                {
                    ZDO avatar = ZDOMan.instance.GetZDO(peer.m_characterID);
                    if (avatar != null && ZNetScene.InActiveArea(position, avatar.GetPosition())) return true;
                }
            }
            return false;
        }

        /// <param name="mainLog">
        /// False only for the routine per-object wear freeze (1.0.5): live on Proxima it was ~400 lines plus 120
        /// "(+N more ...)" summaries in the main log over one 2-hour session with a single player, all expected
        /// behaviour with nothing for an operator to act on. It keeps its full detail.log tier and its telemetry
        /// counter (zonesim.wearDamageSuppressedHeldBand, in every telemetry line); the rarer guards below stay in both.
        /// </param>
        private static void RecordBlock(string key, string message, bool mainLog = true)
        {
            string line = "held-band preservation: " + message;
            // 1.1.0: every one of these is the guard doing its job (an item, piece, ship or tame PRESERVED), expected in
            // normal play and counted in telemetry -- main log at LogLevel=Verbose only (New Dawn: ~300 lines plus ~110
            // "(+N more ...)" summaries a day at Normal otherwise). detail.log keeps its own, larger tier below.
            if (mainLog && NoodlesLog.IsAtLeast(NoodlesLogLevel.Verbose))
                NoodlesLog.WarningRateLimited(key, line, Tier1FirstN, RateLimitWindowSeconds);
            NoodlesLog.DetailRateLimited(key, line, Tier2FirstN, RateLimitWindowSeconds);
        }

        // --- Finding 1a: WearNTear weather/ash/lava/support-loss damage -------------------------------------------

        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.UpdateWear))]
        internal static class Patch_WearNTear_UpdateWear_HeldBandFreeze
        {
            static bool Prefix(WearNTear __instance)
            {
                if (!s_flag.Active) return true;
                try
                {
                    if (!TryGetHeldBandZone(__instance.gameObject, out Vector3 pos, out Vector2s zone)) return true;

                    s_wearDamageSuppressed.Increment();
                    RecordBlock("heldband.wearntear.wear",
                        $"WearNTear.UpdateWear damage/wear accumulation skipped for '{__instance.gameObject.name}' " +
                        $"at {pos} (zone {zone}) -- unattended in the held band (Finding 1); resumes the instant a " +
                        "real peer's active area reaches it.", mainLog: false);
                    return false; // skip the ENTIRE original call -- reproduces vanilla's own OutsideActiveArea full early-return effect
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_WearNTear_UpdateWear_HeldBandFreeze), e);
                    return true; // fail open to vanilla -- never let a guard bug itself suppress real wear/damage bookkeeping
                }
            }
        }

        // --- Finding 1b: Ship Ashlands damage ----------------------------------------------------------------------

        [HarmonyPatch(typeof(Ship), "TakeAshlandsDamage")]
        internal static class Patch_Ship_TakeAshlandsDamage_HeldBandFreeze
        {
            static bool Prefix(Ship __instance)
            {
                if (!s_flag.Active) return true;
                try
                {
                    if (!TryGetHeldBandZone(__instance.gameObject, out Vector3 pos, out Vector2s zone)) return true;

                    s_shipAshlandsDamageSuppressed.Increment();
                    RecordBlock("heldband.ship.ashlands",
                        $"Ship.TakeAshlandsDamage skipped for '{__instance.gameObject.name}' at {pos} (zone {zone}) " +
                        "-- unattended in the held band (Finding 1); resumes the instant a real peer's active area reaches it.");
                    return false;
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_Ship_TakeAshlandsDamage_HeldBandFreeze), e);
                    return true;
                }
            }
        }

        // --- Finding 2: ItemDrop 1-hour despawn --------------------------------------------------------------------

        [HarmonyPatch(typeof(ItemDrop), "TimedDestruction")]
        internal static class Patch_ItemDrop_TimedDestruction_HeldBandFreeze
        {
            static bool Prefix(ItemDrop __instance)
            {
                if (!s_flag.Active) return true;
                try
                {
                    if (!TryGetHeldBandZone(__instance.gameObject, out Vector3 pos, out Vector2s zone)) return true;

                    s_itemDespawnSuppressed.Increment();
                    RecordBlock("heldband.itemdrop.despawn",
                        $"ItemDrop.TimedDestruction skipped for '{__instance.gameObject.name}' at {pos} (zone {zone}) " +
                        "-- unattended in the held band (Finding 2); item is preserved until a real peer's active area reaches it.");
                    return false;
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_ItemDrop_TimedDestruction_HeldBandFreeze), e);
                    return true;
                }
            }
        }

        // --- Finding 3: tame protection from non-player attackers -------------------------------------------------

        [HarmonyPatch(typeof(Character), "RPC_Damage")]
        internal static class Patch_Character_RPC_Damage_TameFreeze
        {
            static bool Prefix(Character __instance, HitData hit)
            {
                if (!s_flag.Active) return true;
                try
                {
                    // Wild-vs-wild is deliberately untouched (userDecisions: "protect tames only") -- only a
                    // defender with a Tameable component is in scope for this guard at all.
                    if (__instance.GetComponent<Tameable>() == null) return true;

                    // UNCONDITIONAL bypass, checked first and regardless of held-band state: a Player attacker
                    // must never be suppressed by this guard (acceptance criterion's own explicit requirement --
                    // this is the one guard in this file touching live combat resolution).
                    Character attacker = hit?.GetAttacker();
                    if (attacker != null && attacker.IsPlayer()) return true;

                    if (!TryGetHeldBandZone(__instance.gameObject, out Vector3 pos, out Vector2s zone)) return true;

                    s_tameCombatSuppressed.Increment();
                    RecordBlock("heldband.tame.combat",
                        $"Character.RPC_Damage skipped for tame '{__instance.gameObject.name}' at {pos} (zone {zone}) " +
                        "-- non-player attacker, unattended in the held band (Finding 3); a player can still damage " +
                        "this tame normally.");
                    return false; // no damage, no stagger, nothing -- see class remarks
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_Character_RPC_Damage_TameFreeze), e);
                    return true;
                }
            }
        }

        // --- Finding 4 (fixer pass): piece protection from non-player combat damage ------------------------------

        /// <summary>
        /// Closes the gap Audit A's own combat analysis never covered: it analyzed <c>Character.RPC_Damage</c>
        /// (creature-vs-creature, Finding 3 above) but never <c>WearNTear</c>'s own, completely separate,
        /// combat-damage chain, so a hostile creature spawned by <c>ServerSpawning</c> could freely destroy a
        /// player-built wall/door/ship/cart while unattended in the held band, with nothing in this file (or
        /// anywhere else in the codebase — confirmed by search) suppressing, mitigating or even detecting it.
        /// Targets <c>ApplyDamage</c> rather than the public <c>Damage(HitData)</c> wrapper or <c>RPC_Damage</c>
        /// itself: it is the one choke point both the combat call (<c>RPC_Damage</c>, SERVER:150348) and
        /// <c>UpdateWear</c>'s own decay call (SERVER:149788, already unreachable here via the Finding-1 guard
        /// above) funnel through, and patching it avoids the documented skip-capable conflict AzuAntiCheat's
        /// <c>ACWNT_RPCDamage.Prefix</c> has on the wrapper/RPC pair (DESIGN.md conflict table) — no
        /// <c>HarmonyPriority</c> coordination with that mod is needed because this simply isn't the same
        /// Harmony target method.
        /// </summary>
        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.ApplyDamage))]
        internal static class Patch_WearNTear_ApplyDamage_HeldBandFreeze
        {
            static bool Prefix(WearNTear __instance, float damage, HitData hitData, ref bool __result)
            {
                if (!s_flag.Active) return true;
                try
                {
                    // UNCONDITIONAL bypass, checked first and regardless of held-band state: a Player attacker
                    // must never be suppressed by this guard (same shape as Finding 3's tame guard) -- battles
                    // in front of a real player stay completely normal everywhere, exactly as before, since the
                    // guard predicate below can never be true where a player is present anyway (O6 construction).
                    // A null hitData (UpdateWear's own decay call) is treated the same as a non-Player attacker --
                    // neither one is ever a reason to exempt a hit from this guard.
                    Character attacker = hitData?.GetAttacker();
                    if (attacker != null && attacker.IsPlayer()) return true;

                    if (!TryGetHeldBandZone(__instance.gameObject, out Vector3 pos, out Vector2s zone)) return true;

                    s_pieceCombatSuppressed.Increment();
                    RecordBlock("heldband.wearntear.combat",
                        $"WearNTear.ApplyDamage skipped ({damage:F1} dmg) for '{__instance.gameObject.name}' at " +
                        $"{pos} (zone {zone}) -- non-player attacker, unattended in the held band (Finding 4); " +
                        "resumes the instant a real peer's active area reaches it.");
                    __result = false; // no damage applied at all -- matches the acceptance criterion's "cannot
                                       // damage," not merely "cannot destroy," and mirrors vanilla's own no-op
                                       // return (health already <= 0) since nothing here calls Set/Destroy either
                    return false; // skip the ENTIRE original call -- health untouched, Destroy never reached
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_WearNTear_ApplyDamage_HeldBandFreeze), e);
                    return true; // fail open to vanilla -- never let a guard bug itself suppress real combat resolution
                }
            }
        }

        // --- Defense-in-depth, round-2 fix (2026-09-28): mark a genuine RPC_Remove-originated destroy -------------

        /// <summary>
        /// Same-call handoff from <see cref="Patch_WearNTear_RPCRemove_MarkPlayerInitiated"/> to
        /// <see cref="Patch_WearNTear_Destroy_NoAttackerLossWatch"/> below: set by the former's Prefix, read (and
        /// unconditionally cleared) by the latter's Prefix on the very next <c>Destroy()</c> call system-wide —
        /// see either patch's own remarks for the full reasoning, including why a stale value here can never
        /// wrongly bypass an unrelated <c>Destroy()</c> call.
        /// </summary>
        private static WearNTear s_pendingRemoveInstance;

        /// <summary>
        /// Blocker fix (round-2 review): <c>WearNTear.Remove()</c> (public, SERVER:150372) — the hammer's
        /// ordinary "Remove piece" action, reached from <c>Player.RemovePiece</c>'s
        /// <c>component2.Remove();</c> (SERVER:12397) on any player-built piece — routes through
        /// <c>RPC_Remove</c> (private, SERVER:150380), whose entire body is
        /// <c>if (m_nview.IsValid() &amp;&amp; m_nview.IsOwner()) { Destroy(null, blockDrop); }</c>: <c>hitData</c>
        /// is UNCONDITIONALLY null on this path, for every caller, regardless of who removed the piece. The
        /// Destroy loss-watch's own Player-attacker bypass reads <c>hitData?.GetAttacker()</c>, so it can never
        /// see a player here — leaving <see cref="TryGetHeldBandZone"/>'s peer-proximity re-derivation as the
        /// ONLY remaining gate, and that depends on <c>ZNetPeer.m_refPos</c>, which an unmodded client refreshes
        /// only via its own ~2s periodic report (<see cref="PokeRingDriver.PeerReadyState"/>'s own remarks) —
        /// never on teleport/respawn. A player who portals home and immediately hits "Remove" on their own
        /// piece, before their post-portal position has been reported even once, could therefore trip a false
        /// <c>heldBandWearNTearDestroyedNoAttacker</c> loss signal over completely ordinary play and stand the
        /// whole mod down (<see cref="LossSignals.Report"/> — first occurrence, no rate limit, no second-strike
        /// requirement). This patch marks the exact <see cref="WearNTear"/> instance a genuine <c>RPC_Remove</c>
        /// call is about to destroy — a signal <c>HitData</c> can never carry for this path — so the catcher
        /// below can treat it exactly like a Player attacker: an unconditional bypass, checked first.
        /// <para>
        /// Single-threaded/synchronous, matching this file's own established model: <c>RPC_Remove</c> calls
        /// <c>Destroy()</c> directly within its own body, with nothing able to interleave in between, so there
        /// is no race window between the Prefix below setting <see cref="s_pendingRemoveInstance"/> and the
        /// Destroy prefix reading it for the very same instance.
        /// </para>
        /// </summary>
        [HarmonyPatch(typeof(WearNTear), "RPC_Remove")]
        internal static class Patch_WearNTear_RPCRemove_MarkPlayerInitiated
        {
            static void Prefix(WearNTear __instance)
            {
                if (!s_flag.Active) return;
                try { s_pendingRemoveInstance = __instance; }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_WearNTear_RPCRemove_MarkPlayerInitiated), e);
                }
            }

            /// <summary>
            /// Always runs after <c>RPC_Remove</c>'s own body has fully finished, including its own synchronous
            /// <c>Destroy()</c> call when one happens. Cleans up the two cases nothing else consumes: the
            /// original method's own <c>IsValid() &amp;&amp; IsOwner()</c> guard refusing to call
            /// <c>Destroy()</c> at all (a rare ownership-handoff race), and the feature flag being off when the
            /// Destroy prefix ran (so it returned before reaching its own consume step) — either way, without
            /// this, a stale reference could otherwise sit here and wrongly bypass a LATER, wholly unrelated
            /// <c>Destroy()</c> call on the very same object. Reference-equality-gated, so it only ever clears a
            /// value this same call itself set.
            /// </summary>
            static void Postfix(WearNTear __instance)
            {
                try
                {
                    if (ReferenceEquals(s_pendingRemoveInstance, __instance)) s_pendingRemoveInstance = null;
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_WearNTear_RPCRemove_MarkPlayerInitiated), e);
                }
            }
        }

        // --- Defense-in-depth: this should be provably unreachable ------------------------------------------------

        [HarmonyPatch(typeof(WearNTear), "Destroy")]
        internal static class Patch_WearNTear_Destroy_NoAttackerLossWatch
        {
            /// <summary>
            /// PREFIX, not Postfix (fixer-pass finding, 2026-09-28: this was a Postfix and could never fire, at
            /// all, including for the exact regression it exists to catch). Ground truth: <c>WearNTear.Destroy</c>'s
            /// own last statement is <c>ZNetScene.instance.Destroy(base.gameObject)</c> (SERVER:150426), which
            /// synchronously calls <c>ZNetScene.Destroy(GameObject)</c> (SERVER:82070) -&gt;
            /// <c>component.ResetZDO()</c> (SERVER:82076) -&gt; sets <c>m_zdo = null</c> (SERVER:82840) — BEFORE a
            /// Postfix on this method would ever run. <c>ZNetView.IsValid()</c> (SERVER:82828) reads <c>m_zdo !=
            /// null</c> first, so it is unconditionally false by the time a Postfix observes it: the
            /// "<c>if (znv == null || !znv.IsValid()) return;</c>" line below was true on every single
            /// invocation, silently, deterministically, forever. A Prefix runs before any of that, while
            /// <c>__instance</c>'s own <see cref="ZNetView"/>/<see cref="ZDO"/> are still fully live, so every
            /// read below now reflects real pre-destroy state. A <c>void</c> Prefix never blocks the original
            /// call — this only fixes WHEN the observation happens, never the destroy's own outcome.
            /// <para>
            /// Round-2 fix (2026-09-28), same method: also consumes <see cref="s_pendingRemoveInstance"/> (see
            /// <see cref="Patch_WearNTear_RPCRemove_MarkPlayerInitiated"/>) to recognize a genuine
            /// <c>WearNTear.Remove()</c>-originated destroy, which structurally never carries a <c>HitData</c>
            /// for the Player-attacker check just below to see.
            /// </para>
            /// </summary>
            static void Prefix(WearNTear __instance, HitData hitData)
            {
                // Conditioned on the guard being ACTIVE (spec.json lossSignals: "provably unreachable once WP2's
                // preservation guards are correct" assumes those prefixes are actually running) -- with the flag
                // off, held-band pieces take ordinary damage again (Findings 1/4 reopened, by the operator's own
                // choice), so a destroy there is expected, not a bug, and must never trip the kill switch.
                if (!s_flag.Active) return;
                try
                {
                    if (!IsServerRuntime()) return;

                    // Round-2 fix (2026-09-28): consumed unconditionally, before any of the checks below, so a
                    // stale value can never leak into an unrelated Destroy() call -- see
                    // Patch_WearNTear_RPCRemove_MarkPlayerInitiated's own remarks for the full reasoning.
                    bool viaRemove = ReferenceEquals(s_pendingRemoveInstance, __instance);
                    s_pendingRemoveInstance = null;

                    ZNetView znv = __instance.GetComponent<ZNetView>();
                    if (znv == null || !znv.IsValid()) return;
                    ZDO zdo = znv.GetZDO();

                    // A real Player attacker is always a legitimate destroy (a player razing a build with a
                    // hammer, anywhere) -- same UNCONDITIONAL bypass every guard in this file uses, checked
                    // first. Anything else -- no attacker at all (hitData==null, Finding 1's UpdateWear path) or
                    // a non-Player attacker (Finding 4's combat path) -- is exactly what those two preservation
                    // guards above exist to have already prevented while held-band and unattended.
                    Character attacker = hitData?.GetAttacker();
                    if (attacker != null && attacker.IsPlayer()) return;

                    // Round-2 fix (2026-09-28): WearNTear.Remove() (the hammer's ordinary "Remove piece" action)
                    // always calls Destroy(null, blockDrop) -- hitData is unconditionally null on that path, so
                    // the Player-attacker check just above can never see a player there. viaRemove is this
                    // catcher's only way to recognize that path; treat it exactly like a Player attacker -- same
                    // unconditional bypass, same reasoning (see the marking patch's own remarks).
                    if (viaRemove) return;

                    if (zdo.GetLong(ZDOVars.s_creator, 0L) == 0L) return; // not player-built -- same signal DoorAutoCloseScanner already uses

                    // Integrator fix, 2026-09-28: reuse TryGetHeldBandZone itself rather than a second inline
                    // copy of the ServerZoneMembership.IsServerHeld check, so this catcher's notion of "held
                    // band" can never drift from the corrected one the active suppression guards above now use
                    // (see that method's own remarks). Before this fix, a legitimate near-field-claimed-zone
                    // combat destroy -- now correctly vanilla-normal per the Finding 3/4 fix -- would have
                    // wrongly tripped this catcher too, standing the whole mod down over ordinary combat.
                    if (!TryGetHeldBandZone(__instance.gameObject, out Vector3 heldPos, out Vector2s zone)) return;

                    // Two distinct, separately-documented kinds (never renamed/merged): "NoAttacker" already
                    // means exactly hitData==null in docs/impl/ZONESIM-IMPL.md and any operator dashboard built
                    // against it -- widening ITS meaning instead of adding a second kind would quietly break
                    // that existing reference. The new kind is Finding 4's own combat-path counterpart.
                    bool noAttacker = hitData == null;
                    string kind = noAttacker ? "heldBandWearNTearDestroyedNoAttacker" : "heldBandWearNTearDestroyedNonPlayerAttacker";
                    string cause = noAttacker ? "no attacker (hitData==null), unattended decay (Finding 1)"
                        : $"non-player attacker '{attacker?.gameObject.name ?? "unknown"}', combat (Finding 4)";
                    LossSignals.Report(kind,
                        $"{zdo.m_uid} ('{__instance.gameObject.name}') is about to be destroyed on the server with " +
                        $"{cause} while player-built and unattended in held-band zone {zone} at {heldPos} " +
                        "-- this should be provably unreachable once the preservation guards above are correct; " +
                        "this catcher firing means one of them has a hole. Standing the mod down for a human to " +
                        "look, per the user's own 'no player built object destroyed' instruction.");
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_WearNTear_Destroy_NoAttackerLossWatch), e);
                }
            }
        }
    }
}
