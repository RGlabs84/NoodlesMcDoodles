using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace NoodlesMcDoodles.Core
{
    /// <summary>Which process(es) a patch class should even be offered to <c>CreateClassProcessor</c> on. See <see cref="Side"/>.</summary>
    internal enum PatchSide
    {
        Both,
        ServerOnly,
        ClientOnly,
    }

    /// <summary>
    /// Every module's one and only way to install a Harmony patch. Modules call <see cref="Register"/> for
    /// every patch class they own during their own module Init() (before <see cref="ApplyAll"/> runs), then
    /// <see cref="NoodlesPlugin"/> calls <see cref="ApplyAll"/> exactly once, after every module has
    /// registered. See `docs/impl/CORE-API.md` "Registering a patch class" for the exact call shape.
    /// </summary>
    internal static class PatchRegistry
    {
        private sealed class Entry
        {
            public string FeatureName;
            public Type PatchClass;
            public PatchSide Side;
        }

        private static readonly List<Entry> s_entries = new List<Entry>();
        private static Harmony s_harmony;

        /// <summary>Distinct feature names with every patch class applied, after <see cref="ApplyAll"/> (for the startup line).</summary>
        internal static int FeaturesPatched { get; private set; }
        /// <summary>Distinct feature names with at least one patch class that failed to apply.</summary>
        internal static int FeaturesFailed { get; private set; }

        // WP1: lazily built the first time anything calls FeatureNameForPatchTypeName (in practice, the first
        // caught patch exception, well after every module's Init() has finished registering — s_entries never
        // mutates again after that point, so no lock is needed once this has been built; `volatile` alone gives
        // safe publication if two callers somehow race to build it for the first time (redundant, not wrong).
        private static volatile Dictionary<string, string> s_featureByPatchTypeName;

        /// <summary>
        /// Maps a Harmony patch class's own short <see cref="Type.Name"/> (never <c>FullName</c> — matches how
        /// every existing <c>NoodlesLog.PatchException(nameof(SomePatchClass), e)</c> call site already names
        /// itself) back to the feature name it was <see cref="Register"/>ed under. Used by
        /// <see cref="FeatureStandDown"/> to attribute a caught exception to one feature. Returns null for a
        /// name that isn't a registered patch class at all — most commonly a non-Harmony call site logging
        /// through the same <c>NoodlesLog.PatchException</c> method with its own descriptive string (e.g.
        /// <c>"ReclaimGate.IsActive:" + owner</c>) — which is a deliberate no-op for the per-feature trigger; the
        /// GLOBAL exception-burst trigger (<see cref="KillSwitch.AutoStandDown"/>) still sees every one of these
        /// regardless.
        /// </summary>
        internal static string FeatureNameForPatchTypeName(string typeName)
        {
            var map = s_featureByPatchTypeName;
            if (map == null)
            {
                map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var entry in s_entries)
                {
                    map[entry.PatchClass.Name] = entry.FeatureName;
                }
                s_featureByPatchTypeName = map;
            }
            return map.TryGetValue(typeName, out var featureName) ? featureName : null;
        }

        /// <summary>
        /// Register one Harmony patch class (a class carrying one or more <c>[HarmonyPatch]</c>-annotated
        /// patch methods) under a feature name. <paramref name="featureName"/> should match a name already
        /// passed to <see cref="Features.Register"/> so a patch failure can force that specific feature off
        /// (if no such flag is found, the failure is still logged and the class is still skipped/unpatched —
        /// it just can't be reflected in "noodles status").
        /// </summary>
        public static void Register(string featureName, Type patchClass, PatchSide side = PatchSide.Both)
        {
            s_entries.Add(new Entry { FeatureName = featureName, PatchClass = patchClass, Side = side });
        }

        /// <summary>
        /// Applies every registered patch class individually — one <c>harmony.CreateClassProcessor(type).Patch()</c>
        /// call per class, each wrapped in its own try/catch. Deliberately NEVER <c>harmony.PatchAll()</c>
        /// anywhere in this mod: <c>PatchAll</c> resolves and applies every <c>[HarmonyPatch]</c> class in the
        /// target scope in one pass with no per-class isolation — one class with a missing, renamed, or
        /// ambiguous target throws and takes every other feature in the same call down with it, in whichever
        /// process hit it. Per-class isolation is what "fail open to vanilla, one feature at a time" (this
        /// mod's own absolute, zero-data-loss-adjacent rule) actually requires in code, not just in spec.
        /// </summary>
        public static void ApplyAll(Harmony harmony)
        {
            s_harmony = harmony;
            bool isServer = Side.IsDedicatedServerProcess;
            int applied = 0, skippedWrongSide = 0, failed = 0;
            var appliedEntries = new List<Entry>();
            var failedFeatures = new HashSet<string>();

            foreach (var entry in s_entries)
            {
                if (entry.Side == PatchSide.ServerOnly && !isServer) { skippedWrongSide++; continue; }
                if (entry.Side == PatchSide.ClientOnly && isServer) { skippedWrongSide++; continue; }

                bool ok;
                try
                {
                    ok = ApplyOne(entry);
                }
                catch (Exception e)
                {
                    // ApplyOne handles the failures it expects; this backstop keeps an unexpected one from ending
                    // the loop and leaving every later feature unpatched. That happened on the Proxima mirror
                    // (docs/impl/MIRROR-TESTBED.md): a null in Patch()'s result threw out of the old JIT check.
                    NoodlesLog.Error($"feature '{entry.FeatureName}' ({entry.PatchClass.FullName}): unexpected error " +
                        $"while patching — THIS feature stays disabled; every other feature is unaffected: {e}");
                    UnpatchClass(entry);
                    Features.Find(entry.FeatureName)?.ForceOff($"patching error: {e.GetType().Name}");
                    ok = false;
                }
                if (ok) { applied++; appliedEntries.Add(entry); }
                else { failed++; failedFeatures.Add(entry.FeatureName); }
            }

            // A feature is all-or-nothing. When one of its classes failed, the feature is already forced off, but
            // sibling classes that did patch would stay installed and rely on each re-checking the flag (runtime
            // review 2026-09-28: some siblings do work before any flag check). Remove them too.
            foreach (var entry in appliedEntries)
            {
                if (!failedFeatures.Contains(entry.FeatureName)) continue;
                UnpatchClass(entry);
                applied--;
                failed++;
                NoodlesLog.Warning($"feature '{entry.FeatureName}': also unpatched {entry.PatchClass.FullName}, because " +
                    "another class of the same feature failed to patch.");
            }

            var patchedFeatures = new HashSet<string>();
            foreach (var entry in appliedEntries)
            {
                if (!failedFeatures.Contains(entry.FeatureName)) patchedFeatures.Add(entry.FeatureName);
            }
            FeaturesPatched = patchedFeatures.Count;
            FeaturesFailed = failedFeatures.Count;

            NoodlesLog.Verbose($"PatchRegistry: {applied} feature(s) patched, {failed} failed to patch, " +
                $"{skippedWrongSide} skipped (wrong side, dedicated-server={isServer}).");
        }

        private static bool ApplyOne(Entry entry)
        {
            try
            {
                // HarmonyX compiles each patched method while installing it (ManagedMethodPatcher.DetourTo ->
                // MonoMod ILHook.Apply -> new Detour(...), which pins the generated method and takes its native
                // entry point), so a transpiler that emits invalid IL throws here, at load, never mid-game.
                s_harmony.CreateClassProcessor(entry.PatchClass).Patch();
            }
            catch (Exception e)
            {
                NoodlesLog.Error($"feature '{entry.FeatureName}' ({entry.PatchClass.FullName}) failed to patch " +
                    $"(missing/renamed/ambiguous Harmony target, invalid transpiler IL, or another patching error) — " +
                    $"THIS feature stays disabled; every other feature is unaffected: {e}");
                UnpatchClass(entry); // a class with several targets may have installed some before the one that failed
                Features.Find(entry.FeatureName)?.ForceOff($"patch failed to apply: {e.GetType().Name}");
                return false;
            }

            // Patch() returns HarmonyX's generated replacement methods, not the originals, and an element is null
            // when HarmonyX has no replacement to hand back (seen with Proxima's mod stack). Count what this class
            // really installed from Harmony's own patch table instead.
            int targets = OriginalsPatchedBy(entry.PatchClass).Count;
            if (targets == 0)
            {
                NoodlesLog.Warning($"feature '{entry.FeatureName}' ({entry.PatchClass.FullName}) resolved zero " +
                    "Harmony targets — treating this as a failed patch, disabling the feature.");
                Features.Find(entry.FeatureName)?.ForceOff("patch resolved zero targets");
                return false;
            }

            NoodlesLog.Verbose($"feature '{entry.FeatureName}' patched ({entry.PatchClass.FullName}, " +
                $"{targets} method(s), side={entry.Side}).");
            return true;
        }

        /// <summary>Every original method that carries a patch method declared in <paramref name="patchClass"/> under this mod's Harmony id.</summary>
        private static List<MethodBase> OriginalsPatchedBy(Type patchClass)
        {
            var result = new List<MethodBase>();
            foreach (MethodBase original in s_harmony.GetPatchedMethods())
            {
                if (original != null && PatchMethodsOf(original, patchClass).Count > 0) result.Add(original);
            }
            return result;
        }

        private static List<MethodInfo> PatchMethodsOf(MethodBase original, Type patchClass)
        {
            var result = new List<MethodInfo>();
            Patches info = Harmony.GetPatchInfo(original);
            if (info == null) return result;
            foreach (var patches in new[] { info.Prefixes, info.Postfixes, info.Transpilers, info.Finalizers, info.ILManipulators })
            {
                if (patches == null) continue;
                foreach (Patch p in patches)
                {
                    if (p != null && p.owner == s_harmony.Id && p.PatchMethod != null && p.PatchMethod.DeclaringType == patchClass)
                        result.Add(p.PatchMethod);
                }
            }
            return result;
        }

        /// <summary>
        /// Removes only this class's own patch methods. <c>Unpatch(original, All, id)</c> would also remove every
        /// other feature's patch on the same original (several features patch ZNet.Awake, for example).
        /// </summary>
        private static void UnpatchClass(Entry entry)
        {
            try
            {
                foreach (MethodBase original in OriginalsPatchedBy(entry.PatchClass))
                {
                    foreach (MethodInfo patch in PatchMethodsOf(original, entry.PatchClass))
                    {
                        try
                        {
                            s_harmony.Unpatch(original, patch);
                        }
                        catch (Exception e)
                        {
                            NoodlesLog.Error($"feature '{entry.FeatureName}': failed to cleanly unpatch " +
                                $"'{original.DeclaringType?.FullName}.{original.Name}' after a patch failure — " +
                                $"restarting the process is the only guaranteed-safe recovery if this itself ever fails: {e}");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                NoodlesLog.Error($"feature '{entry.FeatureName}': could not list its patches to undo them: {e}");
            }
        }
    }
}
