using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.Gameplay
{
    /// <summary>
    /// S6 (docs/design/05, Hook H4) — six independent, unconditional-when-on null-guard transpilers, server-only
    /// (H4.1-H4.5 from the design; H4.6, Ship.UpdateSailSize, added in 1.0.5 after it fired live).
    /// Every one exists for the same reason: an unguarded <c>Player.m_localPlayer</c> dereference on a
    /// dedicated server (where it is permanently null) either (a) aborts a method AFTER it already produced a
    /// real side effect but BEFORE it clears the state that prevents that side effect repeating (duplication),
    /// or (b) aborts a method whose entire purpose is a broadcast to every connected client, silently, before
    /// that broadcast is sent — <c>ZRoutedRpc.InvokeRoutedRPC</c>'s self-invoke-before-route order (confirmed
    /// this pass, SERVER:83623-83635: <c>if (targetPeerID==m_id||targetPeerID==0L) HandleRoutedRPC(...); if
    /// (targetPeerID!=m_id) RouteRPC(...);</c>, <c>Everybody==0L</c>) means the LOCAL handler runs first,
    /// unguarded, with no try/catch anywhere in the call chain — an NRE there aborts before <c>RouteRPC</c> ever
    /// runs, dropping the broadcast for every peer, not just crashing the server's own log.
    /// <para>
    /// All six ship together under <see cref="Features_Gameplay.HeadlessNRESweep"/>, default ON (strictly
    /// protective — fixes a real vanilla bug, never changes gameplay, only prevents a crash/duplication/
    /// broadcast-drop path that exists even before any other lever in this module is enabled).
    /// </para>
    /// </summary>
    internal static class NREGuards
    {
        private static readonly MethodInfo s_getTransform =
            AccessTools.PropertyGetter(typeof(Component), nameof(Component.transform));
        private static readonly MethodInfo s_getPosition =
            AccessTools.PropertyGetter(typeof(Transform), nameof(Transform.position));
        private static readonly FieldInfo s_playerLocalPlayer =
            AccessTools.Field(typeof(Player), nameof(Player.m_localPlayer));

        /// <summary>
        /// 1.1.0 (New Dawn log, 14 boots a day): when an S6 anchor is missing because the method no longer reads
        /// <c>Player.m_localPlayer</c> AT ALL, another mod has already made it headless-safe -- on New Dawn,
        /// LetItGrow's own <c>Pickable_RPC_Pick_HeadlessPatch</c> replaces exactly the
        /// <c>Player.m_localPlayer.GetZDOID()</c> pair S6.2 anchors on, so S6.2's transpiler (running after it in the
        /// chain) found nothing to replace. That used to log an Error and ForceOff the whole HeadlessNRESweep
        /// feature, silently disabling the five unrelated guards too, on every boot. With no unguarded read left there
        /// is nothing for this guard to protect: say so at Verbose and leave the feature on. Any other anchor miss
        /// (the read is still there but the IL around it changed) keeps the old Error + ForceOff path.
        /// </summary>
        private static bool AlreadyHeadlessSafe(IEnumerable<CodeInstruction> instructions, string tag, string method)
        {
            try
            {
                foreach (CodeInstruction ci in instructions)
                {
                    if (ci.opcode == OpCodes.Ldsfld && Equals(ci.operand, s_playerLocalPlayer)) return false;
                }
                Telemetry.GetCounter("gameplay.s6.alreadyGuardedElsewhere").Increment();
                NoodlesLog.Verbose($"{tag}: {method} no longer reads Player.m_localPlayer (another mod's headless " +
                    "guard already replaced it), so there is nothing left to guard -- leaving it as is; the other " +
                    "S6 guards stay on.");
                return true;
            }
            catch
            {
                return false; // can't tell -- fall back to the original Error + ForceOff handling
            }
        }

        /// <summary>
        /// Shared by H4.4/H4.5: both replace the identical 3-instruction
        /// <c>ldsfld Player::m_localPlayer; callvirt Component::get_transform(); callvirt Transform::get_position()</c>
        /// sequence (confirmed identical IL shape both sites, both builds) with a call to
        /// <paramref name="safePositionMethod"/> — same stack effect (one <c>Vector3</c> pushed), so the
        /// existing distance/subtraction code immediately after each site is untouched.
        /// </summary>
        private static IEnumerable<CodeInstruction> ReplaceLocalPlayerPositionFetch(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodInfo safePositionMethod)
        {
            var m = new CodeMatcher(instructions, generator);
            m.MatchStartForward(
                 new CodeMatch(OpCodes.Ldsfld, s_playerLocalPlayer),
                 new CodeMatch(OpCodes.Callvirt, s_getTransform),
                 new CodeMatch(OpCodes.Callvirt, s_getPosition))
             .ThrowIfInvalid("Player.m_localPlayer.transform.position fetch not found");

            var replacement = new CodeInstruction(OpCodes.Call, safePositionMethod);
            var old = m.Instruction;
            if (old.labels != null && old.labels.Count > 0) replacement.labels.AddRange(old.labels);
            if (old.blocks != null && old.blocks.Count > 0) replacement.blocks.AddRange(old.blocks);

            m.SetInstructionAndAdvance(replacement);
            m.RemoveInstructions(2);
            return m.InstructionEnumeration();
        }

        // A finite, far-outside-any-real-check-radius sentinel — never a real gameplay position, only ever
        // consumed by a "< some radius" comparison this same guard forces to read false. Not float.MaxValue:
        // both call sites subtract this from a real, finite base position (Vector3.Distance / Utils.DistanceXZ),
        // and float.MaxValue minus a finite value can overflow to Infinity, which is well-defined for a `<`
        // comparison but needlessly exotic; a merely-huge finite value avoids that with no behavioural cost.
        private static readonly Vector3 FarAwaySentinel = new Vector3(1e9f, 0f, 1e9f);

        // --- H4.1: CookingStation.SpawnItem -------------------------------------------------------------------

        /// <summary>
        /// SERVER:122713/CLIENT:123629. <c>Instantiate</c>+<c>ItemDrop.OnCreateNew</c> already ran before the
        /// unguarded <c>m_recordCrafter</c> block (SERVER:122733-122742), and <c>RPC_RemoveDoneItem</c>
        /// (SERVER:122638-122654) calls <c>SpawnItem</c> in a loop, clearing the slot only AFTER the loop
        /// returns — an NRE here means <c>SetSlot</c> never runs, <c>IsItemDone</c> stays true, and the next
        /// retry re-enters the loop and Instantiates another copy of an already-produced item: unbounded
        /// duplication, not a missed craft. Guard scope: only the <c>m_recordCrafter</c> block's entry
        /// condition — the trailing <c>IncrementStatItemCraft</c> call reads <c>Game.m_playerProfile</c>,
        /// unconditionally constructed at server startup (SERVER:99923), so it cannot NRE and needs no guard.
        /// </summary>
        internal static bool GuardRecordCrafter(bool recordCrafter)
        {
            if (!recordCrafter) return false;
            if (!Features_Gameplay.HeadlessNRESweep.Active) return true; // vanilla: proceed, and NRE if null, exactly as today
            if (Player.m_localPlayer != null) return true;
            Telemetry.GetCounter("gameplay.s6.guardedCookingStation").Increment();
            return false; // skip only the crafter-ID/name credit; the item itself was already spawned above
        }

        [HarmonyPatch(typeof(CookingStation), "SpawnItem")]
        internal static class Patch_CookingStation_SpawnItem
        {
            [HarmonyTranspiler]
            internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            {
                try
                {
                    var recordCrafterField = AccessTools.Field(typeof(CookingStation), "m_recordCrafter");
                    var guard = AccessTools.Method(typeof(NREGuards), nameof(GuardRecordCrafter));

                    var m = new CodeMatcher(instructions, generator);
                    m.MatchStartForward(new CodeMatch(OpCodes.Ldfld, recordCrafterField))
                     .ThrowIfInvalid("CookingStation.m_recordCrafter field read not found");
                    // Pure insert right after the existing `ldfld` and before the existing `brfalse` — neither
                    // of those two untouched instructions carries an incoming label (confirmed this pass: the
                    // two branches that DO target this region land on the preceding `ldarg.0`, not here), so no
                    // label-preservation bookkeeping is needed for an insert (nothing is replaced/removed).
                    m.Advance(1).InsertAndAdvance(new CodeInstruction(OpCodes.Call, guard));
                    return m.InstructionEnumeration();
                }
                catch (Exception e)
                {
                    if (AlreadyHeadlessSafe(instructions, "S6.1", "CookingStation.SpawnItem")) return instructions;
                    NoodlesLog.Error($"S6.1: CookingStation.SpawnItem anchor not found, leaving unpatched: {e.Message}");
                    Features_Gameplay.HeadlessNRESweep.ForceOff($"CookingStation.SpawnItem transpiler anchor missing: {e.GetType().Name}");
                    return instructions;
                }
            }
        }

        // --- H4.2: Pickable.RPC_Pick --------------------------------------------------------------------------

        /// <summary>
        /// SERVER:71071. <c>m_pickEffector.Create(..., Player.m_localPlayer.GetZDOID())</c> runs BEFORE the
        /// <c>Drop(...)</c> loop — an NRE here silently eats the harvest with the client's optimistic pick flag
        /// already fired. Guard substitutes <c>ZDOID.None</c> for the effect-credit argument only; the
        /// <c>Drop</c> loop itself is never touched.
        /// </summary>
        internal static ZDOID SafePickerZDOID()
        {
            if (!Features_Gameplay.HeadlessNRESweep.Active) return Player.m_localPlayer.GetZDOID(); // vanilla: NRE if null, exactly as today
            if (Player.m_localPlayer != null) return Player.m_localPlayer.GetZDOID();
            Telemetry.GetCounter("gameplay.s6.guardedPickable").Increment();
            return ZDOID.None;
        }

        [HarmonyPatch(typeof(Pickable), "RPC_Pick")]
        internal static class Patch_Pickable_RPC_Pick
        {
            [HarmonyTranspiler]
            internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            {
                try
                {
                    var getZdoid = AccessTools.Method(typeof(Character), nameof(Character.GetZDOID));
                    var safe = AccessTools.Method(typeof(NREGuards), nameof(SafePickerZDOID));

                    var m = new CodeMatcher(instructions, generator);
                    m.MatchStartForward(
                         new CodeMatch(OpCodes.Ldsfld, s_playerLocalPlayer),
                         new CodeMatch(OpCodes.Callvirt, getZdoid))
                     .ThrowIfInvalid("Player.m_localPlayer.GetZDOID() not found");

                    var replacement = new CodeInstruction(OpCodes.Call, safe);
                    var old = m.Instruction;
                    if (old.labels != null && old.labels.Count > 0) replacement.labels.AddRange(old.labels);
                    if (old.blocks != null && old.blocks.Count > 0) replacement.blocks.AddRange(old.blocks);
                    m.SetInstructionAndAdvance(replacement);
                    m.RemoveInstructions(1);
                    return m.InstructionEnumeration();
                }
                catch (Exception e)
                {
                    if (AlreadyHeadlessSafe(instructions, "S6.2", "Pickable.RPC_Pick")) return instructions;
                    NoodlesLog.Error($"S6.2: Pickable.RPC_Pick anchor not found, leaving unpatched: {e.Message}");
                    Features_Gameplay.HeadlessNRESweep.ForceOff($"Pickable.RPC_Pick transpiler anchor missing: {e.GetType().Name}");
                    return instructions;
                }
            }
        }

        // --- H4.3: Valkyrie.Awake ------------------------------------------------------------------------------

        /// <summary>
        /// SERVER:147765 (class SERVER:147723). <c>m_targetPoint = Player.m_localPlayer.transform.position +
        /// ...</c> runs once the method is already past its own <c>!m_nview.IsOwner()</c> early return — same
        /// pattern as the owner gate itself, so on a null local player this guard reuses vanilla's OWN
        /// established bail-out (<c>base.enabled = false; return;</c>) rather than inventing a new one: there
        /// is no sensible fallback position to compute a Valkyrie's flight path around without a player.
        /// Applied defensively regardless of whether a freshly-spawned Valkyrie ZDO can ever actually be swept
        /// server-side (open question, blueprint) — the fix is free either way.
        /// </summary>
        internal static bool GuardValkyrieIsOwner(bool isOwner)
        {
            if (!isOwner) return false;
            if (!Features_Gameplay.HeadlessNRESweep.Active) return true; // vanilla: proceed, and NRE if null, exactly as today
            if (Player.m_localPlayer != null) return true;
            Telemetry.GetCounter("gameplay.s6.guardedValkyrie").Increment();
            return false; // same disable-and-return path vanilla already uses for "not owner"
        }

        [HarmonyPatch(typeof(Valkyrie), "Awake")]
        internal static class Patch_Valkyrie_Awake
        {
            [HarmonyTranspiler]
            internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            {
                try
                {
                    var isOwner = AccessTools.Method(typeof(ZNetView), nameof(ZNetView.IsOwner));
                    var guard = AccessTools.Method(typeof(NREGuards), nameof(GuardValkyrieIsOwner));

                    var m = new CodeMatcher(instructions, generator);
                    m.MatchStartForward(new CodeMatch(OpCodes.Callvirt, isOwner))
                     .ThrowIfInvalid("Valkyrie.Awake: ZNetView.IsOwner() call not found");
                    // Pure insert between the existing `callvirt IsOwner()` and the existing `brtrue.s` — this
                    // is the method's first branch (nothing precedes it but straight-line field/component
                    // setup), so neither surrounding instruction carries an incoming label.
                    m.Advance(1).InsertAndAdvance(new CodeInstruction(OpCodes.Call, guard));
                    return m.InstructionEnumeration();
                }
                catch (Exception e)
                {
                    if (AlreadyHeadlessSafe(instructions, "S6.3", "Valkyrie.Awake")) return instructions;
                    NoodlesLog.Error($"S6.3: Valkyrie.Awake anchor not found, leaving unpatched: {e.Message}");
                    Features_Gameplay.HeadlessNRESweep.ForceOff($"Valkyrie.Awake transpiler anchor missing: {e.GetType().Name}");
                    return instructions;
                }
            }
        }

        // --- H4.4: Leviathan.RPC_Left -------------------------------------------------------------------------

        /// <summary>
        /// SERVER:132147. First statement is <c>Utils.DistanceXZ(transform.position, Player.m_localPlayer.transform.position)
        /// &lt; m_playerCheckSize</c>, invoked via <c>m_nview.InvokeRPC(ZNetView.Everybody, "RPC_Left")</c>
        /// (SERVER:132144) — an NRE here fires in the LOCAL, self-invoked handler before <c>RouteRPC</c> ever
        /// runs (see class remarks), so the broadcast never reaches any connected client, not just a
        /// server-local symptom. Substitutes a far-away sentinel position for the null local player, forcing
        /// the distance comparison false ("no local player heard this, don't credit the stat") — never skips
        /// the RPC/broadcast itself, which already happened by the time this handler runs.
        /// </summary>
        private static Vector3 SafeLocalPlayerPositionForDistanceCheck(string guardedCounterName)
        {
            if (!Features_Gameplay.HeadlessNRESweep.Active) return Player.m_localPlayer.transform.position; // vanilla: NRE if null
            if (Player.m_localPlayer != null) return Player.m_localPlayer.transform.position;
            Telemetry.GetCounter(guardedCounterName).Increment();
            return FarAwaySentinel;
        }

        // Two thin, parameterless wrappers (not one shared method taking an argument): the transpiler below
        // replaces a fixed 3-instruction sequence with exactly one zero-argument `call`, matching the stack
        // effect of what it replaces (nothing consumed beyond what vanilla's own code already left in place,
        // one Vector3 pushed) — a per-site counter name is baked in C#, not pushed via extra IL.
        internal static Vector3 SafeLocalPlayerPositionForLeviathan() =>
            SafeLocalPlayerPositionForDistanceCheck("gameplay.s6.guardedLeviathanLeft");
        internal static Vector3 SafeLocalPlayerPositionForMusicVolume() =>
            SafeLocalPlayerPositionForDistanceCheck("gameplay.s6.guardedMusicVolumePlay");

        [HarmonyPatch(typeof(Leviathan), "RPC_Left")]
        internal static class Patch_Leviathan_RPC_Left
        {
            [HarmonyTranspiler]
            internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            {
                try
                {
                    var body = ReplaceLocalPlayerPositionFetch(instructions, generator,
                        AccessTools.Method(typeof(NREGuards), nameof(SafeLocalPlayerPositionForLeviathan)));
                    return body;
                }
                catch (Exception e)
                {
                    if (AlreadyHeadlessSafe(instructions, "S6.4", "Leviathan.RPC_Left")) return instructions;
                    NoodlesLog.Error($"S6.4: Leviathan.RPC_Left anchor not found, leaving unpatched: {e.Message}");
                    Features_Gameplay.HeadlessNRESweep.ForceOff($"Leviathan.RPC_Left transpiler anchor missing: {e.GetType().Name}");
                    return instructions;
                }
            }
        }

        // --- H4.5: MusicVolume.RPC_PlayMusic ------------------------------------------------------------------

        /// <summary>
        /// SERVER:135059. Same <c>InvokeRPC(Everybody, ...)</c> self-invoke-before-route shape as H4.4. An NRE
        /// on the first statement (<c>Vector3.Distance(Player.m_localPlayer.transform.position, ...)</c>) drops
        /// the broadcast to every peer AND skips the trailing owner-gated <c>ZDOVars.s_plays</c> write — a state
        /// desync on top of the broadcast loss. Same sentinel substitution as H4.4 (shared helper above): forces
        /// "this local player didn't hear it" rather than crashing, and the owner-gated <c>s_plays</c> write
        /// below is untouched (still reachable, still correct — it never referenced the local player).
        /// </summary>
        [HarmonyPatch(typeof(MusicVolume), "RPC_PlayMusic")]
        internal static class Patch_MusicVolume_RPC_PlayMusic
        {
            [HarmonyTranspiler]
            internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            {
                try
                {
                    return ReplaceLocalPlayerPositionFetch(instructions, generator,
                        AccessTools.Method(typeof(NREGuards), nameof(SafeLocalPlayerPositionForMusicVolume)));
                }
                catch (Exception e)
                {
                    if (AlreadyHeadlessSafe(instructions, "S6.5", "MusicVolume.RPC_PlayMusic")) return instructions;
                    NoodlesLog.Error($"S6.5: MusicVolume.RPC_PlayMusic anchor not found, leaving unpatched: {e.Message}");
                    Features_Gameplay.HeadlessNRESweep.ForceOff($"MusicVolume.RPC_PlayMusic transpiler anchor missing: {e.GetType().Name}");
                    return instructions;
                }
            }
        }

        // --- H4.6: Ship.UpdateSailSize (1.0.5) ----------------------------------------------------------------

        /// <summary>
        /// SERVER 1.0.16 IL_0095. The sail-change branch of <c>Ship.UpdateSailSize</c> — <c>if (!flag &amp;&amp;
        /// m_sailWasInPosition)</c> — opens with <c>Player.m_localPlayer.GetPlayerID()</c> to credit the sound to the
        /// local helmsman, and it runs on EVERY instance, owner or not (<c>Ship.CustomFixedUpdate</c> calls
        /// <c>UpdateSail</c> before its own owner check). A vanilla dedicated server never has a ship instance near a
        /// player; this mod's server-side zone simulation does. Live on Proxima 2026-09-29 07:29:02 a player changed
        /// sail next to the server's copy of their ship: 99 NullReferenceExceptions, one per FixedUpdate until the sail
        /// finished moving (the throw skips the trailing <c>m_sailWasInPosition = flag</c>, so the branch re-enters
        /// every frame), each one aborting MonoUpdaters' whole FixedUpdate for every object on the server (see
        /// <see cref="UpdateIsolation"/>). Guard scope: only this branch's own <c>m_sailWasInPosition</c> read. With no
        /// local player it reads false, so the branch is skipped outright — its only effects are a Debug.Log line and a
        /// sound/particle effect a headless server can't play — while the rest of the method (the sail's transform and
        /// cloth, and the trailing <c>m_sailWasInPosition = flag</c>) runs unchanged.
        /// </summary>
        internal static bool GuardShipSailEffect(bool sailWasInPosition)
        {
            if (!sailWasInPosition) return false;
            if (!Features_Gameplay.HeadlessNRESweep.Active) return true; // vanilla: proceed, and NRE if null, exactly as today
            if (Player.m_localPlayer != null) return true;
            Telemetry.GetCounter("gameplay.s6.guardedShipSail").Increment();
            return false; // skip only the sail-change sound and log line; the sail itself still moves
        }

        [HarmonyPatch(typeof(Ship), "UpdateSailSize")]
        internal static class Patch_Ship_UpdateSailSize
        {
            [HarmonyTranspiler]
            internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            {
                try
                {
                    var wasInPositionField = AccessTools.Field(typeof(Ship), "m_sailWasInPosition");
                    var guard = AccessTools.Method(typeof(NREGuards), nameof(GuardShipSailEffect));

                    var m = new CodeMatcher(instructions, generator);
                    m.MatchStartForward(new CodeMatch(OpCodes.Ldfld, wasInPositionField))
                     .ThrowIfInvalid("Ship.m_sailWasInPosition field read not found");
                    // Pure insert between the branch's `ldfld m_sailWasInPosition` and its `brfalse.s` (confirmed this
                    // pass against the 1.0.16 server build's IL: IL_0095/IL_009a, the method's only READ of that field —
                    // the trailing write is a `stfld`, which this match never sees). Neither instruction carries an
                    // incoming label, so an insert needs no label bookkeeping.
                    m.Advance(1).InsertAndAdvance(new CodeInstruction(OpCodes.Call, guard));
                    return m.InstructionEnumeration();
                }
                catch (Exception e)
                {
                    // Deliberately NOT ForceOff like S6.1-S6.5: those five guard duplication/broadcast-loss paths and must
                    // not be switched off because a sixth, cosmetic-effect anchor moved. Without this one the sail NRE
                    // is still contained per object by UpdateIsolation; it just stays noisy.
                    if (AlreadyHeadlessSafe(instructions, "S6.6", "Ship.UpdateSailSize")) return instructions;
                    NoodlesLog.Error($"S6.6: Ship.UpdateSailSize anchor not found, leaving unpatched (the other S6 guards stay on): {e.Message}");
                    return instructions;
                }
            }
        }
    }
}
