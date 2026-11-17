using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NoodlesMcDoodles.Core;

namespace NoodlesMcDoodles.Transport
{
    /// <summary>
    /// Telemetry names for this agent's M1 levers (Q7/Q8/Q12/G2-N1) — see docs/design/01-transport-and-queues.md
    /// "Telemetry" section for the naming rationale (grep-able snake_case/dotted names, flushed once per
    /// <c>Telemetry.LogIntervalSeconds</c> by the shared infra, never a per-event log line of our own).
    /// </summary>
    internal static class Telemetry_Transport
    {
        public static void DestroyChunksSent(int chunkCount, int idCount)
        {
            Telemetry.GetCounter("transport.q7.chunksSent").Add(chunkCount);
            Telemetry.GetCounter("transport.q7.idsSent").Add(idCount);
        }

        public static void ForceSendBatchCapped(int attemptedCount)
        {
            Telemetry.GetCounter("transport.q8.batchCapped").Increment();
            Telemetry.SetGauge("transport.q8.lastCappedAttemptCount", attemptedCount);
        }

        // "Q12SkipLogged" per docs/design/01's own Telemetry section ("not separately counted above but
        // should be — add Q12SkipLogged(zdoUid, wouldBeSize) as a companion counter").
        public static void Q12OversizedItemSkipped(ZDOID id, int candidateBytes)
        {
            Telemetry.GetCounter("transport.q12.oversizedItemsSkipped").Increment();
            Telemetry.SetGauge("transport.q12.lastOversizedItemBytes", candidateBytes);
        }

        public static void Q12BudgetBreak(int candidateBytes)
        {
            Telemetry.GetCounter("transport.q12.budgetBreakEvents").Increment();
            Telemetry.SetGauge("transport.q12.lastBreakCandidateBytes", candidateBytes);
        }

        public static void InvalidSectorChunkSent(int sentCount, int remainingCount)
        {
            Telemetry.GetCounter("transport.g2n1.chunksSent").Increment();
            Telemetry.GetCounter("transport.g2n1.idsSent").Add(sentCount);
            Telemetry.SetGauge("transport.g2n1.backlogRemaining", remainingCount);
        }
    }

    /// <summary>
    /// Q12 — the per-item running-total/oversized-item guard inserted into <c>ZDOMan.SendZDOs</c>'s per-item
    /// write loop by <see cref="SendZdosPatch"/>'s transpiler. Called with exactly the live values the
    /// transpiled call site has on hand (<c>zPackage</c>, <c>num</c>, the candidate <c>ZDO</c>) — no
    /// reflection needed here, unlike <see cref="InvalidSectorGuard"/>, since <see cref="ZPackage"/> and
    /// <see cref="ZDO"/> are both public types. docs/design/01 Hook 6 "Q12" text and Data-loss analysis #1.
    /// </summary>
    internal static class Q12Guard
    {
        public const int Proceed = 0;
        public const int Break = 1;
        public const int SkipItem = 2;

        // Bytes vanilla writes in front of each item's payload (SERVER:77103-77110, ZPackage.Write overloads at
        // SERVER:83074+): ZDOID 8+4, OwnerRevision 2, DataRevision 4, owner 8, position 12, and the payload's
        // 4-byte length prefix (Write(ZPackage) writes array.Length as an int, then the bytes).
        private const int ItemHeaderBytes = 42;

        // One reusable buffer, the same pattern vanilla uses for its own per-item scratch package (IL_00e1 /
        // IL_0187: one ZPackage, Clear() per item). SendZDOs only runs on the main thread. Clear() keeps the
        // stream's capacity, so after warm-up this measures without allocating.
        private static readonly ZPackage s_payload = new ZPackage();
        private static readonly HashSet<ZDOID> s_warnedOversized = new HashSet<ZDOID>();

        internal static int Evaluate(ZPackage zPackage, int num, ZDO item2)
        {
            try
            {
                // Vanilla's own unmodified check (SERVER:77094-77096) always runs first, regardless of Q12's
                // own flag — this transpiler REPLACES that check's instructions, so it must reproduce them.
                if (zPackage.Size() > num) return Break;

                if (!SharedBudget.Q12.Active || !SharedBudget.EligibleForThisProcess()) return Proceed;

                // Serialize the candidate's payload exactly as the real write path is about to, so the size is
                // measured, not estimated (Data-loss analysis #1: "serialize the candidate item first").
                s_payload.Clear();
                item2.Serialize(s_payload);
                int candidateSize = s_payload.Size() + ItemHeaderBytes;
                int framingMargin = SharedBudget.Q12FramingMarginBytesConfig.Value;

                // LEAD FIX: skip an item only when it could not fit under Steam's 512 KiB reliable-message cap
                // even in an otherwise empty package. Vanilla sends every smaller item, however large (a heavily
                // edited TerrainComp can be several hundred KB), and it must keep doing so: a peer that never
                // receives a TerrainComp creates a second one when its player edits that terrain, and the
                // duplicate cleanup then deletes one of them (research/04, lever Z8). An earlier fixed 256 KiB
                // ceiling here would have caused exactly that for any item between 256 and ~508 KiB. Items that
                // truly cannot fit would boot-loop the connection in vanilla (fact 8), so skipping them is the
                // lesser harm; they are counted and logged, never silently dropped from the world.
                if (candidateSize + framingMargin > SharedBudget.AbsoluteSteamCeilingBytes)
                {
                    Telemetry_Transport.Q12OversizedItemSkipped(item2.m_uid, candidateSize);
                    if (s_warnedOversized.Add(item2.m_uid)) // once per object, not every send round
                        NoodlesLog.Warning($"Q12: {item2.m_uid} (prefab {item2.GetPrefab()}) serializes to {candidateSize} bytes, " +
                            "more than one Steam reliable message can carry; it is not sent to peers until it shrinks.");
                    return SkipItem;
                }

                // Otherwise stop this call before the running total would cross the cap; the item goes first in
                // a later, emptier package (the sync list is rebuilt and re-sorted every call).
                if (zPackage.Size() + candidateSize + framingMargin > SharedBudget.AbsoluteSteamCeilingBytes)
                {
                    Telemetry_Transport.Q12BudgetBreak(candidateSize);
                    return Break; // break, not continue — nothing later in the (priority-sorted) list is more urgent
                }

                return Proceed;
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(Q12Guard), e);
                return Break; // fail closed: stop this SendZDOs call rather than risk an oversized/malformed package
            }
        }
    }

    /// <summary>
    /// G2-N1 — replaces SERVER:77075-77083's unconditional <c>m_invalidSector</c> write-then-<c>Clear()</c>
    /// block with a byte-budget-chunked drain. <c>ZDOMan.ZDOPeer</c> is `private class ZDOPeer` nested inside
    /// `public class ZDOMan` on BOTH builds (SERVER:76008/CLIENT:76133) — the transpiler passes the peer as
    /// `object` (boxed reference, standard Harmony pattern for an inaccessible original-method parameter
    /// type) and this class resolves `m_invalidSector` via cached reflection rather than naming `ZDOPeer` in
    /// source (its own field type, `HashSet&lt;ZDOID&gt;`, IS public, so no further reflection is needed once
    /// the field value is in hand). docs/design/01 Hook 6, RED-TEAM FIX RT1.
    /// </summary>
    internal static class InvalidSectorGuard
    {
        private static readonly Type s_zdoPeerType = AccessTools.Inner(typeof(ZDOMan), "ZDOPeer");
        private static readonly FieldInfo s_invalidSectorField = AccessTools.Field(s_zdoPeerType, "m_invalidSector");

        internal static bool WriteChunk(object peer, ZPackage zPackage)
        {
            try
            {
                var invalidSector = (HashSet<ZDOID>)s_invalidSectorField.GetValue(peer);
                if (invalidSector == null || invalidSector.Count == 0)
                {
                    zPackage.Write(0); // exact vanilla else-branch (SERVER:77087)
                    return false;
                }

                if (!SharedBudget.G2N1.Active || !SharedBudget.EligibleForThisProcess())
                {
                    // Exact vanilla shape (SERVER:77075-77083): write every pending ID unconditionally, then Clear().
                    zPackage.Write(invalidSector.Count);
                    foreach (var id in invalidSector) zPackage.Write(id);
                    invalidSector.Clear();
                    return true;
                }

                // Byte-budget-driven, not a fixed entry count (docs/design/01: "computes the real remaining
                // byte budget directly instead of trusting" a theoretical per-entry estimate) — write
                // candidates into a scratch package first and measure the ACTUAL serialized size.
                int chunkBudget = SharedBudget.G2N1ChunkBytesConfig.Value;
                var scratch = new ZPackage();
                var written = new List<ZDOID>();
                foreach (var id in invalidSector)
                {
                    scratch.Write(id);
                    if (scratch.Size() > chunkBudget) break; // this id pushed the chunk over budget — exclude it, stop
                    written.Add(id);
                }
                if (written.Count == 0)
                {
                    // Always-progress guard: chunkBudget's configured minimum (16384) dwarfs one ZDOID's wire
                    // size, so this should never trigger in practice — but never let a pathological config
                    // value stall the drain entirely (would starve the whole backlog forever).
                    foreach (var id in invalidSector) { written.Add(id); break; }
                }

                zPackage.Write(written.Count);
                foreach (var id in written) zPackage.Write(id);

                // RED-TEAM FIX RT1 (docs/design/01 Hook 6): remove exactly what was sent, never `.Clear()` and
                // never a bare no-op — a HashSet's enumeration order doesn't change between calls that don't
                // mutate it, so "leave it, don't Clear" alone would re-yield the same leading subset forever,
                // starving every entry past the first chunk.
                invalidSector.ExceptWith(written);
                Telemetry_Transport.InvalidSectorChunkSent(written.Count, invalidSector.Count);
                return true;
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(InvalidSectorGuard), e);
                return false; // fail closed: write nothing further this call rather than risk a malformed package
            }
        }
    }

    /// <summary>
    /// The unified <c>ZDOMan.SendZDOs</c> budget transpiler — Q3, Q5 (one shared value, see
    /// <see cref="SharedBudget.InnerBudgetBytes"/>), Q4, Q12 and G2-N1, all in one <c>[HarmonyTranspiler]</c>
    /// pass, because all five read/write the SAME <c>zPackage</c> before/around the SAME per-item size check
    /// (docs/design/01 Hook 6, "Why one patch, not five"). Target: <c>private bool ZDOMan.SendZDOs(ZDOPeer
    /// peer, bool flush)</c> — SERVER:77055-77120 / CLIENT:77197-77260, confirmed byte-for-byte identical
    /// between builds this pass via <c>ilspycmd -il</c> on both <c>assembly_valheim.dll</c>s (only
    /// assembly-reference metadata differs, never an opcode/operand/local-slot).
    ///
    /// RED-TEAM FIX (both red teams, blocker, docs/design/01): an earlier sketch used
    /// <c>SetOperandAndAdvance(0).InsertAndAdvance(call)</c> at each literal site, which mutates the matched
    /// <c>Ldc_I4</c>'s operand in place AND THEN inserts the getter call AFTER it — both instructions still
    /// execute, pushing two int32s where the surrounding comparison/subtraction expects one, an unconditional
    /// IL-verifier stack-imbalance (<c>InvalidProgramException</c>) independent of any config/flag state. Fixed
    /// here by using <c>SetInstructionAndAdvance</c> at each literal site instead — it replaces the WHOLE
    /// matched instruction (not just its operand), so each site pushes exactly the one int32 the getter
    /// returns, identical stack shape to the literal it replaces.
    ///
    /// All-or-nothing: every anchor below is matched with <c>ThrowIfInvalid</c> (or an explicit bounded
    /// search); if ANY is missing, the whole transpiler aborts, force-offs every sub-lever's flag for visible
    /// status, and returns the ORIGINAL, untouched instruction sequence — never a partially-patched method.
    /// The post-patch JIT-invoke check <see cref="PatchRegistry"/> performs automatically on every transpiled
    /// method (via <c>RuntimeHelpers.PrepareMethod</c>, right after patching, before any real call can reach
    /// it) is this hook's mandatory "IL-dump or JIT-invoke smoke test before any live connection" gate
    /// (docs/design/01 Test plan, RT1's blocking prerequisite) — it runs on every plugin load, server and
    /// client, not just once during development.
    /// </summary>
    internal static class SendZdosPatch
    {
        private static readonly MethodInfo s_innerBudgetGetter =
            AccessTools.PropertyGetter(typeof(SharedBudget), nameof(SharedBudget.InnerBudgetBytes));
        private static readonly MethodInfo s_congestionFloorGetter =
            AccessTools.PropertyGetter(typeof(SharedBudget), nameof(SharedBudget.CongestionFloorBytes));
        private static readonly MethodInfo s_writeInvalidSectorChunk =
            AccessTools.Method(typeof(InvalidSectorGuard), nameof(InvalidSectorGuard.WriteChunk));
        private static readonly MethodInfo s_q12Evaluate =
            AccessTools.Method(typeof(Q12Guard), nameof(Q12Guard.Evaluate));
        private static readonly MethodInfo s_timeGetTime =
            AccessTools.PropertyGetter(typeof(UnityEngine.Time), "time");
        private static readonly MethodInfo s_zPackageSize =
            AccessTools.Method(typeof(ZPackage), "Size");

        [HarmonyPatch(typeof(ZDOMan), "SendZDOs")]
        internal static class Patch_ZDOMan_SendZDOs_Budget
        {
            [HarmonyTranspiler]
            internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            {
                try
                {
                    return BuildPatchedBody(instructions, generator);
                }
                catch (Exception e)
                {
                    // Any missing/renamed anchor (a future Valheim build changing this method) lands here —
                    // fail open to vanilla for the WHOLE coordinated patch, never a partial one, per this
                    // class's own "all-or-nothing" contract.
                    NoodlesLog.Error("SendZDOs budget transpiler: anchor not found, disabling Q3/Q4/Q5/Q12/G2-N1 " +
                        $"and leaving ZDOMan.SendZDOs completely unpatched: {e.Message}");
                    SharedBudget.Q3.ForceOff($"SendZDOs transpiler anchor missing: {e.GetType().Name}");
                    SharedBudget.Q4.ForceOff($"SendZDOs transpiler anchor missing: {e.GetType().Name}");
                    SharedBudget.Q12.ForceOff($"SendZDOs transpiler anchor missing: {e.GetType().Name}");
                    SharedBudget.G2N1.ForceOff($"SendZDOs transpiler anchor missing: {e.GetType().Name}");
                    return instructions; // CodeMatcher copies its input, so this is still the pristine original sequence
                }
            }

            private static IEnumerable<CodeInstruction> BuildPatchedBody(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            {
                var m = new CodeMatcher(instructions, generator);

                // --- Site 1: outer gate `sendQueueSize > 10240` (SERVER:77058) -> `> SharedBudget.InnerBudgetBytes` ---
                m.MatchStartForward(new CodeMatch(OpCodes.Ldc_I4, SharedBudget.VanillaInnerBudgetBytes))
                 .ThrowIfInvalid("Q5 outer-gate literal (10240) not found");
                ReplacePreservingLabels(m, new CodeInstruction(OpCodes.Call, s_innerBudgetGetter));

                // --- Site 2: inner-budget base `10240 - sendQueueSize` (SERVER:77062) -> same shared getter (Q5 == Q3 always) ---
                // CONFIRMED THIS PASS (live smoke test against the real assembly_valheim.dll + this project's
                // actual 0Harmony.dll, see BuildPatchedBody's own remarks): this literal site IS a branch
                // target (`brtrue`/`ble` from the `flush`/`sendQueueSize>10240` checks above both land here) —
                // `CodeMatcher.SetInstruction` is a bare `codes[Pos] = instruction` (verified against the real
                // 0Harmony.dll's IL — it does NOT copy the replaced instruction's `.labels`), so replacing here
                // without explicitly re-attaching the original instruction's labels would silently orphan those
                // two branches. `ReplacePreservingLabels` below is required at this site, not just defensive.
                m.MatchStartForward(new CodeMatch(OpCodes.Ldc_I4, SharedBudget.VanillaInnerBudgetBytes))
                 .ThrowIfInvalid("Q3 inner-budget-base literal (10240) not found");
                ReplacePreservingLabels(m, new CodeInstruction(OpCodes.Call, s_innerBudgetGetter));

                // --- Site 3: congestion floor `num < 2048` (SERVER:77063) -> `< SharedBudget.CongestionFloorBytes` ---
                m.MatchStartForward(new CodeMatch(OpCodes.Ldc_I4, SharedBudget.VanillaCongestionFloorBytes))
                 .ThrowIfInvalid("Q4 congestion-floor literal (2048) not found");
                ReplacePreservingLabels(m, new CodeInstruction(OpCodes.Call, s_congestionFloorGetter));

                // --- G2-N1: replace the whole m_invalidSector if/else block (SERVER:77074-77088, `bool flag = false;`
                // through the branch that joins right before `float time = Time.time;`) with one helper call. ---
                m.MatchStartForward(new CodeMatch(OpCodes.Ldc_I4_0), new CodeMatch(OpCodes.Stloc_3))
                 .ThrowIfInvalid("G2-N1 start anchor (`bool flag = false;` before the m_invalidSector block) not found");

                int g2n1RemoveCount = 0;
                while (!(m.Opcode == OpCodes.Call && m.Operand is MethodInfo endCandidate && endCandidate == s_timeGetTime))
                {
                    m.Advance(1);
                    g2n1RemoveCount++;
                    if (g2n1RemoveCount > 120)
                        throw new InvalidOperationException("G2-N1 end anchor (`Time.get_time()` call) not found within expected range");
                }
                m.Advance(-g2n1RemoveCount);
                m.RemoveInstructions(g2n1RemoveCount);
                m.InsertAndAdvance(
                    new CodeInstruction(OpCodes.Ldarg_1),                          // peer
                    new CodeInstruction(OpCodes.Ldloc_2),                          // zPackage
                    new CodeInstruction(OpCodes.Call, s_writeInvalidSectorChunk),  // -> bool
                    new CodeInstruction(OpCodes.Stloc_3));                         // flag = result

                // --- Q12: capture the "skip this item, go to the next" target (the loop's own MoveNext-check
                // jump, SERVER:77092's `foreach` lowering — the ONLY long-form unconditional `br` in this method). ---
                m.MatchStartForward(new CodeMatch(OpCodes.Br))
                 .ThrowIfInvalid("Q12 loop-continuation jump (long-form `br` to the per-item loop's MoveNext check) not found");
                var skipToNextLabel = (Label)m.Operand;
                m.Advance(1); // step past it so the next search doesn't re-match this same instruction

                // --- Q12: replace `if (zPackage.Size() > num) { break; }` (SERVER:77094-77096) with a 3-way
                // decision (proceed / break-budget-exhausted / skip-this-oversized-item). ---
                // CONFIRMED THIS PASS (live smoke test, see class remarks): this project's actual 0Harmony.dll
                // normalizes every branch opcode to its LONG form when reading a method body (`ble.s` in the
                // raw IL / ilspycmd's disassembly reads back as plain `OpCodes.Ble` here, not `Ble_S`) — the
                // anchor below matches `Ble`, not the short form the raw-IL disassembly shows, precisely
                // because CodeMatcher operates on Harmony's own normalized instruction stream, not raw bytes.
                m.MatchStartForward(
                     new CodeMatch(OpCodes.Ldloc_2),
                     new CodeMatch(OpCodes.Callvirt, s_zPackageSize),
                     new CodeMatch(OpCodes.Ldloc_1),
                     new CodeMatch(OpCodes.Ble),
                     new CodeMatch(OpCodes.Leave))
                 .ThrowIfInvalid("Q12 per-item budget check (`zPackage.Size() > num`) not found");

                var continueLabel = (Label)m.InstructionAt(3).operand;   // the `ble` target: proceed to write this item
                var loopExitLabel = (Label)m.InstructionAt(4).operand;   // the `leave` target: budget exhausted, stop the loop
                // Clone item2's own `ldloc.s` operand from the very next load of it (SERVER:77098-77099,
                // `peer.m_forceSend.Remove(item2.m_uid)`) rather than constructing a raw index ourselves —
                // guarantees the exact operand shape Harmony's own IL reader uses for this pre-existing local,
                // whatever its runtime representation, since we copy it rather than guess it.
                var item2LoadOperand = m.InstructionAt(7).operand;

                var decisionLocal = generator.DeclareLocal(typeof(int));
                var doBreakLabel = generator.DefineLabel();

                m.RemoveInstructions(5);
                m.InsertAndAdvance(
                    new CodeInstruction(OpCodes.Ldloc_2),                    // zPackage
                    new CodeInstruction(OpCodes.Ldloc_1),                    // num
                    new CodeInstruction(OpCodes.Ldloc_S, item2LoadOperand),  // item2
                    new CodeInstruction(OpCodes.Call, s_q12Evaluate),        // -> int decision
                    new CodeInstruction(OpCodes.Stloc, decisionLocal),
                    new CodeInstruction(OpCodes.Ldloc, decisionLocal),
                    new CodeInstruction(OpCodes.Brfalse, continueLabel),     // decision == Proceed(0) -> write this item
                    new CodeInstruction(OpCodes.Ldloc, decisionLocal),
                    new CodeInstruction(OpCodes.Ldc_I4_1),
                    new CodeInstruction(OpCodes.Beq, doBreakLabel),          // decision == Break(1) -> stop the loop
                    new CodeInstruction(OpCodes.Br, skipToNextLabel),        // decision == SkipItem(2) -> next item, don't write this one
                    new CodeInstruction(OpCodes.Leave, loopExitLabel).WithLabels(doBreakLabel));

                return m.InstructionEnumeration();
            }

            /// <summary>
            /// `CodeMatcher.SetInstruction`/`SetInstructionAndAdvance` are a bare `codes[Pos] = instruction`
            /// (confirmed against this project's actual 0Harmony.dll IL this pass — <c>List&lt;CodeInstruction&gt;
            /// ::set_Item</c>, nothing else) — they do NOT carry over the replaced instruction's
            /// <c>.labels</c>/<c>.blocks</c>. Site 2 below (the inner-budget-base literal) IS an incoming
            /// branch target (both the `flush` check and the outer-gate check land there) — confirmed via a
            /// live dump of <c>PatchProcessor.GetOriginalInstructions</c> against the real
            /// <c>assembly_valheim.dll</c> this pass. Used at every literal-replacement site (not only the one
            /// confirmed to need it today) so a future Valheim build that adds a label at a currently
            /// label-free site fails loud at bake time instead of silently orphaning a branch.
            /// </summary>
            private static void ReplacePreservingLabels(CodeMatcher m, CodeInstruction replacement)
            {
                var old = m.Instruction;
                if (old.labels != null && old.labels.Count > 0) replacement.labels.AddRange(old.labels);
                if (old.blocks != null && old.blocks.Count > 0) replacement.blocks.AddRange(old.blocks);
                m.SetInstructionAndAdvance(replacement);
            }
        }
    }
}
