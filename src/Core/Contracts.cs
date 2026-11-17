using System;
using System.Collections.Generic;

namespace NoodlesMcDoodles.Core
{
    // Seams between the M2-M7 modules, defined in Core so each module can be built on its own (DESIGN.md
    // Architecture). Every seam has a safe default for when the module that provides it is absent or switched off.

    /// <summary>
    /// Asked by the Ownership module (docs/design/03) before the server takes an object away from a player who
    /// currently owns it. The Gameplay module's HandoverGuard (docs/design/05: S2/S4/S7/S8 — open containers, crewed
    /// ships, attached carts, working smelters, live creatures mid-action) registers vetoes here.
    /// <para>
    /// Safe default: <see cref="HasActiveGuard"/> is false until a guard is registered AND switched on. The Ownership
    /// module must then never take an object from a present player at all; it may only claim objects that nobody
    /// owns or whose owner has left. Vetoes can only refuse, never force.
    /// </para>
    /// </summary>
    internal static class ReclaimGate
    {
        private sealed class Guard
        {
            public string Owner;
            public Func<bool> IsActive;
            public Func<ZDO, string> Veto; // returns a reason to refuse, or null to allow
        }

        private static readonly List<Guard> s_guards = new List<Guard>();

        public static void RegisterGuard(string owner, Func<bool> isActive, Func<ZDO, string> veto)
        {
            lock (s_guards) s_guards.Add(new Guard { Owner = owner, IsActive = isActive, Veto = veto });
        }

        /// <summary>True when at least one registered guard is switched on right now.</summary>
        public static bool HasActiveGuard
        {
            get
            {
                lock (s_guards)
                {
                    foreach (var g in s_guards)
                    {
                        try { if (g.IsActive()) return true; }
                        catch (Exception e) { NoodlesLog.PatchException("ReclaimGate.IsActive:" + g.Owner, e); }
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// False (with a reason) if any active guard refuses, if no guard is active, or if a guard throws. Main thread.
        /// </summary>
        public static bool AllowReclaim(ZDO zdo, out string reason)
        {
            reason = null;
            bool anyActive = false;
            lock (s_guards)
            {
                foreach (var g in s_guards)
                {
                    try
                    {
                        if (!g.IsActive()) continue;
                        anyActive = true;
                        string r = g.Veto(zdo);
                        if (r != null) { reason = g.Owner + ": " + r; return false; }
                    }
                    catch (Exception e)
                    {
                        NoodlesLog.PatchException("ReclaimGate.Veto:" + g.Owner, e);
                        reason = g.Owner + ": guard error";
                        return false; // an error is a refusal, never a pass
                    }
                }
            }
            if (!anyActive) { reason = "no reclaim guard is active"; return false; }
            return true;
        }
    }

    /// <summary>
    /// Answered by the Zone simulation module (docs/design/04): can the server actually simulate this zone right now
    /// (terrain built, objects instantiated, within ZoneCap's CPU/memory budget)? Asked by the Ownership module before
    /// it holds a zone.
    /// <para>Safe default with no provider: false. The server must never hold a zone that nothing simulates, or it
    /// would own objects that never tick (creatures frozen, smelters stopped) while players stand next to them.</para>
    /// </summary>
    internal static class ZoneSimGate
    {
        public static Func<Vector2s, bool> CanHoldProvider;
        private static bool s_warned;

        public static bool CanHold(Vector2s zone)
        {
            Func<Vector2s, bool> p = CanHoldProvider;
            if (p == null) return false;
            try { return p(zone); }
            catch (Exception e)
            {
                if (!s_warned) { s_warned = true; NoodlesLog.PatchException("ZoneSimGate.CanHold", e); }
                return false;
            }
        }
    }

    /// <summary>
    /// Real data-loss alarms (DESIGN.md Lead decision 4). A module that detects a condition meaning world or player
    /// data may have been lost, rolled back or duplicated reports it here, for example: a persistent ZDO destroyed on
    /// the server with no gameplay request behind it, a DataRevision going backwards on a server-held ZDO, or two
    /// machines both believing they own the same object (HandoverGuard.DoubleDropDetected).
    /// <para>Each report is counted (<c>loss_signal_&lt;kind&gt;</c>), logged as an error, and, unless AutoStandDown
    /// is disabled, switches the whole mod off at once so everything returns to vanilla while a human looks.</para>
    /// </summary>
    internal static class LossSignals
    {
        public static void Report(string kind, string detail)
        {
            try
            {
                Telemetry.GetCounter("loss_signal_" + kind).Increment();
                NoodlesLog.Error($"LOSS SIGNAL {kind}: {detail}");
                if (NoodlesConfig.AutoStandDownEnabled.Value && KillSwitch.Enabled)
                    KillSwitch.SetEnabled(false, $"loss signal {kind}: {detail}", automatic: true);
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(LossSignals), e);
            }
        }
    }

    /// <summary>
    /// Whether a live-authority toggle (`docs/impl/CORE-API.md` "Live-authority toggles: SafeToggle") is actually
    /// ARMED right now, not merely switched on in the config — distinct from the owning <see cref="FeatureFlag.Active"/>
    /// itself, which stays true even while <see cref="SafeToggle"/> has refused to arm it (e.g. a forced pre-arm
    /// save failed, or the world hasn't finished loading yet). <see cref="DeploymentLog"/> checks these before
    /// naming a live-authority feature as "applied" to a peer (deployment-modes review L1/L2, 2026-09-28) — the
    /// per-peer log line must never claim a feature is doing real work while it's still structurally refusing
    /// every claim/grant/reassignment. Same shape as <see cref="ZoneSimGate.CanHoldProvider"/>: a provider each
    /// owning module installs at its own Init(); safe default with no provider registered is false (never claim
    /// something is armed that nothing has confirmed).
    /// </summary>
    internal static class ArmedStateGate
    {
        public static Func<bool> OwnershipAuthorityArmedProvider;
        public static Func<bool> O9ArmedProvider;

        /// <summary>O10 (EXPERIMENTAL, default off) — same shape as <see cref="O9ArmedProvider"/>, installed by
        /// <c>O10SharedAreaOwnership.Init</c>.</summary>
        public static Func<bool> O10ArmedProvider;

        public static bool OwnershipAuthorityArmed => SafeInvoke(OwnershipAuthorityArmedProvider);
        public static bool O9Armed => SafeInvoke(O9ArmedProvider);
        public static bool O10Armed => SafeInvoke(O10ArmedProvider);

        private static bool SafeInvoke(Func<bool> provider)
        {
            if (provider == null) return false;
            try { return provider(); }
            catch (Exception e) { NoodlesLog.PatchException("ArmedStateGate", e); return false; }
        }
    }

    /// <summary>
    /// The server's own <c>ZoneSim.HeldBandRingBonus</c> value (ownership redesign synthesis, 2026-09-28),
    /// exposed here so <see cref="DeploymentLog"/> (Core) can report the held-band ring width without a
    /// compile-time reference to the ZoneSim module — same provider-installed-at-<c>Init()</c> shape as
    /// <see cref="ZoneSimGate.CanHoldProvider"/>/<see cref="ArmedStateGate"/>'s own providers. Safe default with
    /// no provider registered is 0 (never claim a wider held band than nothing has confirmed).
    /// </summary>
    internal static class HeldBandGate
    {
        public static Func<int> RingBonusProvider;

        public static int RingBonus
        {
            get
            {
                Func<int> p = RingBonusProvider;
                if (p == null) return 0;
                try { return p(); }
                catch (Exception e) { NoodlesLog.PatchException("HeldBandGate", e); return 0; }
            }
        }
    }

    /// <summary>
    /// Misconfiguration warning (ownership redesign synthesis, 2026-09-28, grafted from the "retire-or-park"
    /// proposal): <c>ServerZoneSim</c> building real terrain and instantiating real objects around every peer
    /// buys nothing without <c>OwnershipAuthority</c> actually holding zones — CPU/RAM and third-party-mod-
    /// exception exposure for zero ownership benefit. Reads both flags by name (<see cref="Features.Find"/>),
    /// no compile-time reference to either module, so ZoneSim's own <c>Init()</c>/<c>SettingChanged</c> handler
    /// AND Ownership's own <c>SettingChanged</c> handler can each call this after THEIR OWN flag changes,
    /// without needing to see the other module's <c>ConfigEntry</c>.
    /// </summary>
    internal static class CrossFlagWarnings
    {
        // Fires again on every real transition INTO the misconfigured state (not a pure forever-one-shot) —
        // an operator who fixes it, then breaks it again later, should see the warning again; repeated
        // SettingChanged fires that leave the state unchanged (e.g. an unrelated key in the same section) never
        // repeat the log line.
        private static bool s_lastWarned;

        public static void CheckServerZoneSimWithoutOwnershipAuthority()
        {
            // RUNTIME AUTHORITY, not the flag alone (docs/impl/CORE-API.md): the warning's own wording ("the
            // server is building...") is only true on the process that's actually authoritative. Every modded
            // PLAYER's own client now defaults both flags on too (2026-09-28) — without this check, an operator
            // who deliberately turns OwnershipAuthority off on just their own remote client (for whatever local
            // reason) would see a misleading warning implying THEIR machine is wasting CPU/RAM, when nothing
            // server-side is actually happening there at all.
            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                s_lastWarned = false; // never let a stale "warned" state from a previous session (e.g. a
                                      // listen-host player who guest-joins someone else) suppress a real warning later.
                return;
            }

            bool zoneSimOn = Features.Find("ServerZoneSim")?.Active == true;
            bool ownershipOn = Features.Find("OwnershipAuthority")?.Active == true;
            bool shouldWarn = zoneSimOn && !ownershipOn;
            if (shouldWarn == s_lastWarned) return;
            s_lastWarned = shouldWarn;
            if (!shouldWarn) return;

            NoodlesLog.Warning("ServerZoneSim is on but OwnershipAuthority is off: the server is building real " +
                "terrain and instantiating real objects around every peer for zero ownership benefit -- CPU/RAM " +
                "and third-party-mod-exception exposure for nothing. Turn OwnershipAuthority on too, or turn " +
                "ServerZoneSim off.");
        }
    }

    /// <summary>
    /// Ownership changes the mod makes go through here. The Ownership module installs its O7 AuthorizedSetOwner as the
    /// provider (hold-guard aware). Either way the result is checked: two of the user's own mods (GetOffMyLawn,
    /// Wonderland) have skip-capable ZDO.SetOwner prefixes (research/13, DESIGN.md conflict table), so a SetOwner call
    /// can silently do nothing.
    /// </summary>
    internal static class OwnershipApi
    {
        /// <summary>1.0.12: a refused hand-back to nobody (newOwner 0) is a mod holding the object on purpose, not a veto
        /// (see SetOwnerHoldGuard.AuthorizedSetOwner); it is counted under this name instead of
        /// ownership.setOwnerVetoed, which feeds ModeQuarantine.</summary>
        internal const string ReleaseRefusedCounterName = "ownership.o11.releaseRefusedByHolder";

        /// <summary>(zdo, newOwner, reason) -> true if the ZDO's owner is newOwner afterwards.</summary>
        public static Func<ZDO, long, string, bool> SetOwnerProvider;

        public static bool SetOwner(ZDO zdo, long newOwner, string reason)
        {
            Func<ZDO, long, string, bool> p = SetOwnerProvider;
            if (p != null) return p(zdo, newOwner, reason);
            zdo.SetOwner(newOwner);
            bool ok = zdo.GetOwner() == newOwner;
            if (!ok) Telemetry.GetCounter(newOwner == 0L ? ReleaseRefusedCounterName : "ownership.setOwnerVetoed").Increment();
            return ok;
        }
    }
}
