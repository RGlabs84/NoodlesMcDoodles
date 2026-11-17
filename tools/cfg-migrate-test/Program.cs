// cfg-migrate-test: checks src/Core/Config (ConfigMigrator, schema v1 -> v2) against real and synthetic files.
//
//   dotnet run -c Release --project tools/cfg-migrate-test -- --live <copy of a real v1 wubarrk.noodlesmcdoodles.cfg>
//                                                           [--work <scratch dir>] [--src <repo>/src]
//
// --live is copied, never modified (point it at a COPY anyway). --work defaults to a new temp folder. --src
// defaults to the src/ folder found above this tool, and is used to check the migrator's key tables against
// the mod's actual Bind(...) calls. Exit code 0 = every check passed, 1 = at least one failed, 2 = bad usage.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using NoodlesMcDoodles.Core.ConfigFiles;

namespace CfgMigrateTest
{
    internal static class Program
    {
        private static int s_failures;
        private static int s_checks;

        private static int Main(string[] args)
        {
            string live = Arg(args, "--live");
            string work = Arg(args, "--work") ?? Path.Combine(Path.GetTempPath(), "cfg-migrate-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string src = Arg(args, "--src") ?? FindSrc();
            if (live == null || !File.Exists(live))
            {
                Console.Error.WriteLine("usage: cfg-migrate-test --live <copy of a v1 .cfg> [--work <dir>] [--src <repo>/src]");
                return 2;
            }

            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);
            Console.WriteLine($"work dir: {work}");

            if (src != null) Section("0. key tables match the mod's Bind calls", () => TestKeyTables(src));
            else Console.WriteLine("(src/ not found: key-table check skipped)");

            Section("a. live v1 file", () => TestLive(live, work));
            Section("a2. live v1 file with CRLF line endings", () => TestCrlf(live, work));
            Section("b. pristine v1 file (all defaults)", () => TestPristine(live, work));
            Section("c. second run is a no-op", () => TestIdempotent(work));
            Section("d. corrupt / garbage files", () => TestGarbage(work));
            Section("e. unit conversions and other edges", () => TestEdges(live, work));

            Console.WriteLine();
            Console.WriteLine(s_failures == 0
                ? $"ALL PASSED ({s_checks} checks)"
                : $"FAILED: {s_failures} of {s_checks} checks");
            return s_failures == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------------------------------------------------
        private static void TestLive(string live, string work)
        {
            string dir = Dir(work, "a");
            string main = Path.Combine(dir, ConfigSchema.MainFileName);
            string adv = Path.Combine(dir, ConfigSchema.AdvancedFileName);
            File.Copy(live, main);
            byte[] original = File.ReadAllBytes(main);
            CfgDocument v1 = CfgDocument.Load(main);

            var logs = new List<string>();
            ConfigMigrationResult r = ConfigMigrator.Run(main, adv, logs.Add);

            Check(r.Outcome == ConfigMigrationOutcome.Migrated, $"outcome Migrated (got {r.Outcome}, {r.Error?.Message})");
            Check(logs.Count == 1, $"exactly one log line (got {logs.Count})");
            if (logs.Count > 0) Console.WriteLine("  log: " + logs[0]);
            Check(r.FromVersion == 1 && r.ToVersion == 2, "v1 -> v2");

            Console.WriteLine($"  carried ({r.Carried.Count}):");
            foreach (CarriedSetting c in r.Carried) Console.WriteLine("    " + c);
            Console.WriteLine($"  dropped ({r.Dropped.Count}): {string.Join(", ", r.Dropped)}");

            ExpectCarried(r, "Mode", "All", toMain: true);
            ExpectCarried(r, "AutoStandDown", "false", toMain: true);
            ExpectCarried(r, "BufferedSmoothing", "true", toMain: true);
            ExpectCarried(r, "Transport.T3_TimeoutConnectedMs", "120000", toMain: false);
            ExpectCarried(r, "Transport.T4_TimeoutSec", "90", toMain: false);
            ExpectCarried(r, "Telemetry.LogIntervalSeconds", "600", toMain: false);
            ExpectNotCarried(r, "Enabled");
            ExpectNotCarried(r, "MaxSendRatePerPlayerKiB");
            ExpectNotCarried(r, "Transport.Q2_PeersPerTick");
            ExpectNotCarried(r, "ZoneSim.MaxObjectsPerPeer");

            // Generic rule check: carried <=> differs from (or has no) default comment, for every recognised key.
            var carriedFrom = new HashSet<string>(r.Carried.Select(c => c.FromSection + "|" + c.FromKey));
            foreach (CfgEntry e in v1.AllEntries)
            {
                string id = e.Section + "|" + e.Key;
                bool known = MigrationV1ToV2.Promoted.Any(p => p.V1Section == e.Section && p.V1Key == e.Key) ||
                             MigrationV1ToV2.V1AdvancedKeys.Any(k => k.Section == e.Section && k.Key == e.Key);
                if (!known) continue;
                bool shouldCarry = e.DefaultValue == null || !CfgEntry.ValuesEqual(e.Value, e.DefaultValue);
                Check(carriedFrom.Contains(id) == shouldCarry,
                    $"[{e.Section}] {e.Key} = {e.Value} (default {e.DefaultValue ?? "none"}) carried={carriedFrom.Contains(id)}", quiet: true);
            }

            // Files on disk.
            Check(r.BackupPath != null && File.Exists(r.BackupPath), $"backup exists ({r.BackupPath})");
            if (r.BackupPath != null && File.Exists(r.BackupPath))
            {
                Check(File.ReadAllBytes(r.BackupPath).SequenceEqual(original), "backup is byte-identical to the original");
                Check(Regex.IsMatch(Path.GetFileName(r.BackupPath), @"^wubarrk\.noodlesmcdoodles\.cfg\.v1-backup-\d{8}-\d{6}$"),
                    $"backup name pattern ({Path.GetFileName(r.BackupPath)})");
            }

            CfgDocument m = CfgDocument.Load(main);
            CfgDocument a = CfgDocument.Load(adv);
            Check(m.Find(ConfigSchema.General, ConfigSchema.SchemaVersionKey)?.Value == "2", "main has [1 - General] ConfigSchemaVersion = 2");
            Check(m.AllEntries.Count() == 1 + r.Carried.Count(c => c.ToMainFile), "main = schema version + carried main keys only");
            Check(a.AllEntries.Count() == r.Carried.Count(c => !c.ToMainFile), "advanced = carried advanced keys only");
            foreach (CarriedSetting c in r.Carried)
            {
                CfgEntry e = (c.ToMainFile ? m : a).Find(c.ToSection, c.ToKey);
                Check(e != null && e.Value == c.ToValue, $"{c.DisplayName} = {c.ToValue} is in the {(c.ToMainFile ? "main" : "advanced")} file", quiet: true);
            }
            Check(m.GetSection("Core") == null && m.GetSection("Transport") == null, "no v1 sections left in main");
            Check(a.Find("Transport", "T3_TimeoutConnectedMs")?.DefaultValue == "30000", "advanced entry keeps its v1 comments");

            Console.WriteLine("  --- migrated main file ---");
            foreach (string l in File.ReadAllLines(main)) Console.WriteLine("  | " + l);
        }

        private static void TestCrlf(string live, string work)
        {
            string dir = Dir(work, "a2");
            string main = Path.Combine(dir, ConfigSchema.MainFileName);
            string text = File.ReadAllText(live).Replace("\r\n", "\n").Replace("\n", "\r\n");
            File.WriteAllText(main, text, new UTF8Encoding(true)); // with a BOM too

            string lfDir = Dir(work, "a2-lf");
            string lfMain = Path.Combine(lfDir, ConfigSchema.MainFileName);
            File.WriteAllText(lfMain, text.Replace("\r\n", "\n"));
            ConfigMigrationResult lf = ConfigMigrator.Run(lfMain, Path.Combine(lfDir, ConfigSchema.AdvancedFileName), _ => { });

            ConfigMigrationResult r = ConfigMigrator.Run(main, Path.Combine(dir, ConfigSchema.AdvancedFileName), _ => { });
            Check(r.Outcome == ConfigMigrationOutcome.Migrated, $"outcome Migrated (got {r.Outcome}, {r.Error?.Message})");
            Check(r.Carried.Select(c => c.ToString()).SequenceEqual(lf.Carried.Select(c => c.ToString())),
                $"same settings carried as the LF file ({r.Carried.Count} vs {lf.Carried.Count})");
            Check(File.ReadAllText(main).Contains("\r\n"), "CRLF kept in the rewritten main file");
        }

        private static void TestPristine(string live, string work)
        {
            string dir = Dir(work, "b");
            string main = Path.Combine(dir, ConfigSchema.MainFileName);
            string adv = Path.Combine(dir, ConfigSchema.AdvancedFileName);

            // Build the pristine file from the live one: every value reset to its own "# Default value:" comment.
            CfgDocument doc = CfgDocument.Load(live);
            int reset = 0;
            foreach (CfgEntry e in doc.AllEntries)
            {
                if (e.DefaultValue != null && e.Value != e.DefaultValue) { e.Value = e.DefaultValue; reset++; }
            }
            File.WriteAllText(main, doc.Serialize());
            Console.WriteLine($"  generated pristine v1 file ({doc.AllEntries.Count()} entries, {reset} reset to default)");

            var logs = new List<string>();
            ConfigMigrationResult r = ConfigMigrator.Run(main, adv, logs.Add);
            Check(r.Outcome == ConfigMigrationOutcome.Migrated, $"outcome Migrated (got {r.Outcome}, {r.Error?.Message})");
            Check(r.Carried.Count == 0, $"nothing carried (got {r.Carried.Count}: {string.Join(", ", r.Carried.Select(c => c.DisplayName))})");
            Check(r.Dropped.Count == 0, $"nothing dropped (got {r.Dropped.Count})");
            Check(logs.Count == 1, $"exactly one log line (got {logs.Count})");
            if (logs.Count > 0) Console.WriteLine("  log: " + logs[0]);

            CfgDocument m = CfgDocument.Load(main);
            Check(m.AllEntries.Count() == 1 && m.Find(ConfigSchema.General, ConfigSchema.SchemaVersionKey)?.Value == "2",
                "main file has only ConfigSchemaVersion = 2");
            Check(CfgDocument.Load(adv).AllEntries.Count() == 0, "advanced file has no entries (BepInEx fills in defaults)");
        }

        private static void TestIdempotent(string work)
        {
            foreach (string name in new[] { "a", "b" })
            {
                string dir = Path.Combine(work, name);
                string main = Path.Combine(dir, ConfigSchema.MainFileName);
                string adv = Path.Combine(dir, ConfigSchema.AdvancedFileName);
                byte[] m0 = File.ReadAllBytes(main), a0 = File.ReadAllBytes(adv);
                int files0 = Directory.GetFiles(dir).Length;

                var logs = new List<string>();
                ConfigMigrationResult r = ConfigMigrator.Run(main, adv, logs.Add);
                Check(r.Outcome == ConfigMigrationOutcome.UpToDate, $"({name}) outcome UpToDate (got {r.Outcome})");
                Check(logs.Count == 0, $"({name}) nothing logged (got {logs.Count})");
                Check(!r.MainFileRewritten && !r.AdvancedFileWritten, $"({name}) nothing written");
                Check(File.ReadAllBytes(main).SequenceEqual(m0) && File.ReadAllBytes(adv).SequenceEqual(a0), $"({name}) files byte-identical");
                Check(Directory.GetFiles(dir).Length == files0, $"({name}) no new backup");
            }
        }

        private static void TestGarbage(string work)
        {
            var rng = new Random(1234);
            byte[] binary = new byte[4096];
            rng.NextBytes(binary);

            var cases = new (string Name, byte[] Bytes)[]
            {
                ("random binary", binary),
                ("text, no settings", Encoding.UTF8.GetBytes("this is not a config file\n{{{ ]]] \n<html>\n")),
                ("settings, none of ours", Encoding.UTF8.GetBytes("[SomeOtherMod]\nFoo = 1\nBar = true\n")),
                ("bad schema version", Encoding.UTF8.GetBytes("[1 - General]\nConfigSchemaVersion = banana\n[Core]\nEnabled = false\n")),
                ("invalid UTF-8", new byte[] { (byte)'[', (byte)'C', (byte)']', 10, 0xC3, 0x28, (byte)'=', (byte)'1', 10 }),
            };

            int i = 0;
            foreach (var c in cases)
            {
                string dir = Dir(work, "d" + (++i));
                string main = Path.Combine(dir, ConfigSchema.MainFileName);
                string adv = Path.Combine(dir, ConfigSchema.AdvancedFileName);
                File.WriteAllBytes(main, c.Bytes);

                var info = new List<string>();
                var warn = new List<string>();
                ConfigMigrationResult r = ConfigMigrator.Run(main, adv, info.Add, warn.Add);
                Check(r.Outcome == ConfigMigrationOutcome.Failed, $"({c.Name}) outcome Failed (got {r.Outcome})");
                Check(warn.Count == 1 && info.Count == 0, $"({c.Name}) exactly one warning, no info (got {warn.Count}/{info.Count})");
                if (warn.Count > 0) Console.WriteLine($"  warning ({c.Name}): {warn[0]}");
                Check(File.ReadAllBytes(main).SequenceEqual(c.Bytes), $"({c.Name}) original untouched");
                Check(!File.Exists(adv), $"({c.Name}) no advanced file written");
                Check(!r.MainFileRewritten, $"({c.Name}) main not rewritten");
            }
        }

        private static void TestEdges(string live, string work)
        {
            // Unit conversions.
            Check(MigrationV1ToV2.BytesToKiB("262144") == "256", "262144 B/s -> 256 KiB/s");
            Check(MigrationV1ToV2.BytesToKiB("524288") == "512", "524288 B/s -> 512 KiB/s");
            Check(MigrationV1ToV2.BytesToKiB("153600") == "150", "153600 B/s -> 150 KiB/s (vanilla, range min)");
            Check(MigrationV1ToV2.BytesToKiB("1000") == "150", "below range clamps to 150");
            Check(MigrationV1ToV2.BytesToKiB("104857600") == "102400", "range max 104857600 -> 102400");
            Check(MigrationV1ToV2.BytesToKiB("abc") == null, "unreadable -> null");
            Check(MigrationV1ToV2.BytesToMiB("10485760") == "10", "10485760 B/s -> 10 MiB/s");
            Check(MigrationV1ToV2.BytesToMiB("15000000") == "14", "15000000 B/s -> 14 MiB/s (rounded)");
            Check(MigrationV1ToV2.BytesToMiB("1000") == "1", "tiny -> 1 MiB/s (minimum)");
            Check(MigrationV1ToV2.BytesToMiB("104857600") == "100", "range max -> 100 MiB/s");

            // Custom rates in a v1 file land converted in the main file; a removed key is dropped and counted.
            string dir = Dir(work, "e1");
            string main = Path.Combine(dir, ConfigSchema.MainFileName);
            string adv = Path.Combine(dir, ConfigSchema.AdvancedFileName);
            CfgDocument doc = CfgDocument.Load(live);
            doc.Find("Transport", "T1_GlobalRateBps").Value = "524288";
            doc.Find("Transport", "T9_ServerTotalBps").Value = "20971520";
            doc.Find("Overlay", "Opacity").Value = "0.70"; // same number as the default 0.7: not carried
            doc.Set("Transport", "T7_RemovedLever", "5", new[] { "# Default value: 1" });
            File.WriteAllText(main, doc.Serialize());
            var logs = new List<string>();
            ConfigMigrationResult r = ConfigMigrator.Run(main, adv, logs.Add);
            Check(r.Outcome == ConfigMigrationOutcome.Migrated, "outcome Migrated");
            ExpectCarried(r, "MaxSendRatePerPlayerKiB", "512", toMain: true);
            ExpectCarried(r, "ServerUploadLimitMiB", "20", toMain: true);
            ExpectNotCarried(r, "PanelOpacity");
            Check(r.Dropped.Count == 1 && r.Dropped[0].Contains("T7_RemovedLever"), $"removed key dropped ({string.Join(", ", r.Dropped)})");
            Check(logs.Count == 1 && logs[0].Contains("1 obsolete setting dropped"), "log line counts the dropped key");
            if (logs.Count > 0) Console.WriteLine("  log: " + logs[0]);
            CfgDocument m = CfgDocument.Load(main);
            Check(m.Find(ConfigSchema.Network, "MaxSendRatePerPlayerKiB")?.DefaultValue == "256", "converted default comment written (256)");

            // No main file = fresh install: nothing done, nothing logged.
            dir = Dir(work, "e0");
            var none = new List<string>();
            r = ConfigMigrator.Run(Path.Combine(dir, ConfigSchema.MainFileName), Path.Combine(dir, ConfigSchema.AdvancedFileName), none.Add);
            Check(r.Outcome == ConfigMigrationOutcome.FreshInstall && none.Count == 0 && Directory.GetFiles(dir).Length == 0,
                "missing main file: FreshInstall, silent, nothing written");

            // A missing default comment means "carry it".
            dir = Dir(work, "e2");
            main = Path.Combine(dir, ConfigSchema.MainFileName);
            File.WriteAllText(main, "[Core]\nEnabled = true\n\n[ZoneSim]\nPokeRingRadius = 2\n");
            r = ConfigMigrator.Run(main, Path.Combine(dir, ConfigSchema.AdvancedFileName), _ => { });
            ExpectCarried(r, "Enabled", "true", toMain: true);
            ExpectCarried(r, "ZoneSim.PokeRingRadius", "2", toMain: false);

            // A newer schema is left alone with one warning.
            dir = Dir(work, "e3");
            main = Path.Combine(dir, ConfigSchema.MainFileName);
            byte[] newer = Encoding.UTF8.GetBytes("[1 - General]\n\nConfigSchemaVersion = 3\n\n[9 - Future]\n\nThing = 1\n");
            File.WriteAllBytes(main, newer);
            var warn = new List<string>();
            r = ConfigMigrator.Run(main, Path.Combine(dir, ConfigSchema.AdvancedFileName), _ => { }, warn.Add);
            Check(r.Outcome == ConfigMigrationOutcome.NewerSchema, $"v3 file: NewerSchema (got {r.Outcome})");
            Check(warn.Count == 1, "v3 file: one warning");
            Check(File.ReadAllBytes(main).SequenceEqual(newer) && Directory.GetFiles(dir).Length == 1, "v3 file: untouched, nothing else written");

            // An existing advanced file is merged into, not replaced; its unknown lines are kept.
            dir = Dir(work, "e4");
            main = Path.Combine(dir, ConfigSchema.MainFileName);
            adv = Path.Combine(dir, ConfigSchema.AdvancedFileName);
            File.WriteAllText(main, "[Transport]\n\n# Default value: 30000\nT3_TimeoutConnectedMs = 60000\n");
            File.WriteAllText(adv, "## header\n\n[ZoneSim]\n\n# Default value: 2\nPokeRingRadius = 3\nstray line kept\n");
            r = ConfigMigrator.Run(main, adv, _ => { });
            CfgDocument a = CfgDocument.Load(adv);
            Check(a.Find("ZoneSim", "PokeRingRadius")?.Value == "3" && a.Find("Transport", "T3_TimeoutConnectedMs")?.Value == "60000",
                "advanced merge keeps existing values and adds carried ones");
            Check(File.ReadAllText(adv).Contains("stray line kept"), "unknown line preserved");
        }

        // ---------------------------------------------------------------------------------------------------
        /// <summary>Checks the migrator's key tables against the Bind(...) calls in the mod's own source.</summary>
        private static void TestKeyTables(string src)
        {
            var sectionConsts = typeof(ConfigSchema).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue());

            var mainBinds = new HashSet<string>();
            var advancedBinds = new HashSet<string>();
            foreach (string f in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories))
            {
                if (f.Replace('\\', '/').Contains("/Core/Config/")) continue;
                if (Path.GetFileName(f) == "OverlayPanelConfig.cs") continue; // its own overlay .cfg
                string t = File.ReadAllText(f);
                foreach (Match m in Regex.Matches(t, @"\.Bind\(\s*ConfigSchema\.(\w+)\s*,\s*""([^""]+)"""))
                    mainBinds.Add(sectionConsts[m.Groups[1].Value] + "|" + m.Groups[2].Value);
                foreach (Match m in Regex.Matches(t, @"\.Bind\(\s*""([^""]+)""\s*,\s*""([^""]+)"""))
                    advancedBinds.Add(m.Groups[1].Value + "|" + m.Groups[2].Value);
                if (t.Contains("$\"Transport.{leverName}.Active\""))
                    foreach (Match m in Regex.Matches(t, @"BoolEntry\(\s*cfg\s*,\s*""(\w+)""\s*\)"))
                        advancedBinds.Add("Features|Transport." + m.Groups[1].Value + ".Active");
            }

            var promoted = new HashSet<string>(MigrationV1ToV2.Promoted.Select(p => p.V2Section + "|" + p.V2Key));
            var newInV2 = new HashSet<string> { ConfigSchema.General + "|LogLevel" };
            var advTable = new HashSet<string>(MigrationV1ToV2.V1AdvancedKeys.Select(k => k.Section + "|" + k.Key));

            Console.WriteLine($"  source: {mainBinds.Count} main-file binds, {advancedBinds.Count} advanced-file binds");
            Diff("promoted v2 keys missing a Main bind", promoted.Except(mainBinds));
            Diff("Main binds not in the promoted table (or known-new)", mainBinds.Except(promoted).Except(newInV2));
            Diff("advanced table keys with no bind in src", advTable.Except(advancedBinds));
            Diff("advanced binds missing from the v1 advanced table (new keys need no migration, but check)", advancedBinds.Except(advTable));
            Check(!MigrationV1ToV2.Promoted.Any(p => advTable.Contains(p.V1Section + "|" + p.V1Key)), "no v1 key is both promoted and advanced");
            Check(promoted.Count == 18, $"18 promoted keys (got {promoted.Count})");
        }

        private static void Diff(string what, IEnumerable<string> items)
        {
            var list = items.ToList();
            Check(list.Count == 0, $"{what}: {(list.Count == 0 ? "none" : string.Join(", ", list))}");
        }

        // ---------------------------------------------------------------------------------------------------
        private static void ExpectCarried(ConfigMigrationResult r, string displayName, string value, bool toMain)
        {
            CarriedSetting c = r.Carried.FirstOrDefault(x => x.DisplayName == displayName);
            Check(c != null && c.ToValue == value && c.ToMainFile == toMain,
                $"carried {displayName} = {value} -> {(toMain ? "main" : "advanced")}" + (c == null ? " (not carried)" : $" (got {c.ToValue})"));
        }

        private static void ExpectNotCarried(ConfigMigrationResult r, string displayName) =>
            Check(r.Carried.All(x => x.DisplayName != displayName), $"not carried: {displayName}");

        private static void Check(bool ok, string what, bool quiet = false)
        {
            s_checks++;
            if (!ok)
            {
                s_failures++;
                Console.WriteLine("  FAIL " + what);
            }
            else if (!quiet)
            {
                Console.WriteLine("  ok   " + what);
            }
        }

        private static void Section(string name, Action body)
        {
            Console.WriteLine();
            Console.WriteLine("== " + name);
            try { body(); }
            catch (Exception e) { Check(false, $"threw {e}"); }
        }

        private static string Dir(string work, string name)
        {
            string d = Path.Combine(work, name);
            Directory.CreateDirectory(d);
            return d;
        }

        private static string Arg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        private static string FindSrc()
        {
            for (string d = AppContext.BaseDirectory; d != null; d = Path.GetDirectoryName(d))
            {
                string candidate = Path.Combine(d, "src", "Core", "Config");
                if (Directory.Exists(candidate)) return Path.Combine(d, "src");
            }
            return null;
        }
    }
}
