using HarmonyLib;
using UnityEngine;

namespace NoodlesMcDoodles.Core
{
    /// <summary>
    /// P1 (autosave interval clamp) and P2 (backup count/short/long clamp) — doc 02 Hooks 7 &amp; 8. Both
    /// server-only: the fields patched (<c>Game.m_saveInterval</c>, <c>ZNet.m_backupCount/m_backupShort/
    /// m_backupLong</c>) are only ever read by the server's own save/backup countdown logic.
    /// </summary>
    internal static class SavePolicy
    {
        /// <summary>
        /// <c>Game.Awake()</c> — SERVER:99902/CLIENT:100491, confirmed identical, runs once per world load,
        /// strictly before the per-frame save countdown in the same class. No engine-side floor protects a
        /// direct field write (<c>Mathf.Max(5,...)</c> lives only in <c>FejdStartup</c>'s CLI <c>-saveinterval</c>
        /// parse branch, which this write bypasses entirely) — this postfix's own <c>Mathf.Max(30f,...)</c> IS
        /// the floor.
        /// </summary>
        [HarmonyPatch(typeof(Game), nameof(Game.Awake))]
        internal static class Patch_Game_Awake_SaveInterval
        {
            static void Postfix()
            {
                // LEAD FIX 2026-09-28: this used to check ZNet.instance, which does not exist yet during
                // Game.Awake on the dedicated server, so P1 never applied. The side check now lives in
                // ApplySaveInterval itself.
                ApplySaveInterval();
            }
        }

        internal static void ApplySaveInterval()
        {
            if (!Features.SaveIntervalClamp.Active) return;
            // Authoritative process only: on a plain (non-hosting) client the same static field paces the
            // character-profile autosave (Game.UpdateSaving), which this mod must never change. Also covers the
            // SettingChanged path. See IsAuthoritativeProcess()'s own remarks for why this isn't a bare
            // ZNet.instance.IsServer() check.
            if (!IsAuthoritativeProcess()) return;
            // The operator's own -saveinterval wins (LEAD FIX 2026-09-28): overwriting it could lengthen the
            // interval (e.g. -saveinterval 600 -> 1800) and so widen the crash-loss window this lever exists to
            // shrink. The config only fills in when the command line did not set it.
            if (CommandLineSets("-saveinterval")) { LogKept("-saveinterval", "Save.AutosaveIntervalSeconds"); return; }
            Game.m_saveInterval = Mathf.Max(30f, NoodlesConfig.SaveAutosaveIntervalSeconds.Value);
        }

        /// <summary>
        /// <c>FejdStartup.Awake()</c> — SERVER:96892/CLIENT:97435. Confirmed this pass:
        /// <c>ParseServerArguments()</c> (called from inside this same <c>Awake</c>) unconditionally sets
        /// <c>ZNet.m_backupCount = 4;</c> as its very first statement, before parsing any <c>-backups</c> CLI
        /// flag (SERVER:97264) — so a naive write at plugin Awake would be silently clobbered; this postfix
        /// runs after <c>Awake</c>'s body (and therefore after <c>ParseServerArguments</c>) completes.
        /// </summary>
        [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.Awake))]
        internal static class Patch_FejdStartup_Awake_Backups
        {
            static void Postfix()
            {
                // FejdStartup exists on both builds, but ParseServerArguments (and therefore the clobber this
                // guards against) only runs on the dedicated-server executable's own startup flow.
                if (!Side.IsDedicatedServerProcess) return;
                ApplyBackupPolicy();
            }
        }

        internal static void ApplyBackupPolicy()
        {
            if (!Features.BackupPolicy.Active) return;
            // Authoritative process only: m_backupCount also rotates character-profile backups on a plain client.
            if (!IsAuthoritativeProcess()) return;
            // Same rule as ApplySaveInterval: each -backups/-backupshort/-backuplong the operator passed wins, so
            // the mod can never quietly cut a server's backups (e.g. -backups 10 -> 4).
            if (CommandLineSets("-backups")) LogKept("-backups", "Save.BackupCount");
            else ZNet.m_backupCount = ClampBackupCount(NoodlesConfig.SaveBackupCount.Value);
            if (CommandLineSets("-backupshort")) LogKept("-backupshort", "Save.BackupShortSeconds");
            else ZNet.m_backupShort = Mathf.Max(5, NoodlesConfig.SaveBackupShortSeconds.Value);
            if (CommandLineSets("-backuplong")) LogKept("-backuplong", "Save.BackupLongSeconds");
            else ZNet.m_backupLong = Mathf.Max(5, NoodlesConfig.SaveBackupLongSeconds.Value);
        }

        /// <summary>
        /// True on the dedicated server (known from the build-type heuristic alone — deliberately, so this
        /// stays correct even called from <c>Game.Awake</c>/<c>FejdStartup.Awake</c>, where <c>ZNet.instance</c>
        /// is confirmed null on the dedicated server: see this file's own LEAD FIX comments above) OR on a
        /// listen-server host once <c>ZNet.instance</c> exists and confirms it. RUNTIME REVIEW FIX (major,
        /// mixed-and-host, 2026-09-28): replaces the old <c>Side.IsDedicatedServerProcess</c>-only gate, which
        /// wrongly excluded a listen-server host even though the SAME <c>Game.m_saveInterval</c>/
        /// <c>ZNet.m_backupCount</c>/<c>m_backupShort</c>/<c>m_backupLong</c> fields govern that host's own
        /// world save/backup (decompile-confirmed: <c>Game.UpdateSaving</c> -&gt; <c>ZNet.Save</c> -&gt;
        /// <c>m_isServer</c> branch -&gt; <c>ConsiderAutoBackup</c>), not just a genuine remote client's harmless
        /// character-profile pacing. See <see cref="Patch_ZNet_Awake_ReapplyOnListenHost"/> below for the ONE
        /// extra call site this needed (a listen host can't know it's authoritative any earlier than
        /// <c>ZNet.Awake</c> — Side.cs remarks — so it must reapply there, not just at Game/FejdStartup Awake).
        /// </summary>
        private static bool IsAuthoritativeProcess() =>
            Side.IsDedicatedServerProcess || (ZNet.instance != null && ZNet.instance.IsServer());

        /// <summary>
        /// The listen-server-host half of the fix above: <c>Game.Awake</c>/<c>FejdStartup.Awake</c> both run
        /// before <c>ZNet.instance</c> exists (this file's own LEAD FIX comments), so a listen host's own
        /// <see cref="IsAuthoritativeProcess"/> reads false at those call sites even once it goes on to host —
        /// it just hasn't started hosting YET. <c>ZNet.Awake</c> is the earliest point that's actually knowable.
        /// Reapplying on the dedicated server too (where it already applied earlier, correctly, at Game/
        /// FejdStartup Awake) is harmless — both writes are idempotent, same value — so this doesn't special-case
        /// it out.
        /// </summary>
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Awake))]
        internal static class Patch_ZNet_Awake_ReapplyOnListenHost
        {
            static void Postfix(ZNet __instance)
            {
                if (__instance == null || !__instance.IsServer()) return;
                try { ApplySaveInterval(); }
                catch (System.Exception e) { NoodlesLog.PatchException(nameof(Patch_ZNet_Awake_ReapplyOnListenHost) + ".SaveInterval", e); }
                try { ApplyBackupPolicy(); }
                catch (System.Exception e) { NoodlesLog.PatchException(nameof(Patch_ZNet_Awake_ReapplyOnListenHost) + ".BackupPolicy", e); }
            }
        }

        /// <summary>True when the process command line sets <paramref name="flag"/> the way vanilla's
        /// <c>FejdStartup.ParseServerArguments</c> accepts it (SERVER:97340-97355: lower-cased flag followed by an
        /// integer).</summary>
        internal static bool CommandLineSets(string flag)
        {
            string[] args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i].ToLowerInvariant() == flag && int.TryParse(args[i + 1], out _)) return true;
            return false;
        }

        private static readonly System.Collections.Generic.HashSet<string> s_keptLogged = new System.Collections.Generic.HashSet<string>();

        private static void LogKept(string flag, string configKey)
        {
            if (s_keptLogged.Add(flag))
                NoodlesLog.Verbose($"Save: keeping the server's own {flag} from the command line; {configKey} applies only " +
                    "when the command line does not set it.");
        }

        /// <summary>
        /// RED-TEAM FIX carried over verbatim (doc 02 Hook 8): <c>ConsiderAutoBackup</c>
        /// (SERVER:80761-80774, byte-identical CLIENT:80955-80968) treats <c>m_backupCount==1</c> as its own
        /// sentinel for "no backups at all" (<c>int num2 = (m_backupCount != 1) ? m_backupCount : 0;</c>), not
        /// "keep one backup" — a naive 1-10 clamp would silently disable EVERY backup (both world,
        /// SERVER:80647, and character-profile, SERVER:105793, since both share this same field) for as long
        /// as it was set, with nothing distinguishing it in the logs from a healthy 1-slot rotation. The
        /// accepted range therefore excludes 1; a legacy/explicit 1 is upgraded to 2 with a loud warning
        /// rather than passed through.
        /// </summary>
        internal static int ClampBackupCount(int configured)
        {
            int clamped = Mathf.Clamp(configured, 2, 10);
            if (configured == 1)
            {
                NoodlesLog.Warning("Save.BackupCount=1 is vanilla's own sentinel for \"no backups at all\" " +
                    "(ZNet.ConsiderAutoBackup treats m_backupCount==1 as 0) — upgrading to 2 so backups actually " +
                    "happen. Set 2 explicitly to silence this warning.");
            }
            return clamped;
        }

        [NoodlesModuleInit(10)]
        internal static void Init()
        {
            PatchRegistry.Register("SaveIntervalClamp", typeof(Patch_Game_Awake_SaveInterval), PatchSide.ServerOnly);
            PatchRegistry.Register("BackupPolicy", typeof(Patch_FejdStartup_Awake_Backups), PatchSide.ServerOnly);
            // Both: the ONLY way a listen-server host (a client-build process) ever gets a chance to apply
            // either lever — see Patch_ZNet_Awake_ReapplyOnListenHost's own remarks. No matching Features flag
            // (it re-checks SaveIntervalClamp/BackupPolicy's own flags internally) — same "no flag needed"
            // pattern PatchRegistry's own doc uses for Telemetry.* (docs/impl/CORE-API.md).
            PatchRegistry.Register("SavePolicy.ListenHostReapply", typeof(Patch_ZNet_Awake_ReapplyOnListenHost), PatchSide.Both);

            // Live config-edit application (doc 02: "a live edit to the .cfg file takes effect without
            // waiting for another world load"). Only meaningful once Game/ZNet actually exist this session —
            // ApplySaveInterval/ApplyBackupPolicy are no-ops via their own Features.*.Active check either way
            // if called before a world is loaded, since Game.m_saveInterval/ZNet.m_backupCount are static
            // fields safe to write at any time regardless of whether a world is currently loaded.
            NoodlesConfig.SaveAutosaveIntervalSeconds.SettingChanged += (_, __) => ApplySaveInterval();
            NoodlesConfig.SaveBackupCount.SettingChanged += (_, __) => ApplyBackupPolicy();
            NoodlesConfig.SaveBackupShortSeconds.SettingChanged += (_, __) => ApplyBackupPolicy();
            NoodlesConfig.SaveBackupLongSeconds.SettingChanged += (_, __) => ApplyBackupPolicy();
        }
    }
}
