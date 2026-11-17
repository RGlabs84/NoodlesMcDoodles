using System;
using System.Collections.Generic;
using System.Globalization;

namespace NoodlesMcDoodles.Core.ConfigFiles
{
    /// <summary>
    /// Schema v1 (every release up to 1.0.13: one file, ~130 keys in 19 sections) to v2 (a short main file plus
    /// <see cref="ConfigSchema.AdvancedFileName"/>).
    ///
    /// Rule: a value is carried only if it differs from that entry's own <c># Default value:</c> comment (no
    /// comment = carried). Admins keep what they changed; everything they never touched picks up the current
    /// defaults. <see cref="Promoted"/> keys move to the main file under their v2 names (two with a unit change);
    /// every key in <see cref="V1AdvancedKeys"/> goes to the advanced file under the same section and key; any
    /// other key no longer exists and is dropped (counted in the result).
    /// </summary>
    internal sealed class MigrationV1ToV2 : IConfigMigration
    {
        public int From => 1;
        public int To => 2;

        internal sealed class PromotedKey
        {
            public PromotedKey(string v1Section, string v1Key, string v2Section, string v2Key, Func<string, string> convert = null)
            {
                V1Section = v1Section;
                V1Key = v1Key;
                V2Section = v2Section;
                V2Key = v2Key;
                Convert = convert ?? (v => v);
            }

            public string V1Section { get; }
            public string V1Key { get; }
            public string V2Section { get; }
            public string V2Key { get; }

            /// <summary>v1 serialized value to v2 serialized value; null = unreadable.</summary>
            public Func<string, string> Convert { get; }
        }

        /// <summary>v1 keys that became main-file keys. Must match the Main.Bind calls (tools/cfg-migrate-test checks).</summary>
        internal static readonly PromotedKey[] Promoted =
        {
            new PromotedKey("Core", "Enabled", ConfigSchema.General, "Enabled"),

            new PromotedKey("Transport", "T1_GlobalRateBps", ConfigSchema.Network, "MaxSendRatePerPlayerKiB", BytesToKiB),
            new PromotedKey("Transport", "T9_ServerTotalBps", ConfigSchema.Network, "ServerUploadLimitMiB", BytesToMiB),
            new PromotedKey("Features", "Transport.T9.Active", ConfigSchema.Network, "AdaptiveRate"),
            new PromotedKey("Transport", "Q10_Enabled", ConfigSchema.Network, "ClientUploadBoost"),
            new PromotedKey("Features", "ZdoCompression.Active", ConfigSchema.Network, "CompressWorldSync"),

            new PromotedKey("OwnershipAuthority", "Active", ConfigSchema.ServerWorld, "ServerRunsWorld"),
            new PromotedKey("OwnershipAuthority", "Mode", ConfigSchema.ServerWorld, "Mode"),
            new PromotedKey("O9", "Active", ConfigSchema.ServerWorld, "BestPingOwnsSharedObjects"),
            new PromotedKey("O10", "Active", ConfigSchema.ServerWorld, "ServerHoldsForFarPlayers"),
            new PromotedKey("Features", "HeldBandPreservationGuard.Active", ConfigSchema.ServerWorld, "PreserveEmptyAreas"),
            new PromotedKey("Features", "DoorAutoClose.Active", ConfigSchema.ServerWorld, "DoorAutoClose"),

            new PromotedKey("ClientSmoothing", "ShortExtrapolation.Active", ConfigSchema.Client, "ShortExtrapolation"),
            new PromotedKey("ClientSmoothing", "Active", ConfigSchema.Client, "BufferedSmoothing"),
            new PromotedKey("Overlay", "ToggleKey", ConfigSchema.Client, "PanelKey"),
            new PromotedKey("Overlay", "ShowOnStart", ConfigSchema.Client, "PanelShowOnStart"),
            new PromotedKey("Overlay", "Opacity", ConfigSchema.Client, "PanelOpacity"),

            new PromotedKey("Core.AutoStandDown", "Enabled", ConfigSchema.Safety, "AutoStandDown"),
        };

        // MaxSendRatePerPlayerKiB's range (v1 T1_GlobalRateBps 153600..104857600 bytes/s, /1024).
        internal const int MaxSendRateMinKiB = 150;
        internal const int MaxSendRateMaxKiB = 102400;

        // ServerUploadLimitMiB's range (v1 T9_ServerTotalBps 1048576..104857600 bytes/s, /1048576).
        internal const int ServerUploadMinMiB = 1;
        internal const int ServerUploadMaxMiB = 100;

        /// <summary>bytes/s -> KiB/s, rounded to the nearest KiB, clamped to the v2 range. Null if unreadable.</summary>
        internal static string BytesToKiB(string bytes) =>
            ConvertUnits(bytes, ConfigSchema.BytesPerKiB, MaxSendRateMinKiB, MaxSendRateMaxKiB);

        /// <summary>bytes/s -> MiB/s, rounded to the nearest MiB (minimum 1), clamped to the v2 range. Null if unreadable.</summary>
        internal static string BytesToMiB(string bytes) =>
            ConvertUnits(bytes, ConfigSchema.BytesPerMiB, ServerUploadMinMiB, ServerUploadMaxMiB);

        private static string ConvertUnits(string value, int unit, int min, int max)
        {
            if (value == null) return null;
            if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ||
                double.IsNaN(v) || double.IsInfinity(v))
                return null;
            double units = Math.Round(v / unit, MidpointRounding.AwayFromZero);
            int clamped = (int)Math.Max(min, Math.Min(max, units));
            return clamped.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Every v1 key (1.0.13) that stays in the advanced file under the same section and key: all v1 keys minus
        /// <see cref="Promoted"/>. tools/cfg-migrate-test checks this against the Bind calls in src/.
        /// </summary>
        internal static readonly (string Section, string Key)[] V1AdvancedKeys =
        {
            // [ClientRefPos]
            ("ClientRefPos", "Interval"),
            // [ClientSmoothing]
            ("ClientSmoothing", "ExtrapolationCapMs"),
            ("ClientSmoothing", "PlaybackDelayMs"),
            // [ConfigSync]
            ("ConfigSync", "PushIntervalSeconds"),
            // [Core.AutoStandDown]
            ("Core.AutoStandDown", "ExceptionBurstLimit"),
            ("Core.AutoStandDown", "ZdoDeltaTrigger"),
            ("Core.AutoStandDown", "ZdoMismatchTolerance"),
            // [Core.FeatureStandDown]
            ("Core.FeatureStandDown", "ExceptionBurstLimit"),
            ("Core.FeatureStandDown", "WindowSeconds"),
            // [Features]
            ("Features", "BackupPolicy.Active"),
            ("Features", "ConfigSync.Active"),
            ("Features", "DoorAutoClose.IdleSeconds"),
            ("Features", "DoorAutoClose.ScanIntervalSeconds"),
            ("Features", "HandoverGuard.QuiescenceSeconds"),
            ("Features", "HandshakeReentryGuard.Active"),
            ("Features", "HeadlessBiomeWind.Active"),
            ("Features", "HeadlessNRESweep.Active"),
            ("Features", "Negotiation.Active"),
            ("Features", "ProductionHandoverGuard.Active"),
            ("Features", "SaveIntervalClamp.Active"),
            ("Features", "ServerCombatStats.Active"),
            ("Features", "ServerSpawning.Active"),
            ("Features", "ServerSpawning.MaxZonesScannedPerSecond"),
            ("Features", "ServerZoneSim.Active"),
            ("Features", "StructureHandoverGuard.Active"),
            ("Features", "Transport.G2N1.Active"),
            ("Features", "Transport.G6N4.Active"),
            ("Features", "Transport.Q12.Active"),
            ("Features", "Transport.Q13.Active"),
            ("Features", "Transport.Q13.Client.Active"),
            ("Features", "Transport.Q2.Active"),
            ("Features", "Transport.Q3.Active"),
            ("Features", "Transport.Q4.Active"),
            ("Features", "Transport.Q6.Active"),
            ("Features", "Transport.Q7.Active"),
            ("Features", "Transport.Q8.Active"),
            ("Features", "Transport.T1.Active"),
            ("Features", "Transport.T2.Active"),
            ("Features", "Transport.T3.Active"),
            ("Features", "Transport.T4.Active"),
            ("Features", "Transport.T5.Active"),
            ("Features", "UpdateIsolation.Active"),
            ("Features", "VehicleContainerHandover.Active"),
            // [Handshake]
            ("Handshake", "TimeoutSeconds"),
            // [O10]
            ("O10", "FarRttThresholdMs"),
            // [O9]
            ("O9", "RttDwellSeconds"),
            ("O9", "RttEwmaAlpha"),
            ("O9", "RttMarginMs"),
            // [OwnershipAuthority]
            ("OwnershipAuthority", "AllowShipClaim"),
            ("OwnershipAuthority", "ClaimGraceWindow"),
            ("OwnershipAuthority", "ClaimScanBudgetMs"),
            ("OwnershipAuthority", "CompatClientClaimGraceSeconds"),
            ("OwnershipAuthority", "O7HoldTimeoutAlert"),
            ("OwnershipAuthority", "PeerRefPosPadding"),
            ("OwnershipAuthority", "PickableReleaseRadius"),
            ("OwnershipAuthority", "ReleaseZdosInterval"),
            ("OwnershipAuthority", "SimulationDistanceCeiling"),
            // [OwnershipAuthority.LoadShed]
            ("OwnershipAuthority.LoadShed", "MaxZonesShedPerCycle"),
            ("OwnershipAuthority.LoadShed", "ShedEnterDwellSeconds"),
            ("OwnershipAuthority.LoadShed", "ShedEnterMs"),
            ("OwnershipAuthority.LoadShed", "ShedExitDwellSeconds"),
            ("OwnershipAuthority.LoadShed", "ShedExitMs"),
            // [OwnershipAuthority.ModeQuarantine]
            ("OwnershipAuthority.ModeQuarantine", "Active"),
            ("OwnershipAuthority.ModeQuarantine", "VetoBurstLimit"),
            ("OwnershipAuthority.ModeQuarantine", "VetoBurstWindowSeconds"),
            // [Save]
            ("Save", "AutosaveIntervalSeconds"),
            ("Save", "BackupCount"),
            ("Save", "BackupLongSeconds"),
            ("Save", "BackupShortSeconds"),
            // [Telemetry]
            ("Telemetry", "LogIntervalSeconds"),
            // [Transport]
            ("Transport", "G2N1_ChunkBytes"),
            ("Transport", "G6N4_MaxFieldBytes"),
            ("Transport", "Q12_FramingMarginBytes"),
            ("Transport", "Q13_ClientCeilingBytes"),
            ("Transport", "Q13_FrameLoadMs"),
            ("Transport", "Q13_RttSlack"),
            ("Transport", "Q13_ServerAllowanceCapBytes"),
            ("Transport", "Q2_Adaptive"),
            ("Transport", "Q2_PeersPerTick"),
            ("Transport", "Q3_InnerBudgetBytes"),
            ("Transport", "Q4_CongestionFloorBytes"),
            ("Transport", "Q6_ServerZoneBias"),
            ("Transport", "Q7_ChunkIdCount"),
            ("Transport", "Q8_ForceSendBatchCap"),
            ("Transport", "T2_JoinBurstBps"),
            ("Transport", "T2_MaxBurstSeconds"),
            ("Transport", "T3_TimeoutConnectedMs"),
            ("Transport", "T4_PingIntervalSec"),
            ("Transport", "T4_TimeoutSec"),
            ("Transport", "T5_SendBufferBytes"),
            ("Transport", "T8_Enabled"),
            ("Transport", "T9_FloorBps"),
            ("Transport", "T9_LossThreshold"),
            ("Transport", "T9_PingInflationMs"),
            // [ZdoCompression]
            ("ZdoCompression", "AckTimeoutMs"),
            ("ZdoCompression", "CompressionLevel"),
            // [ZoneSim]
            ("ZoneSim", "CapExcludeThresholdPct"),
            ("ZoneSim", "CapIncludeThresholdPct"),
            ("ZoneSim", "CreateBudgetMs"),
            ("ZoneSim", "CreateDestroyHz"),
            ("ZoneSim", "HeldBandRingBonus"),
            ("ZoneSim", "MaxCreatedPerFrame"),
            ("ZoneSim", "MaxGhostSpawnsPerTick"),
            ("ZoneSim", "MaxObjectsGlobal"),
            ("ZoneSim", "MaxObjectsPerPeer"),
            ("ZoneSim", "MaxZoneBuildsPerTick"),
            ("ZoneSim", "MinCreatedPerTick"),
            ("ZoneSim", "ObjectUnfeedGraceTicks"),
            ("ZoneSim", "PokeRingRadius"),
            ("ZoneSim", "UpdateTickSeconds"),
            ("ZoneSim", "ZoneBuildBudgetMs"),
            ("ZoneSim", "ZoneTTLSeconds"),
        };

        public void Apply(MigrationContext ctx)
        {
            CfgDocument v1 = ctx.Main;
            ConfigMigrationResult result = ctx.Result;

            var promoted = new Dictionary<string, PromotedKey>(StringComparer.Ordinal);
            foreach (PromotedKey p in Promoted) promoted[Id(p.V1Section, p.V1Key)] = p;
            var advanced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var k in V1AdvancedKeys) advanced.Add(Id(k.Section, k.Key));

            var main = new CfgDocument { NewLine = v1.NewLine };
            main.Preamble.AddRange(v1.Preamble);
            foreach (string s in new[] { ConfigSchema.General, ConfigSchema.Network, ConfigSchema.ServerWorld, ConfigSchema.Client, ConfigSchema.Safety })
                main.GetOrAddSection(s);

            int recognised = 0;
            foreach (CfgEntry e in v1.AllEntries)
            {
                string id = Id(e.Section, e.Key);

                if (promoted.TryGetValue(id, out PromotedKey p))
                {
                    recognised++;
                    if (e.IsDefault) continue;

                    string value = p.Convert(e.Value);
                    if (value == null)
                    {
                        result.Dropped.Add($"[{e.Section}] {e.Key} (unreadable value '{e.Value}')");
                        continue;
                    }

                    string v1Default = e.DefaultValue;
                    string v2Default = v1Default != null ? p.Convert(v1Default) : null;
                    // A unit conversion can round a changed value back onto the default (e.g. 262000 B/s -> 256 KiB/s).
                    if (v2Default != null && CfgEntry.ValuesEqual(value, v2Default)) continue;

                    main.Set(p.V2Section, p.V2Key, value,
                        v2Default != null ? new[] { CfgDocument.DefaultValueComment(v2Default) } : new string[0]);
                    result.Carried.Add(new CarriedSetting
                    {
                        FromSection = e.Section, FromKey = e.Key, FromValue = e.Value,
                        ToMainFile = true, ToSection = p.V2Section, ToKey = p.V2Key, ToValue = value,
                    });
                }
                else if (advanced.Contains(id))
                {
                    recognised++;
                    if (e.IsDefault) continue;

                    // Same section/key; keep its description/type/default/range comments until BepInEx rewrites them.
                    ctx.Advanced.Set(e.Section, e.Key, e.Value, e.Comments);
                    result.Carried.Add(new CarriedSetting
                    {
                        FromSection = e.Section, FromKey = e.Key, FromValue = e.Value,
                        ToMainFile = false, ToSection = e.Section, ToKey = e.Key, ToValue = e.Value,
                    });
                }
                else
                {
                    result.Dropped.Add($"[{e.Section}] {e.Key}");
                }
            }

            if (recognised == 0)
                throw new FormatException("no NoodlesMcDoodles settings found in the file (not a NoodlesMcDoodles config?)");

            ctx.Main = main;
        }

        private static string Id(string section, string key) => section + "\u0001" + key;
    }
}
