using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using NoodlesMcDoodles.Core;

namespace NoodlesMcDoodles.ZoneSim
{
    /// <summary>
    /// Hook 3 (Z5) — docs/design/04: <c>ZoneSystem.Update()</c> cadence transpile, plus the mandatory companion
    /// per-tick cap on first-time zone generation (<c>CreateGhostZones</c>). Both are required together — the
    /// cadence change alone (0.1s -&gt; 0.05s) doubles how often the uncapped ghost-zone loop below can fire,
    /// which on a fresh world/large group expedition could stall the single server main thread.
    /// </summary>
    internal static class UpdateCadence
    {
        /// <summary>
        /// <c>ZoneSystem.Update()</c> — private instance method, <c>SERVER:113776-113845</c>/
        /// <c>CLIENT:114587-114656</c>, confirmed byte-for-byte identical both builds this session. Full body,
        /// corrected after the live headless boot test caught an incomplete first read (see below):
        /// <c>if (IsServer() &amp;&amp; !LocationsGenerated) { ...; m_timeSlicedGenerationTimeBudget = 0.1f; return; }
        /// ...; m_updateTimer += Time.deltaTime; if (!(m_updateTimer &gt; 0.1f)) return; m_updateTimer = 0f;
        /// bool flag = CreateLocalZones(...); UpdateTTL(0.1f); if (IsServer() &amp;&amp; !flag) { CreateGhostZones(refPos);
        /// foreach (peer in GetPeers()) CreateGhostZones(peer.GetRefPos()); }</c>. THREE textual <c>0.1f</c>
        /// literals exist in this method, not two — the first draft of this transpiler missed the early
        /// world-gen time-slicing one (a different mechanism, only reachable before <c>LocationsGenerated</c>
        /// is first true) and blindly took "the first two 0.1f's found," mis-targeting it instead of
        /// <c>UpdateTTL</c>'s own argument. Caught immediately, on the very first headless boot, by the
        /// anchor-count assertion below refusing to apply a partial patch — exactly the safety net it exists
        /// for. Fixed by anchoring past the unrelated literal first (see the transpiler's own anchor 0).
        ///
        /// Three substitutions, in textual/IL order (each independently anchor-checked — a future game build
        /// that changes this method's shape fails this ONE feature off at patch time, per PatchRegistry's own
        /// JIT-verification net, rather than silently mis-patching):
        /// <list type="number">
        /// <item>the cadence gate's own <c>0.1f</c> (<c>SERVER:113800</c>) -&gt; a call to
        /// <see cref="ZoneSimConfig.EffectiveUpdateTickSeconds"/>;</item>
        /// <item><c>UpdateTTL(0.1f)</c>'s argument (<c>SERVER:113806</c>) -&gt; the SAME call, so <c>dt</c> stays
        /// wall-clock-accurate at whatever cadence is configured — RED-TEAM FIX: left unpatched, TTL would
        /// accumulate at ~2x real-time once the gate moved to 0.05s, silently halving the user's 15s
        /// <c>ZoneTTLSeconds</c> target;</item>
        /// <item>the <c>foreach (ZNetPeer peer in ZNet.instance.GetPeers())</c> loop's own <c>GetPeers()</c>
        /// call site -&gt; <see cref="UpdateCadence.RotatedPeersForGhostZones"/>, giving every peer a turn at
        /// "goes first" (and therefore most likely to win <see cref="Patch_ZoneSystem_CreateGhostZones_RoundRobinCap"/>'s
        /// shared per-tick slot) — RED-TEAM FIX (RT1 major #3): <c>GetPeers()</c> returns <c>m_peers</c> directly
        /// (<c>SERVER:81676-81679</c>), a stable join-ordered list; with a fixed iteration order the earliest-
        /// joined peer would always win a contested tick's one slot.</item>
        /// </list>
        /// A void call is also inserted immediately after substitution 2 (<see cref="ZoneSimConfig.OnRealZoneTick"/>) —
        /// stack-neutral (the preceding <c>call UpdateTTL</c> is itself <c>void</c>-returning) and only reachable
        /// on the real-tick code path past the early-return gate, giving this module an exactly-once-per-real-tick
        /// hook for resetting Hook 3's own per-tick telemetry/counters without any separate timing heuristic.
        /// </summary>
        [HarmonyPatch(typeof(ZoneSystem), "Update")]
        internal static class Patch_ZoneSystem_Update_Cadence
        {
            [HarmonyTranspiler]
            internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var matcher = new CodeMatcher(instructions);

                // LIVE-BOOT-TEST FINDING (this session): the decompiled C# source actually shows THREE 0.1f
                // literals in this method, not two as first read — an earlier, unrelated
                // `m_timeSlicedGenerationTimeBudget = 0.1f;` (SERVER:113791/CLIENT:...) inside the
                // `if (IsServer() && !LocationsGenerated) {...return;}` early branch (world-gen time-slicing
                // budget, a different mechanism that happens to share the same literal value). A first version
                // of this transpiler matched the first two 0.1f's blindly, mis-targeting this one instead of
                // UpdateTTL's argument — caught immediately by the anchor-count assertion below refusing to
                // apply a partial patch, exactly as designed, on the very first headless boot test. Fixed by
                // anchoring past it: m_updateTimer (private field) is only ever referenced starting at
                // `m_updateTimer += Time.deltaTime;`, strictly after the unrelated branch, so searching for the
                // two REAL targets only from there on can never re-encounter it.
                var updateTimerField = AccessTools.Field(typeof(ZoneSystem), "m_updateTimer");
                matcher.Start().MatchStartForward(new CodeMatch(instr => instr.LoadsField(updateTimerField) || instr.StoresField(updateTimerField)))
                    .ThrowIfInvalid("NoodlesMcDoodles Z5 anchor 0/3: m_updateTimer field reference not found in ZoneSystem.Update");
                matcher.Advance(1);

                matcher.MatchStartForward(new CodeMatch(OpCodes.Ldc_R4, 0.1f))
                    .ThrowIfInvalid("NoodlesMcDoodles Z5 anchor 1/3: cadence-gate 0.1f not found in ZoneSystem.Update");
                matcher.SetInstructionAndAdvance(CodeInstruction.Call(typeof(ZoneSimConfig), nameof(ZoneSimConfig.EffectiveUpdateTickSeconds)));

                matcher.MatchStartForward(new CodeMatch(OpCodes.Ldc_R4, 0.1f))
                    .ThrowIfInvalid("NoodlesMcDoodles Z5 anchor 2/3: UpdateTTL(0.1f) argument not found in ZoneSystem.Update");
                matcher.SetInstructionAndAdvance(CodeInstruction.Call(typeof(ZoneSimConfig), nameof(ZoneSimConfig.EffectiveUpdateTickSeconds)));

                // Exactly ONE 0.1f should remain process-wide: the earlier, deliberately-untouched
                // m_timeSlicedGenerationTimeBudget assignment. Zero remaining would mean that literal itself
                // changed/disappeared; two or more would mean a new, ambiguous 0.1f appeared somewhere. Either
                // way, refuse a silently wrong or partial patch (mirrors Hook 6's own dual-literal guard).
                int remaining = matcher.InstructionEnumeration().Count(i => i.opcode == OpCodes.Ldc_R4 && i.OperandIs(0.1f));
                if (remaining != 1)
                {
                    throw new InvalidOperationException(
                        $"NoodlesMcDoodles Z5: expected exactly 1 deliberately-untouched 0.1f literal " +
                        $"(m_timeSlicedGenerationTimeBudget) remaining in ZoneSystem.Update after replacing the " +
                        $"2 real targets, found {remaining} — method shape changed, refusing a partial patch.");
                }

                // Exactly-once-per-real-tick hook: matched on the UpdateTTL CALL itself (not its argument, to
                // avoid inserting between an argument push and the call that consumes it), then advanced past
                // it — UpdateTTL is void, so the stack is empty here, an unambiguous statement boundary.
                matcher.MatchStartForward(new CodeMatch(instr => instr.Calls(AccessTools.Method(typeof(ZoneSystem), "UpdateTTL", new[] { typeof(float) }))))
                    .ThrowIfInvalid("NoodlesMcDoodles Z5 anchor: UpdateTTL(float) call not found in ZoneSystem.Update");
                matcher.Advance(1);
                matcher.InsertAndAdvance(new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(ZoneSimConfig), nameof(ZoneSimConfig.OnRealZoneTick))));

                matcher.MatchStartForward(new CodeMatch(instr => instr.Calls(AccessTools.Method(typeof(ZNet), "GetPeers"))))
                    .ThrowIfInvalid("NoodlesMcDoodles Z5 anchor 3/3: ZNet.GetPeers() call not found in ZoneSystem.Update");
                matcher.SetInstructionAndAdvance(CodeInstruction.Call(typeof(UpdateCadence), nameof(RotatedPeersForGhostZones)));

                return matcher.InstructionEnumeration();
            }
        }

        // --- Round-robin fairness for CreateGhostZones's shared per-tick slot -------------------------------

        private static int s_rotationOffset;
        private static readonly List<ZNetPeer> s_rotatedBuffer = new List<ZNetPeer>();

        /// <summary>
        /// Replaces the transpiled call site's own <c>ZNet.instance.GetPeers()</c> — takes the <c>ZNet</c>
        /// parameter the ORIGINAL call site's IL already pushed (<c>call ZNet::get_instance()</c> followed by
        /// <c>callvirt GetPeers()</c>, the latter consuming that pushed instance as its implicit "this") so this
        /// STATIC replacement stays stack-balanced: one value popped (the ZNet instance), one value pushed (the
        /// list) — exactly what the instruction it replaces did. A parameterless static method here would leave
        /// the already-pushed ZNet reference stranded on the stack, corrupting it.
        ///
        /// <c>GetPeers()</c> returns <c>m_peers</c> BY REFERENCE (not a copy, <c>SERVER:81676-81679</c>) — this
        /// never mutates that list, only copies a rotated VIEW of it into a separate buffer, so vanilla's own
        /// join-ordered list (which other, unrelated code may depend on staying in that order) is untouched.
        /// </summary>
        internal static List<ZNetPeer> RotatedPeersForGhostZones(ZNet znet)
        {
            List<ZNetPeer> peers = znet.GetPeers();
            // V1 FIX (deployment-modes review, BLOCKER, 2026-09-28): runtime authority, not the flag alone --
            // see ZoneSimConfig.IsAuthoritativeProcess's own remarks. This call site sits inside ZoneSystem.Update's
            // own `if (IsServer() && !flag)` ghost-zone block (SERVER:113807), so it's unreachable on a plain
            // client today regardless -- fixed anyway per the binding rule ("every server-authoritative code
            // path... must check runtime authority... never the flag alone") and for defense in depth against
            // that vanilla gate ever changing shape.
            if (!ZoneSimConfig.ServerZoneSimFlag.Active || !ZoneSimConfig.IsAuthoritativeProcess() || peers.Count <= 1) return peers;

            try
            {
                s_rotatedBuffer.Clear();
                int offset = s_rotationOffset % peers.Count;
                for (int i = 0; i < peers.Count; i++) s_rotatedBuffer.Add(peers[(offset + i) % peers.Count]);
                s_rotationOffset = (s_rotationOffset + 1) % peers.Count;
                return s_rotatedBuffer;
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(RotatedPeersForGhostZones), e);
                return peers; // fail open to vanilla's own join order
            }
        }

        /// <summary>
        /// Hook 3's other required patch (docs/design/04's "inseparable" guard): a process-wide counter,
        /// reset once per real <c>ZoneSystem.Update</c> tick (see <see cref="ZoneSimConfig.OnRealZoneTick"/>),
        /// that refuses <c>CreateGhostZones</c> — matching the ORIGINAL method's own "nothing built" return —
        /// once <c>MaxGhostSpawnsPerTick</c> real, first-time <c>SpawnZone(Ghost)</c> generations have already
        /// happened this tick. Prefix only ever returns <c>false</c> (skip) or <c>true</c> (run original);
        /// never replaces the original's own logic.
        /// </summary>
        [HarmonyPatch(typeof(ZoneSystem), "CreateGhostZones")]
        internal static class Patch_ZoneSystem_CreateGhostZones_RoundRobinCap
        {
            private static int s_ghostSpawnsThisTick;

            [HarmonyPrefix]
            internal static bool Prefix(ref bool __result)
            {
                // V1 FIX (deployment-modes review, BLOCKER, 2026-09-28): runtime authority required alongside
                // the flag -- see ZoneSimConfig.IsAuthoritativeProcess's own remarks (CreateGhostZones is itself
                // only ever called from ZoneSystem.Update's IsServer()-gated block, SERVER:113807, so this is
                // unreachable on a plain client today regardless; fixed per the binding rule and for defense in
                // depth, matching every other patch in this module).
                if (!ZoneSimConfig.ServerZoneSimFlag.Active || !ZoneSimConfig.IsAuthoritativeProcess()) return true; // vanilla, uncapped

                try
                {
                    if (s_ghostSpawnsThisTick >= ZoneSimConfig.MaxGhostSpawnsPerTick.Value)
                    {
                        __result = false; // matches CreateGhostZones's own "nothing built" return shape
                        return false;
                    }
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_ZoneSystem_CreateGhostZones_RoundRobinCap), e);
                }
                return true; // let vanilla run; the postfix below counts a REAL build
            }

            [HarmonyPostfix]
            internal static void Postfix(bool __result)
            {
                if (!ZoneSimConfig.ServerZoneSimFlag.Active || !ZoneSimConfig.IsAuthoritativeProcess()) return;
                try
                {
                    if (__result) s_ghostSpawnsThisTick++;
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_ZoneSystem_CreateGhostZones_RoundRobinCap) + ".Postfix", e);
                }
            }

            /// <summary>Called from <see cref="ZoneSimConfig.OnRealZoneTick"/>, once per real tick, before this
            /// tick's CreateGhostZones calls happen.</summary>
            internal static void ResetTickCounter()
            {
                Telemetry_ZoneSim.SetGhostSpawnsThisTick(s_ghostSpawnsThisTick);
                s_ghostSpawnsThisTick = 0;
            }
        }
    }
}
