using UnityEngine;

namespace NoodlesMcDoodles.Core
{
    /// <summary>
    /// Client side (1.0.3): the server's own view of its link to THIS client. The server's send rate to us is our
    /// download, and only the server can set it (T9, per player), so a client has no download setting of its own;
    /// a 1.0.3+ server pushes these numbers every ~2 s over <c>NoodlesMcDoodles.v1.LinkStats</c> to clients that
    /// advertise <see cref="Capabilities.LinkStats"/>. Written only by that RPC handler (main thread), read by the
    /// overlay. Every field is "as of <see cref="ReceivedAtRealtime"/>"; <see cref="Available"/> is false before
    /// the first push, after 10 s without one, and on a vanilla or older server.
    /// </summary>
    internal static class ServerLinkStats
    {
        /// <summary><c>Time.realtimeSinceStartup</c> of the last push; 0 = none this connection.</summary>
        public static float ReceivedAtRealtime;

        /// <summary>The server's current send rate to us in bytes/s (T9); 0 = not managed by T9, i.e. vanilla's fixed rate.</summary>
        public static int RateNowBps;

        /// <summary>The server's configured per-player ceiling in bytes/s (its T1_GlobalRateBps, or vanilla's if T1 is off).</summary>
        public static int RateCeilingBps;

        /// <summary>What the server actually sent us over the last second, bytes/s.</summary>
        public static int SentBps;

        /// <summary>The server's send queue to us right now, bytes.</summary>
        public static int QueueBytes;

        /// <summary>The server's in-flight allowance for us (its Q3 budget + Q13 allowance), bytes; 0 = not known yet.</summary>
        public static int QueueAllowanceBytes;

        /// <summary>True while the server's stall guard holds us at vanilla's rate and queue.</summary>
        public static bool Stalled;

        /// <summary>The server's own measured ping to us, ms; negative = unknown.</summary>
        public static int PingMs = -1;

        public static bool Available => ReceivedAtRealtime > 0f && Time.realtimeSinceStartup - ReceivedAtRealtime < 10f;

        internal static void Clear()
        {
            ReceivedAtRealtime = 0f;
            RateNowBps = 0;
            RateCeilingBps = 0;
            SentBps = 0;
            QueueBytes = 0;
            QueueAllowanceBytes = 0;
            Stalled = false;
            PingMs = -1;
        }
    }
}
