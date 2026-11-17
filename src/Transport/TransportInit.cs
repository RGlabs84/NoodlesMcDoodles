using BepInEx.Configuration;
using NoodlesMcDoodles.Core;
using NoodlesMcDoodles.Core.ConfigFiles;

namespace NoodlesMcDoodles.Transport
{
    /// <summary>
    /// Config binding, feature-flag registration and patch registration for the T1/T2/T3/T4/T5/T8 levers
    /// (docs/design/01-transport-and-queues.md, Hooks 1-5; DESIGN.md Hook table "Transport" rows for those six
    /// levers). The rest of the same DESIGN.md "Transport" module (Q2-Q8, Q10, Q12, G2-N1) is owned by other
    /// files/agents working in this same src/Transport/ directory at the same time — each registers itself
    /// independently through its own <see cref="NoodlesModuleInitAttribute"/> method, per
    /// docs/impl/CORE-API.md ("multiple [NoodlesModuleInit] methods across files is the normal, expected
    /// shape") — so this file never references, binds, or registers anything belonging to those levers, with
    /// one deliberate, safe exception: <see cref="Q4_CongestionFloorBytes"/> below (see its own remarks).
    /// </summary>
    internal static class TransportLeverConfig
    {
        // --- Transport.* config values (DESIGN.md "Configuration reference" / docs/design/01 "Configuration") ---

        /// <summary>T1's value as the admin sees it: main file [2 - Network] MaxSendRatePerPlayerKiB (KiB/s; v1's T1_GlobalRateBps).</summary>
        public static ConfigEntry<int> MaxSendRatePerPlayerKiB;

        /// <summary>T1 in bytes/s (= <see cref="MaxSendRatePerPlayerKiB"/> x 1024), what every consumer uses.</summary>
        public static int T1GlobalRateBps => MaxSendRatePerPlayerKiB.Value * ConfigSchema.BytesPerKiB;

        public static ConfigEntry<int> T2_JoinBurstBps;
        public static ConfigEntry<int> T2_MaxBurstSeconds;
        public static ConfigEntry<int> T3_TimeoutConnectedMs;
        public static ConfigEntry<float> T4_PingIntervalSec;
        public static ConfigEntry<float> T4_TimeoutSec;
        public static ConfigEntry<int> T5_SendBufferBytes;
        public static ConfigEntry<bool> T8_Enabled;

        /// <summary>
        /// Q4 (Hook 6, Transport module, a sibling file/agent's lever) is not ours, but docs/design/01 Hook 2's
        /// own reconciled lifecycle ties T2's drain-revert directly to it ("reverted ... the moment the
        /// queue drains below that floor, or after a 30s hard cap, whichever comes first"). Rather than
        /// hardcode a second, driftable copy of the same knob, this binds the EXACT SAME section/key/type/
        /// default Q4's own file documents (DESIGN.md Configuration reference: `Transport.Q4_CongestionFloorBytes`,
        /// default 4096, range 512-8192) — BepInEx's <c>ConfigFile.Bind</c> is idempotent per (section,key):
        /// whichever module's Init() runs first genuinely creates the entry, the other just receives the same
        /// live <see cref="ConfigEntry{T}"/> object back, so both always read one shared, admin-editable value.
        /// No feature-flag coupling: this is a config VALUE read, not a call into Q4's patch/behavior.
        /// </summary>
        public static ConfigEntry<int> Q4_CongestionFloorBytes;

        // --- Feature flags (Features.Register — one per behaviour, per docs/impl/CORE-API.md) ---
        public static FeatureFlag T1;
        public static FeatureFlag T2;
        public static FeatureFlag T3;
        public static FeatureFlag T4;
        public static FeatureFlag T5;
        public static FeatureFlag T8;

        /// <summary>
        /// T1/T2's client-side opt-in gate (docs/design/01 Hooks 1/2 RED-TEAM FIX RT1). Q10 (Hook 11) is a
        /// sibling lever, not ours — looked up defensively (never assumed registered) so T1/T2 simply fail
        /// closed to "server/listen-host only" (the always-safe default) if Q10 isn't registered in this build,
        /// rather than throwing.
        /// </summary>
        public static bool Q10Active => Features.Find("Transport.Q10")?.Active ?? false;

        /// <summary>T9 (AdaptiveRate.cs) manages per-connection rates while active; T1 is then its ceiling and T2 its join ceiling.</summary>
        public static bool T9Active => AdaptiveRate.T9 != null && AdaptiveRate.T9.Active;

        [NoodlesModuleInit]
        internal static void Init()
        {
            ConfigFile cfg = NoodlesConfig.File;

            // USER DECISION (2026-09-28 ~20:30): default 256 KiB/s per player, sized for ~40 players across ALL the
            // game worlds sharing TheWall's 300 Mbit/s upload (40 x 256 KiB/s = 10 MiB/s, ~84 Mbit/s, ~28% of the
            // line). Was 1 MiB/s. 256 rather than 192: players already hit ~165 KB/s on vanilla's queue, so 192 KiB/s
            // would leave little headroom for a battle.
            // 1.1.0: main file, KiB/s (was [Transport] T1_GlobalRateBps = 262144 bytes/s, range 153600..104857600).
            MaxSendRatePerPlayerKiB = NoodlesConfig.MaxSendRatePerPlayerKiB;

            T2_JoinBurstBps = cfg.Bind("Transport", "T2_JoinBurstBps", 2097152,
                new ConfigDescription(
                    "T2: the higher ceiling for a connection's first T2_MaxBurstSeconds after joining, when the most " +
                    "data flows. With T9 on (default) the rate rises toward it only while the player's line keeps up; " +
                    "with T9 off the connection is pinned at it until its queue drains. Clamped to [T1, 8x T1]. " +
                    "Default 2 MiB/s: a 10 MB join takes ~9.5 s (vanilla ~67 s); 4 MiB/s saves under a second more but " +
                    "pushes twice as hard on the server's upload (tools/t9-sim --knee).",
                    new AcceptableValueRange<int>(153600, 838860800)));
            T2_MaxBurstSeconds = cfg.Bind("Transport", "T2_MaxBurstSeconds", 30,
                new ConfigDescription("T2: hard cap (seconds) on how long a join burst can stay elevated before " +
                    "force-reverting, even if the queue never drains below Q4's floor.",
                    new AcceptableValueRange<int>(5, 120)));

            // RED-TEAM FIX (fixer pass, major): defaulted at 60000 (2x vanilla's own 30000 — SteamRateController.
            // VanillaTimeoutConnectedMs) even though T3 applies to the client's OWN connection by default in
            // client-only mode (Q10 defaults on). That made this mod's own out-of-the-box failure detection
            // take 2x as long as vanilla to notice a dead/stalled link, against this review's own "detect
            // failures no later than vanilla" floor. Defaulting at vanilla's own value rather than 2x it; an
            // admin who wants more patience before disconnecting a laggy-but-alive player can still raise it.
            T3_TimeoutConnectedMs = cfg.Bind("Transport", "T3_TimeoutConnectedMs", 30000,
                new ConfigDescription("T3: Steam TimeoutConnected, applied alongside T1 at ZNet.Start (docs/design/01 Hook 1). " +
                    "Default matches vanilla's own 30000; raise it for more patience before a laggy-but-alive player disconnects.",
                    new AcceptableValueRange<int>(30000, 120000)));

            T4_PingIntervalSec = cfg.Bind("Transport", "T4_PingIntervalSec", 1.0f,
                new ConfigDescription("T4: ZRpc.m_pingInterval, reasserted (docs/design/01 Hook 3) whenever " +
                    "vanilla's own call sites (ZNet.Start, PlayFab OnNetworkJoined/Accept) clobber this " +
                    "process-wide static field. Vanilla's own value is 1.0.",
                    new AcceptableValueRange<float>(0.5f, 2.0f)));
            // RED-TEAM FIX (fixer pass, major): same 2x-vanilla default gap as T3 above (60.0 vs. vanilla's own
            // 30.0 — RpcTimeoutController.VanillaPingIntervalSec's sibling constant), applying to the client's
            // own connection by default in client-only mode. Defaulted at vanilla's own value instead.
            T4_TimeoutSec = cfg.Bind("Transport", "T4_TimeoutSec", 30.0f,
                new ConfigDescription("T4: ZRpc.m_timeout, reasserted the same way. Vanilla clobbers this to 30s " +
                    "at ZNet.Start and to 90s on every PlayFab join/accept, not just the first. Default matches " +
                    "vanilla's own 30s; raise it for more patience before a laggy-but-alive player disconnects.",
                    new AcceptableValueRange<float>(30.0f, 90.0f)));

            T5_SendBufferBytes = cfg.Bind("Transport", "T5_SendBufferBytes", 131072,
                new ConfigDescription("T5: ZSocket2 (raw-TCP) SendBufferSize, applied at ConfigureSocket " +
                    "(docs/design/01 Hook 4). Hard-clamped to 262144 in code regardless of this value. " +
                    "Vanilla's own value is 2048.",
                    new AcceptableValueRange<int>(2048, 262144)));

            T8_Enabled = cfg.Bind("Transport", "T8_Enabled", true,
                "T8: server-side diagnostics stub fixes — ZNet.GetNetStats (a dead 5-out-param zeroed stub on " +
                "the server build only; the client build already has a real implementation) and " +
                "ZSteamSocket.GetConnectionQuality (server build calls the wrong Steam API class, inconsistent " +
                "with every sibling method in the same class). docs/design/01 Hook 5.");

            Q4_CongestionFloorBytes = cfg.Bind("Transport", "Q4_CongestionFloorBytes", 4096,
                new ConfigDescription("Q4: congestion floor bytes — owned by the Q4/Hook 6 lever; also read " +
                    "here by T2's join-burst drain-revert check (see this class's own remarks on the field).",
                    new AcceptableValueRange<int>(512, 8192)));

            ConfigEntry<bool> t1ActiveCfg = BoolEntry(cfg, "T1");
            ConfigEntry<bool> t2ActiveCfg = BoolEntry(cfg, "T2");
            ConfigEntry<bool> t3ActiveCfg = BoolEntry(cfg, "T3");
            ConfigEntry<bool> t4ActiveCfg = BoolEntry(cfg, "T4");

            T1 = Features.Register("Transport.T1", t1ActiveCfg);
            T2 = Features.Register("Transport.T2", t2ActiveCfg);
            T3 = Features.Register("Transport.T3", t3ActiveCfg);
            T4 = Features.Register("Transport.T4", t4ActiveCfg);
            T5 = Features.Register("Transport.T5", BoolEntry(cfg, "T5"));
            // T8's own "T8_Enabled" config bool doubles as its feature flag — DESIGN.md's own config table
            // names this one specially (unlike T1-T5, which have no separate "_Enabled" key), since it has no
            // other tunable value to sit alongside; a second "Features/Transport.T8.Active" entry would just
            // be a confusing duplicate of the same on/off switch.
            T8 = Features.Register("Transport.T8", T8_Enabled);

            PatchRegistry.Register("Transport.T1", typeof(Patch_ZNet_Start_SteamRateAndTimeout), PatchSide.Both);
            PatchRegistry.Register("Transport.T2", typeof(Patch_ZNet_RPCPeerInfo_JoinBurst), PatchSide.Both);
            PatchRegistry.Register("Transport.T2", typeof(Patch_ZNet_Update_JoinBurstRevertTick), PatchSide.Both);
            PatchRegistry.Register("Transport.T4", typeof(Patch_ZNet_Start_RpcTimeoutInit), PatchSide.Both);
            PatchRegistry.Register("Transport.T4", typeof(Patch_ZPlayFabSocket_OnNetworkJoined_ReassertTimeout), PatchSide.Both);
            PatchRegistry.Register("Transport.T4", typeof(Patch_ZPlayFabSocket_Accept_ReassertTimeout), PatchSide.Both);
            PatchRegistry.Register("Transport.T5", typeof(Patch_ZSocket2_ConfigureSocket_BufferSize), PatchSide.Both);
            PatchRegistry.Register("Transport.T8", typeof(Patch_ZNet_GetNetStats_ServerTelemetry), PatchSide.ServerOnly);
            PatchRegistry.Register("Transport.T8", typeof(Patch_ZSteamSocket_GetConnectionQuality_Fix), PatchSide.ServerOnly);

            // RED-TEAM FIX (DATA-LOSS REVIEW, blocker + major): T1/T2/T3/T4 each apply native/global state
            // (Steam per-connection or global SendRateMin/Max/TimeoutConnected; ZRpc's process-wide static ping/
            // timeout fields) OUTSIDE this mod's own C# fields — state that is never actively reverted just by a
            // flag or the kill switch flipping off (every other M1 hook re-reads its own flag live on every
            // call and self-heals automatically; these four write once and otherwise never look at the flag
            // again). Two mechanisms, matching KillSwitch's own documented flush-callback contract:
            //  1. A KillSwitch flush callback per lever-group, so `noodles disable` and an AutoStandDown trip
            //     (which flip Core.Enabled, not any Transport.* config value) revert immediately.
            //  2. Each lever's own SettingChanged handler (mirroring Q2YieldGuard's established pattern above),
            //     so flipping JUST that lever's own .Active config also reverts/re-applies live, with no
            //     restart, in either direction.
            KillSwitch.RegisterFlushCallback("Transport.T1T3", SteamRateController.Resync);
            KillSwitch.RegisterFlushCallback("Transport.T2", JoinBurstController.RevertAll);
            KillSwitch.RegisterFlushCallback("Transport.T4", RpcTimeoutController.RevertToVanilla);

            t1ActiveCfg.SettingChanged += (_, __) => MainThreadDispatcher.Enqueue(SteamRateController.Resync);
            t3ActiveCfg.SettingChanged += (_, __) => MainThreadDispatcher.Enqueue(SteamRateController.Resync);
            t2ActiveCfg.SettingChanged += (_, __) => MainThreadDispatcher.Enqueue(() =>
            {
                if (!T2.Active) JoinBurstController.RevertAll(); // turning T2 back on needs no push — TryApply just resumes gating new joins
            });
            t4ActiveCfg.SettingChanged += (_, __) => MainThreadDispatcher.Enqueue(() =>
            {
                if (T4.Active) RpcTimeoutController.Reassert();
                else RpcTimeoutController.RevertToVanilla();
            });

            // RED-TEAM FIX (fixer pass, major): the two mechanisms above (KillSwitch's global flush, and each
            // lever's own SettingChanged) revert T1/T2/T3/T4's native/global state when Core.Enabled flips or
            // when an admin edits THIS lever's own config bool live. Neither path is reached by a PER-FEATURE
            // ForceOff (PatchRegistry.cs: patch apply/JIT failure; FeatureStandDown.cs: 3 exceptions/30s from
            // that feature's own patch) — that only flips FeatureFlag._forcedOff, which .Active already reads
            // correctly, but the already-applied global Steam SendRateMin/Max/TimeoutConnected or ZRpc
            // ping/timeout writes are never told to revert. Same WP1 per-feature quarantine pattern already
            // used by Ownership's O9/O10/ZoneClaimSweep (FeatureFlag.OnForcedOff remarks) — wired for all four
            // levers, not just the three the fix note named, since T2's own gap is the same shape and
            // JoinBurstController.RevertAll already exists as its release path (used by its own KillSwitch
            // flush/SettingChanged-off above).
            T1.OnForcedOff = SteamRateController.Resync;
            T2.OnForcedOff = JoinBurstController.RevertAll;
            T3.OnForcedOff = SteamRateController.Resync;
            T4.OnForcedOff = RpcTimeoutController.RevertToVanilla;
        }

        /// <summary>Per-lever "Features/Transport.&lt;Name&gt;.Active" bool — docs/impl/CORE-API.md's default convention (T8 is the one documented exception; see above).</summary>
        private static ConfigEntry<bool> BoolEntry(ConfigFile cfg, string leverName) =>
            cfg.Bind("Features", $"Transport.{leverName}.Active", true,
                $"Master on/off for lever {leverName} (docs/design/01). Off = exactly vanilla for this lever's hook(s).");
    }
}
