namespace NoodlesMcDoodles.Core
{
    /// <summary>
    /// The negotiation capability bitmask (doc 02, "Handshake / negotiation"), sent in Hello/HelloAck. A peer uses a
    /// feature that needs both ends only when both masks carry its bit. Bits are assigned here, in one place, so modules
    /// built in parallel never collide; a module adds its bit to <see cref="Mask"/> by calling <see cref="Advertise"/>
    /// from its [NoodlesModuleInit] (before any connection exists).
    /// </summary>
    internal static class Capabilities
    {
        public const long ConfigSync = 1L << 0;         // doc 02, M1
        public const long OwnershipHandover = 1L << 1;  // O8 hand-over-direction RPC, doc 03, M3
        public const long ZdoCompression = 1L << 2;     // D1 compressed ZDO sync, doc 06, M7
        public const long ClientSmoothing = 1L << 3;    // C1 (informational only: smoothing never needs the server), doc 06, M6
        public const long LinkStats = 1L << 4;          // 1.0.3: server -> client link numbers for the F6 panel (ServerLinkStats)

        private static long s_mask = ConfigSync;

        /// <summary>This side's own mask, sent in Hello.</summary>
        public static long Mask => s_mask;

        public static void Advertise(long bit) => s_mask |= bit;
    }
}
