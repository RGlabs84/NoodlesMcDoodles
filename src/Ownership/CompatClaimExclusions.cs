using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.Ownership
{
    /// <summary>
    /// SAA WP-A (<c>scratchpad/saa/final.md</c> §1/§2) — Proxima's other ~49 mods, consulted by
    /// <see cref="NearFieldClaimPolicy"/>'s per-object decision (<c>Evaluate</c>) AFTER the hard <c>Player</c>/
    /// <c>Pickable</c> checks and BEFORE the Mode allowlist. Two independent mechanisms, both unconditional (no
    /// on/off flag of their own — a data-safety/compat fix should not be toggle-able off by accident; only
    /// tunable PARAMETERS, like <see cref="s_clientClaimGraceSeconds"/>'s window, get a config key):
    ///
    /// <list type="bullet">
    /// <item><see cref="IsExcluded"/> — a permanent, structural exclusion for a PREFAB CATEGORY another
    /// installed mod's own code depends on the server never owning (Wonderland's ProductionSupplyEngine;
    /// BlightedWorldHeart's kill-resolution broadcast/loot scaling). Never loosens at any Mode, same as the
    /// Player/Pickable checks above it in <c>Evaluate</c>.</item>
    /// <item><see cref="InClientClaimGrace"/> — a temporary, per-ZDO-instance exclusion: this file's own
    /// <see cref="Patch_ZDO_SetOwner_ClientClaimGraceTracker"/> postfix (registered here, calling no other
    /// builder's file) timestamps every ZDO a real connected client (never the server, never a bare release)
    /// is observed to actually take ownership of, and this stays true for <see cref="s_clientClaimGraceSeconds"/>
    /// afterward — long enough for AzuCraftyBoxes' <c>GetNearbyContainers</c> (every qualifying nearby container
    /// claimed on ANY craft action, not just the one opened) or AzuAreaRepair's <c>Player.Repair</c> (every
    /// <c>WearNTear</c> piece in radius, dozens-to-hundreds per swing) to finish its own multi-object
    /// interaction burst without the near-field claim sweep fighting a just-granted client claim back mid-burst.
    /// Deliberately NOT type-restricted to containers/pieces at the check itself (that's the motivating
    /// real-world case, not a hard requirement) — tracking every client claim generically is cheaper (no
    /// <c>GetComponent</c> in the hot <c>ZDO.SetOwner</c> postfix) and holding back a re-claim for a few seconds
    /// is a safe default for ANY object type, never a correctness risk.</item>
    /// </list>
    ///
    /// See docs/impl/OWNERSHIP-IMPL.md "Proxima compatibility" for the full per-mod triage (including the
    /// several NEEDS_HANDLING findings this file does NOT need to touch, because they're already resolved
    /// structurally elsewhere — the Player-avatar hard exclusion in <c>NearFieldClaimPolicy</c>/
    /// <c>LatencyAwareOwnership.IsExcluded</c> — or already mitigated by <c>Gameplay/NREGuards.cs</c>'s existing
    /// headless-safe <c>Pickable.RPC_Pick</c> patch) and the ones that genuinely cannot be fixed from this side.
    /// </summary>
    internal static class CompatClaimExclusions
    {
        // === IsExcluded: permanent, prefab-category exclusions ================================================

        private static readonly Counter s_wonderlandProductionExclusions =
            Telemetry.GetCounter("ownership.compat.wonderlandProductionExclusions");
        private static readonly Counter s_blightedWorldHeartExclusions =
            Telemetry.GetCounter("ownership.compat.blightedWorldHeartExclusions");

        /// <summary>
        /// True when the server must never claim this object because another installed mod depends on who owns
        /// it (Proxima compat findings, <c>scratchpad/research/compat-feel-bandwidth-harness.json</c>). Called
        /// only for a near-field candidate that has already passed the Player/Pickable checks above it, so this
        /// never needs to re-check those.
        /// </summary>
        internal static bool IsExcluded(ZDO zdo, GameObject prefab, out string reason)
        {
            // MAJOR FIX (lead review, logs finding): moved the "Proxima compat active" line here (see
            // LogActiveOnce below) from Init(), which ran unconditionally on every process (client included,
            // before ZNet even exists) with no Side/ZNet.instance.IsServer()/OwnershipAuthority.Active gate at
            // all -- the one unconditional protective-effect claim of its kind in this codebase (every sibling
            // "active"/"armed" line elsewhere gates on the live state it actually describes). This method is
            // only ever reached from NearFieldClaimPolicy.Evaluate, itself only reachable via NearFieldClaimVeto's
            // Prefix (ZNet.instance.IsServer() + SetOwnerHoldGuard.AuthorizedBulkClaimInFlight -- i.e. only during
            // an actually-armed server's own real claim attempt), so logging on first entry here is genuinely
            // "active" at the moment it's logged, on the correct process only.
            LogActiveOnce();

            reason = null;
            if (prefab == null) return false; // caller's own PrefabUnresolved veto already covers this case upstream; nothing to identify here.

            // --- Wubarrk-Wonderland ProductionSupplyEngine (CONFLICT, DATA LOSS finding) ----------------------
            //
            // /home/rohan/WubarrkCODING/ValheimMods/SERVER SIDE ONLY/Wonderland/Subsystems/ItemFlow/ProductionSupplyEngine.cs
            // (class doc, lines 10-58; ResolveDelivery, lines 417-429; component checks confirmed at lines
            // 144/150/511/560: `prefab.GetComponent<Fireplace>()` / `GetComponent<Smelter>()`) feeds linked-
            // chest fuel/ore into Fireplace-family (torch/hearth/campfire/sconce) and Smelter-family (smelter/
            // blast furnace/kiln) stations via raw ZDO field writes, on the hard-coded invariant "the server
            // NEVER takes ownership of a station" (that file's own doc comment, added in 0.8.0 after a live
            // "the smelter ate my silver" item-loss incident it already fixed once from the OPPOSITE direction:
            // up to 0.7.2 Wonderland itself claimed the station to the server before writing, and a nearby
            // player's own hand-feed was silently dropped by ZRoutedRpc — vanilla routes RPC_AddOre/RPC_AddFuel
            // to the ZDO's OWNER, and a dedicated server pre-SAA has no live Smelter/Fireplace instance at all).
            // NoodlesMcDoodles's own Gameplay S7/ProductionHandoverGuard (src/Gameplay/LivenessBookkeeper.cs)
            // now gives the dedicated server a genuinely live, ticking Smelter/Fireplace component under SAA --
            // exactly the case Wonderland's invariant assumes can never happen. A server-owned station's live
            // component holds its own in-memory fuel/ore state and periodically flushes it to the ZDO;
            // Wonderland's own out-of-band write (every ~20-45s for a smelter, ~2-4min for a fireplace) would
            // race that flush and could silently lose whatever either side just added -- the exact item-loss
            // class this mod's own "zero data loss ever" rule exists to prevent, and one this codebase has
            // already seen happen once (from the other direction) in production.
            //
            // Fix: exclude Smelter/Fireplace from near-field claiming UNCONDITIONALLY -- not gated on detecting
            // Wonderland's own presence (a BepInEx.Bootstrap.Chainloader GUID probe would work today, but a
            // future Wonderland version change, a plugin-load-order quirk, or simply Proxima's mod list changing
            // is exactly the kind of thing that must never silently re-open a data-loss path on a live 1.58M-ZDO
            // world). The cost is real but bounded and safe in the "never worse than vanilla" sense: these two
            // categories simply never move to the server near a player, so behave exactly as they do today,
            // without this mod's ownership changes, whether or not Wonderland is actually installed.
            if (prefab.GetComponent<Smelter>() != null || prefab.GetComponent<Fireplace>() != null)
            {
                reason = "wonderland.productionSupplyEngine";
                s_wonderlandProductionExclusions.Increment();
                NoodlesLog.DetailRateLimited("ownership.compat.wonderlandProduction",
                    $"O11 compat: refused near-field claim of {prefab.name} (Smelter/Fireplace) at " +
                    $"{zdo.GetPosition()} -- Wubarrk-Wonderland's ProductionSupplyEngine assumes the server " +
                    "never owns a production station; see docs/impl/OWNERSHIP-IMPL.md 'Proxima compatibility'.",
                    firstN: 5, windowSeconds: 60f);
                return true;
            }

            // --- Wubarrk-BlightedWorldHeart (NEEDS_HANDLING finding) -------------------------------------------
            //
            // /home/rohan/WubarrkCODING/ValheimMods/BlightedHeart/BlightedWorldHeartAI.cs OnDeath (~229-260) guards its
            // "purified" kill broadcast with `Player.m_localPlayer != null` (always false headless); DropLoot()
            // (~262-296) reads `Player.GetPlayersInRange` (SERVER:14858-14891, confirmed this session: iterates
            // the local, client-only `s_players` list, always empty on a dedicated server) and defaults to
            // playerCount=1 (floor dropCount=3) when that's empty. Gameplay-only (no item/build loss), but under
            // FarOrShared/O10 the server becomes this boss's owner at kill time often enough ("routine instead
            // of rare" per the finding) that kills the server resolves are silently under-announced and
            // under-rewarded. Excluding this one named prefab keeps kill resolution on whichever player's client
            // actually simulates it, matching vanilla/pre-SAA behaviour exactly -- the same "never worse than
            // vanilla" trade this file makes for Wonderland above, just for a gameplay-quality reason instead of
            // a data-loss one. Matched by prefab NAME (Plugin.cs:56 `CreateClonedPrefab("BlightedWorldHeart",
            // "TheHive")`; confirmed the one and only prefab this specific AI script drives), not a component
            // type check, since BlightedWorldHeartAI is a third-party type this project has no reason to take an
            // assembly reference to (BepInEx+Harmony only, per the binding rules) -- GameObject.name is a plain
            // Unity API already used the same way elsewhere in this module (e.g. ZNetScene.GetPrefab(hash)?.name
            // in OWNERSHIP-IMPL.md's own Lead fix batch).
            if (string.Equals(prefab.name, "BlightedWorldHeart", StringComparison.Ordinal))
            {
                reason = "blightedWorldHeart.killResolution";
                s_blightedWorldHeartExclusions.Increment();
                NoodlesLog.DetailRateLimited("ownership.compat.blightedWorldHeart",
                    $"O11 compat: refused near-field claim of BlightedWorldHeart at {zdo.GetPosition()} -- its " +
                    "own kill-announce/loot-scaling reads the client-only Player list, empty headless; see " +
                    "docs/impl/OWNERSHIP-IMPL.md 'Proxima compatibility'.",
                    firstN: 5, windowSeconds: 60f);
                return true;
            }

            return false;
        }

        // --- Deferred, gated startup line (MAJOR FIX, lead review, logs finding) -----------------------------

        private static bool s_startupLineLogged;

        /// <summary>
        /// Same "defer from Init() to first real call" pattern <c>SetOwnerHoldGuard.Patch_ZDO_SetOwner_HoldGuard.Prefix</c>'s
        /// own <c>s_compatCheckLogged</c>/<c>LogForeignPatches</c> already uses, for the same reason: by the time
        /// <see cref="IsExcluded"/> is first actually called, this is genuinely happening on an authoritative,
        /// armed server (see that call site's own remarks) — never merely configured/compiled-in, which is all
        /// Init() time could ever honestly claim.
        /// </summary>
        private static void LogActiveOnce()
        {
            if (s_startupLineLogged) return;
            s_startupLineLogged = true;
            NoodlesLog.Verbose("CompatClaimExclusions: Proxima compat active -- Smelter/Fireplace excluded from " +
                "near-field claiming (Wonderland ProductionSupplyEngine), BlightedWorldHeart excluded (kill " +
                $"resolution), client-claim grace window {Mathf.Clamp(s_clientClaimGraceSeconds.Value, 3f, 10f):F1}s " +
                "(AzuCraftyBoxes/AzuAreaRepair). See docs/impl/OWNERSHIP-IMPL.md 'Proxima compatibility'.");
        }

        // === InClientClaimGrace: temporary, per-ZDO client-claim-burst grace ==================================

        private static ConfigEntry<float> s_clientClaimGraceSeconds;

        // Same shape as ZoneClaimSweep.ClaimGraceTracker: a plain Dictionary<ZDOID, float> of "last observed
        // client claim", swept opportunistically rather than on its own timer (CORE-API.md "don't start a
        // second periodic driver") -- entries are only ever meaningful for a few seconds; a stale one just
        // always reads "not in grace" forever after, harmless correctness-wise, only wasted memory.
        private static readonly Dictionary<ZDOID, float> s_lastClientClaimAt = new Dictionary<ZDOID, float>();

        private static readonly Counter s_clientClaimGraceHits =
            Telemetry.GetCounter("ownership.compat.clientClaimGraceHits");

        /// <summary>
        /// True while a client's own recent claim of this object (an interaction burst such as AzuCraftyBoxes
        /// crafting from nearby chests or AzuAreaRepair's area repair) must not be fought back. See
        /// <see cref="Patch_ZDO_SetOwner_ClientClaimGraceTracker"/> for how a claim is observed in the first
        /// place.
        /// </summary>
        internal static bool InClientClaimGrace(ZDO zdo)
        {
            if (zdo == null) return false;
            if (!s_lastClientClaimAt.TryGetValue(zdo.m_uid, out float claimedAt)) return false;

            float window = s_clientClaimGraceSeconds != null
                ? Mathf.Clamp(s_clientClaimGraceSeconds.Value, 3f, 10f)
                : DefaultClientClaimGraceSeconds;

            bool inGrace = (Time.time - claimedAt) < window;
            if (inGrace)
            {
                s_clientClaimGraceHits.Increment();
                NoodlesLog.DetailRateLimited("ownership.compat.clientClaimGrace",
                    $"O11 compat: held back a near-field re-claim of zdo {zdo.m_uid} at {zdo.GetPosition()} -- " +
                    $"claimed by a client {Time.time - claimedAt:F1}s ago (grace {window:F1}s); see " +
                    "docs/impl/OWNERSHIP-IMPL.md 'Proxima compatibility'.",
                    firstN: 10, windowSeconds: 60f);
            }
            return inGrace;
        }

        private const float DefaultClientClaimGraceSeconds = 5f;

        private static void RecordClientClaim(ZDOID id)
        {
            s_lastClientClaimAt[id] = Time.time;
            SweepStaleClaims();
        }

        // Unbounded-growth guard, same threshold/shape as ZoneClaimSweep.ClaimGraceTracker.SweepStale: cheap,
        // and only ever runs once the table has actually grown large enough to matter.
        private static void SweepStaleClaims()
        {
            if (s_lastClientClaimAt.Count < 4096) return;
            float cutoff = Time.time - 10f; // outside even the max configured (10s) window
            List<ZDOID> stale = null;
            foreach (var kv in s_lastClientClaimAt)
            {
                if (kv.Value < cutoff) (stale ??= new List<ZDOID>()).Add(kv.Key);
            }
            if (stale != null) foreach (var id in stale) s_lastClientClaimAt.Remove(id);
        }

        internal static void ClearClientClaimTracking() => s_lastClientClaimAt.Clear();

        /// <summary>
        /// A postfix, not a prefix -- this file only ever wants to know that a claim actually TOOK (never
        /// assume, per this module's own "postcondition check" convention, e.g. SetOwnerHoldGuard's
        /// AuthorizedSetOwner), and Harmony runs every registered postfix regardless of whether a sibling
        /// prefix on this exact method (SetOwnerHoldGuard's hold guard, NearFieldClaimVeto, GetOffMyLawn's or
        /// Wonderland's own third-party prefixes) vetoed the underlying write. <c>PatchSide.Both</c> +
        /// <c>ZNet.instance.IsServer()</c> runtime check, not <c>PatchSide.ServerOnly</c> -- this needs to work
        /// on a listen-server host too (the exact O1 lesson this codebase already documents in
        /// OWNERSHIP-IMPL.md: a listen-server host runs the CLIENT build/process even while genuinely acting as
        /// server, so a build-type-only gate under-applies there).
        /// </summary>
        [HarmonyPatch(typeof(ZDO), nameof(ZDO.SetOwner))]
        internal static class Patch_ZDO_SetOwner_ClientClaimGraceTracker
        {
            static void Postfix(ZDO __instance, long uid)
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                if (!Features_Ownership.OwnershipAuthority.Active) return; // nothing to grace-protect against otherwise -- cheap, correct narrowing (matches OwnershipLossWatch's own reasoning)
                if (__instance == null || uid == 0L || uid == ZDOMan.GetSessionID()) return; // only a genuine claim TO a real client matters here -- not a release, not a claim to the server itself

                try
                {
                    if (__instance.GetOwner() != uid) return; // vetoed by a sibling prefix (hold guard, third-party mod) -- not a real claim, nothing to track
                    RecordClientClaim(__instance.m_uid);
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_ZDO_SetOwner_ClientClaimGraceTracker), e);
                }
            }
        }

        // === Init ==============================================================================================

        [NoodlesModuleInit]
        internal static void Init()
        {
            ConfigFile config = NoodlesConfig.File;

            s_clientClaimGraceSeconds = config.Bind("OwnershipAuthority", "CompatClientClaimGraceSeconds", DefaultClientClaimGraceSeconds,
                new ConfigDescription(
                    "How long after a real connected client claims a ZDO the near-field claim sweep holds off " +
                    "re-claiming it (InClientClaimGrace) -- covers AzuCraftyBoxes' multi-container craft claims " +
                    "and AzuAreaRepair's multi-piece repair claims finishing without being fought back mid-burst. " +
                    "No vanilla equivalent. Same shape/range as OwnershipAuthority.ClaimGraceWindow (this " +
                    "module's own self-touch grace) for consistency -- both are 'don't immediately re-touch a " +
                    "just-changed ZDO' cooldowns of the same order of magnitude.",
                    new AcceptableValueRange<float>(3f, 10f)));

            PatchRegistry.Register("OwnershipAuthority", typeof(Patch_ZDO_SetOwner_ClientClaimGraceTracker), PatchSide.Both);

            // Shared-infra hygiene (CORE-API.md "Flush callbacks"): NOT registered against
            // Features_Ownership.OwnershipAuthority.OnForcedOff -- ZoneClaimSweep.Init already claims that
            // feature's one-callback slot (Features.cs: a second assignment is refused, logged, not thrown), and
            // this file's own tracking table is not a "hold" in the sense that rule protects against (nothing is
            // blocked/granted by it; a stale entry just always reads "not in grace" after 10s, never a data-loss
            // risk either way) -- so a global kill-switch flush is sufficient hygiene, not a per-feature release.
            KillSwitch.RegisterFlushCallback("Ownership.CompatClaimExclusions", ClearClientClaimTracking);

            // MAJOR FIX (lead review, logs finding): the "Proxima compat active" line used to log HERE,
            // unconditionally -- Init() runs from NoodlesPlugin.Awake() on every process alike (client included),
            // with no Side/ZNet/OwnershipAuthority.Active gate, so it printed a protective-effect claim that
            // could not possibly be true on that process yet (or ever, for a plain client). Deferred to
            // LogActiveOnce(), called from IsExcluded's first real invocation instead -- see that method's own
            // remarks for why that's the correct, honestly-gated place for this line.
        }
    }
}
