using System.Diagnostics;

namespace NoodlesMcDoodles.Core
{
    /// <summary>
    /// Dedicated-server frame-time gauges, one set per telemetry period: the longest frame, the mean frame, and how
    /// many frames took over 50 / 100 / 250 ms. A server stall (GC, autosave, another mod's Update) delays every
    /// connected player at once, so this tells a server hitch apart from a network one when read next to a player's
    /// NoodlesProbe CSV (research/11-skynet-harvest.md). Wall time comes from <see cref="Stopwatch"/>, not
    /// <c>Time.realtimeSinceStartup</c>: a float clock is down to ~8 ms resolution after a day of uptime.
    /// Read-only: no patch, no game state.
    /// </summary>
    internal static class FrameTiming
    {
        private static long s_lastTimestamp;
        private static int s_frames;
        private static double s_sumMs;
        private static double s_maxMs;
        private static int s_over50;
        private static int s_over100;
        private static int s_over250;

        /// <summary>
        /// Smoothed frame time (ms, ~20-frame exponential average) on an authoritative process; 0 elsewhere or before
        /// the first frame. Live, unlike the per-period gauges: Q13 (Transport/QueueWindow.cs) withholds its
        /// round-trip allowance while this is above Q13_FrameLoadMs, so a busy server builds smaller packets.
        /// </summary>
        public static double RecentMeanMs;

        /// <summary>Called once per frame from <see cref="NoodlesHeartbeat"/>.Update.</summary>
        public static void Record()
        {
            long now = Stopwatch.GetTimestamp();
            // RUNTIME REVIEW FIX (minor, mixed-and-host, 2026-09-28): ZNet.IsDedicated() is a hardcoded
            // per-assembly constant (true on SERVER, false on every CLIENT build including a listen-server
            // host — Side.cs remarks) — it answers "which build", not "who's authoritative this session", so a
            // listen host's own frame stalls (which delay every connected guest exactly like a dedicated
            // server's would) never populated these gauges. ZNet.instance.IsServer() is the runtime-authority
            // check every other "server_*"-flavoured gauge in this mod already uses.
            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                s_lastTimestamp = 0; // no world yet (or a non-authoritative client): start fresh once authoritative
                RecentMeanMs = 0;
                return;
            }
            if (s_lastTimestamp == 0) { s_lastTimestamp = now; return; }

            double ms = (now - s_lastTimestamp) * 1000.0 / Stopwatch.Frequency;
            s_lastTimestamp = now;
            RecentMeanMs = RecentMeanMs <= 0 ? ms : RecentMeanMs * 0.95 + ms * 0.05;
            s_frames++;
            s_sumMs += ms;
            if (ms > s_maxMs) s_maxMs = ms;
            if (ms > 50) s_over50++;
            if (ms > 100) s_over100++;
            if (ms > 250) s_over250++;
        }

        /// <summary>Called by <see cref="Telemetry.FlushAndLog"/> before it writes the gauges; starts the next period.</summary>
        public static void PublishAndReset()
        {
            if (s_frames == 0) return;
            Telemetry.SetGauge("server_frame_ms_max", s_maxMs);
            Telemetry.SetGauge("server_frame_ms_mean", s_sumMs / s_frames);
            Telemetry.SetGauge("server_frames_over_50ms", s_over50);
            Telemetry.SetGauge("server_frames_over_100ms", s_over100);
            Telemetry.SetGauge("server_frames_over_250ms", s_over250);
            s_frames = 0;
            s_sumMs = 0;
            s_maxMs = 0;
            s_over50 = s_over100 = s_over250 = 0;
        }
    }
}
