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
    /// S5 (docs/design/05, Hook H3) — <c>EnvMan.GetBiome()</c> (SERVER:95904/CLIENT:96447) AND
    /// <c>EnvMan.UpdateEnvironment(long, BiomeSector)</c> (SERVER:95812/CLIENT:96355) both independently bail
    /// out to a "no biome/no environment" default the moment <c>Utils.GetMainCamera()</c> (which reads
    /// <c>Camera.main</c>, permanently null in batch mode) returns null — confirmed this pass, both methods,
    /// both builds. A fix touching <c>GetBiome</c> alone leaves <c>UpdateEnvironment</c>'s own, separate camera
    /// check bailing BEFORE it ever calls <c>QueueEnvironment</c>, which is the only thing that populates
    /// <c>GetCurrentEnvironment()</c> — and <c>EnvMan.UpdateWind</c> only computes real wind when that's
    /// non-null. Both call sites must be redirected, or wind stays at its zero/default state on a dedicated
    /// server even with "the" biome fallback fixed on paper — the exact fidelity bug this lever exists to close
    /// would remain closed for <c>Ship.GetSailForce</c>/<c>Windmill.GetPowerOutput</c> (both read
    /// <c>EnvMan.instance.GetWindDir()</c>/<c>GetWindIntensity()</c>).
    /// <para>
    /// Mechanism: a disabled, never-rendering proxy <c>Camera</c> component, repositioned lazily (on-demand
    /// inside the same call vanilla already makes, not a new periodic driver — see <see cref="GetProxyCamera"/>),
    /// and a transpiler that redirects ONLY these two specific <c>Utils.GetMainCamera()</c> call sites to
    /// return it instead of <c>Camera.main</c>. No other call site of <c>Utils.GetMainCamera()</c> anywhere
    /// else in the assembly is touched.
    /// </para>
    /// </summary>
    internal static class HeadlessBiomeWind
    {
        private static Camera s_proxyCamera;
        private static float s_lastReposition = float.NegativeInfinity;

        // Cheap and rare enough (EnvMan ticks its environment period on the order of minutes, not frames —
        // m_environmentDuration; GetBiome is called from the same cadence) that a fixed, short interval is
        // plenty responsive without needing its own MonoBehaviour driver (CORE-API.md: don't start a second
        // periodic driver when the existing call sites already provide one at a fine-enough cadence).
        private const float RepositionIntervalSeconds = 5f;

        private static void EnsureProxyCamera()
        {
            if (s_proxyCamera != null) return;
            var go = new GameObject("NoodlesMcDoodles.HeadlessEnvProxyCamera");
            UnityEngine.Object.DontDestroyOnLoad(go);
            s_proxyCamera = go.AddComponent<Camera>();
            s_proxyCamera.enabled = false; // never rendered, never touches the render pipeline
            go.tag = "Untagged"; // deliberately NOT "MainCamera" — must never be found by anything else's Camera.main
        }

        /// <summary>
        /// Best-effort "nearest connected peer" position. <c>GetBiome</c>/<c>UpdateEnvironment</c> operate on
        /// one process-global <see cref="EnvMan"/> instance with no "which zone" parameter to target exactly —
        /// documented as an approximation (blueprint's own words), not claimed exact: repositions to the first
        /// connected peer's character position found, or the world origin if none is connected/instantiated yet.
        /// A single process-global proxy cannot be biome-correct for multiple simultaneously server-held zones
        /// in different biomes at once regardless of how this position is chosen.
        /// </summary>
        private static void RepositionIfStale()
        {
            float now = Time.realtimeSinceStartup;
            if (now - s_lastReposition < RepositionIntervalSeconds) return;
            s_lastReposition = now;

            Vector3 target = Vector3.zero;
            try
            {
                if (ZNet.instance != null)
                {
                    List<ZDO> characters = ZNet.instance.GetAllCharacterZDOS();
                    if (characters != null)
                    {
                        foreach (ZDO zdo in characters)
                        {
                            if (zdo == null) continue;
                            target = zdo.GetPosition();
                            break; // "a" connected peer, not necessarily nearest to any specific zone — see remarks
                        }
                    }
                }
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(HeadlessBiomeWind) + ".RepositionIfStale", e);
            }
            s_proxyCamera.transform.position = target;
            Telemetry.GetCounter("gameplay.s5.proxyRepositions").Increment();
        }

        private static bool IsHeadlessServerSignal()
        {
            // Gate on IsServer() (not merely IsDedicatedServerProcess) so a listen-server host — a real player,
            // with a real camera — never gets redirected; gate on the batch/null-graphics-device signal (not a
            // bare Camera.main==null check) so any OTHER installed mod that happens to instantiate a
            // MainCamera-tagged camera on the dedicated process doesn't silently re-enable vanilla's broken
            // fallback with no error logged (blueprint's own guard requirement).
            return ZNet.instance != null && ZNet.instance.IsServer() && Side.IsDedicatedServerProcess;
        }

        /// <summary>The transpiler redirect target — same signature as <c>Utils.GetMainCamera()</c> (zero args, returns Camera).</summary>
        internal static Camera GetProxyCamera()
        {
            if (!Features_Gameplay.HeadlessBiomeWind.Active) return Utils.GetMainCamera();
            if (!IsHeadlessServerSignal())
            {
                Telemetry.GetCounter("gameplay.s5.fallbackToVanillaCount").Increment();
                return Utils.GetMainCamera();
            }
            try
            {
                EnsureProxyCamera();
                RepositionIfStale();
                return s_proxyCamera;
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(HeadlessBiomeWind) + ".GetProxyCamera", e);
                Telemetry.GetCounter("gameplay.s5.fallbackToVanillaCount").Increment();
                return Utils.GetMainCamera(); // fail open to vanilla's own (null-on-headless) behaviour
            }
        }

        /// <summary>Kill-switch flush — destroys the proxy so a disabled mod leaves no stray GameObject behind.</summary>
        internal static void FlushOnDisable()
        {
            if (s_proxyCamera == null) return;
            try { UnityEngine.Object.Destroy(s_proxyCamera.gameObject); }
            catch (Exception e) { NoodlesLog.PatchException(nameof(HeadlessBiomeWind) + ".FlushOnDisable", e); }
            s_proxyCamera = null;
            s_lastReposition = float.NegativeInfinity;
        }

        [HarmonyPatch(typeof(EnvMan), "GetBiome")]
        internal static class Patch_EnvMan_GetBiome_HeadlessCamera
        {
            [HarmonyTranspiler]
            internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                try
                {
                    return RedirectGetMainCamera(instructions, "EnvMan.GetBiome");
                }
                catch (Exception e)
                {
                    NoodlesLog.Error($"S5: EnvMan.GetBiome anchor not found, leaving unpatched: {e.Message}");
                    Features_Gameplay.HeadlessBiomeWind.ForceOff($"EnvMan.GetBiome transpiler anchor missing: {e.GetType().Name}");
                    return instructions;
                }
            }
        }

        [HarmonyPatch(typeof(EnvMan), "UpdateEnvironment")]
        internal static class Patch_EnvMan_UpdateEnvironment_HeadlessCamera
        {
            [HarmonyTranspiler]
            internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                try
                {
                    return RedirectGetMainCamera(instructions, "EnvMan.UpdateEnvironment");
                }
                catch (Exception e)
                {
                    NoodlesLog.Error($"S5: EnvMan.UpdateEnvironment anchor not found, leaving unpatched: {e.Message}");
                    Features_Gameplay.HeadlessBiomeWind.ForceOff($"EnvMan.UpdateEnvironment transpiler anchor missing: {e.GetType().Name}");
                    return instructions;
                }
            }
        }

        /// <summary>
        /// Both target methods call <c>Utils.GetMainCamera()</c> exactly once each (confirmed this pass via IL
        /// dump, both builds: GetBiome's call is its very first instruction, IL_0000; UpdateEnvironment's is at
        /// IL_0034, straight-line code, not a branch target in either case) — a single-instruction
        /// <c>call -&gt; call</c> swap, matching signature exactly (zero args, returns Camera), so no argument
        /// shuffling or label preservation is needed at either site.
        /// </summary>
        private static IEnumerable<CodeInstruction> RedirectGetMainCamera(IEnumerable<CodeInstruction> instructions, string methodLabel)
        {
            var getMainCamera = AccessTools.Method(typeof(Utils), nameof(Utils.GetMainCamera));
            var proxy = AccessTools.Method(typeof(HeadlessBiomeWind), nameof(GetProxyCamera));

            var m = new CodeMatcher(instructions);
            m.MatchStartForward(new CodeMatch(OpCodes.Call, getMainCamera))
             .ThrowIfInvalid($"{methodLabel}: Utils.GetMainCamera() call not found");

            // Same opcode (Call), same signature (zero args, returns Camera) — only the target method changes.
            // Neither anchor site is a branch target (confirmed this pass via IL dump; GetBiome's is IL_0000,
            // the method's own entry point; UpdateEnvironment's is straight-line code at IL_0034), so — unlike
            // Transport/SendZdosPatch.cs's site 2 — there is no incoming label to carry forward here; still
            // checked defensively rather than assumed, matching this project's established discipline.
            var replacement = new CodeInstruction(OpCodes.Call, proxy);
            var old = m.Instruction;
            if (old.labels != null && old.labels.Count > 0) replacement.labels.AddRange(old.labels);
            if (old.blocks != null && old.blocks.Count > 0) replacement.blocks.AddRange(old.blocks);
            m.SetInstructionAndAdvance(replacement);
            return m.InstructionEnumeration();
        }
    }
}
