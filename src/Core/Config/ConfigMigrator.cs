using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace NoodlesMcDoodles.Core.ConfigFiles
{
    /// <summary>
    /// Upgrades an older <c>wubarrk.noodlesmcdoodles.cfg</c> to <see cref="ConfigSchema.CurrentVersion"/> before any
    /// BepInEx <c>ConfigFile</c> entry is bound (called from <c>NoodlesPlugin.Awake</c>, ahead of
    /// <c>NoodlesConfig.Bind</c>). Pure System.IO: no Unity, BepInEx or Harmony, so tools/cfg-migrate-test compiles
    /// this folder directly.
    ///
    /// Version detection: no main file = fresh install (nothing to do; binding creates v2 files). A main file
    /// without <c>[1 - General] ConfigSchemaVersion</c> = v1. A newer version than this build knows = touch
    /// nothing, one warning. Otherwise every step in <see cref="Steps"/> from the file's version up to the current
    /// one runs in order, against in-memory documents; only then is the original copied to
    /// <c>&lt;main&gt;.v&lt;N&gt;-backup-&lt;yyyyMMdd-HHmmss&gt;</c> and both files written (temp file + replace).
    ///
    /// Never throws. Logs at most one line: one info line on success, one warning on failure or a newer schema,
    /// nothing at all on a no-op boot. On failure the original main file is left byte-for-byte as it was and
    /// the mod boots on defaults.
    /// </summary>
    internal static class ConfigMigrator
    {
        /// <summary>Every migration step, oldest first. A future v2->v3 is one new class appended here.</summary>
        internal static readonly IConfigMigration[] Steps =
        {
            new MigrationV1ToV2(),
        };

        private const int MaxNamesInLogLine = 8;

        public static ConfigMigrationResult Run(string mainPath, string advancedPath, Action<string> log) =>
            Run(mainPath, advancedPath, log, log);

        public static ConfigMigrationResult Run(string mainPath, string advancedPath, Action<string> info, Action<string> warning)
        {
            var result = new ConfigMigrationResult { ToVersion = ConfigSchema.CurrentVersion };
            byte[] advancedOriginal = null;
            bool advancedExisted = false;

            try
            {
                if (!File.Exists(mainPath))
                {
                    result.Outcome = ConfigMigrationOutcome.FreshInstall;
                    return result;
                }

                CfgDocument main = CfgDocument.Load(mainPath);
                if (!main.AllEntries.Any())
                {
                    if (main.UnknownLineCount > 0)
                        throw new FormatException("the file has no 'Key = Value' settings, only unrecognised lines");
                    result.Outcome = ConfigMigrationOutcome.UpToDate; // empty or comments only: nothing to keep
                    return result;
                }

                int version = DetectVersion(main);
                result.FromVersion = version;

                if (version == ConfigSchema.CurrentVersion)
                {
                    result.Outcome = ConfigMigrationOutcome.UpToDate;
                    return result;
                }

                if (version > ConfigSchema.CurrentVersion)
                {
                    result.Outcome = ConfigMigrationOutcome.NewerSchema;
                    result.Message = $"Config file '{mainPath}' is schema v{version}, newer than this build's v" +
                        $"{ConfigSchema.CurrentVersion} (was a newer NoodlesMcDoodles installed before?). Left as it is; " +
                        "settings this build does not recognise are ignored.";
                    SafeLog(warning, result.Message);
                    return result;
                }

                advancedExisted = File.Exists(advancedPath);
                if (advancedExisted) advancedOriginal = File.ReadAllBytes(advancedPath);
                CfgDocument advanced = advancedExisted
                    ? CfgDocument.Parse(CfgDocument.DecodeUtf8(advancedOriginal))
                    : new CfgDocument { NewLine = main.NewLine };

                var ctx = new MigrationContext(main, advanced, result);
                int at = version;
                foreach (IConfigMigration step in Steps.OrderBy(s => s.From))
                {
                    if (step.From < at) continue;
                    if (step.From != at || step.To <= step.From)
                        throw new InvalidOperationException($"no migration step from schema v{at} (next step is v{step.From}->v{step.To})");
                    step.Apply(ctx);
                    at = step.To;
                    ctx.Main.Set(ConfigSchema.General, ConfigSchema.SchemaVersionKey, at.ToString(CultureInfo.InvariantCulture),
                        SchemaVersionComments(at));
                    if (at == ConfigSchema.CurrentVersion) break;
                }
                if (at != ConfigSchema.CurrentVersion)
                    throw new InvalidOperationException($"migration steps end at schema v{at}, expected v{ConfigSchema.CurrentVersion}");

                string mainText = ctx.Main.Serialize();
                string advancedText = ctx.Advanced.Serialize();

                result.BackupPath = MakeBackup(mainPath, version);

                CfgDocument.WriteAtomic(advancedPath, advancedText);
                result.AdvancedFileWritten = true;
                CfgDocument.WriteAtomic(mainPath, mainText);
                result.MainFileRewritten = true;

                result.Outcome = ConfigMigrationOutcome.Migrated;
                result.Message = SuccessLine(result);
                SafeLog(info, result.Message);
                return result;
            }
            catch (Exception e)
            {
                result.Outcome = ConfigMigrationOutcome.Failed;
                result.Error = e;

                if (result.AdvancedFileWritten && !result.MainFileRewritten)
                {
                    // Keep the two files consistent with "nothing happened": put the advanced file back.
                    try
                    {
                        if (advancedExisted) CfgDocument.WriteAtomic(advancedPath, advancedOriginal);
                        else File.Delete(advancedPath);
                        result.AdvancedFileWritten = false;
                    }
                    catch (Exception) { /* best effort */ }
                }

                // BepInEx will rewrite the main file as soon as the mod binds its settings, so keep a copy of
                // what was there for the admin (best effort; the original itself is never modified here).
                if (result.BackupPath == null && !result.MainFileRewritten)
                {
                    try { if (File.Exists(mainPath)) result.BackupPath = MakeBackup(mainPath, result.FromVersion > 0 ? result.FromVersion : 1); }
                    catch (Exception) { /* best effort */ }
                }

                result.Message = $"Config upgrade to schema v{ConfigSchema.CurrentVersion} failed ({e.GetType().Name}: {e.Message}). " +
                    $"'{Path.GetFileName(mainPath)}' was not changed by the upgrade; the mod starts on default settings" +
                    (result.BackupPath != null ? $" (a copy of your old file is at '{result.BackupPath}')." : ".");
                SafeLog(warning, result.Message);
                return result;
            }
        }

        /// <summary>v1 = no <c>[1 - General] ConfigSchemaVersion</c>. Throws <see cref="FormatException"/> on an unreadable value.</summary>
        internal static int DetectVersion(CfgDocument main)
        {
            CfgEntry e = main.Find(ConfigSchema.General, ConfigSchema.SchemaVersionKey);
            if (e == null) return 1;
            if (!int.TryParse(e.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v < 1)
                throw new FormatException($"[{ConfigSchema.General}] {ConfigSchema.SchemaVersionKey} = '{e.Value}' is not a schema version");
            return v;
        }

        internal static IEnumerable<string> SchemaVersionComments(int version) => new[]
        {
            "## Managed by the mod. Do not edit.",
            "# Setting type: Int32",
            CfgDocument.DefaultValueComment(version.ToString(CultureInfo.InvariantCulture)),
        };

        private static string MakeBackup(string mainPath, int fromVersion)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string basePath = $"{mainPath}.v{fromVersion}-backup-{stamp}";
            string path = basePath;
            for (int i = 2; File.Exists(path); i++) path = $"{basePath}-{i}";
            File.Copy(mainPath, path, false);
            return path;
        }

        private static string SuccessLine(ConfigMigrationResult r)
        {
            var sb = new StringBuilder();
            sb.Append($"Config upgraded to schema v{r.ToVersion}: ");
            int n = r.Carried.Count;
            if (n == 0)
            {
                sb.Append("no changed settings to keep");
            }
            else
            {
                IEnumerable<CarriedSetting> ordered = r.Carried.Where(c => c.ToMainFile).Concat(r.Carried.Where(c => !c.ToMainFile));
                List<string> names = ordered.Select(c => c.DisplayName).ToList();
                sb.Append($"{n} changed setting{(n == 1 ? "" : "s")} kept (");
                sb.Append(string.Join(", ", names.Take(MaxNamesInLogLine)));
                if (names.Count > MaxNamesInLogLine) sb.Append($", +{names.Count - MaxNamesInLogLine} more");
                sb.Append(')');
            }
            if (r.Dropped.Count > 0)
                sb.Append($", {r.Dropped.Count} obsolete setting{(r.Dropped.Count == 1 ? "" : "s")} dropped");
            sb.Append($", backup at {r.BackupPath}.");
            return sb.ToString();
        }

        private static void SafeLog(Action<string> log, string message)
        {
            try { log?.Invoke(message); }
            catch (Exception) { /* logging must never break boot */ }
        }
    }

    /// <summary>One schema step. <see cref="Apply"/> edits <see cref="MigrationContext.Main"/>/<see cref="MigrationContext.Advanced"/> in memory only.</summary>
    internal interface IConfigMigration
    {
        int From { get; }
        int To { get; }
        void Apply(MigrationContext ctx);
    }

    internal sealed class MigrationContext
    {
        public MigrationContext(CfgDocument main, CfgDocument advanced, ConfigMigrationResult result)
        {
            Main = main;
            Advanced = advanced;
            Result = result;
        }

        /// <summary>The main file. A step may replace it with a new document.</summary>
        public CfgDocument Main { get; set; }

        /// <summary>The advanced file (empty if it did not exist yet).</summary>
        public CfgDocument Advanced { get; set; }

        public ConfigMigrationResult Result { get; }
    }

    internal enum ConfigMigrationOutcome
    {
        /// <summary>No main file yet; binding creates both files.</summary>
        FreshInstall,
        /// <summary>Already at the current schema (or empty); nothing written, nothing logged.</summary>
        UpToDate,
        Migrated,
        /// <summary>The file is from a newer build; nothing written, one warning.</summary>
        NewerSchema,
        /// <summary>Something threw; the original main file is unchanged, one warning, mod boots on defaults.</summary>
        Failed,
    }

    internal sealed class CarriedSetting
    {
        public string FromSection;
        public string FromKey;
        public string FromValue;
        public bool ToMainFile;
        public string ToSection;
        public string ToKey;
        public string ToValue;

        /// <summary>Main-file settings by their (unique) key; advanced ones as Section.Key.</summary>
        public string DisplayName => ToMainFile ? ToKey : $"{ToSection}.{ToKey}";

        public override string ToString() =>
            $"[{FromSection}] {FromKey} = {FromValue}  ->  {(ToMainFile ? "main" : "advanced")} [{ToSection}] {ToKey} = {ToValue}";
    }

    internal sealed class ConfigMigrationResult
    {
        public ConfigMigrationOutcome Outcome;
        public int FromVersion;
        public int ToVersion;
        public readonly List<CarriedSetting> Carried = new List<CarriedSetting>();

        /// <summary>"[Section] Key" of settings no longer in this build (or with an unreadable value), not carried.</summary>
        public readonly List<string> Dropped = new List<string>();

        public string BackupPath;
        public bool MainFileRewritten;
        public bool AdvancedFileWritten;

        /// <summary>The one line that was logged, if any.</summary>
        public string Message;
        public Exception Error;
    }
}
