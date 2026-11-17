using BepInEx.Configuration;
using NoodlesMcDoodles.Core;

namespace NoodlesMcDoodles.Transport
{
    /// <summary>
    /// Config, feature flags and shared constants for the M1 "unified <c>ZDOMan.SendZDOs</c> budget" hooks:
    /// Q3 (inner budget), Q5 (outer gate — DESIGN.md's Configuration reference: "Q5_OuterGateBytes | derived
    /// from Q3 ... not independently settable — always equals Q3", so this module never gives Q5 its own
    /// config entry or its own <see cref="FeatureFlag"/>; both vanilla <c>10240</c> literals in
    /// <c>ZDOMan.SendZDOs</c> read the ONE <see cref="InnerBudgetBytes"/> getter below), Q4 (congestion
    /// floor) and, via their own getters used by <see cref="SendZdosPatch"/>'s helpers, Q12 (oversized-payload
    /// guard) and G2-N1 (m_invalidSector chunking). See docs/design/01-transport-and-queues.md Hook 6 and
    /// DESIGN.md's Configuration reference, "Transport (Transport.*)" table.
    ///
    /// Q10 (client opt-in eligibility gate for all of the above, docs/design/01 Hook 11) is a sibling lever
    /// owned by <c>ClientSendLoop.cs</c>'s <c>ClientSendLoopModule</c> — <see cref="EligibleForThisProcess"/>
    /// below only READS it via <see cref="Features.Find"/>, never re-binds or re-registers it, so there is
    /// exactly one "Transport.Q10" config entry/flag in the whole mod regardless of module init order.
    ///
    /// The single <c>[NoodlesModuleInit]</c> for every M1 lever this agent owns lives here (Q3/Q4/Q5/Q12/
    /// G2-N1 directly; Q7 and Q8's own config/flags/patch registration are delegated to
    /// <see cref="DestroyChunking"/>/<see cref="ForceSendBudget"/> but called from this one Init, since
    /// NoodlesPlugin only needs ONE tagged method per concern, not one per file).
    /// </summary>
    internal static class SharedBudget
    {
        // ---- Shared vanilla-fallback constants (SERVER:77058/77062/77063, byte-identical CLIENT:77200/77204/77205) ----
        public const int VanillaInnerBudgetBytes = 10240; // both `Ldc_I4 10240` sites' vanilla value (outer gate AND inner-budget base)
        public const int VanillaCongestionFloorBytes = 2048; // `Ldc_I4 2048` site's vanilla value
        public const int HardCeilingBytes = 131072; // Configuration reference: Q3's hard clamp ceiling (also Q5's, since Q5 == Q3)
        public const int MinInnerBudgetBytes = 10240; // Configuration reference: Q3's hard clamp floor — never below vanilla's own value
        public const int AbsoluteSteamCeilingBytes = 524288; // 512 KiB — Q12's own hard cap (Data-loss analysis #1 / Hook 6 "Q12" text)

        public static ConfigEntry<int> Q3InnerBudgetBytesConfig;
        public static ConfigEntry<int> Q4CongestionFloorBytesConfig;
        public static ConfigEntry<int> Q12FramingMarginBytesConfig;
        public static ConfigEntry<int> G2N1ChunkBytesConfig;

        public static FeatureFlag Q3;
        public static FeatureFlag Q4;
        public static FeatureFlag Q12;
        public static FeatureFlag G2N1;

        [NoodlesModuleInit]
        internal static void Init()
        {
            BindConfig();
            RegisterFeatures();
            DestroyChunking.BindAndRegister();  // Q7 — own file for readability, same module Init
            ForceSendBudget.BindAndRegister();  // Q8 — own file for readability, same module Init

            PatchRegistry.Register("Transport.Q3", typeof(SendZdosPatch.Patch_ZDOMan_SendZDOs_Budget), PatchSide.Both);
            PatchRegistry.Register("Transport.Q3", typeof(PeerBudget.Patch_ZDOMan_SendZDOs_PeerBudget), PatchSide.Both);
        }

        private static void BindConfig()
        {
            Q3InnerBudgetBytesConfig = NoodlesConfig.File.Bind("Transport", "Q3_InnerBudgetBytes", 65536,
                new ConfigDescription(
                    "The most world data (bytes) one SendZDOs call may queue for a player (vanilla 10240; also Q5's " +
                    "outer gate). With T9 on (default) each connection's real budget follows the rate T9 has proven for " +
                    "it: enough to keep Steam fed until that player's next send, never below 10240, never above this. " +
                    "A player whose line only manages vanilla's rate keeps vanilla's budget, because a bigger one would " +
                    "just queue data ahead of everything else. Default 64 KiB feeds T1's 1 MiB/s at vanilla's 50 ms " +
                    "send tick. With T9 off every call uses this value. Safe range 10240-131072.",
                    new AcceptableValueRange<int>(MinInnerBudgetBytes, HardCeilingBytes)));

            Q4CongestionFloorBytesConfig = NoodlesConfig.File.Bind("Transport", "Q4_CongestionFloorBytes", 4096,
                new ConfigDescription(
                    "Congestion floor (bytes). ZDOMan.SendZDOs skips this peer for this call when the " +
                    "remaining budget (InnerBudget - GetSendQueueSize()) drops below this.",
                    new AcceptableValueRange<int>(512, 8192)));

            Q12FramingMarginBytesConfig = NoodlesConfig.File.Bind("Transport", "Q12_FramingMarginBytes", 4096,
                new ConfigDescription(
                    "Safety margin (bytes) subtracted from the 512 KiB Steam send ceiling before Q12's " +
                    "running-total guard breaks the per-item loop early.",
                    new AcceptableValueRange<int>(1024, 16384)));

            G2N1ChunkBytesConfig = NoodlesConfig.File.Bind("Transport", "G2N1_ChunkBytes", 65536,
                new ConfigDescription(
                    "Per-call byte budget (bytes) for draining ZDOPeer.m_invalidSector. A backlog larger " +
                    "than this is drained over multiple SendZDOs calls (ExceptWith'd as each chunk actually " +
                    "sends), never dropped.",
                    new AcceptableValueRange<int>(16384, 131072)));

            // Q10 itself (the "Transport.Q10_Enabled" config bool and its "Transport.Q10" FeatureFlag) is
            // owned by ClientSendLoop.cs's ClientSendLoopModule — this file only READS it (via Features.Find,
            // see EligibleForThisProcess below), never re-binds/re-registers it, so there is exactly one
            // ConfigEntry and one FeatureFlag object for Q10 in the whole mod regardless of Init() order.
        }

        private static void RegisterFeatures()
        {
            var q3Cfg = NoodlesConfig.File.Bind("Features", "Transport.Q3.Active", true,
                "Q3/Q5: raised inner-budget/outer-gate bytes in ZDOMan.SendZDOs (one shared value — see " +
                "Transport.Q3_InnerBudgetBytes). Off = both literal sites read back exactly vanilla's 10240. " +
                "This is also the feature name PatchRegistry force-offs if the shared SendZDOs transpiler " +
                "itself fails to apply/JIT — Q4/Q12/G2-N1 below are inert in that case too, since the vanilla " +
                "method is then simply left unpatched.");
            Q3 = Features.Register("Transport.Q3", q3Cfg);

            // WP5 per-feature quarantine wiring (safety spec's own explicit call-out: Q3 is one of only two
            // Transport levers, alongside T2's already-wired join-burst, that hold real cross-tick state —
            // PeerBudget.cs's per-peer Cadence bookkeeping). See PeerBudget.ReleaseAll's own remarks for why
            // this is release-for-hygiene (clean restart of rate learning), not a native-resource leak fix.
            Q3.OnForcedOff = PeerBudget.ReleaseAll;

            var q4Cfg = NoodlesConfig.File.Bind("Features", "Transport.Q4.Active", true,
                "Q4: raised congestion floor in ZDOMan.SendZDOs. Off = the floor stays at vanilla's 2048.");
            Q4 = Features.Register("Transport.Q4", q4Cfg);

            var q12Cfg = NoodlesConfig.File.Bind("Features", "Transport.Q12.Active", true,
                "Q12: running-total guard against the 512 KiB Steam send ceiling inside ZDOMan.SendZDOs " +
                "(skip oversized single items, break early on the running total — never truncate). Off = no " +
                "guard beyond vanilla's own per-item loop (identical to the original method).");
            Q12 = Features.Register("Transport.Q12", q12Cfg);

            var g2n1Cfg = NoodlesConfig.File.Bind("Features", "Transport.G2N1.Active", true,
                "G2-N1: chunk ZDOPeer.m_invalidSector's write instead of writing it unconditionally in one " +
                "call. Off = vanilla's own unconditional write-then-Clear().");
            G2N1 = Features.Register("Transport.G2N1", g2n1Cfg);
        }

        /// <summary>
        /// Both vanilla <c>10240</c> literal sites (outer gate SERVER:77058, inner-budget base SERVER:77062)
        /// read this ONE getter — Q5 has no config/flag of its own (see class remarks). Falls back to the
        /// exact vanilla literal whenever Q3 is off, or (on a non-authoritative process) when Q10 is off —
        /// matching the "flag off -&gt; numerically identical to vanilla" requirement (Hook 6 in
        /// docs/design/01, and RT1's "only Q10 touches a non-authoritative process" rule).
        /// </summary>
        public static int InnerBudgetBytes
        {
            get
            {
                if (!EligibleForThisProcess() || !Q3.Active) return VanillaInnerBudgetBytes;
                int perPeer = PeerBudget.Current; // set for the SendZDOs call in progress (PeerBudget.cs)
                return perPeer > 0 ? perPeer : VanillaInnerBudgetBytes;
            }
        }

        /// <summary>SERVER:77063's `2048` literal — same eligibility/flag-off fallback rule as <see cref="InnerBudgetBytes"/>.</summary>
        public static int CongestionFloorBytes
        {
            get
            {
                if (!EligibleForThisProcess() || !Q4.Active) return VanillaCongestionFloorBytes;
                return Q4CongestionFloorBytesConfig.Value;
            }
        }

        /// <summary>
        /// Authoritative (a genuine dedicated server, OR a listen-server host — a client-build process that is
        /// also hosting) is always eligible; a plain remote client is only eligible when Q10's own opt-in is on
        /// (RED-TEAM FIX RT1, docs/design/01: "Q3/Q4/Q12/G2-N1... gate their non-authoritative-process branch
        /// behind Q10.Active, using ZNet.IsServer()... true for dedicated server AND listen-server host" — NOT
        /// <see cref="Side.IsDedicatedServerProcess"/>, which only distinguishes the BUILD a process runs
        /// (dedicated-server vs. client binary), not whether THIS ZNet instance is acting as a server; a
        /// listen-server host is a client-build process that IS authoritative, so that heuristic alone would
        /// wrongly gate it behind Q10 too). A null <c>ZNet.instance</c> (called before any ZNet exists) reads
        /// as "not yet authoritative," matching vanilla's own state at that point — same rule
        /// <c>ClientSendLoopModule.ShouldApplyOnThisProcess</c> (Hook 11) already uses.
        ///
        /// Q10 itself is owned and registered by <c>ClientSendLoopModule</c> (<c>ClientSendLoop.cs</c>) — read
        /// here only via <see cref="Features.Find"/>, per CORE-API.md's "one flag per behaviour" contract, so
        /// this file never re-binds/re-registers the same config key or feature name (this module reads the
        /// SAME "Transport.Q10" flag; every getter/guard the shared transpiler drives applies this same test,
        /// since "Why one patch, not five" — Hook 6 — means Q3/Q4/Q12/G2-N1 can never be decoupled from each
        /// other's eligibility either).
        /// </summary>
        public static bool EligibleForThisProcess()
        {
            bool isAuthoritative = ZNet.instance != null && ZNet.instance.IsServer();
            return isAuthoritative || (Features.Find("Transport.Q10")?.Active ?? false);
        }

        public static int Clamp(int configured)
        {
            if (configured < MinInnerBudgetBytes) return MinInnerBudgetBytes;
            if (configured > HardCeilingBytes) return HardCeilingBytes;
            return configured;
        }
    }
}
