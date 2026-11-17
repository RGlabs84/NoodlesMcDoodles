using System;
using BepInEx.Configuration;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using NoodlesMcDoodles.Core.ConfigFiles;
using UnityEngine;

namespace NoodlesMcDoodles.Ownership
{
    /// <summary>
    /// O1 (docs/design/03-ownership-and-handover.md, Hook H5, "Phase A" slice — DESIGN.md's M1 Ships list) —
    /// raises the server's simulation-distance ceiling safely by independently hard-clamping both write paths
    /// that can ever move <c>ZNet.m_simulationDistance</c>/a peer's own <c>ZNetPeer.m_simulationDistance</c>,
    /// so a wider value (room a later server-held-zone claim, G3-L1/O6 in M3, needs to actually operate in) can
    /// never be pushed past a configured, code-enforced maximum by either a stale/aggressive CLI flag or a
    /// hostile/buggy peer's own RPC request.
    ///
    /// <b>Two separate write paths, one shared clamp</b> (docs/design/03 levers table, O1 row: "mod hard-clamps
    /// the CLI value and independently clamps RPC_RequestValidSimulationDistance's client-supplied value, both
    /// to the same configured ceiling"):
    /// <list type="bullet">
    /// <item><c>ZNet.ApplySimulationDistance(SimulationDistance)</c> (SERVER:81023-81030/CLIENT:81210-81217,
    /// public, byte-identical both builds) is the ONE place <c>m_simulationDistance</c> is ever written on the
    /// server — including the CLI <c>-simulationdistance</c> flag's own path (<c>FejdStartup.ParseServerArguments</c>
    /// queues <c>ZNet.s_onZNetStart += () => ZNet.instance.ApplySimulationDistance(...)</c>, SERVER:97184-97207/
    /// 97210-97217, fired from <c>ZNet.Start</c>). A prefix here clamps whatever value ANY caller (CLI queue,
    /// <c>SimulationDistanceServerHandshake</c>, a future O3/G4-N3 caller) is about to apply, before it's
    /// stored — no separate "apply at startup" call is needed the way docs/design/03's own sketch adds one,
    /// since intercepting the actual write covers every caller including the very first CLI-driven one,
    /// regardless of init order.</item>
    /// <item><c>ZNet.RPC_RequestValidSimulationDistance(ZRpc, ZPackage)</c> (SERVER:81003-81012/CLIENT:81189-81198,
    /// private both builds, confirmed identical signature — registered as the client's own request-a-higher-
    /// value RPC) is a SEPARATE write path: it clamps the caller's requested value against the server's
    /// current <c>m_simulationDistance</c> using vanilla's own <c>&lt;</c> operator ("min"), then writes the
    /// result straight to <c>GetPeer(rpc).m_simulationDistance</c> — a per-peer field <c>ApplySimulationDistance</c>
    /// never touches. <c>SimulationDistance.Deserialize</c> (SERVER:117813-117820) reads three fixed-width
    /// fields (near:int, far:int, classic:bool) off the wire with NO range validation of its own, so a
    /// hostile/buggy peer can request e.g. near=40 directly — closed here with a second, independent prefix
    /// that re-serializes the deserialized value through the same clamp before vanilla's own body (which
    /// re-deserializes and mins it against <c>m_simulationDistance</c>) ever runs.</item>
    /// </list>
    ///
    /// This file also owns the FIRST registration of <c>Features.OwnershipAuthority</c>/<c>OwnershipAuthority.Active</c>
    /// — the Ownership module's shared master switch (docs/design/03's shared-infra dependency) — since O1 is
    /// the only Ownership-module lever DESIGN.md's M1 ships; a later milestone's Ownership levers (G3-L1, O6,
    /// O3, O4, O8, G4-N3, ...) read the same <see cref="Features_Ownership.OwnershipAuthority"/> flag this
    /// Init() creates, per `docs/impl/CORE-API.md`'s "expose your registered flags as static properties"
    /// convention. Default <c>false</c> ("opt-in until field-tested", DESIGN.md Configuration reference) means
    /// O1 ships as verified, working code in M1 without being live by default — matching every other lever the
    /// Ownership module's own doc keeps behind this same switch (G3-N3's clamp companion, O1's own sibling,
    /// docs/design/03 lever table row 52, ships "Phase B core" behind the identical flag).
    /// </summary>
    internal static class SimulationDistanceCeiling
    {
        // --- Config -------------------------------------------------------------------------------------

        private static ConfigEntry<bool> s_ownershipAuthorityActive;
        private static ConfigEntry<int> s_ceilingLevel;

        /// <summary>
        /// DESIGN.md's Ownership Configuration table lists this key's default as "4 practical / 6 hard clamp",
        /// range "0-10 hard-clamped" — two different numbers under one entry, not a typo. Read together with
        /// docs/design/03's own lever-table prose for O1 ("Practical band 0-4 (&lt;=81 zones/peer) ... recommend
        /// 6 as the absolute clamp even though the practical operating band is 0-4"), this is a P2-style
        /// two-tier clamp: the config's own <see cref="AcceptableValueRange{T}"/> accepts an operator setting
        /// 0-10 (headroom for a value this mod hasn't itself field-tested past level 6 yet), while THIS
        /// constant — not itself operator-configurable — is the actual behavioural ceiling ever applied; a
        /// configured value above it is silently re-clamped at apply time with a loud, one-shot warning,
        /// exactly matching <c>SavePolicy.ClampBackupCount</c>'s "accept a wider config range, enforce a
        /// narrower real one, never pass the excess through silently" precedent.
        /// </summary>
        private const int AbsoluteHardClampLevel = 6;

        /// <summary>Ownership module's shared master switch — see class remarks. Read via <see cref="Features_Ownership.OwnershipAuthority"/>.</summary>
        internal static FeatureFlag OwnershipAuthorityFlag { get; private set; }

        /// <summary>
        /// LEAD FIX (CORE-API.md "Live-authority toggles: SafeToggle"): <c>OwnershipAuthority.Active</c> is a
        /// live-authority toggle — flipping it on lets O6 start claiming zones and O8 start granting hand-backs,
        /// both real ownership moves — and DESIGN.md's own contract for such toggles is a forced, verified save
        /// first (<see cref="SafeToggle.TryEnable"/>), never a bare bool flip. <see cref="OwnershipAuthorityArmed"/>
        /// is the gate every actual claim/grant call site checks (see <see cref="ZoneClaimSweep.ClaimZone"/>,
        /// <c>OwnershipHandoverRpc.RPC_RequestOwnershipHandover</c>) — distinct from <c>.Active</c> itself, which
        /// stays exactly what the operator configured even while a save is refused, so a retry (toggle off/on,
        /// or a later successful save) can arm it without the operator having to notice and re-flip the config.
        /// </summary>
        internal static bool OwnershipAuthorityArmed { get; private set; }

        /// <summary>
        /// The real, code-enforced ceiling level (vanilla's own "level" scale, <c>SimulationDistance.GetSimulationDistance</c>)
        /// — cheap and side-effect-free, safe to call from a hot patch on every apply/RPC. Never logs (the
        /// "configured above the hard clamp" warning is a one-shot, fired only from <see cref="WarnIfConfiguredAboveHardClamp"/>
        /// at Init and on a live config change, not on every hot-path read).
        /// </summary>
        internal static int ResolveCeilingLevel()
        {
            int effective = Mathf.Clamp(s_ceilingLevel.Value, 0, AbsoluteHardClampLevel);
            Telemetry.SetGauge("ownership.simDistCeilingLevel", effective);
            return effective;
        }

        private static void WarnIfConfiguredAboveHardClamp()
        {
            int configured = s_ceilingLevel.Value;
            if (configured > AbsoluteHardClampLevel)
            {
                NoodlesLog.Warning($"OwnershipAuthority.SimulationDistanceCeiling={configured} exceeds this mod's " +
                    $"own absolute hard clamp of {AbsoluteHardClampLevel} (docs/design/03 O1: \"recommend 6 as " +
                    $"the absolute clamp\") — every apply will silently use {AbsoluteHardClampLevel} instead. Set " +
                    $"a value <= {AbsoluteHardClampLevel} explicitly to silence this warning.");
            }
        }

        /// <summary>
        /// Clamps an arbitrary (possibly attacker- or CLI-supplied) <see cref="SimulationDistance"/> down to
        /// <paramref name="ceilingLevel"/>'s own near/far bounds, per-field. A value already inside the ceiling
        /// passes through unchanged (vanilla's classic presets included, see the body); anything clamped comes
        /// out non-classic regardless of what <paramref name="raw"/> claimed: <c>ZoneSystem</c>'s own radius
        /// checks (SERVER:113859/113880/113937, byte-identical CLIENT) skip the circular
        /// <c>ZonesWithinRadius()</c> distance test entirely whenever <c>IsClassic</c> is true
        /// (<c>m_simulationDistance.IsClassic || ZonesWithinRadius(...)</c>) — a classic-true value at a given
        /// Near effectively covers a full square instead of a circle, strictly MORE zones than the same Near
        /// non-classic. Since <c>SimulationDistance.Deserialize</c> reads the classic bool with no validation
        /// of its own, honouring an attacker-supplied <c>classic:true</c> here would let a hostile peer widen
        /// their own effective coverage past what the near/far clamp below intended even while both individual
        /// numbers stay within the ceiling — so the clamp result never carries it through.
        /// </summary>
        internal static SimulationDistance ClampToCeiling(SimulationDistance raw, int ceilingLevel)
        {
            SimulationDistance ceiling = SimulationDistance.GetSimulationDistance(ceilingLevel);

            // 1.0.1: a value already inside the ceiling passes through unchanged, including vanilla's own classic
            // presets (level 0 = (1,2,classic) and the default level 2 = OriginalDistance (2,2,classic)). 1.0.0
            // rebuilt OriginalDistance as non-classic (2,2), which is vanilla's "Low" preset and compares LOWER
            // than classic (2,2), so every client was capped at Low (the corners of the loaded square dropped).
            // A classic square of half-width Near <= 2 fits inside any wider ceiling circle (2*sqrt(2) < 3), so
            // classic is kept only for those presets and only under a classic or strictly wider ceiling; a
            // classic flag on anything bigger (a hostile or buggy request) is still forced non-classic below.
            bool withinNumbers = raw.NearSimulationDistance >= 0 && raw.FarSimulationDistance >= 0
                && raw.NearSimulationDistance <= ceiling.NearSimulationDistance
                && raw.FarSimulationDistance <= ceiling.FarSimulationDistance;
            bool classicFits = !raw.IsClassic
                || (raw.NearSimulationDistance <= SimulationDistance.OriginalNear
                    && (ceiling.IsClassic || ceiling.NearSimulationDistance > raw.NearSimulationDistance));
            if (withinNumbers && classicFits) return raw;

            int near = Mathf.Clamp(raw.NearSimulationDistance, 0, ceiling.NearSimulationDistance);
            int far = Mathf.Clamp(raw.FarSimulationDistance, 0, ceiling.FarSimulationDistance);
            return new SimulationDistance(near, far, classic: false);
        }

        // --- Hooks ----------------------------------------------------------------------------------------

        /// <summary>
        /// Clamps every write to the SERVER's own <c>m_simulationDistance</c> — the CLI <c>-simulationdistance</c>
        /// flag's queued apply, <c>SimulationDistanceServerHandshake</c>'s self-consistency call, and any
        /// future caller — to the configured ceiling. Prefix (not postfix): must clamp the value BEFORE
        /// vanilla stores it, since the field itself has no engine-side ceiling of its own.
        /// </summary>
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.ApplySimulationDistance))]
        internal static class Patch_ZNet_ApplySimulationDistance_Ceiling
        {
            static void Prefix(ZNet __instance, ref SimulationDistance simulationDistance)
            {
                if (!Features_Ownership.OwnershipAuthority.Active) return;
                if (__instance == null || !__instance.IsServer()) return; // O1 is server-only (docs/design/03 H5).

                try
                {
                    int level = ResolveCeilingLevel();
                    SimulationDistance clamped = ClampToCeiling(simulationDistance, level);
                    if (!clamped.Equals(simulationDistance))
                    {
                        NoodlesLog.Warning($"clamped ZNet.m_simulationDistance from (near={simulationDistance.NearSimulationDistance} " +
                            $"far={simulationDistance.FarSimulationDistance} classic={simulationDistance.IsClassic}) down to " +
                            $"(near={clamped.NearSimulationDistance} far={clamped.FarSimulationDistance} classic={clamped.IsClassic}) " +
                            "before applying — see OwnershipAuthority.SimulationDistanceCeiling. A CLI -simulationdistance flag (or " +
                            "another caller) requested more than this mod's configured/hard-clamped ceiling allows.");
                    }
                    else
                    {
                        // Rare (startup and settings changes only), so no rate limit: states what every client will be capped at.
                        NoodlesLog.Verbose($"simulation distance: applying near={simulationDistance.NearSimulationDistance} " +
                            $"far={simulationDistance.FarSimulationDistance} classic={simulationDistance.IsClassic} unchanged " +
                            $"(within ceiling level {level}).");
                    }
                    simulationDistance = clamped;
                }
                catch (Exception e)
                {
                    // Fail open to whatever the caller originally requested — the vanilla value an unmodded
                    // server would have applied anyway — never let this guard itself become a new failure mode.
                    NoodlesLog.PatchException(nameof(Patch_ZNet_ApplySimulationDistance_Ceiling), e);
                }
            }
        }

        /// <summary>
        /// Clamps a PEER's own requested value before vanilla's body deserializes and mins it against
        /// <c>m_simulationDistance</c> — closes the "hostile/buggy peer requests near=40" hazard even when
        /// that peer's request would otherwise land on <c>GetPeer(rpc).m_simulationDistance</c>, a field
        /// <see cref="Patch_ZNet_ApplySimulationDistance_Ceiling"/> above never touches.
        /// </summary>
        [HarmonyPatch(typeof(ZNet), "RPC_RequestValidSimulationDistance")]
        internal static class Patch_ZNet_RPCRequestValidSimulationDistance_Ceiling
        {
            static void Prefix(ZNet __instance, ref ZPackage pkg)
            {
                if (!Features_Ownership.OwnershipAuthority.Active) return;
                if (__instance == null || !__instance.IsServer()) return;
                if (pkg == null) return;

                int savedPos = pkg.GetPos();
                try
                {
                    SimulationDistance raw = SimulationDistance.Deserialize(ref pkg);
                    SimulationDistance clamped = ClampToCeiling(raw, ResolveCeilingLevel());

                    if (!clamped.Equals(raw))
                    {
                        Telemetry.GetCounter("ownership.simDistRequestClamped").Increment();
                        NoodlesLog.Warning(NoodlesLogLevel.Normal, $"clamped a peer's requested simulation distance (near={raw.NearSimulationDistance} " +
                            $"far={raw.FarSimulationDistance} classic={raw.IsClassic}) down to (near={clamped.NearSimulationDistance} " +
                            $"far={clamped.FarSimulationDistance} classic={clamped.IsClassic}) — see OwnershipAuthority.SimulationDistanceCeiling.");
                    }

                    // Re-serialize into a FRESH package rather than rewrite in place: SimulationDistance's wire
                    // shape (two ints + a bool) is fixed-width today, but this makes no assumption about that
                    // staying true. ZPackage.Write/Read share one MemoryStream position, so the fresh package's
                    // position must be reset to 0 before handing it to vanilla's own Deserialize below —
                    // otherwise it would read from end-of-stream (post-write position) and throw.
                    var replacement = new ZPackage();
                    clamped.Serialize(ref replacement);
                    replacement.SetPos(0);
                    pkg = replacement; // vanilla's own body re-deserializes this from position 0, unaffected by our read above.
                }
                catch (Exception e)
                {
                    pkg.SetPos(savedPos); // leave the ORIGINAL package exactly as vanilla itself would have seen it.
                    NoodlesLog.PatchException(nameof(Patch_ZNet_RPCRequestValidSimulationDistance_Ceiling), e);
                }
            }
        }

        [NoodlesModuleInit]
        internal static void Init()
        {
            ConfigFile config = NoodlesConfig.File;

            // 1.1.0: main file [3 - Server World] ServerRunsWorld (was [OwnershipAuthority] Active). Master switch for
            // the Ownership module (the server-owns-zones primitive, docs/design/03). DEFAULT ON (user decision,
            // ownership redesign, 2026-09-28: 'Fix and do all, default to on') — every lever (O1, G3-L1, O6, O3, O4,
            // O8, ...) sits behind it, together with Features.ServerZoneSim.Active and ZoneSim.HeldBandRingBonus=1.
            // RUNTIME AUTHORITY: every claim/grant/reassignment this flag gates also requires ZNet.instance.IsServer()
            // (ZoneSimConfig.IsAuthoritativeProcess() or the inline check each patch carries), never this flag alone —
            // a plain remote client, whose own copy of this config is also true, behaves exactly as if it were off.
            s_ownershipAuthorityActive = NoodlesConfig.ServerRunsWorld;
            OwnershipAuthorityFlag = Features.Register("OwnershipAuthority", s_ownershipAuthorityActive);
            s_ownershipAuthorityActive.SettingChanged += (_, __) => CrossFlagWarnings.CheckServerZoneSimWithoutOwnershipAuthority();

            // LEAD FIX (CORE-API.md "Live-authority toggles"): a LIVE (post-boot) flip goes through
            // SafeToggle.TryEnable (see ReconcileArmedState) — marshalled onto the main thread first, since
            // ConfigFile.SettingChanged is not guaranteed to fire there. Boot-time is different: if the config
            // file already says Active=true when this process starts (an operator's own persistent setting, not
            // a live flip happening now), arm directly with no forced save — nothing has moved yet this session,
            // so the world that's about to load (or is already loading) from disk IS the rollback point; forcing
            // a save of a world that hasn't even finished loading would be meaningless (SafeToggle itself would
            // refuse it: "ZNet.instance is null (no world loaded yet)").
            s_ownershipAuthorityActive.SettingChanged += (_, __) => MainThreadDispatcher.Enqueue(ReconcileArmedState);
            if (s_ownershipAuthorityActive.Value)
            {
                OwnershipAuthorityArmed = true;
                NoodlesLog.Verbose("OwnershipAuthority: armed at boot (already on in the config file) — no forced " +
                    "save; the world about to load from disk is the rollback point.");
            }

            s_ceilingLevel = config.Bind("OwnershipAuthority", "SimulationDistanceCeiling", 4,
                new ConfigDescription("O1 — server simulation-distance ceiling, vanilla's own 'level' scale " +
                    "(SimulationDistance.GetSimulationDistance). Practical operating band is 0-4 (<=81 zones/peer); " +
                    $"this mod's own absolute hard clamp is level {AbsoluteHardClampLevel} regardless of what's " +
                    "configured here (values up to 10 are accepted for future headroom but silently re-clamped " +
                    "at apply time, loudly logged — see docs/design/03's O1 lever entry).",
                    new AcceptableValueRange<int>(0, 10)));

            WarnIfConfiguredAboveHardClamp();
            s_ceilingLevel.SettingChanged += (_, __) =>
            {
                WarnIfConfiguredAboveHardClamp();
                // ConfigFile.SettingChanged is not guaranteed to run on the main thread (CORE-API.md "What NOT
                // to do") — marshal the actual ZNet call through the heartbeat's queue.
                MainThreadDispatcher.Enqueue(ReapplyCeilingIfLowered);
            };

            // NOTE: NOT registered with ConfigSyncRpc in M1, even though DESIGN.md's Configuration reference
            // marks this key "synced" — DESIGN.md's own Architecture section is explicit that M1's ConfigSync
            // payload is deliberately near-empty ("No values need to reach a client in M1 - every M1 lever is
            // server-local"), and O1's clamp is itself server-only (docs/design/03 H5: "Side: server"; a
            // client never reads its own copy of this ceiling for anything in M1). Wiring the synced push is
            // straightforward (ConfigSyncRpc.RegisterSyncedValue, see CORE-API.md) whenever a later phase
            // actually has a client-side consumer for the value.

            // M1-STATUS §6 known limitation, now fixed: PatchSide.ServerOnly gates on Side.IsDedicatedServerProcess
            // (a load-time BUILD-type heuristic), not ZNet.IsServer() (runtime authority) — wrong tool for "is this
            // process authoritative", per SharedBudget.cs's own doc comment and the identical fix already applied
            // to Transport.Q6 (SendPriority.cs) this same shape. Both patches below already carry their own runtime
            // `!__instance.IsServer()` guard (see each Prefix), so PatchSide.Both + that guard is the correct,
            // established pattern — a listen-server host (a client-build process that IS authoritative) would
            // otherwise silently never get O1's clamp applied.
            PatchRegistry.Register("OwnershipAuthority", typeof(Patch_ZNet_ApplySimulationDistance_Ceiling), PatchSide.Both);
            PatchRegistry.Register("OwnershipAuthority", typeof(Patch_ZNet_RPCRequestValidSimulationDistance_Ceiling), PatchSide.Both);

            // L1 (deployment-modes review, 2026-09-28): DeploymentLog reads this instead of OwnershipAuthorityArmed
            // directly, matching every other cross-module seam's own no-compile-time-reference convention.
            ArmedStateGate.OwnershipAuthorityArmedProvider = () => OwnershipAuthorityArmed;
        }

        /// <summary>
        /// Re-runs the server's CURRENT value back through <see cref="Patch_ZNet_ApplySimulationDistance_Ceiling"/>
        /// on a live config edit, so a LOWERED ceiling takes effect without a restart. A RAISED ceiling is a
        /// deliberate no-op here: O1 only ever caps an incoming value, it never itself pushes the applied
        /// value up (that's still vanilla's own CLI flag/default) — see class remarks.
        /// </summary>
        private static void ReapplyCeilingIfLowered()
        {
            if (!Features_Ownership.OwnershipAuthority.Active) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            try
            {
                ZNet.instance.ApplySimulationDistance(ZNet.instance.GetSyncedSimulationDistance());
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException("OwnershipAuthority.SimulationDistanceCeiling.ReapplyCeilingIfLowered", e);
            }
        }

        /// <summary>
        /// LEAD FIX (CORE-API.md "Live-authority toggles"). Reacts to a LIVE (post-boot) flip of
        /// <c>OwnershipAuthority.Active</c> only — called from the config's own <c>SettingChanged</c> handler,
        /// always marshalled onto the main thread first (BepInEx does not guarantee that event fires there, and
        /// <see cref="SafeToggle.TryEnable"/> itself asserts it). Boot-time arming (the flag already on when the
        /// process starts) is handled separately, directly in <see cref="Init"/>, without going through this —
        /// see that call site's own remarks for why a forced save isn't needed there.
        /// </summary>
        private static void ReconcileArmedState()
        {
            bool wantsOn = s_ownershipAuthorityActive.Value;
            if (wantsOn == OwnershipAuthorityArmed) return; // no real transition (e.g. a redundant SettingChanged fire)

            if (!wantsOn)
            {
                // Disarming needs no save: giving up the ABILITY to move ownership can never lose anything;
                // only GAINING it (the branch below) can. Already-claimed zones are not force-released here —
                // that matches this module's own long-documented behaviour (ZoneClaimSweep.cs: disabling stops
                // further claims; vanilla's own next unmodified sweep hands already-claimed objects back on its
                // own). The GLOBAL kill switch's flush callback is the explicit, stronger "revert everything
                // now" path; a per-feature Active=false deliberately isn't required to match its speed.
                OwnershipAuthorityArmed = false;
                NoodlesLog.Info("OwnershipAuthority: disarmed (Active turned off).");
                return;
            }

            bool armed = SafeToggle.TryEnable(() => OwnershipAuthorityArmed = true, "OwnershipAuthority");
            if (armed)
            {
                NoodlesLog.Info("OwnershipAuthority: armed (forced pre-arm save verified).");
            }
            else
            {
                Telemetry.GetCounter("ownership.armFailed").Increment();
                NoodlesLog.Error("OwnershipAuthority: Active is on but arming was REFUSED — see SafeToggle's own " +
                    "log line above for why (low disk space, or a silently-skipped save). No zone will be " +
                    "claimed and no O8 hand-back will be granted until this succeeds; toggle Active off then on " +
                    "again once the underlying problem is fixed, or restart.");
            }
        }
    }

    /// <summary>
    /// Dotted-access convenience wrapper, matching `docs/impl/CORE-API.md`'s own suggested
    /// <c>Features.Transport.Q2.Active</c>-style pattern (there: <c>Features_Transport.Q2</c>) — every later
    /// Ownership-module file should read <see cref="OwnershipAuthority"/>/<see cref="O9"/> from here rather than
    /// re-registering flags another file in this module already owns. <see cref="O9"/> itself is registered by
    /// <c>LatencyAwareOwnership.cs</c> (its own file, per the Configuration reference: "independent of
    /// OwnershipAuthority.Active") — exposed here purely so every file in this module uses the same dotted
    /// access convention for every flag, not just this file's own.
    /// </summary>
    internal static class Features_Ownership
    {
        public static FeatureFlag OwnershipAuthority => SimulationDistanceCeiling.OwnershipAuthorityFlag;
        public static FeatureFlag O9 => LatencyAwareOwnership.O9Flag;
    }
}
