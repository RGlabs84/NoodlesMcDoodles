using System;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;

namespace NoodlesMcDoodles.Core
{
    /// <summary>
    /// Tier-2 file logger (WP1) — for the "tons of logging but not spammy" binding instruction: high-volume,
    /// low-severity detail (a rate-limited per-key trace of ordinary, expected events — see
    /// <see cref="NoodlesLog.DetailRateLimited"/>) lives HERE, in its own file, so it never competes with the
    /// operator-facing Tier-1 log (<see cref="NoodlesLog"/>, `LogOutput.log`/`valheim_server.log`) for attention
    /// or grep-ability, and never grows the vanilla log files this mod otherwise shares with every other plugin.
    ///
    /// Own subfolder directly under BepInEx's own root — <c>BepInEx.Paths.BepInExRootPath</c>/
    /// <c>NoodlesMcDoodles/detail.log</c> — NOT <c>Paths.PluginPath</c>/<c>Assembly.Location</c>'s own directory,
    /// which points at wherever a packaging step happens to have PUT this plugin's own DLL (e.g.
    /// `BepInEx/plugins/Wubarrk-NoodlesMcDoodles/` on the live testbed profiles) — a packaging detail this file
    /// has no reason to depend on. One well-known, BepInEx-relative path, matching how <see cref="NoodlesConfig"/>
    /// locates the shared `.cfg` in spirit (never a guessed path relative to the current working directory).
    ///
    /// Plain buffered <see cref="StreamWriter"/>, flushed at most once every <see cref="FlushIntervalSeconds"/>,
    /// driven from <see cref="NoodlesHeartbeat"/>.Update() via <see cref="FlushIfDue"/> — CORE-API.md's "no
    /// second periodic driver" rule: this is the one new disk-I/O point WP1 adds, and it rides the SAME existing
    /// tick every other periodic thing in this mod already uses, never a new <see cref="System.Threading.Timer"/>.
    /// Size-capped at 10 MiB; a single-generation rotation to `detail.log.1` (overwriting any prior `.1`) is
    /// checked at that same flush checkpoint, never on every individual <see cref="Write"/> call — keeps the hot
    /// write path a single buffered-stream write, not a <see cref="FileInfo"/> stat every time something is logged.
    ///
    /// Scrub rule, enforced here at the one call site every Tier-2 line passes through (same discipline
    /// <see cref="DeploymentLog"/>'s own header comment states for Tier-1): never write a raw IP address, a Steam
    /// session ticket, or a config secret/server password. A peer may be named only by its already-public
    /// `m_playerName`/`m_uid` — never anything socket-level. This is a review-time discipline every CALLER must
    /// follow (each passes an already-built message), not a runtime regex scrub of arbitrary text, which would
    /// be exactly as unreliable as it sounds.
    /// </summary>
    internal static class DetailLog
    {
        private const long MaxBytes = 10L * 1024 * 1024; // 10 MiB
        private const float FlushIntervalSeconds = 5f;

        private static string s_path;
        private static StreamWriter s_writer;
        private static bool s_dirtySinceFlush;
        private static float s_nextFlush;
        private static bool s_disabled;

        // 1.1.0: the main log's level system (NoodlesLog.Info/Verbose/Warning(level, ...)) routes demoted lines here
        // from any module, possibly before this class's own Init has run (module Init order) and, rarely, off the
        // main thread. One lock around every writer touch; Monitor is re-entrant, so a NoodlesLog call made while
        // it is held (the rotation line) is safe.
        private static readonly object s_lock = new object();

        /// <summary>Where detail.log lives relative to the game folder, for the startup line.</summary>
        internal static string DisplayPath => $"BepInEx/{PluginInfo.PLUGIN_NAME}/detail.log";

        private static string ResolvePath() =>
            s_path ?? (s_path = Path.Combine(Paths.BepInExRootPath, PluginInfo.PLUGIN_NAME, "detail.log"));

        /// <summary>
        /// Write one Tier-2 line (prefixed with a UTC timestamp and the usual mod prefix). Never throws; a
        /// failure to open/write the file disables this call for the rest of the session (logged ONCE, via the
        /// ordinary Tier-1 log, never retried every call — a broken detail.log must never itself become a
        /// second source of log spam on top of the very "not spammy" instruction this whole tier exists for).
        /// </summary>
        internal static void Write(string message)
        {
            if (s_disabled) return;
            lock (s_lock)
            {
                if (s_disabled) return;
                try
                {
                    if (s_writer == null && !TryOpen()) { s_disabled = true; return; }
                    s_writer.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss.fff'Z'} [NoodlesMcDoodles] {message}");
                    s_dirtySinceFlush = true;
                }
                catch (Exception e)
                {
                    s_disabled = true;
                    NoodlesLog.Warning($"DetailLog: write failed, disabling Tier-2 logging for the rest of this " +
                        $"session (non-fatal — Tier-1 logging is unaffected): {e.GetType().Name}: {e.Message}");
                }
            }
        }

        /// <summary>
        /// Called from <see cref="NoodlesHeartbeat"/>.Update() every tick; a cheap no-op on every tick except
        /// the one every <see cref="FlushIntervalSeconds"/> that actually has buffered data to flush, per the
        /// class remarks above.
        /// </summary>
        internal static void FlushIfDue()
        {
            if (s_disabled || s_writer == null) return;
            if (Time.realtimeSinceStartup < s_nextFlush) return;
            s_nextFlush = Time.realtimeSinceStartup + FlushIntervalSeconds;

            lock (s_lock)
            try
            {
                if (s_disabled || s_writer == null) return;
                if (s_dirtySinceFlush)
                {
                    s_writer.Flush();
                    s_dirtySinceFlush = false;
                }
                RotateIfNeeded();
            }
            catch (Exception e)
            {
                // Same rule as Write()/TryOpen() above: a Tier-2-only I/O failure (full disk, a locked/
                // undeletable detail.log.1 blocking rotation, a permissions change) must disable JUST this
                // low-priority logging tier for the rest of the session, logged ONCE via plain Tier-1 Warning —
                // never via PatchException, which would feed Core.AutoStandDown's GLOBAL exception-burst
                // trigger (and FeatureStandDown's) from a periodic heartbeat tick every process runs, and could
                // disable the entire mod (LinkGuard/AdaptiveRate/PeerBudget included) within a handful of
                // 5s-spaced flushes over something that only ever touches detail.log.
                s_disabled = true;
                NoodlesLog.Warning($"DetailLog: flush/rotate failed, disabling Tier-2 logging for the rest of " +
                    $"this session (non-fatal — Tier-1 logging is unaffected): {e.GetType().Name}: {e.Message}");
            }
        }

        private static void RotateIfNeeded()
        {
            var info = new FileInfo(s_path);
            if (!info.Exists || info.Length < MaxBytes) return;

            s_writer.Dispose();
            s_writer = null;

            string rolled = s_path + ".1";
            try { if (File.Exists(rolled)) File.Delete(rolled); }
            catch { /* best-effort -- File.Move below still wins the common case if this delete fails */ }
            File.Move(s_path, rolled);

            if (TryOpen())
            {
                NoodlesLog.Verbose("DetailLog: rotated detail.log -> detail.log.1 (10 MiB cap reached).");
            }
            else
            {
                s_disabled = true;
            }
        }

        private static bool TryOpen()
        {
            try
            {
                string path = ResolvePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                s_writer = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = false };
                return true;
            }
            catch (Exception e)
            {
                NoodlesLog.Warning($"DetailLog: could not open '{s_path}' ({e.GetType().Name}: {e.Message}) — " +
                    "Tier-2 logging disabled for this session; Tier-1 logging is unaffected.");
                s_writer = null;
                return false;
            }
        }

        [NoodlesModuleInit]
        internal static void Init()
        {
            try
            {
                lock (s_lock)
                {
                    if (s_disabled) return;
                    // An earlier module's demoted log line may already have opened the file (see ResolvePath).
                    if (s_writer == null && !TryOpen()) { s_disabled = true; return; }

                    Write($"DetailLog started (Tier-2 detail log; scrubbed of IPs/session tickets/passwords; " +
                        $"10 MiB cap, one .1 rotation) — {PluginInfo.PLUGIN_NAME} {PluginInfo.PLUGIN_VERSION}.");
                    // Guarantee the file is non-empty on disk immediately, not just after this session's first
                    // heartbeat tick -- a process that dies moments after boot should still leave a real file behind.
                    if (s_writer == null) return; // Write() itself failed and disabled this tier
                    s_writer.Flush();
                    s_dirtySinceFlush = false;
                    s_nextFlush = Time.realtimeSinceStartup + FlushIntervalSeconds;
                }
            }
            catch (Exception e)
            {
                NoodlesLog.Warning($"DetailLog: Init failed ({e.GetType().Name}: {e.Message}) — Tier-2 logging " +
                    "disabled for this session; Tier-1 logging is unaffected.");
                s_disabled = true;
            }
        }
    }
}
