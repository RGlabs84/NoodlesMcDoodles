# Server zone simulation: terrain, objects, caps (Phase B)

## Summary

Today a dedicated Valheim server has exactly one "real" zone: a 5x5 `Heightmap`+`MeshCollider` block
built around its own reference position, which is permanently pinned to `(1e6, 0, 1e6)` — void ocean no
player can reach (`Game.FixedUpdate`, SERVER:100332-100341, server-build-only). Every other zone the
server tracks is a *ghost*: its ZDOs exist (vegetation, locations, world content), but the GameObject that
would hold a `Heightmap`, a `MeshCollider`, a `MonsterAI`, or a `Rigidbody` is created and destroyed in the
same call and never registered (`ZoneSystem.SpawnZone`, SERVER:113946-113978, `SpawnMode.Ghost` branch).
The server therefore has nothing to *simulate* near any connected peer today, no matter what a separate
ownership blueprint does — it can be told it owns a ZDO, but it has no local terrain, no collider, no AI
component to actually run.

This module fixes that gap. It gives the server a real `Heightmap`+`MeshCollider`+object-instance ring
around **every connected peer's own position**, continuously, as they move — by re-driving the exact same
`ZoneSystem`/`ZNetScene` code vanilla already runs for its one dead reference position, fed a different
input `N` times instead of once. It ships in three milestones:

- **B1 — terrain only** (Z2, Z4, Z5, Z10): real `Heightmap`+`MeshCollider` around every peer, nothing
  standing on it yet. Confirms headless terrain/physics is not blocked by any graphics-device no-op.
- **B2 — objects + bounded cost** (Z3, Z6, Z7, Z9): real GameObjects (colliders, `MonsterAI`,
  `Rigidbody`, `TerrainComp`) in every peer's ring, a hard memory/CPU ceiling with graceful stand-down,
  and a correctness guard so world-gen's `SnapToGround` doesn't mis-place objects under N moving rings.
- **B3 — handover correctness** (Z8, G6-N2): closes the residual `TerrainComp` duplicate-deletion race
  this feature activates for the first time, and instruments it so a live server proves it isn't
  happening rather than assuming so.

**What this fixes for the Arizona case, and what it does not.** This module does **not** move ownership.
Nothing here changes who a ZDO belongs to (`ZDO.SetOwner`), and `ReleaseNearbyZDOS`'s peer-proximity rule
(SERVER:76948-76979) is untouched — a standing player still ends up owning everything near them exactly as
today, every 2s. What this module does is remove the *reason* that rule is the only option: there is
currently nothing else with a real, ticking, physically-present GameObject to hand ownership *to*. Once
B1+B2 ship, the server has a real, always-on, zero-latency-to-New-Jersey simulator standing in every
peer's zone, including the admin's zone in Arizona. That is the load-bearing prerequisite the ownership
blueprint's `G3-L1` postfix on `IsInPeerActiveArea` (owned by `docs/design/03-ownership-and-authority.md`,
not this doc) needs in order to mean anything: taking ownership away from the Arizona peer only helps if
the thing taking it can actually simulate the zone, which is exactly what B1+B2 build. **This doc ships no
ownership change by itself** — it is necessary, not sufficient, for the Arizona fix.

---

## Levers covered

| Lever | Verdict (10-LEVERS, LEAD-CORRECTED noted) | User target | Recommended value/range | Milestone |
|---|---|---|---|---|
| Z2 — per-peer real-terrain poke-ring driver | SAFE-WITH-GUARDS. **RED-TEAM FIX: Hook 1 now carries its own per-tick zone-build cap (`MaxZoneBuildsPerTick`) — see Hook 1; Hook 5's prose no longer claims `ZoneCap` gates Hook 1, since `ZoneCap` counts objects, not zones (RT2 blocker #2).** | (unselected numeric; lever selected) | ring = `min(config, SimulationDistance.NearSimulationDistance)`, driven every `ZoneSystem` tick (see Z5); capped at `MaxZoneBuildsPerTick` new zone builds/tick | B1 |
| Z3 — per-peer object instantiation (`CreateObjects` prefix) | SAFE-WITH-GUARDS. **Re-verified this session: the mandatory prefab-resolution guard (Lead item 3) must also cover `CreateDistantObjects`, not only `CreateObjectsSorted` — both run the identical destructive branch (SERVER:82184-82188 and 82220-82224); the research doc and lever catalog only name the first.** **RED-TEAM FIX: Hook 2 now grace-holds fed ZDOs across a bounded number of un-fed ticks before `RemoveObjects`'s destroy branch (SERVER:82267-82269) can reach them — see Hook 2 (RT1 blocker #1 / RT2 blocker #1).** | (lever selected) | object-count cap per peer, hardware-dependent — see Z9 | B2 |
| Z4 — `ZoneSystem.m_zoneTTL` | SAFE-WITH-GUARDS | **15 s** | 15 s, write gated on `ZNet.instance.IsServer()` | B1 |
| Z5 — `ZoneSystem` update tick / one-ghost-build-per-tick cap | SAFE-WITH-GUARDS. **RED-TEAM FIX: the transpiler now also rewrites the second, previously-missed `0.1f` literal at the `UpdateTTL(0.1f)` call site (SERVER:113806/CLIENT:114617); left unpatched, TTL would have accumulated at ~2x real-time once the tick moved to 0.05s, silently halving the user's 15s target (RT2 blocker #3). The ghost-spawn cap's peer-iteration is now round-robin, not `GetPeers()`'s fixed join order (RT1 major #3).** | **0.05 s** | 0.05 s tick, **plus the mandatory whole-peer-loop ghost-spawn cap (default 1/tick) this doc treats as inseparable from the cadence change**, serviced round-robin across peers | B1 |
| Z6 — `ZNetScene` create/destroy rate + per-frame instantiation budget | SAFE-WITH-GUARDS | (lever selected) | patch the **100** literal (the branch a dedicated server actually reads), not the 10; leave 30Hz cadence at vanilla by default | B2 |
| Z7 — `SnapToGround` guard (defer, don't drain) | SAFE-WITH-GUARDS | (lever selected) | correctness guard, not numeric | B2 |
| Z8 — duplicate `TerrainComp` avoidance during zone handover | NEEDS-LIVE-TEST. **LEAD-CORRECTED: the cross-machine race is not real (duplicates are per-process); only a narrow server-authored-TerrainOp-before-compiler-instantiated residual remains.** | (lever selected) | no standalone hook — closed by Z2→Z3 terrain-before-objects ordering (already mandatory) + G6-N2's readiness gate | B3 |
| Z9 — memory/CPU caps + fail-safe stand-down | SAFE-WITH-GUARDS. **RED-TEAM FIX: `ZoneCap`'s global estimate now decays instead of only ever growing — see Hook 5 (RT1 major #2). Its scope is corrected to "gates Hook 2's object feed only," never Hook 1's terrain builds.** | (lever selected) | per-peer and global object-count caps, hysteresis at 90%/100%, "stop appending, never destroy" | B2 |
| Z10 — headless terrain/physics/navmesh feasibility | SAFE (read-only) | (lever selected) | no lever to pull — confirmation only | B1 |
| G6-N2 — `TerrainComp` duplicate-race handover-atomicity guard | NEEDS-LIVE-TEST | (lever selected) | readiness-gate + canary telemetry (see Hooks); full closure remains NEEDS-LIVE-TEST per the catalog's own verdict | B3 |

---

## Hooks

Shared infrastructure conventions used below (defined in the "rails" blueprint, `docs/design/01-*.md`):
`NoodlesMcDoodles.Core.Features.<Name>.Active` runtime flags, `NoodlesMcDoodles.Core.Telemetry` counters,
BepInEx `ConfigFile` entries, and the "prefix returns true / postfix no-ops / transpiler reads a static
field holding the vanilla literal when disabled" kill-switch pattern. This module's flag is
`Features.ServerZoneSim`.

### Hook 1 (Z2) — `ZoneSim.PokeRingDriver`: per-peer terrain poke ring

- **Target:** `ZoneSystem.PokeLocalZone(Vector2s zoneID)` — private instance method.
  `SERVER:113896-113913` / `CLIENT:114707-114724`. Confirmed present with identical signature and
  identical body in both assemblies (both files opened directly this session; bodies are byte-identical
  except for surrounding whitespace from decompilation).
- **Patch kind:** none — no Harmony patch on `PokeLocalZone` itself. New driver component
  (`InvokeRepeating`/coroutine) that calls the existing private method directly via `HarmonyLib.AccessTools`
  reflection (or a build-time publicizer), once per connected peer per `ZoneSystem` tick, across a ring of
  `min(configured radius, ZNet.instance.GetSyncedSimulationDistance().NearSimulationDistance)` zones
  centered on `peer.GetRefPos()` (`ZNetPeer.GetRefPos`, SERVER:78871-78874 / CLIENT:79013-79016, confirmed
  present both builds, bare field getter).
- **Side:** server only. `SpawnMode.Full` (real, first-time-generation-capable terrain) requires
  `ZNet.instance.IsServer()` inside `SpawnZone` (SERVER:113946-113955); running this driver client-side
  would silently downgrade to `SpawnMode.Client` and add nothing a vanilla client doesn't already have.
- **Why this hook and not another:** `PokeLocalZone` is the smallest primitive that produces a *real*
  terrain GameObject (as opposed to `CreateLocalZones`, which wraps it in a whole-ring loop keyed to a
  single `Vector3` — exactly the one-position limitation Z1 is UNSAFE for) or `CreateGhostZones` (which
  destroys the GameObject in the same call, SERVER:113846-113864, `ZNetView.m_ghostInit` gate,
  SERVER:82658-82675 — proven to never reach `Start()`/`AddInstance`). Driving `PokeLocalZone` directly,
  once per peer, is `ZoneAnchor.md`'s Mechanism 1 generalized from one static anchor to N moving ones,
  with no code path through `CreateLocalZones`'s single-`Vector3` ring loop at all.
- **Code sketch:**

```csharp
// src/ZoneSim/PokeRingDriver.cs
namespace NoodlesMcDoodles.ZoneSim
{
    internal sealed class PokeRingDriver : MonoBehaviour
    {
        private static MethodInfo s_pokeLocalZone; // AccessTools.Method(typeof(ZoneSystem), "PokeLocalZone")
        private readonly HashSet<Vector2s> m_pokedThisTick = new();
        private float m_updateTimer; // RED-TEAM FIX: explicit accumulator, mirrors Hook 3's own gate pattern

        private void FixedUpdate()
        {
            if (!Features.ServerZoneSim.Active || !ZNet.instance.IsServer()) return;
            // RED-TEAM FIX (RT1 minor): FixedUpdate as a bare MonoBehaviour message runs at Unity's
            // physics tick rate (no code-level override found anywhere in either build, Open question 7),
            // not at Config.ZoneSim.UpdateTickSeconds. Gate explicitly instead of trusting the message
            // rate to match Hook 3's cadence.
            m_updateTimer += Time.fixedDeltaTime;
            if (m_updateTimer < Config.ZoneSim.UpdateTickSeconds.Value) return;
            m_updateTimer = 0f;

            m_pokedThisTick.Clear();
            // RED-TEAM FIX (RT2 blocker #2): a per-tick cap on real *zone builds*, independent of Z9's
            // ZoneCap (which counts objects, not zones — see Hook 5's corrected scope). Without this,
            // Hook 1 alone (B1, no Z9 dependency) can synchronously Instantiate+Heightmap+MeshCollider
            // every not-yet-real zone across every peer's ring in one tick (up to PokeRingRadius=2 => 25
            // zones/peer; SpawnZone's Instantiate+Heightmap cost is real even in SpawnMode.Client for an
            // already-world-gen'd zone, confirmed SERVER:113946-113977/113896-113912 this session).
            int zoneBuildsThisTick = 0;
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (!PeerReadyState.HasFirstRealPosition(peer)) continue; // guard, see below
                Vector2s center = ZoneSystem.GetZone(peer.GetRefPos());
                int ring = Mathf.Min(Config.ZoneSim.PokeRingRadius.Value,
                                      ZNet.instance.GetSyncedSimulationDistance().NearSimulationDistance);
                foreach (var z in RingZones(center, ring))
                {
                    if (!m_pokedThisTick.Add(z)) continue; // de-dup across overlapping peer rings
                    if (!ZoneSystem.instance.IsZoneGenerated(z)) continue; // guard: never first-gen implicitly
                    if (ZoneSystem.instance.IsZoneLoaded(z)) continue; // already real: PokeLocalZone would
                                                                         // just reset its TTL for free, but
                                                                         // skip the reflection call anyway
                    if (zoneBuildsThisTick >= Config.ZoneSim.MaxZoneBuildsPerTick.Value) continue; // cap hit:
                                                                         // retried automatically next tick,
                                                                         // this loop re-scans every tick
                    s_pokeLocalZone.Invoke(ZoneSystem.instance, new object[] { z });
                    zoneBuildsThisTick++;
                }
            }
        }
    }
}
```

- **Guards carried from Z2's catalog entry, all implemented above or by a sibling hook:** skip
  `!IsZoneGenerated` zones (shown); de-dup across peer rings before poking (shown, `m_pokedThisTick`);
  gate on `m_zones.ContainsKey`, never `IsZoneLoaded`, for any *readiness* read elsewhere in this module
  (Hook 2); pad the ring for `peer.GetRefPos()` staleness — `RPC_ServerSyncedPlayerData` is sent every 2s
  by vanilla (`CLIENT:80578-80583`), so a fast-moving peer's reported position can be stale by up to two
  seconds; the mandatory "first real position received" gate (`PeerReadyState`) is the `Z2` catalog's item
  (7): `ZNetPeer.m_refPos` defaults to `Vector3.zero` (SERVER:78837) and the driver must not build a ring
  at the origin for a peer whose handshake hasn't yet delivered a live position. **RED-TEAM FIX (RT2 minor
  #4 / RT1 minor):** explicit `m_updateTimer` accumulator gate, and a per-tick `MaxZoneBuildsPerTick` cap
  on real zone construction, both added above.
- **Kill switch off:** `Features.ServerZoneSim.Active == false` short-circuits `FixedUpdate` on its first
  line; no zone is ever poked. Any real terrain already built before the flag flipped is left exactly
  alone — it ages out through vanilla's own `UpdateTTL` (Hook 3) once nothing pokes it anymore, never
  force-destroyed by this hook.

### Hook 2 (Z3) — `ZoneSim.ObjectFeedPrefix`: per-peer object instantiation

- **Target:** `ZNetScene.CreateObjects(List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects)`
  — private instance method. `SERVER:82139-82150` / `CLIENT:82369-82380`. Confirmed identical signature and
  body both builds.
- **Patch kind:** `[HarmonyPatch(typeof(ZNetScene), "CreateObjects")] [HarmonyPrefix]`, `__runOriginal =
  true` always (never skips vanilla — this is a pure "add more candidates" prefix).
- **Side:** server only (this feature's server-authority purpose; nothing stops it compiling for client
  builds, but a client has no reason to run it — its own client already gets real terrain from vanilla).
- **Why this hook and not `CreateDestroyObjects`:** `CreateDestroyObjects` (SERVER:82314-82321 /
  CLIENT:82544-82551, byte-identical) calls `CreateObjects(m_tempCurrentObjects, m_tempCurrentDistantObjects)`
  then `RemoveObjects(m_tempCurrentObjects, m_tempCurrentDistantObjects)` with the **same two `List<ZDO>`
  references**. Appending inside the `CreateObjects` prefix mutates those lists in place before either
  method reads them, so the appended ZDOs are both instantiated (`CreateObjects`) and protected from the
  same-tick sweep (`RemoveObjects`'s earmark logic, SERVER:82242-82261) for free — one hook covers both
  halves of the "make it real and keep it real" requirement. A prefix on `CreateDestroyObjects` itself
  would have to duplicate that list-identity trick manually for no benefit.
- **Mandatory guard, re-verified and extended this session:** `CreateObjectsSorted`'s invalid-prefab
  branch (SERVER:82178-82188, confirmed: `item.SetOwner(ZDOMan.GetSessionID()); ZDOMan.instance.DestroyZDO(item)`
  when `CreateObject(item)` returns null and `ZNet.instance.IsServer()`) is real and destructive, exactly
  as Lead item 3 states. **This session's own read of `CreateDistantObjects` (SERVER:82202-82224) found the
  identical branch there too** (`@object.SetOwner(ZDOMan.GetSessionID()); ZDOMan.instance.DestroyZDO(@object)`,
  SERVER:82220-82224) — the lever catalog and research/04 only cite the near-object copy. Any ZDO this
  hook appends can land in either list depending on distance, so the prefab-resolution filter below must
  run before appending to **both**.
- **RED-TEAM FIX — grace-hold guard for fed ZDOs that stop being fed (RT1 blocker #1 / RT2 blocker #1):**
  both red teams independently traced the same real mechanism this doc's own prior text missed. Re-verified
  directly this session, both builds identical: `RemoveObjects` (SERVER:82242-82273/CLIENT:82472-82503)
  earmarks every ZDO present in `currentNearObjects`/`currentDistantObjects` **this tick** and, for every
  registered `ZNetView` instance *not* earmarked, destroys its local `GameObject` **and**, if
  `!zDO.Persistent && zDO.IsOwner()` (SERVER:82267-82269, exact line range the prior draft's own citation
  of "SERVER:82242-82261" stopped just short of), calls `ZDOMan.instance.DestroyZDO(zDO)` — confirmed this
  is a real network broadcast, not a local-only effect: `DestroyZDO` queues the ZDO for
  `ZRoutedRpc.InvokeRoutedRPC(0L, "DestroyZDO", ...)` (SERVER:76976-76996, target `0L` = broadcast to every
  peer). `CreateDestroyObjects` runs this sweep unconditionally every `1/30s` (SERVER:82303-82312), **not**
  gated by `Features.ServerZoneSim` — only `CreateObjects`'s own prefix (this hook) is. So any ZDO Hook 2
  fed on tick *N* that is not re-fed on tick *N+1* — zone excluded by `ZoneCap`, peer moved/disconnected, or
  the kill switch flips off — falls out of the earmarked set on the very next `1/30s` tick.

  Separately confirmed this session: `ReleaseNearbyZDOS` — the *only* vanilla mechanism that ever reassigns
  a ZDO's ownership by proximity — unconditionally skips every non-`Persistent` ZDO
  (`if (!tempNearObject.Persistent) { continue; }`, SERVER:76957-76960). This means a non-`Persistent` ZDO
  the server ever ends up owning **can never have its ownership moved away by waiting** — RT2's own proposed
  remedy ("give `ReleaseNearbyZDOS`'s 2s window time to resolve ownership away from the server") does not
  actually work, because that path is structurally unreachable for exactly the ZDOs at risk here. The fix
  below is therefore a bounded grace-hold against ordinary ring-boundary churn (a peer's ring crossing a
  zone edge, a transient `ZoneCap` flap, one missed `PokeRingDriver` tick), not a promise that a
  permanently-unfed zone's non-`Persistent`, server-owned content survives forever — nothing can make that
  promise while `ReleaseNearbyZDOS` is structurally unable to claim it. Critically, this branch **can never
  reach a `Persistent` ZDO** — the `!zDO.Persistent` guard is vanilla's own, unconditional, and the one
  category of ZDO Valheim itself writes to the world save. So no path through this hook, with or without the
  grace-hold, can lose, roll back, or corrupt world/player save data; the residual is a visual/gameplay pop
  of ephemeral (never-saved) content, which the grace-hold bounds and the new telemetry counter below makes
  observable rather than silent.
- **Code sketch:**

```csharp
[HarmonyPatch(typeof(ZNetScene), "CreateObjects")]
[HarmonyPrefix]
private static void Prefix(List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects)
{
    if (!Features.ServerZoneSim.Active || !ZNet.instance.IsServer()) return;
    m_seen.Clear();
    m_seen.UnionWith(currentNearObjects);
    m_seen.UnionWith(currentDistantObjects);
    ZoneCap.BeginTick(); // RED-TEAM FIX: rebuild the estimate fresh each tick — see Hook 5
    foreach (var peerRing in PokeRingDriver.ActiveZones()) // zones Hook 1 has real terrain for (m_zones.ContainsKey)
    {
        if (!ZoneCap.CanInclude(peerRing)) continue;          // Z9 hysteresis check, BEFORE any append
        ZDOMan.instance.FindSectorObjects(peerRing, s_nearOnlyDistance, m_tempNear, m_tempDistant);
        AppendFiltered(currentNearObjects, m_tempNear);
        AppendFiltered(currentDistantObjects, m_tempDistant);
    }
    // RED-TEAM FIX: bounded grace-hold. Anything this hook fed recently but did NOT re-feed this tick
    // (m_seen already reflects this tick's real feed, since AppendFiltered adds to it) gets re-earmarked
    // for a few more ticks before RemoveObjects's destroy branch can ever see it drop out.
    GraceHold.TickAndReappend(m_seen, currentNearObjects, currentDistantObjects);
}

private static void AppendFiltered(List<ZDO> target, List<ZDO> candidates)
{
    foreach (var zdo in candidates)
    {
        if (!m_seen.Add(zdo)) continue;                                 // de-dup
        if (ZNetScene.instance.GetPrefab(zdo.GetPrefab()) == null)       // MANDATORY guard, both lists
        {
            Telemetry.ZoneSim.ObjectsSkippedMissingPrefab.Increment();
            ZLog.LogWarning($"NoodlesMcDoodles: skipping unresolved prefab {zdo.GetPrefab()} at {zdo.GetPosition()}");
            continue;
        }
        target.Add(zdo);
        if (!zdo.Persistent && zdo.IsOwner()) GraceHold.Refresh(zdo, target); // only non-Persistent+owned
                                                                                // ZDOs are ever at risk —
                                                                                // see RED-TEAM FIX above
    }
}

// src/ZoneSim/GraceHold.cs — RED-TEAM FIX, new mod-owned bookkeeping
internal static class GraceHold
{
    // ZDO -> (ticks of grace remaining, the list it was last fed into)
    private static readonly Dictionary<ZDO, (int ticks, List<ZDO> list)> s_held = new();

    public static void Refresh(ZDO zdo, List<ZDO> list) =>
        s_held[zdo] = (Config.ZoneSim.ObjectUnfeedGraceTicks.Value, list);

    public static void TickAndReappend(HashSet<ZDO> fedThisTick, List<ZDO> near, List<ZDO> distant)
    {
        foreach (var key in s_held.Keys.ToList()) // snapshot: safe to mutate s_held below
        {
            if (fedThisTick.Contains(key)) continue; // ordinary path already re-earmarked it this tick
            var (ticks, list) = s_held[key];
            if (ticks <= 0 || !key.IsValid())
            {
                s_held.Remove(key); // grace expired (or ZDO gone) — let vanilla's ordinary sweep run;
                continue;           // never a Persistent ZDO, per the guard in AppendFiltered above
            }
            (list == near ? near : distant).Add(key); // keep it earmarked one more tick
            s_held[key] = (ticks - 1, list);
        }
    }
}
```

- **Guards from the catalog, status:** mutate lists in place, never replace (shown); gate per-zone on
  `m_zones.ContainsKey` via `PokeRingDriver.ActiveZones()`, never `IsZoneLoaded` (shown); de-dup via
  `HashSet<ZDO>` every tick (shown, `m_seen`); cap total appended count per peer and globally, checked
  **before** appending (shown, `ZoneCap.CanInclude`, see Z9/Hook 5); prefab-resolution guard on **both**
  target lists (shown, extended per the re-verification above); **RED-TEAM FIX:** bounded grace-hold for
  any fed ZDO that is `!Persistent && IsOwner()` (shown, `GraceHold`), so an ordinary ring-boundary drop
  does not immediately reach `RemoveObjects`'s destroy branch.
- **Kill switch off:** prefix returns on its first line; vanilla's own single-reference-position scan runs
  completely unaffected. **RED-TEAM FIX — corrected:** objects already instantiated by a previous tick's
  append are *not* retroactively removed by this hook itself, but they are **not** protected by Z4's TTL
  either — Z4 (`UpdateTTL`) only ever destroys a zone's terrain root (`zone.m_root`), never touches
  individual entries in `ZNetScene.m_instances` (confirmed this session, SERVER:115548-115562: the method's
  entire body only ages `ZoneData.m_ttl` and, at most, calls `UnityEngine.Object.Destroy(zone2.Value.m_root)`
  — no reference to `m_instances` anywhere). The real decay path for an individual fed object once the
  kill switch flips off is the ordinary `1/30s` `RemoveObjects` sweep described above, now bounded by the
  grace-hold for exactly `ObjectUnfeedGraceTicks` ticks, after which any remaining non-`Persistent`,
  server-owned ZDO is swept — never a `Persistent` one, at any point.

### Hook 3 (Z5) — `ZoneSystem.Update` cadence transpile + mandatory whole-peer-loop ghost cap

- **Target:** `ZoneSystem.Update()` — private instance method, `Update()` MonoBehaviour message.
  `SERVER:113776-113845` / `CLIENT:114587-114656`. Confirmed **byte-for-byte identical** between builds by
  direct side-by-side read this session (both bodies quoted in research/04 §2, re-checked here against the
  live decompile line ranges above).
- **Patch kind:** two patches, both required together per the catalog's "inseparable" guard:
  1. `[HarmonyTranspiler]` on `ZoneSystem.Update` swapping **two** bare `0.1f` literals for a read of
     `Config.ZoneSim.UpdateTickSeconds.Value` each (falls back to `0.1f` when the feature is off, per the
     kill-switch contract — the transpiled read itself checks `Features.ServerZoneSim.Active` and
     substitutes the vanilla literal when false, so disabling the feature restores exact vanilla cadence
     without a restart). **RED-TEAM FIX (RT2 blocker #3):** re-read this session and confirmed there are
     genuinely **two, textually and functionally separate** `0.1f` literals in this method, not one — the
     prior draft's transpiler description named only the first:
     - the cadence gate itself, `if (!(m_updateTimer > 0.1f)) { return; }` (SERVER:113800/CLIENT:114611);
     - four lines later, `UpdateTTL(0.1f);` (SERVER:113806/CLIENT:114617), whose argument is the `dt` that
       `UpdateTTL` adds to every zone's `m_ttl` each call (`zone.Value.m_ttl += dt;`, confirmed
       SERVER:115553/CLIENT:116364 this session). If only the gate literal is patched, the method runs
       twice as often at the user's 0.05s target but still adds a full `0.1` to every zone's TTL accumulator
       each call — TTL would decay at ~2x real-time, silently turning the user's `ZoneTTLSeconds=15` target
       into an effective ~7.5s with nothing in telemetry to reveal the mismatch. The transpiler must replace
       **both** call sites with the same config read, keeping `dt` equal to the real interval between calls
       at any configured cadence. A `CodeMatcher` anchor test asserting exactly two `0.1f` float constants
       are found in `ZoneSystem.Update`, both replaced, mirrors the guard Hook 6 already carries for its own
       "10 vs. 100" dual-literal case (lines below).
  2. `[HarmonyPatch(typeof(ZoneSystem), "CreateGhostZones")] [HarmonyPrefix]` adding a **process-wide**
     counter, reset once per `ZoneSystem.Update` tick (postfix on `Update` resets it to 0), that refuses
     the call (skip, return `false` from the prefix, matching the original method's own "nothing built"
     return) once `Config.ZoneSim.MaxGhostSpawnsPerTick.Value` (default **1**, matching vanilla's own
     single-build-per-tick discipline for the reference-position branch) first-time `SpawnZone(Ghost)`
     generations have already happened this tick. **RED-TEAM FIX (RT1 major #3):** `ZNet.GetPeers()` returns
     `m_peers` directly (confirmed SERVER:81676-81679: `public List<ZNetPeer> GetPeers() { return m_peers; }`)
     — a stable, join-ordered list, and the vanilla loop this prefix guards
     (`foreach (ZNetPeer peer in ZNet.instance.GetPeers()) CreateGhostZones(peer.GetRefPos());`,
     SERVER:113811-113813) iterates it in that same fixed order every tick. With a shared counter and a
     fixed iteration order, exactly the multi-peer scenario this cap was built for (a group exploring new
     territory together, all needing first-time generation every tick) means whichever peer is earliest in
     `m_peers` always wins the tick's one slot, and later-joined peers in the same group can be starved of
     first-time zone generation indefinitely while demand keeps exceeding the cap. The prefix now tracks a
     **rotating start index** into `GetPeers()` (advanced by one peer each tick, wrapping), so the peer
     serviced first — and therefore most likely to get this tick's one slot — changes every tick instead of
     always being the earliest joiner; a peer that loses the race on tick *N* is first in line on some tick
     within the next `peerCount` ticks rather than never.
- **Side:** server (in scope); the same code runs client-side too but this project's client build does not
  drive a peer loop, so patch (2) is a no-op there in practice — still installed unconditionally per the
  "same code, same patch" simplicity the rails contract prefers over per-build patch sets.
- **Why this hook and not a full-method replacement:** the cadence gate is a single bare `float` literal
  compared with `>` — the smallest possible IL surface, well-suited to a transpiler rather than
  reimplementing the whole 70-line method. The ghost-spawn cap, by contrast, cannot be a transpiler (the
  uncapped `foreach (ZNetPeer peer in ZNet.instance.GetPeers()) CreateGhostZones(peer.GetRefPos())` loop at
  SERVER:113840-113844/CLIENT:114651-114655 has no per-iteration literal to swap), so it is a prefix on the
  callee instead, keyed by a shared counter — the smallest hook that can observe "how many first-time
  generations already happened this tick across every peer," which nothing inside `CreateGhostZones` itself
  knows.
- **Why this pairing is mandatory, not optional:** confirmed directly this session — `ZoneSystem.Update`
  runs `CreateLocalZones(GetReferencePosition())` first; only when that returns `false` (the common steady
  state, since the server's own dead-zone ring finishes building within its first few seconds) does it run
  `CreateGhostZones(GetReferencePosition())` **and then, uncapped,** `CreateGhostZones(peer.GetRefPos())`
  for **every** connected peer, each of which can independently run the full synchronous
  `PlaceLocations`+`PlaceVegetation`+`PlaceZoneCtrl` first-time-generation cost (SERVER:113946-113978,
  `SpawnMode.Ghost` still executes the full placement pass before discarding, per Hook 1's evidence).
  Lowering the tick gate from 0.1s to 0.05s **doubles how often that uncapped loop can fire per second**
  with no cap — on a fresh world, a big group expedition, or a post-restart cold cache, this can
  synchronously generate first-time zone content for several peers in one frame, stalling the single
  server main thread for every connected peer at once. The cap makes the 0.05s target safe to ship;
  without it, 0.05s is a live foot-gun the catalog explicitly calls out.
- **Kill switch off:** transpiled read falls back to vanilla's `0.1f` literal; the ghost-cap prefix reads
  `Features.ServerZoneSim.Active` and returns `true` (run original, uncapped) when off — restoring exactly
  vanilla's own (already-uncapped-in-1.0.16) behavior, not a regression versus today.

### Hook 4 (Z4) — `ZoneSystem.m_zoneTTL` config write

- **Target:** `ZoneSystem.m_zoneTTL` — public serialized field, default `4f`. `SERVER:113087` /
  `CLIENT:113898`. Confirmed present, same default, same declaration order, both builds (sibling field
  `m_zoneSize=64f` at SERVER:113084/CLIENT:113895, and the dead `m_zoneTTS=4f` at SERVER:113090/CLIENT:113901
  — grepped for other references to `m_zoneTTS` and found none beyond its declaration on either build,
  confirming the catalog's "do not tune, it's dead" note for the *different* field `G4-N2` names).
  `UpdateTTL` (the only reader) at `SERVER:115548-115562` / `CLIENT:116359-116373`.
- **Patch kind:** none — direct field write from config, applied once at `ZNet` ready and once per live
  config reload.
- **Side:** either build reads the field (`ZoneSystem.Update`/`UpdateTTL` is shared code), so the write
  **must** be gated on `ZNet.instance.IsServer()` — this project ships one DLL that installs on both client
  and server, and an ungated write would raise the mod's own client install's idle-terrain lifetime too
  (not data loss, but unnecessary RSS/CPU on the player's own machine during fast travel).
- **Code sketch:**

```csharp
// applied at ZNet.Awake postfix and on ConfigFile.SettingChanged
if (ZNet.instance.IsServer())
{
    ZoneSystem.instance.m_zoneTTL = Features.ServerZoneSim.Active
        ? Mathf.Clamp(Config.ZoneSim.ZoneTTLSeconds.Value, 4f, 20f)
        : 4f; // vanilla literal when the feature is off
}
```

- **User target 15 s honored directly**, inside the catalog's confirmed-safe 4-20s band: `UpdateTTL` only
  destroys an **empty, unpoked** zone's terrain root (`!ZNetScene.instance.HaveInstanceInSector(zone)`
  gate, confirmed SERVER:115548-115562, `HaveInstanceInSector` body confirmed SERVER:82368-82377 — it
  never inspects Persistent-ness or ownership, purely "is any live `ZNetView` instance registered in this
  sector") — never a zone with live instances, at any TTL value. 15s only buys slack for Hook 1's
  poke-ring driver to miss a tick (network hiccup, a busy frame from Hook 3's ghost-cap deferring other
  work) without a full terrain re-bake.
- **RED-TEAM FIX — `UpdateTTL`'s single-destroy-per-tick limit (RT2 major #4):** re-read this session and
  confirmed the method's own teardown loop caps itself to **one** zone destroyed per call:
  ```csharp
  foreach (KeyValuePair<Vector2s, ZoneData> zone2 in m_zones)
  {
      if (zone2.Value.m_ttl > m_zoneTTL && !ZNetScene.instance.HaveInstanceInSector(zone2.Key))
      {
          UnityEngine.Object.Destroy(zone2.Value.m_root);
          m_zones.Remove(zone2.Key);
          break; // <- stops after the first destroy, confirmed SERVER:115548-115562/CLIENT:116359-116373
      }
  }
  ```
  Under vanilla this is a non-issue (one reference position, at most ~25 zones ever need cleanup). Under
  this module, N peers each carrying up to a `PokeRingRadius=2` (5x5=25-zone) ring can leave hundreds of
  real zones live at once; if a large group disperses, the kill switch flips off, or many rings simply age
  out together, dozens of zones can cross the TTL threshold in the same tick but only one is torn down per
  `~0.05-0.1s` `Update` call, leaving a backlog that holds CPU/RAM for seconds to tens of seconds — exactly
  what Hook 5's circuit breaker is supposed to bound. **Fix, added as a companion patch to this hook:** a
  `[HarmonyPrefix]` on `UpdateTTL` with `__runOriginal = false` that ages every zone's `m_ttl` (unchanged
  from vanilla), then **collects all expired, instance-free zone keys into a temporary list first** (never
  mutating `m_zones` while enumerating it — removing the `break` from the original loop and continuing to
  enumerate would throw `InvalidOperationException` on the next `MoveNext()`, since the vanilla loop's
  safety depended entirely on stopping immediately after its one `Remove`), then destroys and removes every
  collected entry in a second pass:
  ```csharp
  [HarmonyPatch(typeof(ZoneSystem), "UpdateTTL")]
  [HarmonyPrefix]
  private static bool Prefix(ZoneSystem __instance, float dt)
  {
      if (!Features.ServerZoneSim.Active) return true; // vanilla single-destroy-per-tick when off
      var zones = ZoneSystemAccess.Zones(__instance); // AccessTools field ref to private m_zones
      foreach (var kv in zones) kv.Value.m_ttl += dt;
      s_expiredBuffer.Clear();
      foreach (var kv in zones)
      {
          if (kv.Value.m_ttl > __instance.m_zoneTTL && !ZNetScene.instance.HaveInstanceInSector(kv.Key))
              s_expiredBuffer.Add(kv.Key);
      }
      foreach (var key in s_expiredBuffer)
      {
          UnityEngine.Object.Destroy(zones[key].m_root);
          zones.Remove(key);
      }
      Telemetry.ZoneSim.ZonesTTLDestroyedThisTick.Set(s_expiredBuffer.Count); // see Telemetry
      return false; // never run vanilla's own single-destroy version when the feature is active
  }
  ```
  `__runOriginal = false`/`return false` here only ever replaces a **destroy-more-per-tick** version of the
  exact same vanilla logic (same TTL comparison, same `HaveInstanceInSector` guard, same `Destroy`+`Remove`
  pair) — it changes the size of the backlog drain per tick, not what makes a zone eligible for destruction
  or what "destroy" does to it.

### Hook 5 (Z9) — `ZoneSim.ZoneCap`: object budget with hysteresis, consulted by Hook 2

- **Target:** no vanilla method patched. New mod-owned bookkeeping consulted by Hook 2 (before appending a
  zone's objects, shown inline above as `ZoneCap.CanInclude`). **RED-TEAM FIX — scope corrected (RT2
  blocker #2):** the prior draft also claimed Hook 1 ("before poking a new zone") consults `ZoneCap`; it
  never did in Hook 1's own code sketch, and it structurally shouldn't — `ZoneCap` counts **objects**
  (`ZDOMan.FindSectorObjects(...).Count`), which is the real CPU/RAM cost of Hook 2's instantiation, not of
  Hook 1's terrain builds. Hook 1's own, separate per-tick cost (real zones built) is now bounded by its own
  `MaxZoneBuildsPerTick` cap, added directly to Hook 1 above, so B1 does not ship dependent on B2's `ZoneCap`
  at all. `ZoneCap` gates Hook 2 only.
- **Patch kind:** none (pure new code, gate only).
- **Side:** server only.
- **Why this hook:** every "real" GameObject this module creates costs main-thread CPU and RAM; nothing in
  vanilla bounds it once fed a genuinely dense area (a built-up base, per research/04's cost model — a
  single 40m farm field alone reached 39,354 tracked objects in a different mod's field data). This is the
  circuit breaker the catalog requires before Hook 2's object feed is allowed to run unbounded.
- **RED-TEAM FIX — the estimate must be able to go down (RT1 major #2):** the prior code sketch only ever
  incremented `s_globalObjectEstimate` when a zone was newly included; nothing ever decremented it when a
  zone stopped being fed (peer moved away, disconnected, or a zone aged out). On a long-lived 24/7 server
  (archserver, alongside two other Valheim servers) every zone any peer ever passed near would permanently
  add to a running total that can only go up — once it exceeds `MaxObjectsGlobal`, `CanInclude` returns
  `false` for **every** zone, including already-included ones (`s_included.Contains` branch reads the same
  ever-growing total), silently and permanently self-locking the entire object feed with no recovery short
  of a process restart. Fixed by making the estimate a live sum over zones actually fed *this tick*,
  recomputed each `CreateObjects` prefix call instead of accumulated forever:
- **Code sketch:**

```csharp
internal static class ZoneCap
{
    private static readonly HashSet<Vector2s> s_includedThisTick = new(); // RED-TEAM FIX: rebuilt every tick
    private static int s_globalObjectEstimate;                            // = sum over s_includedThisTick

    public static void BeginTick() // called once at the top of Hook 2's prefix, before any CanInclude call
    {
        s_includedThisTick.Clear();
        s_globalObjectEstimate = 0;
    }

    public static bool CanInclude(Vector2s zone)
    {
        int estimate = PerZoneObjectEstimate(zone); // FindSectorObjects(zone,...).Count, cheap read
        bool underIncludeThreshold = s_globalObjectEstimate + estimate
            < Config.ZoneSim.MaxObjectsGlobal.Value * Config.ZoneSim.CapIncludeThresholdPct.Value; // 90%
        bool underExcludeThreshold = s_globalObjectEstimate + estimate
            < Config.ZoneSim.MaxObjectsGlobal.Value * Config.ZoneSim.CapExcludeThresholdPct.Value; // 100%
        // hysteresis: a zone already included this pass over the peer-ring set stays included up to 100%;
        // a zone not yet seen this tick only joins below 90%. Both branches always account it into the
        // running total below, so the total is always exactly "what's included this tick," never a
        // monotonically-growing lifetime sum.
        bool include = s_includedThisTick.Contains(zone) ? underExcludeThreshold : underIncludeThreshold;
        if (include) { s_includedThisTick.Add(zone); s_globalObjectEstimate += estimate; }
        else Telemetry.ZoneSim.CapHitCount.Increment();
        return include;
    }
}
```

  `PokeRingDriver.ActiveZones()`'s own iteration order (stable within a tick) makes `s_includedThisTick`'s
  "already seen this pass" check behave like the old `s_included` set did for hysteresis *within* one tick's
  set of candidate zones, without ever letting the estimate outlive the tick that produced it. `BeginTick`
  is called once from Hook 2's prefix before its `foreach (var peerRing in PokeRingDriver.ActiveZones())`
  loop.
- **Guards from the catalog, status:** cap check runs before appending/poking, never post-hoc (shown);
  never calls `ZNetScene.Destroy`/`DestroyZDO` as enforcement — "over budget" only ever means "refuse new
  work" (no destroy call exists anywhere in `ZoneCap`); log/alert on hit (shown, `Telemetry.ZoneSim.CapHitCount`);
  hysteresis at 90% include / 100% exclude (shown, now scoped to one tick, RED-TEAM FIX above) so a total
  oscillating near the cap doesn't flip a borderline zone's inclusion every tick within that single pass —
  confirmed this session that a zone excluded by `ZoneCap` decays through the same `RemoveObjects` sweep
  and grace-hold Hook 2 now carries (see Hook 2's RED-TEAM FIX) — never reaching a `Persistent` ZDO, only
  ever a visual/gameplay churn risk for ephemeral content, not a data-loss one.
- **Kill switch off:** `Features.ServerZoneSim.Active` is checked by Hook 2 (its only caller) before
  `ZoneCap` is ever consulted; with the feature off, `ZoneCap` is simply never called.

### Hook 6 (Z6) — `ZNetScene.CreateObjects` per-frame instantiation budget

- **Target:** `ZNetScene.CreateObjects` — same method as Hook 2, different literal: `int
  maxCreatedPerFrame = 10; if (InLoadingScreen()) { maxCreatedPerFrame = 100; }`. `SERVER:82139-82150` /
  `CLIENT:82369-82380`, confirmed identical both builds, including `InLoadingScreen()`
  (SERVER:82130-82136/CLIENT:82360-82367): `Player.m_localPlayer == null ? true :
  Player.m_localPlayer.IsTeleporting()`.
- **Patch kind:** `[HarmonyTranspiler]` replacing the `100` constant with a config read. **The `10`
  literal must be left alone and not targeted** — re-confirmed directly this session:
  `Player.m_localPlayer` is structurally always null on a true out-of-process dedicated server (no local
  character ever spawns there), so `InLoadingScreen()` always returns `true` and the server always takes
  the `100` branch. A transpiler that only swaps the `10` literal compiles cleanly and does precisely
  nothing on a real dedicated server — this is the exact footgun the catalog flags.
- **Side:** server only in scope (the same dead-branch fact makes this irrelevant to a normal player
  client, whose `InLoadingScreen()` genuinely toggles with actual loading state).
- **Why this hook:** raising the per-frame instantiation cap directly speeds a cold ring's population
  (join, teleport, fast travel) without touching cadence (leave 30Hz alone by default — a 60Hz option
  exists in the config but defaults to vanilla's 30Hz since `CreateObjectsSorted` already self-scales its
  effective budget once the backlog exceeds ~100x the configured literal, confirmed:
  `Mathf.Max(m_tempCurrentObjects2.Count / 100, maxCreatedPerFrame)`, SERVER:82169).
- **Code sketch:**

```csharp
var matcher = new CodeMatcher(instructions)
    .MatchStartForward(new CodeMatch(OpCodes.Ldc_I4_S, (sbyte)100)) // the InLoadingScreen==true branch
    .ThrowIfInvalid("NoodlesMcDoodles: Z6 anchor (ldc.i4.s 100) not found in ZNetScene.CreateObjects");
matcher.SetOperandAndAdvance(
    CodeInstruction.Call(typeof(Config.ZoneSim), nameof(Config.ZoneSim.EffectiveMaxCreatedPerFrame)));
```

`EffectiveMaxCreatedPerFrame()` itself returns the vanilla `100` when `Features.ServerZoneSim.Active` is
false, satisfying the kill-switch contract without a second guard inside the transpiled call.
- **Guard:** log which branch (`10` vs `100`) is structurally active at plugin startup so an operator
  auditing this hook's effect doesn't chase the wrong literal by hand later (catalog's explicit ask).

### Hook 7 (Z7) — `SnapToGround.SnappAll` defer-instead-of-drain guard

- **Target:** `SnapToGround.SnappAll()` — public **static** method on `SnapToGround : MonoBehaviour`.
  `SERVER:142635-142649` / `CLIENT:143551-143565`. Confirmed present, identical body, both builds:
  unconditionally calls `Heightmap.ForceGenerateAll()`, then iterates the entire private static
  `m_allSnappers` list (`SERVER:142595`/`CLIENT:143511`) calling `Snap()` on every entry and clearing the
  list. `HaveUnsnapped()` (`SERVER:142630-142633`) has zero callers anywhere in either assembly (confirmed
  by grep this session) — nothing today polls "are there still deferred snappers waiting."
- **Patch kind:** `[HarmonyPatch(typeof(SnapToGround), "SnappAll")] [HarmonyPrefix]` with `__runOriginal =
  false` (full replacement), plus a new periodic re-trigger driven off Hook 3's tick.
- **Side:** either build technically compiles this patch; it matters here specifically because Hook 1's
  poke-ring driver triggers world-gen (`PlaceLocations`, first-time zone generation) far more often and
  across far more of the map than vanilla's single static reference position ever did — every trader,
  every `dvergrprops_wood_stakewall`, anywhere any peer's ring sweeps past mid-terrain-bake, is a Door-B
  mis-snap candidate under this feature specifically (research/04 hazard 2).
- **Code sketch:**

```csharp
[HarmonyPatch(typeof(SnapToGround), "SnappAll")]
[HarmonyPrefix]
private static bool Prefix()
{
    if (!Features.ServerZoneSim.Active) return true; // vanilla drain-all behavior when off
    var all = SnapperList.Get(); // AccessTools.StaticFieldRefAccess<List<SnapToGround>>(typeof(SnapToGround), "m_allSnappers")
    if (all.Count == 0) return false;
    Heightmap.ForceGenerateAll();
    for (int i = all.Count - 1; i >= 0; i--)
    {
        var snapper = all[i];
        if (!IsZoneReady(snapper)) continue; // m_zones.ContainsKey(ZoneSystem.GetZone(snapper.transform.position))
        snapper.Snap();
        SnapperInList.Set(snapper, false);
        all.RemoveAt(i);
        Telemetry.ZoneSim.SnapRetried.Increment();
    }
    return false; // never run vanilla's own unconditional Clear()
}

// re-trigger, called from Hook 3's post-tick, at the same cadence as Config.ZoneSim.UpdateTickSeconds
internal static void RetryDeferred() => SnapToGround.SnappAll(); // re-enters the patched version above
```

- **Guards from the catalog, status:** filter to only zone-ready entries before snapping, leaving the rest
  (shown); process-global, not per-anchor, since `m_allSnappers` is `static` (shown — the patch touches
  the one shared list, not a per-peer copy); **mandatory periodic re-trigger** (shown, `RetryDeferred`
  wired to Hook 3's tick) so a deferred entry cannot sit permanently un-snapped the way it structurally
  could today (`HaveUnsnapped` having zero callers means nothing else will ever look again).
- **Kill switch off:** prefix returns `true` on its first line, restoring vanilla's exact drain-all
  behavior (which is also what happens if this mod is never installed at all).

### Hook 8 (Z8 residual / G6-N2) — `ZoneSim.TerrainReadinessGate` + `TryCleanInvalidTCs` canary

Z8's originally-proposed mitigation ("never build real terrain under a peer's own feet") is **withdrawn**
per Lead verification item 2 — it would forbid this module's entire purpose and the cross-machine
duplicate race it was written to prevent does not exist (`TerrainComp.Awake`'s `FindTerrainCompiler`
searches only this **process's own** `s_instances`/`s_duplicateInstances`, confirmed static fields local
to one assembly's runtime, SERVER:143619-143623 declaration, SERVER:143626-143643 `Awake`, both re-read
directly this session and confirmed identical at CLIENT:144506, 144542-144559). The real, narrower residual
Lead item 2 leaves open: **a server-authored terrain edit (a future S-group creature/projectile mechanic
calling into `TerrainOp`) firing in a zone whose existing persistent compiler ZDO this process has not yet
instantiated** creates a genuinely duplicate compiler ZDO via `Heightmap.GetAndCreateTerrainCompiler`
(SERVER:129416-129424, not independently re-read this session — cited from research/04, `[unverified]`
line range not re-confirmed by this doc directly) — and vanilla's own resolution for that is destructive
(`TryCleanInvalidTCs`, SERVER:143647-143681 / CLIENT:144563-144597, confirmed this session: keeps whichever
duplicate has more `m_operations`, unconditionally calls `ZNetScene.instance.Destroy(...)` on the loser,
with **no Persistent check anywhere in the method**).

- **Target 1 (telemetry canary):** `TerrainComp.TryCleanInvalidTCs()` — private instance method.
  `SERVER:143652-143681` / `CLIENT:144568-144597` (`Start()` calls it unconditionally, confirmed both
  builds: `SERVER:143647-143650`/`CLIENT:144563-144566`). Patch kind: `[HarmonyPostfix]`. Side: either
  (relevant on server for this module; equally valid to install client-side since the method is
  identical). Fires only when `s_duplicateInstances.Count > 0` — in a correctly-ordered deployment this
  postfix should almost never fire once terrain-before-objects ordering (Hook 1 → Hook 2, already
  mandatory) is respected; a nonzero rate is the live signal that ordering is being violated somewhere,
  including by another mod's own terrain-editing feature.

```csharp
[HarmonyPatch(typeof(TerrainComp), "TryCleanInvalidTCs")]
[HarmonyPostfix]
private static void Postfix(TerrainComp __instance)
{
    Telemetry.ZoneSim.TerrainCompDuplicatesObserved.Increment(); // canary — see Telemetry section
}
```

- **Target 2 (the actual guard):** no vanilla method patched — new gate consulted before any future
  S-group mechanic is allowed to run a server-authored `TerrainOp` in a given zone (out of this doc's own
  scope to design that mechanic; this doc ships the gate it must call). `ZoneSim.TerrainReadinessGate.IsReady(zone)`
  returns true only once that zone's `Heightmap` root exists (`m_zones.ContainsKey`, via Hook 1) **and**
  Hook 2's object feed has already appended every ZDO in that zone whose prefab is the `TerrainComp` prefab
  (i.e., any pre-existing compiler ZDO for that ground has already been instantiated in this process
  before anything is allowed to create a competing one).
- **Why this hook and not a stronger lock:** no pre-emptive reservation/lock primitive exists anywhere in
  the decompile for this class (confirmed — `TerrainComp.Awake` has no mutex, no "claim intent" ZDO field,
  nothing beyond the reactive, destructive cleanup in `Start()`). The smallest hook that reduces the
  window without inventing new persisted state is enforcing terrain-then-objects-then-server-authored-edits
  ordering, strictly, per zone — which Hooks 1 and 2 already require for an unrelated reason (the
  terrain-before-objects `TerrainComp.Awake`-finds-no-`Heightmap` hazard, research/04 hazard 4). This doc
  does **not** claim that ordering fully closes the race — it narrows the window to "between this zone's
  compiler ZDO being fed and the next `ZNetScene` create pass instantiating it," which is bounded by one
  `1/30s` tick, not unbounded. **This is why the catalog verdict stays NEEDS-LIVE-TEST for both Z8 and
  G6-N2** — this doc reduces, but does not decompile-provably eliminate, the residual.
- **Kill switch off:** the postfix (harmless, additive-only) keeps running even when the feature flag is
  off — it is pure observation of vanilla behavior and costs nothing to leave active; the readiness gate is
  simply never consulted because nothing calls it when `Features.ServerZoneSim.Active` is false.

### Z10 — no hook

Read-only confirmation, re-verified directly this session: `Heightmap.Awake`/`RebuildCollisionMesh`
(grepped the whole class body, SERVER:128238-128460/128733-128781 per research/04, not re-opened line by
line this session since it is a negative-result grep, not a call-order claim) and `Water`/`WaterVolume`
carry no `SystemInfo.graphicsDeviceType`/`Application.isBatchMode` guard; `ClutterSystem.Awake` alone gates
on `SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null` (visual-only, no gameplay effect). This
doc's own new evidence (Hooks 1-7 above, all citing methods that run identically on both builds with no
batch-mode guard anywhere in their call chains) is independent, direct confirmation of the same
conclusion: nothing this module hooks is graphics-gated.

---

## Data-loss analysis

| Path | Guard |
|---|---|
| **Crash while a poke/append is mid-flight** | No persisted-data write happens as part of instantiating a GameObject from an already-existing ZDO (Hooks 1, 2, 6) — `ZDOMan`'s ZDO table is untouched by local `GameObject` lifecycle. The only ZDO-creating path in scope is first-time zone generation (`SpawnZone`'s `Ghost`/`Full` branches, gated by Hook 3's cap), which uses vanilla's own `ZoneSystem`/`ZNetView.Awake` ZDO-creation path unmodified — no new failure mode versus vanilla's own first-time generation. |
| **Peer disconnects mid-ring** | **RED-TEAM FIX — corrected (RT1 blocker #1):** Hook 1's driver reads `ZNet.instance.GetPeers()` fresh every tick; a disconnected peer simply stops appearing, so their ring stops being fed starting the very next tick. This does **not** age out via Z4's TTL as previously claimed here — Z4/`UpdateTTL` only ever destroys a zone's terrain root (`m_root`), never touches individual object instances (confirmed SERVER:115548-115562, no reference to `m_instances` anywhere in the method). The real path is Hook 2's grace-hold followed by `RemoveObjects`'s ordinary `1/30s` sweep once the grace window elapses (see Hook 2). That sweep can only ever destroy a `!Persistent && IsOwner()` ZDO's GameObject (and, for such ZDOs, the ZDO itself via a broadcast `DestroyZDO`, SERVER:82267-82269/76976-76996) — never a `Persistent` one, so no world/player save data is at risk; the visible effect is bounded, grace-held, and counted (`ObjectsDestroyedOnUnfeed`), not a special-cased "peer left, wipe their zone" branch that could race a save. |
| **Peer reconnects** | Hook 1's de-dup (`m_pokedThisTick`) and the "first real position received" gate (Z2 guard 7) mean a reconnecting peer's ring is rebuilt from zero exactly as a first-time join is — no stale state from the previous session's ring persists in this module's own bookkeeping. **RED-TEAM FIX:** `ZoneCap`'s estimate is now rebuilt every tick from scratch (`BeginTick`, see Hook 5), not accumulated across a peer's session, so a returning peer's zones re-enter the same live accounting a fresh peer's would with no reference to the prior session at all. |
| **World save fires mid-poke/mid-append** | `PrepareSave`'s ~0.5s main-thread block (measured, baseline/2026-09-28) and this module's per-tick work are both plain synchronous main-thread calls with no cross-thread handoff in this doc's scope — Unity's single-threaded message loop means a save tick and a `ZoneSystem`/`ZNetScene` tick cannot interleave mid-call, only interleave *between* ticks. No torn-state path exists that this doc's own hooks introduce; the existing P1/P3 guards (owned by the persistence blueprint, `docs/design/06-*.md`) already require any config-reload-triggered save to be marshaled onto the main thread — this module produces no additional cross-thread call site. |
| **Kill switch flips mid-operation** | Every hook checks `Features.ServerZoneSim.Active` at entry (Hooks 1, 2, 3's transpiled reads, 4, 6's transpiled read, 7's prefix). None of them ever call `ZNetScene.Destroy`/`ZDOMan.DestroyZDO`/`ZDO.SetOwner` as part of normal operation (`ZoneCap` explicitly never destroys; the only `DestroyZDO` calls anywhere near this module's hot paths are vanilla's own pre-existing invalid-prefab branches in `CreateObjectsSorted`/`CreateDistantObjects`, which Hook 2's mandatory guard prevents this module's own fed ZDOs from ever reaching). **RED-TEAM FIX — corrected (RT1 blocker #1 / RT2 blocker #1):** flipping the flag off mid-tick does stop future feeding immediately, and already-instantiated GameObjects do **not** decay via "vanilla TTL/sweep" as one mechanism — see the corrected "Peer disconnects" row above for the two genuinely distinct mechanisms (15s zone-root TTL vs. the grace-held `1/30s` object-instance sweep) and their different timeframes. Test plan step 6 now explicitly checks the object-instance path, not only the zone-root TTL path. No ownership was ever taken by this module, so there is nothing to "hand back" in the ownership sense — that responsibility belongs entirely to the separate ownership blueprint's own `G6-N3` hold-guard flush requirement. |
| **Missing content mod on the server (Z3's core hazard)** | The mandatory prefab-resolution guard (Hook 2) is the entire mitigation: any ZDO whose prefab does not resolve via `ZNetScene.instance.GetPrefab(hash)` on this process is skipped, never appended, and therefore never reaches vanilla's destructive `SetOwner`+`DestroyZDO` branch in either `CreateObjectsSorted` or `CreateDistantObjects`. A skip is logged and counted (`Telemetry.ZoneSim.ObjectsSkippedMissingPrefab`); a nonzero count in production is an operational alert ("server is missing a content mod its clients have"), not a data-loss event, precisely because the guard stops the ZDO from ever being fed in the first place. |
| **`TerrainComp` duplicate race (Z8 residual / G6-N2)** | Not fully closed by any hook in this doc — see Hook 8. The terrain-before-objects ordering already mandatory for Hooks 1→2 narrows the window to at most one `1/30s` `ZNetScene` tick; the canary postfix on `TryCleanInvalidTCs` makes any real-world occurrence visible in telemetry rather than silent. **This residual is the one item in this doc's scope that ships as NEEDS-LIVE-TEST, not SAFE-WITH-GUARDS-closed** — see Open questions and Test plan. |
| **Fed non-`Persistent`, server-owned ZDO stops being re-fed (RT1 blocker #1 / RT2 blocker #1, new row)** | **RED-TEAM FIX, added row.** Covered in full in Hook 2's own section. Summary: `RemoveObjects`'s destroy branch (SERVER:82267-82269) is gated on `!zDO.Persistent && zDO.IsOwner()` — it structurally **can never reach a `Persistent` ZDO**, which is the only category of ZDO Valheim's own save system writes to disk. So this path cannot lose, roll back, or corrupt world/player save data under any sequence of kill-switch flips, disconnects, or `ZoneCap` evictions. It can, before this session's fix, cause a same-tick pop of ephemeral (never-saved) content the instant a zone stops being fed; Hook 2's new grace-hold (`GraceHold`, `ObjectUnfeedGraceTicks`) bounds that to ordinary ring-boundary churn, and `Telemetry.ZoneSim.ObjectsDestroyedOnUnfeed` makes any occurrence in production visible rather than silent. Whether today's world-gen content (`PlaceLocations`/`PlaceVegetation`/`PlaceZoneCtrl`) or a future S1 SpawnSystem creature ever actually produces a non-`Persistent`, server-owned ZDO in practice is a Unity-prefab-asset question this doc's decompile-only ground truth cannot answer — flagged `[unverified]`, carried to Open questions and Test plan. |

**Stand-down path (over budget or kill switch), explicit:** this module never removes ownership from a
peer (it never calls `SetOwner` at all) and never destroys, or causes the destruction of, a `Persistent`
ZDO as an enforcement action — confirmed this session, `RemoveObjects`'s only destructive branch is
unconditionally gated on `!Persistent` (SERVER:82267). **RED-TEAM FIX — corrected:** it is not true, as
the prior draft claimed, that this module "never destroys... an already-instantiated GameObject as an
enforcement action" — the ordinary `RemoveObjects` sweep, once a fed zone's grace window (Hook 2) elapses,
does destroy the local `GameObject` for any object this module stops feeding, exactly as it would for any
other object that fell out of a peer's simulation range under pure vanilla. "Hand zones back to players"
in the *ownership* sense is still not a distinct code path this module must implement — it is the automatic
consequence of this module simply stopping: `ReleaseNearbyZDOS`'s peer-proximity branch (owned by the
ownership blueprint, unmodified by this doc) continues to assign every `Persistent` ZDO near a connected
peer to that peer exactly as it does in pure vanilla, the entire time this module runs and after it stops.
`ZoneCap` and the kill switch both only ever gate **new** poke/append work; neither ever tears down
`Persistent` state, and the grace-hold bounds how quickly non-`Persistent` state this module fed can be
swept once feeding stops.

---

## Vanilla and version compatibility

- **Vanilla client + modded server:** every hook in this doc is server-side-only in effect (Hooks 3, 4, 6,
  7's `SnapToGround` correctness fix, and the `TryCleanInvalidTCs` canary are shared-code patches that
  happen to also compile for a client build, but nothing in this doc changes what is sent over the wire —
  confirmed by research/04 §9: `ZDOMan.CreateSyncList` already streams each peer's replication set from
  `peer.m_peer.GetRefPos()`/`peer.m_peer.m_simulationDistance` independent of `ZNetScene`'s own single
  reference position, so a vanilla client's actual gameplay experience — what it receives, what it can
  interact with — is unaffected by whether the *server* has a real local GameObject for the same ZDO. A
  vanilla client cannot detect this module is running from its own network traffic.
- **Modded client + vanilla server:** none of this doc's hooks require a client-side counterpart to
  function (`clientMod: false` for every lever in scope per `levers.json`). A modded client connecting to
  a vanilla server simply never loads a server that has this module's `ZoneSim` component at all — no
  negotiation is needed for these specific hooks, unlike levers that do require both ends (e.g. `O4`/`G4-N1`
  peer-refpos cadence, out of this doc's scope).
- **Modded version skew (two servers/clients running different NoodlesMcDoodles builds):** this doc adds
  no new wire format, RPC, or ZDO schema of its own (the only ZDOs touched are vanilla's own, fed through
  vanilla's own creation/replication path) — the shared infra's version-tagged handshake (owned by the
  rails blueprint) is not load-bearing for any hook in this doc specifically, though this module should
  still report its presence through that handshake for telemetry/ops visibility.
- **PlayFab/crossplay sockets:** out of this doc's scope structurally — every hook here operates on
  `ZoneSystem`/`ZNetScene`/`TerrainComp`, none of which branch on transport type (`ZSteamSocket` vs.
  `ZPlayFabSocket`); a crossplay client is just another `ZNetPeer` to Hook 1's ring driver.

---

## Configuration

All keys below live under the `[ZoneSim]` BepInEx config section, server-local (no client relevance for
any lever in this doc's scope — `clientMod: false` for all ten). None are pushed to clients over the P6
config-sync RPC; that mechanism is reserved for values a modded client's own code needs to read.

| Key | Default | Range | Notes |
|---|---|---|---|
| `Features.ServerZoneSim.Active` | `true` (once this module ships) | bool | Master kill switch for every hook in this doc; also console-command-toggleable per the rails contract. |
| `ZoneSim.PokeRingRadius` | `2` (= vanilla `NearSimulationDistance` at default level) | 1-5, always clamped to `min(value, GetSyncedSimulationDistance().NearSimulationDistance)` at read time | Hook 1. **CORRECTED (held-band redesign synthesis, 2026-09-28):** larger than the negotiated simulation distance buys nothing for a PEER-anchored call (`ReleaseNearbyZDOS`/`CreateSyncList` still use the negotiated value there) — but this is not a universal statement about the ring itself. O6's own claim call (`ZoneClaimSweep.ClaimZone`) anchors on the CANDIDATE ZONE's own centre, not on a peer, so widening the effective ring via `ZoneSim.HeldBandRingBonus` (below) does buy real held-zone coverage for O6 specifically. |
| `ZoneSim.HeldBandRingBonus` **(new, held-band redesign synthesis, 2026-09-28)** | `1` (default on, user decision 2026-09-28) | 0-2 | Hook 1 + O6. Additive term on `PokeRingDriver.EffectiveRingRadius()` — the SAME ring value Hook 1's terrain build, Hook 2's object feed, AND O6's claim-eligibility test (`ZoneSimGate.CanHold`) all read, never a second independently-tuned radius. `0` reproduces the ring O6 has always seen (byte-for-byte). `1` widens it to reach a real held band ~160-224m out from each peer at default `PokeRingRadius`/`PeerRefPosPadding` — see the subsection below. `2` is opt-in headroom (<288m). Pair a nonzero value with `MaxObjectsPerPeer`/`MaxObjectsGlobal` (sized for `1` by default as of this synthesis — see docs/impl/ZONESIM-IMPL.md). |
| `ZoneSim.MaxZoneBuildsPerTick` **(RED-TEAM FIX, new — RT2 blocker #2)** | `4` | 1-16 | Hook 1's own per-tick cap on real zone construction (`Instantiate`+`Heightmap`+`MeshCollider`), independent of `ZoneCap`/Z9 which is B2-scoped and object-count-only. Ships with B1 so Hook 1 never depends on B2 for a cost bound. Tune from Test plan step 11. |
| `ZoneSim.ZoneTTLSeconds` | **15** (user target) | 4-20, hard-clamped, `IsServer()`-gated | Hook 4. |
| `ZoneSim.UpdateTickSeconds` | **0.05** (user target) | 0.05-0.1 | Hook 3. Shipping below 0.05 is not recommended without re-measuring Open question 2's per-tick cost on archserver first. **RED-TEAM FIX:** now also the `dt` fed to `UpdateTTL`'s per-call literal, so TTL decay stays wall-clock-accurate at whatever cadence is configured (RT2 blocker #3). |
| `ZoneSim.MaxGhostSpawnsPerTick` | `1` | 1-4 | Hook 3's mandatory companion cap. Raising it trades a bigger worst-case per-tick hitch for faster cold-world population; default matches vanilla's existing single-build discipline. **RED-TEAM FIX:** serviced round-robin across peers, not `GetPeers()`'s fixed join order (RT1 major #3). |
| `ZoneSim.MaxCreatedPerFrame` | `100` (matches vanilla's already-active dedicated-server branch) | 100-400 | Hook 6. Values above ~100x the object backlog have no additional effect per `CreateObjectsSorted`'s own self-scaling (`Mathf.Max(pending/100, budget)`). |
| `ZoneSim.CreateDestroyHz` | `30` (vanilla) | 30 or 60 | Hook 6's companion cadence knob; 60Hz is a straightforward 2x CPU cost, left at vanilla by default since it wasn't part of the user's selected numeric targets. |
| `ZoneSim.ObjectUnfeedGraceTicks` **(RED-TEAM FIX, new — RT1/RT2 blocker #1)** | `3` (= ~150ms at the 0.05s tick target) | 0-20 | Hook 2's grace-hold: how many extra ticks a fed, non-`Persistent`, server-owned ZDO stays earmarked after its zone drops out of the feed, before `RemoveObjects`'s ordinary sweep is allowed to reach it. `0` restores the pre-fix, immediate-sweep behavior; tune upward only if `ObjectsDestroyedOnUnfeed` shows real churn in Test plan step 11/live play. |
| `ZoneSim.MaxObjectsPerPeer` | **unset — must be set from a testbed run before shipping B2** | hardware-dependent | Hook 5. Research/04's cost model (a different mod's field data) puts a single vanilla-density 5x5 ring around 2,500 objects, sharply higher in a built-up base — no default ships without archserver profiling (Open question 3). |
| `ZoneSim.MaxObjectsGlobal` | **unset — derived as `MaxObjectsPerPeer * expected peak concurrent peers` after profiling** | hardware-dependent | Hook 5. |
| `ZoneSim.CapIncludeThresholdPct` / `CapExcludeThresholdPct` | `0.90` / `1.00` | fixed hysteresis band | Hook 5. **RED-TEAM FIX:** now evaluated against a per-tick-rebuilt estimate, never a lifetime-growing one (RT1 major #2). |

### The held band (`ZoneSim.HeldBandRingBonus`) — added 2026-09-28, held-band redesign synthesis

Resolves the `OPEN: server-held zones are inert at default geometry` finding (`docs/impl/INTEGRATION-REVIEW.md`):
at `HeldBandRingBonus=0`, O6's own exclusion radius (peer active area + `PeerRefPosPadding`, ~160m at defaults)
and Hook 1's ring reach (~160m at defaults) never intersect, so `ownership.serverHeldZones` stayed permanently 0
even with `OwnershipAuthority` on — the server "held" nothing in practice, only in name. `HeldBandRingBonus`
widens the ONE ring value Hook 1's build loop, Hook 2's object feed, and O6's claim-eligibility test all read
(`PokeRingDriver.EffectiveRingRadius`), so at `1` (the shipped default) the server reaches and actually claims a
real, already-simulated band roughly 160-224m out from each peer — structurally impossible to hold a zone that
was never fed real objects, since both the claim test and the feed read the exact same value.

**Caveat (re-derived this pass, not previously stated as clearly): the "(160,224] exact shell" framing above
describes the DEFAULT `PokeRingRadius=2` / `PeerRefPosPadding=64` case specifically, not a universal invariant.**
`PokeRingRadius` is independently operator-configurable 1-5 (see its own table row above), and combined with
O1's own hard clamp of `NearSimulationDistance` at level 6 (`SimulationDistanceCeiling.cs`'s
`AbsoluteHardClampLevel`), the ring's real reach can already exceed even the maximum padded exclusion boundary
(288m at `PeerRefPosPadding=192`) through `PokeRingRadius` alone, independent of `HeldBandRingBonus` — this is
not a new risk this feature introduces, it is a pre-existing property of the config space this doc's own numbers
had not previously spelled out. This is why the module's real safety guarantee has always rested on the
hardened claim/reclaim/hold primitives (`ReclaimGate`, `HandoverGuard`, `GraceHold`, `ZoneCap`, `ZoneTtl`,
`SetOwnerHoldGuard`) actually carrying out any claim safely, not on the ring/exclusion geometry being provably
disjoint at every possible configuration — a future maintainer should not re-derive a false "always disjoint"
invariant from the shipped-default numbers above.

---

## Telemetry

All counters below are `NoodlesMcDoodles.Core.Telemetry.ZoneSim.*`, periodic (not per-event) `LogInfo`
summaries per the rails contract's P7 pattern — never a per-call log inside a 20-30Hz loop.

- `ZonesRealActive` (gauge) — current `m_zones.Count`-equivalent count this module is responsible for
  (read via the same private-field access Hook 1 already needs), i.e. how many real terrain blocks exist
  right now versus vanilla's permanent 1.
- `ZonesPokedTotal` / `GhostSpawnsThisTick` (the latter should never exceed `MaxGhostSpawnsPerTick` — a
  violation means Hook 3's cap has a bug, alert-worthy).
- `ZoneBuildsThisTick` **(RED-TEAM FIX, new)** — should never exceed `MaxZoneBuildsPerTick`; a violation
  means Hook 1's new cap has a bug, alert-worthy (RT2 blocker #2).
- `ZonesTTLDestroyedThisTick` **(RED-TEAM FIX, new)** — gauge/count from Hook 4's backlog-drain fix; a
  sustained run of values `> 1` is the operational signal that many rings aged out together (mass
  disconnect, kill switch flip, dispersing group) — informational now that the backlog no longer lingers
  for multiple ticks (RT2 major #4).
- `ObjectsAppendedTotal`, `ObjectsSkippedMissingPrefab` (should be 0 in a clean deployment; nonzero is an
  operational signal the server is missing a content mod its clients run — see Data-loss analysis).
- `ObjectsDestroyedOnUnfeed` **(RED-TEAM FIX, new — RT1/RT2 blocker #1)** — increments whenever a fed
  ZDO's grace window (`ObjectUnfeedGraceTicks`) expires and `RemoveObjects`'s destroy branch reaches it.
  Can only ever count non-`Persistent`, server-owned ZDOs (the branch's own vanilla gate), so this is the
  live proof, not just the theoretical argument, that no `Persistent`/world-save data is ever touched;
  should be near-zero in ordinary play and is the counter Test plan step 11/live measurement should watch
  first to decide whether `ObjectUnfeedGraceTicks` needs raising.
- `CapHitCount` (Hook 5), `CapCurrentlyOverBudget` (gauge). **RED-TEAM FIX:** `CapCurrentlyOverBudget` now
  reads a per-tick-rebuilt estimate (never a lifetime-growing one, RT1 major #2) — a server that has been
  up for days and shows this gauge permanently pegged at "over" with no peers nearby indicates the
  decrement fix itself has a bug, alert-worthy.
- `TerrainCompDuplicatesObserved` (Hook 8's canary — the single most important number for proving/disproving
  the Z8/G6-N2 residual is real in practice, not just in theory).
- `SnapDeferredCount` / `SnapRetriedCount` (Hook 7 — a large, non-shrinking deferred count over time would
  mean the re-trigger isn't keeping up, an operational signal distinct from the correctness guard itself).
- `MainThreadMsPerTick` (p50/p95/p99, combining Hooks 1+2+6's per-tick cost) — the direct answer to Open
  question 2 below, once measured live.

Every counter here is purely additive/observational (postfix-only or new code, never a prefix-skip), per
the P7 guard this doc inherits from the rails contract.

---

## Interactions with other subsystems

- **Ownership (`docs/design/03-ownership-and-authority.md`)** — hard dependency, one direction only: the
  ownership blueprint's `G3-L1` (`IsInPeerActiveArea` postfix) and `O6`/`O7`/`O8` (server-held ownership
  sweep, hold-guard, hand-over RPC) need this doc's real GameObjects to exist before claiming ownership of
  anything near them is meaningful. This doc does not call into, patch, or depend on any ownership-blueprint
  hook — a clean one-way dependency.
- **Transport / send queues (`docs/design/01-*.md`, `02-*.md`)** — no direct coupling. This doc changes
  what the server has *locally instantiated*, not what gets sent to any peer (research/04 §9, re-confirmed:
  `CreateSyncList` sources from each peer's own position/simulation-distance regardless of `ZNetScene`'s
  reference position). If the ownership blueprint later makes the server the *sender* of more ZDOs (because
  it now owns objects this doc gave it something real to own), that additional send volume is the Q-group
  levers' concern, not this doc's.
- **Gameplay under server authority (`docs/design/05-*.md`)** — the reverse dependency of the ownership
  one: `S1`/`S2` (SpawnSystem headless-fatal-gate removal, AI tick routing) need this doc's Z3 object
  instantiation and Z2 terrain to have any zone content to spawn into or any `MonsterAI` component to route
  ticks for. Hook 8's `TerrainReadinessGate` is specifically built to be called by whatever S-group
  mechanic ends up authoring server-side `TerrainOp`s (creature terrain damage, siege mechanics) — this doc
  ships the gate, not the mechanic that must call it.
- **Persistence (`docs/design/06-*.md`)** — no new save-path interaction beyond what's covered in
  Data-loss analysis; P7's telemetry pattern and P1/P3's main-thread-marshaling requirement are consumed,
  not extended, by this doc.
- **Client smoothing (11) / delta-compression (12)** — no interaction. Both are client-received-data
  concerns; this doc is entirely about what the server has locally instantiated, upstream of any wire
  format.
- **Other mods (live server census, `research/13-mod-compat-census.md`).** **RED-TEAM FIX (RT2 minor #5):**
  the census was not available when this doc was first drafted; it is now (409 lines, this session). Its
  scan method is exhaustive, not a hand-picked watchlist checked at scan time — it walked every
  `[HarmonyPatch]`-derived attribute (type+method, IL metadata, not obfuscatable) in every plugin DLL on
  both the live server (49 DLLs) and the admin's client profile (60 DLLs), then reported every distinct
  `Type.Method` target actually found (148 of them across 1170 patch records). Re-checked this session,
  by name, against this doc's own 8 hook targets: `ZNetScene.CreateObjects` (the private two-`List<ZDO>`
  overload Hooks 2/6 patch), `ZoneSystem.Update`/`PokeLocalZone`/`CreateGhostZones`/`UpdateTTL`, and
  `SnapToGround.SnappAll` appear **nowhere** in the census (confirmed by direct grep of the census file,
  zero hits for any of them) — i.e. zero third-party patches found on any of them across all ~46-59 mods
  scanned. `TerrainComp`/`Heightmap` (Hook 8) are separately and explicitly listed as "Confirmed clean
  (zero patches found on either side, on any method)" (research/13, Method & scope section). The one
  adjacent, already-flagged item is `ZoneSystem.(class-wide)` skip-capable prefixes from Server_devcommands/
  BetterContinents/Wonderland[SERVER] — confirmed (research/13 §7, its own cited line range) these target
  only `RPC_SetGlobalKey`/`RPC_RemoveGlobalKey`/`Start`/`SetStartingGlobalKeys`/`SendGlobalKeys`, none of
  which this doc touches. This doc's "no conflicts" read for its own 8 hooks is now a confirmed result, not
  an absence-of-evidence assumption; Open question 5 is updated accordingly.
  - **BetterContinents** (custom heightmap/biome provider) — this doc's Hook 1/6 depend on
    `HeightmapBuilder.instance.IsTerrainReady` polling correctly under a modified terrain provider; the
    census confirms BetterContinents does not patch that method or any of this doc's 8 hook targets, but
    functional compatibility (does its custom terrain provider *behave* correctly under N moving poke
    rings, not just "is it unpatched") is still untested — remains an open question.
  - **AzuAntiCheat** (Harmony-patch integrity scan) — every Harmony patch this doc adds (Hooks 2, 3, 6, 7,
    8) will appear in its per-mod patch walk; no new risk beyond what any other NoodlesMcDoodles module
    already accepts by patching at all (already documented, unrelated to this doc, in the prior session's
    freeze investigation).
  - **Njord, ValkyriesCargo, Smoothbrain-ServerCharacters** — no known interaction; Hook 2's prefab-resolution
    guard is exactly the safety net that makes an unknown content mod's custom prefabs near a peer's ring
    fail safely (skip + log) rather than fail destructively.

---

## Test plan

**Testbed (single/multi real client, per `run-linux-1.0-server.sh`, isolated `-savedir`):**

1. **B1 smoke test:** connect one client, enable `Features.ServerZoneSim`, wait `MaxGhostSpawnsPerTick`-bounded
   settle time, then confirm via a new admin console command (dumps `ZonesRealActive`/`m_zones` keys) that a
   ring of real zones exists centered on the connected player's position — not `(1e6,0,1e6)`.
2. **Collider presence:** from the dumped zone list, raycast-probe (server-side debug command) that a
   `MeshCollider` genuinely exists at a known terrain point inside the ring — confirms Z10's feasibility
   claim in practice, not just by grep.
3. **Overlapping-ring de-dup:** two clients standing close enough that their rings overlap; confirm via
   `ZonesPokedTotal`/`ObjectsAppendedTotal` telemetry that shared zones are fed exactly once per tick, not
   twice.
4. **Missing-prefab guard:** temporarily disable one server-side content-mod plugin whose prefabs a test
   client still carries; confirm `ObjectsSkippedMissingPrefab` increments and the server log names the
   prefab hash, and confirm (critically) that the corresponding client-owned ZDOs are **not** deleted —
   diff the world save before/after.
5. **Disconnect/reconnect:** disconnect mid-ring, confirm the zone's terrain root ages out via
   `ZoneTTLSeconds` (not force-destroyed), confirm **separately** that any fed object instances decay only
   after `ObjectUnfeedGraceTicks` via `RemoveObjects`'s sweep (**RED-TEAM FIX, RT2 blocker #1** — the prior
   plan conflated these two distinct mechanisms and timeframes; test both explicitly now), confirm
   `ObjectsDestroyedOnUnfeed` only ever increments for non-`Persistent` ZDOs (diff the world save — a
   `Persistent` ZDO must never disappear from it); reconnect, confirm the ring rebuilds cleanly with no
   leftover state from `ZoneCap` (its estimate is rebuilt every tick, RT1 major #2, so there is nothing to
   leak across sessions).
6. **Kill switch mid-operation:** flip `Features.ServerZoneSim.Active` off while a ring is actively being
   built; confirm no exception, confirm the zone terrain root decays via vanilla TTL (Hook 4) **and**
   confirm any fed, non-`Persistent`, server-owned object instances are swept only after
   `ObjectUnfeedGraceTicks` via `RemoveObjects`, not instantly (**RED-TEAM FIX** — this is the scenario RT1
   blocker #1's evidence traced directly: a bare flag flip previously risked an immediate, ungraced sweep),
   confirm no `Persistent` ZDO is ever affected, confirm no ownership change occurred at any point (this
   module never calls `SetOwner`).
7. **ZoneCap self-lock regression (RED-TEAM FIX, new — RT1 major #2):** run a long soak (hours, or a
   simulated fast-forward of `ZoneCap.BeginTick`/`CanInclude` calls) with peers continuously entering and
   leaving many different zones; confirm `CapCurrentlyOverBudget`/`s_globalObjectEstimate` tracks only the
   zones fed *this tick*, never grows unbounded, and that the object feed recovers immediately once demand
   drops — the exact failure mode ("refuses everything permanently, no recovery short of a restart") the
   prior design would have hit on a 24/7 server.
8. **Ghost-spawn fairness (RED-TEAM FIX, new — RT1 major #3):** several peers grouped together, all
   simultaneously needing first-time zone generation for several ticks in a row (a fresh world or a
   post-restart cold cache); confirm via per-peer `GhostSpawnsThisTick` attribution that the round-robin
   start index visibly rotates and no single peer (in particular, not just whichever joined first) is
   starved across the whole run.
9. **Zone-build cap / TTL backlog drain (RED-TEAM FIX, new — RT2 blocker #2 / major #4):** scatter enough
   peers (or simulate enough rings) that more than `MaxZoneBuildsPerTick` not-yet-real zones are eligible in
   one tick; confirm `ZoneBuildsThisTick` never exceeds the cap and that the overflow zones are built on
   later ticks, not dropped. Separately, force many zones past their TTL simultaneously (e.g. disconnect a
   large group at once); confirm `ZonesTTLDestroyedThisTick` can exceed 1 in a single tick and the backlog
   drains within one `Update` call rather than lingering for seconds.
10. **Z8/G6-N2 live test (per the catalog's own required recipe):** with a real client actively terraforming
   continuously at a zone boundary while that same zone is also in the client's own poke-ring, force a
   server-authored `TerrainOp` (once the S-group mechanic that would trigger one exists) in an
   as-yet-uninstantiated compiler zone; watch `TerrainCompDuplicatesObserved` and diff `.db2` terrain data
   before/after per operation count. Until the S-group mechanic exists, this test can only be simulated by
   directly invoking `Heightmap.GetAndCreateTerrainCompiler` from a debug command in a deliberately
   not-yet-fed zone.
11. **Memory/CPU budget:** 1-6 simulated peers scattered across biomes of varying density (open wilderness
   vs. a dense base), measured on archserver (12 cores / 31GB, shared with 2 other Valheim servers) —
   the only way to turn the cost model's order-of-magnitude estimates into real `MaxObjectsPerPeer`/
   `MaxObjectsGlobal` defaults, and to pick real `ObjectUnfeedGraceTicks`/`MaxZoneBuildsPerTick` defaults
   from `ObjectsDestroyedOnUnfeed`/`ZoneBuildsThisTick` telemetry under real load (RED-TEAM FIX additions
   above).

**Live measurement with NoodlesProbe / telemetry (once on Proxima or a comparable live server):** watch
`MainThreadMsPerTick` p95 during normal play, `ZonesRealActive` vs. connected-peer count (should track
1:1 modulo overlap), `SnapDeferredCount` trend (should not grow unbounded), `TerrainCompDuplicatesObserved`
rate (target: zero in normal play; any nonzero rate under ordinary terraforming, not the forced test in
step 10, is a real bug this doc's ordering guard failed to prevent).

---

## Open questions

1. **Does `HeightmapBuilder.instance.IsTerrainReady`'s worker-thread bake keep pace with N simultaneously-moving
   peer rings?** (carried from research/04, in scope for this doc's Hook 1/3.) Not decompile-answerable —
   requires the multi-peer testbed run (Test plan step 11).
2. **Real, profiled per-tick main-thread cost of Hooks 1+2+6 running once per connected peer at up to 20Hz
   (Z5's 0.05s target), versus vanilla's single pass** — this doc establishes the code can be re-driven
   this way; it has no measured number. `MaxObjectsPerPeer`/`MaxObjectsGlobal` cannot responsibly ship a
   default until this is measured (Test plan step 11).
3. **Does the 1.0.16 `TryCleanInvalidTCs` op-count-keeps-the-winner logic behave differently when the two
   competing `TerrainComp` instances are (a) a peer's own client vs. this server, versus (b) two purely
   server-side instantiations from Hook 8's residual case?** Not traced end-to-end for case (b)
   specifically this session — Hook 8's readiness gate is this doc's best decompile-derivable mitigation,
   not a proof of closure.
4. **Full `Water`/`WaterVolume`/`Pathfinding` update-logic audit beyond the graphics-guard grep** (Z10) —
   flagged `[unverified]` by research/04, not independently re-derived this session; out of this doc's own
   hook set but relevant to any future companion feature that leans on headless water/navmesh behavior.
5. **RED-TEAM FIX — narrowed (RT2 minor #5):** `research/13-mod-compat-census.md` is now available (409
   lines, read this session) and confirms zero third-party Harmony patches on any of this doc's 8 hook
   targets (`ZNetScene.CreateObjects`, `ZoneSystem.Update`/`PokeLocalZone`/`CreateGhostZones`/`UpdateTTL`,
   `SnapToGround.SnappAll`, `TerrainComp.TryCleanInvalidTCs`) across the 46-59 mods scanned on either
   machine — see the corrected "Other mods" section above. What remains open, narrowly: **BetterContinents'
   custom terrain provider's *functional* behavior** under this module's N simultaneously-moving poke
   rings (does `HeightmapBuilder.instance.IsTerrainReady` still report correctly, does its custom
   heightmap-generation timing interact with `MaxZoneBuildsPerTick`'s backlog?) — the census proves no
   Harmony *patch* conflict exists, but says nothing about runtime behavior of an unpatched but
   functionally different terrain provider. Requires a live BetterContinents test, not decompile-answerable.
6. **Whether Hook 8's terrain-before-objects ordering narrows the Z8/G6-N2 residual to zero in practice, or
   only reduces its rate** — this is precisely why both levers keep their NEEDS-LIVE-TEST verdict in the
   catalog; Test plan step 10 is the only way to answer it, and it cannot be fully exercised until an
   S-group mechanic that authors server-side `TerrainOp`s actually exists to drive the test.
7. **Unity's actual shipped physics tick rate on the dedicated server build** — no code-level override
   found in either assembly (confirmed: no `Time.fixedDeltaTime =` write, no `Physics.autoSimulation` write,
   no `m_isDedicated` field, all re-confirmed absent by research/04's grep, not independently re-grepped
   this session) — this is a project-settings fact, not decompile-derivable, and doesn't block anything in
   this doc's own scope but would matter to a future companion feature editing terrain/physics timing.
8. **RED-TEAM FIX — new (RT1 blocker #1 / RT2 blocker #1):** whether today's world-gen content
   (`PlaceLocations`/`PlaceVegetation`/`PlaceZoneCtrl`, run inside `SpawnZone`'s `Full`/`Ghost` branches) or
   a future S1 SpawnSystem creature ever actually produces a ZDO that is simultaneously non-`Persistent`
   and server-owned in practice — the one precondition for `RemoveObjects`'s destroy branch and this doc's
   new grace-hold to matter at all. This is a Unity-prefab-asset question (`m_persistent`/equivalent
   serialized fields on placed/spawned prefabs), not answerable from the C# decompile this doc's ground
   truth is restricted to. `Telemetry.ZoneSim.ObjectsDestroyedOnUnfeed` (near-zero expected if the answer
   is "rare/never," meaningfully nonzero if not) is this doc's live-measurement answer, exercised by Test
   plan steps 5, 6 and 11.

---

## Red-team reconciliation

Both red teams' findings were independently re-verified this session directly against the decompiles
(`grep -n`/`sed -n` only, both `assembly_valheim_SERVER.decompiled.cs` and
`assembly_valheim.decompiled.cs`, plus `research/13-mod-compat-census.md` for the one mod-compat item).
Every blocker in both reports pointed at a real mechanism; none was a misreading. All four blockers and
both majors are **fixed in place** above; the two minors are fixed; nothing was rejected outright, and one
item (RT2 minor #5) went from "deferred, evidence not yet available" to "resolved with evidence" now that
`research/13-mod-compat-census.md` exists.

| # | Source | Severity | Where | Disposition | Evidence |
|---|---|---|---|---|---|
| 1 | RT1 #1 / RT2 #1 (same issue, independently found) | **Blocker** | Hook 2 (Z3) "Kill switch off" bullet, Hook 5 (Z9) guard bullet, Data-loss table rows "Peer disconnects mid-ring"/"Kill switch flips mid-operation", "Stand-down path" paragraph | **FIXED.** Confirmed: `RemoveObjects`'s destroy branch (`if (!zDO.Persistent && zDO.IsOwner()) ZDOMan.instance.DestroyZDO(zDO);`, SERVER:82267-82269/CLIENT:82497-82499, both builds byte-identical) is real and is a genuine network broadcast (`DestroyZDO` queues `ZRoutedRpc.InvokeRoutedRPC(0L, "DestroyZDO", ...)`, SERVER:76976-76996 — confirmed `0L` = broadcast). It runs inside `CreateDestroyObjects`, unconditionally every `1/30s` (SERVER:82303-82312), not gated by `Features.ServerZoneSim`. The prior doc's own citation of "SERVER:82242-82261" for this branch stopped 6 lines short of it — both red teams caught the same gap. Also confirmed: Z4's `UpdateTTL` never touches individual `m_instances` entries, only a zone's `m_root` (SERVER:115548-115562, no reference to `m_instances` anywhere in the method) — the prior text's "age out exactly as Z4/Z9 describe" / "decay via vanilla TTL/sweep" conflated two genuinely different mechanisms and timeframes (15s zone-root TTL vs. `1/30s` object-instance sweep), now corrected everywhere it appeared. Also confirmed, and load-bearing for the fix's design: `ReleaseNearbyZDOS` unconditionally skips every non-`Persistent` ZDO (`if (!tempNearObject.Persistent) { continue; }`, SERVER:76957-76960) — so RT2's own proposed remedy ("wait for `ReleaseNearbyZDOS`'s 2s window to resolve ownership away from the server") cannot work for the ZDOs actually at risk, since that path is structurally unreachable for them. **Fix shipped:** Hook 2 gained a bounded grace-hold (`GraceHold`, `Config.ZoneSim.ObjectUnfeedGraceTicks`, default 3 ticks) that keeps re-earmarking a fed, non-`Persistent`, server-owned ZDO for a few extra ticks after its zone drops from the feed, absorbing ordinary ring-boundary churn; a new counter `Telemetry.ZoneSim.ObjectsDestroyedOnUnfeed` makes any real occurrence visible. Also confirmed and now stated explicitly: this branch is unconditionally gated on `!zDO.Persistent` — it **can never reach a `Persistent` ZDO**, the only category Valheim's own save system writes to disk — so no sequence through this hook can violate the project's zero-data-loss rule for world/player save data; the residual (now bounded and telemetered) is ephemeral-content churn, not save-data loss. Whether today's placed/spawned content is ever actually non-Persistent+server-owned in practice is a prefab-asset question outside this doc's decompile-only ground truth, carried to Open question 8 and Test plan steps 5/6/11. |
| 2 | RT1 #2 | Major | Hook 5 (Z9), `ZoneCap.CanInclude` | **FIXED.** Confirmed from the doc's own prior code sketch: `s_globalObjectEstimate` was only ever incremented, never decremented when a zone stopped being included — a genuine, unbounded-growth design bug in new mod code (not a vanilla-behavior question). On a 24/7 server this would eventually make `CanInclude` return `false` forever with no recovery short of a restart. **Fix shipped:** `ZoneCap` now rebuilds its estimate from scratch every tick (`BeginTick()`, called from Hook 2's prefix before its zone loop), so the total is always exactly "what's included this tick," never a lifetime sum. New telemetry note added to `CapCurrentlyOverBudget`; Test plan step 7 added to regression-test the self-lock scenario directly. |
| 3 | RT1 #3 | Major | Hook 3 (Z5), `MaxGhostSpawnsPerTick` cap vs. `ZNet.GetPeers()` | **FIXED.** Confirmed: `GetPeers()` returns `m_peers` directly (SERVER:81676-81679: `public List<ZNetPeer> GetPeers() { return m_peers; }`), a stable join-ordered list, and the vanilla `CreateGhostZones` peer loop (SERVER:113811-113813) iterates it in that fixed order every tick. With a shared per-tick counter (default 1) and a fixed iteration order, the earliest-joined peer needing first-time generation always wins the tick's one slot — a real starvation risk for later-joined peers in exactly the multi-peer scenario the cap was built for. **Fix shipped:** the ghost-spawn cap's prefix now tracks a rotating start index into `GetPeers()`, advanced by one peer each tick, so who gets serviced first changes every tick instead of always being the earliest joiner. Test plan step 8 added. |
| 4 | RT1 #4 | Minor | Hook 1 (Z2), `PokeRingDriver.FixedUpdate` code sketch | **FIXED.** Confirmed: the sketch's own comment claimed the driver was "driven at `Config.ZoneSim.UpdateTickSeconds` via a manual timer," but the shown `FixedUpdate()` body had no accumulator — as written it would run at Unity's raw physics tick rate (no code-level override of `Time.fixedDeltaTime` found in either build, Open question 7), not the claimed cadence. **Fix shipped:** an explicit `m_updateTimer` accumulator gate was added to `PokeRingDriver.FixedUpdate`, mirroring Hook 3's own gate pattern, so the code now actually matches its own comment and Open question 2's cost model. |
| 5 | RT2 #1 (same issue as RT1 #1) | Blocker | Hook 2 (Z3) design, `CreateDestroyObjects`/`RemoveObjects` interaction | **FIXED** — see row 1; RT2's independent trace of the same mechanism (with the additional, correct observation that `CreateDestroyObjects` shares the same `List<ZDO>` references between `CreateObjects` and `RemoveObjects`, SERVER:82305-82312) is folded into the same fix. RT2's own proposed remedy (a) ("keep re-appending... long enough for `ReleaseNearbyZDOS`'s 2s window to resolve ownership away from the server") is evidence-checked and rejected as written: `ReleaseNearbyZDOS` structurally cannot ever resolve ownership of a non-`Persistent` ZDO away from the server (SERVER:76957-76960), so no grace period, however long, would let that happen — the shipped grace-hold instead exists purely to absorb ordinary churn, and the doc now says so explicitly rather than implying the grace period "waits out" a reassignment that can never occur. |
| 6 | RT2 #2 | Blocker | Hook 5 (Z9) prose ("consulted by Hook 1... and Hook 2") vs. Hook 1's own code sketch (no `ZoneCap` call) vs. milestone split (Z9 is B2, Z2 is B1) | **FIXED.** Confirmed: Hook 1's code sketch never called `ZoneCap.CanInclude` despite Hook 5's prose claiming it did, and `ZoneCap`/Z9 is B2-scoped while Hook 1/Z2 ships alone in B1 — meaning B1 as originally drafted had zero rate-limit on real zone construction (`Instantiate`+`Heightmap`+`MeshCollider`, confirmed real per-zone cost even in the cheap `SpawnMode.Client` path, SERVER:113946-113977/113896-113912) across every peer's ring in one tick. **Fix shipped, not deferred to B2:** Hook 5's prose is corrected to state `ZoneCap` gates Hook 2 (objects) only — it structurally shouldn't gate Hook 1, since it counts objects, not zones, and conflating the two resource types was itself part of the bug. Hook 1 gained its own, independent, B1-scoped per-tick cap (`Config.ZoneSim.MaxZoneBuildsPerTick`, default 4), so B1 never depends on B2 shipping for a cost bound. This keeps both items on their existing milestones rather than pulling Z9 forward, per RT2's first suggested option. Test plan step 9 added. |
| 7 | RT2 #3 | Blocker | Hook 3 (Z5) transpiler description, single `0.1f` literal named | **FIXED.** Confirmed directly this session: `ZoneSystem.Update` contains **two** separate `0.1f` literals — the cadence gate (`if (!(m_updateTimer > 0.1f))`, SERVER:113800/CLIENT:114611) and, separately, the `UpdateTTL(0.1f)` call-site argument (SERVER:113806/CLIENT:114617), which becomes the `dt` that `UpdateTTL` adds to every zone's `m_ttl` (`zone.Value.m_ttl += dt;`, confirmed SERVER:115553/CLIENT:116364). The prior draft's transpiler patched only the first. Left as originally drafted, running the gate at the user's 0.05s target while `UpdateTTL` still received a hardcoded `0.1f` per call would have made TTL accumulate at ~2x real-time, silently turning the user's `ZoneTTLSeconds=15` target into an effective ~7.5s — a genuine defeat of a lever the user gave an explicit numeric target for, exactly the "10 vs. 100" class of footgun this doc already guards against in Hook 6. **Fix shipped:** the transpiler now replaces both literals with the same config read, plus a `CodeMatcher` anchor test asserting exactly two `0.1f` constants are found and both handled, mirroring Hook 6's own guard. |
| 8 | RT2 #4 | Major | `ZoneSystem.UpdateTTL`, single-destroy-per-tick `break` | **FIXED.** Confirmed: the method's teardown loop `break`s immediately after destroying its first expired, instance-free zone (SERVER:115555-115561/CLIENT:116366-116372) — sized for vanilla's single reference position, now shared across potentially hundreds of real zones this module can create. A mass disconnect, a kill-switch flip, or (compounding row 7) TTL decaying too fast could previously leave dozens of expired zones undestroyed for seconds. Also confirmed why the naive fix (just delete the `break`) is unsafe: the loop enumerates `m_zones` directly and calling `Remove` mid-enumeration without immediately breaking throws `InvalidOperationException` on the next `MoveNext()`. **Fix shipped:** a `[HarmonyPrefix]` full replacement (`__runOriginal = false`) that ages every zone exactly as vanilla does, then collects all expired-and-instance-free keys into a temporary buffer first, then destroys/removes every collected entry in a second pass — draining the whole backlog per tick instead of one entry at a time, using the same collect-then-mutate pattern this doc already uses elsewhere. New gauge `ZonesTTLDestroyedThisTick`; Test plan step 9 extended to cover the backlog case explicitly. |
| 9 | RT2 #5 | Minor | "Other mods" section / Open question 5, mod-compat census not available at time of writing | **RESOLVED with evidence, not merely deferred.** `research/13-mod-compat-census.md` (409 lines) is now available and was read this session. Its scan is exhaustive over every `[HarmonyPatch]`-derived attribute in every plugin DLL on both the live server and the admin's client profile (148 distinct `Type.Method` targets found across 1170 patch records) — confirmed by direct grep of the census file that none of this doc's 8 hook targets (`ZNetScene.CreateObjects`, `ZoneSystem.Update`/`PokeLocalZone`/`CreateGhostZones`/`UpdateTTL`, `SnapToGround.SnappAll`, `TerrainComp.TryCleanInvalidTCs`) appear anywhere in it — zero third-party patches found on any of them. `TerrainComp`/`Heightmap` are additionally and explicitly listed as "Confirmed clean" in the census's own methodology section. The one adjacent finding the census does report, `ZoneSystem.(class-wide)` skip-capable prefixes from three mods, is confirmed (by the census's own cited sub-section) to target only global-key RPCs, none of which this doc touches. "Other mods" section and Open question 5 updated accordingly; the narrow residual left open (BetterContinents' *functional*, not patch-level, compatibility) is now Open question 5's precise remaining scope. |

**Overall:** every blocker raised by either red team traced back to the same underlying, previously
uncited lines in `RemoveObjects` (rows 1/5) plus three independent, real design/implementation gaps in
this doc's own new code (rows 2, 3, 6, 7, 8) — none were false positives, and none required removing an
item from its milestone. The two majors and remaining minors are likewise fixed in place. No claim in
either red team report was rejected; the closest to a partial correction is row 5/1, where RT2's specific
proposed *mechanism* (waiting out `ReleaseNearbyZDOS`) was evidence-checked and shown not to work, while
the underlying finding and its severity were fully upheld and fixed by a different mechanism (bounded
grace-hold). Both `Z8`/`G6-N2` keep their pre-existing NEEDS-LIVE-TEST verdict — neither red team disputed
that, and nothing in this reconciliation changes it. All four blockers are closed; B1 and B2 as now
specified in this document are ready to leave design review.
