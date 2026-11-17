using System;
using System.Text;
using System.Collections.Generic;
using BepInEx.Configuration;
using NoodlesMcDoodles.Core;
using NoodlesMcDoodles.Core.ConfigFiles;
using UnityEngine;

namespace NoodlesMcDoodles.Ownership
{
    /// <summary>
    /// SAA WP-A (<c>scratchpad/saa/final.md</c> §1/§2) — server-authoritative areas: how far O6's own
    /// zone-claim pass reaches into a zone a connected peer is actually standing in ("near-field"), on top of
    /// the always-unchanged far-band behaviour a zone with nobody nearby already had before this feature
    /// existed. <see cref="OwnershipAuthorityMode"/> is the server-wide policy knob; <see cref="IsClaimable"/>
    /// is the per-object decision <see cref="NearFieldClaimVeto"/>'s <c>ZDO.SetOwner</c> prefix asks, once per
    /// object, inside O6's own bulk claim call (<see cref="SetOwnerHoldGuard.RunAuthorizedBulkClaim"/> via
    /// <see cref="ZoneClaimSweep.ClaimZone"/>) — the actual enforcement point the SAA v1 design was missing
    /// (v1's <c>TryPrepareClaim</c> only ever deferred a whole zone or accumulated telemetry; nothing filtered
    /// individual objects inside a claimed zone). <see cref="ZoneClaimSweep"/> (this module's other file, same
    /// owner) additionally widens WHICH zones are even candidates at <see cref="OwnershipAuthorityMode.FarOrShared"/>/
    /// <see cref="OwnershipAuthorityMode.All"/> — this file decides what happens to each object once such a
    /// zone's own bulk claim runs.
    ///
    /// <b>Hard exclusions, every Mode AND every distance, unconditional (final.md §1; widened from "every Mode"
    /// to also "every distance" by a lead-review BLOCKER fix, then extended to the Mode=All-only
    /// transfer-on-interact classes by a round-3 install-gate fix — see <see cref="IsClaimable"/>'s own remarks
    /// for both):</b>
    /// a connected peer's own avatar (<c>Player</c> component — structural, belt-and-suspenders on top of the
    /// margin <c>ReleaseNearbyZDOS</c>'s own <c>IsInPeerActiveArea</c> arithmetic already gives the O6 path, and
    /// on top of the primary fix in O9/O10's own <c>LatencyAwareOwnership.IsExcluded</c>, a different
    /// file/builder's task); ANY <c>Pickable</c>, full stop — not merely one within
    /// <see cref="PickableReleaseRadius"/> of a live peer, which is all near-field ever needed to guarantee
    /// this class's own allowlist below never includes <c>Pickable</c> either way, but which the pre-fix
    /// far-band shortcut bypassed entirely; a <c>Ship</c>, unless <c>Mode == All</c> and
    /// <see cref="s_allowShipClaim"/> is explicitly on (the SAME gate <see cref="IsClassAllowedForMode"/> already
    /// applied near-field, just no longer skippable by distance); the Mode=All-only transfer-on-interact classes
    /// (<see cref="IsAllOnlyClass"/> — Container/ItemStand/ArmorStand/Vagon/Sadle/Turret/Sign/ItemDrop), unless
    /// <c>Mode == All</c> (the SAME gate, same "no longer skippable by distance" reasoning as Ship — the far
    /// band turned out to be every zone from ~160 m out at the shipped defaults, i.e. the entire ZoneSim
    /// held-band ring, not a rare edge case); anything <see cref="CompatClaimExclusions"/> names (another
    /// installed mod's own narrow hold, or a client's own recent interaction burst — also
    /// "saa:compat-pickable"'s file). None of these five ever loosen merely because an object is far from a
    /// peer, including the far band — Ship and the transfer-on-interact classes still loosen at
    /// <see cref="OwnershipAuthorityMode.All"/> exactly as configured; that gate just no longer depends on
    /// distance either way.
    ///
    /// <b>Far-band is otherwise untouched, byte-for-byte, at every Mode (final.md §1's own correction to v1):</b>
    /// once the checks above have already run, a zone that fails
    /// <see cref="ZoneClaimSweep.AnyPeerWithinPaddedActiveArea"/> — today's existing
    /// <see cref="OwnershipAuthorityMode.All"/>/<see cref="OwnershipAuthorityMode.FarOrShared"/> Mode allowlist
    /// for every OTHER class (<see cref="IsFarOrSharedClass"/>'s WearNTear/production/creature list) and
    /// <see cref="CompatClaimExclusions.InClientClaimGrace"/> are NEVER consulted for a far-band object — is
    /// allowed exactly as before this feature existed, at every Mode. This is checked against the OBJECT's own
    /// zone (<see cref="ZDO.GetSector"/>), not the zone <see cref="ZoneClaimSweep.ClaimZone"/> is currently
    /// claiming — vanilla's own <c>ReleaseNearbyZDOS</c> scans a real-world radius around a zone's centre, which
    /// can genuinely reach into a neighbouring zone that IS peer-overlapped even while the zone being claimed is
    /// not.
    /// </summary>
    internal static class NearFieldClaimPolicy
    {
        /// <summary>Server-wide near-field claim posture (final.md §1). Ordinal order matters — see
        /// <see cref="IsClassAllowedForMode"/>'s <c>mode &gt;= FarOrShared</c> check, which relies on
        /// <see cref="All"/> being a strict superset of <see cref="FarOrShared"/>'s own allowlist.</summary>
        internal enum OwnershipAuthorityMode
        {
            SharedFar = 0,
            FarOrShared = 1,
            All = 2,
        }

        private enum VetoReason
        {
            None,
            PrefabUnresolved,
            Player,
            PickableNearPlayer,
            CompatExcluded,
            ClientClaimGrace,
            ShipClaimDisabled,
            // round-3 install-gate fix: distinct from ModeNotAllowed so telemetry can tell an IsAllOnlyClass
            // object refused unconditionally (any distance, incl. far-band) apart from Evaluate()'s own
            // near-field ModeNotAllowed veto for every other class.
            AllOnlyClassNotAllowed,
            ModeNotAllowed,
            // 1.0.11: a building piece a still-connected player placed this session (its ZDOID was minted by
            // that player's own client) stays with that player near-field -- see IsFreshBuildOfConnectedPeer.
            FreshBuild,
        }

        // --- Config ------------------------------------------------------------------------------------------

        private static ConfigEntry<OwnershipAuthorityMode> s_mode;
        private static ConfigEntry<bool> s_allowShipClaim;
        private static ConfigEntry<float> s_pickableReleaseRadius;

        /// <summary>Live-clamped radius, for <see cref="PickableRelease"/> ("saa:compat-pickable", a different
        /// file/owner) to read — the config entry itself is bound here per this task's own file assignment.</summary>
        internal static float PickableReleaseRadius => Mathf.Clamp(s_pickableReleaseRadius.Value, 6f, 30f);

        /// <summary>The configured Mode, with <see cref="ModeQuarantine"/>'s auto-downgrade already applied
        /// (final.md §1: "effective Mode = ModeQuarantine.ForceSharedFar ? SharedFar : configured"). Every
        /// caller in this module (<see cref="ZoneClaimSweep"/>'s zone eligibility, this file's own per-object
        /// allowlist) reads THIS, never <c>s_mode.Value</c> directly, so a quarantine trip takes effect on the
        /// very next zone/object with no separate propagation step.</summary>
        internal static OwnershipAuthorityMode EffectiveMode =>
            ModeQuarantine.ForceSharedFar ? OwnershipAuthorityMode.SharedFar : s_mode.Value;

        // --- Startup/deployment line (task: "start-up/deployment line states the effective Mode") --------------
        //
        // A dedicated line here, not a change to Core/DeploymentLog.cs — this task's own file assignment is
        // src/Ownership/{NearFieldClaimPolicy,NearFieldClaimVeto,ZoneClaimSweep}.cs plus docs; DeploymentLog.cs
        // belongs to no builder this pass and up to a dozen agents are editing this repo at once, so this stays
        // self-contained rather than risking a collision on a shared Core file nobody assigned to this task.
        // (int)(OwnershipAuthorityMode)(-1) is not a real enum value -- guarantees the very first call always
        // logs as "at startup", matching DeploymentLog.LogModeIfChanged's own sentinel-then-diff shape.
        private static OwnershipAuthorityMode s_lastLoggedMode = (OwnershipAuthorityMode)(-1);

        /// <summary>Cheap (one enum compare) — called every <see cref="ZoneClaimSweep"/> claim cycle
        /// (<see cref="ZoneClaimSweep.RunClaimCycle"/> does; that is this method's ONLY call site as of the
        /// lead-review modes fix — <see cref="Init"/> no longer calls this directly, see that method's own
        /// remarks for why). Only logs on a real transition, or the very first call — and, since its one caller
        /// only ever runs on an actually-authoritative, actually-active server, the very first call is always
        /// genuinely "at startup" on a process where this is genuinely true.</summary>
        internal static void LogEffectiveModeIfChanged()
        {
            OwnershipAuthorityMode mode = EffectiveMode;
            if (mode == s_lastLoggedMode) return;
            bool firstTime = (int)s_lastLoggedMode < 0;
            s_lastLoggedMode = mode;

            string quarantineNote = ModeQuarantine.ForceSharedFar && s_mode.Value != OwnershipAuthorityMode.SharedFar
                ? $" (quarantined down from configured {s_mode.Value} by a veto/exception burst — an operator's " +
                  "'noodles clear' re-raises it)"
                : "";

            // 1.1.0: the boot-time line is detail (Verbose) unless the mode is quarantined; a change is always shown.
            if (!firstTime)
                NoodlesLog.Info($"O11: effective [3 - Server World] Mode changed to {mode}{quarantineNote}.");
            else if (quarantineNote.Length > 0)
                NoodlesLog.Info($"O11: effective [3 - Server World] Mode at startup = {mode}{quarantineNote}.");
            else
                NoodlesLog.Verbose($"O11: effective [3 - Server World] Mode at startup = {mode}.");
        }

        // --- Per-frame peer-list memo (avoids one ZNet.GetPeers() allocation per near-field object touched by
        // a single ReleaseNearbyZDOS call -- IsClaimable can be asked about many objects in one claim, all in
        // the same frame, since RunClaimCycle's whole claim pass for this tick runs synchronously within it) ---

        private static readonly List<ZNetPeer> s_emptyPeers = new List<ZNetPeer>();
        private static List<ZNetPeer> s_cachedPeers = s_emptyPeers;
        private static int s_cachedPeersFrame = -1;

        private static List<ZNetPeer> PeersThisFrame()
        {
            int frame = Time.frameCount;
            if (frame != s_cachedPeersFrame)
            {
                s_cachedPeersFrame = frame;
                s_cachedPeers = ZNet.instance != null ? ZNet.instance.GetPeers() : s_emptyPeers;
            }
            return s_cachedPeers;
        }

        // --- Telemetry (task: "ownership.o11.* (claims, vetoes by reason, playerZdoClaimAttempted which must
        // stay 0)") -- resolved once at class load, matching HandoverGuard.cs's own established perf pattern
        // rather than a per-call Telemetry.GetCounter(string) lookup. -----------------------------------------

        private static readonly Counter s_claims = Telemetry.GetCounter("ownership.o11.claims");
        private static readonly Counter s_vetoedPrefabUnresolved = Telemetry.GetCounter("ownership.o11.vetoed.prefabUnresolved");
        private static readonly Counter s_vetoedPlayer = Telemetry.GetCounter("ownership.o11.vetoed.player");
        private static readonly Counter s_vetoedPickableNearPlayer = Telemetry.GetCounter("ownership.o11.vetoed.pickableNearPlayer");
        private static readonly Counter s_vetoedCompatExcluded = Telemetry.GetCounter("ownership.o11.vetoed.compatExcluded");
        private static readonly Counter s_vetoedClientClaimGrace = Telemetry.GetCounter("ownership.o11.vetoed.clientClaimGrace");
        private static readonly Counter s_vetoedShipClaimDisabled = Telemetry.GetCounter("ownership.o11.vetoed.shipClaimDisabled");
        private static readonly Counter s_vetoedAllOnlyClassNotAllowed = Telemetry.GetCounter("ownership.o11.vetoed.allOnlyClassNotAllowed");
        private static readonly Counter s_vetoedModeNotAllowed = Telemetry.GetCounter("ownership.o11.vetoed.modeNotAllowed");
        private static readonly Counter s_vetoedFreshBuild = Telemetry.GetCounter("ownership.o11.vetoed.freshBuild");

        /// <summary>The one counter that "must stay 0" (final.md §9/WP-G item 3) — a nonzero value means O6's
        /// own bulk claim actually reached a live connected player's own avatar ZDO before this veto caught it;
        /// this is a loss-risk SIGNAL, not a routine metric, and gets its own Tier-1 rate-limited line below,
        /// not just a counter buried in the periodic telemetry flush.</summary>
        private static readonly Counter s_playerZdoClaimAttempted = Telemetry.GetCounter("ownership.o11.playerZdoClaimAttempted");

        // --- The decision -------------------------------------------------------------------------------------

        /// <summary>
        /// Asked by <see cref="NearFieldClaimVeto"/>'s <c>ZDO.SetOwner</c> prefix, once per object, only while
        /// O6's own bulk claim is in flight and only for a claim targeting the server's own session id.
        /// <paramref name="serverUid"/> is accepted (not re-derived) to match the veto's own already-verified
        /// call site exactly — this function itself never needs to know the value beyond having already been
        /// confirmed by the caller.
        /// </summary>
        internal static bool IsClaimable(ZDO zdo, long serverUid)
        {
            if (zdo == null) return false;

            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;

            // BLOCKER FIX (lead review, dl-saa-compat #1/#2): these three checks must hold at EVERY distance,
            // not only within the near-field radius the far-band shortcut below governs. Previously all three
            // were reachable only via Evaluate(), which the far-band shortcut skipped ENTIRELY -- so a
            // Wonderland Smelter/Fireplace, a BlightedWorldHeart, a Ship, or a Pickable beyond the near-field
            // radius (but still, e.g., inside ZoneSim's widened held-band ring) got claimed with NONE of these
            // ever consulted, contradicting this class's own "none of these ever loosen at any Mode" remarks
            // above and the user's binding "ships and pickables are never taken" decision. Ship and Pickable are
            // made unconditional here to match what near-field ALREADY effectively guarantees today: Pickable
            // has never been a member of either IsClassAllowedForMode class list below, so near-field already
            // vetoes it via ModeNotAllowed regardless of live-player distance (the IsNearLivePlayer check inside
            // Evaluate only ever gave that same outcome its own dedicated telemetry reason, never a DIFFERENT
            // outcome); Ship's own Mode/AllowShipClaim gate is reused as-is below, not tightened -- just no
            // longer skippable by distance.
            if (CompatClaimExclusions.IsExcluded(zdo, prefab, out _))
            {
                RecordVeto(zdo, prefab, VetoReason.CompatExcluded);
                return false;
            }
            if (prefab != null && prefab.GetComponent<Pickable>() != null)
            {
                RecordVeto(zdo, prefab, VetoReason.PickableNearPlayer);
                return false;
            }
            if (prefab != null && prefab.GetComponent<Ship>() != null)
            {
                bool shipAllowed = EffectiveMode == OwnershipAuthorityMode.All && s_allowShipClaim.Value;
                if (!shipAllowed)
                {
                    RecordVeto(zdo, prefab, VetoReason.ShipClaimDisabled);
                    return false;
                }
                // Mode=All + AllowShipClaim=true: admin has explicitly opted in -- matches near-field's own
                // existing allowance exactly (IsClassAllowedForMode's Ship branch, inside Evaluate below); fall
                // through to the far-band/near-field handling below like any other claimable object.
            }

            // BLOCKER FIX (round-3 install-gate review, claim-class-order lens): the far-band shortcut below
            // returned true for ANY object not already refused above -- so a connected peer's own avatar, and
            // every Mode=All-only transfer-on-interact class (IsAllOnlyClass -- Container/ItemStand/ArmorStand/
            // Vagon/Sadle/Turret/Sign/ItemDrop), reached it and got claimed with Evaluate()/
            // IsClassAllowedForMode() never consulted. Not a rare edge case: ZoneSim's own widened held-band
            // ring (HeldBandRingBonus, on by default) makes the ENTIRE ~160-224 m held band around every
            // connected peer "far-band" to this policy (this policy's own near-field radius tops out at
            // PeerRefPosPadding, default 64 m -- well inside that ring) -- so a player's own turret, chest,
            // stand, cart, sign or dropped item sitting in their own held band was getting silently
            // server-claimed, against the binding user decision ("chests, carts, item stands, armor stands,
            // turrets, signs and ground items STAY WITH PLAYERS... unless an admin sets Mode=All"). Promoted
            // here exactly like Ship and Pickable already were above: refused unconditionally, before the
            // far-band shortcut, with a distinct VetoReason so telemetry can tell this apart from Evaluate()'s
            // own near-field ModeNotAllowed. Player is promoted the same way -- a player's own character must
            // never be taken at any distance, matching this class's own "every distance" remarks above, which
            // nothing actually enforced outside Evaluate() (unreachable from the far band) until now.
            if (prefab != null && prefab.GetComponent<Player>() != null)
            {
                RecordVeto(zdo, prefab, VetoReason.Player);
                return false;
            }
            if (prefab != null && IsAllOnlyClass(prefab) && EffectiveMode != OwnershipAuthorityMode.All)
            {
                RecordVeto(zdo, prefab, VetoReason.AllOnlyClassNotAllowed);
                return false;
            }

            // Far-band: allowed exactly as today for everything else, at every Mode, before the near-field-only
            // checks below even run (final.md §1's correction to v1 — see class remarks).
            if (!ZoneClaimSweep.AnyPeerWithinPaddedActiveArea(zdo.GetSector(), PeersThisFrame()))
            {
                return true;
            }

            VetoReason reason = Evaluate(zdo, prefab);
            if (reason == VetoReason.None)
            {
                s_claims.Increment();
                return true;
            }

            RecordVeto(zdo, prefab, reason);
            return false;
        }

        /// <summary>Order matches the task brief exactly: Player -> Pickable-near-player -> compat exclusions
        /// -> client claim grace -> Mode allowlist. Only ever reached for a near-field object now (IsClaimable's
        /// own unconditional pre-checks already veto a Player/Ship/Pickable/CompatExcluded object, and (round-3
        /// install-gate fix) an IsAllOnlyClass object outside Mode==All, at any distance, before this runs --
        /// see that method's remarks) -- the Player, Pickable-near-player and CompatExcluded checks below (plus
        /// IsClassAllowedForMode's own IsAllOnlyClass/Ship branches) are therefore defense-in-depth against a
        /// future call site calling this directly, never the only thing standing between a near-field claim and
        /// any of those categories; left in place deliberately rather than removed, matching this codebase's own
        /// layered-guard convention elsewhere.</summary>
        private static VetoReason Evaluate(ZDO zdo, GameObject prefab)
        {
            // Z3-style "can't resolve what this even is" guard (same reasoning as
            // Gameplay.HandoverGuard.IsSafeToReclaim's own prefab-null branch): never widen near-field claiming
            // for an object this process can't identify.
            if (prefab == null) return VetoReason.PrefabUnresolved;

            if (prefab.GetComponent<Player>() != null) return VetoReason.Player;

            if (prefab.GetComponent<Pickable>() != null && PickableRelease.IsNearLivePlayer(zdo.GetPosition()))
                return VetoReason.PickableNearPlayer;

            if (CompatClaimExclusions.IsExcluded(zdo, prefab, out _)) return VetoReason.CompatExcluded;

            if (CompatClaimExclusions.InClientClaimGrace(zdo)) return VetoReason.ClientClaimGrace;

            if (IsPlayerBuiltStructure(prefab) && IsFreshBuildOfConnectedPeer(zdo)) return VetoReason.FreshBuild;

            if (!IsClassAllowedForMode(prefab, EffectiveMode, out bool shipDisabled))
                return shipDisabled ? VetoReason.ShipClaimDisabled : VetoReason.ModeNotAllowed;

            return VetoReason.None;
        }

        /// <summary>
        /// The Mode class allowlist (final.md §1's table, unchanged from v1 in WHICH prefabs land in which
        /// list — only the CHECK ORDER changed, see BLOCKER FIX below): <c>FarOrShared</c> adds the
        /// routed-RPC / no-transfer classes (never a client-visible ownership transfer — vanilla always relays
        /// their state through a targeted RPC to whoever currently owns them, so claiming one near a player
        /// changes who relays it, never what the player sees happen). <c>All</c> is a strict superset
        /// (<c>mode &gt;= FarOrShared</c>) plus the B1/B2 transfer-on-interact classes, with <c>Ship</c> gated
        /// by its own <see cref="s_allowShipClaim"/> (default off — see that key's own description).
        ///
        /// BLOCKER FIX (round-2 review, saa-core, exclusions lens): the transfer-on-interact allOnlyClass check
        /// (and Ship's own gate) now run and return FIRST, before <see cref="IsFarOrSharedClass"/> ever runs —
        /// previously it was the other way around. Container, ArmorStand, ItemStand, Turret and ItemDrop all
        /// also carry a WearNTear component in vanilla (confirmed via decompile, Valheim 1.0.16: each calls
        /// <c>GetComponent&lt;WearNTear&gt;()</c> on itself in its own <c>Awake</c> and wires up its
        /// <c>m_onDestroyed</c> hook — Container.cs:83, ArmorStand.cs:87, ItemStand.cs:93, Turret.cs:165,
        /// ItemDrop.cs:1239/1275 — which only makes sense if that component is normally present on the piece,
        /// consistent with all of them being ordinarily destructible in vanilla play). Under the OLD ordering,
        /// <see cref="IsFarOrSharedClass"/> matched via WearNTear alone and returned <c>true</c> at the very
        /// first check for any of these at the live <c>FarOrShared</c> default — even though
        /// <see cref="IsAllOnlyClass"/> explicitly restricts every one of them to <c>Mode.All</c>. That directly
        /// violated the binding user decision "chests, carts, item stands, signs and ground items stay with
        /// whoever uses them": a player's own chest/stand/turret/dropped item could get near-field-claimed to
        /// the server, silently dropping any routed RPC aimed at it (e.g. Container's own
        /// RPC_RequestOpen/RPC_RequestStack/RPC_RequestTakeAll) the next time its owner tried to use it —
        /// exactly the AzuCraftyBoxes/AzuAreaRepair-adjacent hazard this task was asked to protect against.
        /// <see cref="IsAllOnlyClass"/> is now authoritative regardless of which other components the same
        /// prefab happens to carry, closing the leak for today's five confirmed prefabs and for Vagon/Sadle/Sign
        /// against any future vanilla component change. Ship is likewise moved ahead of
        /// <see cref="IsFarOrSharedClass"/> as defense-in-depth for the same reason (<see cref="Evaluate"/>'s
        /// own remarks already note this method must stay correct if a future call site ever reaches it
        /// directly, not only via <see cref="IsClaimable"/>'s own unconditional Ship pre-check) — behaviourally
        /// unchanged for Ship today, since <see cref="IsClaimable"/> already gates every real call site on
        /// <c>Mode==All &amp;&amp; AllowShipClaim</c> before <see cref="Evaluate"/> is ever reached for one.
        /// A round-3 install-gate fix gave <see cref="IsAllOnlyClass"/> itself the exact same unconditional
        /// pre-check treatment in <see cref="IsClaimable"/> (refused whenever <c>EffectiveMode != All</c>,
        /// before the far-band shortcut) — so this method's own allOnlyClass branch is, likewise, now
        /// unreachable for a real call site outside that pre-check, kept only as the same defense-in-depth.
        /// </summary>
        private static bool IsClassAllowedForMode(GameObject prefab, OwnershipAuthorityMode mode, out bool shipDisabled)
        {
            shipDisabled = false;

            // Regression canary (see CheckAllOnlyFarOrSharedOverlapOnce's own remarks) -- cheap bool guard on
            // every call, real scan only once.
            CheckAllOnlyFarOrSharedOverlapOnce();

            if (prefab.GetComponent<Ship>() != null)
            {
                if (mode != OwnershipAuthorityMode.All) return false;
                if (!s_allowShipClaim.Value) { shipDisabled = true; return false; }
                return true;
            }

            if (IsAllOnlyClass(prefab)) return mode == OwnershipAuthorityMode.All;

            if (mode >= OwnershipAuthorityMode.FarOrShared && IsFarOrSharedClass(prefab)) return true;

            return false;
        }

        /// <summary>
        /// Mode=All-only transfer-on-interact classes (final.md §1's table) — vanilla always relays these
        /// through a targeted RPC to whoever currently owns them, so a near-field claim of one IS a
        /// client-visible ownership transfer, unlike <see cref="IsFarOrSharedClass"/>'s list. Factored out of
        /// <see cref="IsClassAllowedForMode"/> so <see cref="CheckAllOnlyFarOrSharedOverlapOnce"/> shares the
        /// exact same definition rather than risking a second, driftable copy.
        /// </summary>
        private static bool IsAllOnlyClass(GameObject prefab) =>
            prefab.GetComponent<Container>() != null || prefab.GetComponent<ItemStand>() != null ||
            prefab.GetComponent<ArmorStand>() != null || prefab.GetComponent<Vagon>() != null ||
            prefab.GetComponent<Sadle>() != null || prefab.GetComponent<Turret>() != null ||
            prefab.GetComponent<Sign>() != null || prefab.GetComponent<ItemDrop>() != null;

        /// <summary>
        /// 1.0.12 (live Proxima 2026-10-01 14:38). A prefab a player BUILDS: it has a WearNTear and is not a ground
        /// item. In Valheim 1.0.16 harvested food (Onion, Honey, Blueberries, Raspberry, Mushroom, Pukeberries, ...)
        /// is ItemDrop + Piece + WearNTear, so the bare WearNTear test 1.0.11 used took every onion a player
        /// harvested for a "fresh build". Ground items follow the item rules (<see cref="IsAllOnlyClass"/>), never
        /// the fresh-build ones.
        /// </summary>
        internal static bool IsPlayerBuiltStructure(GameObject prefab) =>
            prefab.GetComponent<WearNTear>() != null && prefab.GetComponent<ItemDrop>() == null;

        /// <summary>
        /// 1.0.11 (live Proxima 2026-10-01). True when this piece's ZDOID was minted by a client that is still
        /// connected: vanilla <c>ZDOMan.CreateNewZDO</c> uses the creating process's own session id as the
        /// ZDOID's UserID, and a connected peer's <c>m_uid</c> is that same id (live: INIGO's character
        /// -571950909:1 and new portal -571950909:1454, peer uid -571950909). Such a piece was placed this session
        /// by someone still online, so near-field it stays with whoever vanilla gives it to (its builder while
        /// near), exactly as in vanilla. INIGO's portal was claimed by the server moments after INIGO stepped
        /// away and was destroyed while server-held. Older pieces and pieces in the far band are unaffected.
        /// </summary>
        internal static bool IsFreshBuildOfConnectedPeer(ZDO zdo)
        {
            long minter = zdo.m_uid.UserID;
            if (minter == 0L || ZNet.instance == null || minter == ZDOMan.GetSessionID()) return false;
            return ZNet.instance.GetPeer(minter) != null;
        }

        // 1.0.11: TreeBase and TreeLog left this list (live Proxima 2026-10-01: "trees vanish before they touch
        // the ground", "a stump vanished"). A server-owned tree near a player turns every chop into a routed hit
        // the server resolves, then the server spawns and simulates the falling log and the stump; logs break
        // each other and stumps on the server (mirror repro r3), the player sees that late, and in zones the
        // server doesn't hold vanilla hands the falling log to the player mid-fall. Near-field, trees and logs
        // now stay with the player as in vanilla (PickableRelease.RunReleasePass hands back any the server
        // still holds within a live player's 3x3); the far band still claims them like everything else.
        /// <summary>
        /// FarOrShared routed-RPC / no-transfer classes (final.md §1's table). Factored out for the same reuse
        /// reason as <see cref="IsAllOnlyClass"/>; see that method's own remarks and
        /// <see cref="IsClassAllowedForMode"/>'s BLOCKER FIX note for why this list must never be consulted
        /// before <see cref="IsAllOnlyClass"/> for the same prefab.
        /// </summary>
        private static bool IsFarOrSharedClass(GameObject prefab) =>
            prefab.GetComponent<WearNTear>() != null ||
            prefab.GetComponent<Smelter>() != null || prefab.GetComponent<Fermenter>() != null ||
            prefab.GetComponent<Beehive>() != null || prefab.GetComponent<CookingStation>() != null ||
            prefab.GetComponent<Incinerator>() != null || prefab.GetComponent<ShieldGenerator>() != null ||
            prefab.GetComponent<MineRock>() != null || prefab.GetComponent<MineRock5>() != null ||
            prefab.GetComponent<Door>() != null || prefab.GetComponent<TerrainComp>() != null ||
            prefab.GetComponent<Windmill>() != null || prefab.GetComponent<CraftingStation>() != null ||
            prefab.GetComponent<BaseAI>() != null || prefab.GetComponent<MonsterAI>() != null ||
            prefab.GetComponent<Tameable>() != null;

        // --- Regression guard (finding fix, round-2 review, exclusions lens) -----------------------------------

        private static bool s_overlapRegressionChecked;

        /// <summary>
        /// One-time diagnostic: with the ordering fix above, a prefab matching BOTH <see cref="IsAllOnlyClass"/>
        /// and <see cref="IsFarOrSharedClass"/> is no longer a live claim-leak (allOnlyClass always wins,
        /// unconditionally) — but a silent overlap is still exactly the kind of thing a future refactor could
        /// re-break without anyone noticing, so this logs one line per offending prefab, once, if it ever finds
        /// one, rather than relying on nobody ever re-introducing the old check order.
        ///
        /// Deliberately NOT run from <c>[NoodlesModuleInit]</c>/boot, despite that being the fix's own original
        /// suggestion: <c>Init()</c> runs from <c>NoodlesPlugin.Awake()</c> on both the dedicated-server and the
        /// Steam-client process alike, before <c>ZNetScene.instance</c> exists (let alone has a populated
        /// <c>m_prefabs</c>) on either — the exact same lesson this file's own <c>Init()</c> already documents
        /// for why its startup mode-line had to move off <c>Init()</c> entirely (see that method's own MAJOR FIX
        /// remarks). Deferred instead to the first real <see cref="IsClassAllowedForMode"/> call — by then
        /// <c>ZNetScene.instance.m_prefabs</c> is guaranteed populated, since this method's only call site
        /// (<see cref="Evaluate"/>) is itself only reachable from an actually-armed server's real claim attempt
        /// (see <see cref="IsClaimable"/>'s own remarks).
        /// </summary>
        private static void CheckAllOnlyFarOrSharedOverlapOnce()
        {
            if (s_overlapRegressionChecked) return;
            s_overlapRegressionChecked = true;

            try
            {
                if (ZNetScene.instance == null) return; // shouldn't happen at this call site; defensive only -- retried never, this is a one-shot best-effort diagnostic
                // 1.0.2: one summary line instead of one Warning per prefab (236 lines on Proxima's mod stack).
                int overlap = 0;
                var examples = new StringBuilder();
                foreach (GameObject prefab in ZNetScene.instance.m_prefabs)
                {
                    if (prefab == null) continue;
                    if (IsAllOnlyClass(prefab) && IsFarOrSharedClass(prefab))
                    {
                        if (overlap < 8) examples.Append(overlap == 0 ? "" : ", ").Append(prefab.name);
                        overlap++;
                    }
                }
                if (overlap > 0)
                {
                    NoodlesLog.Verbose($"O11: {overlap} prefab(s) match BOTH the Mode=All-only transfer-on-interact " +
                        "classes and the FarOrShared WearNTear/production/creature classes (e.g. " + examples +
                        "). IsClassAllowedForMode checks allOnlyClass first and it always wins, so none of them is " +
                        "claimed near players below Mode=All; a future edit to that method must keep that order.");
                }
            }
            catch (Exception e)
            {
                // Plain internal helper, not a Harmony patch body -- matches ZoneClaimSweep.ReleaseAllHeldZones'
                // own reasoning for logging directly rather than through PatchException (whose patch-name
                // lookup wouldn't resolve this to a registered feature anyway). Best-effort diagnostic only --
                // never lets a failure here affect any real claim decision.
                NoodlesLog.Warning($"O11: CheckAllOnlyFarOrSharedOverlapOnce threw ({e.GetType().Name}: " +
                    $"{e.Message}) -- this is a one-time diagnostic only, no claim decision is affected.");
            }
        }

        /// <summary>
        /// Counters (unconditional) plus logging (WP1 "tons of logging but not spammy" — CORE-API.md's
        /// rate-limited idiom: the first few of a given reason log in full, Tier-1 for the one signal that
        /// "must stay 0", Tier-2 for the rest, everything past that aggregated into one periodic "+K more
        /// suppressed" line rather than either silence or a flood). The per-name counters ALSO surface for
        /// free in Core's own periodic telemetry flush (`Telemetry.FlushAndLog`) — the aggregated Tier-1
        /// numeric line the task asks for, with zero extra code.
        /// </summary>
        private static void RecordVeto(ZDO zdo, GameObject prefab, VetoReason reason)
        {
            switch (reason)
            {
                case VetoReason.PrefabUnresolved: s_vetoedPrefabUnresolved.Increment(); break;
                case VetoReason.Player:
                    s_vetoedPlayer.Increment();
                    s_playerZdoClaimAttempted.Increment();
                    // Wording FIX (round-3 install-gate fix): dropped "near-field" -- this reason is now also
                    // reachable from IsClaimable's own unconditional far-band pre-check (see that method's
                    // remarks), not only Evaluate()'s near-field path, matching the same fix already applied
                    // to the generic Tier-2 line below.
                    NoodlesLog.WarningRateLimited("ownership.o11.playerZdoClaimAttempted",
                        $"O11: server's own claim pass attempted to take a CONNECTED PLAYER'S OWN " +
                        $"AVATAR ZDO ({zdo.m_uid}) -- vetoed here, but this should never happen (O9/O10's own " +
                        "IsExcluded already categorically excludes a connected peer's own avatar upstream of " +
                        "this) -- investigate.",
                        firstN: 3, windowSeconds: 60f);
                    return; // own Tier-1 line above is enough; skip the generic Tier-2 line below for this one
                case VetoReason.PickableNearPlayer: s_vetoedPickableNearPlayer.Increment(); break;
                case VetoReason.CompatExcluded: s_vetoedCompatExcluded.Increment(); break;
                case VetoReason.ClientClaimGrace: s_vetoedClientClaimGrace.Increment(); break;
                case VetoReason.ShipClaimDisabled: s_vetoedShipClaimDisabled.Increment(); break;
                case VetoReason.AllOnlyClassNotAllowed: s_vetoedAllOnlyClassNotAllowed.Increment(); break;
                case VetoReason.ModeNotAllowed: s_vetoedModeNotAllowed.Increment(); break;
                case VetoReason.FreshBuild: s_vetoedFreshBuild.Increment(); break;
            }

            // Wording FIX (lead review, dl-saa-compat): this line now also fires for a far-band veto (Ship/
            // Pickable/CompatExcluded -- see IsClaimable's own pre-checks, which run independently of distance),
            // not only a near-field one -- "near-field" was dropped so the message stays accurate either way.
            string name = prefab != null ? prefab.name : $"prefab-hash {zdo.GetPrefab()}";
            NoodlesLog.DetailRateLimited("ownership.o11.vetoed." + reason,
                $"O11: claim vetoed ({reason}) -- {name} at {zdo.GetPosition()} (zdo {zdo.m_uid}).",
                firstN: 20, windowSeconds: 60f);
        }

        // --- Init --------------------------------------------------------------------------------------------

        [NoodlesModuleInit]
        internal static void Init()
        {
            ConfigFile config = NoodlesConfig.File;

            // 1.1.0: main file [3 - Server World] Mode (was [OwnershipAuthority] Mode). SAA (scratchpad/saa/final.md):
            // how far O6's zone-claim pass reaches into a zone a peer is standing in ('near-field'), on top of the
            // unchanged far-band behaviour. A live player's own avatar, a Pickable within PickableReleaseRadius of a
            // connected peer's LIVE position, and anything CompatClaimExclusions/InClientClaimGrace names are
            // hard-excluded at every Mode. SharedFar: near-field off (pre-SAA floor). FarOrShared: adds the routed-RPC /
            // no-transfer classes (WearNTear, production buildings, MineRock, Door, TerrainComp, Windmill,
            // CraftingStation, creatures, Tameable; since 1.0.11 trees, logs and pieces a connected player placed this
            // session stay with nearby players) -- claiming one changes who relays its state, never what the player
            // sees. All: adds the transfer-on-interact classes (Container, ItemStand, ArmorStand, Vagon, Sadle, Turret,
            // Sign, ItemDrop) plus Ship (gated by AllowShipClaim). DEFAULT FarOrShared ("everything but items").
            // Auto-downgrades to SharedFar under a veto/exception burst (ModeQuarantine) until an operator clears it.
            s_mode = NoodlesConfig.ServerWorldMode;

            s_allowShipClaim = config.Bind("OwnershipAuthority", "AllowShipClaim", false,
                new ConfigDescription(
                    "Only consulted at Mode=All. Off by default because O8 (OwnershipHandoverRpc) has no " +
                    "vanilla-client hand-back path for a server-held ship (docs/design/03 'Vanilla and version " +
                    "compatibility': confirmed no RPC_Request*-style ownership-claim RPC exists on Ship at " +
                    "all) -- a vanilla (unmodded) client that boards a server-claimed ship under this flag has " +
                    "no way back until it installs the mod. Leave off unless you specifically accept that " +
                    "limitation at every dock a player owns."));

            s_pickableReleaseRadius = config.Bind("OwnershipAuthority", "PickableReleaseRadius", 12f,
                new ConfigDescription(
                    "Radius around a connected peer's LIVE avatar position (never the staler refPos " +
                    "PeerRefPosPadding governs) inside which a Pickable is refused at claim time and " +
                    "proactively handed back if already server-held (PickableRelease, once per claim cycle). " +
                    "Sized above vanilla's own 5 m interact range (Humanoid.m_maxInteractDistance, " +
                    "SERVER:9577) plus one claim-cycle's worth of running-speed movement, with a small margin " +
                    "-- the 6 m floor stays above the interact range itself, since a smaller value would " +
                    "defeat the purpose.",
                    new AcceptableValueRange<float>(6f, 30f)));

            // MAJOR FIX (lead review, modes finding): this used to call LogEffectiveModeIfChanged() unconditionally
            // right here. Init() runs from NoodlesPlugin.Awake() (NoodlesModuleInitAttribute discovery) on BOTH
            // the dedicated-server and the Steam-client process alike, before ZNet.instance exists on either
            // (Side.cs's own remarks) -- so that call printed "O11: effective OwnershipAuthority.Mode at startup
            // = FarOrShared" on a plain client's own console too, and on the non-hosting side of a mixed
            // connection, falsely implying that process enforces near-field claims. This project's own binding
            // rule (docs/impl/CORE-API.md "Runtime authority, not the flag alone") is that a deployment-mode line
            // must gate on ZNet.instance.IsServer(), never the flag/config alone -- DeploymentLog.cs's own
            // ZNet.Awake-postfix lines already follow it; this one didn't. No replacement call is needed here:
            // ZoneClaimSweep.RunClaimCycle already calls LogEffectiveModeIfChanged() every claim cycle (see that
            // method), and that call site only ever runs from Patch_ZDOMan_ReleaseZDOS_ClaimPass.Postfix, itself
            // gated on ZNet.instance.IsServer() AND (via the Prefix's own __state) OwnershipAuthority.Active --
            // so the very first real call, on an actually-authoritative, actually-active process, still logs
            // "at startup" (s_lastLoggedMode's sentinel guarantees that), and a non-authoritative or inactive
            // process now correctly never logs this line at all instead of logging a misleading one.
        }
    }
}
