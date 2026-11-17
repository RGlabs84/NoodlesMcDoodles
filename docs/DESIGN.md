# NoodlesMcDoodles design

Integrator's document. Reconciles the six per-subsystem blueprints in `docs/design/01..06-*.md` (each already
carries its own Hooks / Data-loss analysis / Test plan / Red-team reconciliation section — this document points
into them rather than repeating them) plus the mod-compatibility census, `research/13-mod-compat-census.md`, into
one build order. Ground truth for every citation below is
`libs-Tools/1.0/DECOMPILED/assembly_valheim.decompiled.cs` (CLIENT) and `assembly_valheim_SERVER.decompiled.cs`
(SERVER), Valheim 1.0.16 / network version 40.

## What it does and how it fixes the Arizona problem

Two things, one plugin:

1. **The dedicated server can own and simulate zones itself**, instead of ownership defaulting to whichever
   player happened to arrive first and staying there for as long as that player remains (vanilla's actual rule —
   `ReleaseNearbyZDOS` hands a server-owned object to any peer in range because the server's own reference
   position is permanently pinned off-map; see Hook table, `IsInPeerActiveArea`). The admin plays from Arizona
   against a server in New Jersey; under vanilla rules his upload and his latency become everyone else's lag the
   moment he's the first one in a zone, for as long as he stays. Server-held zones remove that dependency —
   the server simulates and sends, every player's experience depends on the server's own (good, wired, 300 Mbps)
   connection instead of on whoever got there first.
2. **Send queues and bitrates are raised and tuned** so the transport layer isn't the next bottleneck once the
   server is doing more of the sending — bigger per-peer budgets, more peers serviced per tick, a higher global
   rate ceiling, faster ownership hand-over cadence.

Because full server-held zones is the larger, riskier change, **O9** (latency-aware ownership: when several
players share an area, hand the objects there to whichever one has the lowest measured RTT, using vanilla's own
ping/pong) ships first as a smaller, reversible interim step that directly attacks the Arizona case without
requiring the server to simulate anything itself. It is not yet adjudicated by the user — see Decisions.

Every behaviour is opt-in behind a runtime flag and reverts to exact vanilla behaviour with no restart if
switched off — see Architecture. Nothing here preloads or prefetches world areas to clients; a client only ever
receives what a peer legitimately in range would receive under vanilla's own rules, just from a different owner.

## Architecture

- **One DLL**, plugin GUID `wubarrk.noodlesmcdoodles`, namespace `NoodlesMcDoodles`. BepInEx 5 + Harmony only —
  no Jotunn, no ServerSync, no other runtime dependency. Runs on server and client; a vanilla client must keep
  working against a modded server and a modded client must keep working against a vanilla server (every
  handshake-gated feature below fails safe to "peer is vanilla" when the handshake doesn't complete).
- **Client vs. server game assemblies differ** (client build 25527674, server build 25527701). Every hook is
  checked, and cited, against both decompiles; a handful (P1/P2 autosave/backup, the ownership sweep, zone
  simulation) are server-only because the corresponding vanilla code path is dead on a client (`IsDedicated()`/
  `IsServer()` gates), not because the patch is skipped by build.
- **Source layout**: `src/<Module>/`, one folder per module below, plus `src/Core/` for the shared infrastructure
  (Features, kill switch, telemetry, negotiation, config).

  | Module (dir) | Blueprint | Levers |
  |---|---|---|
  | `Transport` | `docs/design/01-transport-and-queues.md` | T1–T5, T8, Q2–Q8, Q10, Q12, G2-N1, G6-N4 |
  | `Rails` (→ `Core`) | `docs/design/02-safety-rails-and-ops.md` | G1-L1, G1-N1, P1–P4, P5 (no-patch confirmation), P6, P7, P8 (test matrix, not a code lever) |
  | `Ownership` | `docs/design/03-ownership-and-handover.md` | G3-L1, G3-N1, G4-N3, O1, O3, O4, O6, O7 (= `G6-N3`), O8, O9; `G2-N2` cross-referenced here (bandwidth accounting for O3's cadence — same underlying literal, no hook of its own) but owned by `01` |
  | `ZoneSim` | `docs/design/04-server-zone-simulation.md` | Z2, Z3, Z4, Z5, Z6, Z7, Z8/G6-N2, Z9, Z10 (no hook — feasibility confirmation only) |
  | `Gameplay` | `docs/design/05-gameplay-under-server-authority.md` | S1–S8 |
  | `Smoothing` | `docs/design/06-client-smoothing-and-compression.md` | C1, C2 (deferred), C3, C5, D1, D2 |

  **CRITIC FIX:** `P5`, `P8` and `G2-N2` — three of the user's 55 selected levers — were entirely absent from this
  document (not in this table, no hook-table row, not in any milestone's Ships list, not in Deferred/rejected).
  Added above and to the Hook table / Milestones below (as `Kind: none` rows, since `P5` is a confirmed
  no-code-needed safe non-change and `P8` is a live test matrix, not a patch — neither is an actual Harmony patch,
  but both are real obligations that needed a place in the schedule). `Q12` (real M1 code, present in
  the Hook table, Ships list and Config reference all along) was likewise missing from this summary line only.
  `G2-N2` is the same vanilla literal `O3` already tunes (`ReleaseZDOS`'s cadence), viewed as a bandwidth cost
  rather than a handover-latency one — not a second hook, but its own NEEDS-LIVE-TEST obligation (see M3 exit
  criteria fix below). The `Q2–Q10` range wrongly implied `Q9` (ZRoutedRpc backpressure) shipped in M1; `Q9` is
  real (`research/10-LEVERS.md`) but not one of the user's 55 selections and is not scheduled anywhere in this
  document — corrected to `Q2–Q8, Q10`.

- **Shared infrastructure contract** (defined in `docs/design/02`'s "rails" blueprint; every module above
  depends on it):
  - `NoodlesMcDoodles.Core.Features.<Name>.Active` — one flag per behaviour. `Core.Enabled` is the global kill
    switch (config + the `noodles` console command: `status | enable | disable`, `remoteCommand:true` +
    `onlyServer:true`, authorized through vanilla's own `RPC_RemoteCommand` admin-list check, SERVER:81925-81934
    — **not** `onlyAdmin`, which vanilla never reads). Switching off returns the game to exactly vanilla
    behaviour at runtime, no restart: prefixes return true, postfixes no-op, transpilers read a static field that
    holds the vanilla literal when disabled. `Core.AutoStandDown` can flip `Core.Enabled` off itself on: (1) a
    sustained ZDO created/destroyed mismatch beyond tolerance across two telemetry periods, (2) two consecutive
    failed disk-space checks during a forced save, (3) an exception burst (>5/60s) from this mod's own patches,
    and always flushes any ownership hold-set before returning.
  - **Handshake / negotiation**: a namespaced RPC (`NoodlesMcDoodles.v1.Hello`) sent from a postfix on
    `ZNet.RPC_PeerInfo` right after vanilla's own success path, idempotent per peer (`PeerState.HelloSent`).
    Vanilla ignores unknown RPC hashes, so an unmodded peer on either end is a silent no-op; features that need
    both ends cooperating (O4, O8, O9, D1, and the config-sync push) activate per peer only after negotiation
    completes. A capability bitmask is reserved for future use; only bit 0 (`ConfigSync`) is assigned today.
  - **Config sync**: `NoodlesMcDoodles.v1.ConfigSync`, registered unconditionally at `ZNet.Awake` (so a live
    off→on flag flip doesn't need a reconnect), pushed by the server to negotiated peers with that capability bit
    set, and re-pushed every `ConfigSync.PushIntervalSeconds`. No values need to reach a client in M1 (every M1
    lever is server-local); the channel is proven working with a near-empty payload now so later phases have a
    tested channel on day one.
  - **Telemetry**: `NoodlesMcDoodles.Core.Telemetry` counters (per-peer where relevant), one `LogInfo` line per
    `Telemetry.LogIntervalSeconds` formatted `[NoodlesMcDoodles] telemetry key=value ...`, grep-able from
    `LogOutput.log`/`valheim_server.log`. See Telemetry section.
  - Ownership-holding features must register a flush callback with the kill switch so `Core.Enabled=false`
    always hands objects back safely, never leaves them in limbo.

## Lead decisions (after the critic, 2026-09-28) — these override anything below that disagrees

1. **`ServerZoneMembership` is Core infrastructure, shipped in M1 as an always-empty registry** (`src/Core/ServerZoneMembership.cs`).
   Q6 (M1) reads it; the Ownership module (M3) is its only writer. This resolves the critic's build-order finding: M1 is now
   buildable and runnable on its own, and the lookup is O(1)-false until `OwnershipAuthority.Active` turns on in M3.
2. **Q2 must stand aside when another mod already replaces `ZDOMan.SendZDOToPeers2`.** The census entry is wrong on one point:
   Zen_ModLib's `OptimizeNetworkZDOManSendToPeers2.ZDOMan_SendZDOToPeers2` is a `void` prefix but takes `ref bool __runOriginal`
   and sets it `false`, so it **does** skip the original. When its config `Network - Dedicated Server - SendToPeers Sync` is true
   (it is **true on Proxima**, and it only acts on a dedicated server) it replaces the method with "every 0.05 s, `SendZDOs` to
   every peer". That is already a stronger form of Q2 than our ceiling of 4, so on Proxima our Q2 would be a regression and a
   second full replacement would double-run the send loop. Rule: at patch time and again on each config change, Q2 inspects
   `Harmony.GetPatchInfo(SendZDOToPeers2)`; if any *other* owner has a prefix on it, Q2 does not patch (or its prefix returns
   `true` immediately), logs `transport.q2.yieldedTo=<owner>`, and counts it in telemetry. Q3/Q4/Q5/Q12/G2-N1 (the `SendZDOs`
   transpiler) are unaffected: Zen's loop calls the same `SendZDOs`. The M1 exit criterion "Q2 vs Zen_ModLib verified live" becomes
   "Q2 yields to Zen_ModLib on Proxima, verified in the log".
3. **Negotiation is server-initiated.** A client never sends a NoodlesMcDoodles RPC to a server until it has received the server's
   `NoodlesMcDoodles.v1.Hello`. Reason: `Wubarrk-Wonderland`'s `ModEnforcementPatches.Patch_ZRpc_HandlePackage` prefix, when
   `ModEnforcementEnabled=true`, kicks any non-admin client that sends a ZRpc hash outside its 26-name vanilla allowlist
   (`ModEnforcement.InitVanillaZRpcWhitelist`). On Proxima it is **disabled** (`ModEnforcementEnabled = false`,
   `ModEnforcementAdminBypass = true`, `wubarrk.wonderland.cfg` lines 209/214), so M1 negotiation is not blocked there. Operational
   rule: before enabling Wonderland's ModEnforcement on any server that runs NoodlesMcDoodles with modded clients, add the
   NoodlesMcDoodles RPC names to Wonderland's allowlist (the user's own mod) or modded non-admin clients will be kicked.
   Wonderland's prefix only reads the method hash and never touches server-originated RPCs, so server→client traffic is unaffected.

4. **AutoStandDown trigger #1 (ZDO created/destroyed drift) is opt-in, off by default** (`Core.AutoStandDown.ZdoDeltaTrigger = false`).
   Created-minus-destroyed is not a loss signal on a live server: exploration makes the owning client generate hundreds of ZDOs
   per zone and chopping/mining/combat destroy them in bulk, so any small per-minute tolerance trips in normal play and switches
   the mod off. M1 cannot create or destroy ZDOs, so the trigger could only fire falsely. Counters stay in telemetry. M3 must
   define a real loss signal (for example: a persistent ZDO destroyed on the server without a gameplay destroy request, or an
   OwnerRevision/DataRevision regression on a server-held ZDO) before any ownership feature ships.
5. **T9 (adaptive per-connection rate) joins M1, on by default; T1 becomes its ceiling and T2 its join ceiling** (user's design
   intent, 2026-09-28: "help the person in Arizona and everyone else on the server and unlock networking options that will
   improve gameplay for everyone"). In this Steam networking build the send rate is a fixed rate with no congestion control
   (`libs-Tools/VALHEIM-1.0.15-STEAM-SEND-RATE-FACTS.md` §3, measured), so pinning every connection at 1 MiB/s (4 MiB/s while
   joining) floods any player whose line is slower than that. `src/Transport/AdaptiveRate.cs`: every Steam connection starts
   at `T9_FloorBps` (vanilla 153600, never lower) and rises (x1.5 per clean second while joining, x1.25 per two clean seconds
   after) only while data is waiting AND Steam reports >= 99% delivery to that player AND their ping is within a small margin
   of its 2-minute minimum; it backs off x0.7 on loss (< 0.98 twice, < 0.95 at once) or ping growth (> max(30 ms, half the
   minimum) twice), then holds 5 s. A server-wide total on measured sending (`T9_ServerTotalBps`, 10 MiB/s; see decisions 6 and 7) caps raises. With T9 on, the global
   Steam rate stays at vanilla (connections T9 has not reached yet inherit it). Server-only; clients with Q10 run it on their own
   upload. Offline check with the real rule (`tools/t9-sim`): fast line 116 MB vs 132 MB fixed, same ping; 4 Mbit line 6 loss-
   seconds and 55 ms average ping vs 30 loss-seconds and 71 ms for the fixed rates; idle and unknown-quality connections stay at
   vanilla. Live test: watch `transport_t9_rate_bps_peer*`, `transport_t9_ping_ms_peer*`, `transport_t9_raised/lowered_peer*`.
6. **Defaults: "a great improvement over vanilla but not overkill" (user, 2026-09-28).** The generated config is what most
   servers will run, so each default sits at the knee of its curve (most of the gain for the least load, measured where
   possible), never at the largest value that is still safe; the safe maximum goes in the key's description as opt-in
   headroom. Every module IMPL doc carries a Defaults table (key | vanilla | default | safe max | why). Applied to M1:
   (a) `T2_JoinBurstBps` 4 -> **2 MiB/s** (`tools/t9-sim --knee`: 10 MB join 9.5 s vs 8.7 s at 4 MiB/s, vanilla 67.5 s;
   3-4 MiB/s inflated ping on a 20 Mbit line for no gain); (b) **Q3's per-call send budget now follows T9's proven rate per
   connection** (`src/Transport/PeerBudget.cs`: rate x measured gap between that peer's SendZDOs calls x 1.3, clamped to
   [10240, `Q3_InnerBudgetBytes`], ceiling default 64 KiB): the fixed 32 KiB kept ~190 ms of data queued ahead of everything
   else on a connection at vanilla's rate (vanilla ~43 ms), so the weakest lines got more lag, not less; now they keep
   vanilla's budget and fast lines get up to 1 MiB/s of supply; (c) `T9_ServerTotalBps` 20 -> **8 MiB/s**, counted on
   measured sending instead of granted rates (T9 never lowers an idle connection, so summed grants would let players who
   loaded earlier block new ones). After the first live NJ-AZ test (2026-09-28) the user made Q10 client upload and
   ClientSmoothing.ShortExtrapolation default on (0.1.1). Still to decide: O9 and server-held zones (the latter needs
   the geometry redesign, see docs/impl/INTEGRATION-REVIEW.md).
7. **Per-player rate sized to the shared server line (user, 2026-09-28 ~20:30).** TheWall has 300 Mbit/s up / 2000 down, shared by every game world on it. The user: "default to 256 or 192 for now with the idea of 40ish players between all the game worlds". `T1_GlobalRateBps` 1 MiB/s -> **256 KiB/s** (the ceiling T9 may raise a connection to, server and Q10 client upload alike) and `T9_ServerTotalBps` 8 -> **10 MiB/s** (40 x 256 KiB/s, ~84 Mbit/s, ~28% of the line). 256 over 192: the user already hit ~165 KB/s on vanilla's 10 KB queue, so 192 would leave little headroom for a battle. `T2_JoinBurstBps` stays 2 MiB/s (the first 30 s after a join only). Context: vanilla's 10 KB send gate counts sent-but-unacknowledged bytes (`ZSteamSocket.GetSendQueueSize` adds `m_cbSentUnackedReliable`), so it caps a connection at ~10 KB per round trip: ~165 KB/s at LAN ping, far less for a player 110 ms away. Raising the rate alone does nothing without a matching queue; the round-trip-aware queue (scratchpad `queue-unlock/`, in progress) is the follow-up.

8. **Unlock the send queue by the round trip (user, 2026-09-28 ~20:20-20:45).** "unlock the queue, even a small bump can make all the difference"; the far player "would need more to stabilize and get the same throughput as others". Vanilla's 10 KB SendZDOs gate counts sent-but-unacknowledged bytes, so it is a 10 KB window per round trip (measured cap ~165 KB/s at 1 ms; queue-unlock sim at 256 KiB/s: 84% of the rate at 1 ms, 20% at 160 ms; the old server budget 100% -> 34%). Q13 (`src/Transport/QueueWindow.cs`) adds `min(unacked, rate x baseline RTT x Q13_RttSlack, cap)` to Q3's budget: the waiting part (what small RPCs queue behind) stays one send tick at the proven rate; only bytes already in flight are discounted. Given only on T9-managed Steam links with a known minimum ping, never on PlayFab, stalls, a flush or an inflated ping, and withheld while the server's mean frame time is above `Q13_FrameLoadMs`. Applies to a modded client's own upload too (`Q13_ClientCeilingBytes` 48 KiB in total), replacing the pin at 10240 set after 17:02. Rejected: flat bumps (16/24/32 KiB) tripled RPC wait for near players with no gain for them. Known cost: after a link blip Steam re-sends up to one allowance before new data (catch-up delay, not lost state). The sim's far-player numbers are not yet measured live. Also fixed: the send_budget_near_miss counter now uses the live budget instead of a hardcoded 10240. Analysis: scratchpad `queue-unlock/`.

## Hook table

One row per patch. `Kind` follows Harmony terms (prefix / postfix / transpiler / new RPC / component = no vanilla
patch). Citations are `CLIENT:<line>` / `SERVER:<line>`, taken from each module's own re-verified blueprint;
`(ref)` marks a citation given for context in the blueprint but not itself patched. Milestone numbers are this
document's unified schedule — see Milestones for what each one means and why levers land where they do.

### Transport (`docs/design/01`)

| Method | Kind | Side | Lever(s) | Milestone | CLIENT | SERVER |
|---|---|---|---|---|---|---|
| `ZNet.Start` | postfix | both | T1, T3 | M1 | 79448-79451 | 79301-79304; `IsServer` 81184-81186 |
| `ZNet.Start` (same postfix, extra lines) | postfix | both | T4 (init) | M1 | — | fields 83859/83861 |
| `ZNet.RPC_PeerInfo` | postfix | both | T2 | M1 | 80002 | 79821; 79989-79990; `AddPeer` 76814-76820 |
| `ZPlayFabSocket.OnNetworkJoined` | postfix | both | T4 | M1 | 91840-91847 | 91357-91365 |
| `ZPlayFabSocket.Accept` | postfix | both | T4 | M1 | 92200-92208 | 91712-91719 |
| `ZSocket2.ConfigureSocket` | postfix | both | T5 | M1 | 84940-84944 | 84710-84714 |
| `ZNet.GetNetStats` | postfix | server | T8 | M1 | 81759-81799 (ref) | 81567-81574 |
| `ZSteamSocket.GetConnectionQuality` | prefix | server | T8 | M1 | — | 86673 (sig); 86687 (bug: wrong Steam API class) |
| `ZDOMan.SendZDOs` | transpiler | both | Q3, Q4, Q5, Q12, G2-N1 | M1 | 77197-77256 | 77055-77118 (literals 77058/77062/77063; invalid-sector 77075-77083) |
| `ZDOMan.SendZDOToPeers2` | prefix, full replace | server (no-op on client) | Q2 | M1 | 77026-77049 | 76883-76909 |
| `ZDOMan.ServerSortSendZDOS` | prefix, full replace | server | Q6 | M1 | 77500 (sig) | 77358-77378; 76150; 77335 |
| `ZDOMan.SendDestroyed` | prefix, full replace | both | Q7 | M1 | 77126 (area) | 76984-76997 |
| `ZDOMan.ForceSendZDO` (×2 overloads) | none — library wrapper | both | Q8 | M1 | 77895; 77903 (offset, same body) | 77753; 77761 |
| Hook 6/7's own transpiler+prefix, applied to the client build | transpiler + prefix | client, opt-in | Q10 | M1 | 77197-77256; 77026-77049 | n/a |
| `ZDO.Set(string,byte[])` | none — library guard (`SafeZdoWrite.TrySet`) | both | G6-N4 | M1 | n/a | n/a |

**CRITIC FIX:** the `ZSteamSocket.GetConnectionQuality` row above previously cited `SERVER:86620; 86648` — those
are `GetSendQueueSize`'s and `GetCurrentSendRate`'s own line numbers (cited correctly by `docs/design/01` line 429
only as *sibling methods using the correct Steam API class for comparison*), not `GetConnectionQuality`'s. Verified
directly against the decompile this pass and corrected to the method's actual location; matches
`research/10-LEVERS.md`'s independently-cited `SERVER:86635-86696` region.

### Rails (`docs/design/02`)

| Method | Kind | Side | Lever(s) | Milestone | CLIENT | SERVER |
|---|---|---|---|---|---|---|
| `ZNet.RPC_PeerInfo` | prefix | both | G1-L1 | M1 | 80002 | 79821; `IsConnected` 80040-80053 |
| `ZNet.RPC_PeerInfo` | postfix | both | G1-N1 / Negotiation | M1 | 80002 | 79821 |
| `ZNet.Awake` | postfix | both | P6 / ConfigSync register | M1 | 79346 | 79204 |
| `Terminal.ConsoleCommand("noodles",...)` | component | both | P4 (admin surface) | M1 | 43099-43149 | 43098-43148; `RPC_RemoteCommand` 81925-81934 |
| — (`Core.Enabled` + `AutoStandDown` supervisor) | component | both | P4 (kill-switch core) | M1 | n/a | n/a |
| `ZDOMan.CreateNewZDO` / `DestroyZDO` / `SendZDOs` / `ZSteamSocket.SendQueuedPackages` | postfix | server | P7 (telemetry) | M1 | n/a | 76731; 76976-76981; 77055-77070; 86518-86544 |
| `Game.Awake` | postfix | server | P1 (autosave clamp) | M1 | 100491 (ref) | 99902 |
| `FejdStartup.Awake` | postfix | server | P2 (backup clamp) | M1 | 97435 (ref) | 96892 |
| `ZNet.Save` via `SafeToggle` | component | server | P3 (forced pre-toggle save) | M1 (primitive) | 80414; 80760 (ref) | 80221; 80567-80581; silent-skip paths 80223-80231 |
| — (no patch — confirmed-safe non-change) | none | both | P5 | M1 (doc-only) | `ZRpc.HandlePackage` 84205 area (ref) | `ZRpc.HandlePackage` 83748 area (ref) |
| — (live test matrix against A1/A2, not a code lever) | none | both | P8 | M1 (scenarios 1–3); scenarios 4–6 not scheduled — see Deferred | n/a | n/a |

### Ownership (`docs/design/03`)

| Method | Kind | Side | Lever(s) | Milestone | CLIENT | SERVER |
|---|---|---|---|---|---|---|
| `ZDOMan.IsInPeerActiveArea` | postfix | server | G3-L1 | M3 | inert | 76934-76946 |
| `ZDOMan.ReleaseZDOS` | postfix (claim pass) + transpiler (cadence) | server | O6, O3 | M3 | inert | 76919-76930 |
| `ZDOMan.ReleaseNearbyZDOS` | none — called directly (allowlist gate) | server | O6 | M3 | n/a | 76948-76971 |
| `ZNet.RPC_RequestValidSimulationDistance` / `ApplySimulationDistance` | prefix (clamp) | server | O1, G4-N3 | O1: M1 · G4-N3: M3 | inert | 81003-81012; 81023-81030 |
| `ZNet.SendPeriodicData` | transpiler | client | O4 (= G4-N1) | M3 | same src | 80383-80406 (ref) |
| `ZDO.SetOwner` | prefix (bool hold-guard) | both | O7 | M2 (shared groundwork) | same src | 74674-74680 |
| new RPC `RPC_RequestOwnershipHandover` | new RPC | both | O8 | M3 | n/a | modeled on 122011-122032; Ship 140181-141062 |
| `ZRpc.UpdatePing` / `ReceivePing` | postfix | server | O9 (RTT source) | M2 | 84089-84188 (offset) | 83907-83936 |
| `ZDOMan.ReleaseZDOS` | postfix — **separate patch class from O6's**, gated on `Features.O9.Active` alone | server | O9 (policy) | M2 | n/a | 76919-76930 |
| `ZDOMan.ReleaseNearbyZDOS` | postfix, new, read-only capture | server | O9 (H10) | M2 | n/a | 76948-76971; `m_tempNearObjects` 76140/76952 |

### Zone simulation (`docs/design/04`)

| Method | Kind | Side | Lever(s) | Milestone | CLIENT | SERVER |
|---|---|---|---|---|---|---|
| `ZoneSystem.PokeLocalZone` | component (reflection call, ring driver) | server | Z2 | M3 | 114707-114724 (ref) | 113896-113913 |
| `ZNetScene.CreateObjects` | prefix (peer-ring append + grace-hold) | server | Z3 | M4 | 82369-82380; destroy 82497-82499 | 82139-82150; destroy 82267-82269; broadcast 76976-76996 |
| `ZoneSystem.Update` | transpiler (cadence, both 0.1f literals) | both | Z5 | M3 | 114587-114656 | 113776-113845 |
| `ZoneSystem.CreateGhostZones` | prefix (round-robin cap) | both | Z5 | M3 | 114651-114655 (ref) | 113811-113813 (ref) |
| `ZoneSystem.m_zoneTTL` field + `UpdateTTL` | prefix (backlog-drain rewrite) | server | Z4 | M3 | 113898; 116359-116373 | 113087; 115548-115562 |
| — (`ZoneCap.CanInclude`) | component (per-tick-rebuilt circuit breaker) | server | Z9 | M4 | n/a | n/a |
| `ZNetScene.CreateObjects` | transpiler (`maxCreatedPerFrame` literal) | server | Z6 | M4 | 82369-82380; 82360-82367 | 82139-82150; 82130-82136; 82169 |
| `SnapToGround.SnappAll` | prefix, full replace | both | Z7 | M4 | 143551-143565 | 142635-142649 |
| `TerrainComp.TryCleanInvalidTCs` | postfix (telemetry canary + readiness gate) | both | Z8 / G6-N2 | M5 | 144568-144597 | 143652-143681 |

### Gameplay under server authority (`docs/design/05`)

| Method | Kind | Side | Lever(s) | Milestone | CLIENT | SERVER |
|---|---|---|---|---|---|---|
| `ZNet.Awake` | postfix (lazy `PatchAll` if `IsDedicated()`) | both | H0 (shared by S1/S5/S6) | M3 | 79346 | 79204 |
| `SpawnSystem.UpdateSpawning` | transpiler | server | S1 | M3 | 111318 (ref) | 110686 |
| `Character.RPC_Damage` | postfix | server | S3 | M3 | 2414 (ref) | 2413 |
| `EnvMan.GetBiome` / `UpdateEnvironment` | transpiler | server | S5 | M3 | 96447 (ref) | 95904; 95812 |
| `CookingStation.SpawnItem` | transpiler (null-guard) | server | S6.1 | M3 | 123629 (ref) | 122733; 122740/122742 |
| `Pickable.RPC_Pick` | transpiler (null-guard) | server | S6.2 | M3 | (ref) | 71071 |
| `Valkyrie.Awake` | transpiler (null-guard) | server | S6.3 | M3 | (ref) | 147723/147765 |
| `Leviathan.RPC_Left` | transpiler (null-guard) | server | S6.4 | M3 | (ref) | 132147 |
| `MusicVolume.RPC_PlayMusic` | transpiler (null-guard) | server | S6.5 | M3 | (ref) | 135059 |
| — (`HandoverGuard.IsSafeToReclaim`) | component + `QuiescenceTracker` backstop | server | S4, S2/S7 | M4 | n/a | Container 121983-121992; Vagon 147323/147563-147570; Sadle 18250/18255-18270; Ship 140294/140898-140922 |
| `Ship.UpdateOwner` | transpiler (null-check bypass) | server | S4 (ship hand-back) | M4 (dormant until ZoneSim prereq) | n/a | 140858-140867 |
| `BaseAI.UpdateAI` + per-type production postfixes | postfix | server | S2 / S7 | M4 | n/a | 26018; overrides 25649/27879; Smelter 142297/142000 |
| `WearNTear.UpdateSupport` / `Door.RPC_UseDoor` / `RPC_ClearCachedSupport` | none — routing confirmed already safe | server | S8 (correctness) | M4 | n/a | 149920-149987; 123606-123836; 123772 |
| — (`DoorAutoCloseScanner`, reuses `RPC_UseDoor`'s write path) | component / periodic scan | server | S8 (new) | M5 | n/a | `CanInteract` 123684-123687; `s_creator` 136141/136308-136317 |

### Client smoothing & compression (`docs/design/06`)

| Method | Kind | Side | Lever(s) | Milestone | CLIENT | SERVER |
|---|---|---|---|---|---|---|
| `ZSyncTransform.SyncPosition` | prefix (buffered interpolation) | both, server-inert | C1, C3, C5 | M6 | 87922-88018 | 87674-87766 |
| `ZSyncTransform.ClientSync` (kinematic branch) | not built — hook mechanism undecided | client | C2 | deferred | 88033-88063 (ref) | 87785-87815 (ref) |
| `ZDOMan.AddPeer` | postfix (capability negotiation) | both | D1 (H-D1a) | M7 | 76956-76962; 84211 | 76814-76820; called from 79989 |
| `ZDOMan.SendZDOs` | prefix, conditional full replace | both | D1 (H-D1b) | M7 | 77197-77256 | 77055-77118 |
| `ZDOMan.ZDOPeer.ShouldSend` | postfix (in-flight suppression) | both | D1 (H-D1c) | M7 | 76175 | 76050 |
| new RPCs `RPC_ZDODataCompressed` / `RPC_Capability` | new RPC (reuses `RPC_ZDOData` parse body via reflection) | both | D1 (H-D1d/e) | M7 | 77265 (shared body) | 77123-77186 |

### Same-method conflicts within NoodlesMcDoodles

Every case where two of our own hooks target the same vanilla method. None require rejecting either hook; each
resolution is stated once here rather than in every module doc.

1. **`ZNet.RPC_PeerInfo`** — `T2` postfix (01), `G1-L1` prefix (02), `G1-N1` postfix (02). No `HarmonyPriority`
   is strictly required: `G1-L1`'s prefix is the accept/reject gate, but `T2` and `G1-N1` each carry their own
   idempotency guard independent of whether `G1-L1` accepted or rejected a given call (`T2`: `ConditionalWeakTable`
   keyed by peer identity; `G1-N1`: `PeerState.HelloSent`), matching Harmony's own semantics that every postfix
   runs regardless of a sibling prefix's return. Recommend `G1-L1` still carry `HarmonyPriority.First` so it is
   always the first prefix Harmony evaluates on this crowded method (see external conflict E below) — belt, not
   suspenders.
2. **`ZDOMan.SendZDOs`** — `Q3/Q4/Q5/Q12/G2-N1` transpiler (01, always baked into the method body), `P7` postfix
   (02, telemetry, always runs), `D1`/H-D1b prefix (06, can `return false` and skip the transpiled body entirely
   for a compression-confirmed peer). This is the one real conflict: if D1's prefix skips the original, Hook 6's
   transpiled safety guards (Q12/G2-N1) never execute for that call. **Already resolved in `docs/design/06`**:
   `BuildRawBatch` calls Hook 6's own `SharedBudget` chunking helper directly rather than re-implementing it, so
   the guard holds regardless of which path ran. `P7`'s postfix is unaffected either way — it recomputes its
   near-miss condition from live `GetSendQueueSize()` state, not from `__result`, so it stays correct whether the
   transpiled original or D1's replacement produced the send. This makes `SharedBudget` (from `01`, M1) a hard
   prerequisite for `D1` (M7) — already reflected in the milestone order.
3. **`ZDOMan.ReleaseZDOS`** — `O6`+`O3` postfix/transpiler and `O9`'s own, deliberately separate, postfix class
   (03). By design (each gated on its own flag, `OwnershipAuthority.Active` vs. `O9.Active`, so either can run
   without the other). No ordering dependency between them.
4. **`ZDOMan.ReleaseNearbyZDOS`** — `O6` calls it directly (not a patch); `O9`'s `H10` is a genuine postfix on it,
   added specifically because `O6`'s own claim-pass calls trigger it too and `H10` must capture every call's
   per-peer near-object set before the next call overwrites the shared `m_tempNearObjects` field. No conflict —
   this is the intended mechanism.
5. **`ZNet.Awake`** — `P6` postfix (02, RPC registration) and `H0` postfix (05, lazy `PatchAll` of S1/S5/S6 gated
   on `IsDedicated()`). Independent effects, order-independent.
6. **`ZNetScene.CreateObjects`** — `Z3` prefix (04, appends peer-ring ZDOs + grace-hold) and `Z6` transpiler (04,
   raises `maxCreatedPerFrame`). A prefix always runs before the (transpiled) original body regardless of the
   transpiler, so `Z3`'s appended objects are visible to `Z6`'s raised per-frame cap with no extra work needed.

### Conflicts with other installed mods (from `research/13-mod-compat-census.md`) — go/no-go gates

These are the load-bearing findings from the Mono.Cecil scan of Proxima Maxi's ~47 server-side and ~59
client-profile plugin DLLs. "Skip-capable" means a `bool`-returning `Prefix` that can make the original method
(and any lower-priority patch, including ours) a no-op for everyone.

| Vanilla method | Third-party patch | Affects | Gate |
|---|---|---|---|
| `ZDOMan.SendZDOToPeers2` | `ZenDragon-Zen_ModLib` `OptimizeNetworkZDOManSendToPeers2`, non-skip `Prefix` (void, cannot silently defeat us, but shares mutable per-peer state) | `Q2` (M1) | **Hard blocker.** Read Zen_ModLib's patch body, set an explicit `HarmonyPriority`, verify live on a Zen_ModLib-loaded server before Q2 ships. `SendZDOs` itself also carries a harmless non-skip pair from Server_devcommands' ghost-mode suppression — no action needed. |
| `ZDO.SetOwner` | `Wubarrk-GetOffMyLawn` `OwnershipHold/SetOwnerGuard.Prefix` **and** `Wubarrk-Wonderland` `ItemFlow.ZdoSetOwnerPatch.Prefix`, both skip-capable, both the user's own mods | `O7` (M2), `O9` (M2), `S4`'s ship hand-back (M4) | **Hard blocker.** Either guard returning `false` makes our handover silently do nothing. `O7`'s `AuthorizedSetOwner` already verifies the postcondition (`zdo.GetOwner()==newUid`) and increments `ownership.setOwnerVetoed` on a refused claim or handover (a refused hand-back to nobody is `ownership.o11.releaseRefusedByHolder` since 1.0.12), but an explicit `HarmonyPriority` relative to both mods is still required and must be live-verified, not assumed from priority alone. |
| `ZRpc.HandlePackage` | `Wubarrk-Wonderland` `Security.ModEnforcementPatches.Patch_ZRpc_HandlePackage.Prefix`, skip-capable, server-only | Hello/negotiation RPC (M1), D1's new RPCs (M7), O8's RPC (M3) | **Preflight required.** This is the lowest-level RPC dispatch method in the game; if it's an RPC-name whitelist and ours isn't on it, every mod RPC below is silently dropped before reaching vanilla's own "unknown hash → ignore" fallback. Read Wonderland's actual source (it's the user's own mod) and test against a Wonderland-enabled server before any RPC-dependent feature ships live. |
| `ZRoutedRpc.RouteRPC` / `HandleRoutedRPC` / `RPC_RoutedRPC` | 5 independent guard systems: AzuAntiCheat (skip-capable), Wonderland ×2 (one skip-capable), 3×RavenIronStudios sender-side (skip-capable), Zen_ModLib (non-skip dedup) | `O8`'s handover RPC (M3) if routed through `ZRoutedRpc` | **Recommendation, not a blocker**: prefer a raw `ZRpc.Register`-based RPC over `ZRoutedRpc` for our own new RPCs where the call shape allows it (peer-to-peer, not broadcast) — still passes through `HandlePackage` (see above) but skips this second, busier gauntlet entirely. |
| `ZNet.RPC_PeerInfo` | 34 mods, ~80 skip-capable prefix records (mostly per-mod ServerSync `VerifyClient` copies at `HarmonyPriority(800)`) | `T2`, `G1-L1`, `G1-N1` (all M1) | **Test live, not sandboxed.** No single mod here is individually risky, but the aggregate crowd on this one method means our own three patches (internal conflict 1, above) must be verified to still behave correctly on Proxima's real join sequence, not just a clean testbed. |
| `ZDOMan.AddPeer` | `ZenDragon-ZenMap`, non-skip postfix | `G1-L1` (indirect, targets `RPC_PeerInfo`), `D1`/H-D1a (M7, direct postfix on `AddPeer`) | Informational only — ZenMap's postfix is non-skip and does not interact functionally; noted so a future patch-stack trace isn't a surprise. |
| `ZNet.SendPeriodicData` | `Neobotics-HUDCompass` (client-only, in the user's own client profile), **skip-capable** `Prefix` | `O4` (M3) | Confirm what condition HUDCompass's guard checks before trusting O4's tightened refPos cadence on the admin's own client — it's already installed there today. |
| `ZDO.SetOwnerInternal` / `ZDO.Deserialize` | AzuAntiCheat (both, non-skip) + Wonderland (`Deserialize`, non-skip) — activity logging / validation, not gating | Every ownership-churn lever (`G3-L1`, `O6`, `O7`, `O9`) | Cost, not correctness: every `SetOwner` we cause is also logged/validated by AzuAntiCheat. Ownership-churn hysteresis (ground-truth fact #10 — `SetOwner` bumps `OwnerRevision` and forces a resend) already exists to bound bandwidth; it doubles as bounding this overhead. |
| `WearNTear.Damage` | AzuAntiCheat `ACWNT_RPCDamage.Prefix`, skip-capable | `WearNTear.Damage`'s own wrapper/RPC pair is what AzuAntiCheat's prefix targets; `Features.HeldBandPreservationGuard` (WP2 Finding 4, default ON) now patches `WearNTear.ApplyDamage` instead — two calls deeper, past `Damage`/`RPC_Damage` entirely | Still no live conflict: `HeldBandPreservationGuard` was deliberately targeted at `ApplyDamage`, not the shallower `Damage`/`RPC_Damage` pair AzuAntiCheat's skip-capable prefix guards, specifically to avoid needing `HarmonyPriority` coordination with that mod. No action needed today. |
| AzuAntiCheat client-DLL allowlist (operational, not a patch) | `BepInEx/config/AzuAntiCheat/{Whitelist,Greylist}/`, content-hash keyed | Any player running the modded client, other than an `adminlist.txt` admin (exempt) | Every build of the client DLL that will be run by a non-admin player must be copied into the server's `Greylist/` (or `Whitelist/`) folder, by hash, every time it changes, or that player is kicked on join. The admin's own testing sessions already work without this because admins are exempt. |

## Milestones

Each milestone ships on its own, in the order below; each is fully gated behind its own `Features.*.Active`
flag(s) and the global `Core.Enabled` kill switch, and reverts to vanilla with no restart if disabled. Detailed
Data-loss analysis, Test plan and Red-team reconciliation for every item live in the per-module blueprint;
this section states exit criteria and rollback, not the underlying reasoning.

### M1 — Foundation, transport & queues (blueprints 01 + 02 "Phase A", + `03`'s O1)

**Ships**: T1, T2, T3, T4, T5, T8, Q2, Q3, Q4, Q5, Q6, Q7, Q8, Q10 (client-local, default on since 0.1.1), Q12, G2-N1, G6-N4 (01); G1-L1,
G1-N1/Negotiation, P1, P2, P3, P4, P5 (doc-only — no code, confirmed-safe non-change), P6, P7, P8 scenarios 1–2
(executable now on the testbed) and scenario 3 (needs A2's Hello/ConfigSync payloads, still within M1 — P8
scenarios 4–6 are not scheduled, see Deferred), the Features/kill-switch/telemetry framework itself (02); O1 (03,
raises the simulation-distance ceiling — independently useful groundwork, no ownership-engine change yet).

**CRITIC NOTE (flagged, not fixed — needs the lead's judgement):** Q6's `ServerZoneMembership.IsServerHeld` lookup
(`docs/design/01` Hook 8) is a live reference to a type the Ownership module (`03`, M3) owns, even though Q6 itself
ships in M1. This is safe *at runtime* by construction — the set is empty and the lookup is O(1)-false until
`OwnershipAuthority.Active` flips on in M3 — but it means M1 is only independently *runnable*, not independently
*buildable*: the `ServerZoneMembership` class (even as an always-empty stub) must already exist in the codebase
before M1's code compiles, a real, undocumented build-order coupling between two modules this document otherwise
treats as strictly sequential. See the remaining-problems list returned with this review.

**Why bundled**: nothing here changes *who* owns anything — it's pure transport tuning plus the operational
scaffolding (flags, kill switch, telemetry, handshake, save/backup safety) every later milestone needs already
built and load-bearing before it's trusted with an ownership change. `05`'s S1/S3/S5/S6 (gameplay correctness
fixes with no ownership dependency) *could* ship this early too — they're pure vanilla-bug fixes — but are kept
at M3 to match the blueprint's own staging and ship as one coherent, single-test "gameplay under server
authority" unit; nothing stops promoting them earlier if useful.

**Exit criteria**:
- Hook 6's `SendZDOs` transpiler IL is machine-verified (IL dump or JIT-invoke smoke test) before any live
  connection exercises it — mandatory per both red teams on `01`.
- `Q2` vs. Zen_ModLib and `ZDO.SetOwner` vs. GetOffMyLawn/Wonderland priority checks are **not** M1 gates (O7/O9
  land in M2) but `Q2`'s own gate against Zen_ModLib's `SendZDOToPeers2` prefix is read and verified live.
- Boot-loop regression: force a large `m_invalidSector` backlog, confirm chunked skip-and-log, not a
  disconnect/reconnect loop.
- Kill switch verified to fully revert every M1 hook with no restart; all three `AutoStandDown` triggers exercised
  on the testbed (disk-space 2-in-a-row, exception burst, ZDO-mismatch).
- `Save.BackupCount` clamp (2–10) and `Save.AutosaveIntervalSeconds` floor (30s) verified; `SafeToggle` confirmed
  to refuse a toggle when the forced pre-save silently no-opped (`SafeToggleRefusedSkippedSave`).
- Telemetry lines appear at `Telemetry.LogIntervalSeconds`, grep-able.
- Negotiation handshake (`Hello`) tested against a Wonderland-enabled server — `ZRpc.HandlePackage` preflight
  (conflict table) resolved before A2 (negotiation) is considered done, even though A1 (everything server-local)
  has no hard dependency on it.
- T4 timeout reassertion verified against a forced PlayFab join/accept (confirms the postfix wins over vanilla's
  own 30s/90s clobbers).
- Multi-hour soak on the single-server testbed with every M1 flag on, zero unexpected exceptions or
  auto-stand-downs.

**Rollback**: `noodles disable` (or `Core.Enabled=false` in config) reverts every M1 hook immediately; no
persistent/world data is touched by anything in M1 (transport and save-policy tuning only), so the worst case is
uninstalling the DLL entirely with no data implication.

### M2 — O9 latency-aware ownership (interim), pending adjudication

**SUPERSEDED (WP3, binding user decision, 2026-09-28)**: this section's "pending adjudication"/`Active=false`
framing predates the "every feature ON by default for the first live version" decision ("i want it all, all
enabled full test goes live with first version") — `Features.O9.Active` now defaults `true`
(`src/Ownership/LatencyAwareOwnership.cs`; see the Configuration reference table above). What made this safe to
default on is the CRITICAL Finding-4 fix in `docs/impl/OWNERSHIP-IMPL.md` (`IsExcluded` now categorically excludes
a connected peer's own Player avatar before any RTT margin/dwell math ever runs), not a separate live-adjudication
step. Kept below unedited as the historical record of the original M2 exit-criteria reasoning, which is otherwise
unchanged: the `SetOwner`-priority live test against GetOffMyLawn/Wonderland and the `RttDwellSeconds` live-tuning
against real Arizona↔New Jersey jitter (both below) remain NEEDS-LIVE-TEST on Proxima, not blockers to shipping
this default (see also Decision 5).

**Ships**: O7 (`ZDO.SetOwner` hold-guard) and H10 (`ReleaseNearbyZDOS` capture) as "shared groundwork" — both run
in unmodified vanilla behaviour regardless of whether O9 itself is on — then O9 proper, gated solely on
`Features.O9.Active` (default `false`).

**Why here, why separate from M1**: this is explicitly *not yet adjudicated* by the user (see Decisions) — it's
the smaller, reversible answer to the Arizona problem the lead proposed as a candidate ahead of full server-held
zones, and it only needs O7/H10, not any of Phase B core's zone-claiming machinery. Shipping it as its own
milestone means the decision to build it at all doesn't block M1, and doesn't have to wait for M3's larger,
riskier zone-ownership change either.

**Exit criteria**:
- User decision recorded (see Decisions) before this milestone is scheduled for implementation at all.
- `ZDO.SetOwner` vs. GetOffMyLawn/Wonderland priority (conflict table) verified live — `ownership.setOwnerVetoed`
  stays at 0, or any nonzero reading is understood and resolved before shipping past the testbed.
- `O9.RttDwellSeconds` tuned from real Arizona↔New Jersey jitter data (the one genuinely NEEDS-LIVE-TEST
  parameter in this milestone) — starting default 5.0s.
- `ownership.o9.switchesConsideredButSuppressed` nonzero relative to `ownership.o9.switches` (proves hysteresis is
  doing real work, not just always-switch or never-switch).
- No in-use vehicle or container is ever reassigned mid-use (Ship/Vagon/Sadle/Container exclusion verified).

**Rollback**: `Features.O9.Active=false` hands every O9-held object back to vanilla's own ownership rule
immediately (O7's hold guard flushes on kill switch per the shared-infra contract); no persistent data at risk —
O9 only ever calls the same `SetOwner` vanilla already calls, just choosing a different, still-real peer.

### M3 — Ownership core + zone terrain + standalone gameplay fixes (Phase B core groundwork, `04` B1, `05` B1)

**Ships**: G3-L1, G3-N1, O6, O3, O4/G4-N1, G4-N3, O8 (03 — the server-owns-zones primitive itself); Z2, Z4, Z5,
Z10 (04 B1 — real terrain, nothing simulated on top yet); S1, S3, S5, S6, and H0 (05 B1 — standalone vanilla-bug
fixes with no ownership dependency).

**Why bundled**: `G3-L1` is *the* fix — the postfix on `IsInPeerActiveArea` that stops the server's pinned
off-map reference position from losing every object to the nearest peer within 2 seconds. Everything else here is
either a mandatory prerequisite for it (`G3-N1`'s padding, `O1` already shipped in M1), the cadence/radius tuning
around it (`O3`, `O4`, `G4-N3`), the hand-back path it needs (`O8`), or the terrain a server-held zone needs to
exist at all before objects can populate it (`04` B1). `05`'s B1 fixes ship alongside because they're the
regression-proofing that keeps spawning/wind/harvest-item behaviour correct the moment the first zone flips to
server ownership — shipping them one milestone earlier than the objects that would expose their absence (M4) is
deliberate slack, not a hard requirement.

**Exit criteria**:
- `ownership.serverHeldZones` sustains > 0 in a live session with the admin present; `IsInPeerActiveArea`'s fix
  confirmed via telemetry, not just code review.
- **CRITIC FIX**: `G2-N2`'s NEEDS-LIVE-TEST obligation (`research/10-LEVERS.md`) — `O3`'s 1.0s cadence (down from
  vanilla's 2.0s) is the same literal G2-N2 flags as a bandwidth lever, not just a latency one, because every
  `SetOwner` it triggers bumps `OwnerRevision` and forces a resend (ground-truth fact #10). Measure `SendZDOs`
  volume against Q2/Q3/Q5's budgets at the faster cadence before trusting M3 past the testbed — this was an
  unplaced obligation (see the lever-placement fix above), not previously in this milestone's exit criteria.
- `ownership.staleHeldZoneEvictions` behaves — zones the server no longer claims are actually released, no
  permanent stale-forever holds against vanilla's own 4s TTL eviction.
- `ownership.zonesProcessedPerCycle` respects `MaxZonesClaimedPerCycle`; no unbounded per-tick cost as held-zone
  count grows.
- `ZonesRealActive`/`ZoneBuildsThisTick` show real terrain forming around every connected peer without exceeding
  `MaxZoneBuildsPerTick`; no `HeightmapBuilder` starvation observed with the realistic peer count on Proxima.
  Terrain-only (no objects yet), so no `ObjectsDestroyedOnUnfeed`/`CapHitCount` signal is expected this milestone.
- `05` B1: multi-hour session with `HeadlessNRESweep` on shows zero unhandled NREs from the five guarded methods;
  mob spawning confirmed restored in a server-claimed zone; combat stat-credit and headless wind fidelity checked.
- `G1-L1` (already live since M1) re-confirmed under the added join/ownership load.

**Rollback**: `OwnershipAuthority.Active=false` releases every server-claimed zone back to the nearest peer within
one `O3` cadence tick; `Features.ServerZoneSim.Active=false` independently reverts the terrain-ring hooks; the
gameplay B1 flags are each independently toggleable. No persistent world/player data is touched — only ephemeral,
non-`Persistent` ZDOs and ownership pointers move.

### M4 — Zone objects + bounded cost + handover-invariant gameplay (`04` B2, `05` B2)

**Ships**: Z3, Z6, Z7, Z9 (04 B2 — real GameObjects, colliders, `MonsterAI`, bounded by `ZoneCap`); S2, S4, S7, S8
correctness (05 B2 — `HandoverGuard`/`QuiescenceTracker` gated production, vehicle, container and structure
handling).

**Why bundled**: `Z3` is where objects actually start populating server-held zones (the terrain M3 built), which
is exactly the condition `05` B2's hand-over-invariant guards exist to protect — they have no reason to matter
before real objects with owners exist near a server-claimed zone.

**Exit criteria**:
- `ObjectsDestroyedOnUnfeed` stays near-zero and is confirmed, by construction, to never touch a `Persistent`
  ZDO (the grace-hold's only reachable target is non-`Persistent`, per `04`'s Data-loss analysis).
- `ZoneCap`'s per-tick-rebuilt estimate never permanently self-locks (`CapCurrentlyOverBudget` recovers, doesn't
  latch).
- `HandoverGuard.ReclaimBlocked.*.QuiescenceBackstop` is nonzero in real play — live proof the remote-owner blind
  spot the redesign fixed is real, not just a decompile theory.
- `HandoverGuard.DoubleDropDetected` stays at **0** for the entire test window — the hard zero-data-loss
  assertion for this milestone; any nonzero reading blocks shipping past the testbed.
- Each vehicle/container type exercised explicitly with a player mid-use: `Container`, `Vagon`, `Ship`, `Sadle`.

**Rollback**: `Features.ServerZoneSim.Active=false` plus `VehicleContainerHandover`/`ProductionHandoverGuard`/
`StructureHandoverGuard.Active=false` falls back to M3's terrain-only state; the guards are additive
pre-reclaim checks, never a destructive action by themselves, so disabling them removes protection but doesn't
itself risk data — re-enabling M3 alone (without M4) is a safe intermediate state.

### M5 — Handover-correctness residuals + door auto-close (`04` B3, `05` B3)

**Ships**: Z8/G6-N2 (04 B3 — closes the residual `TerrainComp` duplicate-deletion race); S8 new (05 B3 —
`DoorAutoCloseScanner`, off by default).

**Exit criteria**:
- `TerrainCompDuplicatesObserved` stays at the documented narrow-residual rate; terrain-before-objects ordering
  confirmed to reduce (the doc does not claim eliminate) the race.
- `DoorAutoClose`: `SkippedNotPlayerPlaced` fires correctly on POI/dungeon doors (player-placed-only scope
  confirmed — see Decisions), keyed doors (`m_keyItem`) and `m_canNotBeClosed` doors excluded, idle timeout
  respected.

**Rollback**: `Features.DoorAutoClose.Active=false` is fully independent of everything else. `G6-N2`'s telemetry
postfix is deliberately harmless and additive — it may stay on even with the kill switch off.

### M6 — Client smoothing (Phase C1, `06`)

**Ships**: C1, C3 (folded into C1 as a config knob), C5.

**Why here**: no value until server-owned wildlife/dropped items are common (M3–M5), and no dependency on any of
them beyond that — purely client-local rendering, so it's low-risk and can move earlier if the team wants earlier
visual validation.

**Exit criteria**:
- `smoothing.fallback.*` stays low relative to `smoothing.takeovers`; the ingest-time raw-sample teleport guard
  verified not to smear a real teleport into a multi-frame glide.
- Per-instance interpolation-buffer CPU cost measured negligible at realistic concurrent server-owned-creature
  counts on the admin's own client hardware.

**Rollback**: `ClientSmoothing.Active=false` — purely client-local, per-viewer, no server or persistent-data
interaction at all. The safest rollback of any milestone in the project.

### M7 — ZDO compression, shipped dark (Phase C3, `06`)

**SUPERSEDED (WP5, binding user decision, 2026-09-28)**: this section's "shipped dark"/`Active=false` framing
predates the "every feature ON by default for the first live version" decision — `Features.ZdoCompression.Active`
now defaults to `true` (`src/Compression/CompressionModule.cs`; see the Configuration reference table above).
Kept below unedited as the historical record of D1/D2's own design reasoning, which is otherwise unchanged: D2's
live CPU/RTT measurement and the Wonderland preflight are still real readiness gates for a live multi-peer
session (docs/impl/COMPRESSION-IMPL.md "Known risks" / "What must be true..."), just no longer gating the
*code's own default* — negotiation is structural (a vanilla or non-capable peer is never affected regardless of
this flag), which is what makes shipping the default ahead of that live verification safe.

**Ships**: D1 (code), with `Features.ZdoCompression.Active=false` by default; D2 is the live measurement that
gates flipping that default, not code to ship.

**Hard prerequisites**: `01`'s `SharedBudget` (M1) — D1's `BuildRawBatch` calls it directly, not a
reimplementation (internal conflict 2, above); `G1-L1` (M1) — closes the duplicate-`ZDOPeer` keying risk D1's
in-flight state depends on; the `ZRpc.HandlePackage` vs. Wonderland preflight (conflict table) resolved for the
new RPCs.

**Exit criteria**:
- `compression.oversizedEnvelopeSkipped` behaves as a rare, logged skip, never an uncontrolled boot-loop path —
  proves the `SharedBudget` reuse actually holds for confirmed peers.
- `compression.sendException` stays ~0 (Guard 8's fail-open try/catch should essentially never fire).
- D2's live CPU/RTT measurement passes, including aggregate `compression.recvCpuMsPerTick` across multiple
  concurrent confirmed peers, before `ZdoCompression.Active` is ever flipped to `true` outside the testbed.

**Rollback**: flip `ZdoCompression.Active` back to `false` (no longer the default — see the SUPERSEDED note above)
— D1's prefix falls through (`return true`) to the vanilla/M1 `SendZDOs` path for every peer with zero behaviour
change. No persistent data touched; this is a wire-format optimization only.

### Deferred, unscheduled: C2

Kinematic-object (ship/cart) smoothing. Blocked on a hook-mechanism decision (transpiler vs. full-`ClientSync`
prefix) not made in `06`; revisit with real data on whether C1 alone (wildlife/dropped items) already satisfies
the Arizona-case visual goal before committing to it.

## Configuration reference

All keys are `BepInEx` `ConfigFile` entries. "Synced" = server-authoritative value pushed to negotiated modded
clients over `ConfigSync`; every synced key is still a normal local config entry on the server itself. Full
per-key rationale is in each module's own Configuration section; this is the consolidated list.

### Transport (`Transport.*`)

| Key | Default | Synced | Notes |
|---|---|---|---|
| `T1_GlobalRateBps` | **262144** | local | per-connection ceiling (T9 on) or fixed rate (T9 off). Was 1 MiB/s; 256 KiB/s per player sized for ~40 players across all of TheWall's game worlds (Lead decision 7) |
| `T2_JoinBurstBps` | **2097152** | local | T9's join ceiling, range ≥T1 and ≤8×T1. Was 4 MiB/s; 2 MiB/s is the knee (`tools/t9-sim --knee`: a 10 MB join 9.5 s vs 8.7 s at 4 MiB/s, vanilla 67.5 s; 3-4 MiB/s inflated ping on a 20 Mbit line) |
| `T2_MaxBurstSeconds` | 30 | local | hard cap before auto-revert |
| `T3_TimeoutConnectedMs` | 30000 | local | Steam `TimeoutConnected`. Default matches vanilla (fixer pass: was 60000, 2x vanilla, out of the box even in client-only mode) |
| `T4_PingIntervalSec` | 1.0 | local | vanilla default, reasserted |
| `T4_TimeoutSec` | 30.0 | local | reasserted after `ZNet.Start`, and after every PlayFab `OnNetworkJoined`/`Accept` (vanilla clobbers to 30s/90s otherwise). Default matches vanilla (fixer pass: was 60.0, same 2x-vanilla gap as T3) |
| `T5_SendBufferBytes` | 131072 | local | `ZSocket2` raw-TCP, hard clamp 262144 |
| `T8_Enabled` | true | local | server-side telemetry stub fix |
| `Q2_PeersPerTick` | 4 | local | ceiling, hard clamp 1–8, 1 = vanilla |
| `Q2_Adaptive` | true | local | if true, `Q2_PeersPerTick` is a ceiling, not a fixed value |
| `Q3_InnerBudgetBytes` | **65536** | local | now the **ceiling** of a per-connection budget that follows T9's proven rate (`PeerBudget.cs`: rate × measured call gap × 1.3, never below 10240). A fixed 32 KiB queued ~190 ms ahead of everything else on a connection at vanilla's rate (vanilla ~43 ms). Hard clamp 10240–131072; also drives Q5 |
| `Features.Transport.Q13.Active` | **true** | local | round-trip allowance (Lead decision 8, `src/Transport/QueueWindow.cs`): up to one round trip of bytes already on the wire stops counting against the SendZDOs gate, so a far player gets a near player's speed |
| `Features.Transport.Q13.Client.Active` | **true** | local | the same on a modded client's own upload (needs Q10 and T9); replaces the post-17:02 pin at 10240 (user decision) |
| `Q13_RttSlack` | 1.0 | local | allowance = rate x 2-minute minimum ping x this; range 0.5-2.0 |
| `Q13_ServerAllowanceCapBytes` | 49152 | local | per-player cap on the server's allowance (covers 256 KiB/s at ~190 ms); 0 = off |
| `Q13_ClientCeilingBytes` | 49152 | local | a client's whole upload budget (waiting part at most half); 10240 = vanilla |
| `Q13_FrameLoadMs` | 50 | local | allowance withheld while the server's ~20-frame mean frame time is above this |
| `Q4_CongestionFloorBytes` | 4096 | local | range 512–8192 |
| `Q5_OuterGateBytes` | derived from Q3 | local | not independently settable — always equals Q3 |
| `Q6_ServerZoneBias` | 50.0 | local | range 0–200 |
| `Q7_ChunkIdCount` | 8000 | local | range 4000–16000 |
| `Q8_ForceSendBatchCap` | 1000 | local | range 100–5000 |
| `Q10_Enabled` | **true** (0.1.1) | local | client-local; on by default after the 2026-09-28 NJ-AZ live test (user decision) |
| `Q12_FramingMarginBytes` | 4096 | local | range 1024–16384 |
| `G2N1_ChunkBytes` | 65536 | local | range 16384–131072 |
| `G6N4_MaxFieldBytes` | 2048 | local | range 256–8192, shared `SafeZdoWrite` cap |
| `Features.Transport.T9.Active` | true | local | adaptive per-connection rate (Lead decision 5); T1/T2 are its ceilings |
| `T9_FloorBps` | 153600 | local | start and minimum rate = vanilla's |
| `T9_ServerTotalBps` | **10485760** | local | raises stop while the server is already *sending* this much in total (measured, not granted rates, so idle players never block new ones); 10 MiB/s = ~40 players x 256 KiB/s (Lead decision 7); was 8 MiB/s, before that 20 MiB/s of granted rates |
| `T9_PingInflationMs` | 30 | local | back off when ping rises this far above the 2-minute minimum (or half of it, whichever is larger) |
| `T9_LossThreshold` | 0.98 | local | back off below this delivery ratio; below 0.95 at once |

### Rails / Core (`Core.*`, `Handshake.*`, `ConfigSync.*`, `Save.*`, `Telemetry.*`)

| Key | Default | Synced | Notes |
|---|---|---|---|
| `Core.Enabled` | true | local | global kill switch, applies immediately |
| `Core.AutoStandDown.Enabled` | true | local | |
| `Core.AutoStandDown.ZdoMismatchTolerance` | 50 | local | range 10–500 |
| `Core.AutoStandDown.ExceptionBurstLimit` | 5 | local | range 1–50, per rolling 60s |
| `Features.HandshakeReentryGuard.Active` (G1-L1) | true | local | |
| `Features.Negotiation.Active` | true | local | off disables every capability-gated feature |
| `Features.ConfigSync.Active` | true | local | gates send/receive only; registration is unconditional |
| `Features.SaveIntervalClamp.Active` | true | local | |
| `Features.BackupPolicy.Active` | true | local | |
| `Handshake.ProtocolVersion` | 1 | n/a | compiled constant |
| `Handshake.TimeoutSeconds` | 10 | local | range 3–30 |
| `ConfigSync.PushIntervalSeconds` | 30 | local | range 5–300 |
| `Save.AutosaveIntervalSeconds` | 1800 | **synced** | range 30–1800, hard floor; the server's `-saveinterval` always wins (so do `-backups/-backupshort/-backuplong` over the other `Save.*` keys); dedicated server only |
| `Save.BackupCount` | 4 | **synced** | range **2–10** (1 is vanilla's "no backups" sentinel, upgraded with a warning) |
| `Save.BackupShortSeconds` | 7200 | **synced** | floor 5s |
| `Save.BackupLongSeconds` | 43200 | **synced** | floor 5s |
| `Telemetry.LogIntervalSeconds` | 60 | local | range 10–600 |

### Ownership (`OwnershipAuthority.*`, `ClientRefPos.*`, `O9.*`)

| Key | Default | Synced | Notes |
|---|---|---|---|
| `OwnershipAuthority.Active` | **true** | local | DEFAULT ON (user decision, ownership redesign, 2026-09-28: "Fix and do all, default to on, Proxima is our real testing ground") — was "opt-in until field-tested"; see Decisions |
| `OwnershipAuthority.ReleaseZdosInterval` (O3) | **1.0 s** | synced | range 0.5–2.0s — see Decisions |
| `OwnershipAuthority.ClaimGraceWindow` | 5.0 s | synced | range 3–10s |
| `OwnershipAuthority.PeerRefPosPadding` (G3-N1) | 64 m | synced | range 0–192m |
| `OwnershipAuthority.SimulationDistanceCeiling` (O1) | 4 practical / 6 hard clamp | synced | range 0–10 hard-clamped |
| `OwnershipAuthority.O7HoldTimeoutAlert` | 10.0 s | local | ops alert only, range 5–60s |
| `OwnershipAuthority.MaxZonesClaimedPerCycle` | 8 | synced | range 1–32 |
| `ClientRefPos.Interval` (O4/G4-N1) | 0.5 s | local | range 0.2–1.0s, client-local |
| `O9.Active` | **true** | synced | DEFAULT ON (WP3, binding user decision, 2026-09-28: "i want it all, all enabled full test goes live with first version") — was `false` pending adjudication; see M2 and Decision 5 below for the now-superseded framing. Independent of `OwnershipAuthority.Active` |
| `O9.RttMarginMs` | 30 | synced | range 10–100ms |
| `O9.RttDwellSeconds` | **5.0** | synced | range 2–30s — see Decisions, NEEDS-LIVE-TEST |
| `O9.RttEwmaAlpha` | 0.2 | synced | range 0.05–0.5 |
| `O10.Active` **(EXPERIMENTAL)** | **true** (WP3, was `false`) | synced | DEFAULT ON (WP3, binding user decision, 2026-09-28: "i want it all, all enabled full test goes live with first version") — overrides this row's original "user decision 2, ownership redesign... default to off"; safe because it shares O9's own Finding-4 `IsExcluded` fix. Server-in-shared-areas: server becomes a candidate owner for an object 2+ far peers both sight. Requires `O9.Active=true` too (shares its own reassignment pass). See `docs/impl/OWNERSHIP-IMPL.md`'s dedicated O10 section (Config table there updated to match) |
| `O10.FarRttThresholdMs` **(EXPERIMENTAL)** | 60 ms | synced | range 20–150ms |
| `OwnershipAuthority.Mode` (SAA WP-A) | **FarOrShared** | synced | enum `SharedFar` \| `FarOrShared` \| `All` — how far O6's near-field claim pass reaches into a zone a peer is standing in, on top of the always-unchanged far-band behavior. Auto-downgrades to `SharedFar` under `OwnershipAuthority.ModeQuarantine` (below) until an operator runs `noodles clear ModeQuarantine` — see docs/impl/OWNERSHIP-IMPL.md's WP-A/WP-E sections |
| `OwnershipAuthority.AllowShipClaim` (SAA WP-A) | false | synced | only consulted at `Mode=All`; off by default — O8 (`OwnershipHandoverRpc`) has no vanilla-client hand-back path for a server-held ship |
| `OwnershipAuthority.PickableReleaseRadius` (SAA WP-A) | 12 m | synced | range 6–30m; radius around a connected peer's LIVE avatar position inside which a `Pickable` is refused at claim time and proactively handed back if already server-held |
| `OwnershipAuthority.CompatClientClaimGraceSeconds` (SAA WP-A, `CompatClaimExclusions.cs`) | 5.0 s | synced | range 3–10s; recent-client-interaction grace (crafting from nearby chests, area repair, etc.) before near-field claiming that object |
| `OwnershipAuthority.LoadShed.ShedEnterMs` (SAA WP-D, `FrameBudget.cs`) | 100 ms | local | range 20–500ms; server frame-mean threshold, held continuously for `ShedEnterDwellSeconds`, before near-field claiming starts shedding held zones back to vanilla — see docs/impl/OWNERSHIP-IMPL.md |
| `OwnershipAuthority.LoadShed.ShedExitMs` (SAA WP-D) | 60 ms | local | range 20–500ms; recovery threshold, held continuously for `ShedExitDwellSeconds`; clamped at runtime to never exceed `ShedEnterMs` |
| `OwnershipAuthority.LoadShed.ShedEnterDwellSeconds` (SAA WP-D) | 4 s | local | range 1–30s |
| `OwnershipAuthority.LoadShed.ShedExitDwellSeconds` (SAA WP-D) | 8 s | local | range 1–30s; longer than the enter dwell by design — recovering is never urgent |
| `OwnershipAuthority.LoadShed.MaxZonesShedPerCycle` (SAA WP-D, `LoadShedder.cs`) | 4 | local | range 1–16; how many NEW held near-field zones are marked shed per `ZoneClaimSweep.ActiveInterval` cycle while frame budget is over |
| `OwnershipAuthority.ModeQuarantine.Active` (SAA WP-E, `ModeQuarantine.cs`) | **true** | local | DEFAULT ON — O11's own independent veto-burst watcher; disables the WATCHER only, not any already-tripped quarantine (`noodles clear ModeQuarantine` for that) |
| `OwnershipAuthority.ModeQuarantine.VetoBurstLimit` (SAA WP-E) | 50 | local | range 5–5000; `ownership.setOwnerVetoed` occurrences (refused claims and handovers; not refused hand-backs to nobody) within `VetoBurstWindowSeconds` before `Mode` auto-downgrades to `SharedFar` |
| `OwnershipAuthority.ModeQuarantine.VetoBurstWindowSeconds` (SAA WP-E) | 60 s | local | range 10–300s; rolling window `VetoBurstLimit` is measured over |

### Zone simulation (`ZoneSim.*`)

| Key | Default | Synced | Notes |
|---|---|---|---|
| `Features.ServerZoneSim.Active` | **true** | local | master switch for this module. DEFAULT ON (user decision, ownership redesign, 2026-09-28) together with `OwnershipAuthority.Active` and `HeldBandRingBonus=1` below — the combination that lets the server actually hold real territory instead of building inert terrain (the prior "was true, switched to false" note below is superseded; a cross-flag warning now catches the ServerZoneSim-on/OwnershipAuthority-off misconfiguration this note used to guard against by keeping the default off) |
| `PokeRingRadius` (Z2) | 2 | local | clamped to `NearSimulationDistance` |
| `HeldBandRingBonus` (new, held-band redesign, 2026-09-28) | **1** | local | 0-2; widens the SAME ring `PokeRingRadius` builds/feeds so O6 can actually claim it — see docs/design/04 "The held band" |
| `MaxZoneBuildsPerTick` | 4 | local | new, B1's own cost cap |
| `ZoneTTLSeconds` (Z4) | **15** | local | clamped 4–20s |
| `UpdateTickSeconds` (Z5) | **0.05** | local | also drives `UpdateTTL`'s companion 0.1f literal |
| `MaxGhostSpawnsPerTick` | 1 | local | round-robin across peers |
| `MaxCreatedPerFrame` (Z6) | 100 | local | matches vanilla's dedicated-server branch |
| `CreateDestroyHz` | 30 | local | 60Hz option unused by default |
| `ObjectUnfeedGraceTicks` | 3 (~150ms) | local | 0 restores pre-fix immediate-sweep |
| `MaxObjectsPerPeer` | **4,000** | local | knee-of-curve default for `HeldBandRingBonus=1`'s wider ring (was 2,500, resized 2026-09-28 — see docs/impl/ZONESIM-IMPL.md) |
| `MaxObjectsGlobal` | **24,000** | local | = `MaxObjectsPerPeer` x 6 (was 15,000, resized alongside it) |
| `CapIncludeThresholdPct` / `CapExcludeThresholdPct` | 0.90 / 1.00 | local | hysteresis band |
| `Features.HeldBandPreservationGuard.Active` (WP2) | **true** | local | DEFAULT ON — freezes the destructive/despawn vanilla code paths (`WearNTear` weather/ash/lava/support-loss damage and piece-combat damage, `Ship` Ashlands damage, `ItemDrop`'s 1-hour despawn, wildlife-vs-tame and wildlife-vs-piece combat) that only fire because `ServerZoneSim`+`OwnershipAuthority` instantiate and claim the empty held ring nobody is near. Turning this OFF is NOT the safe fallback while those two stay on — it reopens item loss/tame loss/piece loss in the held band. A player can always damage their own or any tame/piece normally regardless of this flag. See docs/impl/ZONESIM-IMPL.md |

### Gameplay (`Features.*`)

| Key | Default | Synced | Notes |
|---|---|---|---|
| `ServerSpawning.Active` (S1) | **true** | local | DEFAULT ON 2026-09-28 (WP4, all-on safety-net spec) — was `false`; see docs/impl/GAMEPLAY-IMPL.md |
| `ServerSpawning.MaxZonesScannedPerSecond` | 8 | local | 1–64 |
| `ServerCombatStats.Active` (S3) | **true** | local | DEFAULT ON 2026-09-28 (WP4) — cosmetic/telemetry only, was `false` |
| `HeadlessBiomeWind.Active` (S5) | **true** | local | DEFAULT ON 2026-09-28 (WP4) — field-verified, was `false` |
| `HeadlessNRESweep.Active` (S6) | **true** | local | recommended on, lazy IsDedicated()-gated |
| `VehicleContainerHandover.Active` (S4) | **true** | local | DEFAULT ON 2026-09-28 (ownership redesign decision 1) — one of the two in-use guards O6's reclaim gate needs now that `OwnershipAuthority` defaults on |
| `HandoverGuard.QuiescenceSeconds` | 5 | local | must exceed Smelter's 1s accumulator gap |
| `ProductionHandoverGuard.Active` (S7) | **true** | local | DEFAULT ON 2026-09-28 (ownership redesign decision 1) — the other in-use guard O6's reclaim gate needs |
| `StructureHandoverGuard.Active` (S8 correctness) | false | local | door/support routing scope only |
| `DoorAutoClose.Active` (S8 new) | **true** | local | DEFAULT ON 2026-09-28 (WP4, overrides Decision 4's original off-by-default) — was `false` |
| `DoorAutoClose.IdleSeconds` | 300 | local | 30–3600 |
| `DoorAutoClose.ScanIntervalSeconds` | **30** | local | 1–60; raised from Decision 4's 5s (WP4, "great improvement, not overkill" — 6x cheaper scan, still <10% of IdleSeconds) |

### Smoothing & compression

| Key | Default | Synced | Notes |
|---|---|---|---|
| `ClientSmoothing.Active` (C1) | false | local | experimental; does not beat vanilla on worst-case stutter (tools/smooth-sim), stays off |
| `ClientSmoothing.PlaybackDelayMs` | 100 | local | 0–300ms; C1 only |
| `ClientSmoothing.ShortExtrapolation.Active` (C3) | **true** (0.1.1) | local | the shipped smoothing: vanilla rendering, only the 2 s dead-reckoning cap shortened; on by default after the live test (user decision: stops the far player snapping back) |
| `ClientSmoothing.ExtrapolationCapMs` | 500 | local | 500–2000ms; vanilla is 2000. Measured 2.42 m vs 3.88 m max overshoot in a dropped-batch gap |
| `ZdoCompression.Active` (D1) | **true** | synced | WP5, binding user decision ("every feature ON by default" for the first live version, "Compression ON after its live checks") — supersedes the row below's earlier "stays false" framing. Negotiated per peer only (`PeerState.HasCapability`); a vanilla peer or an older/incompatible NoodlesMcDoodles build is never affected regardless of this default. D2's live CPU/RTT measurement + the Wonderland preflight (docs/impl/COMPRESSION-IMPL.md "Known risks") are unchanged readiness gates for a REAL live session — this default flip ships the code, it does not assert either has passed |
| `ZdoCompression.AckTimeoutMs` | 3000 | synced | 1000–10000 |
| `ZdoCompression.SchemaVersion` | 1 | n/a | build-time constant |

## Telemetry and the zero-data-loss proof

Every module logs its own full counter catalog (see each blueprint's Telemetry section); this is the subset that
specifically proves — or would disprove — zero data loss, one `[NoodlesMcDoodles] telemetry key=value` log line
per `Telemetry.LogIntervalSeconds`.

**Should always read zero / stay flat; any sustained nonzero is the loss signal**:
- `HandoverGuard.DoubleDropDetected` — an object simultaneously claimed by two owners. Must be 0 for the life of
  the server; a single nonzero reading is a stop-the-line bug, not a tuning issue.
- `OversizedZdoWriteRejected` — G6-N4's per-field cap rejecting a write; correct operation is always 0.
- `ObjectsDestroyedOnUnfeed` — live proof the Z3 grace-hold's residual only ever touches non-`Persistent` ZDOs
  (structurally, per the code, it cannot reach a `Persistent` one); should be near-zero in ordinary play, and any
  reading against a prefab known to be `Persistent` would mean the structural guarantee itself broke.
- `SafeToggleRefusedSkippedSave` / `SafeToggleRefusedDiskSpace` — nonzero means a live-authority toggle correctly
  refused to proceed rather than proceeding on an unverified save; the mechanism working as designed, but repeated
  triggering needs investigation (disk space or a latched `SkipSaving()` state).
- `ownership.setOwnerVetoed` — a third-party `SetOwner` prefix (GetOffMyLawn/Wonderland) silently vetoed our
  handover; the postcondition check catching exactly the conflict-table risk it exists for. A refused hand-back to nobody (a mod holding an object on purpose) is `ownership.o11.releaseRefusedByHolder`, not this.
- `compression.sendException` — D1's Guard 8 fail-open catching an exception; should be ~0.
- `compression.oversizedEnvelopeSkipped` — the 512 KiB boot-loop hazard being avoided; expected ~0 with
  `SharedBudget` working correctly, nonzero under genuine overload is the intended fallback, not silent data loss.

**The reconciliation figure** — `ZdosCreated`, `ZdosDestroyed`, and the derived `ZdoDelta` (created minus
destroyed since last reset) is the core zero-data-loss proof: a sustained drift beyond
`Core.AutoStandDown.ZdoMismatchTolerance` across two telemetry periods is one of the three conditions that trips
the kill switch automatically.

**Save health**: `SavesCompleted`, `SavesSkipped`, `SaveDurationMs` — `SavesSkipped` incrementing at all outside
an expected `SkipSaving()` window (e.g. right after `nextseed`) needs investigation before trusting any subsequent
`SafeToggle`-gated feature flip.

**Ownership-specific**: `ownership.staleHeldZoneEvictions` (TTL sync working), `ownership.zonesProcessedPerCycle`
+ cost (the `MaxZonesClaimedPerCycle` cap holding), `ownership.o8.requestTimedOut` (handover RPC silently dropped
by a third-party guard — see conflict table), `ownership.deadZoneStalls` (an O7 hold outliving its alert
threshold, force-cleared).

## Test plan

**Testbed first** (`libs-Tools/DEDICATED-SERVER-TESTBED/`, `run-linux-1.0-server.sh`), one milestone at a time, in
the order in Milestones — each module's own Test plan section is the detailed script (regression checks,
boot-loop forcing, IL verification, multi-hour soaks). Only after a milestone passes on the clean testbed does it
move to a live test against Proxima Maxi's real mod stack, because several of the go/no-go gates in the conflict
table (Zen_ModLib, Wonderland, the RPC_PeerInfo crowd, AzuAntiCheat) cannot be exercised on a clean testbed at
all — they require the real plugin set.

**Live-test-specific items, beyond what each blueprint's own Test plan already lists**:
- M1: join through the real `RPC_PeerInfo` crowd (34 mods); verify `T4`'s timeout reassertion against a genuine
  PlayFab/crossplay join if one is available.
- M2/M3: `SetOwner` priority test against GetOffMyLawn and Wonderland with real gameplay (claim a plot, trigger
  Wonderland's item-flow throttle) actively running.
- M1 (A2)/M3 (O8)/M7: `HandlePackage`/`ZRoutedRpc` preflight against a Wonderland-enabled server before any
  RPC-dependent feature is trusted live.
- Any milestone distributing the client DLL to a non-admin player: confirm that build's hash is in AzuAntiCheat's
  `Greylist/` first (server-side change, ask the server owner).

**NoodlesProbe** (`probe/`, read-only, client-only, no Harmony/RPCs/ZDO writes, installed only in the Gale
"Proxima - ADMIN -Noodle Tester" profile) is the live measurement tool for the admin's own connection and is safe
to run directly against Proxima without any server-side change. It logs at 1 Hz to `BepInEx/NoodlesProbe/`:
Steam live ping/quality/bytes, send queue depth, ZDOs sent/received, frame time, an owner census of instantiated
objects (both near-area and the far ring), GC collections/heap, plus a per-owner and per-other-player breakdown
(`owners-*.csv`: objects held, change rate; other players' character-update count, max gap, metres moved). F10
writes a manual lag mark. `probe/analyze_probe.py` summarises the newest session.

Use it to validate, from the admin's own seat: `T4`/`Q2` tuning (send-queue and RTT behaviour under M1), `O3`/`O4`
handover responsiveness once M2/M3 are live (owner-census transitions, per-owner change rate around a zone
boundary), and `O9`'s RTT-based switching behaviour directly (the admin's own measured RTT vs. `ownership.o9.*`
telemetry from the server side). No server-side probe exists or is planned (explicit user rule); server-side
telemetry is read via log grep on the `[NoodlesMcDoodles] telemetry` format instead.

## Decisions for the user

Six genuine forks where the blueprints deliberately left a choice rather than picking one; everything else in the
config tables above is already resolved with a concrete default.

1. **Q2 — fixed vs. adaptive peers-per-tick.** User wrote "2-" (incomplete). **Recommended: adaptive, ceiling 4**
   (`Q2_Adaptive=true`, `Q2_PeersPerTick=4` as a ceiling, not a fixed value) — won't overrun frame budget under
   load, and 4 is a conservative starting point given the combined Q2×Hook-8 per-frame cost hasn't been measured
   live yet at Proxima's real near-ring sizes (open question in `01`). Tune the ceiling toward 8 only after that
   measurement exists. Config already ships both shapes so either can be selected without a code change.
2. **O3 — ownership-sweep cadence.** User wants 0.6s. **Recommended: start at 1.0s**, tune down only after
   `ownership.*` telemetry shows handover *latency*, not bandwidth, is the actual bottleneck at 1.0s — bandwidth
   cost scales with `SetOwner`'s `OwnerRevision` bump (ground-truth fact #10) and is the thing 0.6s would risk
   without evidence it's needed.
3. **T4 — ping interval / timeout.** Fully resolved, not really open: **ping 1.0s (vanilla, unchanged), timeout
   60.0s**, reasserted after every `ZNet.Start()` (vanilla clobbers to 30s) and after every PlayFab
   `OnNetworkJoined`/`Accept` (vanilla clobbers to 90s) — the mod always wins back to the configured 60s. No
   further user input needed here; listed for completeness since HANDOFF flagged it as open.
   **UPDATE (fixer pass, major finding):** the default itself was the bug — 60.0s (and T3's sibling 60000ms)
   is 2x vanilla's own 30s/30000ms, and both apply to the client's own connection by default in client-only
   mode (Q10 defaults on), so out of the box this mod's own failure detection was slower than vanilla's floor.
   T3/T4 now default at vanilla's own values (30000/30.0); still admin-raisable for more patience with a
   laggy-but-alive player.
4. **S8 — door auto-close, and "player only?"** **Recommended: off by default** (opt-in, matches every other new
   behaviour), **player-placed doors only** when enabled — the scanner already excludes any door whose
   `ZDOVars.s_creator` isn't a player-placed one, plus vanilla's own `m_keyItem`/`m_canNotBeClosed` exclusions
   (matching `Door.CanInteract`'s own treatment), so POI/dungeon doors are never touched. `IdleSeconds` has no
   user target — recommended **300s (5 min)**, `ScanIntervalSeconds` 5s.
   **UPDATE (WP4, 2026-09-28, all-on safety-net spec):** `DoorAutoClose.Active` now defaults **on** (audited,
   confirmed zero data-loss risk — a single ZDO field write through vanilla's own replication path; the only
   effect is a ward mod's own door-access check not seeing this one privileged server write), overriding this
   decision's original off-by-default. `ScanIntervalSeconds` also raised from this decision's 5s to **30s**
   (still <10% of `IdleSeconds`, 6x cheaper scan) now that every server pays it by default, not just an opt-in
   few. See docs/impl/GAMEPLAY-IMPL.md, "Configuration — Defaults table" for the full reasoning.
5. **O9 — build it at all?** Not yet adjudicated (HANDOFF: "proposed but not yet adjudicated"). **Recommended:
   yes, as M2**, specifically because it's small, fully reversible, and attacks the Arizona problem directly
   without waiting on the much larger M3–M5 zone-ownership work — but keep `O9.Active=false` until the
   `SetOwner`-priority live test (Decision context, conflict table) and the `RttDwellSeconds` live-tuning against
   real Arizona↔New Jersey jitter both pass.
   **UPDATE (WP3, 2026-09-28, all-on safety-net spec):** `O9.Active` now defaults **on** ("i want it all, all
   enabled full test goes live with first version"), overriding this point's original recommendation to keep it
   off pending the live tests above. Made safe by the CRITICAL Finding-4 fix (`IsExcluded` now categorically
   excludes a connected peer's own Player avatar before any RTT math runs — docs/impl/OWNERSHIP-IMPL.md). The
   `SetOwner`-priority live test and the `RttDwellSeconds` live-tuning against real Arizona↔New Jersey jitter this
   point originally gated the default on remain NEEDS-LIVE-TEST on Proxima, not blockers to shipping this default
   (see also M2 above).
6. **Ship ownership hand-back for vanilla clients.** `03`'s open question: build a real vanilla-client-reachable
   request/grant RPC for server-held ships (mirroring Vagon/Sadle), or accept that a vanilla client can never take
   back a server-held ship (`Ship.UpdateOwner` structurally cannot fire server-side —
   `Player.m_localPlayer` is permanently null there). **Recommended: accept the limitation for now** — it's the
   least game-critical of the vehicle types, and building a new client-visible RPC is real scope that should wait
   until M4 data shows it's actually a problem in play, not a theoretical one.

## Deferred and rejected items

- **CRITIC FIX — P8 scenarios 4–6** — not scheduled to any milestone (a placement gap; P8 itself was previously
  missing from this document entirely, see the lever-placement fix under Architecture). Per `docs/design/02`'s own
  lever table: scenarios 4–5 need multiple concurrent real clients, not yet scripted against any milestone here;
  scenario 6 needs `G6-N1` (a lead-added, non-selected lever with no home in this document either). Revisit once a
  multi-client test rig exists.
- **C2 (kinematic/vehicle smoothing)** — deferred, no target milestone. Hook mechanism (transpiler vs. full
  `ClientSync` prefix) undecided; revisit with real data once C1 (M6) ships and shows whether wildlife/item
  smoothing alone already satisfies the visual goal.
- **T6/T9 (`ZPlayFabSocket` tuning, adaptive per-connection rate controller)** — `01`'s own "M2" (a different
  numbering from this document's M2 — renamed here to avoid confusion), out of scope for the current 55-lever
  selection; not scheduled.
- **WearNTear mid-`UpdateSupport()` force-claim guard (`05` H7)** — deferred, not designed. No ZDO-replicated
  signal fine enough for the sub-second race was found; S8 (correctness) still ships in M4 on the strength of the
  ownership subsystem's clean-relinquish contract alone for this one race, an explicitly accepted, bounded
  residual rather than a solved guard.
- **Q7's queue-size-aware defer-between-chunks guard** — deferred; `InvokeRoutedRPC`'s broadcast form doesn't
  expose per-peer routing without its own change.
- **`Core.AutoStandDown` per-feature attribution** (vs. tripping the whole kill switch) — deferred; not cleanly
  possible until an ownership feature exists to be the plausible suspect. Currently trips globally on every
  trigger, the simplest and safest default.
- **Capabilities bitmask beyond bit 0** — reserved, not assigned; the first real consumers are the smoothing/
  ownership hand-over features, which will claim bits as they ship rather than pre-allocating speculatively.
- **RCON / a console surface for a headless server with no attached admin client** — explicitly rejected, not
  proposed. The accepted gap: an operator with shell access only, no player connected, has no in-process way to
  flip the kill switch except editing the config file directly (which does apply live via `SettingChanged`).
- **O9 within a server-held zone's own boundary region** (RTT tie-break for two peers approaching a zone about to
  be released) — **RESOLVED 2026-09-28** (held-band redesign synthesis, Open question 3 in
  `docs/design/03-ownership-and-handover.md`): O9's own write path only, never O6's/`HeldBandRingBonus`'s — the
  two domains are geometrically disjoint by construction (O9 only ever acts inside some peer's own active area;
  O6/`HeldBandRingBonus` only ever holds a zone outside every peer's active area), so there is no dual-writer
  ambiguity to resolve. See that doc's Open question 3 for the full resolution and its preserved O10
  (server-in-shared-areas) notes for the genuinely different, still-unbuilt idea this question originally
  gestured toward.
- **Full Water/WaterVolume/Pathfinding update-logic audit (Z10 residual)** — flagged `[unverified]` in `research/
  04`, not independently re-derived during blueprint reconciliation; left as a known gap, not blocking.
- **Doc filename cross-references** — `04` and `05` cite `docs/design/03-ownership-and-authority.md` and a
  "persistence" doc at `docs/design/06-*.md`; the repo's actual files are `03-ownership-and-handover.md` and
  `06-client-smoothing-and-compression.md` (no persistence-named doc exists). Noted, not fixed — doc hygiene, not
  a decompile or data-loss issue; worth a one-line fix in a future pass.
