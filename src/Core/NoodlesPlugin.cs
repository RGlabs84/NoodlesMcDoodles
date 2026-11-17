using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using NoodlesMcDoodles.Core.ConfigFiles;

namespace NoodlesMcDoodles.Core
{
    /// <summary>
    /// The plugin entry point. One <c>BaseUnityPlugin</c>, loaded identically by BepInEx on both the
    /// dedicated-server executable and the Steam client (one DLL, per DESIGN.md Architecture) — everything
    /// this <c>Awake()</c> does must therefore be side-agnostic at the C# level; side-specific behaviour lives
    /// entirely inside individual patch classes and <see cref="PatchRegistry"/>'s side filtering, never here.
    ///
    /// Boot order, and why: <see cref="MigrateConfigFiles"/> (an old config is upgraded before anything binds),
    /// then <see cref="NoodlesConfig.Bind"/>, <see cref="Features.InitCoreFlags"/> and
    /// <see cref="KillSwitch.Init"/> are hard-sequenced (each depends on the one before) and called directly
    /// — every module's own <c>Init()</c> (discovered via <see cref="NoodlesModuleInitAttribute"/>, see that
    /// class's remarks) needs <see cref="NoodlesConfig.File"/> and <see cref="Features.Register"/> to already
    /// work, so this bootstrap must finish first. <see cref="PatchRegistry.ApplyAll"/> runs LAST, after every
    /// module (Core's own and every other one compiled into this assembly) has had a chance to
    /// <see cref="PatchRegistry.Register"/> its patch classes — this is what makes "one Harmony instance,
    /// every class patched individually, never PatchAll" (this mod's own absolute rule) actually work across
    /// several independently-developed modules without any of them needing to call `ApplyAll` themselves.
    /// </summary>
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class NoodlesPlugin : BaseUnityPlugin
    {
        private Harmony _harmony;

        private void Awake()
        {
            MainThreadDispatcher.CaptureMainThread();

            MigrateConfigFiles();
            var advancedConfig = new ConfigFile(Path.Combine(Paths.ConfigPath, ConfigSchema.AdvancedFileName), true,
                MetadataHelper.GetMetadata(this));
            NoodlesConfig.Bind(Config, advancedConfig);
            Features.InitCoreFlags();
            KillSwitch.Init();
            ZNetHelpers.ValidateOrDisableDependents();

            RunAllModuleInits();

            _harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            PatchRegistry.ApplyAll(_harmony);

            AdminCommand.Init(); // not a Harmony patch — vanilla's own Terminal.ConsoleCommand extension point.

            // Round-3 install-gate review: the separate ServerZoneMembership.ClearAll kill-switch flush is gone.
            // ZoneClaimSweep (the only writer of that set) registers ReleaseAllHeldZones, which now drops a zone only
            // after a verified hand-back and keeps partially released zones held while its bounded retry finishes;
            // a blanket clear running right after it (registration order) erased exactly that protection.

            gameObject.AddComponent<NoodlesHeartbeat>();
            ConfigWatcher.Start();

            LogStartup();
        }

        /// <summary>
        /// 1.1.0: the boot block is one short line at every log level (plus one warning naming anything forced
        /// off). The per-feature "patched" lines, the PatchRegistry summary and the old long status line are
        /// Verbose-only; below Verbose they go to detail.log.
        /// </summary>
        private static void LogStartup()
        {
            NoodlesLog.Verbose($"{PluginInfo.PLUGIN_NAME} {PluginInfo.PLUGIN_VERSION} loaded " +
                $"({(Side.IsDedicatedServerProcess ? "dedicated server" : "client")} process). {KillSwitch.StatusLine()}");

            string failed = PatchRegistry.FeaturesFailed > 0 ? $", {PatchRegistry.FeaturesFailed} FAILED" : ", 0 failed";
            NoodlesLog.Notice($"{PluginInfo.PLUGIN_NAME} {PluginInfo.PLUGIN_VERSION} loaded on " +
                $"{(Side.IsDedicatedServerProcess ? "dedicated server" : "client")}: " +
                $"{(KillSwitch.Enabled ? "enabled" : "DISABLED ([1 - General] Enabled = false)")}, " +
                $"{PatchRegistry.FeaturesPatched} features patched{failed}; log level {NoodlesLog.Level}" +
                (NoodlesLog.Level == NoodlesLogLevel.Verbose ? "." : $" (more detail in {DetailLog.DisplayPath})."));

            var forcedOff = new System.Collections.Generic.List<string>();
            foreach (var f in Features.All)
            {
                if (f.IsForcedOff) forcedOff.Add($"{f.Name} ({f.ForcedOffReason})");
            }
            if (forcedOff.Count > 0)
            {
                NoodlesLog.Warning($"forced off at startup: {string.Join(", ", forcedOff)} -- see the warnings above " +
                    "and 'noodles status'.");
            }
        }

        /// <summary>
        /// Upgrades an older config (schema v1: one file with every key) to the current main + advanced files,
        /// BEFORE anything is bound. BepInEx already read the old main file into this plugin's <c>Config</c> as
        /// orphaned entries when it constructed it; after a rewrite, <c>Config.Reload()</c> makes it re-read the
        /// migrated file so the v1 orphans are not written back on the next save. Never throws: a failed upgrade
        /// leaves the old file as it was, logs one warning and the mod boots on defaults.
        /// </summary>
        private void MigrateConfigFiles()
        {
            try
            {
                string advancedPath = Path.Combine(Paths.ConfigPath, ConfigSchema.AdvancedFileName);
                ConfigMigrationResult result = ConfigMigrator.Run(Config.ConfigFilePath, advancedPath,
                    NoodlesLog.Info, NoodlesLog.Warning);
                if (result.MainFileRewritten) Config.Reload();
            }
            catch (Exception e)
            {
                NoodlesLog.Warning($"Config upgrade check failed ({e.GetType().Name}: {e.Message}); continuing with the files as they are.");
            }
        }

        /// <summary>
        /// Finds every <see cref="NoodlesModuleInitAttribute"/>-tagged static method in this assembly (Core's
        /// own, and every other module's, compiled together into one DLL per this project's "one DLL" rule)
        /// and calls each one, ascending by order (ties broken by declaring type name for a stable,
        /// reproducible boot order), each in its own try/catch so one module's broken Init() can't stop any
        /// other module — or the plugin itself — from loading. A module that fails here has registered
        /// nothing with <see cref="PatchRegistry"/>, so it simply patches nothing and stays inert, exactly
        /// like a module that was never compiled in at all.
        /// </summary>
        private static void RunAllModuleInits()
        {
            var inits = Assembly.GetExecutingAssembly()
                .GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .Select(m => new { Method = m, Attr = m.GetCustomAttribute<NoodlesModuleInitAttribute>() })
                .Where(x => x.Attr != null)
                .OrderBy(x => x.Attr.Order)
                .ThenBy(x => x.Method.DeclaringType?.FullName, StringComparer.Ordinal)
                .ToList();

            foreach (var entry in inits)
            {
                try
                {
                    entry.Method.Invoke(null, null);
                }
                catch (Exception e)
                {
                    NoodlesLog.Error($"module Init() '{entry.Method.DeclaringType?.FullName}.{entry.Method.Name}' " +
                        $"threw — that module registers nothing and stays inert; every other module is unaffected: {e}");
                }
            }
        }
    }
}
