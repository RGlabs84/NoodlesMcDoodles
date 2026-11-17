using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using NoodlesMcDoodles.Core.ConfigFiles;

namespace NoodlesMcDoodles.Smoothing
{
    /// <summary>
    /// C3, shipped standalone (2026-09-28 lead decision, after `tools/smooth-sim` showed C1's own buffered
    /// interpolation does not clearly beat vanilla — see SMOOTHING-IMPL.md §5.3). When on and C1
    /// (<see cref="Features_Smoothing.ClientSmoothing"/>) is off, vanilla's own
    /// <c>ZSyncTransform.SyncPosition</c> runs COMPLETELY UNCHANGED except for one thing: its hardcoded 2000ms
    /// dead-reckoning extrapolation cap becomes <see cref="SmoothingModule.ExtrapolationCapMs"/> — the same
    /// config key C1's own C3 knob already reads, reused here rather than adding a second one. Everything else
    /// vanilla does (the 0.2-per-tick lerp, the 5m snap, the parented-object branch, the rotation slerp) is
    /// completely untouched — this is a one-literal patch, not a rendering replacement.
    ///
    /// <b>ZERO DATA LOSS:</b> this patch's own body (the transpiler) never runs at game time — it runs once, at
    /// Harmony patch-apply time, rewriting IL. The method it produces is vanilla's own <c>SyncPosition</c> with
    /// one constant swapped for a call to <see cref="ResolveCapSeconds"/> — no new read or write surface is
    /// introduced; the resulting method still only reads the ZDO and writes the local <c>Transform</c>, exactly
    /// as vanilla's own unmodified method does (see <see cref="Patch_ZSyncTransform_SyncPosition"/>'s own proof
    /// of that for the full method body — unaffected by this one-instruction substitution).
    /// </summary>
    internal static class Patch_ZSyncTransform_SyncPosition_ShortExtrapolation
    {
        internal static ConfigEntry<bool> ActiveConfig;
        internal static FeatureFlag ShortExtrapolationFlag { get; private set; }

        [NoodlesModuleInit]
        internal static void Init()
        {
            ConfigFile config = NoodlesConfig.File;

            // 1.1.0: main file [4 - Client] ShortExtrapolation (was [ClientSmoothing] ShortExtrapolation.Active). C3 on
            // its own: when on (and C1 BufferedSmoothing is off), leaves vanilla's non-owner rendering
            // (ZSyncTransform.SyncPosition) unchanged and only shortens its hardcoded 2000ms (CLIENT:88005/
            // SERVER:87757) dead-reckoning timer to ExtrapolationCapMs. Default ON since 0.1.1 (NJ-AZ live test) —
            // measured net-positive on worst-case overshoot during a dropped-batch gap (tools/smooth-sim: 2.42m at
            // 500ms vs 3.88m at 2000ms, SMOOTHING-IMPL.md §5.4), equal to vanilla in ordinary play. Render-only. Inert
            // whenever C1 is on, the kill switch is off, or on a dedicated server (a listen-server host still renders).
            ActiveConfig = NoodlesConfig.ShortExtrapolation;
            ShortExtrapolationFlag = Features.Register("ClientSmoothing.ShortExtrapolation", ActiveConfig);

            PatchRegistry.Register("ClientSmoothing.ShortExtrapolation", typeof(Patch), PatchSide.Both);
        }

        // Vanilla's own literal, reused verbatim as the fail-open/opt-out value — CLIENT:88005/SERVER:87757.
        private const float VanillaCapSeconds = 2f;

        private static readonly MethodInfo s_resolveCapSecondsMethod =
            AccessTools.Method(typeof(Patch_ZSyncTransform_SyncPosition_ShortExtrapolation), nameof(ResolveCapSeconds));

        /// <summary>
        /// Called from inside vanilla's own (transpiled) <c>SyncPosition</c> body every tick a non-owned,
        /// non-parented object's dead-reckoning timer is capped — must be as cheap and as fail-safe as C1's own
        /// hot-path checks in <see cref="Patch_ZSyncTransform_SyncPosition"/>.
        /// </summary>
        internal static float ResolveCapSeconds()
        {
            try
            {
                if (Side.IsDedicatedServerProcess) return VanillaCapSeconds; // C5 — nothing renders there.
                if (!Features_Smoothing.ShortExtrapolation.Active) return VanillaCapSeconds; // flag off / kill switch off (FeatureFlag.Active folds both in).
                if (Features_Smoothing.ClientSmoothing.Active) return VanillaCapSeconds; // C1 already owns this concept when it's the one rendering.
                return SmoothingModule.ExtrapolationCapMs.Value / 1000f;
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(Patch_ZSyncTransform_SyncPosition_ShortExtrapolation), e);
                return VanillaCapSeconds; // fail open to vanilla's own value — never let this read break movement sync.
            }
        }

        [HarmonyPatch(typeof(ZSyncTransform), "SyncPosition")]
        private static class Patch
        {
            /// <summary>
            /// Ground truth (re-verified directly against both decompiles this pass, not assumed): `SyncPosition`
            /// contains exactly TWO `2f` literals, not one — `CLIENT:87960`/`SERVER:87712` (the parented
            /// branch's own identical-shaped dead-reckoning cap on the same `m_targetPosTimer` field, used for
            /// the parent-relative position calculation) and `CLIENT:88005`/`SERVER:87757` (the plain-branch
            /// extrapolation cap this feature exists to shorten — the one this design's own citation names).
            /// The parented branch always `return`s before the plain-branch code is reachable
            /// (`CLIENT:87925-87991`), so its IL must be emitted before the plain branch's in any straightforward
            /// compilation of this method (no goto/loop restructuring crosses that boundary) — the later of the
            /// two matches, by instruction index, is unambiguously the plain-branch anchor. Never touching the
            /// parented branch's own `2f` matters: C1's own Guard 4 always defers that branch to vanilla
            /// untouched, and this feature must not quietly do something C1 itself refuses to do.
            /// <para>
            /// Asserts exactly 2 matches and fails closed (throws, caught by <see cref="PatchRegistry"/>, forces
            /// only THIS feature off) if the method's shape ever changes — never silently patches a guess.
            /// </para>
            /// </summary>
            [HarmonyTranspiler]
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var list = new List<CodeInstruction>(instructions);
                var matches = new List<int>();
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].opcode == OpCodes.Ldc_R4 && list[i].operand is float f && f == VanillaCapSeconds)
                        matches.Add(i);
                }

                if (matches.Count != 2)
                {
                    throw new InvalidOperationException(
                        $"NoodlesMcDoodles ShortExtrapolation: expected exactly 2 occurrences of {VanillaCapSeconds}f " +
                        $"in ZSyncTransform.SyncPosition (one per branch), found {matches.Count} — method shape " +
                        "changed, refusing a partial patch.");
                }

                // Mutate in place (not a new CodeInstruction) so any labels/exception blocks Harmony already
                // attached to this instruction survive the substitution.
                CodeInstruction anchor = list[matches[matches.Count - 1]];
                anchor.opcode = OpCodes.Call;
                anchor.operand = s_resolveCapSecondsMethod;

                return list;
            }
        }
    }
}
