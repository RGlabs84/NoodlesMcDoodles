using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using NoodlesMcDoodles.Core;

namespace NoodlesMcDoodles.ZoneSim
{
    /// <summary>
    /// Hook 6 (Z6) — docs/design/04: <c>ZNetScene</c> create/destroy rate + per-frame instantiation budget.
    /// Two independent transpilers, different methods, same lever.
    /// </summary>
    internal static class CreateObjectsBudget
    {
        /// <summary>
        /// <c>ZNetScene.CreateObjects</c> (same method Hook 2/<see cref="Patch_ZNetScene_CreateObjects_ObjectFeed"/> prefixes — a prefix
        /// always runs before the (transpiled) original body regardless of any transpiler on the same method,
        /// so Hook 2's appended objects are visible to this raised budget with no extra work). Body confirmed
        /// this session: <c>int maxCreatedPerFrame = 10; if (InLoadingScreen()) { maxCreatedPerFrame = 100; }</c>
        /// (<c>SERVER:82139-82144</c>/<c>CLIENT:82369-82374</c>). <c>InLoadingScreen()</c>
        /// (<c>SERVER:82130-82136</c>) is <c>Player.m_localPlayer == null ? true : ...IsTeleporting()</c> — on a
        /// true dedicated server <c>Player.m_localPlayer</c> is permanently null (no local character ever
        /// spawns there), so <c>InLoadingScreen()</c> is permanently true and a dedicated server ALWAYS takes
        /// the <c>100</c> branch already, today, unmodded. The transpiler therefore targets ONLY the <c>100</c>
        /// literal — the <c>10</c> literal is a dead branch on a real dedicated server and must not be touched
        /// (this project's own catalog explicitly flags swapping the wrong one as a footgun that would compile
        /// clean and change nothing).
        /// </summary>
        [HarmonyPatch(typeof(ZNetScene), "CreateObjects")]
        internal static class Patch_ZNetScene_CreateObjects_FrameBudget
        {
            [HarmonyTranspiler]
            internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var matcher = new CodeMatcher(instructions)
                    .MatchStartForward(new CodeMatch(OpCodes.Ldc_I4_S, (sbyte)100))
                    .ThrowIfInvalid("NoodlesMcDoodles Z6 anchor: ldc.i4.s 100 (InLoadingScreen()==true branch) not found in ZNetScene.CreateObjects");
                matcher.SetInstructionAndAdvance(CodeInstruction.Call(typeof(ZoneSimConfig), nameof(ZoneSimConfig.EffectiveMaxCreatedPerFrame)));
                return matcher.InstructionEnumeration();
            }
        }

        /// <summary>
        /// <c>ZNetScene.Update()</c> — <c>m_createDestroyTimer += deltaTime; if (m_createDestroyTimer &gt;= 1f /
        /// 30f) { ...; CreateDestroyObjects(); }</c> (<c>SERVER:82296-82305</c>/<c>CLIENT:...</c>, one occurrence
        /// of the compile-time-folded <c>1f/30f</c> constant). <c>CreateDestroyHz</c>'s companion cadence knob —
        /// left at vanilla's 30Hz by default; an admin opting into 60Hz doubles this pass's own CPU cost.
        /// </summary>
        [HarmonyPatch(typeof(ZNetScene), "Update")]
        internal static class Patch_ZNetScene_Update_CreateDestroyCadence
        {
            [HarmonyTranspiler]
            internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var matcher = new CodeMatcher(instructions)
                    .MatchStartForward(new CodeMatch(OpCodes.Ldc_R4, 1f / 30f))
                    .ThrowIfInvalid("NoodlesMcDoodles Z6 anchor: 1f/30f (create/destroy cadence gate) not found in ZNetScene.Update");
                matcher.SetInstructionAndAdvance(CodeInstruction.Call(typeof(ZoneSimConfig), nameof(ZoneSimConfig.EffectiveCreateDestroyIntervalSeconds)));
                return matcher.InstructionEnumeration();
            }
        }

        /// <summary>
        /// Catalog's explicit ask (Hook 6 "Guard"): log which branch is structurally active at startup so an
        /// operator auditing this hook doesn't chase the wrong literal by hand later. This is a decompile-derived
        /// FACT true for any real `-batchmode -nographics` dedicated server (see class remarks above), not a
        /// runtime probe — logged once, unconditionally (informational, costs nothing, no feature gate needed).
        /// </summary>
        internal static void LogActiveBranchOnce()
        {
            NoodlesLog.Verbose("ZoneSim Z6: ZNetScene.CreateObjects's InLoadingScreen()==true branch (the '100' " +
                "literal) is ALWAYS the one a real dedicated server takes (Player.m_localPlayer is permanently " +
                "null server-side) — MaxCreatedPerFrame tunes that branch; the '10' literal is a dead branch here " +
                "and is left untouched.");
        }

        // --- 1.0.9 (frame-stall fix, SP/harness108 1.0.7 evidence: phantom walking a 128m loop at an Ashlands
        // base hit 20 frames >50ms / 10 >100ms in 300s, max 196ms; exploring: 16/7, max 162ms; kill-switch off
        // same boot: 0 frames >50ms, max 14ms; GC never involved; slow-frame attribution: ZNetScene.CreateObjects
        // -> CreateObject ~34.5%) -- Z6's own per-tick WALL-CLOCK budget, on top of MaxCreatedPerFrame's existing
        // COUNT cap. Root cause: vanilla's count cap (`num = Mathf.Max(backlog/100, maxCreatedPerFrame)`,
        // SERVER:82167) is sized for a dedicated server's own single, permanently-void reference position, which
        // never presents a multi-thousand-object backlog in one tick -- ObjectFeed (Hook 2) legitimately CAN, the
        // instant a peer's ring first reaches a freshly-populated zone (measured here: 2,000+ objects at once in
        // an Ashlands base). At the measured ~1.5ms/object for dense Ashlands prefabs, the existing 100-400
        // object COUNT cap alone can still cost 100-400 * 1.5ms in a single frame -- a time budget bounds that
        // directly, independent of any one prefab's own instantiate cost.
        //
        // Mechanism: a Stopwatch reset once per real create/destroy tick (the Prefix below, on this same
        // ZNetScene.CreateObjects method Hook 2's own ObjectFeed prefix and Hook 6's own FrameBudget transpiler
        // above already share -- Harmony runs every prefix on a method before its (transpiled) body regardless
        // of registration order, so this reset always lands before CreateObjectsSorted/CreateDistantObjects run
        // this same tick), consulted by EffectiveCreateCap below every time vanilla's own loop re-checks its cap.
        // HARD RULE (task brief): this NEVER touches ZNetScene.CreateObject itself, and CreateObjectsSorted/
        // CreateDistantObjects never skip an item without calling CreateObject on it first -- this only changes
        // what vanilla's own EXISTING `created >= num` / `created > maxCreatedPerFrame` break check compares
        // against, so the loop stops exactly where vanilla's own code already knows how to stop (never reaching
        // the "CreateObject returned null" destroy branch some other way).
        private static readonly System.Diagnostics.Stopwatch s_tickStopwatch = new System.Diagnostics.Stopwatch();
        private static int s_createdThisTick;
        private static bool s_budgetAppliedThisTick;

        /// <summary>
        /// A THIRD patch class on <c>ZNetScene.CreateObjects</c> (joins Hook 2's own <c>ObjectFeed</c> prefix and
        /// Hook 6's own <c>FrameBudget</c> transpiler above on this exact method -- multiple independent patch
        /// classes safely sharing one Harmony target is already this project's own established pattern, e.g.
        /// this file's two existing classes on two different methods plus ObjectFeed.cs's prefix on this one).
        /// Prefix resets this tick's Stopwatch/counters before anything this tick's create pass does; Postfix
        /// (runs after the transpiled body, i.e. after BOTH CreateObjectsSorted and CreateDistantObjects have
        /// run) reports the backlog left over and whether this tick was actually time-budget-limited.
        /// </summary>
        [HarmonyPatch(typeof(ZNetScene), "CreateObjects")]
        internal static class Patch_ZNetScene_CreateObjects_TimeBudgetTick
        {
            [HarmonyPrefix]
            internal static void Prefix()
            {
                // Unconditional and cheap (one Stopwatch.Restart() + two field writes) -- EffectiveCreateCap
                // itself re-checks ServerZoneSimFlag/authority before ever acting on these, so a plain client
                // or a ZoneSim-off server pays this and nothing else changes (byte-identical vanilla behaviour).
                s_tickStopwatch.Restart();
                s_createdThisTick = 0;
                s_budgetAppliedThisTick = false;
            }

            [HarmonyPostfix]
            internal static void Postfix(List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects)
            {
                if (!ZoneSimConfig.ServerZoneSimFlag.Active || !ZoneSimConfig.IsAuthoritativeProcess()) return;
                try
                {
                    int backlog = 0;
                    foreach (ZDO zdo in currentNearObjects) if (!zdo.Created) backlog++;
                    foreach (ZDO zdo in currentDistantObjects) if (!zdo.Created) backlog++;
                    Telemetry_ZoneSim.SetCreateBacklog(backlog);
                    Telemetry_ZoneSim.SetCreatedThisTick(s_createdThisTick);
                    if (s_budgetAppliedThisTick) Telemetry_ZoneSim.CreateBudgetLimitedTicksTotal.Increment();
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_ZNetScene_CreateObjects_TimeBudgetTick) + ".Postfix", e);
                }
            }
        }

        /// <summary>
        /// Transpiles <c>CreateObjectsSorted</c>'s own <c>if (created &gt;= num) { break; }</c>
        /// (SERVER:82178-82181) -- confirmed this session against the real <c>assembly_valheim.dll</c> (the
        /// dedicated-server Managed folder, via <c>ilspycmd -il</c>): <c>ldarg.3; ldind.i4; ldloc.1; blt.s
        /// IL_0139; leave.s IL_0155</c> (Harmony's own method-body reader normalizes short branches to their
        /// long form when read back through <see cref="CodeMatcher"/> -- confirmed precedent, see
        /// Transport/SendZdosPatch.cs's own "CONFIRMED THIS PASS" remark on this exact fact against this
        /// project's real 0Harmony.dll -- hence matching <c>Blt</c>/<c>Leave</c> below, not the short forms the
        /// raw ilspycmd dump shows). <c>num</c> (local 1) is computed once before the loop
        /// (<c>Mathf.Max(backlog/100, maxCreatedPerFrame)</c>, SERVER:82167) and never reassigned -- every
        /// per-iteration re-read of it is this one <c>ldloc.1</c>, so replacing what THIS read pushes (never the
        /// stored local itself) is what makes the budget check re-evaluate fresh every iteration instead of once
        /// for the whole loop. Inserted right after the untouched <c>ldloc.1</c> (still pushes vanilla's own
        /// <c>num</c> first, so an off/vanilla-process EffectiveCreateCap call below is a pure passthrough):
        /// pushes a fresh <c>created</c> (its own <c>ldarg.3; ldind.i4</c> pair, a side-effect-free re-read of
        /// the same byref int the original left/first operand already read) and calls
        /// <see cref="EffectiveCreateCap"/>, leaving exactly one int on the stack where vanilla's own single
        /// <c>ldloc.1</c> used to -- the <c>blt.s</c> right after is untouched and never needs to know anything
        /// changed.
        /// </summary>
        [HarmonyPatch(typeof(ZNetScene), "CreateObjectsSorted")]
        internal static class Patch_ZNetScene_CreateObjectsSorted_TimeBudget
        {
            [HarmonyTranspiler]
            internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                try
                {
                    var matcher = new CodeMatcher(instructions)
                        .MatchStartForward(
                            new CodeMatch(OpCodes.Ldarg_3),
                            new CodeMatch(OpCodes.Ldind_I4),
                            new CodeMatch(OpCodes.Ldloc_1),
                            new CodeMatch(OpCodes.Blt),
                            new CodeMatch(OpCodes.Leave))
                        .ThrowIfInvalid("NoodlesMcDoodles Z6 time-budget anchor: `if (created >= num) break;` " +
                            "(SERVER:82178-82181) not found in ZNetScene.CreateObjectsSorted");
                    matcher.Advance(3); // Ldarg_3(0) Ldind_I4(1) Ldloc_1(2) -> positioned at Blt(3)
                    matcher.InsertAndAdvance(
                        new CodeInstruction(OpCodes.Ldarg_3),
                        new CodeInstruction(OpCodes.Ldind_I4),
                        CodeInstruction.Call(typeof(CreateObjectsBudget), nameof(EffectiveCreateCap)));
                    return matcher.InstructionEnumeration();
                }
                catch (Exception e)
                {
                    NoodlesLog.Error("NoodlesMcDoodles Z6 time-budget transpiler (CreateObjectsSorted) anchor " +
                        "not found -- leaving this method completely unpatched (vanilla's own count-only cap, " +
                        $"pre-1.0.9 behaviour) rather than risk a partial/unsafe patch: {e}");
                    return instructions; // CodeMatcher copies its input, so this is still the pristine original sequence
                }
            }
        }

        /// <summary>
        /// Transpiles <c>CreateDistantObjects</c>'s own <c>if (created &gt; maxCreatedPerFrame) { break; }</c>
        /// (SERVER:82217-82220) -- confirmed this session, same method as the top-of-method early return
        /// (<c>if (created &gt; maxCreatedPerFrame) return;</c>, SERVER:82204-82207): both compile to the
        /// identical 3-instruction prefix <c>ldarg.3; ldind.i4; ldarg.2</c>, which is why this anchor's trailing
        /// <c>Leave</c> (this site) vs <c>Ret</c> (the unrelated early-return site) is load-bearing, not
        /// decorative -- it is what lets <c>MatchStartForward</c> skip straight past the early return and land
        /// only on the real in-loop break. Same shape and same <see cref="EffectiveCreateCap"/> helper as
        /// <see cref="Patch_ZNetScene_CreateObjectsSorted_TimeBudget"/> above -- <c>created - 1</c> (what a
        /// budget-limited call returns) fails a <c>&gt;=</c>-continue AND a <c>&gt;</c>-continue re-test
        /// identically, so one helper serves both this file's <c>blt</c> site and this method's own <c>ble</c>
        /// site without two separate formulas.
        /// </summary>
        [HarmonyPatch(typeof(ZNetScene), "CreateDistantObjects")]
        internal static class Patch_ZNetScene_CreateDistantObjects_TimeBudget
        {
            [HarmonyTranspiler]
            internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                try
                {
                    var matcher = new CodeMatcher(instructions)
                        .MatchStartForward(
                            new CodeMatch(OpCodes.Ldarg_3),
                            new CodeMatch(OpCodes.Ldind_I4),
                            new CodeMatch(OpCodes.Ldarg_2),
                            new CodeMatch(OpCodes.Ble),
                            new CodeMatch(OpCodes.Leave))
                        .ThrowIfInvalid("NoodlesMcDoodles Z6 time-budget anchor: `if (created > maxCreatedPerFrame) break;` " +
                            "(SERVER:82217-82220) not found in ZNetScene.CreateDistantObjects");
                    matcher.Advance(3); // Ldarg_3(0) Ldind_I4(1) Ldarg_2(2) -> positioned at Ble(3)
                    matcher.InsertAndAdvance(
                        new CodeInstruction(OpCodes.Ldarg_3),
                        new CodeInstruction(OpCodes.Ldind_I4),
                        CodeInstruction.Call(typeof(CreateObjectsBudget), nameof(EffectiveCreateCap)));
                    return matcher.InstructionEnumeration();
                }
                catch (Exception e)
                {
                    NoodlesLog.Error("NoodlesMcDoodles Z6 time-budget transpiler (CreateDistantObjects) anchor " +
                        "not found -- leaving this method completely unpatched (vanilla's own count-only cap, " +
                        $"pre-1.0.9 behaviour) rather than risk a partial/unsafe patch: {e}");
                    return instructions;
                }
            }
        }

        /// <summary>
        /// Shared decision function both transpilers above call on every iteration of vanilla's own capped
        /// create loop, immediately after a successful <c>CreateObject</c> + <c>created++</c> -- never wraps
        /// <c>CreateObject</c> itself (HARD RULE, task brief: only ever changes what the loop's own EXISTING
        /// break condition compares against). Returns <paramref name="originalCap"/> unchanged (byte-identical
        /// vanilla behaviour) unless ALL of: ZoneSim is active and this process is authoritative; the per-tick
        /// floor (<c>ZoneSim.MinCreatedPerTick</c>) has already been met THIS tick (combined across both
        /// CreateObjectsSorted and CreateDistantObjects -- they share the same <c>created</c> byref, so the
        /// floor is "at least this many, near+distant combined, per tick", exactly the task brief's own per-tick
        /// floor); and the per-tick Stopwatch (reset once per real tick by
        /// <see cref="Patch_ZNetScene_CreateObjects_TimeBudgetTick"/>'s Prefix) has exceeded
        /// <c>ZoneSim.CreateBudgetMs</c>. Only then returns <c>createdSoFar - 1</c>, which the caller's own
        /// existing <c>blt</c>/<c>ble</c> + <c>break</c> does the rest with -- created-1 always fails BOTH a
        /// strict-less-than and a less-or-equal re-test against createdSoFar, so this one formula is correct for
        /// CreateObjectsSorted's <c>&lt;</c> continue-check AND CreateDistantObjects' <c>&lt;=</c> continue-check
        /// without needing two separate formulas.
        /// </summary>
        // 1.0.9 ROUND 2 (measured on the first harness108 1.0.9 run, SP/harness108/server-console.fix-1.0.9-frame-
        // stall.log): the first attempt (floor 4 + 7ms budget) barely moved the numbers -- Phase B CreateObject
        // calls=50124 vs 1.0.7's 50130, essentially unchanged. Root cause found by dumping the actual per-slow-
        // frame breakdown: several remaining slow frames show CreateObject at 1.4-7.4ms/object average (created
        // 23/52/68 objects that tick) -- i.e., cost is concentrated in occasional expensive INDIVIDUAL objects
        // (plausibly a first-instantiation-of-this-prefab-type cost: shader/material/mesh setup only paid once
        // per prefab type per process) that can land inside the UNCONDITIONAL floor or simply be the one object
        // "in flight" when the budget check fires (a check between iterations can never shrink one already-
        // running Instantiate call's own cost, only stop MORE from being attempted). A count/time floor alone
        // cannot bound that -- so this round adds a THIRD signal: back off additional work once RECENT frames
        // are already running hot, so this loop stops compounding an already-marginal stretch instead of only
        // reacting after a frame has already gone over 50ms. Reuses Core/FrameTiming.cs's existing ~20-frame EMA
        // (already computed every frame for Transport's Q13 lever) rather than inventing a second smoothing
        // mechanism -- RecentFrameHotMs=20 is deliberately well under the profiler's own 50ms slow-frame line
        // (a single healthy-baseline frame at this mod's own measured ~2-6ms mean, per harness108 phase A/D,
        // needs 2-3 elevated frames in a row to push the EMA past 20 -- reacting to a developing run of stalls,
        // not a single blip).
        private const double RecentFrameHotMs = 20.0;

        private static int EffectiveCreateCap(int originalCap, int createdSoFar)
        {
            if (!ZoneSimConfig.ServerZoneSimFlag.Active || !ZoneSimConfig.IsAuthoritativeProcess()) return originalCap;
            s_createdThisTick = createdSoFar;
            if (createdSoFar < ZoneSimConfig.MinCreatedPerTick.Value) return originalCap;
            // Once recent frames are already running hot, treat the budget as spent immediately (skip straight
            // to the cut) rather than waiting for CreateBudgetMs to elapse fresh -- never below the floor above.
            bool recentFramesHot = NoodlesMcDoodles.Core.FrameTiming.RecentMeanMs > RecentFrameHotMs;
            if (!recentFramesHot && s_tickStopwatch.Elapsed.TotalMilliseconds < ZoneSimConfig.CreateBudgetMs.Value) return originalCap;
            s_budgetAppliedThisTick = true;
            return createdSoFar - 1;
        }
    }
}
