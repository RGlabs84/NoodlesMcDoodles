# Ownership authority and handover (Phase B core + O9 interim)

## Summary

This module is the mod's ownership engine: it decides which ZDOs (networked objects) the
**dedicated server** simulates directly, which it hands to a specific connected player, how
that handover happens without a moment where two machines both think they're the simulator,
and how everything reverts to stock Valheim the instant the kill switch trips.

Two independent problems live in this doc, and they ship on different timelines:

- **Phase B core (G3-L1, O6, O7, O8, O3, O4, G3-N1, G4-N3, O1).** Makes the server the
  default owner of every idle, non-interactive persistent object (creatures, structures,
  traps, idle containers/carts/boats) in the zones it has chosen to simulate, instead of
  ownership defaulting to "whichever player happens to be standing there." This is the real
  fix for the Arizona problem: once the server owns a zone's objects, no single player's
  upload or RTT is in the path for anyone else's view of that zone's creatures, structures or
  physics — the server, which has a stable 300 Mbps uplink and sits in the same datacenter
  segment as `ZDOMan.SendZDOToPeers2`'s own send loop, is now the one relay everyone already
  talks to for the sync traffic itself.
- **O9 (latency-aware ownership), the interim mitigation.** For the narrow set of objects that
  must stay owned by *some player* — a helmed ship, a pulled cart, an open container, anything
  a player is actively interacting with — vanilla's own reassignment rule (`ReleaseNearbyZDOS`'s
  per-peer loop, SERVER:76948) hands ownership to whichever connected peer happens to appear
  first in internal iteration order among those in range, with no regard for latency. When the
  Arizona admin and a low-latency local player are both near the same interactive object, O9
  breaks that tie in favor of whichever player the server's own `ZRpc` ping/pong round-trip
  measures as fastest, instead of leaving it to iteration-order luck. It needs no client mod
  and can ship **before** Phase B core, as a standalone patch on the existing per-peer sweep —
  see **RED-TEAM FIX** under "Shared groundwork" below for what "standalone" actually requires.

**What ships where:**

| Milestone | Levers | What it buys |
|---|---|---|
| **Phase A (groundwork)** | O1 | Raises the simulation-distance ceiling the rest of this doc needs room to operate in; independently useful, no ownership-engine change yet. |
| **Shared groundwork (ships with O9 interim; Phase B core reuses it, does not re-implement it)** | O7 (H7), H10 (new) | **RED-TEAM FIX** (closes RT2's blocker + RT1 major #3 — see Red-team reconciliation): `ZDO.SetOwner`'s hold-guard (O7/H7) and the new per-peer near-object capture hook (H10) have no functional dependency on G3-L1/O6/zone-claiming — `ZDO.SetOwner` and `ReleaseNearbyZDOS` both run in unmodified vanilla regardless of whether the server ever claims a zone. Reclassified out of "Phase B core only" so O9-interim can actually be self-contained, as this doc's own summary always intended; Phase B core (O6/O8/O9-within-zones) then reuses this same infra rather than duplicating it. |
| **O9 interim (can ship standalone, any time after Phase A + Shared groundwork)** | O9 | Latency-aware tie-break for player-owned objects in shared areas. Works against vanilla's *existing* ownership engine; does not require the server to own anything — now literally true, not just claimed (see H9/H10 below). |
| **Phase B core** | G3-L1, G3-N1, O6, O3, O4, G4-N3, O8 | The server-owns-zones primitive itself, its mandatory prerequisites, its cadence tuning, and the hand-back path for objects it must give up. O7 moved to Shared groundwork (above); Phase B core still depends on it, just doesn't own its milestone slot. |

## Levers covered

| Lever | Verdict (10-LEVERS, LEAD-CORRECTED where noted) | User target | Final value/range recommended here | Milestone |
|---|---|---|---|---|
| G3-L1 | SAFE-WITH-GUARDS. Lead-added; this **is** the "G5-ReleaseZDOS-patch" every server-owns-zones lever is gated on. | (selected, no numeric target) | Postfix on `IsInPeerActiveArea`; binary held/not-held per zone, no range | Phase B core |
| O6 | SAFE-WITH-GUARDS ("the actual primitive") | selected | Postfix on `ReleaseZDOS` driving `ReleaseNearbyZDOS` once per server-claimed zone; allowlisted object classes only | Phase B core |
| O7 | SAFE-WITH-GUARDS | selected | Prefix on `ZDO.SetOwner`, `HashSet<ZDOID>` hold set, bound duration (recommend 10 s hard alert threshold, holds normally clear in <1 s) | **RED-TEAM FIX: Shared groundwork** (moved out of Phase B core — see milestone table; still a hard prerequisite for Phase B core, just ships earlier, with O9) |
| O8 | SAFE-WITH-GUARDS | selected | New `RPC_RequestOwnershipHandover`, current-owner-decides pattern | Phase B core |
| O3 | SAFE-WITH-GUARDS | user 0.6 s; lead suggests start 1.0 s and measure | **Start at 1.0 s**, tunable 0.5–2.0 s via config, tune down only after telemetry (P7-style counters, see Telemetry) show handover latency is the bottleneck, not bandwidth | Phase B core |
| O4 (= G4-N1) | SAFE-WITH-GUARDS | selected | 0.5 s (band 0.2–1.0 s); gated to fire only against a server that answered the mod's capability handshake, else vanilla 2.0 s | Phase B core |
| G3-N1 | SAFE-WITH-GUARDS, **mandatory prerequisite for O6** | selected | Pad = one zone width (64 m) as a starting point, re-derived if O4 changes | Phase B core |
| G4-N3 | SAFE-WITH-GUARDS (judge's literal how-to-pull is dead; corrected implementation below) | selected | Same field O1 writes (`ZNet.m_simulationDistance`); no independent numeric target — see Hooks | Phase B core |
| O1 | SAFE-WITH-GUARDS | selected | Practical band 0–4 (≤81 zones/peer); mod hard-clamps the CLI value and independently clamps `RPC_RequestValidSimulationDistance`'s client-supplied value, both to the same configured ceiling (recommend 6 as the absolute clamp even though the practical operating band is 0–4) | Phase A |
| O9 | **New — adjudicated below: SAFE-WITH-GUARDS**, with one NEEDS-LIVE-TEST sub-question (hysteresis dwell time) | new, not yet on board | RTT margin 30 ms, minimum dwell 5 s before a switch is even considered, EWMA α=0.2 on ping samples | O9 interim |
| G1-L1 | **RED-TEAM FIX: added as an explicit mandatory prerequisite of this entire doc** (was previously listed in the user's 55-lever selection but not cross-referenced here — see Red-team reconciliation, RT1 major #4) | selected (already in the user's 55-lever set) | Duplicate-connection guard on `ZNet.RPC_PeerInfo`/`ZDOMan.AddPeer` (no numeric target — see research) | Prerequisite for every uid-keyed mechanism in this doc (H1/H4/H7/H9/H10); treated the same way G3-N1 is treated for O6 |

`G2-N2` (ReleaseZDOS cadence as a *bandwidth* lever) is explicitly cross-referenced, not owned
here — see **Interactions with other subsystems**; the cadence value itself is O3's, and O3's
change is what G2-N2's NEEDS-LIVE-TEST bandwidth-measurement obligation attaches to.

**RED-TEAM FIX — G1-L1 as a hard prerequisite (RT1 major #4, verified):** every ownership
primitive in this doc keys state by `long uid` (peer.m_uid) and assumes exactly one live
connection per uid. Confirmed this pass: `ZDOMan.AddPeer` (SERVER:76814-76820) does
`m_peers.Add(zDOPeer)` with **no check** for an existing entry sharing that uid — a reconnect
racing the server's stale-connection timeout (plausible on the Arizona↔NJ path this whole
project targets) can produce two simultaneously-live entries for one uid, which would let
vanilla's own per-peer `ReleaseNearbyZDOS` loop (SERVER:76922-76928) and O9's RTT-peer
resolution (H4, first-match against `ZNet.GetPeers()`) both act on stale/duplicate state in
the same tick — a genuine double-writer risk this doc's own summary (line 7) claims to prevent.
G1-L1 (already in the user's selected 55 levers, per `tools/lever-board/levers.json` and
`HANDOFF.md`) is the fix; it just wasn't cross-referenced from this doc. No new lever is being
invented — this is purely closing a missing cross-reference.

## O9 adjudication

**Verdict: SAFE-WITH-GUARDS**, contingent on the guards below; the hysteresis dwell-time value
specifically is NEEDS-LIVE-TEST (code reasoning can bound it, not tune it) — this mirrors how
the catalog already treats T9 (an analogous adaptive-controller lever): the *mechanism* is safe
by construction, the *tuning* needs a running server.

**Why it's safe, mechanism by mechanism:**

1. **RTT source needs no client mod.** `ZRpc.UpdatePing` (SERVER:83907-83920) sends a bare
   `{0, true}` package every `m_pingInterval` (1 s, SERVER:83859) on **both** sides of every
   connection — this is core `ZRpc` keepalive, not mod code. `ZRpc.ReceivePing`
   (SERVER:83927-83936) replies `{0, false}` to any received ping, and on receiving a *reply*
   (`ReadBool()==false`) merely resets `m_timeSinceLastPing = 0f` — it does **not** compute an
   RTT today. But because a vanilla client already runs this exact reply logic unconditionally,
   the server can compute a real RTT itself, from its own side, by recording the send timestamp
   in a postfix on `UpdatePing` and diffing against `now()` in a postfix on `ReceivePing`'s
   `else` branch — entirely server-local code, observing traffic a vanilla client already
   generates. Confirmed identical mechanism on both builds (CLIENT:84089-84188, same structure,
   offset by the file's general line drift).
2. **Peer resolution needs no private API.** `ZNet.GetPeer(ZRpc rpc)` (SERVER:80550) is
   `private`, but `ZNet.GetPeers()` (SERVER:81676) is `public` and returns the same
   `List<ZNetPeer>` `GetPeer(ZRpc)` scans; a Harmony postfix on `ReceivePing` can do the same
   O(peers) linear match (`peer.m_rpc == __instance`) from outside the class, at most once per
   peer per second — negligible cost even at high peer counts.
3. **The handover write is the same authoritative write vanilla's own reassignment already
   makes.** The server is the hub every `RPC_ZDOData` packet routes through; when
   `ReleaseNearbyZDOS`'s per-peer branch calls `tempNearObject.SetOwner(uid)` today
   (SERVER:76969), that write happens on the server's own canonical `ZDO` copy and is what gets
   broadcast next. O9 does not introduce a new write path — it changes **which `uid`** that
   existing, already-verified-safe write targets, from "whichever peer is first in `m_peers`
   iteration order" to "whichever in-range peer has the lower smoothed RTT." No new hazard is
   introduced by this substitution: the surrounding `if (!tempNearObject.HasOwner() ||
   !IsInPeerActiveArea(...)) && InActiveArea(...)` guard is untouched, and per research/03 §7,
   a hand-over is only durable when driven by the machine whose write reaches the widest
   audience fastest — which here is unchanged (still the server's own sweep).
4. **Exclusions reuse O6's already-vetted allowlist, not a new one.** O9 only ever considers
   objects in O6's server-safe class list (WearNTear, Trap, idle Container/ItemDrop/Vagon/
   Catapult/Fish/Sadle, BaseAI/MonsterAI) **and only when currently player-owned, not
   server-held** — it is deliberately narrower than O6, not an alternative to it. Concretely
   excluded, all confirmed by direct code read this pass:
   - **Container** — `Container.IsInUse()` / `ZDOVars.s_inUse` (SERVER:78720, written at
     SERVER:121873, read at SERVER:121877): any container with `s_inUse==1` is skipped.
   - **Ship** — `Ship.UpdateOwner`'s own guard (SERVER:140860, confirmed this pass:
     `m_nview.IsOwner() && Player.m_localPlayer != null && m_players.Count > 0 &&
     !IsPlayerInBoat(...)`) never fires on a dedicated server (`Player.m_localPlayer` is
     permanently null there — research/03 Addition A1), so O9 uses its own check instead:
     skip any `Ship` whose `m_players.Count > 0` (a helm/crew is aboard).
   - **Vagon (cart)** — skip while `m_wagon.InUse()` / attached to a puller (research/03 §8
     classification, `RPC_RequestOwn` attach pattern, SERVER:147455-147461).
   - **Sadle** — skip while ridden (`RPC_RequestControl`, SERVER:18225-18242).
   - **TerrainComp** — excluded entirely from O9's candidate set (never a target); it has no
     request-handshake to key an "in use" check off, and G6-N2 already owns the handover-
     atomicity problem for this class — O9 must not touch it.
   - **Beds / TeleportWorld (portals)** — neither carries per-open network ownership state
     that O9's reassignment would touch: a bed's `RPC_SetOwner` (SERVER:~119520) is a
     *gameplay* claim field (`ZDOVars`-backed "whose bed is this"), not `ZDO` network
     ownership, and a portal's only `SetOwner` call is the "claim to write `SetConnection`"
     pattern on an *unowned target* portal (research/03 §8, `SetConnectedPortal`,
     SERVER:143503-143512) — both are ordinary static placed pieces from a network-ownership
     standpoint and fall under the generic "safe, idle, server/O9-eligible" class, not a
     special exclusion. The exclusion that matters for both is the generic one: never reassign
     while a player is mid-interaction, covered by guard 5 below.
   - **Anything a player is actively interacting with, generically.** Guard: never reassign a
     ZDO within a fixed grace window (recommend 5 s, same order as O6 guard 7's window) of any
     `RPC_Request*`-family call touching it — reuses the same in-memory timestamp map O6 keeps
     for its own grace-window guard rather than building a second one.
5. **Hysteresis prevents flapping.** Track a smoothed RTT per connected peer
   (EWMA, α=0.2, discard any single sample >5× the current median as an outlier — a GC pause or
   a mispredicted late ACK, not a real latency shift). Only consider a switch when a candidate
   peer's smoothed RTT is lower than the current owner's by more than a fixed margin (recommend
   30 ms — comfortably above ordinary jitter, well below the ~150–300 ms gap the Arizona-vs-NJ
   case actually shows), **and** require that margin to hold for a minimum dwell time before
   acting (recommend 5 s — long enough that a single lucky ping doesn't trigger a switch, short
   enough to matter compared to O3's 1 s sweep cadence). **This dwell time is the one
   NEEDS-LIVE-TEST parameter** — the right value depends on real jitter distributions on the
   Arizona↔New Jersey path the mod exists to fix, which code reading cannot supply.
6. **Handover mechanics reuse O7, exactly as required — but from O9's *own* `ReleaseZDOS`
   postfix, not O6's.** **RED-TEAM FIX (RT2 blocker, verified — see Red-team reconciliation):**
   the original text here said O9's `SetOwner` call runs "from inside the same `ReleaseZDOS`
   postfix context O6/G3-L1 already run in," which made O9 structurally dependent on Phase B
   core shipping first, contradicting this doc's own "ships before Phase B core" claim (and
   H9's "no new hook needed" claim, RT1 major #3 — also fixed, see H9/H10 below). O9 registers
   its **own** Harmony postfix directly on `ZDOMan.ReleaseZDOS(float dt)` (SERVER:76919), a
   separate patch class from O6's `ReleaseZDOS_Cadence`, gated solely on `Features.O9.Active`
   (independent of `Features.OwnershipAuthority.Active`, exactly as the Configuration table
   already specifies `O9.Active` to be). Harmony allows multiple independent postfixes on one
   target method — O9's runs whether or not O6's is also loaded. Before calling `SetOwner` on
   the winning peer, O9 takes an O7 hold on the ZDOID (blocking vanilla's own sweep or a stray
   competing write for the few milliseconds the transition takes — O7/H7 is Shared groundwork,
   not Phase-B-gated, see milestone table), performs the `SetOwner` call synchronously inside
   its own postfix, then releases the hold immediately. No window exists where the hold is up
   but the new owner hasn't been written, because both happen inside the same synchronous call.
   O9's postfix runs after `ReleaseZDOS`'s body (all of vanilla's self-pass and per-peer passes)
   has already executed for this tick, so it sees a complete, consistent set of per-peer
   near-object data from H10 (below) before deciding.

**What would move this to UNSAFE:** reassigning an object O9 has no exclusion rule for while a
player is mid-`RPC_ZDOData` write to it (closed by guard 4's allowlist-plus-grace-window, which
is a strict subset of O6's already-reconciled guard set — nothing new to re-litigate). What
would move it to NEEDS-LIVE-TEST *in full* (not just the dwell-time sub-question) would be
discovering in testing that RTT sampled over `ZRpc`'s 1 s ping cadence is too coarse to
distinguish two candidate peers reliably — the mitigation (EWMA + margin + dwell) is a standard
control-loop answer to that, not a new invention, so this doc keeps the SAFE-WITH-GUARDS
verdict rather than downgrading pre-emptively.

## Ownership state machine

Four states per persistent ZDO, all pre-existing vanilla states except "server-held":

```
                    ┌─────────────────────────────────────────────┐
                    │                                               │
    ┌───────────┐   │ O6 claim (G3-L1 lets it stick)   ┌──────────┐│
    │ UNOWNED   │──────────────────────────────────────>│  SERVER  ││
    │(uid==0)   │<──────────────────────────────────────│  HELD    ││
    └───────────┘   vanilla release (owner leaves        └────┬─────┘
         │           active area, ReleaseNearbyZDOS            │
         │           SetOwner(0), SERVER:76958)                │ O8 hand-back RPC
         │                                                      │ (server decides,
         │ vanilla per-peer claim                               │ then SetOwner)
         │ (ReleaseNearbyZDOS SetOwner(uid),                    ▼
         │  SERVER:76969, or O9's                        ┌──────────────┐
         │  RTT-picked target)                            │ PLAYER-HELD  │
         └────────────────────────────────────────────────>│ (interactive)│
                                                            └──────┬───────┘
                                                                   │
                          O9 reassigns among candidate players     │
                          (only when NOT server-held; hold via O7) │
                          ◄──────────────────────────────────────┘
```

- **UNOWNED** — vanilla's default for a persistent ZDO nobody has claimed. Reachable from
  SERVER-HELD only via an explicit release (kill switch flush, see below); reachable from
  PLAYER-HELD via vanilla's own release branch when the owner leaves the object's active area
  with nobody else in range.
- **SERVER-HELD** — this doc's core addition. Only objects in O6's allowlisted classes, in a
  zone `m_zones.ContainsKey`-loaded and not currently overlapped (with G3-N1's staleness
  padding) by any connected peer's active area, and outside O6's grace window for a just-granted
  container/interactive object. Held via G3-L1 making `IsInPeerActiveArea` report true for the
  server's own held zones, so vanilla's own per-peer sweep no longer sees the object as fair
  game. Protected against races (not against the normal sweep, which G3-L1 already neutralizes)
  by O7 during the claim write itself.
- **PLAYER-HELD (interactive)** — a specific connected player's client is the simulator: a
  helmed ship, a pulled cart, an open container, a ridden saddle, or simply a player standing
  near an object the server hasn't (yet, or ever, for excluded classes) claimed. This is where
  O9 operates: among player-held objects visible to multiple connected peers, it steers *which*
  player holds it, never touching SERVER-HELD objects and never taking an object O6 already
  excludes from server claims (the classes are the same list, by design).
- **Transient/non-persistent ZDOs** (a player's own character, dropped items mid-flight,
  projectiles) never enter this state machine at all — `ReleaseNearbyZDOS`'s very first
  per-object check is `if (!tempNearObject.Persistent) continue;` (SERVER:76951), so nothing in
  this doc's patches can reach them, by construction.

### How handover works without a two-writer window

Every transfer in this doc follows the same shape vanilla's own request-then-grant RPCs already
use (`Container.RPC_RequestOpen`, SERVER:122011-122031: current owner decides, *then* calls
`SetOwner`, never the reverse):

1. The **current simulator** (server for SERVER-HELD→PLAYER-HELD via O8; server, acting as the
   authoritative relay, for UNOWNED/PLAYER-HELD→SERVER-HELD via O6/G3-L1/O9) is the one that
   calls `SetOwner`, never a third party claiming from outside.
2. Immediately before that call, O7's hold-guard hash-set gains the ZDOID (server-side only;
   the hold only needs to exist where the authoritative `SetOwner` write happens).
3. The `SetOwner` call executes synchronously in the same method — there is no `await`, no
   cross-frame gap, between taking the hold and writing the new owner.
4. The hold is released immediately after, in the same call stack. Total held duration is
   sub-millisecond in the normal case; O7's own "bound total hold duration, alert on any hold
   outliving a sane timeout" guard exists for the abnormal case (an exception mid-transfer),
   not the normal path.
5. Because the server is the relay every peer's `RPC_ZDOData` traffic already passes through,
   the moment this write lands in the server's own canonical `ZDO` copy it *is* the new
   authoritative state — there is no second machine whose independent copy could race it back,
   as long as O7's hold blocks the one thing that could (vanilla's own concurrent sweep, or a
   stray in-flight `RPC_RequestOwn`-family call from the object's previous simulator).

The one handover this doc adds that does **not** follow the "current owner decides" shape by
default is O8's crew hand-back: a *boarding* client sends the request, but per O8's own mandatory
guard, the **server** (current owner) is still the one that validates and calls `SetOwner` — the
requester never self-claims. This mirrors `RPC_RequestOpen` exactly, just with the roles
(current owner = server, requester = boarding player) matching this doc's Phase B world instead
of vanilla's peer-to-peer default.

### Kill switch: ownership returns to vanilla

Per the shared infrastructure contract, `NoodlesMcDoodles.Core.Features.OwnershipAuthority.Active`
gates every hook in this doc. Disabling it must do all of the following, not merely stop new
activity (P4's guard 7, G6-N3's guard 4 — both explicitly call this out as the failure mode a
naive kill switch produces):

1. **G3-L1's postfix on `IsInPeerActiveArea`** starts returning the vanilla answer again
   immediately (flag read, no state to unwind) — the server's held zones stop being "present"
   from the sweep's point of view.
2. **O7's hold set is flushed**, not merely stopped-from-growing: every currently-held ZDOID is
   released. Without this, an object held at the instant of disable stays administratively
   un-ownable forever (a stall, not data loss, but a real one).
3. **Nothing explicitly un-claims already-transferred SERVER-HELD objects.** This is
   intentional and matches P4's own documented limitation ("disabling the ownership-sweep patch
   stops further claims but does not instantly undo already-transferred ownership") — the very
   next unmodified `ReleaseZDOS` sweep (now running with G3-L1 off) reassigns every SERVER-HELD
   object to the nearest in-range peer within one O3-interval, by vanilla's own unmodified
   logic, exactly as it would if the server had never claimed it. No mod code is needed to
   "give objects back"; removing the one patch that let the server keep them is sufficient,
   because the object was never anything but a normal persistent ZDO with an owner field vanilla
   already knows how to reassign.
4. **O9's RTT tracking and dwell-time state is dropped** — harmless, since it is entirely
   derived, in-memory, non-authoritative data (smoothed RTT estimates), never anything written
   to a ZDO.

## Hooks

For every hook: `Type.Method(signature)`, confirmed line ranges in **both** assemblies (or an
explicit note where they differ), patch kind, side, why this hook, a code sketch against the
shared infrastructure contract, and off-flag behavior.

---

### H1 — `ZDOMan.IsInPeerActiveArea(Vector3 point, long uid)` — G3-L1

- **CLIENT:** method does not run meaningfully on the client build (`ReleaseZDOS`, its only
  caller family, is gated `if (ZNet.instance.IsServer())`, SERVER:76862-76866 / same source
  compiled into both builds per research/03's confirmed "same source, `IsServer()`-gated"
  finding) — patch is registered on both builds for build-uniformity but is inert on a
  connecting client (`IsServer()` is false unless that client is itself hosting a listen
  server, in which case the same server-side behavior applies to it too, correctly).
- **SERVER:76934-76946** (private method, confirmed this pass — see excerpt below).
- **Patch kind:** Harmony **postfix**.
- **Side:** server (or listen-host acting as server).
- **Why this hook, not another:** this is the single predicate vanilla's own per-peer
  reassignment loop uses to decide "is the current owner still defending this object" —
  patching it is the minimal change that makes a server-side claim durable, versus
  reimplementing `ReleaseNearbyZDOS` wholesale (which the 10-LEVERS reconciliation explicitly
  rejected: "No reimplementation of the self-pass or `FindSectorObjects` is needed").

Actual code (confirmed, re-read this pass):

```csharp
// SERVER:76934
private bool IsInPeerActiveArea(Vector3 point, long uid)
{
    if (uid == m_sessionID)
    {
        return ZNetScene.InActiveArea(point, ZNet.instance.GetReferencePosition());
    }
    ZNetPeer peer = ZNet.instance.GetPeer(uid);
    if (peer == null) { return false; }
    return ZNetScene.InActiveArea(point, peer.GetRefPos());
}
```

Sketch:

```csharp
[HarmonyPatch(typeof(ZDOMan), "IsInPeerActiveArea")]
static class IsInPeerActiveArea_ServerHold
{
    static void Postfix(Vector3 point, long uid, ref bool __result)
    {
        if (!Features.OwnershipAuthority.Active) return;         // off -> untouched vanilla result
        if (__result) return;                                    // vanilla already says "yes"
        if (uid != ZDOMan.GetSessionID()) return;                 // only the server's own claim
        if (OwnershipHeldZones.Contains(ZoneSystem.GetZone(point)))
            __result = true;                                      // server "present" in its held zone
    }
}
```

`OwnershipHeldZones` is the mod's own held-zone set, maintained by O6's claim pass (H2, not H4
— fixed mis-citation).

**RED-TEAM FIX (RT1 major #1, verified):** confirmed this pass, `ZoneSystem.UpdateTTL`
(SERVER:115548-115561) actively evicts an `m_zones` entry — `m_zones.Remove(zone2.Key)` at
SERVER:115559 — once `zone2.Value.m_ttl > m_zoneTTL` (default `4f`, SERVER:113087) **and**
`!ZNetScene.instance.HaveInstanceInSector(zone2.Key)` (SERVER:82368), checked on a 0.1 s cadence
(`UpdateTTL(0.1f)` called every `Update()` tick past its own timer, SERVER:113806). As
originally written, this doc never states whether `OwnershipHeldZones` is additive (grown over
time, never shrunk except by the kill switch) or re-derived. If additive, a zone whose vanilla
`m_zones` entry gets TTL-evicted (e.g. this doc's own claim pass stops refreshing it, or on a
restart that repopulates `m_zones` fresh) would still report `true` from G3-L1's postfix forever
— which is the *wrong* direction: it means vanilla's own per-peer sweep would keep believing the
server defends that position even after the server stopped touching it, permanently blocking any
approaching peer's own `ReleaseNearbyZDOS` pass from ever claiming those objects (the guard
`!IsInPeerActiveArea(...)` would never fire). **Fix:** `OwnershipHeldZones` is **fully
re-derived, not additively grown**, at the top of every H2 postfix invocation:
`OwnershipHeldZones = new HashSet<Vector2s>(OwnershipZonePolicy.CurrentClaimTargets())`, replacing
the previous tick's set wholesale, *before* the claim-pass loop runs. Since
`CurrentClaimTargets()` is itself `m_zones.ContainsKey`-gated live at call time (H3 guard (a)),
a zone that vanilla evicts from `m_zones` simply stops appearing in `CurrentClaimTargets()` on
the very next `ReleaseZDOS` tick (≤1 O3-interval later) and is therefore dropped from
`OwnershipHeldZones` in the same tick, with no separate eviction hook needed. Diff the previous
and new sets each tick and log/count every zone that drops out due to `m_zones` no longer
containing it (as opposed to dropping out because a peer moved into range, which is the normal
case) as `ownership.staleHeldZoneEvictions` (see Telemetry) — this was RT1's suggested option (a)
(re-validate every tick), not option (b) (a new hook on `UpdateTTL`/`m_zones.Remove`); option (a)
is preferred here because it reuses a check (H3 guard (a)) this doc already performs every tick
for a different purpose, rather than adding a new patch target for the same information. Note
`m_zones` itself is `private readonly` (SERVER:113154), so `CurrentClaimTargets()`'s
`ContainsKey` check needs a cached `AccessTools.FieldRefAccess<ZoneSystem,
Dictionary<Vector2s, ZoneData>>` — the same reflection pattern H3 already uses for
`ReleaseNearbyZDOS` itself, not a new technique.

- **Off-flag behavior:** postfix returns immediately without touching `__result` — exact
  vanilla behavior (server always reports "absent" outside its pinned sentinel position).

---

### H2 — `ZDOMan.ReleaseZDOS(float dt)` — O6 (claim pass) and O3 (cadence)

- **SERVER:76919-76930**, confirmed this pass (excerpt below). Private method; same source on
  CLIENT but inert there (`Update`'s `IsServer()` gate, SERVER:76858-76867).
- **Patch kind:** Harmony **postfix** for O6 (new claim pass); **transpiler** for O3 (swap the
  `2f` cadence literal for a mutable field read — per the catalog's own guard: "prefer a
  transpiler patch on the IL constant over a full skip-and-reimplement prefix," since a
  bool-return prefix would AND against any other mod's own prefix on this generically-named
  method).
- **Side:** server only.
- **Why this hook:** it's the existing entry point for the *entire* generic ownership sweep —
  extending it with one more pass (per server-claimed zone) reuses every piece of vanilla's
  already-correct persistent/active-area logic, exactly as O6's `how` field specifies.

```csharp
// SERVER:76919
private void ReleaseZDOS(float dt)
{
    m_releaseZDOTimer += dt;
    if (!(m_releaseZDOTimer > 2f)) { return; }
    m_releaseZDOTimer = 0f;
    ReleaseNearbyZDOS(ZNet.instance.GetReferencePosition(), m_sessionID);
    foreach (ZDOPeer peer in m_peers)
    {
        ReleaseNearbyZDOS(peer.m_peer.m_refPos, peer.m_peer.m_uid);
    }
}
```

Sketch (O3's cadence field + O6's claim postfix; `ReleaseNearbyZDOS` itself is `private` but
reachable via `AccessTools.Method` + a cached delegate, no reimplementation):

```csharp
// O3: transpiler swaps the literal 2f for Config.ReleaseZdosInterval.Value (default 1.0f)
[HarmonyPatch(typeof(ZDOMan), "ReleaseZDOS")]
static class ReleaseZDOS_Cadence
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instrs)
    {
        var matcher = new CodeMatcher(instrs)
            .MatchStartForward(new CodeMatch(OpCodes.Ldc_R4, 2f));
        if (!matcher.IsValid) { Log.Warn("ReleaseZDOS 2f literal not found; leaving unpatched"); return instrs; }
        return matcher
            .SetOperandAndAdvance(0f) // placeholder; real IL loads Config.ReleaseZdosInterval.Value via a static field
            .InstructionEnumeration();
    }

    // O6: runs after vanilla's own two passes, once per server-claimed zone
    // RED-TEAM FIX (RT1 major #1 + RT2 major "O6 unbounded cost", both verified — see
    // Red-team reconciliation): re-derive OwnershipHeldZones fresh every tick (closes the
    // TTL-staleness gap) AND cap how many zones get a full ReleaseNearbyZDOS sweep per tick
    // (closes the unbounded-cost gap), round-robining any remainder across subsequent ticks.
    static void Postfix(ZDOMan __instance)
    {
        if (!Features.OwnershipAuthority.Active) return;
        var targets = OwnershipZonePolicy.CurrentClaimTargets(); // m_zones.ContainsKey-gated live, excludes overlapped/grace-window zones
        OwnershipHeldZones.ReplaceWith(targets); // full re-derivation, not additive growth; diffs old vs. new and counts ownership.staleHeldZoneEvictions
        int processed = 0;
        foreach (var zoneCenter in OwnershipZonePolicy.NextClaimBatch(targets, Config.MaxZonesClaimedPerCycle.Value)) // caps + round-robins the remainder
        {
            ReleaseNearbyZDOSDelegate(__instance, zoneCenter, ZDOMan.GetSessionID());
            processed++;
        }
        Telemetry.RecordZonesProcessed(processed, targets.Count); // ownership.zonesProcessedPerCycle
    }
}
```

- **Off-flag behavior:** O3's transpiler reads a static field that holds the vanilla `2f`
  literal when the feature is off (per the shared infra contract — "transpilers read their
  value from a static field that holds the vanilla literal when disabled"), so cadence reverts
  exactly to 2.0 s. O6's postfix early-returns, so no extra claim pass runs, and
  `OwnershipHeldZones` is left untouched by O6 (but see H1's RED-TEAM FIX — G3-L1's own postfix
  also early-returns when off, so a stale `OwnershipHeldZones` has no effect either way while
  the feature is disabled).
- **RED-TEAM FIX (RT2 major, "O6 claim-pass cost has no cap," verified):** each
  `ReleaseNearbyZDOS` call re-runs a full `FindSectorObjects` sweep over the near-simulation-
  distance radius (confirmed SERVER:76948-76958) — unlike vanilla's own sweep, whose cost is
  bounded by (1 sentinel pass + connected-peer count), an uncapped claim pass's cost scales with
  *held-zone count*, which is unbounded here as originally written and multiplies with O1's
  raised simulation-distance ceiling and O3's shortened cadence. **Fix:**
  `Config.MaxZonesClaimedPerCycle` (default recommended: 8; see Configuration table) caps how
  many zones get a real sweep per `ReleaseZDOS` tick; `OwnershipZonePolicy.NextClaimBatch`
  round-robins the remainder across subsequent ticks (same shape as the Z-group's own
  object-count-cap pattern) so a large held-zone count degrades to slower re-affirmation, not
  unbounded per-tick cost. `ownership.zonesProcessedPerCycle` (count) and its wall-clock cost
  (new telemetry) make the O3/O6 cadence coupling this doc already flags (Data-loss analysis
  table, last-but-two row) measurable instead of assumed safe.

---

### H3 — `ZDOMan.ReleaseNearbyZDOS(Vector3, long)` (reused, not re-patched) and object-class
    allowlist gate for O6's claim targets

- **SERVER:76948-76971**, confirmed this pass (excerpt already shown under Ownership state
  machine's "vanilla per-peer claim" evidence). No independent patch here — H2's postfix calls
  this **existing private method** directly (via a cached `MethodInfo`/delegate obtained at
  startup, `AccessTools.Method(typeof(ZDOMan), "ReleaseNearbyZDOS")`), reusing its exact
  persistent-only, active-area-tested, `SetOwner(0)`/`SetOwner(uid)` logic without
  reimplementing any of it — precisely the 10-LEVERS `how` field's design.
- The allowlist gate (which zones/object classes the server is even allowed to claim) is new
  mod code, not a patch: before adding a zone center to `OwnershipZonePolicy.CurrentClaimTargets()`,
  check (a) `m_zones.ContainsKey(zone)` (never `IsZoneLoaded`, per O6 guard 2 — avoids the
  terrain-before-objects fall-through hazard), (b) no connected peer's (G3-N1-padded) active
  area overlaps this zone (O6 guard 3/4 — this is the check that races staleness, which is why
  G3-N1 is a hard prerequisite, not a nice-to-have), and (c) no object in the zone is within
  O6's grace window of a just-granted `RPC_Request*` handshake (O6 guard 7).

---

### H4 — `ZNetPeer` constructor and `ZRpc.UpdatePing` / `ZRpc.ReceivePing` — O9's RTT source

- **`ZNetPeer(ISocket socket, bool server)`** — SERVER:78853-78857 / CLIENT:78995-78999,
  confirmed identical source this pass. Patch kind: none needed — O9 does not patch this
  constructor; peer lookup happens lazily via the public `ZNet.GetPeers()` scan (see below),
  not via a table built at construction time, to avoid any lifecycle-management burden.
- **`ZRpc.UpdatePing(float dt)`** — SERVER:83907-83920 / CLIENT (same structure, offset
  consistent with the file's drift; `m_pingInterval = 1f` confirmed at both SERVER:83859 and
  CLIENT:84089). Private method.
- **`ZRpc.ReceivePing(ZPackage package)`** — SERVER:83927-83936 / CLIENT (same structure).
  Private method.
- **Patch kind:** Harmony **postfix** on both (private methods patch fine via
  `AccessTools.Method`; no access-level obstacle).
- **Side:** server only (the mod only needs the *server's* view of RTT to each peer; running
  the same patch on a client build would measure the client's RTT to the server, which is not
  what O9 needs and is left un-wired even though the patch is harmless there).
- **Why this hook:** it is the only place a round-trip timestamp pair exists anywhere in the
  stock protocol, and it requires zero client-side change — the reply half of the exchange is
  core `ZRpc` behavior every connected peer (vanilla or modded) already performs.

```csharp
// SERVER:83907 (UpdatePing) / SERVER:83927 (ReceivePing), confirmed this pass
private void UpdatePing(float dt)
{
    m_pingTimer += dt;
    if (m_pingTimer > m_pingInterval)
    {
        m_pingTimer = 0f;
        m_pkg.Clear(); m_pkg.Write(0); m_pkg.Write(true);
        SendPackage(m_pkg);
    }
    m_timeSinceLastPing += dt;
    if (m_timeSinceLastPing > m_timeout) { ZLog.LogWarning("ZRpc timeout detected"); m_socket.Close(); }
}

private void ReceivePing(ZPackage package)
{
    if (package.ReadBool())
    {
        m_pkg.Clear(); m_pkg.Write(0); m_pkg.Write(false);
        SendPackage(m_pkg);
    }
    else { m_timeSinceLastPing = 0f; }
}
```

Sketch:

```csharp
[HarmonyPatch(typeof(ZRpc), "UpdatePing")]
static class ZRpc_UpdatePing_RttSend
{
    static void Prefix(ZRpc __instance, float dt)
    {
        if (!Features.OwnershipAuthority.Active) return;
        // mirror vanilla's own "about to send" condition without touching private fields:
        // record "a ping is about to go out" via a Postfix instead (below), simpler and
        // avoids re-deriving m_pingTimer's private state.
    }
    static void Postfix(ZRpc __instance)
    {
        if (!Features.OwnershipAuthority.Active) return;
        RttTracker.NoteOutgoingPingTick(__instance); // records now() keyed by ZRpc instance,
                                                       // idempotent no-op if no ping actually sent this tick
    }
}

[HarmonyPatch(typeof(ZRpc), "ReceivePing")]
static class ZRpc_ReceivePing_RttSample
{
    // RED-TEAM FIX (RT2 minor, verified): the original condition was
    // `package.GetPos() == 0 && !PeekBool(package)`. Confirmed this pass,
    // ZRpc.HandlePackage (SERVER:83952-83958) always calls package.ReadInt() (consuming
    // 4 bytes, SERVER:83339-83341 ReadInt / SERVER:83476-83478 GetPos) BEFORE dispatching to
    // ReceivePing when that int == 0 — so by the time this Prefix runs, GetPos() is always 4,
    // never 0. The `GetPos() == 0` branch could never fire; RTT sampling would silently never
    // record a sample. Fix: drop the dead position check entirely — HandlePackage's call site
    // already guarantees this is the id-0 (ping/pong) handler, so the only discriminator
    // ReceivePing's own Prefix needs is the bool itself, peeked at whatever position
    // HandlePackage already correctly left the stream at.
    static void Prefix(ZRpc __instance, ZPackage package)
    {
        if (!Features.OwnershipAuthority.Active) return;
        if (!PeekBool(package)) // false = pong (a reply); true = ping (no RTT sample here)
        {
            var peer = ZNet.instance.GetPeers().Find(p => p.m_rpc == __instance);
            if (peer != null) RttTracker.RecordSample(peer.m_uid, RttTracker.ElapsedSinceLastTick(__instance));
        }
    }
}
```

(`PeekBool` reads without consuming, since the real `ReadBool()` call must still happen exactly
once inside vanilla's own method body; the sketch's `RttTracker.NoteOutgoingPingTick` approach
sidesteps needing to peek at all by instead timestamping every `UpdatePing` tick and having
`RttTracker.RecordSample` compute the delta from the *previous* tick's timestamp when a pong
postfix fires — either formulation is implementable with public-only access; the exact choice is
an implementation detail, not a design one.)

- **Off-flag behavior:** both postfixes early-return; zero overhead, zero behavior change (the
  reply itself is vanilla code, untouched either way).

---

### H5 — `ZNet.RPC_RequestValidSimulationDistance` and direct `m_simulationDistance` write — O1 + G4-N3

- **SERVER:81003-81012**, confirmed this pass (excerpt below). CLIENT never runs the server
  branch (`if (IsServer())` gate) but the method exists identically in both builds.
- **Patch kind:** Harmony **postfix**, clamping the deserialized client value before it's
  stored.
- **G4-N3's corrected mechanism (confirmed this pass, not the judge's original literal
  proposal):** a bare postfix on `GetDesiredSimulationDistance()` (SERVER:81045-81047, `return
  m_simulationDistance;`) is self-canceling, because `GetSyncedSimulationDistance()`
  (SERVER:81035-81043) takes `min(m_simulationDistance, GetDesiredSimulationDistance())` — and
  on the server build both sides of that `min` **are the same field**, so a postfix that
  inflates only the getter's return value is discarded by the `min` against the untouched raw
  field. O1 and G4-N3 are, in the actual code, **the same write target**: `ZNet.m_simulationDistance`.
  The correct implementation is a direct call to the existing **public** `ApplySimulationDistance`
  (SERVER:81023-81030), not a private-field reflection write, since that method already does
  the field write plus the `ZoneSystem`/`Water`/`Heightmap` `ApplySettings()` calls vanilla
  itself makes whenever this value changes.
- **Side:** server only.
- **Why this hook:** `RPC_RequestValidSimulationDistance` is the only place an *unvalidated*
  client-supplied value reaches this field; independently clamping it here (tighter than or
  equal to O1's own raised ceiling) closes the "hostile/buggy peer requests near=40" hazard the
  catalog flags, without which raising the ceiling for legitimate use is itself the risk.

```csharp
// SERVER:81003
private void RPC_RequestValidSimulationDistance(ZRpc rpc, ZPackage pkg)
{
    if (IsServer())
    {
        SimulationDistance simulationDistance = SimulationDistance.Deserialize(ref pkg);
        SimulationDistance simulationDistance2 = (simulationDistance < m_simulationDistance) ? simulationDistance : m_simulationDistance;
        GetPeer(rpc).m_simulationDistance = simulationDistance2;
        ZPackage pkg2 = new ZPackage();
        simulationDistance2.Serialize(ref pkg2);
        rpc.Invoke("RPC_ValidatedSimulationDistance", pkg2);
    }
}
```

Sketch:

```csharp
[HarmonyPatch(typeof(ZNet), "RPC_RequestValidSimulationDistance")]
static class RPC_RequestValidSimulationDistance_Clamp
{
    // Prefix, not postfix: must clamp the DESERIALIZED value before vanilla's own min() runs,
    // since vanilla's min() only protects against the raised ceiling, not a value already above it.
    // RED-TEAM FIX (RT1 minor, verified): the original sketch left a dangling "see note"
    // pointing nowhere and proposed an in-place byte rewrite without establishing the wire
    // format is fixed-width. Confirmed this pass: SimulationDistance.Serialize/Deserialize
    // (SERVER:117807-117821) is a fixed int/int/bool layout (two 4-byte ReadInt/Write(int)
    // fields plus one bool), so an in-place rewrite would in fact be structurally safe today —
    // but rather than depend on that staying true, clamp via a full re-serialize-into-a-new-
    // ZPackage-and-swap, which makes no assumption about field widths at all:
    static void Prefix(ref ZPackage pkg)
    {
        if (!Features.OwnershipAuthority.Active) return;
        int savedPos = pkg.GetPos();
        var raw = SimulationDistance.Deserialize(ref pkg);
        var clamped = ClampToCeiling(raw, Config.SimulationDistanceCeiling.Value);
        var capped = new ZPackage();
        clamped.Serialize(ref capped);
        pkg = capped; // vanilla's own Deserialize call (inside the original method body) reads
                       // this replacement package from position 0, unaffected by our own read above
    }
}

// Applied once at server startup (Phase A) and re-applied after any live config change,
// never as a bare getter postfix:
void ApplyConfiguredSimulationDistance()
{
    if (!Features.OwnershipAuthority.Active) return;
    var target = new SimulationDistance(Config.SimulationDistanceCeiling.Value, 0, isClassic: false);
    ZNet.instance.ApplySimulationDistance(target); // public method — no reflection needed
}
```

- **Off-flag behavior:** the prefix is skipped (vanilla's own unclamped `min` logic runs
  unmodified — the pre-existing "warn-only" vanilla ceiling behavior, not a regression from
  this mod's absence), and `ApplyConfiguredSimulationDistance` is never called, so
  `m_simulationDistance` stays at whatever the server's own `-simulationdistance` CLI flag (or
  its vanilla default of level 2) set.

---

### H6 — `ZNet.SendPeriodicData` client refPos cadence — O4 (= G4-N1)

- **SERVER:80382-80406**, confirmed present (same source both builds; client-only branch is
  the one this lever changes). Cited extensively and re-verified across research/03 and 10-LEVERS;
  not independently re-opened line-by-line this pass beyond confirming the method exists at the
  cited range (grep-confirmed).
- **Patch kind:** Harmony **transpiler**, IL constant swap on the shared `2f` literal.
- **Side:** client only (the server build never executes the reporting branch of this shared
  method) — **RED-TEAM FIX (RT2 minor, verified):** confirmed this pass, the `m_periodicSendTimer
  >= 2f` gate itself (SERVER:80383-80388) is evaluated **before** the `IsServer()` split
  (SERVER:80389), i.e. it's shared by the server's own `SendNetTime`/`SendPlayerList`/
  `SendHistoricalPlayerList` broadcast branch *and* the client's `SendServerSyncPlayerData`
  reporting branch — the literal is not confined to "the reporting branch" as the surrounding
  prose implies, only the *effect this doc wants* is. Since the shared infra contract allows
  this DLL to load on the server too, the transpiler's backing config field must be read as
  `!ZNet.instance.IsServer() ? Config.ClientRefPosInterval.Value : 2f` (or gated at the point of
  application) — never a bare process-global static — so the server's own broadcast cadence can
  never be inadvertently altered by a change intended only for the client's refPos report.
- **Why this hook:** the client's own reference-position report is the input `G3-N1`'s padding
  and `O6`'s peer-overlap exclusion test are sized against — tightening it directly shrinks the
  staleness window those guards must otherwise absorb.
- **Guard (mandatory, per catalog):** the tightened cadence must be gated on a successful
  capability handshake with a server this mod recognizes (shared infra contract's negotiated
  RPC), falling back to vanilla's 2.0 s against any server that doesn't answer — otherwise a
  player who keeps this client mod installed imposes extra load on every unrelated server they
  later join, vanilla or foreign-modded.
- **Scope note (RT2 minor, see Open question 7):** this doc deliberately implements only the
  cadence transpiler (guard 1 of `research/10-LEVERS.md`'s O4 entry). It does **not** implement
  that entry's guards (2)/(3) — a server-side plausibility check on `RPC_ServerSyncedPlayerData`
  and its mandatory teleport-exemption reset — in this pass. Recorded explicitly rather than
  left to be silently rediscovered: adding guard (2) later without also porting guard (3) would
  reproduce the "portal freezes the refPos anchor forever" hazard `10-LEVERS.md` already named.
- **Off-flag behavior:** transpiler's static field holds `2.0f`; cadence is bit-for-bit vanilla.

---

### H7 — `ZDO.SetOwner(long uid)` — O7 hold-guard

- **SERVER:74674-74680**, confirmed this pass (excerpt below; public method, `ZDO` class).
  Same source both builds (research/03's confirmed byte-identical list includes `ZDO`).
- **Patch kind:** Harmony **prefix**, `bool`-returning (skip vanilla body when held by someone
  other than the mod's own authorized call).
- **Side:** both (the primitive is symmetric, though this project's use of it is server-driven).

```csharp
// SERVER:74674
public void SetOwner(long uid)
{
    if (ZDOExtraData.GetOwner(m_uid) != uid)
    {
        SetOwnerInternal(uid);
        IncreaseOwnerRevision();
    }
}
```

Sketch:

```csharp
[HarmonyPatch(typeof(ZDO), "SetOwner")]
static class ZDO_SetOwner_HoldGuard
{
    // RED-TEAM FIX (RT1 minor, verified): the original bypass was a blanket
    // `[ThreadStatic] static bool AuthorizedCallInFlight`, which would also bypass the hold
    // guard for any OTHER ZDO's SetOwner call made reentrantly on the same thread while the
    // flag is true (e.g. if a future extension, or an interaction with another mod's own
    // SetOwner-adjacent hook, triggers a nested SetOwner on a different ZDO before this one
    // clears the flag). No confirmed reentrant call chain exists in vanilla today
    // (SERVER:74674-74683, ZDO.SetOwner/SetOwnerInternal/IncreaseOwnerRevision show no such
    // call), but the fix is free, so scope the bypass to the specific ZDOID being transferred:
    [ThreadStatic] internal static ZDOID? AuthorizedZDOID;

    static bool Prefix(ZDO __instance, long uid)
    {
        if (!Features.OwnershipAuthority.Active) return true;      // off -> vanilla runs unmodified
        if (AuthorizedZDOID == __instance.m_uid) return true;       // this mod's own transfer, THIS ZDO — let it through
        if (OwnershipHold.IsHeld(__instance.m_uid)) return false;    // held — veto every other caller
        return true;
    }
}

// Every authorized transfer site (H2's claim pass, H8's O8 handback, O9's reassignment) wraps
// its own SetOwner call, and verifies the write actually landed:
static void AuthorizedSetOwner(ZDO zdo, long newUid)
{
    ZDO_SetOwner_HoldGuard.AuthorizedZDOID = zdo.m_uid;
    try { zdo.SetOwner(newUid); }
    finally { ZDO_SetOwner_HoldGuard.AuthorizedZDOID = null; }
    // RED-TEAM FIX (RT2 major, verified against research/13-mod-compat-census.md #1 — see
    // Red-team reconciliation): a third-party skip-capable Prefix on this exact method
    // (confirmed: Wubarrk-GetOffMyLawn's OwnershipHold/SetOwnerGuard and Wubarrk-Wonderland's
    // ItemFlow.ZdoSetOwnerPatch, both server-side, both already installed) can return false and
    // silently no-op the write — Harmony ANDs all bool-returning prefixes, so if any sibling
    // prefix vetoes, SetOwnerInternal/IncreaseOwnerRevision (SERVER:74674-74680) never runs,
    // and nothing before this fix would have noticed. Verify the postcondition instead of
    // assuming success:
    if (zdo.GetOwner() != newUid)
    {
        Telemetry.IncrementSetOwnerVetoed(zdo.m_uid, newUid); // ownership.setOwnerVetoed
    }
}
```

- **Guard (mandatory, per catalog):** at mod load, enumerate `Harmony.GetPatchInfo(AccessTools.Method(typeof(ZDO), "SetOwner"))` and log loudly if any foreign prefix/transpiler is already present — a blanket veto-prefix on this widely-shared vanilla method can silently desync a co-installed mod (the catalog names `GetOffMyLawn` specifically as a confirmed real example) that assumes its own prefix's execution implies the field actually changed.
- **RED-TEAM FIX (RT2 major, verified — research/13-mod-compat-census.md §1 and its summary
  table row `ZDO.SetOwner | Prefix / yes | Wubarrk-GetOffMyLawn[SERVER],
  Wubarrk-Wonderland[SERVER] | HIGH`):** the startup `GetPatchInfo` log only proves a foreign
  patch *exists*, not that any specific call was vetoed. Since `GetOffMyLawn` and `Wonderland`
  are both the user's own mods, this doc's guard is upgraded from passive logging to two
  concrete steps: (1) `AuthorizedSetOwner` (above) verifies `zdo.GetOwner() == newUid` after
  every call and increments `ownership.setOwnerVetoed` on mismatch instead of assuming success;
  (2) set this prefix's Harmony priority explicitly (recommend `Priority.First`, since this
  guard's whole job is to veto competing writers *before* any other mod's own SetOwner-adjacent
  logic runs) and document the resulting order relative to `GetOffMyLawn`'s and `Wonderland`'s
  own prefixes in one place (this doc's Interactions section), rather than leaving Harmony's
  default load-order-dependent priority to decide silently.
- **Off-flag behavior:** prefix returns `true` unconditionally — vanilla `SetOwner`, unmodified.

---

### H8 — New RPC `RPC_RequestOwnershipHandover` — O8

- No vanilla counterpart; new mod RPC, registered following the exact vanilla request pattern
  `Container.RPC_RequestOpen` demonstrates (SERVER:122011-122031, confirmed this pass — current
  owner decides, then calls `SetOwner`).
- **Patch kind:** new RPC registration (via `ZRoutedRpc`/`ZNetView.Register`, the shared infra
  contract's namespaced-handshake pattern), not a patch on existing code.
- **Side:** both — a boarding/remaining crew client sends the request; the **server**, as
  current owner of a SERVER-HELD ship, decides and calls `SetOwner`.
- **Why this exists:** `Ship.UpdateOwner` (SERVER:140858-140867, guard confirmed this pass at
  SERVER:140860: `m_nview.IsOwner() && Player.m_localPlayer != null && m_players.Count > 0 &&
  !IsPlayerInBoat(Player.m_localPlayer)`) can never execute on a dedicated server —
  `Player.m_localPlayer` is permanently null there (research/03 Addition A1, independently
  re-traced this pass: the only call chain that ever sets it runs through `Game.UpdateRespawn`,
  called only from the **client** build's `Game.FixedUpdate`, never the server build's). Once
  the server can own a manned ship (via O6/G3-L1, restricted by O6's own `m_players.Count > 0`
  exclusion... except O8 exists precisely for the case where the server ends up holding a ship
  anyway, e.g. right after every crew member disconnects and it becomes idle, then a new player
  boards), vanilla has no path that ever hands it back — this RPC is that path.

```csharp
void RPC_RequestOwnershipHandover(ZRpc rpc, ZDOID targetShip)
{
    if (!Features.OwnershipAuthority.Active) { return; } // no-op: request simply ignored, vanilla has no such RPC anyway
    var zdo = ZDOMan.instance.GetZDO(targetShip);
    if (zdo == null || !zdo.IsOwner()) return;            // server must currently own it
    var peer = ZNet.instance.GetPeer(rpc);
    if (peer == null || !ShipHasOccupant(targetShip, peer.m_uid)) return; // validate requester is a current occupant
    if (OwnershipHold.IsHeld(targetShip)) OwnershipHold.Release(targetShip); // route through O7 if held
    AuthorizedSetOwner(zdo, peer.m_uid);
}

// RED-TEAM FIX (RT1 major #5, verified): ShipHasOccupant is defined explicitly against the
// same live crew state O9's own Ship exclusion reads (Ship.m_players, SERVER:140293,
// populated via OnTriggerEnter/OnTriggerExit, SERVER:140898-140924) rather than left as an
// unspecified helper — see note below the code block for why this closes the grace-window gap
// without a second, redundant timestamp map.
bool ShipHasOccupant(ZDOID shipZdoid, long uid) =>
    ShipRegistry.Get(shipZdoid)?.HasPlayerAboard(uid) ?? false; // reads Ship.m_players via the ship's own live component
```

- **Off-flag behavior:** handler early-returns without granting the transfer — functionally
  identical to the RPC never having been registered, from the requester's point of view (no
  response, same as a vanilla server that doesn't know this RPC name).
- **RED-TEAM FIX (RT1 major #5, verified):** the Data-loss-analysis table's "Container-open
  duplication race" row's grace-window guard (a timestamp map keyed by ZDOID, written on every
  `RPC_Request*`-family grant) was never wired into H8's grant here, and this doc's own O9 guard
  4 reuses that same unhooked mechanism. Rather than bolt an unhooked timestamp write onto H8 to
  match the pattern by rote, the actual guard that already applies is made explicit: O9 (see
  adjudication above) already excludes any `Ship` with `m_players.Count > 0` from its candidate
  set entirely. Because `ShipHasOccupant` (above) is defined against that exact same
  `m_players` state, a grant can only succeed when the boarding player is already registered in
  `m_players` — which means `m_players.Count > 0` is already true at the instant `H8` grants,
  so O9's own exclusion is already active for this ship before the grant completes, and stays
  active for as long as the crew remains aboard. No separate grace-window write is needed for
  H8 specifically; this doc states that explicitly here instead of silently relying on an
  unhooked mechanism, per RT1's own suggested resolution.
- **RED-TEAM FIX (RT1 major #2, verified — see Red-team reconciliation):** confirmed this pass
  by grepping the full `Ship` class body (SERVER:140181-141062): its only registered RPCs are
  `Stop`/`Forward`/`Backward`/`Rudder` (SERVER:192-195 relative to the class) — no
  `RPC_Request*`-style ownership-claim RPC exists in vanilla for ships, unlike `Container`
  (`RPC_RequestOpen`, SERVER:122011-122032), `Vagon` (`RPC_RequestOwn`, SERVER:147455-147461,
  confirmed `if (m_nview.IsOwner()) { ... SetOwner(sender); }` — current-owner-decides, works
  even when the server is the current owner) and `Sadle` (`RPC_RequestControl`,
  SERVER:18229-18243, same pattern). `Ship.UpdateOwner`'s guard (SERVER:140858-140867:
  `m_nview.IsOwner() && Player.m_localPlayer != null && ...`) can never fire server-side
  (`Player.m_localPlayer` stays null on a headless server — confirmed this pass at
  SERVER:9562/10121-10126: it is only ever set from `Player.SetLocalPlayer()`, called once from
  the client-only spawn path). **This means a genuinely vanilla (unmodded) client has no path at
  all to claim a server-held ship** — this doc's compatibility section previously implied full
  vanilla compatibility by only discussing the container case. Fixed below in "Vanilla and
  version compatibility": this is now stated as a real, explicit limitation (option (a) from
  RT1 — a vanilla-client Ship RPC is a real engineering addition, out of scope for this pass;
  tracked as Open question 6).

---

### H9 — O9's reassignment site (composes with H10, its own independent `ReleaseZDOS` postfix)

**RED-TEAM FIX (RT1 major #3 + RT2 blocker, both verified — see Red-team reconciliation):** the
original text here claimed "O9 does not add a new Harmony patch target beyond H2's `ReleaseZDOS`
postfix and H4's RTT tracking" and that this logic "runs inside the same postfix method H2
already registers." Both claims are wrong as a matter of code fact, not just milestone
sequencing. Confirmed this pass: `ReleaseNearbyZDOS` (SERVER:76948-76971) clears and refills a
single **shared, private** `List<ZDO> m_tempNearObjects` field (declared SERVER:76140,
`.Clear()` at SERVER:76952) on **every** call — vanilla's own per-peer loop
(SERVER:76919-76930) calls it once for the self-pass and once per connected peer, each call
overwriting the previous call's contents. By the time any postfix on the *outer* `ReleaseZDOS`
runs (i.e. H2's postfix, which only starts after `ReleaseZDOS`'s entire body — self-pass plus
every peer pass — has finished), every intermediate per-peer near-object set is already gone,
overwritten by the next call. There is no way to discover "which ZDOs were near peer A" *and*
"which were near peer B" from H2's postfix alone — the data doesn't exist there. So O9's
"argmin over peers near this ZDO" mechanism, as originally specified, had no working
implementation path.

**Fix:** a new hook, H10, below, captures each peer pass's near-object set as it happens,
*before* the next call overwrites it. H9's policy logic — compute `argmin` over
`RttTracker.Smoothed(uid)` among the peers H10 recorded as seeing this ZDO this tick, apply the
margin+dwell hysteresis, and on a qualifying switch call `AuthorizedSetOwner(zdo, winningUid)`
through H7's hold guard — now runs in **O9's own postfix on `ReleaseZDOS`** (see O9 adjudication
point 6's RED-TEAM FIX above), reading H10's per-tick accumulator, not H2's.

- **Off-flag behavior:** gated on `Features.O9.Active` alone (independent of
  `Features.OwnershipAuthority.Active`, per the Configuration table) — when off, O9's own
  postfix and H10's capture both early-return, so no accumulator is even built.

---

### H10 — `ZDOMan.ReleaseNearbyZDOS(Vector3 refPosition, long uid)` — O9's per-peer near-object capture (new)

- **SERVER:76948-76971**, same citation H3 already uses for this method (H3 calls it via a
  cached delegate for O6's claim pass; H10 additionally postfixes it — Harmony permits multiple
  independent patches, prefix/postfix, on the same target method).
- **Patch kind:** Harmony **postfix**.
- **Side:** server only.
- **Why this hook, why it's new, and why it's safe to add:** this is the only point in the call
  graph where a given peer pass's near-object set exists before the next call clears it. The
  postfix reads (never writes) the method's own inputs/private state — it observes
  `m_tempNearObjects` via a cached `AccessTools.FieldRefAccess<ZDOMan, List<ZDO>>` immediately
  after the method body has populated it for this call, copies the persistent ZDOIDs into a
  per-tick `Dictionary<ZDOID, List<long>>` (ZDO → peer uids that saw it in range this pass),
  keyed by `uid` (the method's own second parameter — `m_sessionID` for the self-pass and for
  H2's own claim-pass calls, a real peer's `m_uid` for genuine peer passes). It skips accumulate
  when `uid == ZDOMan.GetSessionID()` (self-pass / O6 claim-pass calls are not peer sightings).
  This adds a read-only observation, not a new write path — it cannot itself cause a data-loss
  hazard, since it never calls `SetOwner` or mutates any ZDO; only O9's own postfix (H9, above),
  reading the finished accumulator after `ReleaseZDOS`'s body completes, ever writes.
- **Why this doesn't need Phase B core:** `ReleaseNearbyZDOS` runs in unmodified vanilla
  regardless of whether `OwnershipAuthority.Active` is on — the self-pass and per-peer passes
  are vanilla's own unconditional behavior (SERVER:76919-76930). H10 is gated on
  `Features.O9.Active` alone, so O9-interim can genuinely ship without G3-L1/O6/O3/O4/G3-N1/
  G4-N3/O8 existing, resolving RT2's blocker.

```csharp
[HarmonyPatch(typeof(ZDOMan), "ReleaseNearbyZDOS")]
static class ReleaseNearbyZDOS_PeerSightingCapture
{
    static readonly AccessTools.FieldRef<ZDOMan, List<ZDO>> TempNearObjectsRef =
        AccessTools.FieldRefAccess<ZDOMan, List<ZDO>>("m_tempNearObjects");

    static void Postfix(ZDOMan __instance, long uid)
    {
        if (!Features.O9.Active) return;
        if (uid == ZDOMan.GetSessionID()) return; // self-pass / O6 claim-pass call, not a peer sighting
        foreach (var zdo in TempNearObjectsRef(__instance))
        {
            if (!zdo.Persistent) continue; // mirror vanilla's own first check, SERVER:76951
            O9SightingsThisTick.Record(zdo.m_uid, uid); // per-tick accumulator, cleared by O9's own ReleaseZDOS postfix after it consumes it
        }
    }
}
```

- **Off-flag behavior:** postfix early-returns; zero overhead, zero behavior change (the sweep
  itself is vanilla code, untouched either way).

## Data-loss analysis

| Path | Mechanism | Guard |
|---|---|---|
| **Vanilla's own sweep silently reverts a server claim before it's used.** | `ReleaseNearbyZDOS`'s per-peer branch treats a server-owned ZDO as unowned-by-anyone-nearby because `IsInPeerActiveArea(pos, serverSession)` always tested the pinned position — confirmed the exact mechanism this pass (SERVER:76934-76946, 76969). | G3-L1, closes it structurally — not a race mitigated after the fact, the predicate itself now returns true for held zones. |
| **RED-TEAM FIX (RT1 major #1): `OwnershipHeldZones` outlives vanilla's own zone TTL eviction.** | `ZoneSystem.UpdateTTL` (SERVER:115548-115561) evicts an `m_zones` entry after `m_zoneTTL` (default 4 s, SERVER:113087) once `HaveInstanceInSector` (SERVER:82368) is false. If `OwnershipHeldZones` were additive rather than re-derived, a TTL-evicted zone would keep reporting server-defended forever, permanently blocking any approaching peer from ever claiming its objects. | H1/H2 RED-TEAM FIX: `OwnershipHeldZones` is fully re-derived from `CurrentClaimTargets()` (itself `m_zones.ContainsKey`-gated live) every `ReleaseZDOS` tick, not additively grown — closes within one O3-interval of eviction. `ownership.staleHeldZoneEvictions` makes it measurable. |
| **Claiming before terrain/objects settle.** | Gating a claim on `IsZoneLoaded` instead of `m_zones.ContainsKey` reproduces the terrain-before-objects fall-through hazard the Z-group already documents. | O6 guard 2, restated in H3: gate strictly on `m_zones.ContainsKey`. |
| **Claiming an actively-driven object.** | Taking ownership of a helmed ship / pulled cart / open container fights the current simulator's `DataRevision`-leader-wins writes — produces visible snap/stall loops, not deletion, but is a real desync. | O6's allowlist + exclusion checks (H3), independently re-derived for O9 in the adjudication above; O8 as the sanctioned hand-back path for the one class (manned ships) with no vanilla fallback. |
| **Container-open duplication race.** | `RPC_RequestOpen` grants ownership and replies `RPC_OpenResponse(true)` before the new owner's own `s_inUse` write is guaranteed to have landed (two independent sends, no ordering guarantee under load, SERVER:122011-122031/121873-121877). If O6's or O9's sweep reclaims in that window, an in-panel item move can duplicate an item. | O6's grace-window guard (5–10 s from any `RPC_Request*` grant, tracked in-memory by timestamp, not relying on `s_inUse` alone) — reused as-is by O9's guard 4. |
| **`ReleaseZDOS` cadence changes (O3) amplify a pre-existing hazard.** | Faster cadence increases the *frequency* of any unguarded boundary double-claim; it does not create a new failure mode. | Tune O3 together with O6 (coupled per catalog); keep within frame budget; prefer transpiler over a skip-and-reimplement prefix so a co-installed mod's own prefix on this generically-named method composes safely. |
| **`ZDO.SetOwner` hold never clears (exception, holder disconnect).** | A bug or crash between taking and releasing an O7 hold makes that ZDOID permanently un-ownable — a stall, not a deletion. | Bound hold duration with an alert threshold; clear on holder disconnect; kill switch (see below) force-flushes the hold set as an explicit, separate recovery path. |
| **Kill switch disabled mid-handover.** | If the flag flips false between H7's hold-take and hold-release (impossible in the normal synchronous path, but a defensive case), a stale hold could survive the flag flip. | P4 guard 7 / G6-N3 guard 4: disabling **must** actively flush the hold set, not just stop granting new holds — implemented as an explicit step in the flag's `SettingChanged` handler, not an implicit side effect of the flag read. **RED-TEAM FIX (RT1 minor):** BepInEx's `ConfigFile` supports live file-reload via a filesystem watcher, and `SettingChanged` can fire off the Unity main thread in that case (reasoned from standard BepInEx behavior — outside this doc's mandated ground truth, i.e. the Valheim decompiles, so flagged rather than cited by line; cheap to guard regardless). Marshal the flush onto the main thread (queue it, apply at the top of the next `ReleaseZDOS`/`Update` tick) rather than mutating `OwnershipHold`/`OwnershipZonePolicy`/`RttTracker` directly inside `SettingChanged`. |
| **Save during handover.** | `ZNet.SaveWorld`'s `PrepareSave()` snapshots whatever is in `ZDOMan` at the moment it's called, synchronously, on whichever thread calls `Save()`. A handover's `SetOwner` write is a single synchronous field mutation with no partial-write state — there is no "half-transferred" ZDO to snapshot mid-write, so an autosave landing between H7's hold-take and hold-release simply captures either the pre- or post-transfer owner, both valid states. No mitigation needed beyond P3's existing "marshal onto the main thread" guard, which this doc inherits rather than re-derives. | P3 (out of this doc's scope, referenced). |
| **Crash mid-handover.** | On process death, in-memory-only state (O7's hold set, O9's RTT trackers) is gone; the last `SetOwner` write that actually reached `ZDOExtraData` (in-memory ZDO store, persisted at the next successful save) is whatever was last durably applied — no different from a crash during any other ordinary gameplay ownership transfer vanilla already performs constantly (container opens, item pickups). Zero new crash-recovery surface. | None needed beyond existing save/reload behavior. |
| **Disconnect/reconnect during a hold.** | O7 guard 3 (clear the hold immediately on the holding machine's disconnect) — "holding machine" here is always the server itself for this doc's holds (server-driven transfers), so this guard is really about a **peer** disconnecting mid-O8-handshake (the requester vanishes between request and grant). | O8's handler re-validates `ShipHasOccupant` at grant time, not just at request time — a disconnected requester fails that check and the grant is simply skipped. |
| **`m_deadZDOs` resurrection guard interaction.** | If any transfer logic in this doc ever recreated a ZDO from cached state (it doesn't — every hook here operates on ZDOs already live in `ZDOMan`), the server's own `m_deadZDOs` guard (SERVER:77040-77043/77180-77184) would silently re-destroy it. Documented as a constraint on any *future* extension of this module, not a live hazard today. | Don't add replay/recreate logic to this module without re-reading research/03 Addition A2 first. |
| **OwnerRevision churn / bandwidth.** | Every `SetOwner` call — vanilla's, O6's, O7's, O8's, O9's — bumps `OwnerRevision`, which forces a resend to every peer via `ZDOPeer.ShouldSend` (SERVER:76049-76058, cited in research/03 §7 and reconfirmed by the G2-N2 cross-reference). O9 in particular *adds* reassignment events vanilla wouldn't otherwise make (it overrides, not merely accelerates, the target peer choice), so it is not bandwidth-neutral versus doing nothing. | O9's hysteresis (margin + dwell) exists precisely to bound how often this fires — a switch only happens when it's worth the resend cost. O3's cadence is coupled to G2-N2's bandwidth accounting for the same reason: don't lower `ReleaseZDOS`'s interval without re-checking `SendZDOs` volume against Q2/Q3/Q5's budgets. Telemetry (below) counts churn events specifically so this is measurable, not assumed. |

## Vanilla and version compatibility

- **Vanilla client + modded server.** Every hook in this doc is server-side except H4/H10's RTT
  and sighting-capture work (server-only by design; a vanilla client's `ReceivePing` reply,
  which the server's measurement depends on, is unmodified core `ZRpc` behavior it already
  performs) and H6 (O4, client-only, gated behind a capability handshake so a vanilla client
  simply never activates the tightened cadence). A vanilla client sees only the effects of who
  owns what — ordinary `SetOwner`-driven behavior it already handles every time it opens a chest
  or watches ownership churn near a zone boundary — **for every object class except ships.**
  O8's handback RPC is registered under a namespaced name a vanilla client never sends, so it's
  simply unreachable from a vanilla client (no crash — vanilla clients don't send unregistered
  RPC names).
  **RED-TEAM FIX (RT1 major #2, verified — see H8 and Red-team reconciliation):** this is a real
  limitation for ships specifically, stated explicitly here rather than left implicit: confirmed
  this pass that `Ship` registers only `Stop`/`Forward`/`Backward`/`Rudder`
  (SERVER:140181-141062) — no vanilla `RPC_Request*`-style ownership-claim RPC exists for ships
  at all (unlike `Container`/`Vagon`/`Sadle`, all confirmed to have a working current-owner-
  decides RPC that functions correctly even when the server is the current owner) — and
  `Ship.UpdateOwner` (SERVER:140858-140867) can never execute server-side because
  `Player.m_localPlayer` is permanently null on a dedicated server. **A vanilla (unmodded)
  client that boards a server-held ship has no path to ever become its owner** until it installs
  this mod; only a modded client can send H8's `RPC_RequestOwnershipHandover`. This is a real,
  accepted vanilla-compatibility gap for ships only — every other object class this doc's O6
  allowlist covers keeps full vanilla-client compatibility. Building a true vanilla-client-
  reachable hand-back path (e.g. patching `Ship` to add a real request-grant RPC under a
  vanilla-looking name a vanilla client's own boarding code would invoke) is tracked as Open
  question 6, deferred out of this pass's scope.
- **Modded client + vanilla server.** H6 (O4) explicitly falls back to vanilla's 2.0 s cadence
  when the capability handshake fails (mandatory guard, restated above) — a modded client on a
  vanilla server behaves exactly like a vanilla client for every hook in this doc, since every
  other hook here is server-side and simply doesn't exist on that server.
- **Modded version skew (modded client, older/newer modded server, or vice versa).** Gated by
  the shared infrastructure contract's namespaced RPC handshake carrying an explicit protocol
  version (per G1-N1's mandatory guard, inherited here): O4's tightened cadence and O8's handback
  RPC both require a version match (or an explicit compat table) before activating; on mismatch
  they fail closed to vanilla behavior, never silently misparse.
- **PlayFab/crossplay sockets.** None of this doc's hooks touch socket-layer code
  (`ZPlayFabSocket`, `ZSteamSocket`) at all — `ZRpc`'s ping/pong (H4) and every ownership
  primitive here sit entirely above the transport layer, so crossplay connections are unaffected
  beyond whatever T1–T6 (out of this doc's scope) already do to them.

## Configuration

All server-authoritative, pushed to modded clients over the shared infra's custom RPC (no
ServerSync), per the shared contract. Local-only keys apply without a server round-trip.

| Key | Default | Range | Server-synced? |
|---|---|---|---|
| `OwnershipAuthority.Active` | `false` (opt-in until Phase B is field-tested) | bool | local kill switch, also console-command-settable |
| `OwnershipAuthority.ReleaseZdosInterval` | `1.0` s | 0.5–2.0 s | server-authoritative |
| `OwnershipAuthority.ClaimGraceWindow` | `5.0` s | 3–10 s | server-authoritative |
| `OwnershipAuthority.PeerRefPosPadding` | `64` m (one zone width) | 0–192 m | server-authoritative |
| `OwnershipAuthority.SimulationDistanceCeiling` | `4` (practical band) / `6` (hard clamp) | 0–10 (hard-clamped) | server-authoritative |
| `OwnershipAuthority.O7HoldTimeoutAlert` | `10.0` s | 5–60 s | local (ops-facing alert threshold only) |
| `OwnershipAuthority.MaxZonesClaimedPerCycle` **(RED-TEAM FIX, RT2 major)** | `8` | 1–32 | server-authoritative |
| `ClientRefPos.Interval` (H6/O4) | `0.5` s | 0.2–1.0 s | client-local, only activates after handshake confirms a NoodlesMcDoodles server |
| `O9.Active` | `false` | bool | server-authoritative, independent flag from `OwnershipAuthority.Active` so O9 can ship/ship-off separately per the "interim, can ship standalone" design |
| `O9.RttMarginMs` | `30` | 10–100 ms | server-authoritative |
| `O9.RttDwellSeconds` | `5.0` (NEEDS-LIVE-TEST to tune) | 2–30 s | server-authoritative |
| `O9.RttEwmaAlpha` | `0.2` | 0.05–0.5 | server-authoritative |

## Telemetry

Per `NoodlesMcDoodles.Core.Telemetry` conventions, periodic + per-peer where relevant:

- **`ownership.serverHeldZones`** — current count of zones the server claims (proves O6/G3-L1
  are doing something, at a glance).
- **`ownership.claimsPerMinute` / `ownership.releasesPerMinute`** — vanilla-equivalent counters
  split by cause (`vanilla-sweep`, `o6-claim`, `o9-reassign`, `o8-handback`) — the direct proof
  that G2-N2's bandwidth-coupling concern is being measured, not assumed.
- **`ownership.holdOutliers`** — count of O7 holds that exceeded `O7HoldTimeoutAlert`; should be
  ~0 in steady state, a non-zero value is the first signal something in H2/H7/H8/H9 is racing.
- **`ownership.graceWindowBlocks`** — count of claim attempts skipped because they landed inside
  the container-open grace window — proves the duplication-race guard is actually firing, not
  dead code.
- **`ownership.o9.rttSamples` / `ownership.o9.rttSmoothed`** per connected peer — the direct
  input O9 acts on; exposing it lets the live test that settles `RttDwellSeconds` actually be
  run and measured, not guessed.
- **`ownership.o9.switches`** — count of actual reassignments O9 performed, versus
  **`ownership.o9.switchesConsideredButSuppressed`** (margin or dwell not met) — the pair proves
  the hysteresis guard is doing real work, not merely present in code.
- **`ownership.killSwitch.holdsFlushed`** — logged once per kill-switch disable event, with the
  count flushed — the direct, auditable proof P4/G6-N3's "must actively flush" guard executed.
- **`ownership.deadZoneStalls`** — count of ZDOIDs whose O7 hold outlived the alert threshold
  and had to be force-cleared — should be ~0; any non-zero value is a zero-data-loss audit flag
  (a stall an operator needs to know happened), not silently absorbed.
- **`ownership.staleHeldZoneEvictions`** — **RED-TEAM FIX (RT1 major #1).** Count of zones
  dropped from `OwnershipHeldZones` on a given tick specifically because they no longer appear
  in `CurrentClaimTargets()` due to `m_zones` no longer containing them (vanilla TTL eviction),
  as opposed to the normal case (a peer moved into range). Proves the re-derivation guard is
  actually catching real evictions, not dead code.
- **`ownership.zonesProcessedPerCycle`** (count) and its wall-clock cost — **RED-TEAM FIX (RT2
  major).** Direct measurement of `MaxZonesClaimedPerCycle`'s effect: how many of the current
  held-zone count actually got a real `ReleaseNearbyZDOS` sweep this tick, and how long that
  took, so the O3/O6/O1 cadence-vs-ceiling coupling this doc already flags as needing joint
  tuning is measured, not assumed safe.
- **`ownership.setOwnerVetoed`** — **RED-TEAM FIX (RT2 major).** Count of `AuthorizedSetOwner`
  calls whose postcondition (`zdo.GetOwner() == newUid`) failed to hold after the call returned
  — the direct signal that a third-party `ZDO.SetOwner` prefix (confirmed installed:
  `GetOffMyLawn`, `Wonderland`) silently vetoed a transfer this doc's telemetry would otherwise
  have reported as successful. Should be ~0; non-zero is a priority-ordering bug to fix, not a
  data-loss event by itself (the object simply keeps its previous, valid owner).
- **`ownership.o8.requestTimedOut`** — **RED-TEAM FIX (RT2 major).** Count of
  `RPC_RequestOwnershipHandover` requests that received no server response within a bounded
  timeout — the direct signal that H8's RPC (or the handshake gating it) was silently dropped
  by a third-party `ZRoutedRpc`/`HandlePackage` guard (confirmed installed and stacked:
  AzuAntiCheat, Wonderland, three RavenIronStudios mods, Zen_ModLib) rather than reaching this
  doc's handler at all.

## Interactions with other subsystems

- **Transport (01).** O9's RTT samples ride the existing `ZRpc` ping channel, not a new
  socket-level mechanism — no interaction with T1–T6's Steam/PlayFab rate tuning beyond sharing
  the same connection. O3/O6's increased claim/reassignment frequency feeds `SendZDOs` volume,
  which is exactly G2-N2's cross-group bandwidth coupling — this doc owns the cadence value,
  G2's budget levers (Q2/Q3/Q5) own the ceiling that volume must fit under.
- **Rails (shared infra contract).** Every hook here is written behind
  `Features.OwnershipAuthority.Active` and `Features.O9.Active` independently; the negotiated
  RPC handshake gates H6/H8; config values are server-pushed per the contract, not
  ServerSync-based.
- **Milestone sequencing / cross-doc prerequisites — RED-TEAM FIX (RT1 major #4 + RT2 blocker,
  both verified).** This doc now depends on **G1-L1** (duplicate-peer-connection guard on
  `ZNet.RPC_PeerInfo`/`ZDOMan.AddPeer`, confirmed no existing check at SERVER:76814-76820) as a
  hard prerequisite for every uid-keyed mechanism here (H1, H4, H7, H9, H10) — added to the
  levers table above, the same way G3-N1 is already treated as mandatory for O6. Separately,
  **O7 (H7)** and the new **H10** are reclassified from "Phase B core" to a **Shared groundwork**
  milestone that ships alongside O9-interim (see milestone table): both patch methods
  (`ZDO.SetOwner`, `ZDOMan.ReleaseNearbyZDOS`) that run in unmodified vanilla regardless of
  whether the server ever claims a zone, so neither has an actual functional dependency on
  G3-L1/O6/O3/O4/G3-N1/G4-N3/O8. Phase B core still depends on O7 (H2's claim pass and H8's
  handback both route through `AuthorizedSetOwner`), it just doesn't own O7's milestone slot —
  O7 ships once, reused by both O9-interim and Phase B core.
- **ZDO replication (02).** `SetOwner`'s `OwnerRevision` bump and its `ShouldSend` interaction
  are the direct bandwidth cost every hook in this doc pays for every transfer — quantified in
  the Data-loss analysis table's last row and measured by the telemetry counters above.
- **Server zone simulation (04).** O6/G3-L1's claim pass only operates on zones `m_zones`
  already has loaded (H3's gate) — it is downstream of, not a replacement for, whatever poke-ring
  driver (Z2) and object-instantiation prefix (Z3) that doc defines to get a zone loaded and
  populated on the server in the first place. G6-N2's TerrainComp handover-atomicity guard
  (out of this doc's scope) is the reason TerrainComp is excluded from O9's candidate set
  entirely, rather than this doc inventing a second, competing atomicity mechanism for the same
  class.
- **Gameplay under server authority (05).** Every object class this doc's O6 allowlist claims
  (creatures, structures, traps, idle containers/carts/boats) is exactly the set that doc's own
  S-group levers (S1/S2/S4/S7/S8) depend on G3-L1 for — restated here since G3-L1 is this doc's
  hook, not that doc's: those levers cannot function without H1 shipping first, which the levers
  table's `req: ['G3-L1']` on O6 already encodes.
- **Client smoothing (11) / delta and compression (12).** No direct interaction — those C*/D*
  levers operate on payload shape and interpolation, not on who owns what. The only shared
  surface is that every ownership churn event this doc produces is one more `OwnerRevision`
  bump those modules' payloads carry; no design coupling beyond volume.
- **Other mods.** **RED-TEAM FIX (RT2 major, verified — see Red-team reconciliation):** the
  original text here claimed "AzuAntiCheat and the server's other ~47 mods were not found to
  touch any hook in this doc" — this is now directly contradicted by
  `research/13-mod-compat-census.md`, which exists and was completed after that claim was
  written. The corrected picture, per the census:
  - **`ZDO.SetOwner`** (H7/O7) — confirmed two pre-existing skip-capable prefixes, both the
    user's own mods: `Wubarrk-GetOffMyLawn` (`OwnershipHold/SetOwnerGuard`) and
    `Wubarrk-Wonderland` (`ItemFlow.ZdoSetOwnerPatch`), both server-only. Addressed by H7's
    RED-TEAM FIX above (postcondition verification + explicit priority), not just the startup
    `GetPatchInfo` log.
  - **`ZRoutedRpc.RouteRPC`/`HandleRoutedRPC`/`RPC_RoutedRPC`** (H8's `RPC_RequestOwnershipHandover`
    travels through here unavoidably — confirmed `ZNetView.InvokeRPC`, SERVER:82896-82903,
    always calls `ZRoutedRpc.instance.InvokeRoutedRPC`) — confirmed **five** independent
    guard/optimization systems already stacked on this exact method family: `Azumatt-AzuAntiCheat`
    (`RoutedRpcGuards`, one skip-capable, one not), `Wubarrk-Wonderland` (two systems, one
    skip-capable), three `RavenIronStudios` mods' sender-side skip-capable guards, and
    `ZenDragon-Zen_ModLib`'s non-skip spam-dedup finalizer. A namespaced, mod-only RPC name is a
    plausible target for a whitelist-style skip-capable guard to drop silently.
  - **`ZRpc.HandlePackage`** (the shared infra contract's negotiated RPC handshake dispatches
    through here) — confirmed a skip-capable prefix from the user's own `Wubarrk-Wonderland`
    (`Security.ModEnforcementPatches/Patch_ZRpc_HandlePackage`), plausibly a whitelist of known
    RPC/method names per the census's own reading of its sibling guards.
  - **Fix:** H8 gets a request-timeout/retry so a silently-dropped handback RPC is detected
    (`ownership.o8.requestTimedOut`, see Telemetry) instead of failing invisibly with no
    diagnosable cause. The negotiated handshake should be verified against `Wonderland`'s actual
    whitelist logic before shipping (its source is available — it's the user's own mod, not a
    black box) and tested live against a `Wonderland`-enabled server specifically; a raw
    `ZRpc.Register`-based RPC (bypassing `ZRoutedRpc` entirely, still passing through
    `HandlePackage`) is worth considering for the handshake specifically if that testing finds a
    real collision, per the census's own recommendation.
  - **`ZDO.SetOwnerInternal`/`ZDO.Deserialize`** — observed (not vetoed) by AzuAntiCheat's
    activity/validation logging; not a correctness risk to this doc, but every `SetOwner` call
    this doc's hooks make generates proportional AzuAntiCheat log/CPU work — already accounted
    for by this doc's existing hysteresis/cadence guards (O9's margin+dwell, O3's cadence cap),
    not a new obligation.
  A full re-check before enabling `OwnershipAuthority` against live Proxima Maxi should still
  re-verify the census against whatever plugin set is installed at enable time (the census is a
  point-in-time snapshot, not a live guarantee).

## Test plan

**Testbed (single server + one real client, per `libs-Tools/DEDICATED-SERVER-TESTBED/run-linux-1.0-server.sh`):**

1. **G3-L1 sanity.** Server claims a zone with no peer nearby; confirm via
   `ownership.serverHeldZones` that it stays claimed across 10 `ReleaseZDOS` cycles (per the
   lever's own stated live test).
2. **Handover on approach.** Walk a client into a server-held zone; confirm ownership transfers
   to the client within one `O3`-interval + one `O4`-interval of the client's refPos actually
   entering the (padded) active area, with zero `ownership.holdOutliers`.
3. **Container-open race.** Open/close a chest repeatedly at high frequency while the server's
   claim pass is active nearby; confirm `ownership.graceWindowBlocks` increments and no item
   duplication occurs (extends the catalog's own P8 Scenario acceptance test).
4. **O8 handback.** Let a ship go server-held (crew fully disconnects), reconnect, board, send
   the handover RPC, confirm ownership lands on the boarding client with a single `SetOwner`
   call (no snap-loop).
5. **Kill switch mid-handover.** Toggle `OwnershipAuthority.Active` off while a claim/handover
   is in flight (scripted tight loop); confirm `ownership.killSwitch.holdsFlushed` fires and no
   ZDOID stays permanently held (extends P8 Scenario 6's fallback-swap-kill pattern to this
   module's own state).
6. **RED-TEAM FIX (new, RT1 major #1) — held-zone TTL eviction.** Let a server-claimed zone's
   scene instances go idle long enough for `ZoneSystem.UpdateTTL`'s 4 s default to evict it from
   `m_zones` (e.g. via the same conditions doc-04's driver would otherwise refresh); confirm
   `ownership.staleHeldZoneEvictions` increments within one `ReleaseZDOS` tick of eviction and
   that a peer walking into the (now-unclaimed) area afterward successfully claims the objects
   there via vanilla's own sweep, with zero `ownership.holdOutliers`.
7. **RED-TEAM FIX (new, RT2 blocker) — O9 standalone, `OwnershipAuthority.Active=false`.**
   With `Features.OwnershipAuthority.Active=false` and only `Features.O9.Active=true`, confirm
   O9 still reassigns a player-held, multi-peer-visible object to the lower-RTT peer (proves H10
   + O7 + O9's own `ReleaseZDOS` postfix genuinely don't need G3-L1/O6/O3/O4/G3-N1/G4-N3/O8 to
   be loaded at all — the actual claim this milestone makes).
8. **RED-TEAM FIX (new, RT2 major) — O6 claim-pass cost cap.** With held-zone count artificially
   pushed above `MaxZonesClaimedPerCycle`, confirm `ownership.zonesProcessedPerCycle` never
   exceeds the configured cap in a single tick and that all held zones still get re-affirmed
   within a bounded number of ticks (round-robin fairness), not starved indefinitely.
9. **RED-TEAM FIX (new, RT2 major) — third-party `SetOwner` veto detection.** With
   `GetOffMyLawn`'s or `Wonderland`'s own `ZDO.SetOwner` prefix installed and configured to
   veto a specific ZDO, attempt an O6/O8/O9 transfer on that ZDO; confirm
   `ownership.setOwnerVetoed` increments and the object's owner is unchanged (no silent
   false-positive "success" in `ownership.claimsPerMinute`/`o9.switches`).

**Live test with NoodlesProbe / telemetry, ideally with the Arizona admin present (per HANDOFF.md's next-step plan):**

10. Run one session with `OwnershipAuthority.Active=true`, `O9.Active=false` first — measure
   `ownership.claimsPerMinute`/`releasesPerMinute` split by cause, confirm the server-held-zone
   count stabilizes (not flapping) near the admin's position, and confirm the Arizona admin's
   own perceived lag for nearby creature/structure updates drops (this is the actual goal this
   whole module exists to hit).
11. Enable `O9.Active=true` in a second session with at least one low-latency local peer also
   connected near the admin; log `ownership.o9.rttSamples`/`rttSmoothed` for both, and
   `ownership.o9.switches` vs `switchesConsideredButSuppressed`; use this data to settle
   `O9.RttDwellSeconds` (the one NEEDS-LIVE-TEST parameter in this doc) rather than shipping the
   5 s starting guess unmeasured.
12. Cross-check `ownership.claimsPerMinute`'s bandwidth cost against G2-N2's own measurement
   obligation (SendZDOs byte-volume delta) at the tuned O3 cadence, on the same session.
13. **RED-TEAM FIX (new, RT2 major) — RPC pipeline collision check against the live mod stack.**
   With Proxima Maxi's actual `~47`-mod set running (not the testbed's clean set), send an H8
   handover request and confirm it either succeeds or `ownership.o8.requestTimedOut` fires
   within the configured timeout — never a silent no-response with no telemetry signal.
   Separately confirm the negotiated capability handshake (gating H6/H8) actually reaches this
   mod's handler through `Wonderland`'s `ZRpc.HandlePackage` guard, per the census's
   recommendation to test against a `Wonderland`-enabled server explicitly.

## Open questions

1. **O9's exact dwell-time value** — bounded by reasoning (2–30 s, recommend starting at 5 s)
   but genuinely NEEDS-LIVE-TEST against real Arizona↔New Jersey jitter, not derivable from the
   decompile alone (this is the one sub-piece of O9 not resolved to plain SAFE-WITH-GUARDS by
   this pass).
2. **RESOLVED by `research/13-mod-compat-census.md` (RT2 major, see Red-team reconciliation) —
   residual open piece: keeping it current.** The census now exists and confirms `GetOffMyLawn`
   and `Wonderland` on `ZDO.SetOwner`, `Wonderland` on `ZRpc.HandlePackage`, and five systems on
   `ZRoutedRpc`. What remains genuinely open: the census is a point-in-time snapshot of the
   plugin set at
   `/home/rohan/ArchShare/ProximaMaxi/BepInEx/plugins/` — it should be re-run (or at minimum
   diffed against the installed DLL set) immediately before `OwnershipAuthority` is actually
   enabled against live Proxima Maxi, since the mod list can change between now and then.
3. **RESOLVED (held-band redesign synthesis, 2026-09-28).** Original question: whether O9 should
   also apply within a SERVER-HELD zone's *boundary* region — i.e., when a server-held zone's
   claim is about to lapse (a peer is approaching) and two peers approach simultaneously, should
   O9's RTT tie-break decide who the zone hands to, rather than leaving that to vanilla's plain
   iteration-order pick? **Answer: O9's own write path only, never O6's/`HeldBandRingBonus`'s —
   the two domains are geometrically disjoint by construction, so there is no dual-writer
   ambiguity to resolve.** O9 (H9/H10) only ever captures and reassigns objects a peer's OWN
   `ReleaseNearbyZDOS` pass sights — real reach ~96-112m (`PointInsideActiveArea`,
   `SERVER:82344-82365`) — i.e. strictly INSIDE some peer's own active area. O6/`HeldBandRingBonus`
   (this doc, H2/H3 + `docs/design/04`'s held band) only ever holds a zone whose centre is OUTSIDE
   every connected peer's padded active area (`AnyPeerWithinPaddedActiveArea`, ~160m+ at default
   padding) — a zone can be an O9 candidate or an O6 candidate, never both, by the same geometric
   argument that keeps O6 from ever claiming from a present player in the first place (see the
   `ClaimZone` remarks above). Extending O9 specifically to the SERVER→PLAYER handback moment this
   question originally asked about remains a distinct, separate idea — that is what
   server-in-shared-areas (O10, below) exists to cover, on its OWN write path (never O6's), once
   it is actually built; this question itself, about whether O6 and O9 could ever contend over the
   SAME write, is closed.

### O10 — server-in-shared-areas (server owns shared areas when every nearby player is far)

Not built as part of this pass (the held-band synthesis above, decision 1) — its target scenario
(2+ simultaneously-connected far-RTT peers sighting the same object) has never occurred in any live
session to date; both real sessions so far had exactly one far peer plus one near peer,
precisely the case this design does NOT engage for. Its own reasoning is preserved here, ready to
build, for the separate decision that covers it:

- **Trigger**: engage only when EVERY peer currently sighting an object is "far" (RTT above a
  threshold), a deliberately conservative choice over a fuller cost-sum model — a purely local
  multiplayer session structurally cannot trigger this, since any near-RTT sighting peer blocks it.
- **Aggregate round-trip cost derivation**: `cost_player(P) = cost_server + (N-2) * RTT_P` — the
  reasoning for why routing a shared object's simulation through the server can cost LESS aggregate
  round-trip latency than leaving it on one far player's own client once 2+ other peers also see it.
- **Disjointness from O6/`HeldBandRingBonus`**: sound and re-derivable independently (see Open
  question 3's resolution above) — O10's own candidate set (objects INSIDE some peer's active area,
  sighted by 2+ far peers) and O6's (zones OUTSIDE every peer's active area) cannot overlap.
- **Destructive-prone direction (player→server)**: reuses `IsExcluded`'s existing pre-filter
  (Container-in-use/Sadle-ridden/Ship/Vagon/TerrainComp, already live in O9),
  `ReclaimGate.AllowReclaim` → `HandoverGuard.IsSafeToReclaim`'s fail-closed-per-category logic, and
  `OwnershipApi.SetOwner` → `AuthorizedSetOwner`'s postcondition check — verbatim, unmodified; O10
  should only ever decide WHEN to invoke this existing hardened machinery, never reimplement HOW a
  claim/hold/release step is carried out.
- **What does not yet exist**: the actual control-flow surgery on `LatencyAwareOwnership`'s live
  reassignment pass, and a new refcounted `SharedAreaZoneHold`-style registry for the shared-area
  hold itself — neither has been reviewed as real code yet; whoever builds this needs the same
  data-loss/correctness/runtime review pass O9's own code went through, plus a `SafeToggle`-style
  arming gate matching O9's own, before it ships live.
- Revisit/build once (a) O9 itself has a live RTT/dwell test, and (b) a real far+far multiplayer
  session is actually observed on Proxima — or per whatever separate decision authorizes building
  it; full scoring and both proposal texts are preserved in
  `redesign/redesign-result.json` (scratchpad).
4. **`ApplyConfiguredSimulationDistance`'s (H5) interaction with `ZoneSystem`/`Water`/`Heightmap`
   `ApplySettings()` side effects at a raised ceiling** — O1's own catalog entry already flags
   this as requiring load-testing on target hardware per increment; this doc inherits that
   obligation rather than re-deriving it, since H5 calls the same vanilla `ApplySimulationDistance`
   vanilla itself calls on every simulation-distance renegotiation.
5. **Exact wire mechanics of H4's ping/pong sketch** (peek-vs-postfix-timestamp approach) is left
   as an implementation choice in the Hooks section rather than fully specified IL — both
   formulations are public-API-only and equally valid; whichever is simpler against the actual
   Harmony patching of a `private` method with an `out`-by-ref `ZPackage` read position should be
   chosen during implementation, not pre-decided here. (**RED-TEAM FIX, RT2 minor:** the peek
   variant's discriminator condition itself was buggy as originally written —
   `package.GetPos() == 0` can never be true at that point, confirmed against
   `ZRpc.HandlePackage`'s `ReadInt()` call ahead of it, SERVER:83952-83958 — now fixed in H4's
   sketch above; this open question is only about which of the two *correct* approaches to pick,
   not whether the peek approach works at all.)
6. **RED-TEAM FIX, new (RT1 major #2): building a real vanilla-client hand-back path for
   server-held ships.** This doc now explicitly documents (see "Vanilla and version
   compatibility") that a vanilla (unmodded) client has no path to claim a server-held ship,
   unlike every other O6-allowlisted class. Whether to close this gap by patching `Ship` to add
   a real request-grant RPC under a vanilla-looking name (so an unmodded client's own boarding
   code would invoke it, mirroring `Vagon`/`Sadle`'s pattern), or to accept the limitation and
   require the mod client-side for ship ownership specifically, was not adjudicated in this pass
   and needs its own scoping decision before Phase B core ships.
7. **RED-TEAM FIX, new (RT1 minor): H6's (O4) missing plausibility-check / teleport-exemption
   cross-reference.** `research/10-LEVERS.md`'s O4 entry lists a server-side plausibility check
   on `RPC_ServerSyncedPlayerData` (confirmed: no distance/plausibility validation exists today)
   as guard (2), with a mandatory teleport-exemption reset as guard (3) — flagged there as
   "guaranteed-to-recur," not an edge case. This doc's H6 section implements only guard (1)
   (the capability-handshake fallback) and doesn't mention guards (2)/(3) at all. Confirmed this
   pass this is a real omission, not a rejected finding — this doc deliberately does not
   implement the plausibility check in this pass (H6 stays scoped to the cadence transpiler
   alone), so guard (3)'s teleport hazard is currently moot; but if a future revision adds the
   plausibility check without re-reading `research/10-LEVERS.md`'s guard (3) first, it will
   reintroduce exactly the "portal freezes the refPos anchor forever" bug that source already
   named. Recorded here so that revision finds it.

## SAA WP-A — near-field claim policy (`OwnershipAuthority.Mode`, O11)

Server-authoritative areas (SAA), per the user's binding direction ("lets take the Desync out of desync by
removing the client as the adhoc zone controller... server being more responsible and handing things out,
instead of other players' computers") and decision 1 ("everything but items": `OwnershipAuthority.Mode =
FarOrShared` — creatures, buildings, production, trees/rocks, doors, terrain, tames around players; chests,
carts, item stands, signs and ground items stay with whoever uses them; ships and pickables are never taken; a
player's own character is never taken). Full design/adjudication: `scratchpad/saa/final.md` (authoritative for
this section); implementation status: `docs/impl/OWNERSHIP-IMPL.md`'s own SAA WP-A section.

**What this widens, and what stays exactly as it was.** Every hook above this section (H1-H10, O6's own claim
pass in particular) already existed before SAA and is **completely unchanged** for the always-existing
"far-band" case — a zone no connected peer is anywhere near. SAA's only new surface is what happens to an
object O6's own bulk claim (`ReleaseNearbyZDOS`, called from `ZoneClaimSweep.ClaimZone` exactly as H2/H3
already describe) reaches **inside a zone a peer is actually standing in** ("near-field"). Before SAA, H3's own
zone-eligibility gate (`AnyPeerWithinPaddedActiveArea`) simply excluded any such zone from candidacy at all —
`Mode=SharedFar` is that exact behaviour, preserved byte-for-byte as the permanent floor at every Mode: an
object in the always-existing far-band case is allowed exactly as it always was, checked first, before any of
the machinery below even runs (`NearFieldClaimPolicy.IsClaimable`'s own first line).

### H11 — `ZDO.SetOwner(long uid)` — near-field object-level veto (new, `NearFieldClaimVeto.cs`)

- **SERVER:74674-74680** (same target H7/O7 already patches — `SetOwnerHoldGuard.Patch_ZDO_SetOwner_HoldGuard`).
  This is a SECOND, independent Harmony prefix on the same method, not a replacement.
- **Patch kind:** Harmony prefix, default priority (Harmony ANDs every bool-returning prefix's result on one
  method regardless of registration order — this composes safely with H7's own prefix and with
  GetOffMyLawn's/Wonderland's third-party prefixes on this same method, neither of which ever blocks a transfer
  *to* the server, per H7's own class remarks).
- **Side:** server only (`ZNet.instance.IsServer()` checked first, matching every sibling patch in this
  module).
- **Scope, structurally narrow:** fires its real logic only when BOTH `SetOwnerHoldGuard.AuthorizedBulkClaimInFlight`
  is true (O6's own bulk claim window — nothing else in the mod, and no third-party code, can set this) AND
  the target `uid` is the server's own session id (a claim *to* the server, never away from it — O8's
  hand-back and O9/O10's reassignment, both targeting a *player* uid, are untouched by construction). Every
  other `ZDO.SetOwner` call anywhere in the process — a plain client's own ownership churn, a vanilla peer's own
  claim, O8, O9, O10 — returns `true` immediately from this prefix's first two checks.

```csharp
// final.md §2's own sketch, implemented verbatim
static bool Prefix(ZDO __instance, long uid)
{
    if (!SetOwnerHoldGuard.AuthorizedBulkClaimInFlight) return true;
    if (uid != ZDOMan.GetSessionID()) return true;
    return NearFieldClaimPolicy.IsClaimable(__instance, uid);
}
```

This is the actual enforcement point a prior design pass (v1) was missing — v1's own zone pre-scan
(`ZoneClaimSweep.TryPrepareClaim`) only ever deferred a whole zone or accumulated telemetry; nothing filtered
an individual object once a zone's bulk claim actually ran. H11 fires once per object vanilla's own
`ReleaseNearbyZDOS` loop tries to claim, not once per zone.

### Object-level policy — `NearFieldClaimPolicy.IsClaimable(ZDO, long)` (new, `NearFieldClaimPolicy.cs`)

Per object, in order (matches `final.md` §2 exactly):

1. **Far-band check, first, unconditional at every Mode.** `!ZoneClaimSweep.AnyPeerWithinPaddedActiveArea(zdo.GetSector(), peers)`
   — reused as a pure predicate, made `internal` (the one signature change to `ZoneClaimSweep.cs`). Checked
   against the OBJECT's own zone, not necessarily the zone `ClaimZone` is currently claiming: vanilla's own
   `ReleaseNearbyZDOS` scans a real-world radius around a zone's centre wide enough to genuinely reach a
   neighbouring zone that IS peer-overlapped even while the zone being claimed itself is not. If far-band:
   `true`, immediately — nothing below this line ever runs for a far-band object.
2. **`Player` component → refuse, always, every Mode.** A connected peer's own avatar is never taken.
   Structural belt-and-suspenders: the *primary* fix for this hazard is `LatencyAwareOwnership.IsExcluded`'s own
   categorical `Player` check (O9/O10's own file, a different builder's task this same pass — see
   `docs/impl/OWNERSHIP-IMPL.md`'s WP3 fix batch), since O9/O10's reassignment path is the one with no
   pre-existing distance margin at all; O6's own claim path already has real headroom from vanilla's own
   `IsInPeerActiveArea` arithmetic even without this check (traced by hand against a live peer's refPos
   staleness margin — see `final.md` §0). `ownership.o11.playerZdoClaimAttempted` counts every time THIS check
   actually fires — it must stay 0 across a real session; nonzero means O6's own bulk claim reached a live
   player's avatar before this veto caught it, a loss-risk signal, not a routine metric.
3. **`Pickable` within `OwnershipAuthority.PickableReleaseRadius` of any connected peer's LIVE position → refuse.**
   Delegates to `PickableRelease.IsNearLivePlayer` ("saa:compat-pickable" builder's file this pass) — LIVE
   position, never the staler `refPos` `PeerRefPosPadding` governs, since a 5 m vanilla interact range closing
   at running speed needs tighter freshness than the ~3 s worst-case staleness `PeerRefPosPadding` accepts for
   zone-level eligibility. Complemented by a retroactive pass (below) for a `Pickable` claimed on an *earlier*
   cycle, before a peer walked up — a claim-time veto alone can't undo that.
4. **`CompatClaimExclusions.IsExcluded` / `InClientClaimGrace` → refuse.** Another installed mod's own narrow,
   timed hold on this exact object, or a client's own recent interaction burst (AzuCraftyBoxes crafting from
   nearby chests, AzuAreaRepair's area repair) that must not be fought back — "saa:compat-pickable" builder's
   file this pass.
5. **Mode class allowlist.** `FarOrShared` (default): `WearNTear`, `Smelter`/`Fermenter`/`Beehive`/
   `CookingStation`/`Incinerator`/`ShieldGenerator`, `MineRock`/`MineRock5`, `TreeBase`/`TreeLog`, `Door`,
   `TerrainComp`, `Windmill`, `CraftingStation`, `BaseAI`/`MonsterAI`, `Tameable` — none of these ever change
   simulator via a client-visible ownership transfer; vanilla always relays their state through a targeted RPC
   to whoever currently owns them, so claiming one near a player changes *who relays it*, never what the
   player sees happen. `All` additionally allows the B1/B2 transfer-on-interact classes (`Container`,
   `ItemStand`, `ArmorStand`, `Vagon`, `Sadle`, `Turret`, `Sign`, `ItemDrop`) plus `Ship`, gated by its own
   `AllowShipClaim` (default off — O8 has no vanilla-client hand-back path for a server-held ship, see
   "Vanilla and version compatibility" above). The **effective** Mode used here is
   `ModeQuarantine.ForceSharedFar ? SharedFar : OwnershipAuthority.Mode` — a veto/exception burst auto-downgrades
   to the unchanged floor until an operator clears it ("saa:failbacks" builder's file this pass).

### Zone eligibility — `ZoneClaimSweep.ComputeCandidateZones` (widened, same file/owner as H1-H3)

At `SharedFar` (including the quarantined-down effective value), a peer-overlapped zone is disqualified from
candidacy exactly as it always was — H3's own guard, unchanged. At `FarOrShared`/`All`, a peer-overlapped zone
now becomes a candidate too, UNLESS `LoadShedder.IsZoneShed` says the frame-budget shedder has it shed for this
cycle ("saa:failbacks" builder's file this pass — see the safety-net spec for the shedder's own hysteresis/
priority design). This method only decides which zones get a real `ReleaseNearbyZDOS` sweep attempted at all;
H11/`NearFieldClaimPolicy` above is the actual per-object safety net once that sweep runs.

### Retroactive Pickable release (per claim cycle, `ZoneClaimSweep.RunClaimCycle` → `PickableRelease.RunReleasePass`)

Once per `ActiveInterval` tick (never riding the round-robin claim cadence, which is capped and can lag several
seconds behind a large held set — too slow against a 5 m interact range at running speed), hands any
already-server-held `Pickable` within `PickableReleaseRadius` of a live peer back (owner 0) through
`OwnershipApi.SetOwner`. Owned by "saa:compat-pickable" this pass; `ZoneClaimSweep.cs` (this file's own owner)
only adds the one call site, at the same place every other per-cycle housekeeping call in that method already
runs.

### Telemetry (O11)

- `ownership.o11.claims` — a near-field claim `IsClaimable` allowed (the NEW surface this feature adds; a
  far-band allow is not counted here, since that behaviour is unchanged from before this feature existed).
- `ownership.o11.vetoed.player` / `.pickableNearPlayer` / `.compatExcluded` / `.clientClaimGrace` /
  `.modeNotAllowed` / `.shipClaimDisabled` / `.prefabUnresolved` — one counter per refusal reason.
- `ownership.o11.playerZdoClaimAttempted` — **must stay 0** across a real session (see step 2 above); also
  fires its own Tier-1 rate-limited `NoodlesLog.WarningRateLimited` line the moment it's nonzero, not just a
  counter buried in the periodic flush.
- Every `ownership.o11.*` counter above also surfaces for free in Core's own periodic aggregated Tier-1
  telemetry line (`Telemetry.FlushAndLog`); each veto reason additionally gets a rate-limited Tier-2
  `NoodlesLog.DetailRateLimited` line naming the object (prefab name, position, ZDOID) — "tons of logging but
  not spammy" via the established rate-limit-then-aggregate idiom (CORE-API.md), not a new logging mechanism.
- A startup line (`NearFieldClaimPolicy.Init`) and a live-transition line (once per `RunClaimCycle` tick, cheap,
  only logs on a real change) both state the currently-EFFECTIVE Mode, including whether `ModeQuarantine` has
  it quarantined down from the configured value.
- `ownership.o11.pickableRetroactiveReleases` — owned by "saa:compat-pickable" (`PickableRelease.cs`'s own
  counter, not this section's file), a nonzero sustained rate proving the retroactive pass is doing real,
  continuous work rather than the claim-time veto having already prevented the claim in the first place.

### Configuration (O11)

Bound in `NearFieldClaimPolicy.cs` unless noted otherwise (new keys; existing `OwnershipAuthority.*` keys above
are unchanged):

| Key | Default | Range | Server-synced? |
|---|---|---|---|
| `OwnershipAuthority.Mode` | `FarOrShared` | enum: `SharedFar` \| `FarOrShared` \| `All` | server-authoritative |
| `OwnershipAuthority.AllowShipClaim` | `false` | bool | server-authoritative |
| `OwnershipAuthority.PickableReleaseRadius` | `12` m | 6–30 m | server-authoritative |
| `OwnershipAuthority.CompatClientClaimGraceSeconds` | `5.0` s | 3–10 s | server-authoritative (bound in `CompatClaimExclusions.cs`) |

### Acceptance for this work package

**Provable headless:** 0 connected peers → 0 near-field zones held (`ComputeCandidateZones` never sees a
peer-overlapped candidate with nobody connected to overlap it), 0 exceptions, `ownership.o11.claims` and every
`ownership.o11.vetoed.*` counter at 0 (nothing near-field to evaluate at all). **Cannot prove headless, needs
Proxima:** the real per-object exclusion behaviour (a live player's own avatar or a nearby `Pickable` actually
walking up to a near-field-claimed zone), the Animator-culling melee/AoE/projectile question `final.md` §9
flags as genuinely unknowable without a live hit, and `ownership.o11.playerZdoClaimAttempted` staying 0 across
a full real session.

## SAA WP-D/WP-E — failbacks

`final.md` §3/§7/§8's "robust error correction and failbacks" requirement for near-field claiming, implemented
in a later fixer pass (`FrameBudget.cs` new; `LoadShedder.cs`/`ModeQuarantine.cs` went from this doc's own
placeholder sketches to full implementations — see docs/impl/OWNERSHIP-IMPL.md's own dedicated section for the
file-by-file detail; this section states the design-level shape).

**WP-D — the frame-time load-shedding valve.** `FrameBudget.cs` derives a sticky "is the server currently over
its frame-time budget" signal from `Core/FrameTiming.RecentMeanMs` (the same smoothed signal Q13 uses),
applying its own two-threshold/two-dwell hysteresis — the same shape O9's `RttMarginMs`/`RttDwellSeconds`
established for RTT, but with a separate ENTER (`ShedEnterMs`, default 100 ms) and EXIT (`ShedExitMs`, default
60 ms) threshold, each requiring its own continuous dwell (`ShedEnterDwellSeconds`=4s /
`ShedExitDwellSeconds`=8s) before the state flips — specifically so a load-shedding valve doesn't flap right at
one boundary. `LoadShedder.cs` is the consumer: while over budget, once per `ZoneClaimSweep.ActiveInterval`, it
round-robins through currently-held zones that are ALSO near-field (peer-overlapped — a purely far-band held
zone was never near-field-claiming cost, so shedding it relieves nothing), marking up to
`MaxZonesShedPerCycle` (default 4) NEW zones "shed" per cycle. A shed zone is excluded from
`ZoneClaimSweep.ComputeCandidateZones`'s own candidacy; that file's existing release-diffing loop then releases
it on its very next tick — reused, not duplicated. Recovery clears every shed zone at once (re-claim pace
afterward still governed by O6's own `MaxZonesClaimedPerCycle`, so no unbounded-cost risk). The physical
liveness check that gates whether a candidate zone may actually be shed runs directly against
`Container.IsInUse()`/`Vagon.InUse()`/`Sadle.HaveValidUser()`/`Ship.m_players.Count>0` — deliberately NOT
through `ReclaimGate` (data-loss critic's Finding 3), since `ReclaimGate.AllowReclaim`'s safe default of
"refuse everything" whenever the relevant `Gameplay.HandoverGuard` sub-flag is off would make this safety VALVE
silently impossible in exactly that configuration; a zone that fails the check is skipped this cycle, not
shed, and retried next cycle.

**WP-E — the veto-burst auto-downgrade.** `ModeQuarantine.cs` gives O11 its own independent quarantine lever —
narrower than `Core.FeatureStandDown` (which would force off ALL of `OwnershipAuthority`, far-band included) —
backed by a real `FeatureFlag` named `ModeQuarantine` purely for its existing `ForceOff`/`IsForcedOff` latch,
so `noodles clear ModeQuarantine` already works with zero changes to the admin-command dispatcher. Two trip
signals, polled once per heartbeat frame, either one forces the *effective* `OwnershipAuthority.Mode` down to
`SharedFar` until an operator clears it: (1) any growth at all in `ownership.o11.playerZdoClaimAttempted` — no
window, no threshold, since this counter must stay 0 across a real session by construction; (2) more than
`VetoBurstLimit` (default 50) `ownership.setOwnerVetoed` events within `VetoBurstWindowSeconds` (default 60s)
— a rolling window mirroring `Core.FeatureStandDown`'s own queue-of-timestamps shape. Signal 2's counter is
shared with O9/O10's own `AuthorizedSetOwner`-mediated writes, so a burst driven purely by O9/O10 activity can
still trip it — accepted deliberately, the more conservative reading of "robust error correction."

**Telemetry**: `ownership.loadshed.entered`/`.exited` (state transitions), `.shedZones` (gauge),
`.zonesShedTotal`/`.zonesRestoredTotal` (lifetime counters), `.skippedBusy`; `ownership.modeQuarantine.tripped`
(lifetime counter), `.vetoBurstWindowCount` (gauge).

**Configuration**: `OwnershipAuthority.LoadShed.ShedEnterMs` (100, range 20–500ms, local),
`.ShedExitMs` (60, range 20–500ms, local), `.ShedEnterDwellSeconds` (4, range 1–30s, local),
`.ShedExitDwellSeconds` (8, range 1–30s, local), `.MaxZonesShedPerCycle` (4, range 1–16, local);
`OwnershipAuthority.ModeQuarantine.Active` (true, local — DEFAULT ON), `.VetoBurstLimit` (50, range 5–5000,
local), `.VetoBurstWindowSeconds` (60, range 10–300s, local). None of these are server-synced in the
`ConfigSyncRpc.RegisterSyncedValue` sense — every one is pure server-internal failback tuning with no
client-visible effect to carry over the wire, the same category `Core.AutoStandDown.*` already occupies.

## Red-team reconciliation

Both red teams' findings were independently re-verified against the decompiles this pass (not
taken on trust — every line citation below was re-grepped, not just re-read from the red team's
own text). Disposition legend: **fixed** = blueprint edited in place, marked `**RED-TEAM FIX**`
at the edit site; **deferred** = real finding, out of scope for this pass, tracked as an Open
question; **rejected** = re-verified and found not to hold, with evidence.

### RED TEAM 1 (data loss)

| # | Severity | Issue | Disposition | Evidence / reasoning |
|---|---|---|---|---|
| 1 | major | `OwnershipHeldZones` not synced with vanilla's `ZoneSystem.UpdateTTL` zone eviction — objects could become permanently un-claimable. | **Fixed.** H1 and H2 now specify `OwnershipHeldZones` as fully re-derived (not additively grown) from `CurrentClaimTargets()` every `ReleaseZDOS` tick; `CurrentClaimTargets()` is already `m_zones.ContainsKey`-gated live, so a TTL-evicted zone drops out within one tick with no new hook. `ownership.staleHeldZoneEvictions` added. Data-loss-analysis table and Test plan item 6 updated. | Re-confirmed this pass: `ZoneSystem.UpdateTTL` at SERVER:115548-115561 (`m_zones.Remove` at 115559), `m_zoneTTL=4f` at SERVER:113087, `HaveInstanceInSector` at SERVER:82368, called every 0.1s via `UpdateTTL(0.1f)` at SERVER:113806. `m_zones` confirmed `private readonly` at SERVER:113154 (needs a cached FieldRef, noted in the fix). RT1's citations all checked out exactly. |
| 2 | major | Ships have no vanilla request-grant RPC (unlike Container/Vagon/Sadle); a vanilla client boarding a server-held ship can never become its owner. `Ship.UpdateOwner` can never fire server-side. | **Fixed as an explicit, documented limitation**, not a code fix (option (a) from RT1). "Vanilla and version compatibility" now states this plainly instead of implying full vanilla compatibility; building a real vanilla-reachable hand-back RPC is deferred to Open question 6 (a genuine scoping decision, not a blocker to fix silently). | Re-confirmed this pass: `Ship`'s only registered RPCs are `Stop`/`Forward`/`Backward`/`Rudder` (grep of SERVER:140181-141062, no `RPC_Request*`). `Ship.UpdateOwner` guard at SERVER:140858-140867 requires `Player.m_localPlayer != null`; confirmed `m_localPlayer` is only ever set via `Player.SetLocalPlayer()` (SERVER:10121-10126), called once from a spawn path (SERVER:100143) that requires a locally-driven `Player` instance a headless dedicated server never creates. `Vagon.RPC_RequestOwn` (SERVER:147455-147461) and `Sadle.RPC_RequestControl` (SERVER:18229-18243) both confirmed to work correctly when the server is current owner (`if (m_nview.IsOwner()) { ...; SetOwner(sender); }`), unlike Ship. RT1's citations all checked out exactly. |
| 3 | major | H9's claim that O9 needs "no new hook beyond H2's postfix" is false: `ReleaseNearbyZDOS`'s `m_tempNearObjects` is a single shared, private field cleared and overwritten on every call, gone by the time any postfix on the outer `ReleaseZDOS` runs. O9's core discovery mechanism had no working implementation path as specified. | **Fixed.** New hook H10 (Harmony postfix directly on `ZDOMan.ReleaseNearbyZDOS`) captures each peer pass's near-object set immediately, before the next call overwrites it, into a per-tick accumulator O9's own `ReleaseZDOS` postfix (H9) consumes after the sweep completes. This is RT1's suggested option (a) (a genuine new hook), not option (b) (an independent re-scan) — avoids any risk of the reimplementation diverging from vanilla's own active-area semantics. | Re-confirmed this pass, exact match to RT1's citations: `m_tempNearObjects` declared `private readonly List<ZDO>` at SERVER:76140; `.Clear()` at SERVER:76952 inside `ReleaseNearbyZDOS` (SERVER:76948-76971); the self-pass + per-peer loop at SERVER:76919-76930 (`ReleaseZDOS`) all run before H2's postfix (which only fires after `ReleaseZDOS` returns) ever gets control. RT1's technical claim is correct as a matter of code fact, not just style. |
| 4 | major | Every uid-keyed mechanism in this doc (H1, H4, H7, H9) assumes one live connection per uid; `ZDOMan.AddPeer` has no duplicate check; G1-L1 (the fix, already in the user's 55-lever selection) was never listed as a prerequisite of this doc. | **Fixed.** G1-L1 added to the levers table as an explicit mandatory prerequisite for the whole doc, treated the same way G3-N1 is treated for O6. No new lever invented — this was purely a missing cross-reference to a lever the user had already selected. | Re-confirmed this pass: `ZDOMan.AddPeer` (SERVER:76814-76820) does `m_peers.Add(zDOPeer)` unconditionally, no existing-uid check. Matches the lead's independently-verified Fact 5 and RT1's citation exactly. |
| 5 | major | O8's ship hand-back writes no grace-window timestamp before/after `AuthorizedSetOwner`, unlike the Container-race guard this doc otherwise treats as settled; O9 could theoretically reassign a freshly-granted ship instantly. | **Fixed**, via RT1's own suggested alternative (state the actual guard rather than add a redundant timestamp map). `ShipHasOccupant` is now defined explicitly against `Ship.m_players` — the same state O9's own Ship exclusion (`m_players.Count > 0`) reads — so a grant can only succeed when the boarding player is already registered aboard, meaning O9's exclusion is already active before H8's grant completes. No separate grace-window write needed. | `Ship.m_players` confirmed `private readonly List<Player>` at SERVER:140293, populated via `OnTriggerEnter`/`OnTriggerExit` at SERVER:140898-140924. `ShipHasOccupant` was previously an unspecified placeholder in H8's sketch — now pinned to this field. |
| 6 | minor | Kill-switch flush assumed implicitly synchronous with the main-thread `ReleaseZDOS` postfix; BepInEx `ConfigFile`'s file-watcher-driven `SettingChanged` can fire off-thread. | **Fixed, precautionary.** Data-loss-analysis table's "Kill switch disabled mid-handover" row now specifies marshaling the flush onto the main thread. Flagged as outside this doc's mandated ground truth (BepInEx internals, not the Valheim decompiles) rather than cited by line — the fix is free regardless of whether the race is real, so applied anyway. | Not independently verifiable against the Valheim decompiles (correctly scoped as such by RT1 itself); accepted as a low-cost defensive fix rather than requiring proof. |
| 7 | minor | H5's sketch left a dangling "see note" for its peek-clamp implementation without confirming `SimulationDistance`'s wire format is fixed-width. | **Fixed.** Concrete citation added; implementation changed from an in-place byte rewrite to a full re-serialize-into-a-new-`ZPackage`-and-swap, which makes no assumption about field widths at all (safer than RT1's own "confirm fixed-width" alternative). | Re-confirmed this pass: `SimulationDistance.Serialize`/`Deserialize` at SERVER:117807-117821, fixed int/int/bool layout, exact match to RT1's citation. |
| 8 | minor | H7's `[ThreadStatic] bool AuthorizedCallInFlight` bypass is unscoped — a reentrant `SetOwner` on a *different* ZDO during the same call stack would also bypass the hold guard. | **Fixed.** Bypass flag changed to `[ThreadStatic] static ZDOID? AuthorizedZDOID`, compared against the specific ZDO being transferred. | No confirmed reentrant call chain found (`ZDO.SetOwner`/`SetOwnerInternal`/`IncreaseOwnerRevision` at SERVER:74674-74683 show none) — RT1 itself flagged this as unconfirmed-but-cheap-to-fix; fixed regardless since the cost is zero and ZERO DATA LOSS is an absolute project rule. |

### RED TEAM 2 (runtime/compat)

| # | Severity | Issue | Disposition | Evidence / reasoning |
|---|---|---|---|---|
| 1 | blocker | Milestone table claims O9 "ships standalone before Phase B core," but O9's own Hooks section says its `SetOwner` call and discovery both run inside Phase-B-core infrastructure (H2's postfix = O6; H7's hold set = O7) — not actually buildable in the stated order. | **Fixed** (option (b) from RT2: give O9 genuinely independent infra, not option (a) dropping the standalone claim — chosen because the task's own framing calls O9 "a candidate interim fix... before full server-held zones," a real product goal that dropping standalone-ness would defeat). O7 (H7) and the new H10 reclassified from "Phase B core" to a new **Shared groundwork** milestone that ships with O9-interim; O9 gets its own independent `ReleaseZDOS` postfix (separate Harmony patch class from O6's, gated on `Features.O9.Active` alone). Neither `ZDO.SetOwner` (H7) nor `ReleaseNearbyZDOS` (H10) has any functional dependency on G3-L1/O6/O3/O4/G3-N1/G4-N3/O8 — both run in unmodified vanilla regardless. Milestone table, levers table, O9 adjudication point 6, H9/H10, and Interactions section all updated consistently. New Test plan item 7 verifies O9 works with `OwnershipAuthority.Active=false`. | Confirmed by tracing the actual call graph (see RT1 major #3's evidence for H10's necessity) — this was a real, buildable-order blocker, not just an editorial inconsistency. |
| 2 | major | Third-party skip-capable `ZDO.SetOwner` prefixes (confirmed: `GetOffMyLawn`, `Wonderland`, both the user's own mods) can silently no-op any transfer in this doc; the only mitigation was a passive startup log, not verification. | **Fixed.** `AuthorizedSetOwner` now verifies `zdo.GetOwner() == newUid` after every call and increments `ownership.setOwnerVetoed` on mismatch; H7's guard section adds explicit Harmony priority (`Priority.First`) and documents the resulting order relative to the two sibling mods, instead of relying on load-order-dependent defaults. New Test plan item 9. | Confirmed via `research/13-mod-compat-census.md` §1 and its summary table row (`ZDO.SetOwner \| Prefix / yes \| Wubarrk-GetOffMyLawn[SERVER], Wubarrk-Wonderland[SERVER] \| HIGH`) — the census exists and names both mods exactly as RT2 described; not hypothetical. |
| 3 | major | H8's new RPC and the shared handshake both route through method families (`ZRoutedRpc`, `ZRpc.HandlePackage`) the doc's own "Other mods" section claimed nothing touches — directly contradicted by the compat census. | **Fixed.** "Other mods" section rewritten to state the real collision surface by name (AzuAntiCheat's `RoutedRpcGuards`, Wonderland's `ModEnforcementPatches`/`Patch_ZRpc_HandlePackage`, three RavenIronStudios mods, Zen_ModLib's spam-dedup) instead of the contradicted claim. H8 gets a request-timeout/retry (`ownership.o8.requestTimedOut`) so a silently-dropped RPC is detected instead of failing invisibly. New Test plan item 13 tests against the live mod stack specifically. | Confirmed `ZNetView.InvokeRPC` (SERVER:82896-82903) unconditionally routes through `ZRoutedRpc.instance.InvokeRoutedRPC` — exact match to RT2's citation. Confirmed via `research/13-mod-compat-census.md` §§2-3 that the named mods and methods are real, already-installed patches, not a hypothetical risk — the census's own summary table independently corroborates every mod name RT2 cited. |
| 4 | major | H2's O6 claim-pass postfix has no cap on how many zones it re-sweeps per tick; cost scales with held-zone count (unbounded), not peer count, and compounds with O1's raised ceiling and O3's shortened cadence. | **Fixed.** `Config.MaxZonesClaimedPerCycle` (default 8) caps the per-tick sweep count; `OwnershipZonePolicy.NextClaimBatch` round-robins the remainder across subsequent ticks. `ownership.zonesProcessedPerCycle` (+ wall-clock cost) added. New Test plan item 8. | Re-confirmed this pass: no `cap`/`throttle`/`per cycle` mechanism existed anywhere in the original doc for zone count (grepped the full original text); `ReleaseNearbyZDOS`'s `FindSectorObjects` cost per call confirmed at SERVER:76948-76958 — RT2's claim checked out exactly. |
| 5 | minor | H4's ping/pong sketch discriminator `package.GetPos() == 0 && !PeekBool(package)` is dead code — `GetPos()` is never 0 at that point because `HandlePackage` already consumed 4 bytes via `ReadInt()` before dispatching to `ReceivePing`. | **Fixed.** Dead `GetPos() == 0` check removed; discriminator is now just `!PeekBool(package)`, since `HandlePackage`'s call site already guarantees this is the id-0 handler. Open question 5 updated to note the bug is fixed, not just an implementation choice. | Re-confirmed this pass, exact match to RT2's citations: `HandlePackage` (SERVER:83952-83958) calls `ReadInt()` (SERVER:83339-83341, consumes 4 bytes) before `if (num == 0) ReceivePing(package)`; `GetPos()` (SERVER:83476-83478) returns the stream position, which is 4, not 0, by the time `ReceivePing` runs. |
| 6 | minor | H6's `2f` timer literal gates both the server's own broadcast branch and the client's reporting branch (evaluated before the `IsServer()` split) — framing it as confined to "the reporting branch" risks a process-global static altering the server's own cadence when this DLL loads server-side. | **Fixed.** H6 now states explicitly that the config field backing the transpiled literal must be read as `!IsServer() ? Config.Value : 2f` (or gated at the point of application), never a bare process-global static. | Re-confirmed this pass, exact match to RT2's citation: `SendPeriodicData` (SERVER:80383-80406), the `m_periodicSendTimer >= 2f` gate (SERVER:80386-80388) precedes the `IsServer()` check (SERVER:80389). |
| 7 | minor | H6 doesn't mention `research/10-LEVERS.md`'s O4 guards (2)/(3) — a server-side plausibility check and its mandatory teleport-exemption reset — an omission a later implementer could rediscover the hard way. | **Deferred, documented rather than implemented.** H6 now states explicitly that this pass deliberately implements only guard (1) and does not add the plausibility check, so guard (3)'s teleport hazard is currently moot; added as Open question 7 so a future revision that does add the check finds the teleport-exemption requirement first instead of rediscovering it against a live, portal-heavy server. | Confirmed via `research/10-LEVERS.md` lines ~328-333 — the guards RT2 cites exist exactly as described and are absent from the original H6 text. |

**Net effect on scope:** two hooks reclassified (O7/H7 and the new H10 move to a "Shared
groundwork" milestone shared by O9-interim and Phase B core, resolving RT2's blocker without
weakening either milestone); one new hook added (H10); one new lever cross-reference added
(G1-L1, already user-selected, not a new ask); five new config/telemetry surfaces added
(`MaxZonesClaimedPerCycle`, `staleHeldZoneEvictions`, `zonesProcessedPerCycle`,
`setOwnerVetoed`, `o8.requestTimedOut`); one explicit, permanent vanilla-compatibility
limitation documented (server-held ships need the mod client-side); zero items rejected — every
issue from both red teams re-verified as a real, evidenced finding against the decompiles, and
every blocker/major either fixed in place or explicitly deferred with a named reason, per the
brief's own instruction that blockers must be fixed or removed from their milestone (none were
removed — all were fixed).
