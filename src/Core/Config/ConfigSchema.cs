namespace NoodlesMcDoodles.Core.ConfigFiles
{
    /// <summary>
    /// File names, the current schema version and the main file's section names — shared by the bind sites
    /// (which need BepInEx) and <see cref="ConfigMigrator"/> (which must not), so the two can never disagree.
    /// BepInEx writes sections in alphabetical order, hence the numeric prefixes.
    /// </summary>
    internal static class ConfigSchema
    {
        /// <summary>Bump together with a new <see cref="IConfigMigration"/> in <see cref="ConfigMigrator"/>'s step list.</summary>
        public const int CurrentVersion = 2;

        public const string MainFileName = "wubarrk.noodlesmcdoodles.cfg";
        public const string AdvancedFileName = "wubarrk.noodlesmcdoodles.advanced.cfg";

        // --- Main file sections -------------------------------------------------------------------------
        public const string General = "1 - General";
        public const string Network = "2 - Network";
        public const string ServerWorld = "3 - Server World";
        public const string Client = "4 - Client";
        public const string Safety = "5 - Safety";

        /// <summary>[1 - General] key the migrator reads to detect the schema version. Absent = schema v1.</summary>
        public const string SchemaVersionKey = "ConfigSchemaVersion";

        // --- Unit conversions for the two promoted rate keys --------------------------------------------
        public const int BytesPerKiB = 1024;
        public const int BytesPerMiB = 1048576;
    }
}
