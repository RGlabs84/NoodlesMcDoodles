using UnityEngine;

namespace NoodlesMcDoodles.Core
{
    /// <summary>
    /// The mod's one <see cref="MonoBehaviour"/>, added once in <see cref="NoodlesPlugin.Awake"/> via
    /// <c>gameObject.AddComponent</c> on BepInEx's own plugin GameObject (which already survives scene loads),
    /// so nothing here needs its own <c>DontDestroyOnLoad</c>. Deliberately the ONLY per-frame driver in Core —
    /// every other periodic thing in this mod (telemetry flush, queued main-thread actions) hangs off this
    /// single <c>Update()</c> rather than each starting its own timer/coroutine, per the shared-infra
    /// contract's "will register their own counters into the same periodic flush rather than starting a
    /// second timer" (doc 02, Interactions with other subsystems).
    /// </summary>
    internal sealed class NoodlesHeartbeat : MonoBehaviour
    {
        private float _nextTelemetryFlush;

        private void Awake()
        {
            _nextTelemetryFlush = Time.realtimeSinceStartup + NoodlesConfig.TelemetryLogIntervalSeconds.Value;
        }

        private void Update()
        {
            MainThreadDispatcher.DrainOnMainThread();

            FrameTiming.Record();

            // SAA WP-D/WP-E failbacks (scratchpad/saa/final.md §3/§8) -- the one call site this task's file
            // scope grants in this file; fans out to FrameBudget/ModeQuarantine internally (see LoadShedder.cs
            // class remarks for why one entry point covers all three files that task owns).
            try { NoodlesMcDoodles.Ownership.LoadShedder.Tick(); }
            catch (System.Exception e) { NoodlesLog.PatchException("NoodlesHeartbeat LoadShedder.Tick", e); }

            // 1.0.10 (claim-pass redesign, coordinator ask: "a fast coroutine that ties in to the main tick"):
            // the O6 claim/release scanner's own per-frame time-sliced step -- see ClaimScanner.PumpFrame's own
            // remarks for why this hangs off the mod's one existing per-frame driver rather than a real Unity
            // coroutine or a second timer. Self-gates on server/Active; cheap no-op otherwise.
            try { NoodlesMcDoodles.Ownership.ClaimScanner.PumpFrame(); }
            catch (System.Exception e) { NoodlesLog.PatchException("NoodlesHeartbeat ClaimScanner.PumpFrame", e); }

            try { PeerState.CheckTimeouts(); }
            catch (System.Exception e) { NoodlesLog.PatchException("NoodlesHeartbeat PeerState.CheckTimeouts", e); }

            try { ConfigSyncRpc.PushDueUpdates(); }
            catch (System.Exception e) { NoodlesLog.PatchException("NoodlesHeartbeat ConfigSyncRpc.PushDueUpdates", e); }

            // WP1: rides this same tick rather than a second timer (CORE-API.md "no second periodic driver").
            try { NoodlesLog.SweepRateLimitWindows(); }
            catch (System.Exception e) { NoodlesLog.PatchException("NoodlesHeartbeat NoodlesLog.SweepRateLimitWindows", e); }

            try { DetailLog.FlushIfDue(); }
            catch (System.Exception e) { NoodlesLog.PatchException("NoodlesHeartbeat DetailLog.FlushIfDue", e); }

            if (Time.realtimeSinceStartup >= _nextTelemetryFlush)
            {
                _nextTelemetryFlush = Time.realtimeSinceStartup + Mathf.Max(1f, NoodlesConfig.TelemetryLogIntervalSeconds.Value);
                try
                {
                    Telemetry.FlushAndLog();
                }
                catch (System.Exception e)
                {
                    NoodlesLog.PatchException("NoodlesHeartbeat telemetry flush", e);
                }
            }
        }

        private void OnDestroy()
        {
            ConfigWatcher.Stop();
        }
    }
}
