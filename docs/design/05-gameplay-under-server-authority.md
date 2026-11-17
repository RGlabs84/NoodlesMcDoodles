# Gameplay under server authority (Phase B)

Ground truth: `CLIENT:<line>` = `assembly_valheim.decompiled.cs`, `SERVER:<line>` = `assembly_valheim_SERVER.decompiled.cs` (both under `libs-Tools/1.0/DECOMPILED/`). Every citation below was re-read directly this pass, not copied blind from research. Where re-reading found something the research/lever catalog did not state, it is marked **NEW THIS PASS**.

## Summary

This module is what happens *inside* a zone once the server owns it. Ownership itself (who holds a ZDO, the `ReleaseZDOS` race, the hold/reclaim mechanism) is a different subsystem's job (`G3-L1`/`O6`/`O7`, the ownership blueprint); this module assumes that subsystem hands it two things — a zone the server currently owns, and a clean, simulator-initiated way to give an object back — and is responsible for everything gameplay-shaped that then runs on the server process instead of a player's: monster spawning, AI/pathfinding, combat stat bookkeeping, vehicles and containers, biome/wind for headless physics, a sweep of crash sites where vanilla code assumes a local player exists, production buildings, and structures.

**What it fixes for the Arizona case.** None of S1–S8 changes *who* owns a zone or *how fast* data reaches a peer — that is Phase A/the ownership+zonesim blueprints. What this module buys is that once those subsystems move a zone's ownership off the Arizona player and onto the server (or, later, onto O9's lowest-RTT peer), the zone keeps behaving correctly instead of silently breaking: mobs still spawn, AI still runs, doors still open, smelters still smelt, ships still sail with the right wind, and nothing about that transition can duplicate a player's items, drop their cargo, or delete a ZDO. Every guard in this document exists because the *decompiled code*, not intuition, shows a specific place where a naive "now the server just owns it" implementation would lose or duplicate world state. Without this module, server-owned zones are a **regression**: spawning silently stops working worldwide the instant any zone flips to server ownership (S1), and a handful of headless NREs can silently eat harvests or duplicate items (S6).

**Milestones.**
- **B1 — standalone correctness fixes.** No dependency on the ownership subsystem being live; each is dormant-safe if a zone never ends up server-owned, and several (S6 in particular) are a strict safety improvement over vanilla even before server-owned zones exist at all. Ships: S1, S3, S5, S6.
- **B2 — hand-over-invariant levers.** Require the ownership subsystem's "clean relinquish" contract (see Interactions) to be safe; correctness of the underlying vanilla routing is already confirmed, the risk is entirely in *how* ownership moves, which this module does not itself implement but must gate on. Ships: S2, S4, S7, S8 (correctness). **RED-TEAM FIX:** S2/S4/S7's hand-over-only invariant is now backed by a redesigned `HandoverGuard` (real hooks + a replicated, owner-agnostic `QuiescenceTracker` backstop, not the previous draft's local-only checks — see Hooks/H5/H6 and Red-team reconciliation); S8 (correctness)'s door/support *routing* remains fully confirmed safe as originally analyzed, but its one additional guard against a force-claim mid-`WearNTear.UpdateSupport()` recalculation is **deferred**, not solved — S8 (correctness) still ships in B2 on the strength of the ownership subsystem's clean-relinquish contract alone for that one race, the same dependency every other B2 lever already has, not on a guard this module provides.
- **B3 — new feature, off by default.** S8's auto-close-door add-on: not a vanilla-correctness fix, a new server-only behavior, shipped after B2 because it depends on the same zone-ownership machinery and the same ZDO-write path already proven safe by then.

## Levers covered

| lever | verdict (10-LEVERS, w/ LEAD-CORRECTED changes) | user target | recommended final value/range | milestone |
|---|---|---|---|---|
| S1 | SAFE-WITH-GUARDS — `SpawnSystem.UpdateSpawning` headless-fatal gate | none given | boolean gate (transpiler removes only the null-player operand); per-zone tick budget added as a guard | B1 (dormant until zonesim's per-peer instantiation hook is live) |
| S2 | SAFE-WITH-GUARDS — AI tick/NavMesh already owner-routed correctly | none given | no patch; hand-over-only invariant is mandatory | B2 |
| S3 | SAFE-WITH-GUARDS — combat latency fixed characteristic; optional stat-fix | none given | optional companion patch, recommended ON | B1 |
| S4 | SAFE-WITH-GUARDS — RequestControl/RequestOwn already correct; hand-back is dead code server-side | none given | in-use guard mandatory; hand-back implemented, dormant until zonesim per-peer instantiation hook is live | B2 |
| S5 | SAFE-WITH-GUARDS — `EnvMan.GetBiome` Meadows fallback | none given | postfix + **corrected scope** (see Hooks: `UpdateEnvironment` also needs the patch, not `GetBiome` alone — NEW THIS PASS) | B1 |
| S6 | SAFE-WITH-GUARDS — headless NRE sweep | none given | 5 guards, all mandatory and unconditional | B1 (ships earliest — see Interactions) |
| S7 | SAFE-WITH-GUARDS — production/timer objects, wall-clock design already lossless under clean handover | 3600f cap "freely tunable" | no patch to the timer design; hand-over-only invariant mandatory; 3600f left at vanilla default (pure pacing knob, not this doc's concern) | B2 |
| S8 (correctness) | SAFE-WITH-GUARDS — WearNTear/Door/TerrainComp already ownership-agnostic, targeted-RPC routed | none given | no patch; routing correctness confirmed unconditionally, mid-`UpdateSupport()` force-claim guard **deferred** (RED-TEAM FIX, see H7) | B2 |
| S8 (auto-close, new) | not in the lever catalog — user open question | "auto close doors? player only? is it safe?" | **yes, feasible; yes, player-placed-only; safe** — see Hooks/Data-loss analysis | B3 |

## Hooks

Every hook lives under `NoodlesMcDoodles.Gameplay.*` and is gated by its own `NoodlesMcDoodles.Core.Features.<Name>.Active` flag per the shared infrastructure contract. All are server-only except where noted; none require the client to run the mod, because every S-lever's vanilla routing already targets the ZDO's owner correctly — the client-side half of "both" in the lever table is just "this code path already runs correctly on whichever machine owns the object," not a new client hook.

### H0 — shared: lazy, dedicated-only patch application for H1/H3/H4 — Harmony **postfix** on `ZNet.Awake()`, always installed, both sides

**RED-TEAM FIX (Red Team 1, major #4).** The previous draft claimed "none of this module's patches are client-side" (Interactions § Other mods) and that H2 alone is gated on `IsServer()`. Re-reading H1 ("transpiler still applies... Harmony patches are static once loaded"), H3 ("`GetProxyCamera()` returns `Utils.GetMainCamera()` unconditionally" when off — the redirect is still installed) and H4 (patched "at Awake," and `Features.HeadlessNRESweep.Active` defaults `true`) confirms the claim was false as written: H1, H3 and H4's Harmony patches are applied on **every** process that loads the plugin, client included, and by default H4's five patches are *active* there too. This matters concretely on Proxima Maxi, which runs AzuAntiCheat 5.1.1 walking every installed mod's Harmony patches from the client's main thread on a 60s timer (confirmed separately, not part of this doc's scope) — every one of these installed-but-inert client patches is exactly the kind of surface that walk sees.

The fix is to stop applying H1/H3/H4's patches at plugin `Awake()` at all and instead apply them lazily, once, from a tiny postfix on `ZNet.Awake()` — confirmed present in both assemblies (`private void Awake()`, SERVER:79204, CLIENT:79346, inside the `ZNet` class starting SERVER:78896/CLIENT:79038) — gated on `ZNet.instance.IsDedicated()` (confirmed present in both assemblies: `public bool IsDedicated()`, SERVER:81194, CLIENT:81380). `ZNet.Awake` cannot fire before any RPC, spawn, or biome call could plausibly run (no session exists yet), so applying the patches there is not a race. This is a one-way `harmony.Patch(...)` call, never an unpatch, consistent with the kill-switch section's existing rule against unpatching/repatching at runtime; turning a feature off after this point still goes through each hook's own per-call flag read exactly as documented in H1/H3/H4, this just changes *whether the IL exists at all* on a listen-server host or a real player's client.

```csharp
[HarmonyPatch(typeof(ZNet), "Awake")]
static class Patch_ZNet_Awake_LazyServerOnlyPatches
{
    static bool s_applied;
    static void Postfix()
    {
        if (s_applied || !ZNet.instance.IsDedicated()) return;
        s_applied = true;
        NoodlesHarmony.Instance.PatchAll(typeof(Patch_SpawnSystem_UpdateSpawning).Assembly
            .GetTypes().Where(t => t.GetCustomAttribute<DedicatedOnlyPatchAttribute>() != null));
        // tags Patch_SpawnSystem_UpdateSpawning (H1), Patch_EnvMan_HeadlessCamera (H3),
        // and the five H4 transpiler classes; H2's own IsServer()-gated postfix is
        // harmless either way and is left on PatchAll's normal, always-applied path.
    }
}
```

After this fix, the only Harmony patch installed on **every** client, always, regardless of `IsDedicated()`, is this one tiny `ZNet.Awake` postfix itself — a single cheap, auditable, no-op-until-dedicated hook, not the four gameplay-shaped transpilers/prefixes it gates. The "Vanilla and version compatibility" and "Interactions with other subsystems" sections below are corrected to describe this accurately instead of claiming zero client-side footprint.

### H1 — S1: `SpawnSystem.UpdateSpawning()` — Harmony **transpiler**, server-only

`private void UpdateSpawning()` — SERVER:110686, CLIENT:111318. Same body shape in both (near-identical shared source; only the client additionally reads local UI state elsewhere in the class). The 3-term OR guard is confirmed byte-for-byte at SERVER:110686-110689 immediately inside the method:

```csharp
if (!m_nview.IsValid() || !m_nview.IsOwner() || Player.m_localPlayer == null)
{
    return;
}
```

**Why a transpiler and not a prefix.** A prefix can only skip the whole method or let it run; it cannot remove one operand from a 3-term boolean OR and keep the other two. `!m_nview.IsOwner()` must still gate (only the owning machine should spawn) and `!m_nview.IsValid()` must still gate (an unregistered view can't safely proceed). Only the `Player.m_localPlayer == null` term — true on every dedicated server process, permanently — needs to go.

**Why this hook and not another.** `SpawnSystem` is the only vanilla spawn driver; there is no lower-level "spawn one creature" API to hook instead, and rewriting spawn logic from scratch would duplicate `GetPlayersInZone`/`FindSpawnPoint`/biome-check logic that vanilla already gets right for every other purpose.

```csharp
[HarmonyPatch(typeof(SpawnSystem), "UpdateSpawning")]
static class Patch_SpawnSystem_UpdateSpawning
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instrs, ILGenerator il)
    {
        if (!Features.ServerSpawning.Active) return instrs; // read once at patch time isn't enough; see note below
        var matcher = new CodeMatcher(instrs, il)
            .MatchStartForward(
                new CodeMatch(OpCodes.Call, AccessTools.PropertyGetter(typeof(Player), nameof(Player.m_localPlayer))));
        if (!matcher.IsValid)
        {
            Log.Warning("S1: UpdateSpawning anchor not found; leaving method unpatched.");
            return instrs; // fail closed
        }
        // Remove the `Player.m_localPlayer == null` branch of the OR by
        // rewriting the boolean chain to short-circuit before it — the
        // real implementation walks the IL for the `brtrue`/`ceq` pair
        // following this token, per the CodeMatcher pattern, not just
        // the ldsfld itself.
        return matcher.InstructionEnumeration();
    }
}
```

Because a transpiler runs once, at patch-apply time (not per-call), the *feature flag* cannot be a plain `if` inside the transpiler and still support a live kill switch — see Data-loss analysis / kill-switch.

**RED-TEAM FIX (Red Team 1, major #3):** the previous draft of this section additionally described an "early-return prefix that skips straight past the patched logic when off," alongside the static-field mechanism below, without reconciling the two. That prefix reading is rejected outright, not merely clarified: a Harmony prefix can only skip or not skip the **whole method call**. `SpawnSystem.UpdateSpawning` is the same shared method on **every** client too (CLIENT:111318), and `Features.ServerSpawning.Active` is server-local, defaulting `false` and never synced (Configuration table) — it therefore reads `false` on every client, permanently, by design. A prefix that "skips straight past the patched logic when off" would skip the *entire method* on every client with the mod installed, silently breaking all local/vanilla monster spawning for every player who installs the mod, everywhere, including zones they legitimately own themselves — indistinguishable from a total spawning outage. That reading is now explicitly out of scope; the only implementation is the one below.

The concrete implementation ships **one IL body**: the transpiler unconditionally rewrites the boolean chain (the removed-operand IL is always present once Harmony loads, since patches are static), but the value it substitutes for the removed `Player.m_localPlayer == null` operand is read from a runtime static field (`Patch_SpawnSystem_UpdateSpawning.s_active`, itself just a cached copy of `Features.ServerSpawning.Active`, updated whenever the config value changes) **inside the small patched IL block itself**, so the flag is checked every call, not baked in at patch time, and the method's overall shape — three-term OR in, three-term OR out — is preserved byte-for-byte whether the extra term evaluates to "always false" (on) or "same as vanilla" (off). No prefix is added to this method at all.

**Mandatory co-guard (owned by this hook, enforced here):** per-zone `InvokeRepeating(1f)` tick stagger. `SpawnSystem` runs one independent `InvokeRepeating` per zone instance (SERVER:110680-110686) with no aggregate cap; a server holding many zones pays for all of them in one process. The mod maintains a shared `Features.ServerSpawning.MaxZonesScannedPerSecond` token bucket that `UpdateSpawning`'s prefix consults before letting the (patched) body run for a given zone this tick; zones over budget are skipped this tick and get their next opportunity next tick — never batched or force-run later, so no spawn window is ever silently doubled.

**Prerequisite, not owned by this hook:** `GetPlayersInZone`'s player list is empty until the zone-simulation subsystem's per-peer instantiation hook is active (its doc, not this one). Until then this patch is a **documented, intentional no-op** — the OR-gate removal fires, but the method's own `m_tempNearPlayers.Count == 0` early return (left untouched) then exits immediately, so nothing spawns and nothing is at risk.

**Flag off:** transpiler still applies (Harmony patches are static once loaded), but `s_active` reads `false`, so the patched IL's substituted operand evaluates the same way the removed `Player.m_localPlayer == null` operand always did — the three-term OR effectively behaves as vanilla's three-term OR again, in every zone, immediately, with no prefix, no early return, and no unpatch/repatch at runtime (unapplying/reapplying patches at runtime is itself a compat and thread-safety risk the shared infrastructure contract's kill-switch section warns against).

### H2 — S3: `Character.RPC_Damage(long, HitData)` — Harmony **postfix**, gated `IsServer()`

`private void RPC_Damage(long sender, HitData hit)` — SERVER:2413, CLIENT:2414. Confirmed identical ordering in both: the stat-credit branch runs **before** the owner gate:

```csharp
private void RPC_Damage(long sender, HitData hit)
{
    if (IsDebugFlying()) return;
    if (hit.GetAttacker() == Player.m_localPlayer)               // SERVER:2417-2420
    {
        Game.instance.IncrementPlayerStat(IsPlayer() ? PlayerStatType.PlayerHits : PlayerStatType.EnemyHits);
        m_localPlayerHasHit = true;
    }
    if (!m_nview.IsOwner()) return;                               // SERVER:2421-2424
    ...
}
```

`Player.m_localPlayer` is permanently `null` on a dedicated server, so `hit.GetAttacker() == Player.m_localPlayer` can never be true there — once a target is server-owned, this stat credit silently stops for every hit against it. No crash, no data loss, just `PlayerStatType.PlayerHits`/`EnemyHits` quietly going stale.

**Why a postfix, not a prefix or transpiler.** The fix is purely additive — derive the same credit from data the vanilla method already has (`hit.GetAttacker()`'s owning ZDOID) instead of the client-only singleton — so it does not need to intercept or alter vanilla's own (already-correct-when-not-server-owned) branch at all; a postfix that runs after vanilla's no-op branch and does the server-specific equivalent is the smallest correct hook.

```csharp
[HarmonyPatch(typeof(Character), "RPC_Damage")]
static class Patch_Character_RPC_Damage
{
    static void Postfix(Character __instance, HitData hit)
    {
        if (!Features.ServerCombatStats.Active) return;
        if (!ZNet.instance.IsServer()) return;
        // hit.GetAttacker() resolves to a ZDOID-backed Character reference
        // when the attacking Character's own ZDO is instantiated server-side
        // (zonesim prerequisite, same as S1/S2/S4/S7). Resolve to the
        // attacker's owning peer via ZDOMan, not Player.m_localPlayer.
        var attackerOwner = ZDOMan.instance.GetZDO(hit.m_attacker)?.GetOwner();
        if (attackerOwner is long uid && uid != 0)
        {
            Telemetry.Counters.CombatStatCreditsApplied.Increment();
            // credit routed to peer uid's own client-side stat tracker via
            // a namespaced RPC (server has no PlayerProfile worth writing to)
        }
    }
}
```

**Flag off:** postfix body returns immediately; behavior is byte-identical to unpatched vanilla (stats silently stop for server-owned targets, exactly as they do today with no mod installed at all).

### H3 — S5: `EnvMan.GetBiome()` **and** `EnvMan.UpdateEnvironment(long, BiomeSector)` — Harmony **transpiler** (redirect target), server-only — **scope corrected this pass**

`private BiomeSector GetBiome()` — SERVER:95904, CLIENT:96447. Confirmed fallback exactly as the lever catalog states:

```csharp
private BiomeSector GetBiome()
{
    Camera mainCamera = Utils.GetMainCamera();          // SERVER:95906
    if (mainCamera == null) return BiomeSector.EmptyMeadows;   // SERVER:95908
    ...
}
```

**NEW THIS PASS — the lever catalog's "postfix on `GetBiome`" is not sufficient by itself.** `private void UpdateEnvironment(long sec, BiomeSector biome)` — SERVER:95812, CLIENT:confirmed present at the corresponding offset — has its **own, independent** `Camera.main == null` early return, upstream of ever consuming the `biome` parameter for anything:

```csharp
private void UpdateEnvironment(long sec, BiomeSector biome)
{
    string environmentOverride = GetEnvironmentOverride();
    if (!string.IsNullOrEmpty(environmentOverride)) { ... return; }
    long num = sec / m_environmentDuration;
    Camera mainCamera = Utils.GetMainCamera();            // SERVER:95822
    if (mainCamera == null) { return; }                   // SERVER:95824-95825 — bails BEFORE QueueEnvironment
    ...
    QueueEnvironment(envSetup);                            // only reachable past the bail above
}
```

`EnvMan.UpdateWind` (SERVER:96444) only computes real wind when `GetCurrentEnvironment() != null` (SERVER:96454); `GetCurrentEnvironment()` is only ever populated by `QueueEnvironment`, which `UpdateEnvironment` never reaches headless because of its own camera check. **A postfix on `GetBiome` alone leaves wind permanently at its zero/default state on a dedicated server even with the biome fallback "fixed"** — the exact fidelity bug S5 exists to close would remain closed on paper and still broken for `Ship.GetSailForce` (SERVER:140670) and `Windmill.GetPowerOutput` (SERVER:150716), both of which read `EnvMan.instance.GetWindDir()`/`GetWindIntensity()`.

**Why a transpiler redirecting `Utils.GetMainCamera()`, not two separate prefixes reimplementing each method.** Both call sites fail for the identical reason (no `Camera.main` exists in batch mode) and both, once handed a non-null camera reference at the right position, run their own already-correct vanilla logic unmodified. Reimplementing either method's body (50+ lines each, `UpdateEnvironment` especially) as a full prefix-replacement is exactly the kind of large surface the "prefer smallest hook" instruction warns against, and it durably forks two more vanilla methods away from any future patch. Instead the mod keeps a single, disabled, `DontDestroyOnLoad`, never-rendering `Camera` component instance (`m_nview`-free, not tagged `MainCamera`, `enabled = false` so nothing in the render pipeline touches it) that it repositions once per environment tick to the nearest connected peer to whichever server-owned zone is being evaluated (or the world origin/`Game.m_worldLevel`-neutral default when no zone has a nearby peer), and transpiler-redirects the two specific `Utils.GetMainCamera()` call sites inside `GetBiome`/`UpdateEnvironment` to return that proxy instead of `Camera.main`. No other call site of `Utils.GetMainCamera()` anywhere else in the assembly is touched.

```csharp
[HarmonyPatch(typeof(EnvMan), "GetBiome")]
[HarmonyPatch(typeof(EnvMan), "UpdateEnvironment")]
static class Patch_EnvMan_HeadlessCamera
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instrs)
    {
        var getMainCamera = AccessTools.Method(typeof(Utils), nameof(Utils.GetMainCamera));
        var proxy = AccessTools.Method(typeof(HeadlessEnvProxy), nameof(HeadlessEnvProxy.GetProxyCamera));
        foreach (var ci in instrs)
        {
            if (ci.Calls(getMainCamera)) yield return new CodeInstruction(OpCodes.Call, proxy);
            else yield return ci;
        }
    }
}

static class HeadlessEnvProxy
{
    static Camera GetProxyCamera()
    {
        if (!Features.HeadlessBiomeWind.Active) return Utils.GetMainCamera(); // vanilla path when off
        if (!IsHeadlessServerSignal()) return Utils.GetMainCamera();          // listen-server host keeps a real camera
        return s_proxyCamera; // repositioned by a low-frequency updater, never null once Awake has run
    }

    static bool IsHeadlessServerSignal() =>
        ZNet.instance.IsServer() &&
        (Application.isBatchMode || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null);
}
```

**Guards (from the lever catalog, retained):** gate on `IsHeadlessServerSignal()` above, not a bare `Camera.main == null` check — any other installed mod instantiating even one `MainCamera`-tagged camera on the dedicated server process would otherwise silently re-enable vanilla's broken fallback with no error logged. The proxy-repositioning tick must stay allocation-free and cheap. A single process-global `EnvMan.instance` cannot be biome-correct for multiple simultaneously server-held zones in different biomes at once; "nearest connected peer to the zone being evaluated" is the best available proxy and is documented as imperfect, not claimed as exact.

**Flag off:** `GetProxyCamera()` returns `Utils.GetMainCamera()` unconditionally, i.e., exactly vanilla's `Camera.main` (null headless) — byte-identical to unpatched behavior.

### H4 — S6: headless NRE sweep — five independent guards, server-only, each mandatory and unconditional

All five share one rationale: an unguarded `Player.m_localPlayer` dereference either (a) aborts a method *after* it has already produced a real side effect but *before* it clears the state that prevents that side effect from repeating (duplication), or (b) aborts a method whose entire purpose is a broadcast to every connected client, silently before that broadcast is sent (not merely a server-local symptom). Every guard below is a **transpiler-inserted null check**, not a prefix, so a null `Player.m_localPlayer` short-circuits to the same value vanilla would have produced with a *non-crashing* fake local player (typically "attribute this to no one" / "treat as out of range"), preserving compatibility with any other mod that also patches these hot, commonly-touched methods.

**RED-TEAM FIX (Red Team 2, minor, mechanical).** Four line citations in guard #1 below and one in H5's `Vagon.m_container` evidence were off by 1–2 lines from hand-counting rather than grepping the exact anchor string; re-grepped and corrected in place this pass (`SERVER:122732→122733`, `122739→122740`, `122741→122742`, `122647→122649`, `147322→147323`), along with `Character.OnDeath`'s owner-gate citation in H6 (`3101→3102`) and `Door.CanInteract`'s citation in H8 (`123663-123667→123684-123687`). None of these changed any functional claim — every method/logic claim checked out semantically correct at the corrected line.

1. **`CookingStation.SpawnItem(string, int, Vector3, bool)`** — SERVER:122713, CLIENT:123629. Confirmed: `Instantiate`+`ItemDrop.OnCreateNew` run **before** the unguarded block:
   ```csharp
   GameObject obj = UnityEngine.Object.Instantiate(itemPrefab, vector, rotation);   // SERVER:122733
   ItemDrop component = obj.GetComponent<ItemDrop>();
   ItemDrop.OnCreateNew(component, cheated);
   ...
   if (m_recordCrafter)                                                             // SERVER:122740
   {
       component.m_itemData.m_crafterID = Player.m_localPlayer.GetPlayerID();       // SERVER:122742 — NRE here
       component.m_itemData.m_crafterName = Player.m_localPlayer.GetPlayerName();
   }
   ```
   and `RPC_RemoveDoneItem` (SERVER:122638) calls `SpawnItem` **in a loop, then clears the slot only after the loop returns**:
   ```csharp
   for (int j = 0; j < amount; j++) { SpawnItem(itemName, i, userPoint, cheated); }
   SetSlot(i, "", 0f, Status.NotDone, cheated: false);   // SERVER:122649 — never reached if SpawnItem throws
   ```
   If `m_recordCrafter` is ever true on a reachable prefab, the NRE fires *after* the real-Instantiate call, `SetSlot` never clears the slot, `IsItemDone` stays true, and the next retry (or a second `RPC_RemoveDoneItem` call, since nothing prevents a client resending it) re-enters the loop and Instantiates **another** copy of an already-produced item — unbounded duplication, not a missed craft. **Checked this pass and confirmed safe, not needing the same guard:** the trailing `Game.instance.GetPlayerProfile().IncrementStatItemCraft(...)` line reads `Game.m_playerProfile`, which is unconditionally constructed at `Game.Awake`-equivalent startup (SERVER:99923, `m_playerProfile = new PlayerProfile();`) on every process including the server — that call cannot NRE, so the guard is needed on the `m_recordCrafter` block only, not the whole method tail.
   ```csharp
   [HarmonyPatch(typeof(CookingStation), "SpawnItem")]
   static class Patch_CookingStation_SpawnItem
   {
       static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instrs)
       {
           if (!Features.HeadlessNRESweep.Active) return instrs;
           // insert `if (Player.m_localPlayer == null) goto skipCrafterBlock;`
           // immediately before the m_recordCrafter branch's first ldsfld.
           return NullGuardTranspilerUtil.SkipBlockIfLocalPlayerNull(instrs,
               enterMarker: AccessTools.Field(typeof(CookingStation), "m_recordCrafter"));
       }
   }
   ```
2. **`Pickable.RPC_Pick(long, int)`** — SERVER:71071, CLIENT:confirmed present at corresponding offset. `m_pickEffector.Create(basePos, Quaternion.identity, null, 1f, -1, Player.m_localPlayer.GetZDOID())` runs **before** the `Drop(...)` loop; an NRE here means the harvest is silently eaten with the client's optimistic pick flag already fired, not duplicated. Guard: skip only the effect-credit argument (substitute `ZDOID.None`), never skip the `Drop` loop itself.
3. **`Valkyrie.Awake()`** — SERVER:147765 (class at SERVER:147723), CLIENT:confirmed present. `m_targetPoint = Player.m_localPlayer.transform.position + ...` — SERVER:147779. Same pattern as #2; guarded defensively even though whether a freshly-spawned Valkyrie ZDO can ever be swept server-side is unresolved (open question, listed below) — the fix is free either way.
4. **`Leviathan.RPC_Left(long)`** — SERVER:132147, CLIENT:confirmed present. **NEW to the audit methodology, confirmed this pass:** invoked via `m_nview.InvokeRPC(ZNetView.Everybody, "RPC_Left")` (SERVER:132144). `ZRoutedRpc.InvokeRoutedRPC`'s self-invoke-before-route order (`if (targetPeerID==m_id||targetPeerID==0L) HandleRoutedRPC(...); if (targetPeerID!=m_id) RouteRPC(...);`, confirmed SERVER:83623-83635, `Everybody==0L` confirmed SERVER:82581/83544) means the **local handler runs first, unguarded, with no try/catch anywhere in the call chain** (confirmed SERVER:82884-82887, SERVER:83692-83700). `Utils.DistanceXZ(base.transform.position, Player.m_localPlayer.transform.position)` is the method's first statement — an NRE there aborts before `RouteRPC` ever runs, so the broadcast **never reaches any connected client**, not just a server-local symptom.
5. **`MusicVolume.RPC_PlayMusic(long)`** — SERVER:135059, CLIENT:confirmed present. Same `InvokeRPC(Everybody, ...)` shape, same self-invoke-before-route hazard. `Vector3.Distance(Player.m_localPlayer.transform.position, base.transform.position)` is the first statement (SERVER:135061) — an NRE there drops the broadcast to every peer **and** skips the trailing owner-gated `ZDOVars.s_plays` write, a state desync on top of the broadcast loss.

**Guard, generalized (mandatory methodology, not just these five):** any future audit must grep for every `InvokeRPC(ZNetView.Everybody, ...)` (or the untargeted string-overload, which resolves to `GetOwner()` — see S8) call site whose handler dereferences `Player.m_localPlayer` without a null check, because the self-invoke-before-route order makes an unguarded NRE there a **broadcast-drop bug for every connected peer**, not merely a server-side crash log. This pass found two (#4, #5) beyond the three the research doc listed on its first read of the same methodology — the sweep should be treated as not-yet-exhaustive.

**Flag off:** transpilers unpatched at plugin startup entirely (checked once, at Awake, not per-call, since these are cheap/rare methods where a static flag check per call is unnecessary) — vanilla's crash/duplication/broadcast-drop behavior returns exactly as today.

### H5 — S4: vehicle/container hand-over guard + ship hand-back — one shared guard function + one transpiler, server-only

**RED-TEAM FIX (Red Team 1, blocker #1 + major #2; Red Team 2, major #3).** Both red teams found real defects in the shared guard below, and re-reading the decompile confirms both, with one correction to Red Team 1's scope. The guard as previously drafted checked `Container.IsInUse()`/`Vagon.InUse()` — a field set **only** by `SetInUse(bool)`, itself gated `if (m_nview.IsOwner() && ...)` (SERVER:121988-121992, confirmed) — meaning a non-owner (the server, evaluating whether it may reclaim from a remote peer) always reads it as `false`/idle, the exact condition the guard exists to catch. **Confirmed identical structural gate for production** (`Smelter.UpdateSmelter`'s `if (!m_nview.IsOwner()) return;` immediately before the accumulator/`Spawn()` loop, SERVER:142309-142311) — this blind spot equally invalidates H6's production/creature checks below, which is why the fix here is shared with H6, not repeated. The naive "fix" of reading vanilla's own replicated `ZDOVars.s_inUse` field instead (which the old owner does keep updated, via `UpdateUseVisual()`, confirmed owner-gated write at SERVER:121867-121880) is **rejected**: nothing clears that field on an ungraceful disconnect (`RemoveOrphanNonPersistentZDOS`, SERVER:77453, only sweeps *non-persistent* ZDOs, and a placed container is persistent) — the Arizona admin's chest, opened when his connection drops, would read `s_inUse == 1` forever, and no new owner could ever be found safe to reclaim it, a permanent live-lock and a regression from vanilla.

**Correction to Red Team 1's scope: `Sadle` does not share this blind spot, contrary to the blocker's general framing (Red Team 1's own evidence already conceded this).** `Sadle.HaveValidUser()` (SERVER:18250) is not a sticky local flag; it is recomputed every `FixedUpdate` by `CalculateHaveValidUser()` (SERVER:18255-18270), which walks `ZNet.instance.GetAllCharacterZDOS()` — **every connected player's globally-replicated character ZDO**, not owner-local state — and checks distance. Any machine with the Sadle's GameObject instantiated computes the true answer, regardless of which machine owns the Sadle's ZDO. No fix needed here; kept as-is.

**Correction to Red Team 2's diagnosis, confirmed and fixed: the `Ship` branch's `m_players.Count > 0` check.** `Ship.m_players` (`private readonly List<Player> m_players`, SERVER:140294) is populated exclusively by `OnTriggerEnter`/`OnTriggerExit` (SERVER:140898-140922, confirmed) — plain Unity physics-collider callbacks with **no `IsOwner()` gate at all**. Once the zone-simulation subsystem's per-peer instantiation prerequisite is live, the server's own locally-instantiated `Ship` GameObject populates its own `m_players` correctly from real physics, independent of which machine owns the ZDO — so, unlike `Container`/`Smelter`, this check needed no redesign, only the reflection call the previous draft left as a comment instead of code:

```csharp
namespace NoodlesMcDoodles.Gameplay
{
    public static class HandoverGuard
    {
        // Called by the ownership subsystem before it ever calls ZDO.SetOwner()
        // on a claim/reclaim sweep. Returns true => safe to move; false => leave
        // it with its current owner this pass, try again next sweep.
        public static bool IsSafeToReclaim(ZDO zdo)
        {
            var prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
            if (prefab == null) return false; // Z3's missing-prefab guard applies transitively
            if (prefab.GetComponent<Container>() != null)
            {
                var live = ZNetScene.instance.FindInstance(zdo); // null if not locally instantiated
                if (live != null && live.GetComponent<Container>().IsInUse()) return false;      // SERVER:121983-121986, fast path when the server can see it
                var wagon = live?.GetComponent<Container>().GetComponentInParent<Vagon>();
                if (wagon != null && wagon.InUse()) return false;                                 // SERVER:147563-147570, same fast path
                if (!QuiescenceTracker.IsQuiescent(zdo, Features.HandoverGuard.QuiescenceSeconds)) return false; // mandatory backstop — see below
            }
            if (prefab.GetComponent<Sadle>() != null)
            {
                var live = ZNetScene.instance.FindInstance(zdo);
                if (live != null && live.GetComponent<Sadle>().HaveValidUser()) return false;      // SERVER:18250 — self-healing from replicated data, no backstop needed
            }
            if (prefab.GetComponent<Ship>() != null)
            {
                var live = ZNetScene.instance.FindInstance(zdo);
                var players = live != null
                    ? AccessTools.FieldRefAccess<Ship, List<Player>>(live.GetComponent<Ship>(), "m_players")
                    : null;
                if (players != null && players.Count > 0) return false;                            // SERVER:140294/140898-140922 — physics-populated, owner-agnostic
            }
            return true;
        }
    }
}
```

**The mandatory backstop: `QuiescenceTracker`, a replicated, self-expiring liveness signal — replaces the previous draft's `SimulationTracker`.** Instead of any local, owner-gated boolean, the guard samples `ZDO.DataRevision` (`public uint DataRevision { get; set; }`, SERVER:73541, bumped by the private `IncreaseDataRevision()` on every `ZDO.Set(...)` overload, confirmed SERVER:73879 and call sites throughout the `Set` overloads) — vanilla replication metadata the server already receives for any ZDO it tracks, from **any** owner, modded or vanilla, with no new wire format and no client cooperation required:

```csharp
public static class QuiescenceTracker
{
    // Server-process-local only, never saved, never sent — exactly like the
    // old SimulationTracker's stated nature, but now a cache of an
    // already-correct replicated signal instead of a guess.
    static readonly Dictionary<ZDOID, (uint revision, float sinceLocalTime)> s_seen = new();

    public static bool IsQuiescent(ZDO zdo, float quiescenceSeconds)
    {
        var id = zdo.m_uid;
        if (!s_seen.TryGetValue(id, out var last) || last.revision != zdo.DataRevision)
        {
            s_seen[id] = (zdo.DataRevision, Time.time);
            return false; // just changed (or first time seen this sweep) — treat as active
        }
        return Time.time - last.sinceLocalTime >= quiescenceSeconds;
    }
}
```

This fixes both findings at once: it works identically whether the current owner is the server itself, a modded peer, or a plain vanilla client (Blocker #1), and it is inherently self-expiring — a disconnected owner simply stops advancing `DataRevision`, so after `QuiescenceSeconds` (new config key, default below) the object is correctly judged idle with no sticky flag to live-lock on (Major #2). It is deliberately coarse (seconds, not the single-tick precision the old `SimulationTracker` implied) and is documented as a *backstop*, not a replacement for the fast-path `IsInUse()`/`HaveValidUser()` checks kept above wherever they're locally observable.

Evidence for the remaining checks: `Container.IsInUse()` — `public bool IsInUse() { return m_inUse; }`, SERVER:121983-121986. `Container.RPC_RequestOpen`/`OnContainerChanged`/`Save` — confirmed SERVER:122011-122177: opening grants transient ownership (`ZDOMan.instance.ForceSendZDO(uid, ...); m_nview.GetZDO().SetOwner(uid);`, SERVER:122029-122031) and the only persistence path (`OnContainerChanged → Save`, SERVER:122163-122177) is unconditionally gated `if (!m_loading && IsOwner()) { Save(); }` with **no non-owner fallback** — if the mod's own reclaim sweep ever calls `SetOwner(server)` on a container between a player's local inventory edit and their next `OnContainerChanged`, that pending write is silently never saved by anyone; the `QuiescenceTracker` backstop above is what actually closes this, not the fast-path check alone. `Vagon.m_container` is a direct public field reference (`public Container m_container;`, SERVER:147323) and `Vagon.InUse()` (SERVER:147563-147570) checks `m_container.IsInUse()` — confirming the container is a separate component the guard must check explicitly, not assume is covered by checking the Vagon's own ZDO alone. `Sadle.HaveValidUser()` — SERVER:18250. `Ship.UpdateOwner()` — SERVER:140858-140867, confirmed genuinely dead server-side:
```csharp
private void UpdateOwner()
{
    if (m_nview.IsValid() && m_nview.IsOwner() && !(Player.m_localPlayer == null)
        && m_players.Count > 0 && !IsPlayerInBoat(Player.m_localPlayer))   // permanently false: server-side Player.m_localPlayer == null
```

**Ship hand-back replacement — Harmony transpiler, same class of fix as H1.** Bypass only the `Player.m_localPlayer == null` operand, same anchored-`CodeMatcher`/fail-closed discipline as S1. Dormant (no-op) until the zone-simulation subsystem's per-peer instantiation hook populates `m_players` with real occupants via real `OnTriggerEnter`/`Exit` against real `Player` colliders server-side — documented explicitly, not assumed to "just work."

**Flag off:** `HandoverGuard.IsSafeToReclaim` is only ever consulted by the (separately flagged) ownership subsystem's reclaim sweep; with `Features.VehicleContainerHandover.Active == false` the ownership subsystem's own default behavior (whatever it does when this module isn't present) applies, and the `Ship.UpdateOwner` transpiler's own per-call flag check makes it fall through to vanilla's permanently-false condition.

### H6 — S7/S2 production & creature hand-over — no Harmony patch; consumes H5's contract

Confirmed the wall-clock/ZDO-persisted-timer design is lossless across a *clean* handover: `Smelter.UpdateSmelter()` (SERVER:142297-142352) places its owner gate (`if (!m_nview.IsOwner()) return;`) after the always-safe `UpdateRoof/UpdateSmoke/UpdateState` calls and before the accumulator loop, which calls `QueueProcessed → Spawn`, a real `Instantiate`-backed output path. `Character.OnDeath()`'s real owner gate is confirmed at SERVER:3102 (`if ((bool)m_nview && !m_nview.IsOwner()) return;`, inside SERVER:3078-3170 — **citation corrected this pass, was off by one line**) with `CharacterDrop.OnDeath` (SERVER:5270-5276) trusting that gate with none of its own. Both designs are correct **only if ownership is single-writer at every instant** — the owner gate assumes, but does not itself enforce, that no two machines believe `IsOwner()==true` at once. Because both output paths are `Instantiate`-backed, `DataRevision`-leader-wins (the field-level conflict resolution the ownership subsystem otherwise relies on) **cannot retroactively undo an already-executed duplicate spawn** — a force-claim mid-flight is a confirmed real-item-duplication path, not merely a desync.

This module's obligation is therefore: **never call this its own patch to fix** (the vanilla logic is already correct) but **assert, in the extended `HandoverGuard`, that no production ZDO or live creature ZDO is ever reclaimed except via the ownership subsystem's clean-relinquish contract** (request-and-wait-for-ack, or fence-old-owner-first — implemented by that subsystem, consumed here).

**RED-TEAM FIX (Red Team 2, blocker #1).** The previous draft named the write side of this check `MonoUpdaters.UpdateAI`/"`InvokeRepeating` callback entry" — neither exists as a hookable Harmony target. Confirmed: `MonoUpdaters` (`public class MonoUpdaters : MonoBehaviour`, SERVER:72157) has no method named `UpdateAI`; the only symbol by that name on the type is a call *through* the generic extension method `MonoUpdatersExtra.UpdateAI(this List<IUpdateAI>, ...)` (SERVER:72275), invoked as `m_ai.UpdateAI(BaseAI.Instances, ..., 0.05f)` from `MonoUpdaters.FixedUpdate` (SERVER:72200) — not itself a patchable per-creature hook. `InvokeRepeating` is a native Unity `MonoBehaviour` facility with no managed dispatcher in either assembly (`grep -n "void InvokeRepeating"` returns nothing in SERVER). `AccessTools.Method` would resolve null for either target and `Harmony.PatchAll()` throws at plugin load, taking down every hook in the plugin on both processes — exactly the class of risk this review flags.

The real, verified hooks: `BaseAI.UpdateAI(float dt)` (SERVER:26018) is `public virtual`, and its own first two statements are `if (!m_nview.IsValid()) return false;` then `if (!m_nview.IsOwner()) { m_alerted = ...; return false; }` — i.e. vanilla itself already only lets this method proceed past its own gate on the owning machine, mirroring `Smelter.UpdateSmelter`'s shape. Exactly two subclasses override it (confirmed: only two `override bool UpdateAI(float dt)` matches in the whole assembly, at SERVER:25649 and SERVER:27879), and both call `base.UpdateAI(dt)` as their own first statement — so a single Postfix on the base virtual method observes every creature's owner-gated tick, with no per-subclass patching needed (the standing audit instruction: re-verify this base-call invariant whenever a new `BaseAI` subclass is added, and patch that override directly if it ever doesn't call base). Production types don't share one entry point the way AI does — `Smelter` ticks via `InvokeRepeating("UpdateSmelter", 1f, 1f)` (SERVER:142000) while `Fermenter` uses `InvokeRepeating("SlowUpdate", ...)` (confirmed present, different method name) — so each production type's own named, owner-gated update method is patched individually, not through one generic dispatcher:

```csharp
[HarmonyPatch(typeof(BaseAI), "UpdateAI")]
static class Patch_BaseAI_UpdateAI_LivenessPostfix
{
    static void Postfix(BaseAI __instance, bool __result)
    {
        if (__result) LivenessBookkeeper.NoteLocalTick(__instance.GetZDOID()); // reached past the owner gate this call
    }
}

[HarmonyPatch(typeof(Smelter), "UpdateSmelter")]
static class Patch_Smelter_UpdateSmelter_LivenessPostfix
{
    static void Postfix(Smelter __instance)
    {
        if (__instance.GetZDO()?.IsOwner() == true) LivenessBookkeeper.NoteLocalTick(__instance.GetZDOID());
    }
}
// ...one further postfix per production type (Fermenter.SlowUpdate, etc.), same shape.
```

`LivenessBookkeeper` is purely local (per-ZDOID last-local-tick timestamp, server-process-only, never saved or sent) and answers "did *this* machine, which currently believes it owns this ZDO, actually tick it recently" — a real, always-correct signal, but only for the direction where the local process **is** the current owner (e.g. the server checking its own zones before offering one to O9). It cannot see a **remote** peer's ticks, which is the same blind spot Red Team 1's blocker #1 raised for H5's `Container`/`Smelter` checks. `HandoverGuard.IsSafeToReclaim` is therefore extended using the **same `QuiescenceTracker` backstop introduced in H5** (reading `ZDO.DataRevision`, SERVER:73541/73879 — vanilla-replicated, owner-agnostic, self-expiring), with `LivenessBookkeeper`'s local ticks used only as an optional, strictly-more-precise fast path when the observing machine happens to already know the answer:

```csharp
if (prefab.GetComponent<Smelter>() != null || prefab.GetComponent<Fermenter>() != null /* ...other production types */)
{
    if (LivenessBookkeeper.TickedRecently(zdo)) return false;                                       // fast path: server is the current owner and knows it's active
    if (!QuiescenceTracker.IsQuiescent(zdo, Features.HandoverGuard.QuiescenceSeconds)) return false;  // mandatory backstop: works for any owner
}
if (prefab.GetComponent<Character>() != null)
{
    if (LivenessBookkeeper.TickedRecently(zdo)) return false;
    if (!QuiescenceTracker.IsQuiescent(zdo, Features.HandoverGuard.QuiescenceSeconds)) return false;
}
```

**Flag off:** `LivenessBookkeeper`/`QuiescenceTracker` bookkeeping is cheap and always-on (read-only observation, no gameplay effect by itself); the *guard's use* of it by the reclaim sweep is what's gated, by the ownership subsystem's own flags, not this module's.

### H7 — S8 (correctness): structures — no Harmony patch; extends the same `HandoverGuard`

Confirmed no patch is needed for support routing or door routing: `WearNTear.UpdateSupport()`'s cross-piece RPC is targeted, not broadcast — `componentInParent2.m_nview.InvokeRPC(componentInParent2.m_nview.GetZDO().GetOwner(), "RPC_ClearCachedSupport")` (SERVER:149982-149992, confirmed exact this pass at SERVER:149983-149987), so the S6-style self-invoke-before-route hazard does not apply here. `Door` (class body SERVER:123606-123836, confirmed zero `IsOwner()` calls) routes `RPC_UseDoor` via `m_nview.InvokeRPC("UseDoor", flag)` (SERVER:123772-ish, confirmed at the `Open(Vector3)` method), the untargeted string overload which itself resolves to `ZRoutedRpc.instance.InvokeRoutedRPC(m_zdo.GetOwner(), ...)` — targeted to the ZDO's current owner, correctly, regardless of which machine that owner is.

The one real risk (confirmed this pass, SERVER:149920-149987): `WearNTear.UpdateSupport()` computes support **live from each machine's own local neighbor snapshot**, not from stored state. If a piece is force-claimed mid-computation, one machine can decide to collapse it (destroying it, dropping its own copy of debris/loot) while the other machine has not yet seen that decision — a genuine destroy-with-drops duplication race, not a self-correcting desync.

**Downgraded this pass (follows from Red Team 2, blocker #1, applied by extension).** The previous draft extended `HandoverGuard` with `SimulationTracker.IsMidSupportRecalculation(zdo)` — the same fictional tracker Red Team 2 disproved for H6. Unlike H6's guards, this one is **not** a good fit for the `QuiescenceTracker`/`LivenessBookkeeper` fix introduced there: `UpdateSupport()`'s hazard window is a single method call (sub-second, possibly sub-frame), while `QuiescenceTracker`'s `DataRevision`-sampling is deliberately coarse (seconds) to avoid false-blocking a genuinely idle object on ordinary replication jitter — it cannot resolve a race this tight, and no ZDO-replicated signal fine-grained enough to catch it has been identified in this pass. This guard is therefore **deferred**, not fixed: H7's other findings (targeted, owner-agnostic RPC routing for `WearNTear`/`Door`, confirmed above) stand, but the specific "force-claim mid-`UpdateSupport()`" duplication path remains open and is **not yet closed by any mechanism in this document**. Practically, it is bounded today by the same single-writer assumption the Interactions section already flags as this module's most load-bearing external dependency: as long as the ownership subsystem's clean-relinquish contract (ask-and-wait-for-ack, or fence-old-owner-first) is the *only* way `SetOwner` is ever called on a `WearNTear` piece — never a bare force-claim — the race cannot occur, because the old owner is never mid-`UpdateSupport()` when the new owner starts. This makes S8 (correctness)'s claim of full correctness on its own merits **provisional**, gated even harder than S2/S4/S7 on that subsystem never taking a force-claim shortcut for this component type; see Red-team reconciliation and Open questions.

```csharp
if (prefab.GetComponent<WearNTear>() != null)
{
    // No independent guard here yet — see the deferred note above. This branch
    // intentionally falls through to `return true` (relies entirely on the
    // ownership subsystem's clean-relinquish contract, not on this module).
}
```

and the audit methodology gains one more standing instruction: grep for every `m_nview.InvokeRPC(...)` call site whose invoked RPC has **no** `IsOwner()`/`IsValid()` check of its own (the `Door` pattern) in addition to grepping for explicit self-checks, so a future Door-shaped structure relying purely on RPC-routing semantics isn't silently missed by an audit that only looks for the more common explicit-gate pattern.

### H8 — S8 (new): server-side door auto-close — no Harmony patch at all; a periodic `ZDOMan` scan

The user's question: *can the server auto-close doors, should it be player-placed only, and is it safe?* **Yes, feasible; yes, player-placed-only, and it can be made safe** — with a design smaller than the lever catalog's own S8 entry anticipated, because it turns out this feature needs **no live GameObject at all**, only ZDO fields the server already has once it owns the door.

`Door.RPC_UseDoor(long uid, bool forward)` (SERVER:123772-ish, confirmed body) writes state purely at the ZDO level:
```csharp
private void RPC_UseDoor(long uid, bool forward)
{
    if (!CanInteract()) return;
    if (m_nview.GetZDO().GetInt(ZDOVars.s_state) == 0) { m_nview.GetZDO().Set(ZDOVars.s_state, forward ? 1 : -1); }
    else { m_nview.GetZDO().Set(ZDOVars.s_state, 0); }
    UpdateState();
}
```
`ZDOVars.s_state == 0` means closed; nonzero means open (direction-signed). `Piece.m_creator` — the field vanilla's own placement code sets — is itself ZDO-persisted: `m_creator = m_nview.GetZDO().GetLong(ZDOVars.s_creator, 0L)` (SERVER:136141) and `SetCreator(long uid, ...)` (SERVER:136308-136317) is called from exactly one place, the hammer-placement path (SERVER:12503-12506, `GetComponent<Piece>()?.SetCreator(GetPlayerID(), ...)`, run when a player finishes placing a piece). A door that is part of a Location's pre-authored prefab set (dungeon rooms, etc.) is never placed through that path, so its ZDO's `ZDOVars.s_creator` is never set — it reads back `0L`, the same default a freshly-instantiated ZDO starts with. **This is the exact, confirmed signal for "player-placed": `zdo.GetLong(ZDOVars.s_creator, 0L) != 0L`.**

Because both `ZDOVars.s_state` and `ZDOVars.s_creator` are plain ZDO fields, the mod can decide to close a door **the server currently owns without that door's GameObject being instantiated anywhere on the server at all** — the write goes through the normal `ZDO.Set`/replication path exactly like any other server-authored field change, and every peer's own `Door.UpdateState()` (already `InvokeRepeating(0.2s)` polling `DataRevision`, SERVER:123657) picks it up and animates it locally, unmodified.

```csharp
public sealed class DoorAutoCloseScanner : MonoBehaviour // server-only singleton
{
    void FixedUpdate() // gated internally to ScanIntervalSeconds, not every frame
    {
        if (!Features.DoorAutoClose.Active) { s_openSince.Clear(); return; }
        if (!ZNet.instance.IsServer()) return;
        foreach (var zdo in ZDOMan.instance.GetZDOsInZoneOwnedByServer(...)) // ownership subsystem's own enumerator
        {
            var prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
            var door = prefab != null ? prefab.GetComponent<Door>() : null;
            if (door == null || door.m_canNotBeClosed || door.m_keyItem != null) continue; // prefab defaults, read once from the asset
            if (zdo.GetLong(ZDOVars.s_creator, 0L) == 0L) continue;               // NOT player-placed — never touch
            int state = zdo.GetInt(ZDOVars.s_state, 0);
            if (state == 0) { s_openSince.Remove(zdo.m_uid); continue; }          // already closed
            if (!s_openSince.TryGetValue(zdo.m_uid, out var since))
            { s_openSince[zdo.m_uid] = Time.time; continue; }
            if (Time.time - since < Features.DoorAutoClose.IdleSeconds) continue;
            zdo.Set(ZDOVars.s_state, 0);                                          // same write RPC_UseDoor makes
            s_openSince.Remove(zdo.m_uid);
            Telemetry.Counters.DoorAutoClosed.Increment();
        }
    }
}
```

**Guards.** (1) `zdo.GetLong(ZDOVars.s_creator, 0L) != 0L` is a hard, non-configurable requirement — not a config toggle — because it is the only confirmed signal distinguishing a player's own door from a Location/dungeon door, and the consequence of getting it wrong (auto-closing a dungeon door) is a much worse player experience than the consequence of getting it right being occasionally too conservative. (2) `door.m_canNotBeClosed` and **(RED-TEAM FIX, Red Team 2 minor) `door.m_keyItem != null`** are both skipped — confirmed by `Door.CanInteract()` itself (`private bool CanInteract()`, SERVER:123684; `if ((m_keyItem != null || m_canNotBeClosed) && m_nview.GetZDO().GetInt(ZDOVars.s_state) != 0) return false;`, SERVER:123685-123687 — **citation corrected this pass**, treats a keyed door identically to a `m_canNotBeClosed` door for a currently-open door: neither can be closed via the normal `RPC_UseDoor` path in that state, so a player standing at a keyed door they themselves placed could not close it that way either, and the scanner must not bypass that via a direct ZDO write). (3) The scan only ever touches doors the *server itself currently owns* (via the ownership subsystem's own enumerator — this module does not walk all ZDOs, only the server's own held set), so it can never write a ZDO another machine is actively simulating. (4) Off by default; even when on, `IdleSeconds` defaults conservatively (recommend 300s) so it reads as "tidying an abandoned base," not "doors slam behind you."

**Is it safe?** Yes, with the guards above, for the same reason the underlying S8 correctness lever is safe: the write is a single `ZDOVars.s_state` field set through the exact same replication path `RPC_UseDoor` already uses, on a ZDO the server already owns, with no inventory, no Instantiate, no drops, and no other component involved. The only genuinely new risk introduced — closing a door on a player mid-walk-through — is a **UX** hazard (identical in kind to another *player* closing a door on you, which vanilla already allows today), not a *data-loss* hazard; nothing about it can lose, duplicate, or corrupt a ZDO, an inventory, or a save.

**Flag off:** `s_openSince` is cleared and the scanner's body returns immediately; zero ZDO writes originate from this file.

## Data-loss analysis

| Path | Mechanism | Guard |
|---|---|---|
| Duplicate mob spawns | S1's SpawnSystem ZDO flaps ownership every ~2s without the ownership subsystem's `ReleaseZDOS` patch, letting the spawn loop run on alternating machines for the same real-time window | Hard prerequisite on the ownership subsystem's patch (not owned by this doc); this module's own per-zone tick budget (H1) additionally prevents unbounded concentration once that prerequisite holds |
| Duplicate loot on creature death | Force-claiming a live creature mid-`OnDeath` race lets both the old and new believed-owner pass `Character.OnDeath`'s gate and both run `CharacterDrop.OnDeath → DropItems`, each independently `Instantiate`-ing loot | **RED-TEAM FIX:** `HandoverGuard` extended with `LivenessBookkeeper` (local fast path, real `BaseAI.UpdateAI` postfix) + `QuiescenceTracker` (mandatory, owner-agnostic `ZDO.DataRevision`-based backstop) (H6) — reclaim only via clean, simulator-initiated relinquish, never force-claim |
| Duplicate production output | Same mechanism as above, for `Smelter.UpdateSmelter`'s `Instantiate`-backed `Spawn()` — `DataRevision`-leader-wins only resolves the *field* race, not an already-executed `Instantiate` | **RED-TEAM FIX:** same `HandoverGuard`/`LivenessBookkeeper`/`QuiescenceTracker` check, extended to production types via per-type postfixes (H6) |
| Silently-lost pending inventory edit | Reclaiming a `Container`'s ZDO between a player's local inventory removal and their next `OnContainerChanged → Save()` leaves that edit unsaved on every machine, since `Save()` is unconditionally `IsOwner()`-gated with no fallback | **RED-TEAM FIX:** `HandoverGuard.IsSafeToReclaim` checks the local `Container.IsInUse()`/`Vagon.InUse()`/`Sadle.HaveValidUser()`/`Ship`'s physics-populated `m_players.Count>0` fast paths where locally observable, **plus the mandatory `QuiescenceTracker` backstop** for `Container`/`Vagon` so a remote (possibly vanilla) owner's pending edit is still protected even though the server can't see its local `IsInUse()` state (H5) |
| Destroy-with-drops duplication | Force-claiming a `WearNTear` piece mid-`UpdateSupport()` recalculation lets one machine decide to collapse (destroy + drop) before the other has seen that decision | **Deferred, not fixed** — no ZDO-replicated signal fine-grained enough for this sub-second race has been identified; bounded only by the ownership subsystem never force-claiming this component type outside its clean-relinquish contract (H7) |
| Unbounded item duplication via `CookingStation` | Unguarded `m_recordCrafter` NRE aborts `RPC_RemoveDoneItem` before `SetSlot` clears the slot, so every retry re-`Instantiate`s another copy | Mandatory, unconditional null-guard transpiler (H4.1) |
| Silently dropped `Everybody` broadcasts | `Leviathan.RPC_Left`/`MusicVolume.RPC_PlayMusic` NRE before `RouteRPC` runs, per the self-invoke-before-route order — the broadcast never reaches any peer | Null-guard transpilers (H4.4, H4.5) |
| Silently eaten harvest | `Pickable.RPC_Pick`/`Valkyrie.Awake` NRE before the drop/placement logic runs, with the client's optimistic flag already fired | Null-guard transpilers (H4.2, H4.3) |
| Server-side stat tracking silently stale | `RPC_Damage`'s `Player.m_localPlayer` credit check is permanently false server-side | Optional, additive companion patch (H2) — cosmetic only, not a data-loss path, listed for completeness |
| Door auto-close closing a non-player door | Would only happen if the `ZDOVars.s_creator` check were skipped or bypassed | Non-configurable hard check (H8) — no config key can disable this specific guard |
| Door auto-close writing a ZDO mid-handover | The scan only enumerates ZDOs the server currently, actually owns (via the ownership subsystem's own set) | Same ownership boundary every other server-authored write in this module already respects |

**Crash.** Every hook here either (a) reads state and writes nothing (H4's guards are pure crash-prevention; a crash *without* them is strictly worse than one *with* them, since the guard converts a duplication-risking abort into a clean no-op) or (b) writes a single ZDO field through the existing replication path (H8, and indirectly H1/H5/H6/H7 via the ownership subsystem's own `SetOwner`/production calls). A process crash mid-write loses at most the in-flight field write, identical in kind to vanilla's own exposure to a crash mid-`ZDO.Set` — nothing here adds a new crash-time data-loss window.

**Disconnect / reconnect.** None of these hooks hold any state keyed to a specific connection; `LivenessBookkeeper`, `QuiescenceTracker`, and `s_openSince` are purely local, in-memory, server-process bookkeeping (never saved, never sent to a peer) that self-heals on the next scan tick regardless of who connects or disconnects meanwhile. **RED-TEAM FIX (Red Team 1, major #2):** this self-healing property is why `QuiescenceTracker` reads `ZDO.DataRevision` rather than any sticky owner-written boolean — a disconnecting player mid-interaction simply stops advancing `DataRevision` on the objects they were using, so after `Features.HandoverGuard.QuiescenceSeconds` elapses the reclaim sweep can pick them up on its own schedule like any other disconnect-orphaned ZDO, with a bounded wait rather than the permanent live-lock a raw `ZDOVars.s_inUse` read would have produced.

**Save during handover.** All ZDO writes in this module (H1's spawn timestamps, H2's telemetry-only, H8's `s_state`) go through the normal `ZDO.Set` path that `ZDOMan.Save` already reads unconditionally from live memory — there is no separate "pending write" buffer in this module that a mid-handover save could miss. A save that lands between a `SetOwner` and this module's next tick simply captures whichever owner was current at that instant, exactly like vanilla.

**Kill switch mid-operation.** Every Harmony patch and every periodic scan in this module reads its `Features.<Name>.Active` flag on every call/tick, never once at patch-apply time. **RED-TEAM FIX (Red Team 1, major #3):** H1's transpiler is the one structural exception, and the exception is now narrower and unambiguous — the *IL* is always present once loaded (once H0's lazy, `IsDedicated()`-gated apply has fired), and a runtime-read static field inside it gates the actual behavior change; there is no early-return prefix of any kind on this method, so flipping the flag off takes effect on the very next call with no re-patch and no risk of skipping the whole method. Turning a flag off mid-operation therefore: stops H1 from bypassing the null-player gate (spawning reverts to vanilla's broken-but-safe "never spawns" state, not a half-spawn); stops H8's scanner from writing any further `s_state` changes (in-flight opens/closes already committed stay as they are — a door left open is not a data-loss state, just an open door); and, most importantly, does **not** by itself un-stick an object `HandoverGuard` is currently holding — turning `Features.VehicleContainerHandover.Active` off must also, per the coupled-lever note in the research (`P4 + G6-N3`), flush every currently-tracked hold in `HandoverGuard`/`LivenessBookkeeper`/`QuiescenceTracker` so a mid-flip disable can't leave an object permanently unreclaim-able; this module's kill-switch hook clears `s_openSince` and every `LivenessBookkeeper`/`QuiescenceTracker` table on disable, matching that requirement.

## Vanilla and version compatibility

**Vanilla client + modded server.** Every hook in this module runs its *behavior* server-only (H1, H3, H4, H8) or on a server-side-only branch of a shared-body method that a vanilla client never executes differently (H2's postfix is gated `ZNet.instance.IsServer()`; a vanilla client's own `RPC_Damage` call is untouched). `HandoverGuard`/`LivenessBookkeeper`/`QuiescenceTracker` (H5–H7) are pure server-side bookkeeping consulted by the (separate) ownership subsystem — a vanilla client sees only the *effects* (an object stays with its current owner a little longer than a naive implementation would), never a protocol difference. No new RPC is registered by this module; H8's door writes go through the exact `ZDOVars.s_state` field and replication path vanilla's own `RPC_UseDoor` already produces, so a vanilla client's `Door.UpdateState()` applies it exactly as if another player had closed the door.

**RED-TEAM FIX (Red Team 1, major #4).** The previous draft of this document and of the Interactions § Other mods entry below claimed this module's client-side *footprint* was zero. That was false on the document's own terms: H1's and H3's transpilers, and by default H4's five transpilers (`Features.HeadlessNRESweep.Active` defaults `true`), were previously applied at plugin `Awake()` unconditionally, meaning their IL was installed on **every** process that loads the plugin, client included — a real, walkable Harmony-patch surface on a player's own machine, not merely an "effect the client only sees" as claimed. This is now fixed structurally by H0: H1/H3/H4's patches are applied lazily from a postfix on `ZNet.Awake()`, gated on `ZNet.instance.IsDedicated()`, so on a real player's client (including a listen-server host, who is a real player, not a dedicated process) those four patches are never installed at all. The only patch a client ever has installed, unconditionally, is H0's own tiny `ZNet.Awake` postfix and H2's already-`IsServer()`-gated postfix (installed but inert on a client). This is now an accurate, not aspirational, zero-added-surface claim for the gameplay-shaped hooks, and a small, named, auditable exception for H0/H2 themselves.

**Modded client + vanilla server.** None of S1–S8 requires the client to run the mod at all (the lever table's "clients need mod?" column is `no` for every one of S1–S8) — every fix here is either server-only or activates identically regardless of which machine the client happens to be. Running this module's client build against a vanilla server is a pure no-op: `Features.*.Active` flags relevant to this module default to reading `ZNet.instance.IsServer()` first and short-circuiting off the client, so a modded client talking to a vanilla server behaves exactly as an unmodded client would for every hook in this document.

**Modded version skew.** This module adds no new RPC, no new wire format, and no new ZDO var beyond ones vanilla already defines (`ZDOVars.s_state`, `ZDOVars.s_creator`) — there is no version-skew surface to negotiate for S1–S8 specifically. The one place a future version-skew question could arise is H2's stat-credit companion RPC (if implemented as a routed RPC to the attacking peer rather than left server-local-only) — that RPC must go through the shared infrastructure's namespaced-RPC handshake (per-peer capability check) exactly like any other new RPC the mod adds elsewhere, and is documented here only as a dependency, not re-specified.

**PlayFab/crossplay sockets.** Nothing in this module inspects or depends on which `ISocket` implementation a peer uses — every hook operates at the ZDO/GameObject level, below the transport. A PlayFab/crossplay peer sees identical behavior to a Steam peer for every S1–S8 hook.

## Configuration

All keys below are server-authoritative BepInEx config, on the server side, pushed to modded clients over the shared infrastructure's negotiated RPC only where a client-visible effect exists (marked); a vanilla or non-negotiated modded client simply never receives them and experiences server-only default behavior.

| Key | Default | Range | Synced? | Notes |
|---|---|---|---|---|
| `Features.ServerSpawning.Active` | `false` | bool | server-local | S1 master flag |
| `Features.ServerSpawning.MaxZonesScannedPerSecond` | `8` | 1–64 | server-local | H1's per-tick stagger budget |
| `Features.ServerCombatStats.Active` | `false` | bool | server-local | S3, purely cosmetic when off |
| `Features.HeadlessBiomeWind.Active` | `false` | bool | server-local | S5 |
| `Features.HeadlessNRESweep.Active` | `true` | bool | server-local | S6 — recommended default ON; strictly protective, never changes gameplay, only prevents a crash/dup path that can occur even before this mod's other features are enabled |
| `Features.VehicleContainerHandover.Active` | `false` | bool | server-local | S4's `HandoverGuard` participation |
| `Features.HandoverGuard.QuiescenceSeconds` | `5` | 2–60 | server-local | **RED-TEAM FIX (new key):** `QuiescenceTracker`'s `ZDO.DataRevision`-idle window for `Container`/`Vagon` (H5) and production/creature (H6) reclaim; must exceed the widest normal per-tick write gap for any guarded type (Smelter's 1s accumulator is the tightest known case) with margin — needs live-measurement tuning before B2 ships, same open-question class as O3 |
| `Features.ProductionHandoverGuard.Active` | `false` | bool | server-local | S7's `HandoverGuard`/`LivenessBookkeeper`/`QuiescenceTracker` participation |
| `Features.StructureHandoverGuard.Active` | `false` | bool | server-local | S8 correctness — door/support routing only; the `WearNTear` mid-`UpdateSupport()` guard is deferred (H7), not gated by this key alone |
| `Features.DoorAutoClose.Active` | `false` | bool | server-local | S8 new feature (B3) |
| `Features.DoorAutoClose.IdleSeconds` | `300` | 30–3600 | server-local | H8 |
| `Features.DoorAutoClose.ScanIntervalSeconds` | `5` | 1–60 | server-local | H8, scan cadence not tied to any replication-budget lever |

None of these are marked client-synced: every effect in this module is either invisible to the client (it only ever sees the *result* — an object's owner, a ZDO field — never a protocol difference) or is the kind of server-policy value (like door auto-close timing) the shared infrastructure contract's "server-authoritative values pushed to modded clients" clause exists for only when a client needs to *react* to the value locally, which none of S1–S8 requires.

## Telemetry

Per `NoodlesMcDoodles.Core.Telemetry`, all counters below are periodic-logged and per-peer where a peer is meaningfully involved (marked); all others are process-global.

- `ServerSpawning.ZonesScannedPerTick`, `ServerSpawning.SpawnsProduced` — proves S1 is doing work, and (compared against a manual spawn-window audit during the B1 live test) proves it isn't double-spawning across the old 2s flap boundary.
- `CombatStats.CreditsApplied` (per-peer) — proves S3's companion patch is restoring stat credit for server-owned-target hits.
- `HeadlessBiomeWind.ProxyRepositions`, `HeadlessBiomeWind.FallbackToVanillaCount` — the second should read 0 whenever the headless signal is expected true; a nonzero count means another mod defeated the detection (the exact failure mode the guard exists to catch).
- `NRESweep.Guarded.CookingStation`, `.Pickable`, `.Valkyrie`, `.LeviathanLeft`, `.MusicVolumePlay` — each increments only when the guard actually caught a would-be NRE; a nonzero count is direct, in-the-wild proof the hazard is real on this server's content, not merely a theoretical decompile finding.
- `HandoverGuard.ReclaimBlocked.InUseContainer/Vagon/Sadle/Ship` — proves the in-use guard is actually preventing reclaims that would otherwise race a player's pending inventory edit; a corresponding **client-side** `Container` save-failure counter (if the smoothing/telemetry module exposes one) crossed against this at 0 unexpected saves-lost is the strongest available zero-data-loss evidence for S4. **RED-TEAM FIX:** split further into `.InUseFastPath` (local `IsInUse()`/`HaveValidUser()`/`m_players` check fired) vs. `.QuiescenceBackstop` (only the `DataRevision`-based backstop caught it) — a nonzero `.QuiescenceBackstop` count is the concrete, in-the-wild proof that Red Team 1's blocker (a remote, non-modded owner's activity is invisible to the local fast path) is a real, observed condition on this server, not merely a theoretical decompile finding.
- `HandoverGuard.ReclaimBlocked.ActiveSimulation` (creatures/production, split `.LocalTick` vs `.QuiescenceBackstop` the same way) — same proof pattern for S2/S7. **`HandoverGuard.ReclaimBlocked.MidSupportRecalc` is removed** (S8's structure guard for this race is deferred, H7 — there is nothing yet to increment it).
- `HandoverGuard.DoubleDropDetected` — an assertion counter: increments only if, despite every guard, `CharacterDrop.OnDeath`/`Smelter.QueueProcessed`/WearNTear's collapse path is ever observed running twice for the same ZDOID within one tick window. Should read 0 for the life of the server; any nonzero value is treated as a guard failure requiring investigation, not a tunable.
- `DoorAutoClose.Closed`, `.SkippedNotPlayerPlaced`, `.SkippedCanNotBeClosed`, `.SkippedOwnershipBoundary` — the second counter in particular is the direct, ongoing proof that the player-placed-only guard is actually filtering dungeon doors, not merely coded correctly.

## Interactions with other subsystems

- **Ownership subsystem (`G3-L1`/`O6`/`O7`, doc03).** Hard prerequisite for every hook in this module except H4 (NRE sweep, which is independently useful — see B1 rationale). **RED-TEAM FIX (Red Team 2, major #2).** The previous draft claimed "this module never itself calls `ZDO.SetOwner` for a claim/reclaim." That is false for one hook: H5's Ship hand-back transpiler exists specifically to unblock `Ship.UpdateOwner()`'s vanilla body, whose last statement is `m_nview.GetZDO().SetOwner(newOwnerID);` (SERVER:140858-140867, confirmed) — once the zone-sim prerequisite is live, that call becomes real and is made directly by vanilla code this module has deliberately re-enabled, not by the ownership subsystem. This matters because two of the user's own other mods already hold skip-capable Harmony prefixes on `ZDO.SetOwner` (`Wubarrk-GetOffMyLawn`'s `OwnershipHold`/`SetOwnerGuard`, `Wubarrk-Wonderland`'s `ItemFlow.ZdoSetOwnerPatch` — both SERVER-only, confirmed in `research/13-mod-compat-census.md` §1/table row `ZDO.SetOwner`) whose Harmony priority could silently short-circuit this call with no error and no telemetry catching it, since the previous design assumed this call path didn't exist. Fix: the Ship hand-back transpiler must declare an explicit Harmony priority relative to those two prefixes (not default), and H5's telemetry must read `zdo.GetOwner()` immediately after this specific call to confirm it actually changed, rather than assuming `SetOwner` having been called implies it took effect. Beyond this one call, the rest of the claim stands: this module supplies `HandoverGuard.IsSafeToReclaim`, `LivenessBookkeeper` and `QuiescenceTracker` for the ownership subsystem to consult before *it* calls `SetOwner` on a claim/reclaim sweep. If that subsystem's clean-relinquish contract is ever implemented differently than "ask, wait for ack, or fence-then-claim," every duplication guard in this document is only as good as that contract — this is the single most load-bearing cross-subsystem dependency in the whole module.
- **Zone simulation subsystem (`Z2`/`Z3`/`Z9`, doc04).** H1 (`SpawnSystem`'s `GetPlayersInZone`) and H5's `Ship`/`Vagon` hand-back are both **documented, intentional no-ops** until that subsystem's per-peer instantiation hook makes real Player colliders/GameObjects exist server-side. Z3's missing-prefab-deletion guard (`ZNetScene.CreateObjectsSorted` destroying any ZDO it can't instantiate, SERVER:82184-82188) is why `HandoverGuard` checks `ZNetScene.instance.GetPrefab(zdo.GetPrefab()) != null` before touching anything — the same guard that subsystem's own object feed must apply.
- **Transport/bandwidth subsystem (Phase A).** `ZDO.SetOwner` bumps `OwnerRevision`, which forces every peer to resend that ZDO (research finding #10) — every reclaim this module gates through `HandoverGuard` is therefore also a bandwidth event; a `HandoverGuard` that's too permissive (reclaims too eagerly) directly costs Phase A's send-queue budget, not just data-loss risk. This is a real coupling, not merely a shared-codebase note.
- **Client smoothing (doc `11-client-smoothing.md`, C* levers).** H8's door writes and H5–H7's ownership moves are both ordinary `ZDO.Set`/`SetOwner` events from the client's point of view — any client-side interpolation/smoothing this mod adds must treat a server-authored door close or an ownership handover exactly like a normal remote-peer-authored change, since nothing here changes the wire shape of those events.
- **Delta/compression (doc `12-delta-and-compression.md`, D* levers).** No interaction — every write in this module is a normal single-field `ZDO.Set`, already covered by whatever general delta/compression scheme those levers implement; this module does not need its own payload format.
- **Other mods.** AzuAntiCheat (installed on Proxima Maxi) inspects Harmony patches from all installed mods on a 60s client-side timer (confirmed separately, not part of this doc's scope). **RED-TEAM FIX (Red Team 1, major #4):** the previous claim that "none of this module's patches are client-side" was false as written (see Vanilla and version compatibility above) — H0's lazy, `IsDedicated()`-gated apply now makes it true in practice for H1/H3/H4; the only patches a client ever has installed are H0's own tiny `ZNet.Awake` postfix and H2's already-inert, `IsServer()`-gated postfix, both small enough to be trivially auditable if AzuAntiCheat's walk ever flags them. Jotunn, Smoothbrain-ServerCharacters, BetterContinents, ValkyriesCargo, Njord, and the rest of Proxima's ~47 mods are not known, from static analysis alone, to patch any of the same methods this module touches; the one shared-hot-method risk flagged elsewhere in the research (transpiler collisions on commonly-patched methods) applies here specifically to `SpawnSystem.UpdateSpawning` (H1) and is why H1's transpiler is required to be anchor-matched and fail-closed rather than index-based. Also see H5's corrected note above (Ownership subsystem bullet) on the `ZDO.SetOwner` priority interaction with `Wubarrk-GetOffMyLawn`/`Wubarrk-Wonderland`.

## Test plan

**Testbed (`libs-Tools/DEDICATED-SERVER-TESTBED/`).**
1. S6 first, alone, before anything else in this doc: enable `HeadlessNRESweep`, run a normal session with no other Phase B feature on, and confirm the server's `LogOutput.log` shows zero unhandled NREs from the five guarded methods over a multi-hour session with cooking, gathering (Pickable), and any installed content that spawns Leviathan/MusicVolume objects — this is a pure regression check with no dependency on anything else in this document.
2. S1: with the ownership subsystem's patch active and a zone held server-owned with a player nearby for 5+ minutes, confirm via `ServerSpawning.SpawnsProduced` that spawns occur once per real-time window, not doubled across the old 2s flap boundary; separately, load-test with another SpawnSystem-patching mod installed (if available) to confirm H1's transpiler still finds its anchor and logs a clear warning if it doesn't.
3. S5: compare `Ship.GetSailForce`/`Windmill.GetPowerOutput` output (via a debug log) headless-with-proxy vs. a listen-server host with a real camera in the same wind conditions, confirming they converge; separately confirm `HeadlessBiomeWind.FallbackToVanillaCount` stays 0 for the duration.
4. S4: open a chest as a player, add an item, then trigger the ownership subsystem's reclaim sweep mid-edit (before the client's next `OnContainerChanged`) and confirm `HandoverGuard.ReclaimBlocked.InUseContainer.InUseFastPath` increments and the item is still present after the next load; board/disembark a server-owned ship and confirm no rubber-band and (once the zonesim prerequisite is live) that hand-back reclaims it cleanly. **RED-TEAM FIX (added test, Red Team 1 blocker #1):** repeat with the "player" being a **vanilla, unmodded client** (or a modded client with the plugin's own bookkeeping stubbed out) so the local `IsInUse()` fast path cannot fire, hard-kill that client's connection mid-edit, and confirm `HandoverGuard.ReclaimBlocked.InUseContainer.QuiescenceBackstop` — not the fast path — is what blocks the reclaim, that it stays blocked for roughly `QuiescenceSeconds`, and that the object becomes reclaimable again after that window elapses (proves both the blind-spot fix and the disconnect-livelock fix from the same test).
5. S2/S7: hold a zone with an actively-fighting creature or an actively-producing smelter, trigger a reclaim attempt mid-simulation, and confirm `HandoverGuard.ReclaimBlocked.ActiveSimulation` increments and — critically — that no duplicate loot/output appears in the world after the eventual clean handover completes. Also run the same "vanilla/hard-kill" variant as item 4, confirming `.QuiescenceBackstop` (not `.LocalTick`) is what fires when the owner is not the mod's own process.
6. S8 (deferred): **no automated test can pass here yet** — the mid-`UpdateSupport()` guard is deferred (H7), so a synthetic force-claim mid-recalculation is expected, today, to reproduce the destroy-with-drops duplication the guard was meant to prevent. Run it anyway as a documented, known-failing regression probe (not gated as a release blocker for B1, but tracked against B2/B3 readiness) so the gap is measured, not assumed.
7. S8 door auto-close: place a door via the hammer, leave it open past `IdleSeconds`, confirm it closes and `DoorAutoClose.Closed` increments; separately confirm a dungeon/Location door (spawn one via a known Location, or `devcommands` teleport to one) is never touched regardless of how long it's left open, and that `DoorAutoClose.SkippedNotPlayerPlaced` increments for it.

**Live, with NoodlesProbe / telemetry (once the ownership+zonesim prerequisites are live on Proxima Maxi).**
- Run a session with the Arizona admin present, one zone held server-owned near him, and watch `HandoverGuard.DoubleDropDetected` (must stay 0), `NRESweep.Guarded.*` (nonzero here is useful evidence, not a failure), and `ServerSpawning.SpawnsProduced` against a manual mob count in that zone.
- Cross-reference `HandoverGuard.ReclaimBlocked.*` telemetry against the probe's existing per-owner object census (already built, `probe/analyze_probe.py`) to confirm the guard is firing on genuinely in-use objects, not spuriously blocking idle ones (which would be a functionality bug, not a safety one, but worth catching).

## Open questions

- **Ship↔Vagon-container ZDO linkage beyond field reference.** Confirmed `Vagon.m_container` is a direct public field (SERVER:147323), but whether that `Container`'s `ZNetView` is a child of the Vagon's own `ZNetView` or an independently-registered ZDO is not resolvable from decompiled C# alone (would need prefab/asset inspection, same class of question as the Player-prefab `m_persistent` read the research already did once via UnityPy). `HandoverGuard`'s `Container`/`Vagon` check above assumes they may be independent and walks both explicitly; this should be confirmed with a live log of both ZDOIDs before B2 ships.
- **`Ship.m_players` field access.** Confirmed private (SERVER:140294); the code above uses `AccessTools.FieldRefAccess`, now implemented rather than sketched (Red Team 2, major #3). Exact reflection fragility (whether the field name/type survives a future game patch) is the same class of risk already flagged for other private-field reads elsewhere in the research (T2's `m_con`) — same mitigation (try/catch, fail closed) applies, not re-derived here.
- **`Features.HandoverGuard.QuiescenceSeconds` tuning.** The 5s default is a reasoned starting point (comfortably above Smelter's 1s accumulator tick, the tightest known per-tick write interval among guarded types) but is not yet measured against a live session the way O3 is; needs the same live-measurement pass before B2 ships, and may need per-type overrides if creature AI or a different production type turns out to have a much sparser natural write cadence than Smelter's.
- **`WearNTear` mid-`UpdateSupport()` guard.** Deferred, not designed (Red Team 2, blocker #1, extended by analogy to H7). No ZDO-replicated signal fine-grained enough for a sub-second, single-method-call race has been identified in this pass; before S8 (correctness) can be considered fully shipped for B2, either such a signal must be found, or the ownership subsystem's clean-relinquish contract must be shown to make a force-claim mid-`UpdateSupport()` structurally unreachable (not merely assumed unreachable), or the risk must be explicitly accepted and documented as a known residual gap.
- **H0's `ZNet.Awake` lazy-patch-apply timing.** Confirmed `ZNet.Awake` exists in both assemblies (SERVER:79204, CLIENT:79346) and fires before any session/RPC/spawn activity is possible, but this pass did not trace every other mod's own `ZNet.Awake` patch ordering (Harmony patch order across multiple mods' postfixes on the same method is generally unordered unless priorities are set) — confirm H0's postfix doesn't need its own explicit priority relative to any other installed mod's `ZNet.Awake` patch before this ships.
- **Whether a freshly-spawned `Valkyrie` ZDO can ever actually be swept server-side.** The H4.3 guard is applied defensively regardless, since it's free; whether the scenario is reachable at all remains open and doesn't block shipping the guard.
- **`m_canNotBeClosed` and `m_keyItem` prefab defaults for door auto-close.** H8 reads these from the prefab asset via `ZNetScene.instance.GetPrefab(...)`, which should work for vanilla door prefabs; whether any installed content mod's custom door prefab has an unusual composition that defeats this (e.g., overrides `CanInteract()` in a way this scan doesn't see) is unverified — the scan is read-only against known vanilla fields and fails safe (skips) on anything it can't positively classify as player-placed and closable.
- **Door auto-close default `IdleSeconds`.** No user target was given for this (open question in the source task itself); 300s is this document's own recommendation, not adjudicated by the user yet — flag for the same round of value confirmation as Q2/O3/T4 in the ownership/transport docs.
- **`RPC_Damage` stat-credit companion RPC's exact wire shape.** H2's sketch above routes credit to "the attacking peer's own client-side stat tracker via a namespaced RPC" without specifying that RPC's payload — deferred to whichever subsystem owns the shared negotiated-RPC channel (G1-N1), since this module shouldn't invent a second one.

## Red-team reconciliation

Both red teams' issues were independently re-verified against the decompiles this pass (not re-copied from either report). Every `SERVER:`/`CLIENT:` citation quoted below as evidence was re-grepped directly. Dispositions:

**Red Team 1 (data loss):**

1. **Blocker — `HandoverGuard`/`SimulationTracker` local-only checks can't see a remote owner's real activity.** **FIXED**, with one scope correction. Confirmed: `Container.IsInUse()` returns `m_inUse` (SERVER:121983-121986), set only by `SetInUse(bool)` gated `if (m_nview.IsOwner() && ...)` (SERVER:121988-121992); `Smelter.UpdateSmelter()`'s owner gate (`if (!m_nview.IsOwner()) return;`, confirmed immediately before the accumulator loop, SERVER:142309-142311) has the identical shape; the previous draft's `SimulationTracker` text ("a small server-side bookkeeping component... updated by a lightweight postfix on `MonoUpdaters.UpdateAI`") is also unconfirmable as a target (see Red Team 2 blocker #1, same root document text). Fix shipped: H5/H6 replaced the local checks with a two-layer design — a local fast path where one is genuinely observable (kept for `Container`/`Vagon`/`Sadle`/`Ship`), plus a mandatory `QuiescenceTracker` backstop reading `ZDO.DataRevision` (SERVER:73541, bumped by `IncreaseDataRevision()` on every `Set`, SERVER:73879) — vanilla replication metadata available regardless of who owns the ZDO or whether they run the mod. **Scope correction:** Red Team 1's framing implied the blind spot was universal across every guard; re-reading shows `Sadle.HaveValidUser()` (SERVER:18250) is **not** affected — `CalculateHaveValidUser()` (SERVER:18255-18270) recomputes every `FixedUpdate` from `ZNet.instance.GetAllCharacterZDOS()`, globally-replicated data, independent of ZDO ownership — Red Team 1's own evidence paragraph already conceded this distinction; the fix left Sadle's check as-is rather than redesigning something that wasn't broken.
2. **Major — the naive fix (reading `ZDOVars.s_inUse` directly) live-locks on disconnect.** **FIXED** (by construction of the same change above, not a separate patch). Confirmed the mechanism: `UpdateUseVisual()`'s owner-only write path (SERVER:121867-121880) never gets a chance to clear a stale `1` once the owner disconnects, and `RemoveOrphanNonPersistentZDOS` (SERVER:77453) only sweeps non-persistent ZDOs, so a persistent container's stale flag is confirmed never cleaned up. `QuiescenceTracker` avoids this entirely by design — it is self-expiring on a bounded local timeout (`Features.HandoverGuard.QuiescenceSeconds`, new config key) rather than sticky, so a disconnected owner's ZDO simply stops advancing `DataRevision` and becomes reclaimable after the timeout, never permanently.
3. **Major — H1's kill-switch spec is internally self-contradictory and, read one way, breaks all client-side vanilla spawning.** **FIXED.** Confirmed the contradiction existed in the previous draft's own text (one paragraph describing "an early-return prefix... that skips straight past the patched logic," the next describing "a runtime-read static field consulted inside the patched IL block"). Confirmed the failure mode: `SpawnSystem.UpdateSpawning` is the same shared method on every client (CLIENT:111318) and `Features.ServerSpawning.Active` is server-local, defaulting `false`, never synced — a prefix-based early return would read `false` and skip spawning on every client, permanently. The prefix reading is now explicitly rejected in H1's text; only the static-field-inside-IL mechanism remains.
4. **Major — the claim that no patches in this module are client-side is false.** **FIXED.** Confirmed from the document's own prior text: H1 ("transpiler still applies... Harmony patches are static once loaded"), H3 ("`GetProxyCamera()` returns `Utils.GetMainCamera()` unconditionally" when off — redirect still installed), H4 (patched "at Awake," default `Active = true`) all install their IL on every process, contradicted only H2 having an `IsServer()` gate. Fix shipped as H0: H1/H3/H4's patches now apply lazily from a `ZNet.Awake()` postfix (confirmed present both assemblies, SERVER:79204/CLIENT:79346) gated on `ZNet.instance.IsDedicated()` (confirmed present both assemblies, SERVER:81194/CLIENT:81380) — a real player's client, including a listen-server host, never has H1/H3/H4 installed at all; the only always-installed client patch is H0's own tiny postfix plus H2's already-inert one.

**Red Team 2 (runtime/compat):**

1. **Blocker — `SimulationTracker`'s Harmony target (`MonoUpdaters.UpdateAI`) doesn't exist; `PatchAll()` would throw and take the whole plugin down.** **FIXED.** Confirmed: `MonoUpdaters` (SERVER:72157) has no `UpdateAI` method; the only symbol is the unrelated extension method `MonoUpdatersExtra.UpdateAI(this List<IUpdateAI>, ...)` (SERVER:72275) called from `MonoUpdaters.FixedUpdate` (SERVER:72200); `InvokeRepeating` has no managed dispatcher in either assembly. Fix shipped: replaced with `BaseAI.UpdateAI(float)` — confirmed `public virtual` (SERVER:26018), already owner-gated as its own first two statements, with exactly two overriding subclasses (SERVER:25649, SERVER:27879) both confirmed calling `base.UpdateAI(dt)` first — a single real Postfix on the base method observes every creature's owner-gated tick. Production types are patched individually by their own real, named, owner-gated update methods (`Smelter.UpdateSmelter`, SERVER:142297; confirmed each production type uses a different `InvokeRepeating` method name, e.g. `Fermenter`'s `SlowUpdate`, so no single generic hook generalizes — documented as a per-type audit requirement, same discipline as H1/H4's anchor checks).
2. **Major — Interactions claims this module never calls `ZDO.SetOwner`, contradicted by H5's Ship hand-back.** **FIXED.** Confirmed `Ship.UpdateOwner()`'s last statement is `m_nview.GetZDO().SetOwner(newOwnerID);` (SERVER:140858-140867) and H5's transpiler is specifically designed to unblock this call once the zonesim prerequisite is live. Confirmed the compat-census risk is real: `research/13-mod-compat-census.md` documents `Wubarrk-GetOffMyLawn`'s `OwnershipHold`/`SetOwnerGuard` and `Wubarrk-Wonderland`'s `ItemFlow.ZdoSetOwnerPatch`, both skip-capable `ZDO.SetOwner` prefixes, SERVER-only. Fix shipped: Interactions section corrected to acknowledge this call path, requires an explicit Harmony priority for the Ship hand-back transpiler relative to those two prefixes, and requires telemetry reading `zdo.GetOwner()` after the call to confirm it took effect.
3. **Major — the `Ship` branch of `HandoverGuard.IsSafeToReclaim` never returns `false` (dead code, not a working guard).** **FIXED.** Confirmed: the previous draft's if-body was empty (a comment), so the function fell through to `return true` for every ship. Confirmed `Ship.m_players` is `private readonly List<Player>` (SERVER:140294) and is populated by `OnTriggerEnter`/`OnTriggerExit` (SERVER:140898-140922) with **no** `IsOwner()` gate — plain physics-collider callbacks, meaning (favorably, beyond what Red Team 2 asked for) the server's own locally-instantiated Ship independently computes the correct occupancy regardless of who owns the ZDO, once the zonesim prerequisite is live. Fix shipped: implemented via `AccessTools.FieldRefAccess<Ship, List<Player>>(...)`, matching the `Container`/`Sadle` pattern, as Red Team 2 recommended.
4. **Minor — H8's door-close guard skips `m_canNotBeClosed` but not `m_keyItem`, unlike vanilla's own `CanInteract()`.** **FIXED.** Confirmed `CanInteract()` (SERVER:123684) gates identically on `(m_keyItem != null || m_canNotBeClosed) && s_state != 0` (SERVER:123685-123687). Fix shipped: scanner's guard now excludes `door.m_keyItem != null` alongside `door.m_canNotBeClosed`.
5. **Minor — five off-by-1/2 line citations.** **FIXED**, mechanically. Re-grepped and corrected: `SERVER:122732→122733`, `122739→122740`, `122741→122742`, `122647→122649`, `147322→147323`, and one more found in the same pass while re-verifying H6, `3101→3102` (`Character.OnDeath` owner gate). No functional claim changed at any corrected line.

**One item raised by neither red team, found and downgraded during reconciliation:** H7's `SimulationTracker.IsMidSupportRecalculation` guard shares Red Team 2 blocker #1's fictional-tracker problem by construction (same class name, same document), but its race window (a single `WearNTear.UpdateSupport()` call, SERVER:149920-149987) is too tight for the `QuiescenceTracker` fix that resolves H5/H6 (which is deliberately coarse, on the order of seconds). **Deferred**, not fixed: no ZDO-replicated signal fine-grained enough for this race was identified in this pass. The "destroy-with-drops duplication" data-loss row is now marked deferred rather than guarded, S8 (correctness) still ships in B2 on the ownership subsystem's clean-relinquish contract alone for this one race (the same dependency every other B2 lever already has), and a follow-up open question is recorded above rather than a false claim of coverage.

**Overall B2 readiness.** With the above fixes, B2's core data-loss claims (duplicate loot/production output, silently-lost container edits) now rest on a mechanism (`QuiescenceTracker`) that is correct regardless of who the current owner is, self-expiring on disconnect, and backed by decompile-verified real Harmony targets — the blockers from both red teams are closed. The one remaining gap (H7's mid-support-recalculation race) is explicitly scoped down to "depends on the ownership subsystem never force-claiming a `WearNTear` piece outside its clean-relinquish contract," which is already this module's single most load-bearing external dependency for everything else in B2, so it does not introduce a new category of risk — it is a known, bounded, and now-documented residual rather than a silently-broken guarantee.
