using System;
using System.Collections.Concurrent;

namespace NoodlesMcDoodles.Core
{
    /// <summary>
    /// One log entry point for the whole mod, every module. Wraps <c>ZLog</c> (the game's own logger,
    /// <c>UnityEngine.Debug.Log</c> under the hood — confirmed `assembly_utils` `ZLog` class, plain
    /// `Debug.Log/LogWarning/LogError` calls) rather than BepInEx's own `ManualLogSource`, specifically so
    /// every line lands in BOTH `BepInEx/LogOutput.log` (BepInEx's Unity-log listener) AND the vanilla
    /// `valheim_server.log`/Player.log (Unity's own log pipeline) — the exact two files doc 02's own Telemetry
    /// section requires the `[NoodlesMcDoodles] telemetry` line be grep-able from.
    ///
    /// Every prefix/postfix/transpiler body in this mod is required (doc 02 Hook 5's "every patch wraps its
    /// body in try/catch" invariant, restated at Hook 1's RED-TEAM FIX) to catch its own exceptions and report
    /// them through <see cref="PatchException"/> — that single call point is what feeds
    /// <c>Core.AutoStandDown</c>'s exception-burst trigger (trigger #3, `Core.AutoStandDown.ExceptionBurstLimit`
    /// per rolling 60s) AND, additively (WP1), <see cref="FeatureStandDown"/>'s narrower per-feature trigger,
    /// without every module having to know about either one's internals.
    ///
    /// Log tiers (WP1, "tons of logging but not spammy" — binding user instruction): Tier-1 is the main log,
    /// `LogOutput.log`/`valheim_server.log`, reserved for things an operator should see once, every time they
    /// happen (a state transition, a startup line, an actual failure); since 1.1.0 which calls reach it depends on
    /// the configured <see cref="NoodlesLogLevel"/> (see "Log levels" below). Tier-2 is <see cref="DetailLog"/> (its
    /// own `detail.log` file) for high-volume, expected-in-normal-play detail that would otherwise flood Tier-1.
    /// <see cref="WarningRateLimited(string, string, int, float)"/>/<see cref="DetailRateLimited"/> below are the one seam a caller with
    /// something worth logging on EVERY occurrence should use instead of choosing between silence and flooding
    /// either tier.
    /// </summary>
    internal static class NoodlesLog
    {
        private const string Prefix = "[NoodlesMcDoodles] ";

        // ---- Log levels (1.1.0) -----------------------------------------------------------------------------------
        //
        // Every main-log call names the lowest NoodlesLogLevel ([1 - General] LogLevel) at which it reaches
        // LogOutput.log/valheim_server.log. Below that level the same line goes to DetailLog (detail.log) instead,
        // so lowering the level hides lines from the main log without losing them:
        //   Notice / Warning / Error / WarningRateLimited(key, ...)  -> every level, Quiet included (must-see)
        //   Info / Warning(Normal, ...) / WarningRateLimited(Normal, key, ...) -> Normal and Verbose
        //   Verbose / Warning(Verbose, ...) / WarningRateLimited(Verbose, key, ...) -> Verbose only
        // At Verbose the main log gets exactly what 1.0.x logged. PatchException is always a main-log warning.

        /// <summary>The level currently in force (<see cref="NoodlesLogSettings.Level"/>).</summary>
        public static NoodlesLogLevel Level => NoodlesLogSettings.Level;

        /// <summary>True when a line tagged <paramref name="level"/> goes to the main log right now.</summary>
        public static bool IsAtLeast(NoodlesLogLevel level) => NoodlesLogSettings.Level >= level;

        /// <summary>Main log at every level, Quiet included: the startup line, kill-switch changes, admin actions.</summary>
        public static void Notice(string message) => ZLog.Log(Prefix + message);

        /// <summary>Main log at Normal and Verbose (real state changes worth seeing once); detail.log at Quiet.</summary>
        public static void Info(string message) => InfoAt(NoodlesLogLevel.Normal, message);

        /// <summary>Main log at Verbose only (boot detail, per-feature patch lines, routine events); detail.log otherwise.</summary>
        public static void Verbose(string message) => InfoAt(NoodlesLogLevel.Verbose, message);

        /// <summary>Main log at every level: something an admin should act on, or a real failure.</summary>
        public static void Warning(string message) => ZLog.LogWarning(Prefix + message);

        /// <summary>
        /// Warning-severity line that reaches the main log only at <paramref name="shownFrom"/> or above; below
        /// that it goes to detail.log (marked "WARN"). For warnings that are expected in normal play or that an
        /// admin cannot act on.
        /// </summary>
        public static void Warning(NoodlesLogLevel shownFrom, string message)
        {
            if (NoodlesLogSettings.Level >= shownFrom) ZLog.LogWarning(Prefix + message);
            else DetailLog.Write("WARN " + message);
        }

        /// <summary>Main log at every level.</summary>
        public static void Error(string message) => ZLog.LogError(Prefix + message);

        /// <summary>Straight to detail.log (never the main log), unthrottled. Use <see cref="DetailRateLimited"/> for anything per-object.</summary>
        public static void Detail(string message) => DetailLog.Write(message);

        private static void InfoAt(NoodlesLogLevel shownFrom, string message)
        {
            if (NoodlesLogSettings.Level >= shownFrom) ZLog.Log(Prefix + message);
            else DetailLog.Write(message);
        }

        /// <summary>
        /// Call from the catch block of any patch (prefix/postfix/transpiler-guarded call) that fails
        /// unexpectedly. Logs loudly, counts toward telemetry, and feeds BOTH the global exception-burst
        /// auto-stand-down trigger AND the narrower per-feature one (<see cref="FeatureStandDown"/>) — never
        /// throws itself, regardless of how broken the caller's own state is.
        /// </summary>
        public static void PatchException(string patchName, Exception e)
        {
            try
            {
                Warning($"'{patchName}' threw and was caught (falling back to vanilla for this call): {e}");
                Telemetry.Counters.PatchExceptions.Increment();
                KillSwitch.AutoStandDown.RecordPatchException(patchName);
                FeatureStandDown.RecordException(patchName);
            }
            catch
            {
                // Logging itself must never be the thing that brings down a hot vanilla call path.
            }
        }

        // ---- Tiered / rate-limited logging (WP1) ---------------------------------------------------------------
        //
        // The first `firstN` occurrences of a given `key` within a rolling `windowSeconds` window log in full;
        // every occurrence after that increments a per-key suppressed counter and is absorbed silently; when
        // the window rolls over, ONE summary line reports how many were suppressed (never logged at all if
        // nothing was suppressed — a quiet key produces nothing extra). Per-key state is a small mutable class
        // (Interlocked-shaped like <see cref="Telemetry.Counter"/>, never a boxed tuple), allocated once per
        // distinct key the first time it's ever used (`ConcurrentDictionary.GetOrAdd`) — an ordinary call after
        // that only touches the one already-allocated object, briefly locked (this is never the "nothing is
        // logged" hot path: something IS being logged, or counted, on every call).

        private sealed class RateLimitState
        {
            public DateTime WindowStart;
            public float WindowSeconds;
            public int Shown;
            public int Suppressed;
            /// <summary>Where this key's lines (and its "(+N more ...)" summary) go; set on every call.</summary>
            public Action<string> Sink;
        }

        private static readonly ConcurrentDictionary<string, RateLimitState> s_warningLimits =
            new ConcurrentDictionary<string, RateLimitState>();
        private static readonly ConcurrentDictionary<string, RateLimitState> s_detailLimits =
            new ConcurrentDictionary<string, RateLimitState>();

        // One cached delegate per level, so the hot path never allocates a closure.
        private static readonly Action<string> s_warningAlways = Warning;
        private static readonly Action<string> s_warningNormal = m => Warning(NoodlesLogLevel.Normal, m);
        private static readonly Action<string> s_warningVerbose = m => Warning(NoodlesLogLevel.Verbose, m);
        private static readonly Action<string> s_detailSink = DetailLog.Write;

        /// <summary>Rate-limited warning in the main log at every level -- see the class remarks above.</summary>
        public static void WarningRateLimited(string key, string message, int firstN, float windowSeconds) =>
            RunRateLimited(s_warningLimits, key, message, firstN, windowSeconds, s_warningAlways);

        /// <summary>
        /// Rate-limited warning that reaches the main log only at <paramref name="shownFrom"/> or above (detail.log
        /// below it). The "(+N more ...)" summary line follows the same rule.
        /// </summary>
        public static void WarningRateLimited(NoodlesLogLevel shownFrom, string key, string message, int firstN, float windowSeconds) =>
            RunRateLimited(s_warningLimits, key, message, firstN, windowSeconds,
                shownFrom <= NoodlesLogLevel.Quiet ? s_warningAlways
                : shownFrom == NoodlesLogLevel.Normal ? s_warningNormal
                : s_warningVerbose);

        /// <summary>Rate-limited Tier-2 detail line (<see cref="DetailLog"/>'s own file, NOT the main log) — see the class remarks above.</summary>
        public static void DetailRateLimited(string key, string message, int firstN, float windowSeconds) =>
            RunRateLimited(s_detailLimits, key, message, firstN, windowSeconds, s_detailSink);

        private static void RunRateLimited(ConcurrentDictionary<string, RateLimitState> table, string key,
            string message, int firstN, float windowSeconds, Action<string> sink)
        {
            try
            {
                RateLimitState state = table.GetOrAdd(key,
                    _ => new RateLimitState { WindowStart = DateTime.UtcNow, WindowSeconds = windowSeconds });

                lock (state)
                {
                    state.WindowSeconds = windowSeconds; // callers are expected to pass the same value every time for a given key.
                    state.Sink = sink;
                    CloseWindowIfElapsed(state, key);

                    if (state.Shown < firstN)
                    {
                        state.Shown++;
                        sink(message);
                    }
                    else
                    {
                        state.Suppressed++;
                    }
                }
            }
            catch
            {
                // Same absolute rule as PatchException: logging itself must never bring down a hot vanilla call path.
            }
        }

        /// <summary>Caller must already hold the lock on <paramref name="state"/>.</summary>
        private static void CloseWindowIfElapsed(RateLimitState state, string key)
        {
            DateTime now = DateTime.UtcNow;
            if ((now - state.WindowStart).TotalSeconds < state.WindowSeconds) return;

            if (state.Suppressed > 0)
            {
                state.Sink?.Invoke($"(+{state.Suppressed} more '{key}' suppressed in the last {state.WindowSeconds:0}s)");
            }
            state.WindowStart = now;
            state.Shown = 0;
            state.Suppressed = 0;
        }

        /// <summary>
        /// Called from <see cref="NoodlesHeartbeat"/>.Update() every tick so a window with nothing suppressed
        /// since it elapsed still gets its pending summary line without waiting on another occurrence of that
        /// same key to trigger the check lazily. Cheap: only ever iterates keys some caller has actually used at
        /// least once (both tables start, and stay, empty on a session where neither rate-limited method is ever
        /// called).
        /// </summary>
        internal static void SweepRateLimitWindows()
        {
            SweepTable(s_warningLimits);
            SweepTable(s_detailLimits);
        }

        private static void SweepTable(ConcurrentDictionary<string, RateLimitState> table)
        {
            foreach (var kv in table)
            {
                lock (kv.Value) { CloseWindowIfElapsed(kv.Value, kv.Key); }
            }
        }
    }
}
