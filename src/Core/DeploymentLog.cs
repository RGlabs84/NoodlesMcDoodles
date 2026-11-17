using System;
using System.Collections.Generic;
using HarmonyLib;

namespace NoodlesMcDoodles.Core
{
    /// <summary>
    /// The "which deployment mode was detected, which features were applied" logging the live-server test plan
    /// needs (NEW USER REQUIREMENT, 2026-09-28): the mod must work — and say what it's doing — whether it's
    /// running server-only, server+client, client-only (a modded player on a vanilla/not-yet-modded server), a
    /// mixed server (some modded peers, some vanilla, at once — the planned Proxima test), or a listen-server
    /// host, so a live test can happen without every player having the mod installed.
    ///
    /// Every line here is pure observability: nothing in this file changes any gameplay behaviour. It only
    /// reads state that already exists and already governs the real behaviour elsewhere (<see cref="PeerState"/>,
    /// <see cref="Features"/>/<see cref="FeatureFlag.Active"/>) and turns it into one short, prefixed
    /// (<see cref="NoodlesLog"/>) line per transition — never passwords, Steam session tickets, or IPs; a peer is
    /// named by its <c>ZNetPeer.m_playerName</c> (public, in-game display name, already broadcast to every other
    /// player) and vanilla's own <c>m_uid</c>, nothing socket-level.
    ///
    /// Cross-module feature lookups go through <see cref="Features.Find(string)"/> — the same
    /// no-compile-time-reference convention <c>Transport.Q10</c>/<c>ClientSmoothing.ShortExtrapolation</c> etc.
    /// already use for another module to read a flag without a direct reference to its owning module (Core has
    /// no compile-time reference to Transport/Ownership/Compression/Smoothing, and never should — see
    /// `docs/impl/CORE-API.md`).
    /// </summary>
    internal static class DeploymentLog
    {
        private static string s_lastLoggedKind;

        // ---- Startup: process kind + eligible feature groups ------------------------------------------------
        //
        // From a postfix on ZNet.Awake — NOT plugin Awake. A dedicated server is knowable at plugin Awake
        // (Side.IsDedicatedServerProcess, no ZNet needed — see Side.cs), but a listen-server host is a
        // CLIENT-build process that only becomes authoritative once a world is actually hosted, which is a menu
        // action that happens long after plugin Awake; ZNet.Awake (SERVER:79204/CLIENT:79346, `m_instance =
        // this;` the very first statement) is the earliest point ZNet.instance.IsServer() can tell a listen host
        // apart from a plain remote client. This is also the documented hook point for Side.ConfirmAgainstZNet
        // (Side.cs remarks) — wired here too (RUNTIME REVIEW FIX, minor, 2026-09-28: that cross-check existed
        // but had no call site anywhere in the codebase).
        //
        // L4 FIX (deployment-modes review, 2026-09-28): this used to log/tag exactly once per process
        // (s_startupLogged). ZNet.Awake fires again whenever this process's own ZNet is torn down and rebuilt —
        // a listen-host player who returns to the main menu and starts a DIFFERENT session (e.g. joins someone
        // else's world as a guest after hosting their own, or vice versa) — so a stale kind/tag could silently
        // survive that transition. Recomputed on every call now; the tag is always refreshed, but a new log
        // line only fires when the kind actually changed (or the first time), so an unmodified session still
        // gets exactly the one "detected" line it always did.
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Awake))]
        internal static class Patch_ZNet_Awake_ModeDetect
        {
            static void Postfix(ZNet __instance)
            {
                try
                {
                    Side.ConfirmAgainstZNet(__instance.IsDedicated());
                    LogModeIfChanged(__instance);
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_ZNet_Awake_ModeDetect), e);
                }
            }
        }

        private static void LogModeIfChanged(ZNet znet)
        {
            bool authoritative = znet != null && znet.IsServer();
            string kind = Side.IsDedicatedServerProcess ? "dedicated-server"
                : authoritative ? "listen-server-host"
                : "client";

            // Always updated, even when unchanged — L4's own "always update the tag" -- cheap, and a periodic
            // telemetry flush reading a momentarily-stale tag right after a real transition is worse than this.
            Telemetry.SetTag("process_kind", kind);

            if (kind == s_lastLoggedKind) return; // no real transition -- nothing new worth a log line
            bool firstTime = s_lastLoggedKind == null;
            s_lastLoggedKind = kind;

            string groups = (Side.IsDedicatedServerProcess || authoritative)
                ? "server-authoritative (ConfigSync push, ZdoCompression, OwnershipHandover grant, SavePolicy, ZoneSim) + transport pacing, per connected peer"
                : "client-local (ClientSmoothing, Q10 upload pacing) + whatever the server negotiates (ConfigSync, ZdoCompression, OwnershipHandover)";

            // L8: the kill-switch state belongs on this line too -- an operator reading the very first lines of
            // a boot log should be able to tell whether anything below it is even live.
            string killSwitch = KillSwitch.Enabled ? "on" : "off";

            // Held-band redesign synthesis, 2026-09-28 ("state held zones are on and at which ring" -- extends
            // this existing startup line rather than adding a competing one): "on" only once every layer that
            // actually has to agree is true -- this process is authoritative (a listen host counts, a plain
            // client never does, matching every other server-authoritative gate in this file), ServerZoneSim is
            // simulating real terrain/objects, OwnershipAuthority is configured on, AND it's actually ARMED
            // (SafeToggle-verified, not merely .Active -- same ArmedStateGate seam "ownership handover" above
            // already uses, never .Active alone per CORE-API.md "Live-authority toggles"). A plain client whose
            // own copy of both flags defaults true now always reads "off" here, since `authoritative` is false
            // for it -- the runtime-authority rule this whole deployment-modes line exists to prove.
            bool heldZonesOn = authoritative &&
                Features.Find("ServerZoneSim")?.Active == true &&
                Features.Find("OwnershipAuthority")?.Active == true &&
                ArmedStateGate.OwnershipAuthorityArmed;
            string heldZones = heldZonesOn ? $"on (ring bonus={HeldBandGate.RingBonus})" : "off";

            // O10 (EXPERIMENTAL, default off, user decision 2, ownership redesign 2026-09-28): same
            // authoritative + actually-armed rule as heldZones above, never Active alone (CORE-API.md
            // "Live-authority toggles") -- a plain client whose own copy of O10.Active now also defaults false
            // (and would still read "off" here even if turned on locally, since `authoritative` is false for
            // it) always reads "off".
            bool o10On = authoritative && Features.Find("O10")?.Active == true && ArmedStateGate.O10Armed;
            string o10 = o10On ? "on (experimental)" : "off";

            // 1.1.0: the boot-time detection is detail (Verbose); a later change of process kind is shown at Normal.
            if (firstTime)
                NoodlesLog.Verbose($"deployment: detected process kind={kind}; eligible feature groups: {groups}; kill switch={killSwitch}; held zones: {heldZones}; O10: {o10}.");
            else
                NoodlesLog.Info($"deployment: process kind changed to {kind}; eligible feature groups: {groups}; kill switch={killSwitch}; held zones: {heldZones}; O10: {o10}.");
        }

        // ---- Kill switch: one short line per transition (L6) --------------------------------------------------
        //
        // The per-peer "features:" lines below are one-shot, logged once at negotiation time (see
        // `docs/impl/CORE-API.md` "Deployment modes") -- they describe the state AT negotiation, not a live
        // subscription. A kill-switch flip changes what's actually applied for every already-negotiated peer at
        // once, with no new negotiation to re-log against, so it gets its own short line instead. Called from
        // KillSwitch.HandleEnabledTransition -- the one place a real (debounced) transition is detected.
        internal static void LogKillSwitchTransition(bool enabledNow)
        {
            NoodlesLog.Info(enabledNow
                ? "deployment: kill switch on: negotiated players get their own features again."
                : "deployment: kill switch off: every player now gets vanilla behaviour.");
        }

        // ---- Server-side, per peer, once per transition ------------------------------------------------------

        internal static void LogServerPeerNegotiated(ZNetPeer peer, PeerState state)
        {
            NoodlesLog.Info($"{PeerLabel(peer)} runs NoodlesMcDoodles {state.RemoteModVersion}: " +
                $"server+client mode for this player (features: {ServerPeerAppliedFeatures(state)}).");
        }

        internal static void LogServerPeerVanilla(ZNetPeer peer, PeerState state)
        {
            NoodlesLog.Info($"{PeerLabel(peer)} is vanilla: server-only mode for this player " +
                $"(still applies: {VanillaSafeFeatures()}).");
        }

        private static string PeerLabel(ZNetPeer peer)
        {
            if (peer == null) return "peer uid=?";
            return string.IsNullOrEmpty(peer.m_playerName)
                ? $"peer uid={peer.m_uid}"
                : $"{peer.m_playerName} (peer uid={peer.m_uid})";
        }

        /// <summary>
        /// Most entries here only name a feature the peer will actually see because it negotiated the matching
        /// capability bit; "server-authoritative areas" is a deliberate exception — see the FIX note below.
        /// Every other entry still needs: this server's own flag is on, the peer negotiated the matching
        /// capability bit, AND — for a live-authority feature — it's actually ARMED, not merely
        /// switched on in config (L1 FIX, deployment-modes review, 2026-09-28: OwnershipAuthority.Active alone
        /// used to be enough to claim "ownership handover", even while SafeToggle had refused to arm it, e.g. a
        /// forced pre-arm save failed — see <see cref="ArmedStateGate"/>).
        /// </summary>
        private static string ServerPeerAppliedFeatures(PeerState state)
        {
            var parts = new List<string>();

            // FIX (modes review, 2026-09-28): this omitted OwnershipAuthority/SAA's own core zone+near-field
            // claiming (O6/O10/O11; O9 is separately listed below) entirely — ZoneClaimSweep.RunClaimCycle
            // iterates ZNet.instance.GetPeers() (every connected peer) with no PeerState.Negotiated or
            // Capabilities check anywhere in ComputeCandidateZones/AnyPeerWithinPaddedActiveArea
            // (src/Ownership/ZoneClaimSweep.cs) — so a negotiated peer sees SAA's effects exactly like a
            // vanilla one does (see VanillaSafeFeatures below), regardless of what it advertised.
            // Deliberately UNCONDITIONAL on capability — same Active+armed gate as heldZones/O10 above, no
            // HasCapability check — because this feature never asks the peer for cooperation. Kept separate
            // from "ownership handover" below, which stays exactly what it always correctly was: the narrower,
            // capability-gated O8 RPC_RequestOwnershipHandover client-initiated-request feature
            // (OwnershipHandoverRpc.cs) — a different mechanism.
            if (Features.Find("OwnershipAuthority")?.Active == true && ArmedStateGate.OwnershipAuthorityArmed)
                parts.Add("server-authoritative areas");

            if (Features.ConfigSync.Active && state.HasCapability(Capabilities.ConfigSync)) parts.Add("config sync");
            if (Features.Find("ZdoCompression")?.Active == true && state.HasCapability(Capabilities.ZdoCompression)) parts.Add("ZDO compression");
            if (Features.Find("OwnershipAuthority")?.Active == true && ArmedStateGate.OwnershipAuthorityArmed &&
                state.HasCapability(Capabilities.OwnershipHandover)) parts.Add("ownership handover");
            return parts.Count > 0 ? string.Join(", ", parts) : "none";
        }

        /// <summary>
        /// The set of features that don't need the peer's own cooperation. L3 FIX (deployment-modes review,
        /// 2026-09-28): this used to check only <c>Transport.Q2</c> — a vanilla peer this server is actually
        /// pacing via ANY of T1-T9/Q2-Q12/G2N1 (server-side send-rate/budget/chunking levers, none of them
        /// capability-gated) saw "vanilla behavior only" even while pacing was genuinely helping them. Now:
        /// any active `Transport.*` flag groups as "transport pacing" (one short name, not every T/Q flag —
        /// this line stays a summary, not a feature dump); `Transport.Q7` (destroy-message chunking) is
        /// distinct enough in kind to name separately. O9 additionally requires <see cref="ArmedStateGate.O9Armed"/>
        /// (L2 FIX, same reasoning as L1 above — O9.Active alone can be true while SafeToggle has refused to
        /// arm it).
        ///
        /// FIX (modes review, 2026-09-28): this never mentioned OwnershipAuthority/SAA either, even though it's
        /// the flagship feature that needs the LEAST peer cooperation of anything in this file — it claims
        /// zones/near-field objects around a vanilla peer exactly as it would around a negotiated one (see
        /// ServerPeerAppliedFeatures above; both read the same Active+armed pair, deliberately with no
        /// capability check). docs/design/03-ownership-and-handover.md's own "Vanilla and version compatibility"
        /// section is explicit that a vanilla client sees SAA's effects transparently for every object class
        /// except ships, so install mode (A) — server-only, every player vanilla, the exact case this method's
        /// output describes — was the one place this was most wrong to omit.
        /// </summary>
        private static string VanillaSafeFeatures()
        {
            var parts = new List<string>();

            if (Features.Find("OwnershipAuthority")?.Active == true && ArmedStateGate.OwnershipAuthorityArmed)
                parts.Add("server-authoritative areas");

            bool anyTransportPacing = false;
            bool destroyChunking = false;
            foreach (var flag in Features.All)
            {
                if (flag == null || !flag.Name.StartsWith("Transport.", StringComparison.Ordinal)) continue;
                if (!flag.Active) continue;
                if (flag.Name == "Transport.Q7") destroyChunking = true;
                else anyTransportPacing = true;
            }
            if (anyTransportPacing) parts.Add("transport pacing");
            if (destroyChunking) parts.Add("destroy chunking");

            if (Features.Find("O9")?.Active == true && ArmedStateGate.O9Armed) parts.Add("latency-aware ownership");

            return parts.Count > 0 ? string.Join(", ", parts) : "vanilla behavior only";
        }

        // ---- Client-side, per connection, once ----------------------------------------------------------------

        internal static void LogClientServerNegotiated(PeerState state)
        {
            NoodlesLog.Info($"server runs NoodlesMcDoodles {state.RemoteModVersion}: server+client mode " +
                $"(features: {ClientFeatureSummary(state)}).");
        }

        internal static void LogClientServerVanilla(PeerState state, float timeoutSeconds)
        {
            NoodlesLog.Info($"server is vanilla (no handshake reply in {timeoutSeconds:0} s): client-only mode " +
                $"(features: {ClientFeatureSummary(state)}).");
        }

        private static string ClientFeatureSummary(PeerState state)
        {
            string q10 = Features.Find("Transport.Q10")?.Active == true ? "on" : "off";
            string shortExtrap = Features.Find("ClientSmoothing.ShortExtrapolation")?.Active == true ? "on" : "off";
            string compression = state.HasCapability(Capabilities.ZdoCompression) ? "negotiated" : "not negotiated";
            // Only "on" when it can act: Q13 and its client switch, plus Q10 (the client's own send path) and T9 (the
            // rate it follows). Not per connection: T9 also needs a Steam link it manages (pretest q13-review).
            string q13 = Features.Find("Transport.Q13")?.Active == true && Features.Find("Transport.Q13.Client")?.Active == true
                && Features.Find("Transport.Q10")?.Active == true && Features.Find("Transport.T9")?.Active == true
                ? "on" : "off";
            return $"Q10 upload={q10}, upload queue unlock (Q13)={q13}, ShortExtrapolation={shortExtrap}, compression={compression}";
        }

        [NoodlesModuleInit(10)]
        internal static void Init()
        {
            PatchRegistry.Register("DeploymentModeDetect", typeof(Patch_ZNet_Awake_ModeDetect), PatchSide.Both);
        }
    }
}
