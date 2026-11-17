using System;
using System.Collections.Concurrent;
using BepInEx.Configuration;

namespace NoodlesMcDoodles.Core
{
    /// <summary>
    /// Per-feature exception-burst quarantine (WP1) — the "quarantine" half of the user's own binding "robust
    /// error correction and failbacks" instruction, ADDITIVE to (never a replacement for)
    /// <see cref="KillSwitch.AutoStandDown"/>'s existing GLOBAL exception-burst trigger. Mirrors that class's
    /// own shape closely — a rolling wall-clock window of timestamps per key, safe from any thread with no
    /// shared lock across features — but keyed PER FEATURE instead of one shared counter for the whole mod, and
    /// with a tighter default (3 exceptions / 30s vs. the global 5/60s): this only has to watch one feature's
    /// own patches, not every patch in the mod at once, so a much smaller burst from just that one feature is
    /// already a meaningful signal.
    ///
    /// Fed exclusively from <see cref="NoodlesLog.PatchException"/>, right after that method's existing global
    /// <see cref="KillSwitch.AutoStandDown.RecordPatchException"/> call — the global trigger still sees every
    /// exception from every feature, exactly as before. A caught exception whose <c>patchTypeName</c> doesn't
    /// resolve to a known registered patch class (a non-Harmony call site logging through the same method, e.g.
    /// <c>"ReclaimGate.IsActive:" + owner</c>) is a silent no-op here — the global trigger still covers it; this
    /// one only adds a second, narrower trigger for patch classes <see cref="PatchRegistry"/> actually knows
    /// the owning feature of.
    /// </summary>
    internal static class FeatureStandDown
    {
        private static ConfigEntry<int> s_exceptionBurstLimit;
        private static ConfigEntry<float> s_windowSeconds;

        // Wall-clock (not UnityEngine.Time), same rationale as KillSwitch.AutoStandDown: safe to call from any
        // thread with no main-thread dependency of its own.
        private static readonly ConcurrentDictionary<string, ConcurrentQueue<DateTime>> s_exceptionTimestamps =
            new ConcurrentDictionary<string, ConcurrentQueue<DateTime>>();

        /// <summary>
        /// Called from <see cref="NoodlesLog.PatchException"/> for every caught patch exception, in ADDITION to
        /// (never instead of) the global AutoStandDown trigger. <paramref name="patchTypeName"/> is whatever
        /// string the throwing call site passed to <c>PatchException</c> — in practice almost always
        /// <c>nameof(SomePatchClass)</c>, matching <see cref="PatchRegistry.FeatureNameForPatchTypeName"/>'s own
        /// key convention exactly.
        /// </summary>
        internal static void RecordException(string patchTypeName)
        {
            try
            {
                string featureName = PatchRegistry.FeatureNameForPatchTypeName(patchTypeName);
                if (featureName == null) return; // not a registered patch class -- the global trigger still covers it.

                var queue = s_exceptionTimestamps.GetOrAdd(featureName, _ => new ConcurrentQueue<DateTime>());
                var now = DateTime.UtcNow;
                queue.Enqueue(now);

                float windowSeconds = s_windowSeconds?.Value ?? 30f;
                while (queue.TryPeek(out var oldest) && (now - oldest).TotalSeconds > windowSeconds)
                {
                    queue.TryDequeue(out _);
                }

                int limit = s_exceptionBurstLimit?.Value ?? 3;
                if (queue.Count > limit)
                {
                    Features.Find(featureName)?.ForceOff(
                        $"feature-level exception burst: {queue.Count} in {windowSeconds:0}s, most recently from '{patchTypeName}'");
                }
            }
            catch
            {
                // Never let this quarantine's own bookkeeping be the thing that brings down a hot vanilla call
                // path (same absolute rule NoodlesLog.PatchException itself follows).
            }
        }

        [NoodlesModuleInit]
        internal static void Init()
        {
            s_exceptionBurstLimit = NoodlesConfig.File.Bind("Core.FeatureStandDown", "ExceptionBurstLimit", 3,
                new ConfigDescription("Caught-exception count from ONE feature's own patches inside a rolling " +
                    "WindowSeconds window before that single feature (never [1 - General] Enabled itself) is forced off. " +
                    "Tighter than the global Core.AutoStandDown.ExceptionBurstLimit (default 5/60s) since this " +
                    "only has to watch one feature's own patches, not every patch in the mod at once.",
                    new AcceptableValueRange<int>(1, 20)));
            s_windowSeconds = NoodlesConfig.File.Bind("Core.FeatureStandDown", "WindowSeconds", 30f,
                new ConfigDescription("Rolling window (seconds) ExceptionBurstLimit is measured over, per feature.",
                    new AcceptableValueRange<float>(5f, 300f)));
        }
    }
}
