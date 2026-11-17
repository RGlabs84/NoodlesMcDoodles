using NoodlesMcDoodles.Core;
using NoodlesMcDoodles.Core.ConfigFiles;

namespace NoodlesMcDoodles.Transport
{
    /// <summary>
    /// Q10 (docs/design/01 Hook 11) — the one M1 Transport lever whose benefit requires a player's OWN client
    /// to run this mod: reusing Hook 6's <c>SendZDOs</c> budget transpiler (Q3/Q4/Q5/Q12/G2-N1) and Hook 7's
    /// <c>SendZDOToPeers2</c> k-peer prefix (Q2) unmodified against the client assembly's byte-identical copy
    /// of those same methods (CLIENT:77197-77256 / 77026-77049 — confirmed identical to SERVER:77055-77118 /
    /// 76883-76909), so an opted-in remote client — or a listen-server host, which needs no opt-in since it's
    /// already authoritative — uploads its own owned ZDOs faster.
    ///
    /// This is not a new hook and owns no <c>[HarmonyPatch]</c> of its own: it is purely the shared, default-off
    /// gate every other Transport hook whose vanilla method also runs on a plain remote client reads before
    /// doing anything (Hook 6 in <c>ZdoSendBudget.cs</c>, Hook 7 in <c>PeersPerTick.cs</c>), independent of every
    /// server-side flag so a server operator turning M1 on never silently changes a connecting vanilla client's
    /// behaviour, and installing the client-side mod without ever touching this flag changes nothing either
    /// (docs/design/01, RED-TEAM FIX RT1 #3 — the Milestones section's own stated intent, "every lever also has
    /// a client-side twin gated behind its own opt-in flag (Q10)").
    ///
    /// No RPC, no handshake dependency: this is a pure local-process gate over this process's OWN send-side
    /// method bodies. DESIGN.md Lead decision 3 ("a client never sends a NoodlesMcDoodles RPC before it has
    /// received the server's Hello") does not apply here — nothing gated by Q10 sends a NoodlesMcDoodles RPC;
    /// it only changes how fast/in-what-order this process serializes ZDOs it was already going to send via
    /// vanilla's own <c>ZRoutedRpc</c>/<c>ZDOData</c> path.
    ///
    /// Cross-module consumers coordinate ONLY through the Features registry — `Features.Find("Transport.Q10")
    /// ?.Active == true` — never through a direct reference to this class, per CORE-API.md's "one flag per
    /// behaviour" contract (this avoids every Transport-owning file needing a compile-time reference to every
    /// other one). <see cref="Q10"/>/<see cref="ShouldApplyOnThisProcess"/> are exposed here as `internal` only
    /// for this file's own sibling in the same module, <c>PeersPerTick.cs</c>.
    /// </summary>
    internal static class ClientSendLoopModule
    {
        internal static FeatureFlag Q10 { get; private set; }

        [NoodlesModuleInit]
        internal static void Init()
        {
            // Default ON since 0.1.1 (user decision after the 2026-09-28 NJ-AZ live test: the far player's own vanilla upload
            // was the bottleneck for how everyone else saw them, median 205 ms between movement updates).
            // 1.1.0: main file [2 - Network] ClientUploadBoost (was [Transport] Q10_Enabled). When this process is NOT
            // authoritative (ZNet.IsServer()==false — a genuine remote client, not a listen-server host, which is
            // always authoritative and needs no opt-in), makes this mod's own send-side hooks (Q2's k-peers loop,
            // Q3/Q4/Q5/Q12/G2-N1's SendZDOs budget, T1/T2/T9) apply to THIS client's own upload too. Independent of
            // every server-side Transport flag; purely local, no RPC is sent or received because of it.
            var q10Config = NoodlesConfig.ClientUploadBoost;
            Q10 = Features.Register("Transport.Q10", q10Config);

            Telemetry.SetTag("transport.q10.active", Q10.Active ? "on" : "off");
            q10Config.SettingChanged += (_, __) =>
                Telemetry.SetTag("transport.q10.active", Q10.Active ? "on" : "off"); // thread-safe write, no ZNet/ZDOMan touch — no main-thread marshal needed
        }

        /// <summary>
        /// Shared "authoritative, or opted in" gate for a Transport hook whose vanilla method also runs on a
        /// plain remote client (Hooks 1/2's own T1/T3/T2 RED-TEAM FIX and Hook 7's Q2 gate both implement this
        /// exact rule against <c>ZNet.instance.IsServer()</c>). A null <c>ZNet.instance</c> (called before any
        /// ZNet exists) reads as "not yet authoritative," matching vanilla's own state at that point.
        /// </summary>
        internal static bool ShouldApplyOnThisProcess()
        {
            bool isAuthoritative = ZNet.instance != null && ZNet.instance.IsServer();
            return isAuthoritative || Q10.Active;
        }
    }
}
