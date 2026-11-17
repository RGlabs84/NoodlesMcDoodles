using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using BepInEx.Configuration;

namespace NoodlesMcDoodles.Core
{
    /// <summary>
    /// Watches the plugin's two .cfg files (<see cref="NoodlesConfig.Main"/> and the advanced
    /// <see cref="NoodlesConfig.File"/>) for edits made outside the game (an operator with shell access
    /// only, no player connected — doc 02 Open Question 3's accepted gap: "editing the config file directly
    /// ... does apply live via SettingChanged", but BepInEx itself never reloads a config file on its own; it
    /// only writes it. This is the missing other half: notice a file changed, and call
    /// <see cref="ConfigFile.Reload"/> on THAT file ourselves.
    ///
    /// <see cref="FileSystemWatcher"/> callbacks run on a ThreadPool thread, and a single external write
    /// commonly fires several events in quick succession (many editors/`sed -i`/`cp` do a write-then-rename or
    /// multiple partial writes) — both reasons this debounces (a real System.Threading.Timer per file, reset on
    /// every event, firing once after <see cref="DebounceMs"/> of quiet) and marshals the actual
    /// <c>Reload()</c> onto the main thread via <see cref="MainThreadDispatcher"/> rather than calling
    /// it straight from the watcher thread — <c>ConfigEntry.SettingChanged</c> handlers (this mod's own
    /// <see cref="KillSwitch"/> included) are not written to be thread-safe against an arbitrary watcher
    /// thread, and <see cref="MainThreadDispatcher"/>'s own contract exists specifically so nothing reachable
    /// from here ever calls a <c>ZNet</c>/<c>ZDOMan</c>/<c>ZoneSystem</c> API off-thread.
    /// </summary>
    internal static class ConfigWatcher
    {
        private const int DebounceMs = 750;

        /// <summary>One watched file: its own watcher and debounce timer, reloading only its own ConfigFile.</summary>
        private sealed class Watch
        {
            public ConfigFile Config;
            public FileSystemWatcher Watcher;
            public System.Threading.Timer DebounceTimer;
        }

        private static readonly List<Watch> s_watches = new List<Watch>();

        public static void Start()
        {
            var watched = new List<string>();
            foreach (ConfigFile config in new[] { NoodlesConfig.Main, NoodlesConfig.File })
            {
                if (config != null && StartOne(config)) watched.Add(Path.GetFileName(config.ConfigFilePath));
            }

            if (watched.Count > 0)
            {
                NoodlesLog.Verbose($"ConfigWatcher: watching {string.Join(" and ", watched.ToArray())} for external edits " +
                    $"(debounce {DebounceMs}ms).");
            }
        }

        private static bool StartOne(ConfigFile config)
        {
            string path = config.ConfigFilePath;
            try
            {
                string dir = Path.GetDirectoryName(path);
                string file = Path.GetFileName(path);
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file))
                {
                    NoodlesLog.Warning($"ConfigWatcher: could not split config path '{path}', watcher not started " +
                        "(a headless operator will need to reload the game/mod to apply a raw file edit).");
                    return false;
                }

                var watch = new Watch { Config = config };
                watch.DebounceTimer = new System.Threading.Timer(OnDebounceElapsed, watch, Timeout.Infinite, Timeout.Infinite);
                watch.Watcher = new FileSystemWatcher(dir, file)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                watch.Watcher.Changed += (_, __) => Kick(watch);
                watch.Watcher.Created += (_, __) => Kick(watch);
                watch.Watcher.Renamed += (_, __) => Kick(watch); // some editors write to a temp file then rename over the original
                watch.Watcher.Error += (_, e) => NoodlesLog.Warning($"ConfigWatcher: watcher error ({file}): {e.GetException()}");
                watch.Watcher.EnableRaisingEvents = true;
                s_watches.Add(watch);
                return true;
            }
            catch (Exception e)
            {
                NoodlesLog.Warning($"ConfigWatcher: failed to start for '{path}' ({e.GetType().Name}: {e.Message}) — a " +
                    "headless operator's raw edits to that file will not apply until the game/mod restarts. Non-fatal: " +
                    "the mod otherwise runs normally.");
                return false;
            }
        }

        private static void Kick(Watch watch)
        {
            try
            {
                watch.DebounceTimer?.Change(DebounceMs, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // Shutting down; nothing to do.
            }
        }

        private static void OnDebounceElapsed(object state)
        {
            var watch = (Watch)state;
            // Runs on a ThreadPool thread (System.Threading.Timer callback) — marshal before touching Config.
            MainThreadDispatcher.Enqueue(() =>
            {
                try
                {
                    NoodlesLog.Info($"ConfigWatcher: external change to {Path.GetFileName(watch.Config.ConfigFilePath)} detected, reloading.");
                    watch.Config.Reload();
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException("ConfigWatcher.Reload", e);
                }
            });
        }

        public static void Stop()
        {
            try
            {
                foreach (Watch watch in s_watches)
                {
                    watch.Watcher?.Dispose();
                    watch.Watcher = null;
                    watch.DebounceTimer?.Dispose();
                    watch.DebounceTimer = null;
                }
                s_watches.Clear();
            }
            catch (Exception e)
            {
                NoodlesLog.Warning($"ConfigWatcher: error during shutdown (non-fatal): {e}");
            }
        }
    }
}
