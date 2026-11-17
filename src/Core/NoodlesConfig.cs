using BepInEx.Configuration;
using UnityEngine;
using NoodlesMcDoodles.Core.ConfigFiles;

namespace NoodlesMcDoodles.Core
{
    /// <summary>
    /// The mod's two BepInEx config files (schema v2, see <see cref="ConfigSchema"/>), and Core's own keys:
    /// <list type="bullet">
    /// <item><see cref="Main"/> = the plugin's own <c>Config</c>, <c>wubarrk.noodlesmcdoodles.cfg</c>: only the ~20
    /// settings an admin is expected to touch, in numbered plain-English sections (<c>[1 - General]</c> ..
    /// <c>[5 - Safety]</c>).</item>
    /// <item><see cref="File"/> = <b>the ADVANCED file</b>, <c>wubarrk.noodlesmcdoodles.advanced.cfg</c>: every other
    /// key, under its unchanged v1 section and key name. About 30 module files bind through
    /// <c>NoodlesConfig.File</c> (<c>ConfigFile cfg = NoodlesConfig.File;</c>), so pointing <c>File</c> at the
    /// advanced file moved all of them without touching their bind sites. A NEW setting therefore lands in the
    /// advanced file by default; bind it through <see cref="Main"/> (and add a schema migration step that moves
    /// the old key, see <see cref="ConfigMigrator"/>) only if ordinary admins need it.</item>
    /// </list>
    /// An older single-file config is upgraded by <see cref="ConfigMigrator"/> in <c>NoodlesPlugin.Awake</c> before
    /// <see cref="Bind"/> runs. <see cref="ConfigWatcher"/> watches both files. See `docs/impl/CORE-API.md` for how
    /// another module binds its own keys.
    ///
    /// `Transport.G6N4_MaxFieldBytes` is the one exception to "each module owns its own section": it belongs
    /// to the Transport section by DESIGN.md's own naming (`docs/design/01` Hook 12 / Configuration table) even
    /// though the class it configures (<see cref="SafeZdoWrite"/>) lives in Core, because Core ships G6-N4 as
    /// shared library infrastructure every module writes ZDO fields through — see Hook 12's own text: "or
    /// wherever the shared infra ships it".
    /// </summary>
    internal static class NoodlesConfig
    {
        /// <summary>The main file (the plugin's own <c>Config</c>). Only the v2 main-file keys are bound here.</summary>
        public static ConfigFile Main { get; private set; }

        /// <summary>The ADVANCED file (<see cref="ConfigSchema.AdvancedFileName"/>), see the class remarks.</summary>
        public static ConfigFile File { get; private set; }

        // --- Main file: [2 - Network] / [3 - Server World] / [4 - Client] (bound centrally; see Bind) ---
        public static ConfigEntry<int> MaxSendRatePerPlayerKiB;
        public static ConfigEntry<int> ServerUploadLimitMiB;
        public static ConfigEntry<bool> AdaptiveRate;
        public static ConfigEntry<bool> ClientUploadBoost;
        public static ConfigEntry<bool> CompressWorldSync;
        public static ConfigEntry<bool> ServerRunsWorld;
        public static ConfigEntry<NoodlesMcDoodles.Ownership.NearFieldClaimPolicy.OwnershipAuthorityMode> ServerWorldMode;
        public static ConfigEntry<bool> BestPingOwnsSharedObjects;
        public static ConfigEntry<bool> ServerHoldsForFarPlayers;
        public static ConfigEntry<bool> PreserveEmptyAreas;
        public static ConfigEntry<bool> DoorAutoClose;
        public static ConfigEntry<bool> ShortExtrapolation;
        public static ConfigEntry<bool> BufferedSmoothing;
        public static ConfigEntry<KeyboardShortcut> PanelKey;
        public static ConfigEntry<bool> PanelShowOnStart;
        public static ConfigEntry<float> PanelOpacity;

        // --- [1 - General] / [5 - Safety] (main file) -------------------------------------------------------
        public static ConfigEntry<int> ConfigSchemaVersion;
        public static ConfigEntry<bool> CoreEnabled;
        public static ConfigEntry<NoodlesLogLevel> LogLevel;
        public static ConfigEntry<bool> AutoStandDownEnabled;

        // --- Core.AutoStandDown.* (advanced file) -------------------------------------------------------
        public static ConfigEntry<bool> AutoStandDownZdoDeltaTrigger;
        public static ConfigEntry<int> AutoStandDownZdoMismatchTolerance;
        public static ConfigEntry<int> AutoStandDownExceptionBurstLimit;

        // --- Features.* (Core's own five; other modules register their own via Features.Register) -----
        public static ConfigEntry<bool> FeatureHandshakeReentryGuard;
        public static ConfigEntry<bool> FeatureNegotiation;
        public static ConfigEntry<bool> FeatureConfigSync;
        public static ConfigEntry<bool> FeatureSaveIntervalClamp;
        public static ConfigEntry<bool> FeatureBackupPolicy;

        // --- Handshake.* / ConfigSync.* ----------------------------------------------------------------
        public static ConfigEntry<float> HandshakeTimeoutSeconds;
        public static ConfigEntry<float> ConfigSyncPushIntervalSeconds;

        // --- Save.* (server-authoritative, synced to negotiated peers once ConfigSync carries real values) --
        public static ConfigEntry<float> SaveAutosaveIntervalSeconds;
        public static ConfigEntry<int> SaveBackupCount;
        public static ConfigEntry<int> SaveBackupShortSeconds;
        public static ConfigEntry<int> SaveBackupLongSeconds;

        // --- Telemetry.* --------------------------------------------------------------------------------
        public static ConfigEntry<float> TelemetryLogIntervalSeconds;

        // --- Transport.* (G6-N4 only — see class remarks) ------------------------------------------------
        public static ConfigEntry<bool> FeatureTransportG6N4;
        public static ConfigEntry<int> TransportG6N4MaxFieldBytes;

        /// <param name="main">The plugin's own <c>Config</c> (main file).</param>
        /// <param name="advanced">The advanced file; becomes <see cref="File"/>.</param>
        public static void Bind(ConfigFile main, ConfigFile advanced)
        {
            Main = main;
            File = advanced;
            ConfigFile config = advanced;

            ConfigSchemaVersion = main.Bind(ConfigSchema.General, ConfigSchema.SchemaVersionKey, ConfigSchema.CurrentVersion,
                "Managed by the mod. Do not edit.");
            // ConfigMigrator already ran; a lower number can only be a hand edit or a failed upgrade, and leaving it
            // would make the next boot re-run an old migration step over current settings.
            if (ConfigSchemaVersion.Value < ConfigSchema.CurrentVersion) ConfigSchemaVersion.Value = ConfigSchema.CurrentVersion;

            CoreEnabled = main.Bind(ConfigSchema.General, "Enabled", true,
                "Both (server and players). Master switch: off = the mod does nothing and the game behaves exactly " +
                "like vanilla. Applies live, no restart (also 'noodles enable|disable' in the console). Expert " +
                "settings live in " + ConfigSchema.AdvancedFileName + " and normally need no changes.");

            LogLevel = main.Bind(ConfigSchema.General, "LogLevel", NoodlesLogLevel.Normal,
                "Both. How much the mod writes to the game's log: Quiet = warnings and errors only, Normal = a " +
                "short summary plus important events, Verbose = detailed diagnostics for troubleshooting.");
            NoodlesLogSettings.Level = LogLevel.Value;
            LogLevel.SettingChanged += (_, __) => NoodlesLogSettings.Level = LogLevel.Value;

            // 1.1.0: every main-file key is bound here, in the order the file and README list them (BepInEx writes
            // keys in bind order, so binding them from each module would scatter them). Modules read these entries.
            MaxSendRatePerPlayerKiB = main.Bind(ConfigSchema.Network, "MaxSendRatePerPlayerKiB", 256,
                new ConfigDescription(
                    "Both. The fastest the server sends to each player, in KiB/s (and, with ClientUploadBoost on, the " +
                    "fastest a player's game sends to the server). Vanilla is 150. With AdaptiveRate on, each " +
                    "connection only climbs to this while it keeps up; 40 players at the default 256 use about 84 Mbit/s.",
                    new AcceptableValueRange<int>(MigrationV1ToV2.MaxSendRateMinKiB, MigrationV1ToV2.MaxSendRateMaxKiB)));
            ServerUploadLimitMiB = main.Bind(ConfigSchema.Network, "ServerUploadLimitMiB", 10,
                new ConfigDescription("Server only. The most upload the server may use for all players together, in " +
                    "MiB/s (the default 10 is about 84 Mbit/s); per-player rates stop rising once the server sends " +
                    "this much. Raise it for a big server on a fast line, lower it to protect a home connection.",
                    new AcceptableValueRange<int>(MigrationV1ToV2.ServerUploadMinMiB, MigrationV1ToV2.ServerUploadMaxMiB)));
            AdaptiveRate = main.Bind(ConfigSchema.Network, "AdaptiveRate", true,
                "Both. On = each connection starts at vanilla's rate and speeds up toward MaxSendRatePerPlayerKiB only " +
                "while that player's connection keeps up (no packet loss, steady ping). Off = every connection is " +
                "fixed at MaxSendRatePerPlayerKiB.");
            ClientUploadBoost = main.Bind(ConfigSchema.Network,"ClientUploadBoost", true,
                "Client only. On = your game uses the mod's faster upload settings on any server, modded or vanilla, " +
                "so other players see you and the things near you update more smoothly. Off = your upload stays exactly vanilla.");
            CompressWorldSync = main.Bind(ConfigSchema.Network,"CompressWorldSync", true,
                "Both. On = world updates between the server and a player who both run this mod are compressed, " +
                "so they use less bandwidth; players without the mod are never affected. Off = uncompressed, exactly vanilla.");
            ServerRunsWorld = main.Bind(ConfigSchema.ServerWorld, "ServerRunsWorld", true,
                "Server only. On = the server runs parts of the world itself (areas no player is standing in, plus " +
                "what Mode allows near players) instead of leaving them to one player's game, so one laggy player does " +
                "not make the world lag for everyone near them. Off = vanilla: every area is run by one of the players in it.");
            ServerWorldMode = main.Bind(ConfigSchema.ServerWorld, "Mode", NoodlesMcDoodles.Ownership.NearFieldClaimPolicy.OwnershipAuthorityMode.FarOrShared,
                new ConfigDescription(
                    "Server only. How much the server takes over near players when ServerRunsWorld is on. SharedFar = " +
                    "only areas no player is in; FarOrShared = also buildings, creatures, doors and ore near players; " +
                    "All = also chests, item stands, carts and dropped items. If other mods keep refusing it, it falls " +
                    "back to SharedFar until an admin runs 'noodles clear ModeQuarantine' (see the log)."));
            BestPingOwnsSharedObjects = main.Bind(ConfigSchema.ServerWorld, "BestPingOwnsSharedObjects", true,
                "Server only. On = when several players are near the same thing, the one with the best ping to the " +
                "server runs it, so everyone sees it with less lag. Off = vanilla (whoever got there first keeps it).");
            ServerHoldsForFarPlayers = main.Bind(ConfigSchema.ServerWorld, "ServerHoldsForFarPlayers", true,
                "Server only. EXPERIMENTAL. On = when every player near something has a high ping, the server runs " +
                "it itself so no player's updates have to go through another far-away player. Needs " +
                "BestPingOwnsSharedObjects on. Off = the best-ping player runs it, as usual.");
            PreserveEmptyAreas = main.Bind(ConfigSchema.ServerWorld, "PreserveEmptyAreas", true,
                "Server only. On = areas the server keeps running with no player in them do not wear down: no weather " +
                "damage to buildings, no despawning dropped items, no wildlife fights with tames or buildings there " +
                "(vanilla never runs those areas at all). Leave on while ServerRunsWorld is on.");
            DoorAutoClose = main.Bind(ConfigSchema.ServerWorld, "DoorAutoClose", true,
                "Server only. On = in areas the server is running, it closes player-built doors left open for a while " +
                "(never keyed or dungeon doors; ward mods do not see this). Off = doors stay as players left them.");
            ShortExtrapolation = main.Bind(ConfigSchema.Client, "ShortExtrapolation", true,
                new ConfigDescription(
                    "Client only. On = other players and creatures stop sliding sooner when their updates are late " +
                    "(after 0.5 s instead of vanilla's 2 s), so they snap back less. Off = vanilla.",
                    null));
            BufferedSmoothing = main.Bind(ConfigSchema.Client, "BufferedSmoothing", false,
                "Client only. EXPERIMENTAL. On = other players and creatures are drawn about 0.1 s in the past and " +
                "smoothed between updates: smoother most of the time, but can jump harder on a bad connection. Off = " +
                "vanilla movement (plus ShortExtrapolation if that is on).");
            PanelKey = main.Bind(ConfigSchema.Client, "PanelKey", new KeyboardShortcut(KeyCode.F6),
                "Client only. Key that cycles the on-screen status panel: hidden, Basic tab, Detailed tab. Ignored " +
                "while typing in chat or the console. Change it if another mod already uses F6.");
            PanelShowOnStart = main.Bind(ConfigSchema.Client, "PanelShowOnStart", false,
                "Client only. On = show the status panel's Basic tab when you join a world. Off = hidden until you press PanelKey.");
            PanelOpacity = main.Bind(ConfigSchema.Client, "PanelOpacity", 0.70f,
                new ConfigDescription(
                    "Client only. Status panel background darkness, from 0.1 (almost see-through) to 1 (solid black).",
                    new AcceptableValueRange<float>(0.1f, 1f)));

            AutoStandDownEnabled = main.Bind(ConfigSchema.Safety, "AutoStandDown", true,
                "Both. On = the mod switches itself off ([1 - General] Enabled = false) if it keeps hitting errors, " +
                "so a bug cannot keep affecting the game; an admin must turn it back on. Off = it never switches itself off.");
            // LEAD FIX: off by default. Created-minus-destroyed is not a loss signal on a live server: a player
            // exploring new land makes the owning client generate hundreds of ZDOs per zone (each one reaches the
            // server as a new ZDO), and chopping, mining and combat destroy them in bulk, so a healthy server
            // routinely exceeds any small per-minute tolerance and would switch the mod off in normal play.
            // M1 cannot create or destroy ZDOs at all, so here the trigger could only ever fire falsely. The
            // counters stay in telemetry; a real loss signal is designed with M3 (the first milestone that moves
            // ownership).
            AutoStandDownZdoDeltaTrigger = config.Bind("Core.AutoStandDown", "ZdoDeltaTrigger", false,
                "Let a sustained ZDO created/destroyed imbalance (ZdoMismatchTolerance, two consecutive telemetry " +
                "periods) switch the mod off. Off by default: normal exploration and building exceed any small " +
                "tolerance, so this is only useful on a quiet test server.");
            AutoStandDownZdoMismatchTolerance = config.Bind("Core.AutoStandDown", "ZdoMismatchTolerance", 50,
                new ConfigDescription("Sustained |ZdosCreated-ZdosDestroyed| drift, across two consecutive " +
                    "telemetry periods, before auto stand-down.", new AcceptableValueRange<int>(10, 500)));
            AutoStandDownExceptionBurstLimit = config.Bind("Core.AutoStandDown", "ExceptionBurstLimit", 5,
                new ConfigDescription("Caught-exception count from this mod's own patches inside a rolling 60s " +
                    "window before auto stand-down.", new AcceptableValueRange<int>(1, 50)));

            FeatureHandshakeReentryGuard = config.Bind("Features", "HandshakeReentryGuard.Active", true,
                "G1-L1: reject a re-entrant RPC_PeerInfo from an already-ready peer (closes the duplicate-peer " +
                "AddPeer bug). Server-side only; no-op on a client.");
            FeatureNegotiation = config.Bind("Features", "Negotiation.Active", true,
                "Hello handshake. Off disables every capability-gated feature for every peer (they're all " +
                "treated as vanilla).");
            FeatureConfigSync = config.Bind("Features", "ConfigSync.Active", true,
                "Gates the ConfigSync push/receive path only; RPC name registration itself is unconditional " +
                "so a live off->on flip doesn't need a reconnect.");
            FeatureSaveIntervalClamp = config.Bind("Features", "SaveIntervalClamp.Active", true,
                "P1: clamp Game.m_saveInterval to Save.AutosaveIntervalSeconds (hard floor 30s).");
            FeatureBackupPolicy = config.Bind("Features", "BackupPolicy.Active", true,
                "P2: clamp ZNet.m_backupCount/m_backupShort/m_backupLong to Save.BackupCount/BackupShortSeconds/BackupLongSeconds.");

            HandshakeTimeoutSeconds = config.Bind("Handshake", "TimeoutSeconds", 10f,
                new ConfigDescription("Time to wait for a peer's Hello reply before marking it KnownVanilla " +
                    "(bookkeeping only, never a disconnect).", new AcceptableValueRange<float>(3f, 30f)));
            ConfigSyncPushIntervalSeconds = config.Bind("ConfigSync", "PushIntervalSeconds", 30f,
                new ConfigDescription("Periodic re-push after negotiation; covers late capability adoption and " +
                    "reconnects. Server-only meaningful.", new AcceptableValueRange<float>(5f, 300f)));

            SaveAutosaveIntervalSeconds = config.Bind("Save", "AutosaveIntervalSeconds", 1800f,
                new ConfigDescription("World autosave interval. Used only when the server's command line has no " +
                    "-saveinterval (the command line always wins). Hard-clamped in code to >=30s (Mathf.Max(30,...) " +
                    "protects a direct field write with no engine-side floor of its own — see P1).",
                    new AcceptableValueRange<float>(30f, 1800f)));
            SaveBackupCount = config.Bind("Save", "BackupCount", 4,
                new ConfigDescription("Used only when the command line has no -backups (the command line always wins). " +
                    "1 is vanilla's own sentinel for \"no backups at all\" (ConsiderAutoBackup " +
                    "treats m_backupCount==1 as 0) — this range excludes it; a legacy 1 is upgraded to 2 with a " +
                    "warning, never passed through.", new AcceptableValueRange<int>(2, 10)));
            // int, matching ZNet.m_backupShort/m_backupLong's actual field type (SERVER:79102/79104) exactly —
            // vanilla's own CLI parse clamps the same way (Mathf.Max(5, result), SERVER:97346/97350).
            SaveBackupShortSeconds = config.Bind("Save", "BackupShortSeconds", 7200,
                new ConfigDescription("Used only when the command line has no -backupshort. Floor 5s.", new AcceptableValueRange<int>(5, 604800)));
            SaveBackupLongSeconds = config.Bind("Save", "BackupLongSeconds", 43200,
                new ConfigDescription("Used only when the command line has no -backuplong. Floor 5s.", new AcceptableValueRange<int>(5, 2592000)));

            // 1.1.0: default 60 -> 600 (one summary line every 10 minutes instead of every minute).
            TelemetryLogIntervalSeconds = config.Bind("Telemetry", "LogIntervalSeconds", 600f,
                new ConfigDescription("How often the '[NoodlesMcDoodles] telemetry ...' summary line is logged.",
                    new AcceptableValueRange<float>(10f, 600f)));

            FeatureTransportG6N4 = config.Bind("Features", "Transport.G6N4.Active", true,
                "G6-N4: reject any mod-added ZDO field write over Transport.G6N4_MaxFieldBytes (SafeZdoWrite). " +
                "Off = callers bypass the size check and call ZDO.Set directly.");
            TransportG6N4MaxFieldBytes = config.Bind("Transport", "G6N4_MaxFieldBytes", 2048,
                new ConfigDescription("SafeZdoWrite's per-field cap for any mod-added ZDO field this mod writes.",
                    new AcceptableValueRange<int>(256, 8192)));
        }
    }
}
