namespace NoodlesMcDoodles.Core
{
    /// <summary>
    /// G6-N4 (doc 01, Hook 12): a shared library guard, not a vanilla patch — every future NoodlesMcDoodles
    /// write of a mod-added ZDO field routes through here instead of calling <c>ZDO.Set</c> directly, so one
    /// module can never accidentally push an oversized field into a peer's send budget (Q3/Q5's own byte
    /// ceiling, doc 01 Hook 6) by mistake. No M1 module actually adds a custom ZDO field yet — this ships now
    /// as shared infrastructure because it only ever REJECTS a write that would have succeeded, never changes
    /// one, so there's no reason for a later module to be written before this guard exists.
    /// </summary>
    internal static class SafeZdoWrite
    {
        internal static FeatureFlag Feature { get; private set; }

        [NoodlesModuleInit(10)]
        internal static void Init()
        {
            Feature = Features.Register("Transport.G6N4", NoodlesConfig.FeatureTransportG6N4);
        }

        /// <summary>
        /// Attempts a byte-field ZDO write, rejecting it (logged, counted, never truncated) if it exceeds
        /// <c>Transport.G6N4_MaxFieldBytes</c>. Caller must handle a false return itself (e.g. split across
        /// multiple keys/ZDOs) — this never partially writes.
        /// </summary>
        public static bool TrySet(ZDO zdo, string key, byte[] value)
        {
            if (Feature == null || !Feature.Active)
            {
                // Off (or Init() hasn't run — shouldn't happen post-Awake, but fail open rather than throw):
                // callers bypass the size check, exactly like calling ZDO.Set directly.
                zdo.Set(key, value);
                return true;
            }

            int max = NoodlesConfig.TransportG6N4MaxFieldBytes.Value;
            if (value.Length > max)
            {
                NoodlesLog.Warning($"refused oversized ZDO write: key={key} bytes={value.Length} (cap={max})");
                Telemetry.Counters.OversizedZdoWriteRejected.Increment();
                return false;
            }

            zdo.Set(key, value);
            return true;
        }
    }
}
