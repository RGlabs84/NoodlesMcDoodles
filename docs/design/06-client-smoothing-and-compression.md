# Client smoothing and compression (Phase C)

> Ground truth citations `SERVER:<line>` / `CLIENT:<line>` / `UTILS:<line>` are 1.0.16 (network version 40).
> Every load-bearing line cited below was re-opened directly with `grep -n`/`sed -n` against
> `assembly_valheim_SERVER.decompiled.cs`, `assembly_valheim.decompiled.cs` and `assembly_utils.decompiled.cs`
> during this pass — not copied from `research/11-client-smoothing.md`/`12-delta-and-compression.md` on trust —
> and matched exactly unless noted otherwise. Both research docs already carry their own independent verify
> passes (adversarial, two lenses each); this pass re-checked their highest-impact citations a third time and
> found nothing further to correct. Signature/line differences between the two assemblies are called out
> explicitly wherever they matter to a hook.

## Summary

This module (`src/ClientSmoothing/`, `src/Compression/`) covers two independent, loosely-coupled pieces:

1. **Client-side rendering smoothness for non-owned objects** (the C\* levers, `research/11`). Vanilla's
   non-owner interpolation (`ZSyncTransform.ClientSync`/`SyncPosition`) is a crude single-sample 0.2/0.5
   lerp/slerp with an abrupt 5 m instant-teleport snap and a blind up-to-2 s velocity extrapolation. It exists
   on every install today but vanilla only exposes its crudeness for creatures owned by *another player*.
   **Once this project's ownership subsystem (`03-ownership-and-handover.md`) makes the dedicated server the
   default owner of nearly every creature and idle object in a server-held zone, that crude path stops being
   an occasional edge case and becomes the default rendering experience for essentially all wildlife on every
   connected client, all the time.** That is the actual reason this module exists now rather than being a
   low-priority polish item: server ownership doesn't introduce a new smoothing bug, it just makes an
   existing, previously-rare one universal.
2. **Bandwidth reduction on the ZDO sync stream between two modded peers** (the D\* levers, `research/12`).
   Under server-owned zones the server becomes the ZDO source of truth for far more objects across every
   connected peer's batch simultaneously (egress scales with `peers × ZDOs-in-range` instead of being spread
   across whichever client happened to own a given object) — exactly the stream this project's other
   already-decided levers (raised `SendZDOs` budget, more peers serviced per tick, `Q6` priority reweighting)
   are deliberately enlarging, and it is 100% uncompressed today on the Steam/raw-TCP paths that carry it.

**What ships where:**

| Milestone | Levers | What it buys | Gate |
|---|---|---|---|
| **Phase C1 (this doc's build target)** | C1, C3, C5 | Buffered, time-based non-owner interpolation for ordinary (non-kinematic) creatures and dropped items — the actual fix for "server-owned wildlife looks laggy" | Client-only, opt-in kill switch, dedicated-server process inert |
| **Phase C2 (deferred, not v1)** | C2 | Same treatment for the kinematic branch (ships/carts) | Blocked on a hook-mechanism decision (transpiler vs. full-`ClientSync` prefix) not yet made — see Hooks |
| **Phase C3 (build, ship dark)** | D1, D2 | Negotiated GZip compression of the ZDO batch stream between two modded peers | Ships with `Features.ZdoCompression.Active = false` by default; D2's live CPU/RTT measurement is the gate to flipping the default, not a prerequisite to writing the code |
| **Rejected** | D3 | Field-level delta encoding | UNSAFE under this project's zero-data-loss rule — not merely deferred, not to be built without a from-scratch integrity-chain design (see Levers covered) |
| **Rejected (not a lever, flagged for the record)** | "AI-intent broadcast" extrapolation channel (research/11 Summary) | Speculative predicted-position channel | Never researched to a design; would add a new wire message and server compute for unmeasured benefit over C1 + `Q6` — not pursued |
| **Reference only, already decided elsewhere** | C4 (= `Q6`) | Send-priority reweighting toward server-simulated zones | Already `Phase A` per `01-transport-and-queues.md`; restated here only because it's the single biggest lever for shrinking the *source* jitter C1's buffer has to hide |

**What this fixes for the Arizona case.** Neither piece touches *who* owns a zone or *how fast* the server can
push bytes (that's `01`/`03`/`04`'s job). C1 fixes what every connected client — including a local player
near the Arizona admin — actually *sees* once the server (not the admin) owns the wildlife around them: instead
of vanilla's rubber-banding lerp/snap, every client with the mod installed gets smooth, time-interpolated
motion for that server-owned wildlife, regardless of how far away the server itself is from any given viewer.
D1 shrinks the bytes the server has to push to reach every peer with that same server-owned traffic, which is
exactly the traffic volume this project's other levers are increasing. Both are strictly additive to, never a
substitute for, `03`'s ownership fix and `01`'s send-queue tuning.

**No preload.** Nothing in this module fetches, generates, or instantiates any world data ahead of when
vanilla would. C1 only reinterprets bytes vanilla already sent for objects already instantiated on that
client. D1 only compresses bytes vanilla's own `SendZDOs` already decided to send this round. Neither ever
requests, prefetches, or speculatively loads a zone, object, or area a client hasn't already been given.

## Levers covered

| Lever | Verdict (carried from research/11, 12; LEAD-CORRECTED noted) | User target | Final value/range recommended here | Milestone |
|---|---|---|---|---|
| C1 | SAFE-WITH-GUARDS (verify pass downgraded from SAFE — see research/11 Verification: the "always degrades to byte-identical vanilla" claim needed two new mandatory guards; **red-team reconciliation reworked Guard 3's teleport detection and Guard 4's buffer lifecycle — see Red-team reconciliation**) | not yet on board (C\*/D\* were never presented for selection — see HANDOFF next-steps) | Ship with guards 1–5 below; default playback delay 100 ms, clamped 0–300 ms, config-tunable | Phase C1 |
| C2 | SAFE-WITH-GUARDS (verify pass: same downgrade as C1, plus a new finding that C1's hook shape does not reach this code) | not yet on board | Deferred — see Hooks, "C2 (deferred)" | Phase C2, no date |
| C3 | SAFE-WITH-GUARDS | not yet on board | Folded into C1's patch as a config knob; **default unchanged from vanilla (2000 ms cap)** until `T8`/D2-style live telemetry gives a real send-round figure to bound it against | Phase C1 |
| C4 | SAFE-WITH-GUARDS — not a new build, cross-reference only | selected (`Q6`) | No independent value here; owned by `01-transport-and-queues.md`, already `Phase A` | Phase A (elsewhere) |
| C5 | SAFE | not yet on board | Inline runtime check inside C1/C2's own prefix, not a separate patch-application gate (see Hooks — simpler and equally correct) | Phase C1 |
| D1 | SAFE-WITH-GUARDS (verify pass added guards 6–7; **red-team reconciliation added Guard 8 (mandatory fail-open try/catch) and a hard dependency on `01`'s `SharedBudget`/G2-N1 for the invalid-sector write — both were blockers, both fixed in place; see Red-team reconciliation**) | not yet on board | Ship code now; `Features.ZdoCompression.Active = false` by default until D2 passes **and** the Wonderland `HandlePackage` pre-flight (Open question #8) is resolved; schema version is a fixed build-time constant, not a user knob | Phase C3 |
| D2 | NEEDS-LIVE-TEST | not yet on board | Not a lever — a measurement obligation gating D1's default-on flip; test scope must include realistic non-LAN RTT (verify-pass correction: CPU cost scales with RTT-driven resends, not just batch size) **and, per red-team reconciliation, aggregate receive-side decompression cost across all confirmed peers pending in the same tick, not just a per-batch send-side average — see `compression.recvCpuMsPerTick`** | Phase C3 (as a test, not code) |
| D3 | UNSAFE — reject | not yet on board | Do not build. A diverged sender/receiver baseline can be silently promoted to permanent, world-wide, persisted authoritative state on the next ownership handover (`SetOwnerInternal`/`OwnerRevision`, `SERVER:77157-77177`); no per-batch hash-and-rollback guard closes this, because a corrupted sender-side baseline poisons its own hash before the hash is ever computed. Revisit only behind a dedicated, live-tested integrity-chain design — not in this project's current scope. | Rejected |

## Hooks

For every hook: exact `Type.Method` signature, `CLIENT:`/`SERVER:` citation (confirmed this pass, both
assemblies unless noted), patch kind, side, why this hook, a code sketch against the shared infrastructure
contract, and off-flag behavior.

### H-C1 — `ZSyncTransform.SyncPosition(ZDO zdo, float dt, out bool usedLocalRotation)`

- **Signature confirmed identical in both assemblies this pass**: `CLIENT:87922` (`private void
  SyncPosition(ZDO zdo, float dt, out bool usedLocalRotation)`), `SERVER:87674` (byte-identical body, only the
  line offset differs — re-read in full both sides, confirms `research/11`'s own claim that the dedicated
  server compiles and can run this exact code path). Owning class `ZSyncTransform` opens at `CLIENT:87646` /
  `SERVER:87398`.
- **Patch kind**: Harmony `Prefix`, private method, `usedLocalRotation` declared `ref` in the patch signature
  (Harmony's required convention for patching an `out` parameter) so the same prefix can also take over the
  rotation slerp that lives in the *caller*, `ClientSync` (see guard 5 below).
- **Side**: client-only by intent; the patch is applied on both processes (single DLL) but gates itself off on
  a dedicated server at the top of the method body — see H-C5, folded in here rather than as a separate patch.
- **Why this hook and not another**: `SyncPosition` is the exact unit vanilla already uses to isolate "compute
  this tick's target position for a non-owner." Patching it (rather than the outer `ClientSync`) leaves
  `ClientSync`'s owner gate (`CLIENT:88022-88025`/`SERVER:87774-87777`, confirmed this pass byte-identical),
  frame-dedupe check, kinematic branch, scale sync, and rigidbody gravity/sleep handling completely untouched
  — the smallest hook that reaches the actual crude-lerp/snap behavior this module exists to fix.
- **Confirmed this pass, not in the original research doc's citation list**: `SyncPosition` has exactly two
  structurally distinct branches — the parented branch (`m_characterParentSync && zdo.HasOwner()`, re-read in
  full `CLIENT:87924-87991`) which computes position/rotation relative to a parent transform (another player
  sitting on a bench, standing on a moving ship) and unconditionally sets `usedLocalRotation = true` itself at
  the end of that branch, and the plain branch (`CLIENT:87995-88018`) which never touches
  `usedLocalRotation` (stays `false` from the method's opening line, confirmed `CLIENT:87924`/`SERVER:87676`).
  The rotation slerp this hook needs to intercept (`Quaternion.Slerp(..., 0.5f)`) lives in `ClientSync` itself,
  confirmed at `CLIENT:88076`/`SERVER:87828` (**RED-TEAM FIX, RT2 minor**: corrected from a previously-cited
  `SERVER:87826`, which is one line up — the guarding `if (Quaternion.Angle(base.transform.rotation, rotation2)
  > 0.001f)` line, re-confirmed this pass; `87828` is the actual `base.transform.rotation = Quaternion.Slerp(...)`
  assignment; `CLIENT:88076` was already correct), gated by `if (m_syncRotation && !usedLocalRotation)`, not
  inside `SyncPosition` — which is exactly why the `ref usedLocalRotation` write is mandatory, not optional.

```csharp
// src/ClientSmoothing/Patch_ZSyncTransform_SyncPosition.cs
[HarmonyPatch(typeof(ZSyncTransform), "SyncPosition")]
static class Patch_ZSyncTransform_SyncPosition
{
    // Harmony patches an `out` parameter through a `ref` of the same type in the patch signature.
    static bool Prefix(ZSyncTransform __instance, ZDO zdo, float dt, ref bool usedLocalRotation)
    {
        // Inline C5: dedicated server runs this same non-owner path for ZDOs it doesn't own
        // (e.g. connected players' own Characters, CLIENT:88022-88025/SERVER:87774-87777) —
        // skip it there, nothing on that process ever renders it.
        if (ZNet.instance != null && ZNet.instance.IsDedicated()) return true;   // CLIENT:81380/SERVER:81194
        if (!Features.ClientSmoothing.Active) return true;                       // kill switch: vanilla body runs

        // Guard 4 (mandatory): vanilla's own parented-object condition, re-checked every tick,
        // exactly mirrors CLIENT:87924-87927/SERVER:87676-87679. Defer to vanilla unconditionally.
        // RED-TEAM FIX (RT1 minor): explicitly reset the buffer on both the entry into and the exit
        // from this branch, so a long parented excursion (sitting on a bench, riding a ship for
        // several minutes) never leaves a stale pre-parenting sample for TryInterpolate to blend
        // against once un-parenting hands rendering back to this prefix. `state.NoteSuspended()` marks
        // the buffer "needs a fresh single sample before interpolating again" without deallocating it.
        if (__instance.m_characterParentSync && zdo.HasOwner())
        {
            var connId = zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.SyncTransform);
            if (!connId.IsNone())
            {
                SmoothState.For(__instance).NoteSuspended();
                return true;
            }
        }

        try
        {
            var state = SmoothState.For(__instance);           // co-located MonoBehaviour, see guard 2

            // Guard 3 (mandatory, RED-TEAM FIX — RT1 major #3): detect a teleport from the RAW sample
            // pair at ingest time, the same moment vanilla itself would see it — never from the
            // *interpolated* render output. Vanilla's own check (CLIENT:88012/SERVER:87764,
            // `Vector3.Distance(base.transform.position, position) ... < 5f`) always compares the
            // current render against a single freshly-read/extrapolated raw target, so a real
            // teleport is caught in exactly one tick. The original sketch instead compared the
            // render against `TryInterpolate`'s *output* — a point blended between two buffered
            // historical raw samples — so when those two samples straddle a teleport, the blend
            // sweeps smoothly across the gap over however many real ticks the sample interval spans;
            // each individual frame-to-frame delta of that sweep can be well under 5 m even though
            // the true displacement between the two raw samples is large (e.g. 30 m over a 1.5 s gap
            // is <1 m/frame at 60 fps), so the old `dist >= 5f` check could never fire and the object
            // would visibly glide across the teleport instead of snapping — exactly the "server-owned
            // wildlife looks laggy/wrong" symptom this module exists to fix. Fixed by moving the
            // check into Ingest itself, comparing the new raw sample directly against the previous
            // raw sample (not the render), and resetting the buffer there and then:
            bool teleported = state.Ingest(zdo);   // Ingest now returns true iff the new raw sample is
                                                     // >=5m from the immediately preceding raw sample
                                                     // (buffers a new sample only if DataRevision changed)
            if (teleported)
            {
                __instance.transform.position = zdo.GetPosition();
                Telemetry.ClientSmoothing.FallbackTeleport();   // same `smoothing.fallback.teleport` counter,
                                                                  // now fired at ingest time (Guard 3)
                return false;    // snapped this tick; nothing left to interpolate against yet
            }

            if (!state.TryInterpolate(
                    Config.ClientSmoothing_PlaybackDelayMs.Value,
                    Config.ClientSmoothing_ExtrapolationCapMs.Value,   // C3, folded in; default = vanilla's 2000ms
                    out Vector3 pos, out Quaternion rot, out bool tookRotation))
            {
                return true;                                    // buffer not ready (just spawned, no 2nd sample yet)
            }

            __instance.transform.position = pos;   // buffer already guaranteed teleport-free by the Ingest-time check

            if (tookRotation)
            {
                __instance.transform.rotation = rot;
                usedLocalRotation = true;                        // Guard 5 (mandatory): or ClientSync's own
            }                                                     // slerp at CLIENT:88076/SERVER:87828 still
                                                                    // runs and partially overwrites this
            Telemetry.ClientSmoothing.Takeover();
            return false;
        }
        catch (Exception ex)
        {
            // Guard 1 (mandatory): fail open. Neither this code nor the vanilla code it replaces
            // ever calls zdo.Set(...)/SetOwner — both are provably read-ZDO/write-local-Transform
            // only (re-confirmed this pass: no zdo.Set call anywhere in SyncPosition or ClientSync).
            Telemetry.ClientSmoothing.FallbackException(ex);
            return true;
        }
    }
}
```

- **Off-flag behavior**: `Features.ClientSmoothing.Active == false` → the prefix's second check returns `true`
  immediately, every tick, for every instance — `SyncPosition`'s vanilla body runs exactly as it does today,
  with zero measurable overhead beyond one bool read per call.

### H-C2 (deferred, Phase C2 — not built in this milestone)

- **What it would be**: the same buffered-interpolation treatment for the kinematic branch inside `ClientSync`
  (ships, carts), confirmed inline (not a separate callable method) at `CLIENT:88033-88063`/`SERVER:87785-87815`
  (**RED-TEAM FIX, RT2 minor**: corrected from a previously-cited `CLIENT:88033-88070`, which overran the
  kinematic-only `if` block's true close by 7 lines into the adjacent non-kinematic `else` branch — including
  that branch's own `SyncPosition(zDO, dt, out usedLocalRotation)` call, which is not part of the kinematic
  branch this section describes; the `SERVER` bound was already correctly scoped) — 5 m position snap, 0.2
  lerp, 45° rotation snap, `Rigidbody.MovePosition`/`MoveRotation` in place of a direct transform write —
  re-read this pass, matches `research/11` exactly, no `m_characterParentSync` reference anywhere in this
  branch).
- **Why not this milestone**: there is no `SyncPosition`-shaped private method to prefix here — it's inline IL
  inside `ClientSync`. Reaching only this branch needs either (a) a Harmony `Transpiler` targeting just that IL
  block, or (b) a full `Prefix` on `ClientSync` itself that reimplements the owner gate
  (`CLIENT:88022-88025`/`SERVER:87774-87777`) and frame-dedupe check and falls through to vanilla for every
  case except the kinematic branch. Both are materially higher implementation risk than H-C1's clean method
  prefix, and vehicles are rarer and usually player-crewed (crew members are owned by their own client
  regardless, per `O8` in `03-ownership-and-handover.md`), so the payoff is lower too. **Decision: build H-C1
  first, live-test it, then revisit (a) vs. (b) for C2 with real data on whether wildlife-only smoothing
  already satisfies the Arizona-case goal.** Not rejected — parked, with the hook-mechanism gap explicitly on
  record so it isn't rediscovered from scratch later.

### H-D1a — `ZDOMan.AddPeer(ZNetPeer netPeer)` — negotiation

**RED-TEAM FIX (RT2 major #1): retargeted from `ZNet.RPC_PeerInfo` to `ZDOMan.AddPeer`.** The original sketch
Postfixed `RPC_PeerInfo` and justified it as "runs after `m_zdoMan.AddPeer(peer)`" — but a Harmony Postfix runs
after the WHOLE original method returns, on every return path, not just the one that reaches `AddPeer`.
`RPC_PeerInfo` (SERVER:79821-79999, re-read in full this pass) has at least six early `return`s before its
`AddPeer` call that fire for a connection about to be rejected: `peer == null` (SERVER:79827), version mismatch
(79869), blacklist (79881), invalid Steam session ticket (79897), server full (79921), wrong password (79936).
`GetPeer(rpc)` still returns a non-null `ZNetPeer` on every one of these (the peer object is allocated at socket
accept, independent of `RPC_PeerInfo`'s own outcome), so the original postfix would register `NMD_CAP`/`NMD_ZDOC`
and send `Hello` even to peers about to be kicked — harmless in isolation (wasted registration/one packet on a
dying connection) but it directly contradicts the doc's own "runs after AddPeer" rationale and the doc's own
citation for that call was off by one line (`SERVER:79990` is actually `m_routedRpc.AddPeer(peer)`; the real
`m_zdoMan.AddPeer(peer)` call is `SERVER:79989`, both re-confirmed this pass). Rather than reconstructing "did
the connection actually succeed" logic inside a `RPC_PeerInfo` postfix, this hook now targets `ZDOMan.AddPeer`
directly — the one call that is *only* ever reached from `RPC_PeerInfo`'s single success path (the `IL_04a4`
label) on both builds, never from any rejection branch.

- **Signature confirmed identical in both assemblies this pass**: `public void AddPeer(ZNetPeer netPeer)` —
  `SERVER:76814-76820`, `CLIENT:76956-76962`. Call sites: `SERVER:79989` (`m_zdoMan.AddPeer(peer);`, inside
  `RPC_PeerInfo`'s success path only) and `CLIENT:80182` (identical). `netPeer.m_rpc` gives this hook the same
  `ZRpc` the original sketch obtained via `GetPeer(rpc)`.
- **Patch kind**: Harmony `Postfix`.
- **Side**: both (each side negotiates its own connection independently; the handshake is symmetric).
- **Why this hook**: `RPC_PeerInfo` has no protocol-level capability field of its own (confirmed this pass —
  it exchanges name/password/world/seed and calls `VersionMatch()` unconditionally, nothing negotiable), so a
  new capability can only be layered on as an additive postfix that registers new, namespaced RPC names — this
  project's already-accepted `G1-N1` pattern, reused here rather than re-invented. Hooking `AddPeer` instead of
  `RPC_PeerInfo` keeps that same pattern while guaranteeing the postfix only ever fires on a connection that
  actually joined.

```csharp
// src/Compression/Patch_ZDOMan_AddPeer.cs
[HarmonyPatch(typeof(ZDOMan), "AddPeer")]
static class Patch_ZDOMan_AddPeer
{
    static void Postfix(ZNetPeer netPeer)
    {
        if (!Features.ZdoCompression.Active) return;           // kill switch: never advertise this capability
        if (netPeer == null) return;
        // Guard 4: matches H-D1b's own ZPlayFabSocket check (RED-TEAM FIX, RT2 minor — the two guards
        // had drifted: only H-D1b checked this, so the Compatibility section's "never negotiates on a
        // ZPlayFabSocket connection at all" claim was false against the original sketch). Never even
        // offer the capability on crossplay, since ZPlayFabSocket already compresses everything once
        // VersionMatch() fires (SERVER:91578-91580).
        if (netPeer.m_socket is ZPlayFabSocket) return;

        // Long, namespaced names: ZRpc.Register does a silent Remove-then-Add on a 32-bit hash
        // collision (confirmed SERVER:83981-83986/CLIENT:84211 — CLIENT re-checked this pass;
        // corrects the original sketch's unfilled "CLIENT:..." placeholder), never an exception —
        // a short/common name risks a silent last-write-wins overwrite against another mod's RPC.
        netPeer.m_rpc.Register<ZPackage>("NoodlesMcDoodles.NMD_ZDOC", Compression.RPC_ZDODataCompressed);
        netPeer.m_rpc.Register<ZPackage>("NoodlesMcDoodles.NMD_CAP", Compression.RPC_Capability);
        Compression.SendHello(netPeer);                          // {SchemaVersion} — explicit int, never a bool
    }
}
```

- **Off-flag behavior**: registering the handlers is always cheap and harmless even if never used —
  `ZRpc.HandlePackage` (confirmed `SERVER:83952-83971`, re-read this pass) silently no-ops on an unrecognized
  hash, so a vanilla or non-participating peer that never sends `NMD_CAP`/`NMD_ZDOC` is completely unaffected.
  With the flag off, the postfix never registers anything and never sends `Hello` — structurally identical to
  the feature not existing.
- **Open pre-flight dependency (RED-TEAM FIX, RT2 major #2)**: `ZRpc.HandlePackage`'s "silently no-op on an
  unrecognized hash" fallback (SERVER:83952-83971) is only vanilla's behavior. The live server also runs the
  user's own `Wubarrk-Wonderland` mod, whose `Security.ModEnforcementPatches/Patch_ZRpc_HandlePackage.Prefix`
  is a **skip-capable** patch on this exact method (`research/13-mod-compat-census.md` §2, confirmed by static
  analysis of the installed plugin, not yet by reading its guard logic). If that prefix is an RPC-name
  whitelist, as its name and its sibling guards on `ZRoutedRpc.RouteRPC`/`HandleRoutedRPC` and
  `ZNet.RPC_ServerSyncedPlayerData` suggest, it could silently drop `NMD_CAP`/`NMD_ZDOC`/`Hello` before they
  ever reach vanilla's unknown-hash fallback — D1 would then simply never negotiate on Proxima Maxi, with no
  error and no obvious telemetry signal distinguishing it from "the other peer is vanilla." This is not unique
  to D1 (`02-safety-rails-and-ops.md`'s own P5/G1-N1 handshake rests on the identical vanilla-only assumption
  and has the same open gap), but it is a hard pre-flight blocker for flipping `ZdoCompression.Active` on
  Proxima Maxi specifically: read Wonderland's actual guard logic (the user's own mod, source available) before
  enabling D1 there, and confirm `compression.confirmedPeers` actually climbs in a Wonderland-enabled test.
  Promoted to Open questions #8 below.

### H-D1b — `ZDOMan.SendZDOs(ZDOMan.ZDOPeer peer, bool flush)` — compress-and-send

- **Signature confirmed identical in both assemblies this pass**: `SERVER:77055` (`private bool
  SendZDOs(ZDOPeer peer, bool flush)`), `CLIENT:77197` (same signature, confirmed the client build compiles
  and can call this method too — a modded client sending its own owned ZDOs to the server goes through the
  identical code path, which is why D1 is a `runs: both` lever).
- **Patch kind**: Harmony `Prefix` that fully replaces the body (`return false`) for compression-confirmed,
  non-PlayFab peers; falls through to vanilla (`return true`) for everyone else — same replace-or-fall-through
  shape as H-C1, for the same reason (minimizes how much of vanilla's own send logic this design takes over).
- **Side**: both (mirrors the method itself).
- **Why this hook**: this is the one call site that actually builds and dispatches a `ZDOData` batch
  (`SERVER:77055-77118`, re-read in full this pass — confirms the per-item budget check at `77094-77096`, the
  optimistic `peer.m_zdos[...] = new PeerZDOInfo(...)` write at the *send-time* line `77111`, and the single
  `rpc.Invoke("ZDOData", zPackage)` at `77118`). Any compression scheme has to sit exactly here to intercept
  the bytes before the `"ZDOData"` invoke, not build a parallel structure elsewhere.
- **`ZDOMan.ZDOPeer` is a private nested class** (confirmed `SERVER:76008`, `private class ZDOPeer`) — its
  fields (`m_peer`, `m_zdos`, `m_invalidSector`, `m_forceSend`) are all `public`/`readonly` *within* that
  nested class (confirmed `SERVER:76008-76033`), so this hook is reachable once the project's existing
  `BepInEx.AssemblyPublicizer` HintPath convention exposes the nested type at compile time. **[Unverified this
  pass]** whether the publicizer promotes a private nested class's own accessibility, not just its members —
  worth a one-line build-time check before implementation; if it doesn't, `AccessTools.TypeByName("ZDOMan+ZDOPeer")`
  is the fallback, at the cost of losing compile-time type safety on this one patch.

```csharp
// src/Compression/Patch_ZDOMan_SendZDOs.cs
[HarmonyPatch(typeof(ZDOMan), "SendZDOs")]
static class Patch_ZDOMan_SendZDOs
{
    static bool Prefix(ZDOMan __instance, ZDOMan.ZDOPeer peer, bool flush, ref bool __result)
    {
        if (!Features.ZdoCompression.Active) { return true; }
        if (!Compression.IsConfirmed(peer.m_peer)) { return true; }     // no handshake yet -> plain ZDOData
        if (peer.m_peer.m_socket is ZPlayFabSocket) { return true; }    // Guard 4: crossplay already compresses
                                                                          // at the socket layer (VersionMatch,
                                                                          // SERVER:91578-91580) — never stack a
                                                                          // second compression pass on it; kept
                                                                          // in sync with H-D1a's own copy of this
                                                                          // same check (RED-TEAM FIX, see H-D1a)

        // Guard 8 (mandatory, RED-TEAM FIX — RT1 blocker #1): this whole body MUST fail open. Unlike
        // every other hook in this module, SendZDOs is called from SendZDOToPeers2 (SERVER:76884-76907/
        // CLIENT:77026-77049), which is called from ZDOMan.Update (SERVER:76859-76868/CLIENT:77001-77010),
        // called directly from ZNet.Update() immediately before UpdateSave() (SERVER:80090-80091/
        // CLIENT:80283-80284, no try/catch between the two calls, both confirmed this pass). An
        // uncaught exception here propagates out of SendZDOToPeers2 *before* its own `m_nextSendPeer++`
        // and wraparound-reset lines run (SERVER:76904-76907), so `m_nextSendPeer` freezes on this peer's
        // index; the same exception then fires on the very next Unity frame for the same reason, forever,
        // and UpdateSave() never runs again on any frame this keeps happening on — a silent, permanent
        // halt to both world saves and ZDO sync for every connected peer, not just this one, until a
        // manual restart. try/catch is therefore mandatory around the ENTIRE prefix body, matching the
        // fail-open pattern Guard 1 (H-C1) and Guard 6 (H-D1d) already use elsewhere in this module.
        try
        {
            // Same budget fields Transport (01) already tunes via Q3/Q5 — read the LIVE configured
            // values, never a second hardcoded 10240, so D1 stays consistent if Phase A's numbers move.
            int sendQueueSize = peer.m_peer.m_socket.GetSendQueueSize();
            int outerGate = Transport.Config.Q5_OuterGateBytes.Value;
            if (!flush && sendQueueSize > outerGate) { __result = false; return false; }
            int budget = outerGate - sendQueueSize;
            if (budget < Transport.Config.Q4_CongestionFloorBytes.Value) { __result = false; return false; }

            __instance.m_tempToSync.Clear();
            __instance.CreateSyncList(peer, __instance.m_tempToSync);        // unchanged vanilla call — H-D1c's
                                                                               // postfix on ShouldSend is what skips
                                                                               // in-flight ZDOs inside this scan
            if (__instance.m_tempToSync.Count == 0 && peer.m_invalidSector.Count == 0)
            { __result = false; return false; }

            // RED-TEAM FIX (RT1 blocker #2): BuildRawBatch does NOT reimplement its own ad hoc size
            // check. It calls the SAME SharedBudget running-total helper 01-transport-and-queues.md's
            // Hook 6 transpiler inserts into vanilla SendZDOs for Q12/G2-N1 (src/Transport/ZdoSendBudget.cs,
            // `SharedBudget.WriteInvalidSectorChunked`/`TryAppendWithinBudget`). This is load-bearing, not
            // a style choice: H-D1b's Prefix returns `false` for a confirmed peer, which means Harmony
            // SKIPS the (transpiler-patched) vanilla method body outright — Hook 6's own Q12/G2-N1 guard
            // only lives *inside* that body and therefore never runs for any compression-confirmed peer,
            // no matter what order the two patches ship in. Reusing the exact same helper (not "coordinating
            // two independent copies") is the only way a confirmed peer's `peer.m_invalidSector` write
            // (SERVER:77075-77083/CLIENT:77217-77225, unconditional and unbounded in vanilla, re-confirmed
            // this pass) is bounded here too. See "Interactions with other subsystems" and Data-loss #17.
            var batch = Compression.BuildRawBatch(__instance, peer, budget);
            if (batch.Count == 0 && peer.m_invalidSector.Count == 0) { __result = false; return false; }

            int seq = Compression.NextSequence(peer);
            byte[] compressed = batch.Package.GetCompressed();               // ZPackage.GetCompressed -> Utils.Compress
                                                                               // -> GZipStream, UTILS:12483-12496

            // RED-TEAM FIX (RT1 blocker #2): explicit hard-ceiling check on the COMPRESSED envelope,
            // right before the RPC invoke — mirrors Q12's own "skip-and-log, never truncate" guard
            // (research/10-LEVERS.md line 137) so a pathological batch that GZip couldn't shrink enough
            // (e.g. high-entropy ZDOID structs from a mass ownership-handover sweep) cannot reach the
            // documented 512 KiB reliable-Steam-message boot-loop, even after SharedBudget's own
            // running-total check on the *uncompressed* side.
            const int SteamReliableCeilingBytes = 524288;
            if (4 + 4 + compressed.Length + 64 /* framing margin */ >= SteamReliableCeilingBytes)
            {
                Telemetry.ZdoCompression.OversizedEnvelopeSkipped(compressed.Length);
                __result = false;
                return false;   // skip this round only; nothing in batch.Ids was marked in-flight yet
            }

            Compression.TrackInFlight(peer, seq, batch.Ids);                 // Guard 7: withheld from ShouldSend
                                                                               // until this batch acks or times out

            var envelope = new ZPackage();
            envelope.Write(Compression.SchemaVersion);
            envelope.Write(seq);
            envelope.Write(compressed);
            peer.m_peer.m_rpc.Invoke("NoodlesMcDoodles.NMD_ZDOC", envelope); // NEVER also invoke "ZDOData" this round
            Telemetry.ZdoCompression.BatchSent(batch.Package.Size(), compressed.Length);

            __result = true;
            return false;
        }
        catch (Exception ex)
        {
            // Guard 8 (mandatory, RED-TEAM FIX): fail open to vanilla's own SendZDOs body for this tick
            // instead of letting the exception escape into SendZDOToPeers2/ZDOMan.Update/ZNet.Update.
            Telemetry.ZdoCompression.SendException(ex);
            return true;
        }
    }
}
```

- **Off-flag behavior**: identical to H-D1a — every check short-circuits to `return true` and vanilla's own
  `SendZDOs` body runs untouched, including its own optimistic `peer.m_zdos` write.
- **Dependency (RED-TEAM FIX, RT1 blocker #2)**: this hook is now a hard prerequisite on
  `01-transport-and-queues.md`'s Hook 6 (`SharedBudget`, M1/Phase A) — not just "sequenced after" it, but
  literally calling into its shared chunking helper. D1 (Phase C3) cannot ship, even dark, until `SharedBudget`
  exists and is exposed for reuse outside Hook 6's own transpiler. This is a new cross-doc dependency; see
  "Interactions with other subsystems" below.

### H-D1c — `ZDOMan.ZDOPeer.ShouldSend(ZDO zdo)` — in-flight suppression (guard 7)

- **Signature confirmed identical in both assemblies this pass**: `SERVER:76050` (`public bool
  ShouldSend(ZDO zdo)`), `CLIENT:76175` (same signature).
- **Patch kind**: Harmony `Postfix`.
- **Side**: both.
- **Why this hook**: `CreateSyncList` (confirmed `SERVER:77259-77271`, re-read this pass) rescans a peer's
  entire candidate set through `ShouldSend` on *every* `SendZDOs` round with no notion of "already offered but
  awaiting ack" — only "known" (has an `m_zdos` entry) or "not known." Because H-D1b deliberately withholds the
  `m_zdos` write until ack, without this postfix every unacked ZDO would be reselected and recompressed on
  every round until the ack lands — for any connection whose RTT is at or above the project's send-round
  interval (effectively all real, non-LAN connections), a sustained redundant-resend loop that can cost *more*
  bandwidth/CPU than plain `ZDOData`, exactly the finding `research/12`'s verify pass added as mandatory guard
  7.

```csharp
// src/Compression/Patch_ZDOPeer_ShouldSend.cs
[HarmonyPatch(typeof(ZDOMan.ZDOPeer), "ShouldSend")]
static class Patch_ZDOPeer_ShouldSend
{
    static void Postfix(ZDOMan.ZDOPeer __instance, ZDO zdo, ref bool __result)
    {
        if (!Features.ZdoCompression.Active) return;
        if (__result && Compression.IsInFlightUnacked(__instance, zdo.m_uid))
            __result = false;   // already offered in an unacked NMD_ZDOC batch — don't reselect every round
    }
}
```

- **Off-flag behavior**: no-op; `__result` (vanilla's own answer) is never touched.

### H-D1d / H-D1e — new RPCs `RPC_ZDODataCompressed` / `RPC_Capability`, and reuse of vanilla's merge logic

- **Not a patch on existing vanilla code** — two brand-new RPC handlers, registered by H-D1a. **Kind**: new
  RPC / component.
- **Side**: both.
- **Why reuse, not reimplement, the merge logic**: `RPC_ZDOData`'s own body (confirmed `SERVER:77123-77186`,
  re-read in full this pass — the revision-gate merge at `77157-77165`, the `continue` that discards a
  stale-but-owner-newer payload at exactly `77165`, `CreateNewZDO` at `77170`, the final `m_zdos` write at
  `77177`) is the one place vanilla's whole-ZDO merge semantics live. A parallel parser for the decompressed
  path would be a second place those semantics could accidentally diverge — a real desync risk this design
  must not introduce. Instead of a transpiler splitting `RPC_ZDOData` into a shared method (real IL-surgery
  risk on a hot, security-relevant parse path), this design **invokes the existing private method directly by
  reflection**, once cached, so the compressed path is provably running the identical code, not a lookalike:

```csharp
// src/Compression/Compression.cs
static class Compression
{
    const int SchemaVersion = 1;

    // ZDOMan.RPC_ZDOData(ZRpc, ZPackage), private instance method — SERVER:77123 / CLIENT:77265.
    static readonly MethodInvoker s_rpcZdoData =
        MethodInvoker.GetHandler(AccessTools.Method(typeof(ZDOMan), "RPC_ZDOData"));

    public static void RPC_ZDODataCompressed(ZRpc rpc, ZPackage pkg)
    {
        if (!Features.ZdoCompression.Active) return;   // registered handler simply no-ops mid-session
        try
        {
            int version = pkg.ReadInt();
            int seq = pkg.ReadInt();
            byte[] compressed = pkg.ReadByteArray();
            if (version != SchemaVersion)
            {
                SendDowngrade(rpc, Reason.VersionMismatch);
                return;                                  // fails closed: this connection stays on ZDOData
            }
            byte[] raw = Utils.Decompress(compressed);    // UTILS:12483-12496
            var inner = new ZPackage(raw);
            s_rpcZdoData(ZDOMan.instance, rpc, inner);     // IDENTICAL merge logic to RPC_ZDOData — not a copy
            SendAck(rpc, seq);                             // only now does the sender apply its deferred m_zdos write
        }
        catch (Exception ex)
        {
            // Guard 6 (mandatory, verify-pass correction). RED-TEAM FIX (RT2 minor): corrected
            // rationale — the original text claimed this escapes into a clean per-peer "disconnect."
            // What actually happens, re-confirmed this pass: ZRpc.Update's own catch treats
            // EndOfStreamException specially (SERVER:83887-83897) and returns ErrorCode.IncompatibleVersion;
            // ZNet.UpdatePeers (SERVER:80458-80491) checks that return value and does
            // `m_connectionStatus = ConnectionStatus.ErrorVersion` — but does NOT call `Disconnect(item)`
            // in that branch (Disconnect is only called from UpdatePeers' *other*, unrelated
            // `!IsConnected()` loop). `m_connectionStatus` is `private static` (SERVER:79138) — ONE field
            // shared by the whole ZNet instance, not per-peer. So the real failure mode without Guard 6 is
            // worse than "that peer disconnects": a shared static field gets mutated to ErrorVersion
            // (visible/misleading process-wide, on a host that also runs two other Valheim server
            // processes) while the triggering peer's `ZRpc` connection stays open and keeps re-throwing
            // the same exception on every subsequent `Update()` without ever being removed from `m_peers`
            // — a stuck connection, not a clean disconnect. This try/catch MUST still wrap the whole
            // handler body, not just Decompress(), to prevent either outcome.
            Telemetry.ZdoCompression.DecompressOrParseException(ex);
            SendNack(rpc);
        }
    }

    public static void RPC_Capability(ZRpc rpc, ZPackage pkg)
    {
        var msg = (CapMsg)pkg.ReadInt();
        switch (msg)
        {
            case CapMsg.Hello:        RecordPeerVersion(rpc, pkg.ReadInt()); break;
            case CapMsg.Ack:          CommitInFlight(rpc, pkg.ReadInt()); break;        // release deferred m_zdos write
            case CapMsg.Nack:         DropInFlight(rpc, pkg.ReadInt()); break;          // batch failed to parse; ShouldSend will reselect
            case CapMsg.Downgrade:    PermanentlyDowngrade(rpc); break;                 // no renegotiation mid-session
        }
    }
}
```

- **Off-flag behavior**: `RPC_ZDODataCompressed`/`RPC_Capability` both no-op immediately when the flag is off.
  Because the handlers were only ever registered while the flag was on (H-D1a), a peer that connected while the
  feature was disabled simply never gets these RPC names registered at all — indistinguishable from a vanilla
  peer, from the other side's point of view.

## Data-loss analysis

Every path by which this module could lose, roll back, duplicate, or corrupt data, and its guard.

| # | Path | Mechanism | Guard |
|---|---|---|---|
| 1 | C1: exception mid-tick in the buffered-interpolation math | A bug in `SmoothState.Ingest`/`TryInterpolate` throws | try/catch around the whole prefix, fail open to `return true` (vanilla's own logic for that tick). Cannot touch the ZDO at all — neither `SyncPosition` nor `ClientSync` ever calls `zdo.Set(...)`/`SetOwner` (re-confirmed this pass: no such call anywhere in either method body), so this design, which only *adds* reads and writes a *different* local `Transform`, cannot introduce a ZDO write path that wasn't already absent. |
| 2 | C1: per-instance buffer state leaks across object destroy/respawn/pooling | A hand-maintained `Dictionary<ZDOID, SmoothState>` could silently accumulate entries for despawned objects over a long session | State is a `MonoBehaviour` co-located with `ZSyncTransform`, tied to Unity's own destroy semantics — a memory-growth risk if built wrong, never a data-loss risk, but closed by construction rather than by a hand-rolled purge. |
| 3 | C1: a legitimate large teleport (portal, respawn, server-forced reposition) gets smeared across the playback delay instead of snapping | Buffered interpolation blindly interpolates between two samples that happen to straddle a teleport | **RED-TEAM FIX (RT1 major #3)**: reuse vanilla's own already-tuned 5 m distance threshold (`CLIENT:88012`/`SERVER:87764`), but check it at **ingest time between the two raw ZDO samples**, not at render time between the current render and `TryInterpolate`'s blended output. The original guard compared the render against the interpolation output, which can never see the full jump when it's spread smoothly across the buffered interval — corrected in `SmoothState.Ingest`, see H-C1. |
| 3b | C1: `SmoothState` buffer goes stale across a long parented excursion (sitting, riding a ship) and smears on re-entry | Guard 4's early `return true` precedes `state.Ingest(zdo)`, so the buffer is never updated while parented; un-parenting resumes interpolation against a potentially very old sample | **RED-TEAM FIX (RT1 minor)**: `state.NoteSuspended()` on both entry into and exit from the parented branch (H-C1 Guard 4) marks the buffer as needing a fresh single sample before interpolating again — same discipline as the teleport reset (#3), and any large jump across the excursion is additionally caught by #3's own ingest-time check. |
| 4 | C1: the parented/character-parent-sync branch is silently bypassed | A naive "buffer ready → take over" prefix would replace vanilla's parent-relative computation (another player on a bench, bed, or moving ship) with plain absolute-position interpolation | Guard 4: re-check `m_characterParentSync && zdo.HasOwner() && !connId.IsNone()` every tick, exactly mirroring `CLIENT:87924-87927`/`SERVER:87676-87679`, and defer to vanilla when true. |
| 5 | C1: rotation takeover partially blends instead of fully replacing | `ClientSync`'s own slerp (`CLIENT:88076`/`SERVER:87828`, **RED-TEAM FIX RT2 minor**: corrected from a previously-cited `SERVER:87826`, the guarding `if` line one above it) is gated on `SyncPosition`'s own `usedLocalRotation` out-parameter, not on the prefix's return value | Guard 5: the prefix must explicitly write `usedLocalRotation = true` through the `ref` parameter whenever it takes over rendering, or vanilla's slerp still runs afterward and blends the buffered rotation 50% toward the raw current-tick ZDO rotation — not a data-loss bug (local-render-only) but silently defeats the design's own stated benefit. |
| 6 | C1: cross-client rendering "disagreement" | Two clients render slightly different instantaneous positions for the same non-owned ZDO | Not a hazard — hit detection is driven by the attacker's own collider (`Humanoid.OnAttackTrigger`, owner-gated) and damage resolves on the target's own owner (`Character.RPC_Damage`), never by a third-party observer's screen (`05-gameplay-under-server-authority.md`, independently re-confirmed in `research/11`). Every client, modded or not, already renders a slightly different vanilla lerp target today; this design widens that existing, already-harmless gap by at most the playback delay. |
| 7 | C1 running on the dedicated server process | The server also runs the exact same non-owner `ClientSync` path for ZDOs it doesn't own (e.g. connected players' own Characters) | Inline dedicated-server check at the top of H-C1's prefix (folded-in C5). Not a correctness risk even unguarded (still read-ZDO/write-local-Transform only) — a wasted-cycles nit, cheap to close. |
| 8 | D1: decompression or shared-parse-body throws on an intact-but-buggy buffer | A Mono `GZipStream` edge case, or a schema/framing bug in the new envelope reached *after* `Decompress()` returns successfully | Guard 6: the whole handler body (decompress + shared parse call) is inside one `catch (Exception)`, not just `Decompress()`. **RED-TEAM FIX (RT2 minor)**: corrected consequence — without this guard an `EndOfStreamException` from the shared parse body does not cleanly disconnect the peer; `ZRpc.Update`'s catch (`SERVER:83887-83897`) returns `ErrorCode.IncompatibleVersion`, and `ZNet.UpdatePeers` (`SERVER:80458-80491`) sets the `private static` field `m_connectionStatus = ConnectionStatus.ErrorVersion` (`SERVER:79138`) with **no** `Disconnect()` call in that branch — a shared, process-wide status flag flips while the connection itself stays open and keeps re-throwing every tick. Guard 6 remains mandatory either way. |
| 9 | D1: decompression "succeeds" but produces bytes for the wrong schema | GZip's CRC/header check catches corrupt *streams*, not schema-mismatched-but-structurally-valid streams from a different mod build | Explicit integer schema version exchanged and checked *before* attempting parse, never inferred from "decompression didn't throw." |
| 10 | D1: sender marks a ZDO "sent to peer" before the peer actually applied it | Vanilla's own optimistic behavior (`peer.m_zdos[...] = ...` at send time, `SERVER:77111`, confirmed this pass) is normally safe only because Steam/TCP give reliable, checksummed, in-order delivery straight into a fixed-offset parser with no intermediate decode step that can fail. Compression *adds* exactly that step. | Guard 3: this design does not inherit the optimistic mark — it defers the `m_zdos` write until an application-level `NMD_CAP` ack confirms the shared parse body finished without throwing. |
| 11 | D1: redundant resend loop under real RTT | `CreateSyncList` rescans unconditionally every round (`SERVER:77259-77271`) and `ShouldSend` has no "in-flight, awaiting ack" state on its own — without a guard, every unacked ZDO is reselected and recompressed every round until ack, for as many rounds as the connection's RTT spans | Guard 7 (H-D1c): a per-connection in-flight set, keyed by ZDOID and tagged with the batch sequence, consulted from `ShouldSend`'s own postfix; dropped from the set (without marking known) on ack timeout so the next round reselects exactly once, not every round in between. |
| 12 | D1: reconnect mid-flight (connection drops after some `m_zdos` writes are deferred but before ack) | The peer object is torn down before its in-flight bookkeeping resolves | Not a hazard — `AddPeer`/`RemovePeer` (confirmed `SERVER:76814-76824`, re-read this pass: `AddPeer` allocates a brand-new `ZDOPeer`, and therefore a brand-new empty `m_zdos`, on every call) already discards the entire peer state on disconnect; the new connection gets a fresh handshake and a full resync via vanilla's own never-sent-to-this-peer logic. No bespoke reconnect code needed. |
| 13 | D1: kill switch disabled mid-session, with connections already compression-confirmed and batches in flight | Simply stopping future negotiation (H-D1a) leaves already-confirmed peers stuck advertising/expecting `NMD_ZDOC`, and guard 7's in-flight set could otherwise silently withhold ZDOs from `ShouldSend` forever | The `Features.ZdoCompression.Active` setting-changed handler must actively, for every currently-confirmed peer: (a) mark the connection permanently downgraded (same code path as a version-mismatch downgrade — no special case), and (b) clear that connection's in-flight set so `ShouldSend`'s postfix stops suppressing anything. This mirrors `03-ownership-and-handover.md`'s own P4/`G6-N3` rule that a kill switch **must actively flush** held state, not just stop granting new holds. |
| 14 | D1: RPC name hash collision with another mod's own custom RPC | `ZRpc.Register<T>` does an unconditional `Remove` before `Add` (confirmed `SERVER:83981-83986` — the generic `Register<T>(string, Action<ZRpc,T>)` overload this design uses), so a 32-bit name-hash collision is a **silent last-write-wins overwrite**, never an exception | Long, project-namespaced RPC name strings (`"NoodlesMcDoodles.NMD_ZDOC"`/`"NoodlesMcDoodles.NMD_CAP"`), matching this project's existing `G1-N1` guard. |
| 15 | D1: two mods both patching `SendZDOs`/`RPC_PeerInfo`/`ShouldSend` | Harmony patch-ordering collisions with another networking-tuning mod (a real, already-documented risk class for this project's `Q1`/`Q2`/`Q3` levers on the same methods) | Prefer fail-closed patterns (log + skip, never throw, never leave `__result` in an inconsistent state) matching this project's existing guard style; a startup `Harmony.GetPatchInfo` check on these three methods, logged loudly, not silently stacked. |
| 16 | D3 (field-level delta), rejected | A sender/receiver baseline divergence (dropped delta, apply-order bug, schema skew between two mod builds) can be silently promoted to permanent, world-wide, persisted authoritative state the next time ownership changes hands, because whichever peer currently owns a ZDO becomes the sole future source of truth for it (`SetOwnerInternal`/`OwnerRevision`, confirmed in H-D1d's shared parse body). A per-ZDO content hash cannot catch a bug that corrupts the sender's own baseline before the hash is computed — sender and receiver would both "agree" on a wrong hash. | Not built. This is why D1 is whole-batch compression only, never a diff. |
| 17 | **[NEW, RED-TEAM FIX — RT1 blocker #1]** H-D1b's Prefix throws | `SendZDOs` is called from `SendZDOToPeers2` (SERVER:76884-76907), called from `ZDOMan.Update` (SERVER:76859-76868), called directly from `ZNet.Update()` immediately before `UpdateSave()` (SERVER:80090-80091, no try/catch between them, both re-confirmed this pass) — the only call chain in this entire module with no enclosing try/catch anywhere above this hook. An uncaught exception here skips `SendZDOToPeers2`'s own `m_nextSendPeer++`/wraparound (SERVER:76904-76907), freezing the round-robin on this peer forever, and skips `UpdateSave()` for every frame this keeps recurring — Unity's per-frame message dispatch logs the exception and moves on, so the *same* frozen peer throws again next frame, indefinitely, with no self-heal path (`RemovePeer` never resets `m_nextSendPeer`). Net effect: the server silently stops saving the world and stops syncing ZDOs to **every** connected peer until a manual restart — a direct, mechanical path to violating the project's own zero-data-loss rule. | Guard 8 (H-D1b): wrap the entire Prefix body in `try { ... } catch (Exception ex) { Telemetry.ZdoCompression.SendException(ex); return true; }`, matching Guard 1/Guard 6's fail-open pattern elsewhere in this module. |
| 18 | **[NEW, RED-TEAM FIX — RT1 blocker #2]** D1's compressed-send path reproduces vanilla's unguarded `peer.m_invalidSector` write, and structurally cannot inherit `01-transport-and-queues.md`'s own G2-N1 fix for it | Vanilla writes the peer's entire `m_invalidSector` set (count + N raw ZDOIDs) into the package **before** the per-item budget check even starts (SERVER:77075-77083/CLIENT:77217-77225, unconditional, re-confirmed this pass) — the exact hazard `01`'s Hook 6 (M1/Phase A) already builds a chunking fix for. But that fix is a **transpiler** that edits vanilla `SendZDOs`' own IL; H-D1b's Prefix `return false`s for every compression-confirmed peer, which makes Harmony **skip the transpiled original method body outright** — so Hook 6's own G2-N1/Q12 guard, which lives only inside that body, structurally never runs for a single compression-confirmed peer no matter which patch ships first. A mass ownership-handover sweep (O6/O7/O9/G3-L1) queuing tens of thousands of `m_invalidSector` entries for a confirmed peer could still overflow the 512 KiB Steam reliable-message ceiling (research/10-LEVERS.md line 137's already-documented boot-loop) through this path. | `BuildRawBatch` calls the **same** `SharedBudget` running-total chunking helper `01`'s Hook 6 uses (not an independent reimplementation), plus an explicit hard-ceiling check on the compressed envelope right before the RPC invoke (skip-and-log, never truncate). D1 (Phase C3) is now a hard prerequisite on `SharedBudget` existing and being exposed for reuse from `01`'s Hook 6 — see H-D1b and Interactions. |
| 19 | **[NEW, RED-TEAM FIX — RT1 major #4]** D1's in-flight/confirmation state is keyed by `ZDOMan.ZDOPeer` instance, and was never cross-checked against this project's own already-tracked G1-L1 finding | `ZDOMan.AddPeer` allocates a brand-new `ZDOPeer` with no duplicate check (SERVER:76814-76820, matches ground-truth fact #5/G1-L1). If a duplicate `RPC_PeerInfo`/`AddPeer` firing ever slipped past G1-L1's own guard, the same connection would end up with two independent `ZDOPeer` wrappers, and D1's `TrackInFlight`/`IsConfirmed`/`IsInFlightUnacked` state (keyed per-`ZDOPeer` instance, per H-D1b/H-D1c) would fragment across them, permanently doubling that connection's bandwidth/CPU cost. | G1-L1 already ships in `02-safety-rails-and-ops.md`'s **A1 (Foundation)** milestone, strictly before Phase C3 — the same "hard prerequisite" treatment `03-ownership-and-handover.md` already gives G1-L1 for its own uid-keyed mechanisms (see its own Red-team reconciliation, RT1 major #4). Cross-referenced explicitly here rather than redesigning D1's keying, since A1 already closes the precondition before D1 can ever run. See Interactions. |

**RED-TEAM REJECTED (RT2 major #2, partially)**: the Wonderland `ZRpc.HandlePackage` risk is real (see H-D1a's
"Open pre-flight dependency" note and Open question #8) but is not unique to D1 — `02-safety-rails-and-ops.md`'s
own G1-N1/P5 handshake rests on the identical vanilla-only "silent no-op on unknown hash" assumption and carries
the same open gap. Not added as its own Data-loss row here because it cannot corrupt, lose, roll back, or
duplicate any data — the failure mode is "the capability silently never negotiates," which degrades D1 to
permanently-plain-`ZDOData` for that connection, not a corruption/loss path. Tracked as a pre-flight blocker
instead.

## Vanilla and version compatibility

- **Vanilla client + modded server.** C1: no effect — a vanilla client has none of this code and is
  completely unaffected by anything the server does. D1: fully unaffected — the vanilla peer never answers
  `NMD_CAP` (the name is unknown to it, silently ignored per `ZRpc.HandlePackage`'s no-op-on-miss, confirmed
  `SERVER:83952-83971`), so the connection never becomes compression-confirmed and gets plain `ZDOData` exactly
  as vanilla ships it today. Zero wire-format change is visible to it at any layer.
- **Modded client + vanilla server.** C1: this is the design's entire premise — it works unmodified against a
  fully vanilla server, since it only reads ZDO fields vanilla already replicates in the format vanilla already
  sends them; no server-side change is required for a modded client to benefit. D1: symmetric to the case
  above — the modded side's `NMD_CAP` hello goes unanswered, same plain-`ZDOData` fallback.
- **Modded version skew (two modded peers on different builds of this mod).** C1: no interop hazard at all —
  each client's smoothing is purely local rendering, reads nothing another client wrote, writes nothing any
  other client or the server ever reads; two peers can run different playback delays with zero interaction.
  D1: the explicit integer schema version (not a boolean "I support compression" flag, per the `G1-N1` guard
  this project already committed to) fails closed to plain `ZDOData` on any mismatch — this is exactly why an
  int, not a bool, is load-bearing here.
- **PlayFab/crossplay sockets.** C1: `ZSyncTransform`'s mechanism operates purely on already-deserialized ZDO
  fields, well above the transport layer — no PlayFab-specific interaction exists. D1: `ZPlayFabSocket` already
  compresses every outgoing byte once `VersionMatch()` fires (confirmed `SERVER:91578-91580`, re-read this
  pass: `m_useCompression = true`, nothing else) — Guard 4 type-checks the peer's socket and never advertises
  `NMD_CAP`/negotiates on a `ZPlayFabSocket` connection at all, leaving crossplay on vanilla behavior (which is
  already compressed) unconditionally, rather than stacking a second, likely-useless GZip pass on
  already-compressed bytes. **RED-TEAM FIX (RT2 minor)**: this claim was previously false against the sketch —
  the socket check existed only in H-D1b (send-side), not H-D1a (negotiation), so a `ZPlayFabSocket` peer would
  still complete the `NMD_CAP` handshake even though D1 would never actually send it compressed data. Guard 4
  is now present in both H-D1a and H-D1b (see H-D1a's code sketch), so the claim is accurate as written.
- **Dedicated-server install of this same mod (both C\* and D1 run on both client and server builds of one
  DLL).** C1/C2 are gated inert on `ZNet.instance.IsDedicated()` (confirmed `CLIENT:81380`/`SERVER:81194`,
  identical signature both assemblies) — a cleanliness/perf guard, not a correctness one, since the patched
  code is inert-safe even if it somehow ran there (no ZDO write path exists either way). D1 runs on **both**
  processes by design — the server compresses its outbound batches to every confirmed peer, and a confirmed
  client compresses its own owned-ZDO batches back to the server, symmetrically, over the same negotiated
  connection.

## Configuration

All server-authoritative keys are pushed to modded clients over the shared infrastructure contract's namespaced
RPC (no ServerSync). C1's keys are client-local by design (see Compatibility — each client's playback delay is
a purely local rendering choice with zero cross-client interaction), so they are **not** server-pushed.

| Key | Default | Range | Server-synced? |
|---|---|---|---|
| `ClientSmoothing.Active` | `false` (opt-in until field-tested) | bool | local kill switch, console-command-settable |
| `ClientSmoothing.PlaybackDelayMs` | `100` ms | 0–300 ms | local only — taste knob, not correctness |
| `ClientSmoothing.ExtrapolationCapMs` (C3) | `2000` ms (byte-identical to vanilla's own cap) | 500–4000 ms, recommend not tuning until `T8` gives a real send-round figure | local only |
| `ZdoCompression.Active` | `false` (opt-in until D2's live CPU/RTT measurement passes) | bool | server-authoritative — a client only advertises the capability if the server also has it on, so a single flag flip on the server can dark-launch or fully disable the feature cluster-wide |
| `ZdoCompression.AckTimeoutMs` | `3000` ms (generous — Steam/TCP/PlayFab all already guarantee in-order delivery, so a healthy ack round-trip is near-free; this timeout is only meant to fire on a genuine parse-layer bug) | 1000–10000 ms | server-authoritative |
| `ZdoCompression.SchemaVersion` | `1` | fixed, build-time constant, not exposed as a config key | n/a |

## Telemetry

Per `NoodlesMcDoodles.Core.Telemetry` conventions, periodic + per-peer where relevant. NoodlesProbe (the
separate, standalone client-only diagnostic plugin already built for this project) independently corroborates
some of these from outside the mod — its frame-time/GC columns cross-check C1's CPU cost, and its
send-queue/byte columns cross-check D1's bandwidth reduction — but it is not this module's telemetry system and
is not a dependency of it.

- **`smoothing.takeovers`** — count of ticks where C1's buffer was ready and took over rendering (proves the
  patch is doing something, not dead code).
- **`smoothing.fallback.{noSamples, exception, teleport, parented}`** — per-reason fallback/takeover counts.
  A healthy steady state is mostly `noSamples` (new spawns) with `exception` at ~0; a nonzero, sustained
  `exception` rate is the signal something in guard 1 is being exercised for real, not just as a safety net.
  **RED-TEAM FIX (RT1 major #3)**: `teleport` now fires from Guard 3's ingest-time raw-sample comparison
  (a same-tick snap-and-takeover, `return false`), not from a post-hoc comparison of the render against
  `TryInterpolate`'s output — see H-C1 and Data-loss #3.
- **`smoothing.rotationTakeovers`** — count of ticks that wrote `usedLocalRotation = true` — the direct,
  auditable proof guard 5 executed, not merely present in code.
- **`compression.confirmedPeers`** — current count of connections that completed the `NMD_CAP` handshake.
- **`compression.batchesSent` / `compression.bytesRaw` / `compression.bytesCompressed`** — the direct
  bandwidth-reduction proof this design's whole benefit rests on; also the input D2's live test reads.
- **`compression.sendCpuMsPerBatch`** / **`compression.recvCpuMsPerTick`** — **RED-TEAM FIX (RT2 major #3)**:
  split from a single `compression.cpuMsPerBatch` into two distinctly-scoped measurements, because the two
  sides have structurally different frequency profiles, re-confirmed this pass. Send-side compression is
  throttled by `SendZDOToPeers2`'s round-robin — exactly one peer's `SendZDOs` runs per `ZDOMan.Update` call
  (`SERVER:76884-76907`: `SendZDOs(m_peers[m_nextSendPeer], flush:false); m_nextSendPeer++;`) — so its CPU
  cost is O(1) per tick regardless of connected-peer count, and a per-batch average is the right unit for it.
  Receive-side decompression is **not** round-robined: `ZNet.UpdatePeers` (`SERVER:80458-80491`) calls
  `item.m_rpc.Update(dt)` unconditionally for every connected peer every tick, and `ZRpc.Update`'s own
  `for (zPackage = m_socket.Recv(); ...)` loop (`SERVER:83880-83899`) drains each peer's entire receive queue
  synchronously on the main thread — so aggregate decompress cost scales with the number of peers that have a
  pending message *in that same tick*, which a per-batch average cannot show. `compression.recvCpuMsPerTick`
  sums every decompression stopwatch reading that lands in the same `Update()` call, so D2's live test can see
  the real worst-case single-frame cost at 10-20 concurrent Proxima Maxi players, not just an average that a
  low-peer-count test session would understate.
- **`compression.decompressOrParseExceptions`** — should be ~0 in steady state; any nonzero value is a
  zero-data-loss audit flag (guard 6 firing for real), not silently absorbed.
- **`compression.inFlightUnacked`** (gauge) / **`compression.ackTimeouts`** (counter) — the direct proof guard
  7's in-flight suppression is doing real work; a sustained high `inFlightUnacked` count at a given peer's RTT
  is the live signal that peer's connection is too slow for this channel to help, informing whether to
  auto-downgrade it (not built in Phase C3 — flagged as an open question).
- **`compression.downgrades.{versionMismatch, playFabExempt, killSwitch}`** — per-cause downgrade counts, the
  same "prove the guard fired, don't assume it" pattern `03-ownership-and-handover.md` already uses for its own
  kill-switch telemetry.
- **`compression.sendException`** — **[NEW, RED-TEAM FIX — RT1 blocker #1]** count of times Guard 8's outer
  try/catch caught an exception in H-D1b's Prefix and fell open to vanilla `SendZDOs` for that tick. Must stay
  at ~0 in steady state; any sustained nonzero rate is a critical audit flag (the fail-open path itself
  functions, but its root cause needs fixing) — surfaced with the same urgency as `smoothing.fallback.exception`.
- **`compression.oversizedEnvelopeSkipped`** — **[NEW, RED-TEAM FIX — RT1 blocker #2]** count of times the
  compressed-envelope hard-ceiling check in H-D1b skipped a round rather than risk the 512 KiB reliable-message
  boot-loop. Expected ~0 with `SharedBudget`'s own running-total guard working correctly; a nonzero value means
  `SharedBudget`'s chunking undershot the real ceiling for this batch and self-corrects next round, but is a
  signal worth watching during the mass-ownership-handover test (Test plan #14).

## Interactions with other subsystems

- **Transport (01).** D1's send-side hook (H-D1b) reads the *same live* `Q3`/`Q5` budget config Transport
  already owns rather than hardcoding a second `10240` — if Phase A's numbers move, D1 moves with them
  automatically (`couples: Q3` in the lever catalog). `Q6`'s send-priority reweighting (cross-referenced here
  as C4, already `Phase A`) is the single biggest lever for shrinking the *source* jitter C1's buffer has to
  hide — the smaller and more regular the gap between real updates, the shorter C1's playback delay needs to
  be to stay smooth. Neither C1 nor D1 changes anything about `T1`/`T2`'s Steam rate ceilings or `Q1`/`Q2`'s
  round-start/peer-rotation cadence. **RED-TEAM FIX (RT1 blocker #2, new hard dependency)**: D1 additionally
  depends on `01`'s Hook 6 (`SharedBudget`, Q3/Q4/Q5/Q7/Q12/G2-N1's unified `SendZDOs` rewrite, M1/Phase A) for
  more than config values now — `BuildRawBatch` calls `SharedBudget`'s own running-total chunking helper
  directly, because H-D1b's Prefix `return false`s for confirmed peers and therefore skips the transpiled
  vanilla method body Hook 6's guard lives inside. D1 (Phase C3) cannot ship, even dark, before `SharedBudget`
  is factored out for reuse outside Hook 6's own transpiler. See Data-loss #18 and H-D1b.
- **Safety rails (02) / G1-L1.** `02`'s A1 (Foundation) milestone ships G1-L1 (the `RPC_PeerInfo`/`AddPeer`
  duplicate-connection guard) before Phase C3. D1's in-flight/confirmation state, keyed by `ZDOMan.ZDOPeer`
  instance, relies on G1-L1 already having landed to guarantee one `ZDOPeer` per real connection — the same
  hard-prerequisite treatment `03-ownership-and-handover.md` gives G1-L1 for its own uid-keyed mechanisms. See
  Data-loss #19. (**RED-TEAM FIX, RT1 major #4.**)
- **Rails (shared infrastructure contract).** D1's negotiation reuses the exact `G1-N1` pattern (namespaced
  RPC names, explicit int protocol version, silent-no-op fallback for non-participants) rather than inventing
  a second handshake mechanism; `P5`'s already-confirmed "mixed vanilla/modded installs are silent-safe"
  finding (`ZRpc.HandlePackage`/`ZRoutedRpc.HandleRoutedRPC` both plain `TryGetValue`-and-no-op on an
  unregistered hash) is the structural fact D1's entire vanilla-peer compatibility story rests on **for a
  vanilla peer**. Both C1 and D1 are independently gated behind their own `Features.*.Active` flags, per the
  contract. **RED-TEAM FIX (RT2 major #2)**: that "silent-safe" story is vanilla-only. The live server also
  runs the user's own `Wubarrk-Wonderland` mod, whose `Security.ModEnforcementPatches` installs a
  skip-capable Prefix on this exact `ZRpc.HandlePackage` method (`research/13-mod-compat-census.md` §2/
  conflict-list #2) — if it's an RPC-name whitelist, as its shape and its sibling guards on
  `ZRoutedRpc.RouteRPC`/`HandleRoutedRPC`/`ZNet.RPC_ServerSyncedPlayerData` suggest, D1's `NMD_CAP` handshake
  (and `02`'s own G1-N1 `Hello`/`ConfigSync` handshake, same open gap, not this doc's to fix alone) could be
  silently dropped before ever reaching vanilla's unknown-hash fallback. Not a data-loss path (see Data-loss
  table note above the row list) but a hard pre-flight blocker for flipping `ZdoCompression.Active` on Proxima
  Maxi: verify Wonderland's actual guard logic before enabling D1 there. See H-D1a and Open question #8.
- **Ownership and handover (03).** This is the actual motivation for building C1 now: once `03`'s `G3-L1`/`O6`
  make the server the default owner of nearly every creature in a server-held zone, "non-owner interpolation"
  stops being the occasional case vanilla was tuned for and becomes the default rendering path for essentially
  all wildlife on every connected client. A plain `SetOwner`-only change (an `O6`/`O7`/`O9` handover with no
  position change) bumps only `OwnerRevision`, not `DataRevision` or position — C1's buffer is unaffected by
  ownership churn by itself; it only reacts when the new owner's own next position update actually arrives.
  D1 is directly affected by the *volume* `03`'s ownership churn adds: every `SetOwner` call bumps
  `OwnerRevision`, which forces a resend to every peer via `ShouldSend` — `03`'s own telemetry already counts
  this churn; D1's `compression.bytesRaw` counter measures the same stream after that churn, not instead of it.
- **Server zone simulation (04).** `04`'s `Z2`/`Z3` levers (populating a server-held zone with real terrain and
  real object instances for every nearby peer) are what actually create the volume of server-owned creature
  ZDOs C1 and D1 exist to help with — without `04` shipped, a server-held zone has few or no objects in it for
  C1 to smooth or D1 to compress.
- **Gameplay under server authority (05).** `05`'s own finding (server ownership removes vanilla's zero-hop
  `RPC_Damage` case, so every hit against a server-owned creature pays one mandatory attacker→server RPC hop)
  is a combat-feel/hit-latency question, independent of and not fixable by anything in this module — restated
  here only because `research/11` derived it while researching the same code paths.
- **Other mods.** `ZSyncTransform.ClientSync`/`SyncPosition` are exactly the kind of method a "smooth
  movement"-style Thunderstore mod might already target — **[unverified]**, no specific conflicting mod was
  checked against the live server's actual plugin set (`/home/rohan/ArchShare/ProximaMaxi/BepInEx/plugins/`)
  in this pass. `ZDOMan.SendZDOs`/`RPC_PeerInfo` carry the same already-documented `Q1`/`Q2`/`Q3` collision risk
  class this project's catalog already tracks. Mitigation for both: Harmony patch-priority ordering plus a
  startup `Harmony.GetPatchInfo` check that logs loudly and prefers disabling this project's own patch over
  silently double-patching — same discipline `03-ownership-and-handover.md` already applies to its own
  `SetOwner` collision risk with `GetOffMyLawn`. A targeted decompile of the live server's ~47 installed plugins
  for `ZSyncTransform`/`ZDOMan`/`ZRpc` patches, not just a name search, is recommended before enabling either
  feature against Proxima Maxi (same recommendation `03` already makes for its own hooks).

## Test plan

**Testbed (single dedicated server + one or two real clients, per
`libs-Tools/DEDICATED-SERVER-TESTBED/run-linux-1.0-server.sh`):**

1. **C1 sanity — vanilla-equivalence when disabled.** With `ClientSmoothing.Active=false`, confirm rendering
   is pixel-for-pixel unchanged from an unpatched client (record a short clip of a server-owned mob before/after
   installing the mod, flag off).
2. **C1 basic smoothing.** Enable the flag, stand near several server-owned mobs (requires `03`'s `G3-L1`/`O6`
   also enabled on the testbed), confirm no visible rubber-banding versus vanilla's 0.2 lerp; verify
   `smoothing.takeovers` climbs and `smoothing.fallback.exception` stays at 0.
3. **C1 teleport safety.** Force a large reposition (portal use, `tp`-style console teleport if available, or
   a forced respawn) on a server-owned object; confirm it snaps instantly (no smear) and
   `smoothing.fallback.teleport`/the 5 m-guard path fires, not a stretched interpolation.
4. **C1 parented-object safety.** Have a player sit on a bench/chair near server-owned zone content; confirm
   rendering matches vanilla exactly (guard 4's fallback should fire every tick for that object,
   `smoothing.fallback.parented` counting up).
5. **C1 kill switch mid-session.** Toggle `ClientSmoothing.Active` off while several server-owned mobs are
   actively being smoothed; confirm an immediate, clean return to vanilla behavior with no stuck/frozen
   transforms.
6. **D1 negotiation.** Connect one modded and one vanilla client to a modded server with
   `ZdoCompression.Active=true`; confirm `compression.confirmedPeers` counts only the modded connection, and
   the vanilla client's session is otherwise indistinguishable from a fully-vanilla connection.
7. **D1 basic round-trip.** With two modded peers confirmed, confirm `compression.batchesSent` and
   `compression.bytesCompressed < compression.bytesRaw` climb, and `compression.decompressOrParseExceptions`
   stays at 0 over a normal play session.
8. **D1 induced-fault pass.** Use `tc`/`netem` (or the testbed's own latency injection if available) to add
   ~150 ms RTT to one connection; confirm `compression.inFlightUnacked` rises but does not grow unbounded, and
   `compression.ackTimeouts` stays low — the direct test of guard 7's redundant-resend fix.
9. **D1 reconnect mid-flight.** Kill the network connection to a modded client mid-batch (before an ack lands),
   reconnect; confirm a clean full resync with no stuck ZDOs and no duplicate/missing world state.
10. **D1 kill switch mid-session.** Toggle `ZdoCompression.Active` off on the server while connections are
    actively compressing; confirm `compression.downgrades.killSwitch` fires for every previously-confirmed
    peer and traffic falls back to plain `ZDOData` within one round, not gradually.

**Live test with NoodlesProbe / telemetry, ideally with the Arizona admin present (per `HANDOFF.md`'s standing
next-step plan):**

11. Run one session with C1 enabled and `03`'s ownership subsystem live, near the Arizona admin; compare
    subjective smoothness of nearby server-owned wildlife against the two solo baseline sessions already
    captured in `baseline/2026-09-28-probe-solo/`, and record `smoothing.takeovers` vs. `smoothing.fallback.*`
    ratios as an objective proxy.
12. This is **D2** — run with `ZdoCompression.Active=true` between the server and at least one modded client
    at real (non-LAN) Arizona↔New Jersey RTT; read `compression.sendCpuMsPerBatch`,
    `compression.recvCpuMsPerTick` (**RED-TEAM FIX, RT2 major #3** — read with as many concurrent
    compression-confirmed peers online as the test can gather, not just one, since receive-side cost scales
    with peers-with-pending-data-per-tick, not with batch size), and `compression.bytesRaw`/`bytesCompressed`
    off live telemetry; use this, not a LAN rig, to decide whether `ZdoCompression.Active`'s *default* should
    flip to `true` for modded-modded pairs.
13. Cross-check NoodlesProbe's own send-queue/byte columns against `compression.bytesCompressed` on the same
    session as an independent corroboration from outside the mod's own counters.
14. **[NEW, RED-TEAM FIX — RT1 blocker #2]** Mass ownership-handover / large `invalidSector` burst. On the
    testbed with `03`'s ownership subsystem and `01`'s Hook 6 (`SharedBudget`) both live, force a server-side
    ownership sweep across a zone with hundreds of objects (an `O6`/`O7` handover) while at least one peer is
    compression-confirmed; confirm no oversized-envelope disconnect/boot-loop, `compression.oversizedEnvelopeSkipped`
    (if any) stays low and self-corrects over subsequent rounds, and no ghost/duplicate objects appear on that
    peer afterward. This scenario was previously untested by this plan (steps 6-10 cover negotiation, basic
    round-trip, induced RTT, reconnect, and kill-switch, but never a large-`invalidSector` burst).
15. **[NEW, RED-TEAM FIX — RT2 major #2]** Wonderland compatibility pre-flight. Before enabling `ZdoCompression.Active`
    on Proxima Maxi specifically: read `Wubarrk-Wonderland`'s `Security.ModEnforcementPatches/Patch_ZRpc_HandlePackage`
    guard logic directly (source available, it's the user's own mod), then connect a modded client to a
    Wonderland-enabled test server and confirm `compression.confirmedPeers` actually increments — do not assume
    P5's vanilla-only "silent no-op" argument extends to a modded `HandlePackage` prefix.

## Open questions

1. **Real measured update-arrival jitter for a server-owned creature at this project's target peer count**
   (needed to pick a sensible default `ClientSmoothing.PlaybackDelayMs` — too short and the buffer frequently
   falls back to raw extrapolation/snap, too long and observing other entities gets noticeably laggier). Not
   resolvable from the decompile; flagged `NEEDS-LIVE-TEST` in `research/02` and inherited unchanged here.
2. **CPU cost of C1's per-instance interpolation buffer at scale** (many concurrent server-owned creatures on
   one client) — expected negligible (two samples + one timestamp per instance, cheap math) but unmeasured
   against a real busy scene.
3. **Whether C2 (kinematic/vehicle smoothing) is worth building at all**, and if so, transpiler vs. full-
   `ClientSync`-prefix — deferred pending live data on whether wildlife-only smoothing (C1) already satisfies
   the Arizona-case goal for the common case.
4. **D2's exact numeric threshold for flipping `ZdoCompression.Active`'s default** — this doc recommends
   building the code now and gating the *default* on D2's live measurement, but does not pre-commit to a
   specific compression-ratio or CPU-ms threshold; that number should come from the live test, not be guessed
   here.
5. **Whether a chronically-high-RTT connection should auto-downgrade out of D1** (rather than just tolerating
   guard 7's bounded resend overhead indefinitely) — not designed in this pass; `compression.inFlightUnacked`'s
   telemetry is what would inform whether this is worth building later.
6. **Third-party mod census for `ZSyncTransform`/`ZDOMan.SendZDOs`/`RPC_PeerInfo`/`ShouldSend` patches** beyond
   a generic name search of the live server's ~47 installed plugins — the `Harmony.GetPatchInfo` startup check
   catches this at runtime, but a pre-flight static check (decompiling the actual installed plugin set) would
   catch it before either feature ships to Proxima Maxi, mirroring the same open item `03-ownership-and-
   handover.md` already carries for its own `SetOwner` hook.
7. **Whether `BepInEx.AssemblyPublicizer` exposes `ZDOMan.ZDOPeer`'s own type accessibility**, not just its
   members, at the version this project's build already pins — a one-line compile-time check before H-D1b/
   H-D1c are implemented; if it doesn't, `AccessTools.TypeByName("ZDOMan+ZDOPeer")` is the documented fallback
   (see H-D1b).
8. **[NEW, RED-TEAM FIX — RT2 major #2]** Whether `Wubarrk-Wonderland`'s `Security.ModEnforcementPatches`
   skip-capable prefix on `ZRpc.HandlePackage` (`research/13-mod-compat-census.md` §2/conflict-list #2) would
   drop `NMD_CAP`/`NMD_ZDOC`/`Hello` before they reach vanilla's unknown-hash fallback — not resolvable from
   the two Valheim decompiles alone (Wonderland is a separate DLL, out of this doc's ground-truth scope); needs
   a direct read of Wonderland's own source/guard logic and a live test against a Wonderland-enabled server
   before `ZdoCompression.Active` ships to Proxima Maxi. Blocks the default-on flip the same way D2 does; see
   H-D1a and Test plan #15. This also affects `02-safety-rails-and-ops.md`'s own G1-N1 handshake, which rests
   on the identical vanilla-only assumption — worth resolving once for the whole shared infrastructure
   contract rather than per-doc.

## Red-team reconciliation

Every issue from both red-team passes, re-verified against the decompiles directly (not taken on either red
team's word), with its disposition. "Fixed" means the blueprint text/code above was changed in place and
marked `**RED-TEAM FIX**` at the edit site; line/section pointers below are to *this* doc post-fix.

### RED TEAM 1 (data loss)

| # | Issue | Severity | Disposition | Evidence / reasoning |
|---|---|---|---|---|
| 1 | H-D1b's `SendZDOs` Prefix has no try/catch; an exception freezes `ZDOMan`'s peer rotation and halts `UpdateSave()` for every peer until restart | Blocker | **FIXED** — H-D1b, Data-loss #17 | Confirmed exactly as claimed, both assemblies. `ZDOMan.Update` (SERVER:76859-76868/CLIENT:77001-77010) calls `SendZDOToPeers2` then `SendDestroyed()`/`UpdateStats(dt)`; `SendZDOToPeers2` (SERVER:76884-76907) only increments/wraps `m_nextSendPeer` *after* `SendZDOs` returns (SERVER:76902-76907); `ZNet.Update()` calls `m_zdoMan.Update(deltaTime); UpdateSave();` back-to-back with no intervening try/catch (SERVER:80090-80091, mirrored CLIENT:80283-80284). Unity's per-frame `MonoBehaviour.Update` dispatch catches and logs an unhandled exception but does not abort the process or roll back `m_nextSendPeer` — `RemovePeer` (SERVER:76822-76832) never resets it either — so the identical exception recurs every frame indefinitely, permanently starving world saves and ZDO sync for every peer. Added Guard 8: the entire Prefix body now wrapped in `try/catch`, fail-open to `return true`, matching Guard 1 (H-C1) and Guard 6 (H-D1d)'s existing pattern. |
| 2 | D1's compressed-send path reproduces vanilla's unguarded `peer.m_invalidSector` write, with no coordination with `01`'s own G2-N1/Q7/Q12 levers or the project's documented 512 KiB boot-loop hazard | Blocker | **FIXED, and the actual mechanism is worse than described** — H-D1b, Interactions (Transport 01), Data-loss #18 | Confirmed the unguarded write itself: `peer.m_invalidSector` (count + N raw ZDOIDs) is written into `zPackage` unconditionally at SERVER:77075-77083/CLIENT:77217-77225, strictly before the per-item budget check at SERVER:77094/CLIENT:77236 even starts — byte-identical both builds, re-confirmed this pass. Confirmed `01-transport-and-queues.md`'s Hook 6 already builds the fix (`SharedBudget`, a **transpiler** on `SendZDOs`, M1/Phase A) — but this reconciliation found a sharper problem than either red team stated: because H-D1b's Prefix `return false`s for every compression-confirmed peer, Harmony **skips the transpiled original method body outright**, so Hook 6's own guard — which lives only inside that body — structurally can never protect a compression-confirmed peer, regardless of which patch ships first or how "coordinated" the two docs claim to be. Sequencing alone (the red team's suggested fix) does not close this. Fixed by making `BuildRawBatch` call `SharedBudget`'s own running-total helper directly (one implementation, reused, not two independent copies) plus an explicit hard-ceiling check on the compressed envelope immediately before the RPC invoke. Added Test plan #14 (mass ownership-handover / large invalid-sector burst) and telemetry `compression.oversizedEnvelopeSkipped`. |
| 3 | H-C1's teleport-snap guard compares the wrong quantities (interpolated output vs. raw-sample-vs-current-render) and can smear a real teleport into a multi-frame glide | Major | **FIXED** — H-C1, Data-loss #3 | Confirmed vanilla's actual check (CLIENT:88009-88018/SERVER:87761-87764, byte-identical) always compares `base.transform.position` (last frame's committed render) against `position` (this tick's single freshly-read/extrapolated raw ZDO target) — never two buffered historical samples. The original sketch's Guard 3 instead compared the render against `TryInterpolate`'s blended output, which sweeps smoothly across a teleport spanning the buffered sample interval; any individual frame's delta of that sweep can be well under 5 m even when the true raw-to-raw displacement is large, so the `dist >= 5f` check could structurally never fire for exactly this case. Fixed by moving teleport detection into `SmoothState.Ingest` itself, comparing the new raw sample directly against the immediately preceding raw sample (mirroring vanilla's own always-current-vs-previous-raw-target semantics), and snapping immediately at ingest rather than waiting for a render-time comparison that can't see the full jump. |
| 4 | D1's in-flight/confirmation state, keyed by `ZDOMan.ZDOPeer` instance, was never cross-checked against this project's own already-tracked G1-L1 duplicate-peer-registration bug | Major | **FIXED via explicit cross-reference, not a keying redesign** — Interactions (Safety rails 02 / G1-L1), Data-loss #19 | Confirmed the premise: `ZDOMan.AddPeer` allocates a brand-new `ZDOPeer` with no existing-entry check (SERVER:76814-76820, matches ground-truth fact #5). But also confirmed `02-safety-rails-and-ops.md` already ships G1-L1's duplicate-connection guard in its **A1 (Foundation)** milestone — strictly before this doc's Phase C3 — and that `03-ownership-and-handover.md` already gives G1-L1 the identical "hard prerequisite, not a redesign" treatment for its own uid-keyed mechanisms (its own Red-team reconciliation, RT1 major #4). Following that established project pattern rather than reworking D1's `ZDOPeer`-instance keying (which is correct *given* G1-L1 has landed): added an explicit cross-reference so the dependency is on record here too, rather than rediscovered later. |
| 5 | `SmoothState` buffer goes stale across a long parented excursion (Guard 4's early return precedes `state.Ingest`) and can smear on re-entry | Minor | **FIXED** — H-C1 (Guard 4), Data-loss #3b | Confirmed the code-order gap as described: the parented branch's `return true` (doc lines, pre-fix) preceded `state.Ingest(zdo)`, so the buffer never updates while parented. Fixed with `state.NoteSuspended()` on entry to and exit from the parented branch, forcing a fresh single sample before interpolation resumes — cheap and mechanical, same discipline as the teleport reset. Any *large* jump across the excursion is additionally caught for free by finding #3's own ingest-time raw-sample comparison, once that fix landed; the remaining gap this guard closes is small-drift staleness over an unknown elapsed time, which #3's threshold alone wouldn't catch. |

### RED TEAM 2 (runtime/compat)

| # | Issue | Severity | Disposition | Evidence / reasoning |
|---|---|---|---|---|
| 1 | H-D1a's Postfix on `RPC_PeerInfo` fires on every return path (including ~6 rejection branches before `AddPeer`), not just the success path the doc's own rationale assumed; `AddPeer` citation off by one line | Major | **FIXED by retargeting the hook, not patching around the symptom** — H-D1a | Confirmed both parts by re-reading `RPC_PeerInfo` in full (SERVER:79821-79999): six early `return`s before `AddPeer` (peer-null 79827, version mismatch 79869, blacklist 79881, bad Steam ticket 79897, server full 79921, wrong password 79936), and `GetPeer(rpc)` returns non-null on all of them. Confirmed the citation error: SERVER:79989 is `m_zdoMan.AddPeer(peer);`, SERVER:79990 is `m_routedRpc.AddPeer(peer);` (doc had cited 79990). Rather than reconstructing a "did this actually succeed" check inside a `RPC_PeerInfo` postfix, retargeted the whole hook to Postfix `ZDOMan.AddPeer(ZNetPeer netPeer)` directly (SERVER:76814-76820/CLIENT:76956-76962) — confirmed this method is called *only* from `RPC_PeerInfo`'s single success path on both builds (SERVER:79989/CLIENT:80182), never from any rejection branch, giving the same `ZRpc` access with no reconstructed success logic needed. |
| 2 | The doc's entire vanilla-peer compatibility story for D1 (and the shared handshake generally) assumes `ZRpc.HandlePackage` silently no-ops on an unknown RPC hash — true for vanilla, but the live server's own `Wubarrk-Wonderland` mod carries a skip-capable prefix on that exact method that could drop the handshake first | Major | **FIXED as an explicit pre-flight blocker, not resolved outright** — H-D1a, Interactions (Rails), Open question #8, Test plan #15 | Confirmed via `research/13-mod-compat-census.md` §2/conflict-list #2 (not re-derived from a Wonderland decompile — out of this doc's Valheim-only ground-truth scope): `Wonderland.Subsystems.Security.ModEnforcementPatches/Patch_ZRpc_HandlePackage.Prefix` is skip-capable and installed server-side; the census independently already flags this as unresolved and recommends reading Wonderland's actual guard logic before shipping any negotiation RPC. Confirmed doc 06 never mentioned Wonderland/HandlePackage anywhere (grep, zero hits, pre-fix) and that `02-safety-rails-and-ops.md`'s own G1-N1 handshake rests on the identical unverified assumption — this is a project-wide gap, not specific to D1, so it cannot be fully "closed" from within this doc alone. Added as an explicit, hard pre-flight dependency (Open question #8) blocking `ZdoCompression.Active`'s default-on flip on Proxima Maxi specifically, plus a dedicated test-plan step. |
| 3 | D1's send-side (round-robined, O(1)/tick) and receive-side (unthrottled, scales with peer count) CPU costs have structurally different frequency profiles that D2's single `cpuMsPerBatch` telemetry doesn't distinguish, risking an understated live-test result at low peer counts | Major | **FIXED** — Telemetry, Test plan #12 | Confirmed both halves of the asymmetry by direct read: `SendZDOToPeers2` services exactly one peer's `SendZDOs` per `ZDOMan.Update` call (SERVER:76884-76907), so send-side compression cost is O(1)/tick regardless of peer count. `ZNet.UpdatePeers` (SERVER:80458-80491) calls `item.m_rpc.Update(dt)` unconditionally for *every* connected peer every tick, and `ZRpc.Update`'s `for (zPackage = m_socket.Recv(); ...)` loop (SERVER:83880-83899) drains each peer's entire receive queue synchronously on the main thread inside that same call — so receive-side decompress cost scales with however many peers have pending data in that one tick, with no round-robin gate. Split `compression.cpuMsPerBatch` into `compression.sendCpuMsPerBatch` (per-batch average, correct for the round-robined side) and `compression.recvCpuMsPerTick` (an aggregate-per-tick sum, correct for the unthrottled side), and required D2's live test to gather it with multiple concurrent confirmed peers online, not just one. |
| 4 | Doc's Compatibility section claims H-D1b's PlayFab socket-type check means D1 "never advertises NMD_CAP/negotiates on a ZPlayFabSocket connection at all" — but that check only existed in H-D1b (send-side), not H-D1a (negotiation) | Minor | **FIXED** — H-D1a (Guard 4 added), Compatibility (PlayFab) | Confirmed: the original H-D1a sketch registered `NMD_CAP`/`NMD_ZDOC` and sent `Hello` to every peer unconditionally, with no socket-type check — only H-D1b's `SendZDOs` prefix had `peer.m_peer.m_socket is ZPlayFabSocket`. Confirmed `ZPlayFabSocket.VersionMatch` sets `m_useCompression = true` at SERVER:91578-91580 as the doc already claimed. Fixed by adding the identical `is ZPlayFabSocket` check to H-D1a, so the Compatibility section's claim is now true of the code as written rather than aspirational. |
| 5 | Several load-bearing citations drift from the actual line (Quaternion.Slerp assignment, AddPeer call, C2's kinematic-branch range, a literal unfilled `CLIENT:...` placeholder) | Minor | **FIXED, all four** — H-C1, H-D1a, H-C2 | Re-`grep -n`'d every one directly. (a) `Quaternion.Slerp` assignment is SERVER:87828, not 87826 (87826 is the guarding `if (Quaternion.Angle(...) > 0.001f)` line); `CLIENT:88076` was already correct — corrected. (b) `m_zdoMan.AddPeer(peer)` is SERVER:79989, not 79990 (79990 is `m_routedRpc.AddPeer(peer)`) — moot as a citation-only fix since issue RT2#1 retargeted this hook to `AddPeer` itself, but corrected in the new hook's own citation. (c) C2's kinematic-only `if` block closes at CLIENT:88063/SERVER:87815 (SERVER's cited 87814 was one line short but stayed correctly scoped; CLIENT's cited 88070 overran 7 lines into the adjacent non-kinematic branch, including its `SyncPosition(...)` call) — corrected to CLIENT:88033-88063/SERVER:87785-87815. (d) the H-D1a code comment's literal `CLIENT:...` placeholder is filled in as `CLIENT:84211` (`public void Register<T>(string name, Action<ZRpc, T> f)`), re-confirmed against SERVER:83981-83986. |
| 6 | Doc's Guard 6 rationale claims a missing guard would cause "a hard disconnect" for the affected peer; the actual consequence is a `private static` field flip with no `Disconnect()` call in that branch | Minor | **FIXED** — H-D1d (Guard 6 comment), Data-loss #8 | Confirmed: `m_connectionStatus` is `private static ConnectionStatus` (SERVER:79138) — one field shared by the whole `ZNet` instance/process, not per-peer. `ZNet.UpdatePeers` (SERVER:80458-80491) sets `m_connectionStatus = ConnectionStatus.ErrorVersion` when `item.m_rpc.Update(dt)` returns `ErrorCode.IncompatibleVersion`, but does **not** call `Disconnect(item)` in that branch (the only `Disconnect(peer)` call in `UpdatePeers` is in the earlier, unrelated `!IsConnected()` loop). Corrected the rationale text to describe the real failure mode — a stuck connection that keeps re-throwing plus a shared static-field mutation, arguably worse and harder to detect via peer-count telemetry than a clean disconnect — while keeping Guard 6 itself mandatory either way. |

### Summary

Both blockers were real and are fixed in place, not deferred: H-D1b's missing exception guard (RT1#1) and the
`m_invalidSector`/G2-N1 gap (RT1#2, whose actual mechanism — a Harmony Prefix categorically skipping a
transpiled sibling patch's guard — is stricter than either red team described, and now creates an explicit,
necessary cross-doc dependency on `01-transport-and-queues.md`'s `SharedBudget`). All four major findings are
fixed: two by reworking design in place (H-C1's teleport detection, RT1#3; H-D1a's hook target, RT2#1), two by
adding explicit, actionable pre-flight/telemetry requirements rather than by resolving something this doc
cannot resolve alone (the Wonderland `HandlePackage` risk, RT2#2, and the G1-L1 cross-reference, RT1#4, both
of which the project's own established pattern already treats as hard prerequisites rather than in-doc
redesigns). All five minor findings are fixed: one small mechanical code addition (SmoothState staleness,
RT1#5; PlayFab check parity, RT2#4) and citation corrections (RT2#5, RT2#6). No issue from either red team was
rejected outright — every one was confirmed against the decompiles (or, for the two Wonderland-touching items,
against the project's own compat census, since Wonderland's own DLL is outside this doc's Valheim-decompile
ground-truth scope) and closed with either a code/design fix or an explicit, tracked dependency. Phase C1
(C1/C3/C5) and Phase C3 (D1/D2) are both buildable as revised; Phase C3 additionally now gates on
`01`'s `SharedBudget` (M1/Phase A) landing first and on the Wonderland pre-flight check (Open question #8)
before `ZdoCompression.Active` can default to `true` on Proxima Maxi — both were already implied by the
project's milestone ordering and mod-compat census, not new scope.
