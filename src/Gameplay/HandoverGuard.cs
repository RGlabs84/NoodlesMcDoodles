using System;
using System.Collections.Generic;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.Gameplay
{
    /// <summary>
    /// S2/S4/S7/S8-correctness (docs/design/05, Hooks H5-H7) — the single guard the Ownership module (docs/
    /// design/03) consults, through <see cref="ReclaimGate"/> (src/Core/Contracts.cs), before it ever takes an
    /// object away from a player who currently owns it. Registered from <see cref="GameplayInit"/>.
    /// <para>
    /// Safe default (Contracts.cs): with no guard active, <c>ReclaimGate.AllowReclaim</c> is always false — the
    /// Ownership module can never reclaim from a present player at all.
    /// </para>
    /// <para>
    /// <b>CORRECTED 2026-09-28 (lead + data-loss reviewer, confirmed independently).</b> <see cref="IsActive"/>
    /// is true whenever ANY of the three sub-flags is on, which satisfies <c>ReclaimGate</c>'s "at least one
    /// guard is active" gate and stops the "no active guard = refuse" fallback from applying AT ALL — but the
    /// previous <see cref="IsSafeToReclaim"/> only ran a category's checks when THAT category's own flag was
    /// on, and let every other category fall straight through to <c>return true</c>. With only
    /// <c>StructureHandoverGuard</c> on, for example, an open chest, a crewed ship, a ridden saddle, a working
    /// smelter or a creature mid-fight were all silently APPROVED for reclaim, because
    /// <c>VehicleContainerHandover</c>/<c>ProductionHandoverGuard</c> being off meant their categories were
    /// never even checked, not refused. <c>WearNTear</c> pieces were approved unconditionally whenever ANY flag
    /// was on, not just <c>StructureHandoverGuard</c>, despite the deferred mid-<c>UpdateSupport()</c> race.
    /// </para>
    /// <para>
    /// Fixed by failing CLOSED per category, not open: <see cref="IsSafeToReclaim"/> now identifies which
    /// category (if any) a ZDO belongs to FIRST, then checks that category's own flag — off means refuse
    /// outright ("&lt;category&gt; protection off"), on means run that category's existing checks exactly as
    /// before. Each of this guard's three sub-flags still independently gates its OWN participation (S4's
    /// Container/Vagon/Sadle/Ship checks; S2/S7's creature/production checks; S8's <c>WearNTear</c> race
    /// acceptance) — turning one on never silently extends protection to, or removes protection from, a
    /// category a DIFFERENT flag owns. <c>StructureHandoverGuard.Active</c> is no longer a pure status marker:
    /// turning it ON is what makes a <c>WearNTear</c> piece reclaimable at all, explicitly ACCEPTING the
    /// documented, deferred mid-<c>UpdateSupport()</c> race (H7 — routing itself is confirmed already safe;
    /// that race is the one residual this flag knowingly signs off on); leaving it off refuses every
    /// <c>WearNTear</c> reclaim outright, the safest possible default.
    /// </para>
    /// <para>
    /// <b>WP3 Finding 5 / SAA WP-C (2026-09-28).</b> <c>ItemStand</c>/<c>ArmorStand</c> (Finding 5) and
    /// <c>Turret</c>/<c>Sign</c> (SAA final.md WP-C — a prerequisite for a future
    /// <c>OwnershipAuthority.Mode=All</c>, not itself gated on it) previously matched none of the three
    /// categories above, so <see cref="IsSafeToReclaim"/> fell straight through to <c>true</c> for all four
    /// regardless of live state. None of the four has any "in use" signal of any kind on the decompile (checked
    /// directly — see <see cref="CheckVehicleAndContainer"/>'s own remarks) — all four now get the
    /// Quiescence-only backstop, gated by the same <c>VehicleContainerHandover</c> flag as Container/Vagon/
    /// Sadle/Ship.
    /// </para>
    /// </summary>
    internal static class HandoverGuard
    {
        private const string GuardOwnerName = "Gameplay.HandoverGuard";

        internal static void Register()
        {
            ReclaimGate.RegisterGuard(GuardOwnerName, IsActive, Veto);
        }

        private static bool IsActive() =>
            Features_Gameplay.VehicleContainerHandover.Active ||
            Features_Gameplay.ProductionHandoverGuard.Active ||
            Features_Gameplay.StructureHandoverGuard.Active;

        /// <summary>Returns a human-readable refusal reason, or null to allow. Never throws — ReclaimGate itself treats a throw as a refusal, but every branch here already fails closed on its own.</summary>
        private static string Veto(ZDO zdo)
        {
            try
            {
                return IsSafeToReclaim(zdo) ? null : LastRefusalReason;
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(HandoverGuard), e);
                return "guard threw (see log) — refusing to reclaim";
            }
        }

        // Set immediately before IsSafeToReclaim returns false, read immediately after by Veto — both run
        // synchronously on the main thread within the same ReclaimGate.AllowReclaim call, never interleaved
        // with another zdo's check, so a plain static field (not per-call state) is sufficient here.
        private static string LastRefusalReason;

        // RUNTIME REVIEW FIX (2026-09-28, MINOR): every one of these previously went through a helper that
        // built a fresh interpolated string (and, for the inUse*/activeSimulation ones, a Substring/ToLower
        // pair) on every single call — IsSafeToReclaim can run once per ZDO per reclaim-sweep tick, so this
        // was a real per-refusal allocation on a path with no reason to allocate at all.
        // Telemetry.GetCounter(name) already caches by name internally (a ConcurrentDictionary), but resolving
        // the SAME fixed, small set of names here once, at class load, skips both the string-building and the
        // lookup on every call after the first.
        private static readonly Counter s_categoryOffVehicleContainer = Telemetry.GetCounter("gameplay.handoverGuard.reclaimBlocked.categoryProtectionOff.vehicleContainer");
        private static readonly Counter s_categoryOffProduction = Telemetry.GetCounter("gameplay.handoverGuard.reclaimBlocked.categoryProtectionOff.production");
        private static readonly Counter s_categoryOffStructure = Telemetry.GetCounter("gameplay.handoverGuard.reclaimBlocked.categoryProtectionOff.structure");

        private static readonly Counter s_inUseContainerFastPath = Telemetry.GetCounter("gameplay.handoverGuard.reclaimBlocked.inUseContainer.fastPath");
        private static readonly Counter s_inUseContainerBackstop = Telemetry.GetCounter("gameplay.handoverGuard.reclaimBlocked.inUseContainer.quiescenceBackstop");
        private static readonly Counter s_inUseVagonFastPath = Telemetry.GetCounter("gameplay.handoverGuard.reclaimBlocked.inUseVagon.fastPath");
        private static readonly Counter s_inUseVagonBackstop = Telemetry.GetCounter("gameplay.handoverGuard.reclaimBlocked.inUseVagon.quiescenceBackstop");
        private static readonly Counter s_inUseSadleFastPath = Telemetry.GetCounter("gameplay.handoverGuard.reclaimBlocked.inUseSadle.fastPath");
        private static readonly Counter s_inUseShipFastPath = Telemetry.GetCounter("gameplay.handoverGuard.reclaimBlocked.inUseShip.fastPath");

        // WP3 Finding 5 / SAA WP-C (final.md §8): none of these four have any live "in use" signal at all
        // (confirmed against the decompile -- see CheckVehicleAndContainer's own remarks at each check), so
        // there is no fast path for any of them, only the Quiescence backstop -- same shape as Container's
        // own backstop-only path.
        private static readonly Counter s_inUseItemStandBackstop = Telemetry.GetCounter("gameplay.handoverGuard.reclaimBlocked.inUseItemStand.quiescenceBackstop");
        private static readonly Counter s_inUseArmorStandBackstop = Telemetry.GetCounter("gameplay.handoverGuard.reclaimBlocked.inUseArmorStand.quiescenceBackstop");
        private static readonly Counter s_inUseTurretBackstop = Telemetry.GetCounter("gameplay.handoverGuard.reclaimBlocked.inUseTurret.quiescenceBackstop");
        private static readonly Counter s_inUseSignBackstop = Telemetry.GetCounter("gameplay.handoverGuard.reclaimBlocked.inUseSign.quiescenceBackstop");
        private static readonly Counter s_activeSimulationFastPath = Telemetry.GetCounter("gameplay.handoverGuard.reclaimBlocked.activeSimulation.fastPath");
        private static readonly Counter s_activeSimulationBackstop = Telemetry.GetCounter("gameplay.handoverGuard.reclaimBlocked.activeSimulation.quiescenceBackstop");

        /// <summary>
        /// Core decision function, also usable directly (not just through <see cref="ReclaimGate"/>) — kept as
        /// a separate testable entry point per the blueprint's own sketch shape.
        /// </summary>
        internal static bool IsSafeToReclaim(ZDO zdo)
        {
            LastRefusalReason = null;
            if (zdo == null) return false;

            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
            if (prefab == null)
            {
                // Z3's missing-prefab-deletion guard (ZNetScene.CreateObjectsSorted destroying any ZDO it can't
                // instantiate, SERVER:82184-82188) applies transitively here — if we can't even resolve what
                // this object IS, refuse rather than guess.
                LastRefusalReason = "prefab not resolvable";
                return false;
            }

            // CORRECTED 2026-09-28: category detection FIRST, then that category's own flag decides whether
            // it's reclaimable at all — never silently fall through to `true` just because a DIFFERENT
            // category's flag happens to be on. An object matching none of the three categories below (a plain
            // decoration, terrain edit, etc.) is genuinely outside this guard's scope and falls through to
            // `true` at the end, same as always — that is not the bug; approving an in-use Container/Ship/
            // Smelter/creature/WearNTear piece because an UNRELATED flag was on was.

            if (IsVehicleOrContainerCategory(prefab))
            {
                if (!Features_Gameplay.VehicleContainerHandover.Active)
                {
                    s_categoryOffVehicleContainer.Increment();
                    LastRefusalReason = "vehicle/container protection off";
                    return false;
                }
                if (!CheckVehicleAndContainer(zdo, prefab, out string reason)) { LastRefusalReason = reason; return false; }
            }

            if (IsProductionOrCreatureCategory(prefab))
            {
                if (!Features_Gameplay.ProductionHandoverGuard.Active)
                {
                    s_categoryOffProduction.Increment();
                    LastRefusalReason = "production/creature protection off";
                    return false;
                }
                if (!CheckProductionAndCreature(zdo, prefab, out string reason)) { LastRefusalReason = reason; return false; }
            }

            if (prefab.GetComponent<WearNTear>() != null)
            {
                if (!Features_Gameplay.StructureHandoverGuard.Active)
                {
                    s_categoryOffStructure.Increment();
                    LastRefusalReason = "structure protection off";
                    return false;
                }
                // StructureHandoverGuard is explicitly ON: this operator has accepted the documented, deferred
                // mid-UpdateSupport() race (H7 — WearNTear/Door/RPC_ClearCachedSupport routing itself is
                // confirmed already safe regardless of current owner; targeted RPCs, not broadcast). No
                // ZDO-replicated signal fine-grained enough to catch a force-claim mid-recalculation exists
                // (QuiescenceTracker is deliberately coarse, seconds not sub-second) — see docs/design/05 H7 and
                // docs/impl/GAMEPLAY-IMPL.md. This branch therefore never itself vetoes past this point; the
                // only thing standing between that race and the world is the ownership subsystem never
                // force-claiming a WearNTear piece outside its own clean-relinquish contract, a dependency on
                // that subsystem, not a guard this file provides.
            }

            return true;
        }

        private static bool IsVehicleOrContainerCategory(GameObject prefab) =>
            prefab.GetComponent<Container>() != null || prefab.GetComponent<Vagon>() != null ||
            prefab.GetComponent<Sadle>() != null || prefab.GetComponent<Ship>() != null ||
            // WP3 Finding 5 / SAA WP-C (final.md §1/§8: "All adds the B1/B2 transfer-on-interact classes
            // (Container, ItemStand, ArmorStand, Vagon, Sadle, Turret, Sign, ItemDrop)") -- these four share
            // the same transfer-on-interact shape (a live player's own RPC_RequestOwn/interact routes through
            // vanilla's own ownership machinery, same as Container) as the rest of this category, so they
            // belong in it rather than a new one; see CheckVehicleAndContainer for what each one actually gets.
            prefab.GetComponent<ItemStand>() != null || prefab.GetComponent<ArmorStand>() != null ||
            prefab.GetComponent<Turret>() != null || prefab.GetComponent<Sign>() != null;

        private static bool IsProductionOrCreatureCategory(GameObject prefab) =>
            prefab.GetComponent<Smelter>() != null || prefab.GetComponent<Character>() != null;

        // --- H5: Container / Vagon / Sadle / Ship ---------------------------------------------------------------

        private static readonly AccessTools.FieldRef<Ship, List<Player>> s_shipPlayers =
            AccessTools.FieldRefAccess<Ship, List<Player>>("m_players");

        private static bool CheckVehicleAndContainer(ZDO zdo, GameObject prefab, out string reason)
        {
            reason = null;
            float quiescence = Features_Gameplay.QuiescenceSecondsConfig.Value;

            if (prefab.GetComponent<Container>() != null)
            {
                ZNetView live = ZNetScene.instance.FindInstance(zdo);
                Container liveContainer = live != null ? live.GetComponent<Container>() : null;
                if (liveContainer != null && liveContainer.IsInUse()) // SERVER:121983-121992 — fast path, server can see it locally
                {
                    s_inUseContainerFastPath.Increment();
                    reason = "container in use (local)";
                    return false;
                }
                Vagon wagon = liveContainer != null ? liveContainer.GetComponentInParent<Vagon>() : null;
                if (wagon != null && wagon.InUse()) // SERVER:147563-147570 — a cart's own container, same fast path
                {
                    s_inUseVagonFastPath.Increment();
                    reason = "parent wagon in use (local)";
                    return false;
                }
                if (!QuiescenceTracker.IsQuiescent(zdo, quiescence)) // mandatory backstop — see QuiescenceTracker remarks
                {
                    s_inUseContainerBackstop.Increment();
                    reason = "container not quiescent (remote/unknown owner)";
                    return false;
                }
            }

            // Standalone Vagon ZDO (as opposed to reaching a Vagon via a Container child's parent lookup above)
            // — added 2026-09-28 alongside the category-detection fix, since IsVehicleOrContainerCategory now
            // names Vagon explicitly (matching the lead's own enumeration) and a Vagon-typed prefab must not
            // silently fall through to `true` at the end of this method with no check of its own. Vagon.InUse()
            // (SERVER:147563-147570) covers both "someone is pulling it" (IsAttached(), physics/joint state,
            // owner-agnostic like Ship's m_players) and "its own container has items being edited"
            // (m_container.IsInUse(), the SAME owner-gated local flag the Container branch above needs a
            // backstop for) — so this gets the same QuiescenceTracker backstop as Container, not the
            // no-backstop treatment Sadle/Ship get.
            if (prefab.GetComponent<Vagon>() != null)
            {
                ZNetView live = ZNetScene.instance.FindInstance(zdo);
                Vagon liveWagon = live != null ? live.GetComponent<Vagon>() : null;
                if (liveWagon != null && liveWagon.InUse())
                {
                    s_inUseVagonFastPath.Increment();
                    reason = "wagon in use (local)";
                    return false;
                }
                if (!QuiescenceTracker.IsQuiescent(zdo, quiescence))
                {
                    s_inUseVagonBackstop.Increment();
                    reason = "wagon not quiescent (remote/unknown owner)";
                    return false;
                }
            }

            if (prefab.GetComponent<Sadle>() != null)
            {
                ZNetView live = ZNetScene.instance.FindInstance(zdo);
                Sadle sadle = live != null ? live.GetComponent<Sadle>() : null;
                // SERVER:18255-18270 — CalculateHaveValidUser() recomputes every FixedUpdate from
                // ZNet.instance.GetAllCharacterZDOS(), globally-replicated data, not owner-local state. Any
                // machine with this GameObject instantiated computes the true answer regardless of who owns the
                // ZDO — no QuiescenceTracker backstop needed (blueprint H5, "Correction to Red Team 1's scope").
                if (sadle != null && sadle.HaveValidUser())
                {
                    s_inUseSadleFastPath.Increment();
                    reason = "saddle has a valid user";
                    return false;
                }
            }

            if (prefab.GetComponent<Ship>() != null)
            {
                ZNetView live = ZNetScene.instance.FindInstance(zdo);
                Ship ship = live != null ? live.GetComponent<Ship>() : null;
                // SERVER:140293/140898-140922 — m_players is populated by OnTriggerEnter/OnTriggerExit, plain
                // Unity physics callbacks with no IsOwner() gate. Once locally instantiated, ANY machine
                // (including the server) computes true occupancy from real physics, regardless of ZDO
                // ownership — again no backstop needed.
                List<Player> players = ship != null ? s_shipPlayers(ship) : null;
                if (players != null && players.Count > 0)
                {
                    s_inUseShipFastPath.Increment();
                    reason = "ship has players aboard (local physics)";
                    return false;
                }
            }

            // WP3 Finding 5 (ItemStand/ArmorStand) + SAA WP-C (adds Turret/Sign, final.md §8, "prerequisite for
            // Mode=All") -- checked directly against the decompile this pass, not assumed:
            // - ItemStand (SERVER:131361 area): Awake registers RPC_DropItem/RPC_UpdateVisual/RPC_RequestOwn/
            //   RPC_DestroyAttachment/RPC_SetVisualItem. No IsInUse()-shaped bool anywhere on the type.
            // - ArmorStand (SERVER:118827 area): Awake registers RPC_DropItem/RPC_DropItemByName/RPC_RequestOwn/
            //   RPC_DestroyAttachment/RPC_SetVisualItem/RPC_SetPose. Same: no live "in use" signal.
            // - Turret (SERVER:146525 area): has real per-tick live state (m_target/m_haveTarget, its own
            //   FixedUpdate targeting loop, RPC_AddAmmo/RPC_SetTarget) but that loop is not wired to
            //   LivenessBookkeeper's fast path in this pass (LivenessBookkeeper.cs is out of this file's own
            //   scope) -- no fast path available here either.
            // - Sign (SERVER:141636 area): no "in use" concept at all beyond the SetText/UpdateText RPC itself.
            // All four therefore get the Quiescence-only backstop, same shape as Container's own backstop path
            // above -- still strictly safer than the previous unconditional `return true` fallthrough these
            // four types got before this fix (they matched none of the old category checks, so IsSafeToReclaim
            // fell straight through to `true` for them, category-detection-first fix or not).
            if (prefab.GetComponent<ItemStand>() != null && !QuiescenceTracker.IsQuiescent(zdo, quiescence))
            {
                s_inUseItemStandBackstop.Increment();
                reason = "item stand not quiescent (remote/unknown owner)";
                return false;
            }

            if (prefab.GetComponent<ArmorStand>() != null && !QuiescenceTracker.IsQuiescent(zdo, quiescence))
            {
                s_inUseArmorStandBackstop.Increment();
                reason = "armor stand not quiescent (remote/unknown owner)";
                return false;
            }

            if (prefab.GetComponent<Turret>() != null && !QuiescenceTracker.IsQuiescent(zdo, quiescence))
            {
                s_inUseTurretBackstop.Increment();
                reason = "turret not quiescent (remote/unknown owner)";
                return false;
            }

            if (prefab.GetComponent<Sign>() != null && !QuiescenceTracker.IsQuiescent(zdo, quiescence))
            {
                s_inUseSignBackstop.Increment();
                reason = "sign not quiescent (remote/unknown owner)";
                return false;
            }

            return true;
        }

        // --- H6: production (Smelter) + creatures (Character/BaseAI) -------------------------------------------

        private static bool CheckProductionAndCreature(ZDO zdo, GameObject prefab, out string reason)
        {
            reason = null;
            float quiescence = Features_Gameplay.QuiescenceSecondsConfig.Value;

            // Only Smelter is wired to the fast-path LivenessBookkeeper postfix in this pass. Fermenter and
            // Beehive were checked directly against the decompile (not assumed): Fermenter.SlowUpdate is pure
            // visual state (SetActive calls only, no Instantiate, not owner-gated) and Beehive.UpdateBees only
            // accumulates a counter — the actual Instantiate-backed output for both (Fermenter.RPC_Tap,
            // Beehive.RPC_Extract) is a targeted, untargeted-string InvokeRPC that vanilla itself routes to the
            // ZDO's current owner (the same routing-is-already-safe shape H7 confirms for Door), not a
            // dual-owner InvokeRepeating race the way Smelter.UpdateSmelter is — so they do not need this
            // branch's guard the way Smelter does. Windmill has no Instantiate-backed output at all. A future
            // production type should be checked against this same "owner-gated InvokeRepeating tick with
            // Instantiate output" shape before assuming it does or doesn't belong here (standing audit note,
            // matching H1/H4's own anchor-check discipline).
            if (prefab.GetComponent<Smelter>() != null)
            {
                if (LivenessBookkeeper.TickedRecently(zdo)) { s_activeSimulationFastPath.Increment(); reason = "smelter ticked recently (local)"; return false; }
                if (!QuiescenceTracker.IsQuiescent(zdo, quiescence)) { s_activeSimulationBackstop.Increment(); reason = "production not quiescent (remote/unknown owner)"; return false; }
            }

            if (prefab.GetComponent<Character>() != null)
            {
                if (LivenessBookkeeper.TickedRecently(zdo)) { s_activeSimulationFastPath.Increment(); reason = "creature ticked recently (local)"; return false; }
                if (!QuiescenceTracker.IsQuiescent(zdo, quiescence)) { s_activeSimulationBackstop.Increment(); reason = "creature not quiescent (remote/unknown owner)"; return false; }
            }

            return true;
        }

        /// <summary>Kill-switch flush (DESIGN.md P4 x G6-N3 coupling) — see GameplayInit. This guard itself holds
        /// no live state of its own (it's a pure function of ZDO + the trackers below), but the trackers it
        /// reads do, and must forget everything so a mid-flip disable can never leave an object permanently
        /// stuck: they simply start fresh ("just changed, treat as active") the moment they're consulted again.</summary>
        internal static void FlushOnDisable()
        {
            QuiescenceTracker.Clear();
            LivenessBookkeeper.Clear();
        }
    }
}
