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
    /// 1.0.5 — one object's exception no longer stalls every other object the server simulates. Valheim batches
    /// most per-object updates through four static loops in <c>MonoUpdatersExtra</c> (<c>CustomFixedUpdate</c>,
    /// <c>CustomUpdate</c>, <c>CustomLateUpdate</c>, <c>UpdateAI</c>), each one
    /// <c>container.AddRange(source); foreach (item in container) item.Call(...); container.Clear();</c> with no
    /// try/catch anywhere up to <c>MonoUpdaters.FixedUpdate/Update/LateUpdate</c>. A single throwing item
    /// therefore (a) skips every item after it in that list, (b) escapes into MonoUpdaters and skips every LATER
    /// pass of that frame (a Ship throwing in the FixedUpdate pass skips Fish, every BaseAI, every Character's
    /// movement, Aoe, EffectArea...), and (c) leaves MonoUpdaters' shared container uncleared, so the next frame's
    /// first pass re-runs the leftovers ahead of its own list and appends to them.
    /// <para>
    /// Live on Proxima 2026-09-29 07:29:02: a player changed sail next to the server's copy of their ship
    /// (<see cref="NREGuards.GuardShipSailEffect"/>), 99 FixedUpdates in a row threw, every creature the server
    /// simulated froze for ~2 s, and the frame after the last throw called every ZSyncTransform ~98 times. On a
    /// client that is vanilla's problem for one player's own view; with this mod the dedicated server simulates
    /// every player's area, so one object that only misbehaves on a headless server stalls the whole world.
    /// </para>
    /// <para>
    /// Each loop's single interface call is swapped for a call through a guard with the same stack shape (the item
    /// and its arguments in, the same return value out), which runs that one item inside its own try/catch. The
    /// throwing item is left exactly where vanilla leaves it (aborted at the throw); every other item still updates,
    /// every later pass still runs, and the container is always cleared. Nothing else changes: an item that doesn't
    /// throw takes the same call it always did. The first exception of each kind (pass + object type + exception
    /// type) still goes to Unity's own <see cref="Debug.LogException(Exception)"/> — the same error line and stack
    /// trace vanilla prints — followed by one line saying it was contained; repeats are counted
    /// (<c>gameplay.updateIsolation.caught</c>) and rate-limited to one line plus one summary a minute per kind,
    /// instead of a full stack trace every frame.
    /// </para>
    /// </summary>
    internal static class UpdateIsolation
    {
        private static readonly Counter s_caught = Telemetry.GetCounter("gameplay.updateIsolation.caught");

        // Kinds that have already had their one full Unity stack trace. Main thread only (every MonoUpdaters loop
        // runs there). Capped so a pathological mod can't grow it without bound; past the cap a new kind is still
        // counted and rate-limited, it just doesn't get its own stack trace.
        private const int MaxDistinctKinds = 256;
        private static readonly HashSet<string> s_tracedKinds = new HashSet<string>();

        private const float RepeatWindowSeconds = 60f;

        private static bool Active
        {
            get
            {
                FeatureFlag f = Features_Gameplay.UpdateIsolation;
                return f != null && f.Active;
            }
        }

        // --- The four guards: same stack shape as the interface call each one replaces ---------------------------

        internal static void GuardedFixedUpdate(IMonoUpdater item, float deltaTime)
        {
            if (!Active) { item.CustomFixedUpdate(deltaTime); return; } // vanilla: an exception escapes, exactly as today
            try { item.CustomFixedUpdate(deltaTime); }
            catch (Exception e) { Report("FixedUpdate", item, e); }
        }

        internal static void GuardedUpdate(IMonoUpdater item, float deltaTime, float time)
        {
            if (!Active) { item.CustomUpdate(deltaTime, time); return; }
            try { item.CustomUpdate(deltaTime, time); }
            catch (Exception e) { Report("Update", item, e); }
        }

        internal static void GuardedLateUpdate(IMonoUpdater item, float deltaTime)
        {
            if (!Active) { item.CustomLateUpdate(deltaTime); return; }
            try { item.CustomLateUpdate(deltaTime); }
            catch (Exception e) { Report("LateUpdate", item, e); }
        }

        // The caller pops this result unused (MonoUpdatersExtra.UpdateAI ignores it), so what a throwing item
        // "returns" never reaches anything: false is only there to keep the stack balanced.
        internal static bool GuardedUpdateAI(IUpdateAI item, float deltaTime)
        {
            if (!Active) return item.UpdateAI(deltaTime);
            try { return item.UpdateAI(deltaTime); }
            catch (Exception e) { Report("UpdateAI", item, e); return false; }
        }

        private static void Report(string pass, object item, Exception e)
        {
            try
            {
                s_caught.Increment();
                string itemType = item?.GetType().Name ?? "null";
                string exType = e.GetType().Name;
                string key = "updateIsolation." + pass + "." + itemType + "." + exType;

                if (s_tracedKinds.Count < MaxDistinctKinds && s_tracedKinds.Add(key))
                {
                    Debug.LogException(e); // the exact error line + stack trace vanilla would have printed, once per kind
                }
                NoodlesLog.WarningRateLimited(NoodlesLogLevel.Normal, key,
                    $"update isolation: {itemType} threw {exType} in the {pass} pass ({Describe(item)}); every other " +
                    "object kept updating (vanilla would have skipped the rest of this frame's updates for every object " +
                    "on the server). Repeats of this kind are counted in gameplay.updateIsolation.caught and summarised " +
                    "once a minute.",
                    1, RepeatWindowSeconds);
            }
            catch
            {
                // Reporting must never throw back into the game's update loop — that is the one thing this class exists to stop.
            }
        }

        private static string Describe(object item)
        {
            try
            {
                if (item is Component c)
                {
                    if (c == null) return "a destroyed object"; // Unity's fake null: the C# object outlived its GameObject
                    Vector3 p = c.transform.position;
                    return $"'{c.gameObject.name}' at ({p.x:0}, {p.y:0}, {p.z:0})";
                }
                if (item is UnityEngine.Object o) return o == null ? "a destroyed object" : $"'{o.name}'";
                return item == null ? "a null entry" : "a " + item.GetType().FullName;
            }
            catch
            {
                return "an object that could not be described";
            }
        }

        // --- Transpilers --------------------------------------------------------------------------------------

        /// <summary>
        /// Swaps every <c>callvirt <paramref name="vanillaCall"/></c> in the loop for <c>call <paramref name="guard"/></c>
        /// (static, the interface instance as its first parameter — so the operand stack going in and coming out is
        /// identical). Confirmed against the 1.0.16 server build's IL: exactly one such call per method, inside the
        /// foreach's try block, carrying no incoming label (the loop's branch target is the MoveNext() call after it);
        /// labels/blocks are carried over anyway in case another mod's transpiler moved one there. Every occurrence is
        /// swapped, so a loop another mod has already duplicated or unrolled stays fully guarded.
        /// </summary>
        private static IEnumerable<CodeInstruction> SwapInterfaceCall(IEnumerable<CodeInstruction> instructions,
            MethodInfo vanillaCall, MethodInfo guard, string loopName)
        {
            if (vanillaCall == null || guard == null)
                throw new InvalidOperationException($"{loopName}: reflection lookup failed (vanilla={vanillaCall != null}, guard={guard != null})");

            var m = new CodeMatcher(instructions);
            int swapped = 0;
            while (true)
            {
                m.MatchStartForward(new CodeMatch(OpCodes.Callvirt, vanillaCall));
                if (!m.IsValid) break;

                var old = m.Instruction;
                var replacement = new CodeInstruction(OpCodes.Call, guard);
                if (old.labels != null && old.labels.Count > 0) replacement.labels.AddRange(old.labels);
                if (old.blocks != null && old.blocks.Count > 0) replacement.blocks.AddRange(old.blocks);
                m.SetInstructionAndAdvance(replacement);
                swapped++;
            }
            if (swapped == 0) throw new InvalidOperationException($"{loopName}: callvirt {vanillaCall.DeclaringType?.Name}.{vanillaCall.Name} not found");
            return m.InstructionEnumeration();
        }

        private static IEnumerable<CodeInstruction> TranspileOrLeave(IEnumerable<CodeInstruction> instructions,
            MethodInfo vanillaCall, string guardName, string loopName)
        {
            try
            {
                return SwapInterfaceCall(instructions, vanillaCall, AccessTools.Method(typeof(UpdateIsolation), guardName), loopName);
            }
            catch (Exception e)
            {
                // All four loops share one flag and one purpose: with any of them unguarded the server still stalls on the
                // first throw there, so a partial install would only hide the problem. Stand the whole feature down loudly.
                NoodlesLog.Error($"update isolation: MonoUpdatersExtra.{loopName} anchor not found, leaving it unpatched: {e.Message}");
                Features_Gameplay.UpdateIsolation?.ForceOff($"MonoUpdatersExtra.{loopName} transpiler anchor missing: {e.GetType().Name}");
                return instructions;
            }
        }

        [HarmonyPatch(typeof(MonoUpdatersExtra), nameof(MonoUpdatersExtra.CustomFixedUpdate))]
        internal static class Patch_MonoUpdatersExtra_CustomFixedUpdate
        {
            [HarmonyTranspiler]
            internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                TranspileOrLeave(instructions, AccessTools.Method(typeof(IMonoUpdater), nameof(IMonoUpdater.CustomFixedUpdate)),
                    nameof(GuardedFixedUpdate), "CustomFixedUpdate");
        }

        [HarmonyPatch(typeof(MonoUpdatersExtra), nameof(MonoUpdatersExtra.CustomUpdate))]
        internal static class Patch_MonoUpdatersExtra_CustomUpdate
        {
            [HarmonyTranspiler]
            internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                TranspileOrLeave(instructions, AccessTools.Method(typeof(IMonoUpdater), nameof(IMonoUpdater.CustomUpdate)),
                    nameof(GuardedUpdate), "CustomUpdate");
        }

        [HarmonyPatch(typeof(MonoUpdatersExtra), nameof(MonoUpdatersExtra.CustomLateUpdate))]
        internal static class Patch_MonoUpdatersExtra_CustomLateUpdate
        {
            [HarmonyTranspiler]
            internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                TranspileOrLeave(instructions, AccessTools.Method(typeof(IMonoUpdater), nameof(IMonoUpdater.CustomLateUpdate)),
                    nameof(GuardedLateUpdate), "CustomLateUpdate");
        }

        [HarmonyPatch(typeof(MonoUpdatersExtra), nameof(MonoUpdatersExtra.UpdateAI))]
        internal static class Patch_MonoUpdatersExtra_UpdateAI
        {
            [HarmonyTranspiler]
            internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                TranspileOrLeave(instructions, AccessTools.Method(typeof(IUpdateAI), nameof(IUpdateAI.UpdateAI)),
                    nameof(GuardedUpdateAI), "UpdateAI");
        }
    }
}
