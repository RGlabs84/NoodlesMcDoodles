namespace NoodlesMcDoodles.Core
{
    /// <summary>
    /// Plugin identity constants. Kept as a tiny, dependency-free file so every other file
    /// (including ones that must exist before Harmony/BepInEx types are touched, like <see cref="Side"/>)
    /// can reference the GUID/version without pulling in the rest of Core.
    /// </summary>
    internal static class PluginInfo
    {
        public const string PLUGIN_GUID = "wubarrk.noodlesmcdoodles";
        public const string PLUGIN_NAME = "NoodlesMcDoodles";
        public const string PLUGIN_VERSION = "1.1.0";
    }
}
