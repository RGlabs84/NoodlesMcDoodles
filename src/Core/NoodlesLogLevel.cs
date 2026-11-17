namespace NoodlesMcDoodles.Core
{
    /// <summary>How much the mod writes to LogOutput.log / valheim_server.log. Bound as [1 - General] LogLevel.</summary>
    public enum NoodlesLogLevel
    {
        Quiet = 0,
        Normal = 1,
        Verbose = 2,
    }

    internal static class NoodlesLogSettings
    {
        /// <summary>Set by NoodlesConfig at bind time and on every SettingChanged. Read by NoodlesLog.</summary>
        public static volatile NoodlesLogLevel Level = NoodlesLogLevel.Normal;
    }
}
