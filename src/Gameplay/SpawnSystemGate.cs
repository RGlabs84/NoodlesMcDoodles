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
    /// S1 (docs/design/05, Hook H1) — <c>SpawnSystem.UpdateSpawning()</c>, SERVER:110686 / CLIENT:111318,
    /// confirmed byte-identical this pass (re-grepped both decompiles and both builds' IL — see below). Body:
    /// <code>
    /// private void UpdateSpawning()
    /// {
    ///     if (!m_nview.IsValid() || !m_nview.IsOwner() || Player.m_localPlayer == null) return;
    ///     ...
    /// }
    /// </code>
    /// <c>Player.m_localPlayer</c> is permanently null on a dedicated server process, so this three-term OR
    /// never lets a server-owned zone's spawn logic run at all — the exact "spawning silently stops working
    /// worldwide" regression this lever exists to fix. Server-only; dormant (no-op) until the zone-simulation
    /// subsystem's per-peer instantiation hook populates <c>GetPlayersInZone</c>'s result (its own
    /// <c>m_tempNearPlayers.Count == 0</c> early return, left untouched, keeps this a documented no-op until
    /// then — see docs/design/05 H1).
    /// </summary>
    internal static class SpawnSystemGate
    {
        // Live-read every call (never cached/baked at patch time) so a config/kill-switch flip takes effect on
        // the very next InvokeRepeating tick, per the shared-infra "no transpiled literal" rule.
        internal static bool EffectiveLocalPlayerIsNull()
        {
            // Off: byte-identical to vanilla's own comparison (Player.m_localPlayer's static type is
            // UnityEngine.Object-derived, so `== null` here compiles to the SAME op_Equality call vanilla's own
            // IL uses — not a raw reference compare, preserving Unity's "destroyed object reads as null" rule).
            if (!Features_Gameplay.ServerSpawning.Active) return Player.m_localPlayer == null;
            // On: never true, regardless of the real (always-null, on a dedicated server) value — the method's
            // own remaining two OR-terms (IsValid/IsOwner) are untouched and still gate normally.
            return false;
        }

        /// <summary>
        /// Mandatory co-guard (H1): <c>SpawnSystem</c> runs one independent <c>InvokeRepeating</c> per zone
        /// instance (SERVER:110680-110686) with no aggregate cap of its own — a server holding many zones pays
        /// for all of them in the same process. A simple, real-second-bucketed token count, consulted by a
        /// prefix BEFORE the (transpiled) body runs for a given zone this tick; zones over budget are skipped
        /// this tick only — never batched or force-run later, so no spawn window is ever silently doubled up.
        /// </summary>
        private static class ZoneScanBudget
        {
            private static int s_tokensThisSecond;
            private static float s_windowStart;

            public static bool TryConsume()
            {
                float now = Time.realtimeSinceStartup;
                if (now - s_windowStart >= 1f)
                {
                    s_windowStart = now;
                    s_tokensThisSecond = Mathf.Max(1, Features_Gameplay.ServerSpawningMaxZonesPerSecond.Value);
                }
                if (s_tokensThisSecond <= 0) return false;
                s_tokensThisSecond--;
                return true;
            }
        }

        [HarmonyPatch(typeof(SpawnSystem), "UpdateSpawning")]
        internal static class Patch_SpawnSystem_UpdateSpawning
        {
            [HarmonyPrefix]
            internal static bool Prefix()
            {
                if (!Features_Gameplay.ServerSpawning.Active) return true; // vanilla/transpiled body no-ops via EffectiveLocalPlayerIsNull anyway
                try
                {
                    if (ZoneScanBudget.TryConsume())
                    {
                        Telemetry.GetCounter("gameplay.s1.zonesScannedPerTick").Increment();
                        return true;
                    }
                    return false; // over budget this tick — try again next InvokeRepeating tick, nothing lost
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_SpawnSystem_UpdateSpawning) + ".Prefix", e);
                    return true; // fail open: don't let a budget-tracking bug silently stop all spawning
                }
            }

            /// <summary>
            /// Removes only the <c>Player.m_localPlayer == null</c> operand of the three-term OR guard,
            /// leaving <c>!m_nview.IsValid()</c> and <c>!m_nview.IsOwner()</c> untouched — a prefix cannot do
            /// this (it can only skip or not skip the WHOLE method; a prefix reading the server-local, never-synced
            /// <c>ServerSpawning.Active</c> flag would skip the method on every client that installs the mod too,
            /// exactly the failure DESIGN.md/blueprint Red Team 1 major #3 rejected — see class remarks and
            /// <see cref="EffectiveLocalPlayerIsNull"/>'s own doc comment).
            /// <para>
            /// IL anchor (verified this pass via <c>ilspycmd -il</c> against BOTH
            /// <c>libs-Tools/1.0/{client,server}/assembly_valheim.dll</c>, identical offsets in both):
            /// </para>
            /// <code>
            /// IL_0018: brtrue.s IL_001b          // !IsOwner() term; branches into the block below when IsOwner() is true
            /// IL_001a: ret
            /// IL_001b: ldsfld class Player Player::m_localPlayer   // &lt;- sole occurrence in this method, both builds
            /// IL_0020: ldnull
            /// IL_0021: call bool UnityEngine.Object::op_Equality(...)
            /// IL_0026: brfalse.s IL_0029
            /// IL_0028: ret
            /// </code>
            /// The <c>ldsfld</c> at IL_001b is itself the target of the preceding <c>brtrue.s</c> — replacing it
            /// requires carrying its incoming label forward onto the single replacement instruction (see
            /// <see cref="NullGuardIL.ReplaceLocalPlayerNullCheck"/>), the same concern Transport's
            /// SendZdosPatch.cs documents for its own site 2.
            /// </summary>
            [HarmonyTranspiler]
            internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            {
                try
                {
                    return NullGuardIL.ReplaceLocalPlayerNullCheck(
                        instructions, generator,
                        AccessTools.Method(typeof(SpawnSystemGate), nameof(EffectiveLocalPlayerIsNull)));
                }
                catch (Exception e)
                {
                    NoodlesLog.Error($"S1: UpdateSpawning anchor not found, leaving unpatched: {e.Message}");
                    Features_Gameplay.ServerSpawning.ForceOff($"UpdateSpawning transpiler anchor missing: {e.GetType().Name}");
                    return instructions; // CodeMatcher copies its input — this is still the pristine original sequence
                }
            }
        }
    }

    /// <summary>
    /// Shared transpiler helper for the two sites in this module that replace vanilla's
    /// <c>Player.m_localPlayer == null</c> check (compiled as <c>ldsfld; ldnull; call op_Equality</c>, per
    /// Unity's overloaded equality operator — confirmed via IL dump, not the raw <c>ceq</c> a plain reference
    /// compare would emit) with a single call to a feature-gated substitute of identical stack shape (one
    /// <c>bool</c> in, matching what the 3-instruction sequence it replaces produces). Used by
    /// <see cref="SpawnSystemGate"/> (S1) and <see cref="ShipHandback"/> (S4) — both anchor sites are
    /// confirmed (this pass) to be the target of an incoming <c>brtrue.s</c>, so both need the label carried
    /// forward onto the single replacement instruction.
    /// </summary>
    internal static class NullGuardIL
    {
        private static readonly FieldInfo s_playerLocalPlayer = AccessTools.Field(typeof(Player), nameof(Player.m_localPlayer));
        private static readonly MethodInfo s_opEquality = AccessTools.Method(
            typeof(UnityEngine.Object), "op_Equality", new[] { typeof(UnityEngine.Object), typeof(UnityEngine.Object) });

        internal static IEnumerable<CodeInstruction> ReplaceLocalPlayerNullCheck(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodInfo replacementCall)
        {
            var m = new CodeMatcher(instructions, generator);
            m.MatchStartForward(
                 new CodeMatch(OpCodes.Ldsfld, s_playerLocalPlayer),
                 new CodeMatch(OpCodes.Ldnull),
                 new CodeMatch(OpCodes.Call, s_opEquality))
             .ThrowIfInvalid("Player.m_localPlayer == null check (ldsfld;ldnull;call op_Equality) not found");

            var replacement = new CodeInstruction(OpCodes.Call, replacementCall);
            var old = m.Instruction; // the ldsfld — carries the incoming branch-target label, if any (both real sites do)
            if (old.labels != null && old.labels.Count > 0) replacement.labels.AddRange(old.labels);
            if (old.blocks != null && old.blocks.Count > 0) replacement.blocks.AddRange(old.blocks);

            m.SetInstructionAndAdvance(replacement); // now at (old) ldnull
            m.RemoveInstructions(2);                 // drop the now-orphaned ldnull + call op_Equality

            return m.InstructionEnumeration();
        }
    }
}
