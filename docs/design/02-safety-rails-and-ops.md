# Safety rails, negotiation, config sync, telemetry, saves (Phase A)

## Summary

This module is the load-bearing floor every other NoodlesMcDoodles subsystem stands on. It does five things, none of which changes gameplay by itself:

1. **Feature flags** — every behaviour anywhere in the mod (this doc's and every later doc's) is gated by a `Features.<Name>.Active` bool, so any one lever can be turned off without a restart.
2. **A kill switch** — one config bool, one admin console command, and automatic stand-down on guard trips — that returns the whole mod to exactly vanilla behaviour live.
3. **A negotiation handshake** — a namespaced custom RPC exchanged right after vanilla's own `PeerInfo` completes, so each side learns whether the other is modded, which protocol version, and which capabilities, before any cooperative feature (config sync now; ownership hand-over, smoothing, etc. later) is allowed to activate for that peer.
4. **Config sync** — the server pushes its own authoritative scalar tunables to modded clients over that same channel, without ServerSync or Jotunn.
5. **Telemetry + save/backup policy** — counters that turn "zero data loss" from a claim into log-grep evidence, plus three small, independently-useful guards around the server's own save/backup cycle (P1/P2/P3).

None of this fixes the Arizona problem by itself. What it does is make every later fix for it (G3-L1's server-presence patch, O6's ownership sweep, O9's latency-aware hand-over) safe to ship and safe to revert: the kill switch is what lets an operator undo a live ownership change without a restart, the handshake is what a later feature checks before doing anything that needs both ends modded, and the telemetry/save guards are what let the team *prove*, from logs, that none of it lost a ZDO — rather than asserting it from code reading alone.

**Milestones** (both ship together as "Phase A", split only because A2 has no reason to exist until a later phase needs it):
- **A1 — Foundation.** Features/flags framework, kill switch (config + console command + auto stand-down), telemetry framework, G1-L1 (handshake re-entry guard), P7 (counters), P1/P2/P3 (save/backup policy), P8 scenarios 1–3 (executable today, single-server testbed).
- **A2 — Negotiation.** The Hello handshake RPC and the config-sync RPC (P6), P5's confirmed-safe mixed-install behaviour documented as a non-change. A2 has no independent value until a later phase (ownership hand-over, client smoothing) needs a client-cooperative feature — but it must exist *before* that phase, so it ships here rather than being deferred piecemeal.

## Levers covered

| lever | verdict (10-LEVERS, LEAD-CORRECTED noted) | user target | final value/range recommended | milestone |
|---|---|---|---|---|
| G1-L1 | SAFE | selected, no numeric target (binary) | Prefix on `ZNet.RPC_PeerInfo`, guard keyed on `peer.IsReady()` (peer identity), not on the resent `uid` value — see Hooks/Data-loss for why the by-value check vanilla already has (`IsConnected(uid)`) is not sufficient by itself | A1 |
| P1 | judge SAFE → **downgraded to SAFE-WITH-GUARDS** by lead dispute (no engine-side floor protects a direct field write; `Mathf.Max(5,...)` lives only in the CLI `-saveinterval` parse branch) | selected, no explicit number | `Save.AutosaveIntervalSeconds`, default 1800 (vanilla), range **30–1800 s**, hard `Mathf.Max(30f, cfg)` clamp in the mod's own write, reapplied on every live reload | A1 |
| P2 | SAFE-WITH-GUARDS (backupCount≤0 confirmed harmless no-op, not destructive — original data-loss rationale for a `>=1` clamp is refuted; the real guard is write-ordering) — **RED-TEAM FIX:** the safe range excludes `1` (vanilla sentinel for "no backups", not "keep one") | selected | `Save.BackupCount` default 4 (matches vanilla's bootstrap value), range **2–10**, with `1` accepted from old configs but silently upgraded to `2` with a loud `LogWarning` (never left at the vanilla-sentinel value); `Save.BackupShortSeconds`/`Save.BackupLongSeconds` default 7200/43200, floor 5 s each; applied from a Postfix on `FejdStartup.Awake`, **after** `ParseServerArguments` runs, never at plugin `Awake` alone; the same clamp also protects the character-profile backup path, which shares `ConsiderAutoBackup`/`m_backupCount` with world backups | A1 |
| P3 | SAFE-WITH-GUARDS, board-tagged **phase B** (the mechanism ships now; it has no live-authority toggle to protect until a later-phase feature adds one) — **RED-TEAM FIX:** `TryEnable` must confirm the forced save actually ran, not assume it from a `void` return | selected | `SafeToggle.Enable(...)`: forces `ZNet.instance.Save(sync:true, saveOtherPlayerProfiles:true, waitForNextFrame:false)`, blocking, gated on `EnoughDiskSpaceAvailable` **and** on the same silent-skip state `ZNet.Save` itself checks (see Hook 9), **always marshaled to the main thread first** — see Data-loss analysis for the PrepareSave race this closes | A1 (primitive) / consumed starting Phase B |
| P4 | SAFE-WITH-GUARDS — **RED-TEAM FIX:** the auto stand-down disk-space trigger must actually debounce (Hook 5 vs Hook 9 mismatch), and (found during this reconciliation, not by either red team) the `noodles` console command needed `onlyServer: true, remoteCommand: true` or a remote admin's command never reaches the server at all — see Hook 4 | selected | One config bool `Core.Enabled` (default true), one admin console command `noodles`, automatic stand-down on guard trips (below); every gated field a mutable read, never a transpiled literal; disabling flushes G6-N3's hold-set (that lever's own doc, cross-referenced here since P4 requires it) | A1 |
| P5 | SAFE-WITH-GUARDS ("N/A — confirmed no patch needed") | selected | No patch. Documented as a confirmed-safe non-change: `ZRpc.HandlePackage`/`ZRoutedRpc.HandleRoutedRPC` silently no-op on an unregistered RPC hash, so a vanilla peer never errors on a modded peer's custom RPCs, and a mod-only server feature is inert with a vanilla peer's own connection. Guard: every custom RPC still version-tags its payload (see Handshake) | A1 (doc-only) |
| P6 | SAFE-WITH-GUARDS — **RED-TEAM FIX:** RPC name is now registered unconditionally at `ZNet.Awake` (only send/receive logic is flag-gated), so an off→on live flip of `Features.ConfigSync.Active` doesn't need a second world load to work | selected | Namespaced routed method name `"NoodlesMcDoodles.v1.ConfigSync"`, registered from a Postfix on `ZNet.Awake` (registration itself unconditional; `Features.ConfigSync.Active` gates only the push/receive path), wrapped in try/catch (Register throws `ArgumentException` on a 32-bit hash collision) | A2 |
| P7 | SAFE-WITH-GUARDS — **RED-TEAM FIX:** `SendBudgetNearMisses` postfix recomputes the near-miss condition itself instead of trusting `SendZDOs`'s `bool` return, which has two other unrelated false-return paths | selected | Postfix-only counters on `ZDOMan.CreateNewZDO`, `ZDOMan.DestroyZDO`, the per-peer send-budget check, and `ZSteamSocket` send-queue depth; periodic (not per-event) `LogInfo` summary | A1 |
| P8 | NEEDS-LIVE-TEST (test matrix, not a code lever) — **RED-TEAM FIX:** scenario 3 retagged to A2 (it needs the Hello/ConfigSync payloads, which don't exist until A2 ships) | selected | Scenarios 1–2 executable now on the single-server testbed; scenario 3 needs the A2 payloads; scenarios 4–5 need multiple concurrent real clients (not yet scripted); scenario 6 added once G6-N1 ships | A1 (1–2) / A2 (3) / later (4–6) |

## Hooks

**RED-TEAM FIX — build convention this doc's code sketches assume.** Every `[HarmonyPatch(typeof(X), nameof(X.Method))]` attribute below (Hooks 1, 2, 3, 7, 8) targets a `private` method on another class. C#'s `nameof` operand is resolved with ordinary member-access accessibility rules, so `nameof(ZNet.RPC_PeerInfo)` etc. only compiles against a **publicized** reference assembly. Confirmed private in both decompiles this pass (`private void RPC_PeerInfo(ZRpc, ZPackage)` — SERVER:79821/CLIENT:80002; `private void Awake()` — `ZNet` SERVER:79204/CLIENT:79346, `Game` SERVER:99902/CLIENT:100491, `FejdStartup` SERVER:96892/CLIENT:97435). This project's own build convention (`HANDOFF.md`: `BepInEx.AssemblyPublicizer.MSBuild` 0.4.1, HintPaths at `libs-Tools/1.0/{client,server}/*.dll`, `Private=False`) already does this, but that convention lived only in `HANDOFF.md`, never in a design doc, despite this being the first doc to ship compilable Harmony patch code — a developer wiring up a `.csproj` from this doc's own GROUND TRUTH DLL paths alone (not independently knowing the sibling-mod convention) would hit `CS0122` on all five patch classes. Stated here explicitly so this doc is self-contained: **build against the publicized reference assemblies per `HANDOFF.md`'s convention**, not the raw DLLs. (The string-literal form `[HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]` is a no-publicizer-needed alternative and is the conventional pattern for private Unity lifecycle methods regardless — not adopted here only to keep the sketches consistent with the rest of the codebase's existing `nameof` convention.)

### 1. `ZNet.RPC_PeerInfo(ZRpc rpc, ZPackage pkg)` — handshake re-entry guard (G1-L1)

- **CLIENT:80002** / **SERVER:79821** — signature `private void RPC_PeerInfo(ZRpc rpc, ZPackage pkg)`, identical in both builds. Registered as the peer's `"PeerInfo"` RPC at **SERVER:79685** / **CLIENT:79866**.
- **Patch kind:** Prefix. **Side:** installed on both builds (method exists identically on both) but the guard body is a no-op unless `ZNet.instance.IsServer()` is true — the exploit only matters server-side, where a client resending `PeerInfo` can double-enroll itself.
- **Re-verified beyond the lever catalog's own claim.** The catalog says vanilla has "no already-joined check." That's not quite precise, and the imprecision matters for the fix: vanilla's server branch *does* call `IsConnected(uid)` (**SERVER:80040-80053**) before re-registering, but `IsConnected(uid)` matches **by the resent `uid` value**, not by peer identity:
  ```csharp
  public bool IsConnected(long uid) {
      if (uid == GetUID()) return true;
      foreach (ZNetPeer peer in m_peers)
          if (peer.m_uid == uid) return true;
      return false;
  }
  ```
  `uid` is a value the connecting client itself chooses and sends in the packet (`long uid = pkg.ReadLong();`, read at the top of `RPC_PeerInfo`, immediately after the version check). A client that resends `PeerInfo` with the **same** `uid` it used the first time *is* caught (its own `ZNetPeer.m_uid` already equals that value once the first pass completed — `peer.m_uid = uid;` runs at the fall-through label after the password/whitelist checks, **SERVER:79952 / CLIENT:80145** — `peer.m_uid = uid;`, confirmed both builds). But a client that resends with a **different, freshly self-chosen `uid`** sails straight past `IsConnected(newUid)` (no other peer has that value), falls through, and vanilla re-runs the entire back half of the method: `rpc.Register<ZPackage>(...)` for `ServerSyncedPlayerData`/`PlayerList`/etc. (harmless — `ZRpc.Register` does a silent Remove-then-Add, **SERVER:83748+ / CLIENT:84205-84215** confirmed identical), then `m_zdoMan.AddPeer(peer)` (**SERVER:76814 / CLIENT:76956**) and `m_routedRpc.AddPeer(peer)` (**SERVER:83573 / CLIENT:83803**) a second time on the *same* `ZNetPeer`. `ZDOMan.AddPeer` has no duplicate check at all (`m_peers.Add(zDOPeer)`, unconditional, **SERVER:76814-76820**) — this peer now has two `ZDOPeer` entries in `ZDOMan.m_peers`, doubling its share of the once-per-tick send rotation (Q2) at every other connected peer's expense.
- **Guard used here is therefore identity-based, not value-based**: `ZNetPeer.IsReady()` (**SERVER:78866-78869 / CLIENT:79008**, `return m_uid != 0;`) is true from the moment the *first* successful pass sets `peer.m_uid`, regardless of what `uid` a later resend claims.
- **Why this hook and not another:** it's the single choke point every join (and every re-send of the same RPC name) passes through, on both build's `m_isServer` branch, before any peer-list mutation happens. A postfix on `AddPeer` itself would be too late (the second `ZDOPeer` would already exist) and would have to be duplicated on two different classes (`ZDOMan` and `ZRoutedRpc`) instead of one.
- **RED-TEAM FIX:** the original code sketch resolved `GetPeer(ZRpc)` via an uncached `AccessTools.Method(...).Invoke(...)` on **every single call** to this doc's busiest hook, with no null-check on the `MethodInfo` and no try/catch — a direct violation of the "every patch wraps its body in try/catch" invariant this same doc requires of every other hook (Hook 5 trigger #3, Hook 6's guard). If a future build ever renames the private `GetPeer(ZRpc)` overload (plausible: client build 25527674 and server build 25527701 already diverge in other places), `.Invoke` on a `null` `MethodInfo` throws inside the Prefix, and `ZRpc.HandlePackage`'s dispatch has no try/catch of its own around `value2.Invoke(this, package)` (**SERVER:83952-83970**, confirmed this pass — `else if (m_functions.TryGetValue(num, out value2)) { value2.Invoke(this, package); }`), so the exception is uncaught and never counted by the exception-burst auto-stand-down (Hook 5 trigger #3), because nothing catches it to increment that counter. Fixed by resolving the `MethodInfo` once at plugin `Awake` (reusing the same cached wrapper Hook 2 already assumes exists, `ZNetHelpers.GetPeer`) and wrapping the Prefix body in try/catch like every other hook.
- **Code sketch:**
  ```csharp
  // NoodlesMcDoodles.Core.Rails.HandshakeGuard
  [HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_PeerInfo))]
  static class Patch_RPC_PeerInfo_Reentry {
      static bool Prefix(ZNet __instance, ZRpc rpc, ZPackage pkg) {
          if (!Features.HandshakeReentryGuard.Active) return true;   // flag off -> vanilla
          if (!__instance.IsServer()) return true;                  // guard is server-side only
          try {
              var peer = ZNetHelpers.GetPeer(__instance, rpc);   // cached MethodInfo, resolved once at Awake
              if (peer != null && peer.IsReady()) {
                  Telemetry.Counters.HandshakeReentryRejected.Increment();
                  ZLog.LogWarning($"NoodlesMcDoodles: rejected re-entrant PeerInfo from already-ready peer {peer.m_uid}");
                  return false;   // skip vanilla body entirely; connection stays as-is, no Error RPC needed
              }
          } catch (Exception e) {
              Telemetry.Counters.PatchExceptions.Increment();   // feeds Hook 5 trigger #3
              ZLog.LogWarning($"NoodlesMcDoodles: HandshakeGuard prefix failed, falling back to vanilla for this call: {e}");
          }
          return true;
      }
  }
  // ZNetHelpers.GetPeer resolves and caches AccessTools.Method(typeof(ZNet), "GetPeer", new[] { typeof(ZRpc) })
  // once, at plugin Awake; if resolution fails, this trips the kill switch immediately with a loud log
  // (fail loud at startup, not silently per-call at runtime) rather than leaving a null cached delegate.
  ```
- **Off behaviour:** prefix returns `true` unconditionally — vanilla's own (weaker, by-value) `IsConnected(uid)` check is all that remains, exactly matching unmodded dedicated-server behaviour.

### 2. `ZNet.RPC_PeerInfo` — negotiation handshake kickoff (new RPC, A2)

- Same method/lines as Hook 1. **Patch kind:** Postfix (separate patch class from the Prefix above — independently flaggable). **Side:** both.
- **Why a postfix here and not a new hook elsewhere:** this is exactly the point G1-N1's own design calls for — "capability handshake immediately after `RPC_PeerInfo` succeeds" — and it is the earliest point at which `peer.m_uid`, `peer.m_playerName` and the peer's own `rpc` object all exist and are stable.
- **Idempotency is self-contained**, not borrowed from Hook 1: the postfix keeps its own `Dictionary<ZNetPeer, PeerNegotiationState>` and only sends `Hello` once per peer regardless of whether the re-entry guard (Hook 1) is enabled — so G1-L1 and the handshake can each be flagged off independently without one silently depending on the other.
  ```csharp
  [HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_PeerInfo))]
  static class Patch_RPC_PeerInfo_Handshake {
      static void Postfix(ZNet __instance, ZRpc rpc, ZPackage pkg) {
          if (!Features.Negotiation.Active) return;
          var peer = ZNetHelpers.GetPeer(__instance, rpc);   // reflection wrapper, cached MethodInfo
          if (peer == null || !peer.IsReady()) return;       // peer not yet enrolled (e.g. version-mismatch path)
          // NOTE: this does NOT filter out a re-entrant resend that Hook 1 rejected — Harmony runs every
          // postfix regardless of what a sibling prefix returned, and peer.m_uid (what IsReady() checks)
          // was already set by that peer's *first* successful pass, so IsReady() stays true across a
          // rejected resend too (SERVER:78866-78869 / CLIENT:79008-79011, confirmed `return m_uid != 0;`).
          // Safety against re-running this postfix on a resend comes entirely from the HelloSent check
          // immediately below, not from this line — any later hook copying this "separate patch class on
          // the same method" pattern must bring its own equivalent idempotency guard, not assume a sibling
          // prefix's `return false` already excluded it here.
          if (PeerState.TryGet(peer, out var st) && st.HelloSent) return;  // idempotent
          rpc.Register<ZPackage>("NoodlesMcDoodles.v1.Hello", RPC_NoodlesHello);
          var outPkg = new ZPackage();
          outPkg.Write(ProtocolVersion);          // int, currently 1
          outPkg.Write((long)Capabilities.Mask);  // ConfigSync=1<<0, reserved bits for later phases
          outPkg.Write(PluginInfo.PLUGIN_VERSION);
          rpc.Invoke("NoodlesMcDoodles.v1.Hello", outPkg);
          PeerState.MarkHelloSent(peer);
      }
  }
  ```
- **RPC_NoodlesHello handler** (new method, registered above, not a patch on vanilla code):
  ```csharp
  static void RPC_NoodlesHello(ZRpc rpc, ZPackage pkg) {
      try {
          int remoteVersion = pkg.ReadInt();
          long remoteCaps = pkg.ReadLong();
          string remoteModVersion = pkg.ReadString();
          if (remoteVersion != ProtocolVersion) {
              ZLog.LogWarning($"NoodlesMcDoodles: peer protocol version {remoteVersion} != {ProtocolVersion}; treating as unnegotiated (vanilla-equivalent)");
              return;   // fail closed: never mark negotiated on a version we don't have a compat table entry for
          }
          PeerState.MarkNegotiated(rpc, remoteCaps, remoteModVersion);
      } catch (Exception e) {
          ZLog.LogWarning($"NoodlesMcDoodles: malformed Hello payload, ignoring: {e}");
      }
  }
  ```
- **Off behaviour:** postfix no-ops; the RPC name is never registered on that peer's `rpc`, so a peer this mod hasn't negotiated with is architecturally identical to a vanilla peer for every capability-gated feature (per P5's confirmed silent-miss dispatch — a peer that never gets `"NoodlesMcDoodles.v1.Hello"` registered simply never sees the packet type; nothing errors).
- **Vanilla peer behaviour:** a vanilla client/server never registers this RPC name at all, so this side's `rpc.Invoke("NoodlesMcDoodles.v1.Hello", ...)` is silently dropped by `ZRpc.HandlePackage`'s `TryGetValue` miss (confirmed pattern at **SERVER:83748 area / CLIENT:84205 area**, same `Dictionary<int, RpcMethodBase>` used by every registered RPC). `PeerState` for that peer simply never reaches `Negotiated=true`; every capability-gated feature checks that state and treats the peer as vanilla forever. No error, no disconnect, no extra RPC exchange — this is why P5 needed no code change of its own.
- **Timeout:** `Handshake.TimeoutSeconds` (default 10 s). If `HelloSent=true` but `Negotiated` never becomes true within that window, the peer is marked `KnownVanilla=true` (a small optimization so later code stops re-checking); this is purely a local bookkeeping state, never a disconnect trigger.

### 3. `ZNet.Awake()` — config-sync RPC registration (P6, A2)

- **SERVER:79204 / CLIENT:79346** — signature `private void Awake()` on `ZNet` (class at **SERVER:78896 / CLIENT:79038**), identical in both builds.
- **Patch kind:** Postfix. **Side:** both.
- **Why this hook:** `ZRoutedRpc.instance`/`m_routedRpc` is constructed inside this same `Awake()` (`m_routedRpc = new ZRoutedRpc(m_isServer);`), so a postfix here is the earliest point the routed-RPC registry exists to register into, and it runs once per process (not once per peer, unlike Hook 2).
- **RED-TEAM FIX:** the original code sketch gated `Register` itself on `Features.ConfigSync.Active`, but `ZRoutedRpc.instance.Register` only ever runs from this `ZNet.Awake` postfix, which fires once per process/world-load. If an operator (or the hash-collision guard) disables `ConfigSync` and later flips it back on live via `SettingChanged` — which the rest of this doc advertises as restart-free — nothing re-registers the RPC name until the next world load, so the channel silently stays dead for the rest of that session even though the flag reads "on". Fixed by registering the RPC name **unconditionally** at `Awake` and gating only the send/receive logic on the flag, so there is nothing left for a live flag flip to fail to redo.
- **Code sketch:**
  ```csharp
  [HarmonyPatch(typeof(ZNet), nameof(ZNet.Awake))]
  static class Patch_ZNet_Awake_ConfigSyncRegister {
      static void Postfix(ZNet __instance) {
          // Registration is unconditional — Features.ConfigSync.Active gates the push (Hook 2's negotiation
          // postfix) and RPC_ConfigSync's own body, never whether the name is registered at all. This is what
          // makes an off->on SettingChanged flip work without waiting for another ZNet.Awake.
          try {
              ZRoutedRpc.instance.Register<ZPackage>("NoodlesMcDoodles.v1.ConfigSync", RPC_ConfigSync);
          } catch (ArgumentException e) {
              ZLog.LogError($"NoodlesMcDoodles: ConfigSync RPC name collided with another mod's hash, disabling ConfigSync: {e}");
              Features.ConfigSync.ForceOff();  // this one feature only, not the whole kill switch
          }
      }
  }
  // RPC_ConfigSync's own body (and the negotiation postfix's decision to push) both check
  // Features.ConfigSync.Active fresh on every call, so "registered but flag off" behaves exactly like
  // "flag off" everywhere else in this doc — the registration is just plumbing, not a behaviour.
  ```
- **Server push, driven from the negotiation postfix (Hook 2)**, not from `Awake`: once `PeerState.Negotiated==true` for a peer **and** `(remoteCaps & Capabilities.ConfigSync) != 0`, the server sends one `ConfigSync` packet to that peer (scalar key/value pairs, version-tagged, well under 1–2 KB per G6-N4) and re-sends the same packet every `ConfigSync.PushIntervalSeconds` (default 30 s) in case of a late-joining feature or reconnect. Phase A's own levers (G1-L1, P1–P3, P7) are all server-only and need nothing pushed; the channel is exercised in Phase A with an empty/near-empty payload (just the protocol version) so it is proven working before a later phase's transport-tuning levers (T-series) have real values to push.
- **Off behaviour:** **RED-TEAM FIX (updated for the registration-is-unconditional fix above):** the RPC name stays registered, but `RPC_ConfigSync`'s handler body and the Hook 2 push both no-op on `Features.ConfigSync.Active==false`, so no payload is ever sent or acted on — disabling `ConfigSync` mid-session is still behaviourally indistinguishable from talking to a peer that never negotiated it, even though the low-level registration itself is no longer the thing being toggled.

### 4. `Terminal.ConsoleCommand` — admin kill-switch command (P4)

- **SERVER:43098-43148 / CLIENT:43099-43149** — `public class ConsoleCommand`, both constructor overloads present with the same parameter list (`isCheat, isNetwork, onlyServer, isSecret, allowInDevBuild, hideBehindDevCommands, optionsFetcher, alwaysRefreshTabOptions, remoteCommand, onlyAdmin`). Registration is `commands[command.ToLower()] = this;` inside the constructor — not a lookup table this mod can collide with destructively (an overwrite, not a throw), but the mod still picks a distinctive name to avoid confusing an admin.
- **Patch kind:** none — this is a **new** object created via vanilla's own public extension point, not a Harmony patch on any method. (Reported here as kind "component" since it doesn't fit prefix/postfix/transpiler/finalizer/new-RPC.)
- **Side:** both builds carry the `Terminal` class; the mod registers the command from its own plugin `Awake()` on whichever side it's loaded on.
- **RED-TEAM FIX — citation and a second, independently-found bug in the same hook.** RT2 correctly flagged the original admin-list citation (`RPC_Save`/`RPC_Kick`/etc., SERVER:80186/81716/81802/81838) as pointing at the wrong mechanism: those are each RPC handler's own separate `ListContainsId` check, not what enforces `onlyAdmin` for a `Terminal.ConsoleCommand`. Re-deriving this from the decompile during reconciliation surfaced a second, more serious problem the citation error was masking:
  - `ConsoleCommand` has **two constructor overloads** (**SERVER:43137** / **SERVER:43155**): one taking `ConsoleEventFailable` (`public delegate object ConsoleEventFailable(ConsoleEventArgs args)`, **SERVER:43257**) which does `OnlyServer = onlyServer || onlyAdmin;`, and one taking `ConsoleEvent` (`public delegate void ConsoleEvent(ConsoleEventArgs args)`, **SERVER:43259**) which does `OnlyServer = onlyServer;` — **`onlyAdmin` is not folded into `OnlyServer` in the void-delegate overload.** The original code sketch's lambda has a bare `return;` and falls off the end with no value — a void lambda — so it can only bind to the `ConsoleEvent` overload. With `onlyServer` left at its default (`false`), `OnlyServer` stays `false`.
  - `Terminal.IsValid()` (**SERVER:43238-43253**) is the **only** place either field is read for admin/server gating: `if (OnlyServer) { return ZNet.instance.IsServer(); } return true;`. `OnlyAdmin` itself (**SERVER:43118**) is written by both constructors but is **never read anywhere** in either decompile — it has no enforcement effect at all by itself. So the original code sketch's `onlyAdmin: true` argument was silently inert: `OnlyServer==false` meant `IsValid()` returned `true` unconditionally, on **both** client and server.
  - `Terminal.TryRunCommand` (**SERVER:45782-45806**) only forwards a command to the server (`ZNet.instance.RemoteCommand(text)`, **SERVER:81915-81922** → `RPC_RemoteCommand`, **SERVER:81925-81934**) when `IsValid()` is `false` **and** `RemoteCommand==true`. The original sketch never passed `remoteCommand: true` either. Net effect: if a remote in-game admin (the project's own Arizona admin, not someone with shell access to archserver) typed `noodles disable` in their client's chat console, `IsValid()==true` would make it run **locally on the client** — flipping that client's own `Core.Enabled`, never reaching the server at all. This directly contradicts the hook's own prose ("works... an admin issues it from their own in-game chat, which vanilla already routes to the server") and Open Question 3's framing.
  - Vanilla's own real admin-list-gated remote pattern (`RPC_RemoteCommand`, **SERVER:81925-81934**: `if (!ListContainsId(m_adminList, rpc.GetSocket().GetHostName())) { RemotePrint(rpc, "You are not admin"); } else { InternalCommand(rpc, command); }`) is reached only via `onlyServer: true` (explicit, not relying on the `onlyAdmin` fold) **combined with** `remoteCommand: true` — confirmed as vanilla's actual convention: every `remoteCommand: true` registration found in the decompile pairs it with an explicit `onlyServer: true`, and no vanilla command combines `remoteCommand: true` with `onlyAdmin: true` at all (`grep` across the full server decompile: 13 `remoteCommand: true` sites, 0 also carrying `onlyAdmin: true`). This — not the `ConsoleCommand`-level `onlyAdmin` field — is the actual admin gate, and it is what this hook now uses.
- **Why this hook and not another:** it's the same mechanism every other BepInEx mod on Proxima Maxi that doesn't use Jotunn already relies on for admin commands, and it needs no RPC of the mod's own (it rides vanilla's existing `RPC_RemoteCommand`/`InternalCommand` plumbing) — but only once wired with `onlyServer: true, remoteCommand: true` as below, not the original `onlyAdmin: true` alone.
- **Code sketch:**
  ```csharp
  // in the plugin's Awake(), both builds — registered identically on client and server so the SAME
  // command name resolves locally when run where ZNet.instance.IsServer() is true, and forwards via
  // RPC_RemoteCommand (vanilla's own admin-list check) everywhere else.
  new Terminal.ConsoleCommand("noodles", "NoodlesMcDoodles admin control: status | enable | disable",
      (Terminal.ConsoleEventArgs args) => {
          if (args.Length < 2) { args.Context.AddString(Core.KillSwitch.StatusLine()); return; }
          switch (args[1].ToLower()) {
              case "status":  args.Context.AddString(Core.KillSwitch.StatusLine()); break;
              case "enable":  Core.KillSwitch.SetEnabled(true,  "admin console"); break;
              case "disable": Core.KillSwitch.SetEnabled(false, "admin console"); break;
          }
      },
      isCheat: false, isNetwork: false, onlyServer: true,   // explicit: do NOT rely on onlyAdmin folding
      isSecret: false, allowInDevBuild: false, hideBehindDevCommands: false, optionsFetcher: null,
      alwaysRefreshTabOptions: false, remoteCommand: true,  // forwards to RPC_RemoteCommand when not local
      onlyAdmin: false);  // the real gate is RPC_RemoteCommand's own ListContainsId(m_adminList, ...);
                           // onlyAdmin here would be dead weight given the void-delegate overload above
  ```
- **Off behaviour:** n/a — this command *is* the kill switch's manual control surface; it has no separate flag of its own (disabling the whole mod obviously also disables its own admin command's *effects*, but the command registration itself is harmless to leave in place — it just reports "already disabled"). The admin-list check happens server-side inside vanilla's `RPC_RemoteCommand` regardless of `Core.Enabled`, so `noodles enable` still works to bring a stood-down mod back.

### 5. Kill switch core — `Core.Enabled`, auto stand-down (P4)

- **Not a game hook.** The kill switch is a static gate every *other* hook in the whole mod (this doc and every later one) reads first, plus a supervisor that can flip it off on its own.
- **Guarded fields, never transpiled literals:** every numeric lever anywhere in the mod (T1's global rate, Q3's budget, O3's cadence, etc. — out of this doc's scope but built on this same primitive) is implemented as a `ConfigEntry<T>`-backed static field read fresh on every call, exactly as P4's guard requires, so disabling never leaves a transpiler-baked value active.
- **Auto stand-down triggers** (each independently configurable, each sets `Core.Enabled=false` and logs a reason; **none auto-re-enables** — an admin must explicitly flip it back, so a flapping guard can't hide behind a self-heal):
  1. Telemetry's created/destroyed ZDO reconciliation (Hook 6 below) exceeds `Core.AutoStandDown.ZdoMismatchTolerance` (default 50) sustained across two consecutive telemetry periods.
  2. `EnoughDiskSpaceAvailable` returns false during a P3 forced save immediately before any live-authority toggle — the toggle itself is refused (see Data-loss analysis) and, if this happens **twice in a row** (a real consecutive-failure counter, reset on any success — see Hook 9's **RED-TEAM FIX**; the original code sketch tripped the kill switch on the very first failure, contradicting this prose), the kill switch trips. Debounced deliberately: archserver also runs two other Valheim servers whose own autosave/backup cycles can transiently eat disk headroom, and a single blip must not stand down the mod that's supposed to fix the Arizona problem.
  3. A caught exception inside any of this mod's own Prefix/Postfix bodies more than `Core.AutoStandDown.ExceptionBurstLimit` (default 5) times inside a rolling 60 s window — every patch is already required to wrap its body in try/catch (per P7's guard and general hygiene), so this is a last-resort supervisor over bugs the try/catch itself didn't anticipate, not a substitute for it.
  4. Disabling the kill switch **also flushes G6-N3's SetOwner hold-set** (that lever's own document defines the hold mechanism; this doc only owns the contract that P4 must call into it) — "disable" means every held object is released back to vanilla `ReleaseNearbyZDOS` behaviour, not merely "stop granting new holds."
- **Startup diagnostic** (P4's guard, not a runtime trip): at plugin `Awake()`, enumerate `Harmony.GetPatchInfo` for every method this mod patches and `LogWarning` any foreign prefix/transpiler already present on the same target — Harmony's AND-of-all-prefixes semantics mean no per-mod flag can prevent a co-installed mod's own independent patch on the same hot method from still running; this is visibility, not a fix.

### 6. Telemetry counters (P7)

| Hook | CLIENT | SERVER | Patch kind | What it counts |
|---|---|---|---|---|
| `ZDOMan.CreateNewZDO(ZDOID, Vector3, int)` | confirmed present, same private signature (method body reached via the public overload two lines above it) | **76731** | Postfix | `ZdosCreated` (process-lifetime counter) |
| `ZDOMan.DestroyZDO(ZDO)` | mirrored | **76976-76981** | Postfix | `ZdosDestroyed` |
| Per-peer send-budget check (the `sendQueueSize > 10240` / `num < 2048` branch used by Q3/Q5) | mirrored, same 10240/2048 literals | **77055-77065** | Postfix on the containing method (`SendZDOs`) | `SendBudgetNearMisses` (budget almost exhausted, not yet an oversized-payload event) — **RED-TEAM FIX below** |
| `ZSteamSocket.SendQueuedPackages` | not applicable — Steam socket classes are compiled per-build; confirmed present on **SERVER:86518-86544** with the `SteamGameServerNetworkingSockets` variant; the CLIENT build's equivalent method exists as the `SteamNetworkingSockets` (non-Game) variant per T1/T8's own build-split guard | **86518-86544** | Postfix | `SteamSendQueueHighWater` (max observed `m_sendQueue.Count` per peer per telemetry period) |

- **RED-TEAM FIX — `SendBudgetNearMisses` must not key off `SendZDOs`'s raw `bool` return.** Confirmed this pass (**SERVER:77055-77070**): `private bool SendZDOs(ZDOPeer peer, bool flush)` has **three** distinct `false`-return paths — `!flush && sendQueueSize > 10240` (queue already over the outer gate), `num < 2048` where `num = 10240 - sendQueueSize` (**the real near-miss**), and `m_tempToSync.Count == 0 && peer.m_invalidSector.Count == 0` (nothing queued to send — the common, harmless case on an idle tick). A postfix that increments on `__result == false` alone would be dominated by the third path on nearly every idle tick per peer, drowning out the one signal the counter exists to surface. Fixed by having the postfix recompute the near-miss condition itself from its own available parameters (`peer.m_peer.m_socket.GetSendQueueSize()`) rather than trusting `__result`:
  ```csharp
  [HarmonyPatch(typeof(ZDOMan), "SendZDOs")]
  static class Patch_ZDOMan_SendZDOs_Telemetry {
      static void Postfix(ZDOPeer peer, bool flush, bool __result) {
          if (!Features.Telemetry.Active) return;
          try {
              int sendQueueSize = peer.m_peer.m_socket.GetSendQueueSize();
              if (10240 - sendQueueSize < 2048) {   // the actual near-miss condition, independent of __result
                  Telemetry.Counters.SendBudgetNearMisses.Increment(peer);
              }
          } catch (Exception e) { ZLog.LogWarning($"NoodlesMcDoodles: telemetry postfix failed: {e}"); }
      }
  }
  ```
- **Side:** server-only for the ownership/save/queue counters (this doc's scope has no client-side counters); the mechanism itself (a static counter class + periodic flush) is shared code any later client-side lever can reuse.
- **Guards (from the lever, re-affirmed):** postfix-only, never a prefix that could skip vanilla logic; every counter increment wrapped in try/catch so a bug in telemetry can never interrupt a hot send/ZDO path; aggregated in memory and flushed as one `LogInfo` line every `Telemetry.LogIntervalSeconds` (default 60 s), never per-event inside a 20 Hz loop; `ZDO.SetOwner` is **not** hooked globally (only the mod's own explicit `SetOwner` calls in later-phase ownership code count themselves) — sampling `OwnerRevision` deltas periodically from `ZDOMan.Update` is the fallback for anything this doc doesn't itself own.
- **Log format** (one line per period, grep-able key=value pairs):
  ```
  [NoodlesMcDoodles] telemetry t=1234.5 zdos_created=812 zdos_destroyed=804 zdo_delta=+8 handshake_ok=3 handshake_rejected_reentry=0 send_budget_near_miss=2 steam_sendq_hw_peer12345=6144B kill_switch=on auto_standdown=none
  ```
  `zdo_delta` (created minus destroyed, since the counter class's own last reset — not since world start) is the reconciliation figure Hook 5's auto stand-down trigger #1 reads; a large sustained value that tracks player population growth is expected and healthy, a value that grows independent of population is the signal.
- **Off behaviour:** postfixes no-op (return without incrementing); the periodic flush timer still fires but logs `kill_switch=off` and zeroes, so an operator can tell telemetry itself is alive even while everything else is stood down.

### 7. `Game.Awake()` — autosave interval clamp (P1)

- **SERVER:99902 / CLIENT:100491** — signature `private void Awake()` on `Game` (class at **SERVER:99675 / CLIENT:100264**), identical in both builds.
- **Patch kind:** Postfix. **Side:** server (the field is `public static float Game.m_saveInterval`, read every frame by the same class's own save-countdown logic at **SERVER:100349-100365 / CLIENT:100962-100967** — patching where it's *read* would mean re-patching a hot per-frame method for no benefit; the write only needs to happen once per load and once per live config change).
- **Why this hook:** `Game.Awake()` runs once per world load, strictly before the per-frame save-countdown logic in the same class ever executes, and — critically — **after** `FejdStartup`'s own menu/CLI-argument code has already run (`Game` only exists once a world is actually loading), so this write is never at risk of the CLI `-saveinterval` branch clobbering it the way P2's backup fields are.
- **Code sketch:**
  ```csharp
  [HarmonyPatch(typeof(Game), nameof(Game.Awake))]
  static class Patch_Game_Awake_SaveInterval {
      static void Postfix() {
          if (!Features.SaveIntervalClamp.Active) return;
          ApplySaveInterval();
      }
      internal static void ApplySaveInterval() {
          // Hard in-code clamp — Mathf.Max(5,...) inside FejdStartup's own -saveinterval
          // parse branch (SERVER:97354/CLIENT:97903) does NOT protect this direct field write.
          Game.m_saveInterval = Mathf.Max(30f, Config.Save.AutosaveIntervalSeconds.Value);
      }
  }
  // ConfigFile.SettingChanged handler also calls ApplySaveInterval() directly,
  // so a live edit to the .cfg file takes effect without waiting for another world load.
  ```
- **Off behaviour:** postfix no-ops; `Game.m_saveInterval` stays at whatever vanilla/CLI set it to (default 1800 s).

### 8. `FejdStartup.Awake()` — backup count/short/long clamp (P2)

- **SERVER:96892 / CLIENT:97435** — signature `private void Awake()` on `FejdStartup` (class at **SERVER:96598 / CLIENT:97141**), identical in both builds. Inside it, `ParseServerArguments()` (**SERVER:97256 / CLIENT:97805**) unconditionally sets `ZNet.m_backupCount = 4;` as its first statement (**SERVER:97264 / CLIENT:97813**), before parsing any `-backups` CLI flag — this runs *after* BepInEx plugin `Awake()` in the mod-load order, so a naive write at plugin `Awake()` is silently clobbered.
- **Patch kind:** Postfix. **Side:** server.
- **Why this hook and not plugin `Awake()`:** it's the confirmed point *after* which nothing else in vanilla touches `ZNet.m_backupCount`/`m_backupShort`/`m_backupLong` again before `ConsiderAutoBackup` (**SERVER:80761-80774**) reads them.
- **Code sketch:**
  ```csharp
  [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.Awake))]
  static class Patch_FejdStartup_Awake_Backups {
      static void Postfix() {
          if (!Features.BackupPolicy.Active) return;
          ZNet.m_backupCount = ClampBackupCount(Config.Save.BackupCount.Value);
          ZNet.m_backupShort = Mathf.Max(5, Config.Save.BackupShortSeconds.Value);
          ZNet.m_backupLong  = Mathf.Max(5, Config.Save.BackupLongSeconds.Value);
      }
      // RED-TEAM FIX: the documented "safe" range 1-10 included 1, which ConsiderAutoBackup treats as its
      // own sentinel for "no backups at all", not "keep one backup":
      //   int num2 = ((m_backupCount != 1) ? m_backupCount : 0);   // SERVER:80761-80774 / CLIENT:80955-80968
      //   if (num2 > 0) { return SaveSystem.ConsiderBackup(...); } return false;
      // A value this clamp would have happily accepted therefore silently disabled EVERY backup — both world
      // (SERVER:80647) and character-profile (SERVER:105793, same shared call) — for as long as it was set,
      // with nothing in the logs distinguishing it from a healthy 1-slot rotation. Fixed by excluding the
      // sentinel from the accepted range and upgrading a legacy/typo'd 1 to 2 rather than passing it through.
      internal static int ClampBackupCount(int configured) {
          int clamped = Mathf.Clamp(configured, 2, 10);
          if (configured == 1) {
              ZLog.LogWarning("NoodlesMcDoodles: Save.BackupCount=1 is vanilla's own sentinel for "
                  + "\"no backups at all\" (ZNet.ConsiderAutoBackup treats m_backupCount==1 as 0) — "
                  + "upgrading to 2 so backups actually happen. Set 2 explicitly to silence this warning.");
          }
          return clamped;
      }
  }
  ```
- **Off behaviour:** postfix no-ops; `ZNet.m_backupCount` stays at vanilla's bootstrap value of 4 (or whatever `-backups` set), matching an unmodded server exactly — including vanilla's own `==1` sentinel behaviour if an operator sets `-backups 1` on the command line, which is untouched by this doc either way since the postfix simply doesn't run.

### 9. `ZNet.Save(bool, bool, bool)` — forced save before a live-authority toggle (P3 primitive)

- **SERVER:80221** (`public void Save(bool sync, bool saveOtherPlayerProfiles = false, bool waitForNextFrame = false)`), confirmed this pass; the corresponding `SaveWorld(bool)` that it calls into is at **SERVER:80567 / CLIENT:80760**.
- **Patch kind:** none — this hook is a **call site**, not a patch: `SafeToggle` (a helper this doc defines for any later-phase feature that flips a live-authority toggle) calls the vanilla public API directly.
- **Side:** server. **Why this call and not a patch on it:** `PrepareSave()` (`m_zdoMan.PrepareSave()`, `ZoneSystem.instance.PrepareSave()`, etc.) runs **synchronously on whatever thread calls `Save()`**, *before* `SaveWorld` spins up its background thread (**SERVER:80567-80581**) — confirmed by direct re-read this pass. That means the one mandatory guard is procedural, not a patch: never call this from a background thread (a `ConfigFile.SettingChanged` handler, a `FileSystemWatcher` callback) — always marshal onto the Unity main thread first.
- **RED-TEAM FIX — two problems in the original `TryEnable`, both confirmed against the decompile this pass:**
  1. **(blocker) The forced save's success was never verified.** `ZNet.Save` is `void` and has its own silent-skip branch: `if (m_loadError || ZoneSystem.instance.SkipSaving() || DungeonDB.instance.SkipSaving()) { ZLog.LogWarning("Skipping world save"); }` (**SERVER:80229-80231**, byte-identical **CLIENT:80422-80424**), plus a *second*, earlier silent-skip path this reconciliation pass found that neither the original doc nor either red team's issue list cited: `if (SaveSystem.HasSessionFlag(SaveSystemSessionFlags.DontSaveWorld)) { ZLog.LogWarning(...); return; }` (**SERVER:80223-80227**), which runs *before* the `m_loadError`/`SkipSaving` checks and is also public (`SaveSystem.HasSessionFlag`, **SERVER:109651-109654**, `public static bool`). `ZoneSystem.SkipSaving()` (**SERVER:115732-115738**: `if (!m_error) return m_didZoneTest; return true;`) latches permanently true once `ZoneSystem.m_didZoneTest` is set, and vanilla's own admin console commands set it as routine dev/test workflow — `nextseed` (**SERVER:44349-44354**: `ZoneSystem.instance.m_didZoneTest = true;` with the console message "world saving DISABLED until restart") and the location-test spawn path (**SERVER:114938**: `m_didZoneTest = disableSave;`). The original `TryEnable` called `Save(...)` and then unconditionally ran `enableAction()` right after, with no signal that the "safety save" had silently done nothing — so a live-authority toggle (Phase B's O6/G3-L1) could proceed believing a fresh snapshot existed when none was written, and a crash shortly after would leave the reloaded world's on-disk state mismatched with whatever authority change had just gone live.
  2. **(major) The disk-space trip contradicted Hook 5's own documented policy.** The original code called `Core.KillSwitch.SetEnabled(false, ...)` on the very first `EnoughDiskSpaceAvailable()==false`, with no counter anywhere, while Hook 5's prose promised "twice in a row" — a real bug, not just a documentation mismatch, and one a transient I/O blip on archserver (which also autosaves two other Valheim servers) could trigger on the very feature meant to fix the Arizona problem.

  Both are fixed the same way: check state that's actually observable (all four public fields/methods `ZNet.Save` itself consults) instead of trusting a `void` return, and add the consecutive-failure counter the prose already promised.
- **Code sketch:**
  ```csharp
  // NoodlesMcDoodles.Core.Rails.SafeToggle — used by any later-phase feature
  // that flips a live server-authority behaviour (e.g. Phase B's O6 ownership sweep).
  static class SafeToggle {
      static int s_diskGuardConsecutiveFailures = 0;   // reset to 0 on any success

      public static bool TryEnable(Action enableAction, string toggleName) {
          MainThreadDispatcher.AssertMainThread();   // throws if called off-thread — defensive, not a fallback

          if (!ZNet.instance.EnoughDiskSpaceAvailable(out _)) {
              s_diskGuardConsecutiveFailures++;
              ZLog.LogError($"NoodlesMcDoodles: refusing to enable {toggleName} — low disk space "
                  + $"({s_diskGuardConsecutiveFailures} consecutive failure(s))");
              if (s_diskGuardConsecutiveFailures >= 2) {   // matches Hook 5 trigger #2's documented policy
                  Core.KillSwitch.SetEnabled(false, $"disk space guard before {toggleName} (2 in a row)");
              }
              return false;
          }
          s_diskGuardConsecutiveFailures = 0;

          ZNet.instance.Save(sync: true, saveOtherPlayerProfiles: true, waitForNextFrame: false);  // blocks

          // Verify the save actually ran using the same public state ZNet.Save() itself checks — its
          // void return gives no other signal. All four checks are public, no reflection needed.
          if (ZNet.m_loadError
              || ZoneSystem.instance.SkipSaving()
              || DungeonDB.instance.SkipSaving()
              || SaveSystem.HasSessionFlag(SaveSystemSessionFlags.DontSaveWorld)) {
              ZLog.LogError($"NoodlesMcDoodles: refusing to enable {toggleName} — the pre-toggle safety "
                  + "save was silently skipped (ZoneSystem/DungeonDB SkipSaving, a load error, or a "
                  + "DontSaveWorld session flag — e.g. an admin ran nextseed/a location test this session). "
                  + "Restart the server to clear the latch before retrying.");
              Telemetry.Counters.SafeToggleRefusedSkippedSave.Increment();
              return false;   // do NOT run enableAction() on an unconfirmed save
          }

          enableAction();
          Telemetry.Counters.SafeToggleEnables.Increment();
          return true;
      }
  }
  ```
- **Off behaviour:** n/a — `SafeToggle` is only ever invoked by a feature that is itself already gated by its own flag; if that feature is off, `SafeToggle` is simply never called.

## Data-loss analysis

| Path | Guard |
|---|---|
| **Double peer enrollment** (G1-L1): a client resends `PeerInfo` with a fresh self-chosen `uid`, bypassing vanilla's own by-value `IsConnected(uid)` check, and gets added to `ZDOMan`/`ZRoutedRpc`'s peer lists twice. | Hook 1's identity-keyed `IsReady()` guard, independent of what `uid` value is claimed. Doesn't lose data by itself (no ZDO is destroyed), but doubles that peer's share of the send rotation at every other peer's expense — a fairness/bandwidth bug that this doc treats as a data-loss *adjacent* risk because degraded send rotation is exactly the mechanism (per the coupled-levers note in 10-LEVERS) that increases real disconnect frequency, which reopens the persistent-ZDO-stale-owner window (T3/T4's own hazard, out of this doc's scope but fed by this one). |
| **Config-sync RPC name collision** (P6): another mod, or a future NoodlesMcDoodles version, registers a routed RPC whose name hashes to the same 32-bit key. `ZRoutedRpc.Register` throws `ArgumentException` on `Dictionary.Add` (**SERVER:83713-83743**, confirmed `m_functions.Add(name.GetStableHashCode(), ...)`, no pre-check). | try/catch around every `Register` call; on failure, `Features.ConfigSync.ForceOff()` for that one feature only (never the whole kill switch) and a loud log line — the collision throws inside the mod's own `ZNet.Awake` postfix, which runs *after* vanilla's own `Awake` body already completed, so `ZNet` itself is never left partially constructed. |
| **Handshake protocol/schema drift**: a peer running a different NoodlesMcDoodles version sends a `Hello` (or `ConfigSync`) payload this version can't safely parse. GZip/binary deserialization of well-formed-but-mismatched bytes can succeed without throwing and silently misparse (this is G1-N1's own documented risk, inherited here since the Hello/ConfigSync channel is this doc's implementation of G1-N1's pattern). | Every custom payload leads with an explicit `int ProtocolVersion`; a mismatch is checked **before** any further field is read (not just wrapped in try/catch after the fact) and the peer is left `Negotiated=false` — treated exactly as vanilla — rather than partially parsed. |
| **Save skipped silently** (P1/P3): `ZNet.Save` itself already has silent skip paths — an early one, `if (SaveSystem.HasSessionFlag(SaveSystemSessionFlags.DontSaveWorld)) { ZLog.LogWarning(...); return; }` (**SERVER:80223-80227**, found during this reconciliation, not cited by either red team), and the one already documented here, `if (m_loadError || ZoneSystem.instance.SkipSaving() || DungeonDB.instance.SkipSaving()) { ZLog.LogWarning("Skipping world save"); }` (**SERVER:80229-80231**, confirmed this pass) — a lowered `Save.AutosaveIntervalSeconds` doesn't override either; it just calls the same guarded `Save` more often. | Not this doc's guard to add for the *autosave* path (it's vanilla's own correct behaviour) — P7's telemetry surfaces `"Skipping world save"` occurrences as a counter (`SavesSkipped`) so a sustained skip streak (e.g. after a G6-N1 fallback, which deliberately keeps this latch set) is visible in the same periodic log line rather than only in raw `LogOutput.log`. **RED-TEAM FIX:** for the *forced pre-toggle* save specifically, `SafeToggle.TryEnable` (Hook 9) now checks all four of these conditions directly after calling `Save` and refuses the toggle rather than assuming the save happened — see Hook 9. |
| **Near-zero autosave interval stall cascade** (P1's own risk, not closed by the range clamp alone): `Save()` resets `m_saveTimer=0` immediately (**SERVER:80228**, confirmed), and `SaveWorld` **joins the calling thread** if a prior save is still running (**SERVER:80567-80581**, `m_saveThread.Join()`). An interval near the 30 s floor on a large, populated world can mean the *next* autosave's main-thread call blocks waiting for the *previous* background save thread to finish, i.e. a repeating main-thread stall, not a one-time hitch. | The 30 s floor (not vanilla's non-existent one) is a mitigation, not a fix; `Save.AutosaveIntervalSeconds`'s config description says so explicitly, and P7's telemetry logs `save_duration_ms` per save so an operator sees the stall building before it causes connection timeouts. |
| **Forced save races a background config-reload thread** (P3): `PrepareSave()` runs synchronously on whatever thread calls `Save()`, *before* any background save thread exists — if that caller is a `ConfigFile.SettingChanged`/`FileSystemWatcher` callback (not guaranteed main-thread in BepInEx), this races unsynchronized against the real main thread's concurrent `ZDOMan.Update`/RPC writes. Confirmed this pass: **SERVER:80567-80581** (`PrepareSave` calls precede thread spawn) is unconditional regardless of caller thread. | `SafeToggle.TryEnable` asserts main-thread via `MainThreadDispatcher` before calling `Save`; any config-reload path that could eventually reach a live-authority toggle marshals onto the main thread first (an enqueued action consumed from a `MonoBehaviour.Update`), never calling `ZNet.instance.Save`/any `ZDOMan`/`ZoneSystem` API directly from a background thread. Console-command-triggered toggles are already main-thread-safe (Terminal command execution runs on the main thread) and need no extra marshaling. |
| **Kill switch disabled mid-handover** (P4 × G6-N3 coupling): disabling `Core.Enabled` while a later-phase feature is mid-way through an ownership hand-over could leave an object held by a claim that's no longer being maintained. | Out of scope for this doc's own levers (no ownership feature ships in Phase A), but the *contract* is fixed here: `Core.KillSwitch.SetEnabled(false, ...)` must call into G6-N3's hold-set flush before returning, so any later-phase feature that adds a hold-set is required to register a flush callback with this switch rather than inventing its own disable path. |
| **Harmony patch collision with a co-installed mod** (P4): AND-of-all-prefixes semantics mean this mod's flag cannot stop another mod's own independent prefix/postfix on the same target method from running. Not a data-loss path this mod introduces, but it can mask one. | Startup diagnostic (Hook 5) logs every foreign patch found on a method this mod touches; this is visibility only, not a guard — documented as a known limitation, not silently assumed away. |
| **Telemetry postfix throws inside a hot vanilla method** (P7): a bug in a counter increment could interrupt `ZDOMan.CreateNewZDO`/`DestroyZDO`/the send-budget check mid-call. | Every counter postfix wrapped in try/catch, per the lever's own guard; the only observable failure mode left is the postfix silently not incrementing that one counter, never an interrupted vanilla call. |
| **Crash / disconnect / reconnect mid-handshake**: a peer disconnects between `HelloSent=true` and `Negotiated=true`, or reconnects with a new `ZNetPeer` object. | `PeerState` is keyed by `ZNetPeer` object reference (per T2's own guard about reconnect creating a new object), not by connection handle or `uid` value; a reconnect starts the handshake from zero on both sides — no state is carried over, so there's nothing to lose. Config-sync's periodic re-push (`ConfigSync.PushIntervalSeconds`) means a peer that reconnects mid-session gets the current values again within one interval without needing a special "resend on reconnect" path. |
| **Kill switch mid-operation crash**: the process crashes while `Core.Enabled` is being written (e.g. mid-`SetEnabled` call, between flushing G6-N3's hold-set and persisting the config value). | `SetEnabled` flushes hold-sets and updates in-memory state **before** writing the `ConfigEntry` back to disk, so a crash mid-call either leaves the switch in its old on-disk state (safe: mod resumes exactly as it was, holds already flushed in memory are simply re-derived from ZDO state on the next pass) or fully applied — there is no on-disk state that represents "half disabled." |

## Vanilla and version compatibility

- **Vanilla client + modded server.** G1-L1's guard only changes behaviour for a peer that resends `PeerInfo` after already being ready — a vanilla client never does this, so it is unaffected. The negotiation RPC (`"NoodlesMcDoodles.v1.Hello"`) and config-sync RPC (`"NoodlesMcDoodles.v1.ConfigSync"`) are never registered on a vanilla client's `ZRpc`/`ZRoutedRpc` tables, so the server's `rpc.Invoke(...)` calls to that peer are silently dropped exactly like any other unrecognized RPC name (P5's confirmed silent-miss dispatch, `TryGetValue` returning false). No vanilla RPC name, hash, or `ZPackage` argument list is ever altered by anything in this doc.
- **Modded client + vanilla server.** Same silent-miss argument in reverse: the client's `Hello`/`ConfigSync` registrations exist but the vanilla server never sends anything to them, so the client just never negotiates and every capability-gated client feature stays off. G1-L1's guard is a no-op on a client build regardless (it only acts when `IsServer()` is true).
- **Modded version skew.** `ProtocolVersion` is a leading `int` on every custom payload (`Hello` and `ConfigSync`). A version this build doesn't have a compat-table entry for is treated as **not negotiated** rather than parsed further — per G1-N1's own guard, this is deliberately fail-closed rather than "parse what looks parseable," because GZip/binary decode of a schema-mismatched-but-well-formed payload can succeed and silently corrupt in-memory state without throwing.
- **PlayFab/crossplay sockets.** Nothing in this doc's hooks branches on socket type — `RPC_PeerInfo`, `ZNet.Awake`, `FejdStartup.Awake`, and `Game.Awake` are all socket-agnostic. The one place a build split already exists in the surrounding codebase (Steam vs. PlayFab config calls, T1/T6's own concern) is out of this doc's scope; P7's `ZSteamSocket` counter is explicitly Steam-only per the table above and simply reports zero/absent for a PlayFab connection rather than erroring — a later-phase telemetry addition can add the PlayFab-side counter using the same pattern.
- **Network version gate.** Unaffected: the `if (num != 40)` check (**SERVER:79839-79841** area) runs before any of this doc's hooks see the packet, so a genuinely incompatible game version is rejected by vanilla exactly as today, before `RPC_PeerInfo` reaches the point either Hook 1 or Hook 2 look at.

## Configuration

| Key | Default | Range | Synced? | Notes |
|---|---|---|---|---|
| `Core.Enabled` | `true` | bool | local (each side's own switch) | Master kill switch; `SettingChanged` triggers immediate apply, no restart |
| `Core.AutoStandDown.Enabled` | `true` | bool | local | Whether guard trips (below) are allowed to flip `Core.Enabled` off automatically |
| `Core.AutoStandDown.ZdoMismatchTolerance` | `50` | 10–500 | local | Sustained created/destroyed delta drift, two consecutive telemetry periods, before auto stand-down |
| `Core.AutoStandDown.ExceptionBurstLimit` | `5` | 1–50 | local | Caught-exception count inside a rolling 60 s window before auto stand-down |
| `Features.HandshakeReentryGuard.Active` | `true` | bool | local | G1-L1 (Hook 1) |
| `Features.Negotiation.Active` | `true` | bool | local | Hello handshake (Hook 2) — off disables every capability-gated feature for every peer |
| `Features.ConfigSync.Active` | `true` | bool | local | Hook 3 |
| `Features.SaveIntervalClamp.Active` | `true` | bool | local | Hook 7 |
| `Features.BackupPolicy.Active` | `true` | bool | local | Hook 8 |
| `Handshake.ProtocolVersion` | `1` | compiled constant | n/a | Not user-configurable; bumped only on a real payload-shape change |
| `Handshake.TimeoutSeconds` | `10` | 3–30 | local | Time to wait for `Hello` reply before marking a peer `KnownVanilla` |
| `ConfigSync.PushIntervalSeconds` | `30` | 5–300 | local (server-only meaningful) | Periodic re-push after negotiation, covers late capability adoption and reconnects |
| `Save.AutosaveIntervalSeconds` | `1800` | **30–1800** (hard-clamped in code) | server-authoritative, no client relevance | P1 |
| `Save.BackupCount` | `4` | **2–10** (RED-TEAM FIX: `1` is vanilla's own "disable all backups" sentinel — see Hook 8) | server-authoritative, no client relevance | P2, matches vanilla's own bootstrap default |
| `Save.BackupShortSeconds` | `7200` | ≥5 | server-authoritative | P2 |
| `Save.BackupLongSeconds` | `43200` | ≥5 | server-authoritative | P2 |
| `Telemetry.LogIntervalSeconds` | `60` | 10–600 | local | Hook 6 flush cadence |

None of this doc's server-authoritative values (`Save.*`) currently need to reach a client — they have no client-visible effect — so Phase A's `ConfigSync` payload carries only the protocol version and reserved capability bits; it is proven working now so that a later phase's genuinely client-relevant values (transport targets, simulation-distance ceilings) have a working, tested channel to use on day one.

## Telemetry

Counters (all process-lifetime unless noted, all per-peer where a peer identity is meaningful):

- `HandshakeSent`, `HandshakeNegotiated`, `HandshakeRejectedReentry`, `HandshakeVersionMismatch`, `HandshakeTimedOutAsVanilla`
- `ConfigSyncPushed`, `ConfigSyncRegisterFailed`
- `ZdosCreated`, `ZdosDestroyed`, `ZdoDelta` (derived, `created - destroyed` since last counter reset — the zero-data-loss reconciliation figure)
- `SendBudgetNearMisses` (per peer)
- `SteamSendQueueHighWaterBytes` (per peer, Steam connections only, reset each telemetry period)
- `SavesCompleted`, `SavesSkipped`, `SaveDurationMs` (last observed)
- `SafeToggleEnables`, `SafeToggleRefusedDiskSpace`, `SafeToggleRefusedSkippedSave` (RED-TEAM FIX: the forced pre-toggle save was silently a no-op — `m_loadError`/`ZoneSystem.SkipSaving()`/`DungeonDB.SkipSaving()`/`DontSaveWorld` session flag — see Hook 9)
- `KillSwitchState` (on/off), `AutoStandDownEvents` (with reason string)

Log format: one `LogInfo` line per `Telemetry.LogIntervalSeconds`, `key=value` pairs space-separated, prefixed `[NoodlesMcDoodles] telemetry` — chosen specifically so `grep "\[NoodlesMcDoodles\] telemetry"` on `LogOutput.log`/`valheim_server.log` gives a clean time series for `zdo_delta`, `send_budget_near_miss`, and `auto_standdown` without needing a log parser. See Hook 6 for the exact line shape.

## Interactions with other subsystems (and other mods)

- **Transport (G1's T-series, out of this doc's scope):** shares the same `ZNet.Awake`/`RPC_PeerInfo` hook points conceptually (T2's per-connection Steam rate config is also applied from a `RPC_PeerInfo` postfix per that lever's own `how`) — both should be separate patch classes on the same method, never merged, so either can be flagged off independently. T8's diagnostics stub fix is a natural future `ZSteamSocket`/`ZPlayFabSocket` telemetry source this doc's counter framework is built to accept without restructuring.
- **Rails/ownership (G3, G5, out of this doc's scope):** every future ownership feature (G3-L1's `IsInPeerActiveArea` postfix, O6's server-claim sweep, O9's latency-aware hand-over) is required by this doc's contract to (a) check `Core.Enabled` first, (b) use `SafeToggle` (Hook 9) before any live-authority change, and (c) register a hold-set flush callback with the kill switch per the P4×G6-N3 coupling above. This doc ships those primitives now specifically so Phase B doesn't have to invent them under schedule pressure.
- **Zone simulation / gameplay (G4, G5, out of this doc's scope):** no direct hook overlap; those subsystems consume `Telemetry.Counters` and `Features.*` the same way this doc's own hooks do, and will register their own counters into the same periodic flush rather than starting a second timer.
- **Client smoothing / delta-compression (C*, D* levers, docs 11/12, out of this doc's scope):** these are the first genuinely **client-cooperative** features likely to need `Negotiation`'s capability bits for real (beyond this doc's placeholder bit) — this doc reserves bits 1+ in the `Capabilities` mask specifically so they don't need a second handshake.
- **Other mods (AzuAntiCheat, Jotunn, Smoothbrain-ServerCharacters, BetterContinents, ValkyriesCargo, Njord, ~47 total on Proxima Maxi):** this doc's hooks patch `ZNet`/`FejdStartup`/`Game`/`ZDOMan`/`Terminal` — all frequently-patched vanilla classes on a modded server. The startup Harmony-patch-info diagnostic (Hook 5) is the mitigation; no hook in this doc transpiles a method (transpiler collisions are the sharper risk category, reserved for later-phase T6/G1-N2-style levers, not anything here). **AzuAntiCheat 5.1.1 specifically requires every client-side mod DLL to be in its `Whitelist/` or `Greylist/` folder on the server** (`BepInEx/config/AzuAntiCheat/Greylist/`, confirmed from the live config at `/home/rohan/ArchShare/ProximaMaxi/BepInEx/config/AzuAntiCheat/`) — files are compared by content hash, not name, so a new build must have its `.dll` copied into the Greylist folder (or Whitelist, if the team decides every player must run it) every time it's updated, or every player carrying it gets kicked even on a matching version. Admins in `adminlist.txt` are exempt from this check entirely (confirmed from the same README: "Admins in adminlist.txt don't need any of this"), which is how the user's own Arizona-admin testing sessions already work without a Greylist entry — but any *other* player running the client build needs the entry added, which is a server-side change the team should ask the server owner about before distributing the client DLL beyond the admin.
- **RED-TEAM FIX — Hook 1's re-entry guard has a residual scope limit worth stating explicitly.** Harmony runs every registered prefix and postfix on a shared target method regardless of what any one prefix returns — a prefix's `return false` only skips *vanilla's own body* (and, transitively, whatever that body would have called), it does not and cannot stop another mod's own independent postfix on the same method from running. Hook 1's `return false` on a detected re-entrant `PeerInfo` therefore only prevents vanilla's own `AddPeer`-doubling; it does not stop the ~30 other mods' own `ServerSync`/`ConfigSync` postfixes also registered on `ZNet.RPC_PeerInfo` (per `research/13-mod-compat-census.md`) from re-running their own side effects on every rejected resend. This is a residual amplification surface, not a defect in Hook 1 — flagged here so G1-L1 isn't read as a general fix to repeated-`PeerInfo` abuse across the whole mod stack, and so later hooks copying this "separate patch class on the same method" pattern don't assume a sibling prefix already excludes a case their own postfix still sees (see Hook 2's corrected comment above, which hit exactly this trap).
- **RED-TEAM FIX — the negotiation and config-sync RPCs travel through dispatch points several other installed mods actively guard, untested against them so far.** `research/13-mod-compat-census.md` (a Mono.Cecil metadata scan of every plugin DLL on both the live server and the admin's client profile, not speculative) found skip-capable prefixes from the user's own `Wubarrk-Wonderland` mod on both `ZRpc.HandlePackage` (Hook 2's `"NoodlesMcDoodles.v1.Hello"` dispatch point) and `ZRoutedRpc.RouteRPC`/`HandleRoutedRPC` (Hook 3's `"NoodlesMcDoodles.v1.ConfigSync"` dispatch point), plus `AzuAntiCheat`'s `RoutedRpcGuards` on the same routed-RPC methods. If any of these guards is (plausibly, per the census) a whitelist of known RPC/method names rather than a pure pass-through, it could silently veto this mod's own custom RPC before vanilla's unknown-hash fallback is ever reached — meaning the handshake channel could be dead on exactly the one server this project targets, with no crash and no obvious log signal. See the Test plan fix below.

## Test plan

**Testbed:** `/home/rohan/WubarrkCODING/ValheimMods/libs-Tools/DEDICATED-SERVER-TESTBED/` (`run-linux-1.0-server.sh`, `-savedir` pointed at the testbed's own `saves/` — never a production save directory, per P8's own guard). `saves/adminlist.txt` holds the tester's SteamID64 for `onlyAdmin` command testing.

1. **G1-L1 — re-entry guard.** Start the testbed server with the mod. Connect one real client. Using a small test harness (or a debug console command that manually re-invokes `RPC_PeerInfo`'s RPC from the client with a different self-chosen `uid`), send a second `PeerInfo` after the first join completes. Confirm via telemetry (`HandshakeRejectedReentry` increments) and via a direct check that `ZDOMan`'s peer list still has exactly one entry for that connection. Flip `Features.HandshakeReentryGuard.Active` off mid-session and confirm the same resend now succeeds (matches vanilla, doubled peer entry) — proves the flag genuinely reverts behaviour, not just suppresses a log line.
2. **Negotiation + config sync.** Connect a modded client; confirm `HandshakeNegotiated` increments on both ends and `ConfigSyncPushed` fires within one `ConfigSync.PushIntervalSeconds`. Connect a *vanilla* client (or the modded client with `Features.Negotiation.Active=false`) alongside it; confirm the vanilla peer's connection is completely unaffected (no errors in its log, normal play) while the modded peer still negotiates. Kill and reconnect the modded client; confirm negotiation restarts cleanly with no stale state (per the reconnect row in Data-loss analysis). **RED-TEAM FIX:** the clean single-mod testbed alone is not sufficient for this scenario — repeat it with `Wubarrk-Wonderland` and `AzuAntiCheat` also loaded on the testbed (both have skip-capable prefixes on the exact dispatch points `"NoodlesMcDoodles.v1.Hello"`/`"NoodlesMcDoodles.v1.ConfigSync"` travel through, per `research/13-mod-compat-census.md`), or as a short session against the real Proxima Maxi stack, and confirm `HandshakeNegotiated`/`ConfigSyncPushed` still fire — a namespaced RPC name is not automatically exempt from another mod's own RPC whitelist.
3. **Save interval / backup clamp (P1/P2).** Set `Save.AutosaveIntervalSeconds=0` in the config and confirm the live value clamps to 30, not 0 or 5 — reproduces the exact gap the lead's dispute found (`Mathf.Max(5,...)` living only in the CLI branch). Set `Save.BackupCount` via config and confirm (via `ConsiderAutoBackup` behaviour / backup file rotation on disk) it takes effect — proves the `FejdStartup.Awake` postfix ordering actually beats `ParseServerArguments`'s clobber, not just coincidentally matches vanilla's own default of a similar number.
4. **Forced-save primitive (P3).** Add a temporary test-only "toggle" that calls `SafeToggle.TryEnable`; trigger it and confirm (via `SaveDurationMs` telemetry and a file-timestamp check) a real synchronous save happened immediately before the toggle's action ran. Call it from a deliberately-wrong background thread in a debug build and confirm the `MainThreadDispatcher.AssertMainThread()` guard fires rather than silently racing.
5. **Kill switch (P4).** With the mod active and negotiated with a client, flip `Core.Enabled=false` via the `noodles disable` console command as the admin. Confirm: all hooks in this doc revert to vanilla behaviour within the same session (no restart), `HandshakeNegotiated` state stops mattering (any config-sync in flight simply stops), and the status line (`noodles status`) reports off. Re-enable and confirm it comes back cleanly. Separately, manually corrupt/exceed the mismatch tolerance via a debug-only counter injection and confirm auto stand-down fires and logs a reason, and does **not** self-re-enable.
6. **Telemetry log format (P7).** `grep "\[NoodlesMcDoodles\] telemetry"` the resulting `LogOutput.log`/`valheim_server.log` after a short session; confirm `zdo_delta` tracks player join/leave sanely and `send_budget_near_miss`/`steam_sendq_hw_*` are present and non-crashing even at zero.
7. **P8 acceptance matrix:**
   - **Scenario 1 (server-kill-mid-save), A1:** start a save (large-ish world), `kill -9` the server process mid-`SaveWorldThread`. Restart, confirm vanilla's own existing behaviour (primary generation may be bad, `.old` generation intact) is unaffected by anything in this doc — this doc adds no new mid-save write path.
   - **Scenario 2 (forced TerrainComp duplicate race):** out of this doc's scope (Z8/G6-N2), not exercised here.
   - **Scenario 3 (oversized-payload boot-loop), RED-TEAM FIX — retagged A2, not A1:** the original matrix tagged this scenario A1, but its own check ("confirm this doc's own `ConfigSync`/`Hello` payloads never approach the 512 KiB ceiling") needs Hook 2/3's payloads, which don't exist until A2 ships — it was unexecutable at an A1 checkpoint as originally tagged. Out of this doc's scope for the send path itself (Q12/G6-N4); once A2 ships, confirm the `ConfigSync`/`Hello` payloads stay a few dozen bytes — a sanity check, not a stress test.
   - **Scenarios 4–5** (disconnect storm during handover, simultaneous boundary crossings): require multiple concurrent real client connections not currently scripted anywhere in the visible tooling — deferred until that harness exists (needed regardless for later-phase ownership testing, not specific to this doc).
   - **Scenario 6** (kill mid-`.old`-fallback-swap): depends on G6-N1 shipping (out of this doc's scope) — added to this matrix once that lever ships.
8. **Live measurement with NoodlesProbe:** none of this doc's own hooks are client-visible in a way NoodlesProbe (`probe/`) currently measures (it's read-only, no-RPC, no-ZDO-write by design) — this doc's own telemetry (server-side `LogInfo`) is the evidence source for its own claims, not the probe. A future client-side counter (e.g. `HandshakeNegotiated` observed from the client's own log) could be cross-checked against the probe's session boundary if useful, but is not required for this doc's acceptance.

## Open questions

1. **Exact `Capabilities` bit layout beyond bit 0 (ConfigSync)** — left reserved/unassigned pending the client-smoothing and ownership-hand-over docs, which are the first real consumers; assigning bits speculatively here risks a mismatch with what those docs actually need.
2. **Whether `Core.AutoStandDown` should distinguish "stand down this one feature" from "stand down the whole mod"** — this doc currently auto-trips the global switch for every trigger (simplest, safest default), but a future iteration might want the ZDO-mismatch trigger to only disable whichever *feature* most plausibly caused it (impossible to attribute cleanly in Phase A since no ownership feature exists yet to be the suspect).
3. **Console command surface for a headless server with no attached admin client** — **RED-TEAM FIX: the premise of this open question was itself wrong and is now corrected by Hook 4's fix.** The original text asserted `Terminal.ConsoleCommand` with `onlyAdmin` "works when an admin issues it from their own in-game chat" — verified false this pass: `OnlyAdmin` is never read by `IsValid()`/`ShowCommand()` in either decompile, and the original code sketch's void-returning lambda bound to the constructor overload where `onlyAdmin` isn't even folded into `OnlyServer`, so the command would have run locally on whichever side typed it and never reached the server at all. Hook 4 now uses `onlyServer: true, remoteCommand: true`, which is vanilla's actual pattern for a command a remote client can run against server-side state (confirmed: every `remoteCommand: true` registration in the decompile pairs it with an explicit `onlyServer: true`; none pair it with `onlyAdmin: true`), with the real authorization coming from `RPC_RemoteCommand`'s own `ListContainsId(m_adminList, ...)` check (**SERVER:81925-81934**) — so a remote in-game admin (the project's own Arizona admin) now genuinely can flip the switch from their own client's chat. The remaining, narrower gap stands as originally framed: an operator with **shell access only** (no player connected at all) still has no in-process way to flip the kill switch except editing the config file directly (which does work, live, via `SettingChanged`) — an accepted gap, not proposing RCON.
4. **`ConfigSync.PushIntervalSeconds` default of 30 s** is a placeholder sized for "cheap enough not to matter, frequent enough to catch a reconnect quickly" — no live measurement backs this number yet; revisit once a later phase has real values worth pushing and can measure actual payload/frequency cost.
5. **P3's consumer** — this doc ships `SafeToggle` as a primitive with no caller in Phase A (nothing here flips a live-authority toggle); its first real exercise will be whichever Phase B lever (most likely G3-L1 or O6) is implemented first, and that doc should confirm `SafeToggle`'s contract still fits rather than growing a second forced-save path.

## Red-team reconciliation

Every issue from both red teams was independently re-verified against the decompiles this pass (not taken on either team's word) via `grep -n`/`sed -n` on the exact cited ranges, plus, where a claim depended on a call site or overload the red teams didn't cite, by tracing the surrounding code myself. Both blockers were **CONFIRMED and are fixed in place** above (no item needed removing from its milestone). One additional bug — not raised by either red team — was found while re-verifying RT2's Hook 4 citation and is fixed alongside it.

### Blockers (both fixed; neither required removing an item from its milestone)

1. **RT1 — `Save.BackupCount` range 1–10 includes vanilla's "disable all backups" sentinel.** **CONFIRMED**, verbatim. `ConsiderAutoBackup` (**SERVER:80761-80774**, byte-identical **CLIENT:80955-80968**): `int num2 = ((m_backupCount != 1) ? m_backupCount : 0); ... if (num2 > 0) { return SaveSystem.ConsiderBackup(...); } return false;` — a config value the doc's own clamp accepted (`BackupCount=1`) silently zeroed every backup, forever, with no log distinguishing it from a healthy 1-slot rotation. Also confirmed the finding's own char­acter-profile claim: the same `ConsiderAutoBackup` is called from **SERVER:105793** (character) as well as **SERVER:80647** (world) — one config value silently kills both paths. **Fixed:** lever table (P2), Configuration table, and Hook 8's code sketch now clamp to **2–10**, upgrading a legacy/explicit `1` to `2` with a loud `LogWarning` rather than passing the sentinel through.
2. **RT1 — `SafeToggle.TryEnable` never verified its forced save actually happened.** **CONFIRMED**, and found a second, uncited silent-skip path in the process. `ZNet.Save` (**SERVER:80221**) is `void` and has the skip branch the red team cited (`m_loadError || ZoneSystem.instance.SkipSaving() || DungeonDB.instance.SkipSaving()`, **SERVER:80229-80231**) — `ZoneSystem.SkipSaving()` (**SERVER:115732-115738**) latches permanently on `m_didZoneTest`, which vanilla's own `nextseed` (**SERVER:44349-44354**) and location-test-spawn (**SERVER:114938**) admin commands set as routine workflow, both printing "world saving DISABLED until restart". **Additionally found this pass** (not cited by either red team): an *earlier* silent-skip check, `if (SaveSystem.HasSessionFlag(SaveSystemSessionFlags.DontSaveWorld)) { ZLog.LogWarning(...); return; }` (**SERVER:80223-80227**), also public (`SaveSystem.HasSessionFlag`, **SERVER:109651-109654**). **Fixed:** Hook 9's `TryEnable` now checks all four conditions immediately after calling `Save` and refuses the toggle (rather than calling `enableAction()` regardless) if any is true, logging which one and incrementing a new `SafeToggleRefusedSkippedSave` counter.

### Majors (all fixed)

3. **RT1 #3 / RT2 #1 (duplicate finding) — disk-space guard trips the kill switch on the first failure, contradicting Hook 5's documented "twice in a row".** **CONFIRMED** — the original Hook 9 code sketch called `Core.KillSwitch.SetEnabled(false, ...)` unconditionally on the first `EnoughDiskSpaceAvailable()==false`, with no counter anywhere in the doc, while Hook 5's prose promised debouncing. Both red teams independently caught the same contradiction from the same two doc sections; no further decompile work was needed beyond confirming `EnoughDiskSpaceAvailable`'s signature (**SERVER:80265**). **Fixed:** Hook 9 now has an actual `s_diskGuardConsecutiveFailures` counter, reset on any success, tripping the kill switch only on the second consecutive failure — matching Hook 5's prose, which itself now explains *why* (archserver also autosaves two other Valheim servers, so a single blip must not stand down the fix for the Arizona problem).
4. **RT1 #4 — Hook 1's re-entry-guard prefix does uncached reflection with no try/catch, violating this doc's own hygiene rule.** **CONFIRMED.** `ZRpc.HandlePackage`'s dispatch (**SERVER:83952-83970**) has no try/catch around `value2.Invoke(this, package)`, so a null `MethodInfo` (e.g. from a future build renaming `GetPeer(ZRpc)`, private in both builds — **SERVER:80550/CLIENT:80743**) would throw uncaught, on the busiest hook in the doc, on every join. Also noticed the doc already implies its own fix: Hook 2's code sketch references `ZNetHelpers.GetPeer(__instance, rpc); // reflection wrapper, cached MethodInfo` — a helper Hook 1 should have used but didn't. **Fixed:** Hook 1 now uses the same cached `ZNetHelpers.GetPeer` wrapper and wraps its body in try/catch, feeding the exception-burst auto-stand-down counter on failure and falling back to vanilla (`return true`) rather than crashing the RPC dispatch.
5. **RT2 #2 — the negotiation/config-sync RPCs travel through dispatch points several installed mods actively guard, untested against them.** **CONFIRMED** against `research/13-mod-compat-census.md` (a Mono.Cecil metadata scan of every plugin DLL on both the live server and the admin's client profile — not speculative): `Wubarrk-Wonderland` has skip-capable prefixes on both `ZRpc.HandlePackage` and `ZRoutedRpc.RouteRPC`/`HandleRoutedRPC`; `AzuAntiCheat` has its own `RoutedRpcGuards` on the routed-RPC methods. Neither red team nor the original doc's Test plan exercised Hooks 2/3 against this stack. **Fixed:** Test plan item 2 now requires re-running the negotiation test with Wonderland + AzuAntiCheat loaded (or against the live server), not just the clean testbed.
6. **RT2 #3 — the five `nameof(...)`-based `[HarmonyPatch]` attributes only compile against a publicized reference assembly, a convention this doc never states.** **CONFIRMED.** All five targets confirmed `private` in both decompiles this pass (`RPC_PeerInfo` **SERVER:79821/CLIENT:80002**; `Awake` on `ZNet`/`Game`/`FejdStartup` at **SERVER:79204,99902,96892** / **CLIENT:79346,100491,97435**). `HANDOFF.md` documents the `BepInEx.AssemblyPublicizer.MSBuild`/`Private=False` convention this relies on, but this doc — the first to ship compilable patch code — never mentioned it. **Fixed:** added an explicit note at the top of the Hooks section stating the required build convention, with the string-literal `[HarmonyPatch(typeof(X), "Method")]` alternative noted as a no-publicizer-needed fallback.

### Minors (all fixed)

7. **RT1 #5 — Hook 2's comment mischaracterizes what filters out a rejected re-entry.** **CONFIRMED.** `IsReady()` (**SERVER:78866-78869/CLIENT:79008-79011**: `return m_uid != 0;`) stays true across a resend Hook 1 rejected, since `m_uid` was set by that peer's *first* successful pass — Harmony postfixes run regardless of a sibling prefix's return value, so the `IsReady()` check does not filter the re-entry case the comment claimed. **Fixed:** comment corrected in place; the real (and only) protection is the `HelloSent` idempotency check on the next line, called out explicitly so later hooks copying this pattern don't inherit the same false assumption.
8. **RT1 #6 — Hook 3 only registers the ConfigSync RPC once, in `ZNet.Awake`, so a live off→on flag flip can't re-register it.** **CONFIRMED** — the original code sketch's `if (!Features.ConfigSync.Active) return;` sat *before* `Register`, and `Awake` runs once per world load. **Fixed:** registration is now unconditional at `Awake`; `Features.ConfigSync.Active` gates only the send/receive logic, so there is nothing left for a live flag flip to fail to redo.
9. **RT2 #4 — P8's Scenario 3 is tagged executable at A1 but needs A2's Hello/ConfigSync payloads.** **CONFIRMED**, self-inconsistency in the original doc (lever table said "A1 (1–3)"; Test plan item 7 described a check against A2-scoped payloads). **Fixed:** lever table and Test plan both now tag Scenario 3 as A2.
10. **RT2 #5 — `SendBudgetNearMisses` would fire on nearly every idle tick if keyed on `SendZDOs`'s raw `bool` return.** **CONFIRMED.** `SendZDOs` (**SERVER:77055-77070**) has three distinct false-return paths (queue-over-gate, real near-miss, and "nothing queued" — the common, harmless case). **Fixed:** the postfix now recomputes the near-miss condition itself from `GetSendQueueSize()` instead of trusting `__result`.
11. **RT2 #6 — Hook 4's admin-list citation points at the wrong mechanism.** **CONFIRMED**, and re-deriving the right one surfaced a real bug (see "Additional finding" below). The cited lines (`RPC_Save`/`RPC_Kick`/etc.) are each handler's own independent check; the actual enforcement for a remote console command is `RPC_RemoteCommand` (**SERVER:81925-81934**), reached via `OnlyServer = onlyServer || onlyAdmin` (**SERVER:43137**, one of two constructor overloads — see below). **Fixed:** citation corrected in Hook 4.
12. **RT2 #7 — Hook 1's re-entry guard doesn't stop other mods' own postfixes on the same method; worth an explicit caveat.** **CONFIRMED** as a correct statement of Harmony's prefix/postfix independence (the same semantics Hook 5's own startup diagnostic already exists to surface, applied here to a spot the doc hadn't yet applied it). **Fixed:** added an explicit caveat to "Interactions with other subsystems".

### Additional finding (this reconciliation pass, not raised by either red team)

13. **Hook 4's `noodles` console command, as originally coded, could never actually be run by a remote in-game admin — only locally, on whichever side typed it.** Found while re-deriving RT2 #6's correct citation. `ConsoleCommand` has two constructor overloads (**SERVER:43137/43155**): only the `ConsoleEventFailable` (object-returning) one folds `onlyAdmin` into `OnlyServer`; the `ConsoleEvent` (void-returning) one does not. The original code sketch's lambda (bare `return;`, falls off the end) is void-returning and can only bind to the second overload, so `OnlyServer` stayed `false` and `onlyAdmin: true` had **no enforcement effect at all** — `OnlyAdmin` (**SERVER:43118**) is written by both constructors but never read anywhere in either decompile. With `OnlyServer==false`, `Terminal.IsValid()` (**SERVER:43238-43253**) returns `true` unconditionally on both sides, so the command ran **locally**: an admin typing `noodles disable` on their own client would flip their client's own switch, never the server's — and without `remoteCommand: true` (also absent from the original sketch), `TryRunCommand` (**SERVER:45782-45806**) had no path to forward it to the server at all. Confirmed this is not how vanilla does remote-admin commands: every `remoteCommand: true` registration in the server decompile (13 found) pairs it with an explicit `onlyServer: true`; none pair it with `onlyAdmin: true`. **Fixed:** Hook 4 now registers with `onlyServer: true, remoteCommand: true, onlyAdmin: false`, routing a remote admin's command through vanilla's own `RPC_RemoteCommand`, whose `ListContainsId(m_adminList, ...)` check is the actual (and now correctly cited) authorization gate. Open Question 3 updated to match — its original premise ("works when an admin issues it from their own in-game chat") was the very thing this bug broke.

### Not contested

RT2's own verdict noted the architecture (kill switch, handshake, config sync, telemetry, save/backup guards), the A1/A2 milestone split, and the vanilla-compatibility reasoning (silent-miss dispatch, network-version gate, vanilla/modded-peer interop) all checked out against both decompiles with no blocker-level design flaw — re-confirmed during this pass via the spot-checks above (RPC_PeerInfo signature/registration, `IsConnected`, `AddPeer`'s lack of a duplicate check), all exact matches. Nothing was downgraded, deferred, or rejected: every issue from both red teams held up, and the one additional bug found during reconciliation is fixed alongside them.
