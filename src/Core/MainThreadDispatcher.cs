using System;
using System.Collections.Concurrent;
using System.Threading;

namespace NoodlesMcDoodles.Core
{
    /// <summary>
    /// Marshals work from an arbitrary thread onto the Unity main thread, and lets main-thread-only code
    /// assert it's actually there before doing something unsafe off it.
    ///
    /// Why this exists (doc 02, Hook 9's Data-loss analysis, "Forced save races a background config-reload
    /// thread"): <c>ZNet.Save</c>'s <c>PrepareSave()</c> calls run synchronously on whatever thread calls
    /// <c>Save()</c>, before any background save thread exists, and race unsynchronized against the real main
    /// thread's concurrent <c>ZDOMan.Update</c>/RPC writes if that caller isn't the main thread. BepInEx does
    /// NOT guarantee <c>ConfigFile.SettingChanged</c> or a <see cref="System.IO.FileSystemWatcher"/> callback
    /// runs on the main thread (both are used by this mod — live config edits and <see cref="ConfigWatcher"/>'s
    /// headless-operator file watch) — so anything reachable from either that could end up calling
    /// <c>ZNet.instance.Save</c>/<c>ZDOMan</c>/<c>ZoneSystem</c> APIs (i.e. <see cref="SafeToggle"/>) must route
    /// through here first.
    /// </summary>
    internal static class MainThreadDispatcher
    {
        private static int s_mainThreadId = -1;
        private static readonly ConcurrentQueue<Action> s_queue = new ConcurrentQueue<Action>();

        /// <summary>Call once, from the plugin's own Awake() — BepInEx guarantees that runs on the main thread.</summary>
        public static void CaptureMainThread()
        {
            s_mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == s_mainThreadId;

        /// <summary>Throws if called off the main thread. Defensive — a caught bug, not a fallback path.</summary>
        public static void AssertMainThread()
        {
            if (!IsMainThread)
            {
                throw new InvalidOperationException(
                    "NoodlesMcDoodles: this call requires the Unity main thread (current thread id="
                    + Thread.CurrentThread.ManagedThreadId + ", main thread id=" + s_mainThreadId + ")");
            }
        }

        /// <summary>
        /// Queue an action to run on the next <see cref="NoodlesHeartbeat"/> tick. Safe to call from any
        /// thread. The action itself is still responsible for its own try/catch (drained actions that throw
        /// are logged and skipped, never allowed to kill the heartbeat's Update loop).
        /// </summary>
        public static void Enqueue(Action action) => s_queue.Enqueue(action);

        /// <summary>Drains every currently-queued action. Called only from <see cref="NoodlesHeartbeat"/>.Update().</summary>
        internal static void DrainOnMainThread()
        {
            // Bound the drain to what was queued at entry so a re-entrant Enqueue from inside a drained
            // action can't starve the frame — it just runs next tick instead.
            int budget = s_queue.Count;
            for (int i = 0; i < budget && s_queue.TryDequeue(out var action); i++)
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException("MainThreadDispatcher queued action", e);
                }
            }
        }
    }
}
