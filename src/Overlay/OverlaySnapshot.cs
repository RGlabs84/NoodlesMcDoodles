namespace NoodlesMcDoodles.Overlay
{
    /// <summary>
    /// Everything <see cref="OverlayGui"/> draws, already formatted into display-ready strings by
    /// <see cref="OverlaySampler"/> at most once a second. OnGUI (via <see cref="OverlayGui"/>) only ever reads
    /// these fields — no formatting, concatenation or LINQ happens on the draw path, matching
    /// probe/NoodlesProbe.cs's own "Sample() builds the string once; OnGUI only displays it" split.
    ///
    /// Every field defaults to "--" (or the equivalent neutral value), so a panel opened before the client has
    /// even connected — or right after a connection drops — shows an honest "not available yet" instead of a
    /// stale or fabricated number (OVERLAY SPEC point 6's "Use '--' for values not available yet").
    ///
    /// Basic tab = today's ownership/connection numbers plus the two Server/Mode summary lines. Detailed tab
    /// reuses several of the SAME fields (Upload/Download/Ping/SendQueue/UploadLimit/ServerLine/ModeLine/...)
    /// under more specific row labels — see OverlayGui's DrawBasicTab/DrawDetailedTab — rather than duplicating
    /// state that means exactly the same thing on both tabs.
    /// </summary>
    internal sealed class OverlaySnapshot
    {
        // ---- Basic tab: "Who runs the world around you" -------------------------------------------------------
        public string ServerOwnValue = "--";
        public string YouOwnValue = "--";
        public string OtherOwnValue = "--";
        public string UnownedValue = "--";

        // 0..1 shares for the proportion bar. Left at 0 (an empty/neutral bar) whenever there is no live
        // sample — never a stale split from a previous connection.
        public float ServerFrac;
        public float YouFrac;
        public float OtherFrac;

        // ---- Basic tab: "Connection" ----------------------------------------------------------------------------
        // Also reused, under more specific row labels, by the Detailed tab's "Upload (you -> server)"/"Send
        // queue"/"Download (server -> you)" sections.
        public string PingValue = "--";
        public string UploadValue = "--";       // Detailed tab: "Upload, actual"
        public string DownloadValue = "--";     // Detailed tab: "Actual, measured here" (client-side GetConnectionQuality inBps)
        public string SendQueueValue = "--";    // Detailed tab: "Queue, now"
        public string UploadLimitValue = "--";  // Detailed tab: "Upload limit, now"
        public string DownloadLimitValue = "--"; // Basic tab only: "Download limit" (short form of DownloadLimitNowValue below)

        // ---- Basic tab bottom / Detailed tab "Server" section ---------------------------------------------------
        public string ServerLine = "--";
        public string ModeLine = "--";
        public string ModeExplain = "Not connected yet.";

        // ---- Detailed tab: "Link" --------------------------------------------------------------------------------
        public string UploadLimitSetValue = "--";   // this client's configured ceiling (T1_GlobalRateBps)
        public string ConnQualityLocalValue = "--"; // GetConnectionQuality's localQuality, as a percentage
        public string ConnQualityRemoteValue = "--";// GetConnectionQuality's remoteQuality, as a percentage

        // ---- Detailed tab: "Send queue" --------------------------------------------------------------------------
        public string SendQueuePeakValue = "--";     // max send-queue size over the last ~60 samples
        public string QueueAllowanceSetValue = "--"; // configured ceiling governing this client's own upload queue
        public string QueueAllowanceNowValue = "--"; // live budget the transport code is currently using

        // ---- Detailed tab: "Download (server -> you)" -----------------------------------------------------------
        // Sourced from Core.ServerLinkStats -- the SERVER's own view of its link to this client (a 1.0.3+ server
        // pushes these every ~2s; see that class's remarks). ServerLinkAvailable mirrors ServerLinkStats.Available
        // as of this sample: false before the first push, 10s after the last one, or on a vanilla/older server --
        // OverlayGui shows one explanatory line instead of these rows when it's false, so the row values below are
        // only ever drawn when it's true (but still default to "--" here, like every other field in this class).
        public bool ServerLinkAvailable;
        public string DownloadLimitSetValue = "--";      // "Limit, set by server" (RateCeilingBps)
        public string DownloadLimitNowValue = "--";      // "Limit, now" (RateNowBps, or "vanilla (150 KiB/s)" when 0)
        public string DownloadActualValue = "--";        // "Actual" (SentBps -- what the server actually sent us)
        public string ServerQueueForYouValue = "--";     // "Server queue for you" (QueueBytes)
        public string ServerQueueAllowanceValue = "--";  // "Allowance" (QueueAllowanceBytes, or "--" when not known yet)
        public string ServerPingToYouValue = "--";       // "Server's ping to you" (PingMs, the SERVER's own measurement)
        public bool ServerLinkStalled;                   // true = show the "held at vanilla by the stall guard" note row

        // ---- Detailed tab: "Zones around you" ----------------------------------------------------------------
        public string ZoneHereValue = "--"; // e.g. "(12, -5)" -- ZoneSystem.GetZone of the local player's position

        /// <summary>Row-major NW,N,NE,W,Here,E,SW,S,SE — fixed-size, refilled in place every sample, never
        /// reallocated. "--" means no _ZoneCtrl ZDO is loaded for that zone on this client.</summary>
        public readonly string[] ZoneGrid = { "--", "--", "--", "--", "--", "--", "--", "--", "--" };

        // Current zone's object mix (server/you/other-players %), restricted to objects whose zone == ZoneHereValue.
        public string ZoneMixServerValue = "--";
        public string ZoneMixYouValue = "--";
        public string ZoneMixOtherValue = "--";

        /// <summary>Up to 4 "name  N objects" lines for the current zone, largest first; "" = no entry for that
        /// row (OverlayGui skips empty rows). Never a raw uid.</summary>
        public readonly string[] ZoneTopPlayers = { "", "", "", "" };

        // ---- Detailed tab: "This client" -------------------------------------------------------------------------
        public string Q10Value = "--";
        public string Q13Value = "--";
        public string ExtrapolationValue = "--";
        public string CompressionValue = "--";
        public string KillSwitchValue = "--";
        // Mod version never changes at runtime -- read directly from PluginInfo at draw time, no snapshot field.
    }
}
