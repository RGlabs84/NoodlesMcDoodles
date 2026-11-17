# Transport, send queues and bitrates (Phase A)

> Ground truth citations `SERVER:<line>` / `CLIENT:<line>` are 1.0.16 (network version 40), re-read directly from
> `assembly_valheim_SERVER.decompiled.cs` / `assembly_valheim.decompiled.cs` during this pass (not merely copied
> from research/01 or research/10-LEVERS — every load-bearing line cited below was opened again with `grep -n`/`sed -n`
> and compared against the claim). Where this doc's own re-verification differs from a research doc, this doc's
> finding wins and the difference is called out inline.

## Summary

This module (`src/Transport/`, feature-flag root `NoodlesMcDoodles.Core.Features.Transport`) owns everything between
a `ZDO`/RPC being handed to the network layer and it leaving/arriving on the wire: the Steam `SendRateMin`/`SendRateMax`
ceiling, the `ZRpc` ping/timeout clock, the raw-TCP fallback socket's buffer, server-side telemetry that vanilla
dedicated servers don't expose, and — the largest piece — `ZDOMan`'s per-peer send queue: how many peers get serviced
per tick, how many bytes each gets, what order their ZDOs go out in, and the guards that stop an oversized or
unbounded write from tripping Valheim's own "peek-not-dequeue" boot loop.

None of this decides *who owns what* or *what gets simulated where* — that is the ownership/zonesim subsystems. This
module only decides how fast, how often, and in what order bytes already queued for a peer actually leave the
process, and it puts a floor under two structurally-unbounded vanilla write paths (`SendDestroyed`'s destroy list,
`ZDOPeer.m_invalidSector`) that are the real "oversized packet" data-loss risk in the game today, before this mod
changes anything else.

**Milestones**

- **M1 (this phase, "Phase A")**: T1, T2, T3, T4, T5, T8, and the unified `ZDOMan.SendZDOs` budget rewrite (Q3, Q4,
  Q5, Q12, G2-N1), plus Q7 (`SendDestroyed` chunking), Q6 (send-priority bias), Q8 (force-send helper), Q2 (k-peers-
  per-tick), and G6-N4 (a library guard, not a vanilla hook, for future mod-added ZDO fields). All server-side by
  default; every lever also has a client-side twin gated behind its own opt-in flag (Q10) for a player who installs
  the mod client-side.
- **M2 (later, out of this doc's scope)**: T6/T9 (`ZPlayFabSocket` tuning, adaptive per-connection controller),
  T7/G1-N1 (compressed custom side-channel RPC) — deferred because T9 is `NEEDS-LIVE-TEST` and G1-N1 needs the
  handshake/negotiation primitive this doc assumes exists but doesn't itself build.

**What this fixes for the Arizona case, today, and what it does not.** The Arizona/NJ problem is that vanilla gives
zone ownership to whichever player is there first and never moves it (see `03-ownership-and-authority.md` /
`04-server-zone-simulation.md` — not this doc's scope). While he owns those zones, **his own client uploads that
zone's ZDO traffic to the server**, capped by his own client's Steam `SendRateMax`, pinned to 153,600 B/s
(150 KB/s) by vanilla (`ZSteamSocket.RegisterGlobalCallbacks`, SERVER:86261-86274 / CLIENT:86512 area, re-verified
below — see §Hooks, T1). Every hook in this document that runs **only on the server** (T1/T2's server-side global
and per-connection Steam rate, T3/T4's timeout tuning, Q2/Q3/Q5/Q6/Q7/Q12/G2-N1's send-queue changes) raises how
fast the **server** can push bytes **out to** Arizona and to every other player, and makes the server's own queues
safer under load. **None of it touches Arizona's own upload cap**, because that cap is set by his client process's
own `ZSteamSocket.RegisterGlobalCallbacks` call, a completely separate call site in the client assembly that this
mod's server-side patches never reach. The one lever in this catalog that *directly* helps the Arizona case today,
before any ownership/zonesim feature ships, is **Q10 + a client-side T1 analog**: if Arizona runs
NoodlesMcDoodles client-side with those flags on, his own `ZSteamSocket.RegisterGlobalCallbacks` call gets the same
raised `SendRateMin`/`SendRateMax` the server gets, directly relieving the actual bottleneck (his own upload of the
zone he currently owns) under today's vanilla ownership rules. Server-side Phase A alone does not move the needle
on his upload; it is necessary infrastructure (safe queues, telemetry, the shared negotiation/config-push rails
other subsystems will use) but the immediate Arizona relief requires the client-side opt-in too.

**RED-TEAM FIX** (RT1, major): this claim — that only the explicit `Q10` opt-in touches a client's own upload —
was **not** actually true of Hooks 1 and 2 as originally sketched. Both are marked "Side: both" and originally
branched only on `ZNet.instance.IsDedicated()` to pick a Steam API class, with no gate on *whether* to apply beyond
their own `T1`/`T2`/`T3` flags; since `IsDedicated()` is hardcoded `false` on every client build regardless of
whether that client is a remote player or a self-hosting listen-server, a normal remote client running the mod
with M1's server-default `T1`/`T2` flags left on — without ever touching `Q10` — would have silently gotten its own
upload ceiling raised, contradicting this paragraph. Both hooks now explicitly gate their non-authoritative-process
branch behind `Q10.Active` (see the RED-TEAM FIX notes under Hooks 1 and 2 above), which makes this paragraph's
claim actually true of the code as specified, rather than only true of the intent.

---

## Levers covered

| Lever | Verdict (10-LEVERS, LEAD-CORRECTED noted) | User target | Recommended value/range | Milestone |
|---|---|---|---|---|
| T1 | SAFE-WITH-GUARDS | `1048576 B/s` (1 MiB/s) global | 1,048,576 B/s global `SendRateMin=Max`, config-bound, hard-clamped ≤ ~70% of operator's stated uplink (300 Mbps here ⇒ no code-level ceiling bites) | M1 |
| T2 | SAFE-WITH-GUARDS | `4194304 B/s` join burst per connection | 4,194,304 B/s per-connection override for a bounded burst window, then revert to T1's value | M1 |
| T3 | SAFE-WITH-GUARDS | `60000` | `TimeoutConnected` = 60,000 ms (above vanilla's 30,000 ms floor per the lever's own revised-floor guard) | M1 |
| T4 | SAFE-WITH-GUARDS | open — resolved here | ping interval 1.0 s (vanilla, unchanged); disconnect timeout 60.0 s; re-assert 60 s after `ZNet.Start()` (resets to 30) and after any PlayFab join/accept (forces 90) | M1 |
| T5 | SAFE | `128k` | `ZSocket2.SendBufferSize` = 131,072 B (128 KiB), clamped ≤ 262,144 | M1 |
| T8 | SAFE | (on) | Enable server-side telemetry stubs; fix `GetConnectionQuality`'s server-build API mismatch | M1 |
| Q2 | SAFE-WITH-GUARDS | `2-` (incomplete) | **Recommend**: adaptive k, 2–8, frame-time-capped, default k=4 when frame time allows — see Open questions for the fixed-4 alternative | M1 |
| Q3 | SAFE-WITH-GUARDS | `32768` | 32,768 B inner budget, hard-clamped ≤ 131,072 in code | M1 |
| Q4 | SAFE | `4096` | 4,096 B congestion floor | M1 |
| Q5 | SAFE-WITH-GUARDS | `32768` (must equal Q3) | 32,768 B outer gate, driven from the **same** config field as Q3 (one knob, not two) | M1 |
| Q6 | SAFE-WITH-GUARDS | (on) note: "must be effective yet memory efficient" | Re-weight `ServerSortSendZDOS`'s sort key with an O(1) server-held-zone lookup (no per-ZDO allocation); keep the anti-starvation term | M1 |
| Q7 | SAFE-WITH-GUARDS | (on) | Chunk `SendDestroyed` at 8,000 IDs/chunk (~96 KB), deferred (not dropped) on backpressure | M1 |
| Q8 | SAFE | (on) | `ForceSendZDO`/`AddForceSendZdos`, batch-capped at 1,000 IDs per call | M1 |
| Q10 | SAFE | (on) | Same Q1/Q3/Q4/Q5 literals, client build, opt-in flag, off by default | M1 (opt-in) |
| Q12 | SAFE-WITH-GUARDS | (on) | Running-total guard vs. 524,288 B (512 KiB) inside the unified `SendZDOs` budget rewrite; skip, log, retry — never truncate | M1 |
| G2-N1 | SAFE-WITH-GUARDS | (on) | Chunk `m_invalidSector`'s write inside the same unified rewrite | M1 |
| G6-N4 | SAFE-WITH-GUARDS | (on) | Library guard (`Core.Ownership`/future-module call sites), not a vanilla hook — reject any mod-added ZDO field write > 2 KB | M1 (library, no vanilla patch) |

Two levers named in the task's scope list (Q3 and Q5) are, in the actual vanilla method, two **textually distinct**
10240-literal sites inside one `SendZDOs` method (SERVER:77058 outer gate, SERVER:77062 inner-budget-complement —
**RED-TEAM FIX** (RT2, minor): corrected from 77057/77062, re-verified this pass — see §Hooks). This doc ships them, Q4, Q12 and G2-N1 as **one coordinated patch**, not five independent ones, because
all five read/write the same `zPackage` before/around the same per-item loop (10-LEVERS "Coupled levers": Q3+Q5,
Q3/Q5+Q12+G2-N1). A separate patch per lever would let two of the five disagree about "how much budget is left."

---

## Hooks

Shared infrastructure used by every hook below: `NoodlesMcDoodles.Core.Features.Transport.<LeverName>.Active` (bool,
kill-switch-gated), `NoodlesMcDoodles.Core.Config` (BepInEx `ConfigFile`, server-local values — **RED-TEAM FIX**
(RT2, major): not pushed to clients over any RPC in M1, see §Configuration and §Red-team reconciliation item 6 for
why), `NoodlesMcDoodles.Core.Telemetry` (per-peer counters —
see §Telemetry).

### Hook 1 — Global Steam rate + timeout (T1, T3)

**Not** a Harmony patch on `ZSteamSocket.RegisterGlobalCallbacks`. That method's own body is guarded by
`if (m_statusChanged == null)` so vanilla's *effect* only happens once per process — but the method itself is called
from **all four** `ZSteamSocket` constructors (host/accept paths for both builds), so a naive postfix on it would
re-run the mod's own `SetConfigValue`/`GCHandle.Alloc` once per **connection**, leaking a `GCHandle` per join. Both
`10-LEVERS.md` T1's guard #2 and this pass's direct read of the four constructor call sites confirm this.

- **Type.Method**: `NoodlesMcDoodles.Transport.SteamRateController.ApplyGlobalConfig()` — new method, called once
  from a postfix on `ZNet.Start()`.
- **`private void ZNet.Start()`** — SERVER:79301-79304 / CLIENT:79448-79451, confirmed this pass: both bodies open
  with `ZRpc.SetLongTimeout(enable: false);` as their literal first statement, identical in both builds.
- **Patch kind**: Harmony `[HarmonyPostfix]` on `ZNet.Start`.
- **Side**: both (branches internally on `ZNet.instance.IsDedicated()` — SERVER:81194-81197 hardcoded `true`,
  CLIENT:81380-81383 hardcoded `false`, re-confirmed structurally unchanged from research/01 §2) — **but see the
  RED-TEAM FIX below**: this branch alone is not sufficient to keep the effect server-side-only.
- **Why this hook, not `RegisterGlobalCallbacks`**: `ZNet.Start()` runs exactly once per `ZNet` session (both
  builds), which is exactly the "once per process/session" cadence T1/T3 need — no per-connection re-entry, no
  GCHandle churn, and it's the same place T4 already needs to visit for the same reason (see Hook 2).

**RED-TEAM FIX** (RT1, major): the original sketch gated this whole method on nothing but `Features.Transport.T1/
T3.Active`, and used `IsDedicated()` only to pick which Steam API class to call — not to decide *whether* to apply
at all. `ZNet.Start()` runs on **every** `ZNet` session, including a normal remote client that is not hosting
anything: on that process `IsDedicated()` is hardcoded `false`, so the `else` branch fires and calls
`SteamNetworkingUtils.SetConfigValue` against that client's **own** outbound socket — exactly the "client-side T1
analog" the Summary (above) reserves for the explicit, default-off `Q10` opt-in. As written, any player who installs
the client-side mod for *any* reason (accurate telemetry, say) and leaves the M1-default `T1`/`T3` flags on — without
ever touching `Q10` — would silently get their own upload ceiling raised the moment their `ZNet.Start()` fires,
contradicting the Summary's own claim that this requires a separate, deliberate opt-in. The Milestones section
already states the *intent* ("every lever also has a client-side twin gated behind its own opt-in flag (Q10)") —
this fix makes Hook 1's code match that stated intent. Fixed by distinguishing "this process is authoritative" (the
dedicated server, **or** a listen-server host — `ZNet.IsServer()`, SERVER:81184-81186/CLIENT:81370-81372, `return
m_isServer;`, true for both) from "this process is a genuine remote client" (`IsServer() == false` — the only case
where applying a local Steam override changes *that player's own* upload behavior against someone else's server),
and requiring `Q10.Active` specifically for the latter. `IsDedicated()` is still used exactly as before, to pick
which Steam API class a call site must use, since a listen-server host is `IsServer()==true` but `IsDedicated()==false`
and must still call the client-side `SteamNetworkingUtils` class (it doesn't run the GameServer API) even though
it *is* the authoritative side for gating purposes.

```csharp
// src/Transport/SteamRateController.cs
[HarmonyPatch(typeof(ZNet), "Start")]
static class ZNet_Start_TransportInit
{
    static void Postfix()
    {
        if (!Features.Transport.Active) return; // kill switch: run nothing, vanilla's own Start() already ran
        SteamRateController.ApplyGlobalConfig(); // idempotent: internal `s_applied` guard, safe if called again
    }
}

public static class SteamRateController
{
    private static bool s_applied;

    public static void ApplyGlobalConfig()
    {
        if (s_applied) return;
        s_applied = true;
        if (!Features.Transport.T1.Active && !Features.Transport.T3.Active) return;
        // RED-TEAM FIX (RT1, major): a genuine remote client (not this process's own server/listen-host) only
        // gets this hook's effect if it has explicitly opted in via Q10 — see the note above this code block.
        bool isAuthoritative = ZNet.instance.IsServer(); // true for the dedicated server AND a listen-server host
        if (!isAuthoritative && !Features.Transport.Q10.Active) return;

        int rate = Features.Transport.T1.Active ? Config.T1_GlobalRateBps.Value : 153600; // vanilla literal when off
        int timeoutMs = Features.Transport.T3.Active ? Config.T3_TimeoutConnectedMs.Value : 30000;

        GCHandle rateHandle = GCHandle.Alloc(rate, GCHandleType.Pinned);
        GCHandle timeoutHandle = GCHandle.Alloc((float)timeoutMs, GCHandleType.Pinned);
        try
        {
            if (ZNet.instance.IsDedicated())
            {
                SteamGameServerNetworkingUtils.SetConfigValue((ESteamNetworkingConfigValue)10 /*SendRateMin*/, (ESteamNetworkingConfigScope)1, IntPtr.Zero, (ESteamNetworkingConfigDataType)1, rateHandle.AddrOfPinnedObject());
                SteamGameServerNetworkingUtils.SetConfigValue((ESteamNetworkingConfigValue)11 /*SendRateMax*/, (ESteamNetworkingConfigScope)1, IntPtr.Zero, (ESteamNetworkingConfigDataType)1, rateHandle.AddrOfPinnedObject());
                SteamGameServerNetworkingUtils.SetConfigValue((ESteamNetworkingConfigValue)25 /*TimeoutConnected*/, (ESteamNetworkingConfigScope)1, IntPtr.Zero, (ESteamNetworkingConfigDataType)3, timeoutHandle.AddrOfPinnedObject());
            }
            else
            {
                SteamNetworkingUtils.SetConfigValue((ESteamNetworkingConfigValue)10, (ESteamNetworkingConfigScope)1, IntPtr.Zero, (ESteamNetworkingConfigDataType)1, rateHandle.AddrOfPinnedObject());
                SteamNetworkingUtils.SetConfigValue((ESteamNetworkingConfigValue)11, (ESteamNetworkingConfigScope)1, IntPtr.Zero, (ESteamNetworkingConfigDataType)1, rateHandle.AddrOfPinnedObject());
                SteamNetworkingUtils.SetConfigValue((ESteamNetworkingConfigValue)25, (ESteamNetworkingConfigScope)1, IntPtr.Zero, (ESteamNetworkingConfigDataType)3, timeoutHandle.AddrOfPinnedObject());
            }
            Telemetry.Transport.GlobalRateApplied(rate, timeoutMs);
        }
        finally
        {
            rateHandle.Free();
            timeoutHandle.Free();
        }
    }
}
```

**Flag off**: `ApplyGlobalConfig` returns before allocating anything or calling `SetConfigValue` at all — vanilla's
own `RegisterGlobalCallbacks` call (unaffected by this patch) still runs on the first `ZSteamSocket` constructor and
sets the vanilla 153600/30000 pin exactly as today. The postfix itself still fires (cheap no-op) — the kill switch
short-circuits inside `ApplyGlobalConfig`/`ApplyGlobalConfig`'s two `Features.*.Active` checks, never inside the
Harmony patch registration, so re-enabling at runtime needs no re-patch.

### Hook 2 — Per-connection join burst (T2)

- **Type.Method**: `private void ZNet.RPC_PeerInfo(ZRpc rpc, ZPackage pkg)` — SERVER:79821 / CLIENT:80002, confirmed
  this pass: both signatures identical, both bodies end their `m_isServer` success branch with
  `peer.m_socket.VersionMatch(); SendPlayerList(); ...` (SERVER:79981-79986) immediately followed by
  `m_zdoMan.AddPeer(peer); m_routedRpc.AddPeer(peer);` (confirmed at the tail of the method this pass).
- **Patch kind**: `[HarmonyPostfix]`.
- **Side**: both — applies unconditionally when **this process** is the dedicated server or a listen-server host
  (`ZNet.IsServer()`); when this process is a genuine remote client, applies **only** under the `Q10` opt-in — see
  the RED-TEAM FIX below, same reasoning as Hook 1.
- **Why this hook**: it is the first point after the version/auth handshake where `peer.IsReady()`
  (`m_uid != 0`, SERVER:78866-78869, confirmed) is guaranteed true and `peer.m_socket` is known to be live — the
  same point vanilla itself uses to call `VersionMatch()` and start sending player lists.

**Re-entry hazard, re-verified this pass, more precisely than `10-LEVERS.md` states it**: `"PeerInfo"` is
registered exactly once (`peer.m_rpc.Register<ZPackage>("PeerInfo", RPC_PeerInfo);`, SERVER:79685) and never
unregistered (`grep -n "PeerInfo"` finds only that one `Register` and one `Invoke` site, SERVER:79818, in the whole
file). `RPC_PeerInfo` **does** contain an `IsConnected(uid)` check (SERVER:79993-79999, `IsConnected` at
SERVER:80040-80053 — this doc found this check; `research/10-LEVERS.md`'s G1-L1 finding, which is out of this doc's
scope, describes the method as having "no already-joined check" without qualification) — but `IsConnected(long uid)`
walks `m_peers` comparing `peer.m_uid == uid` against the **`uid` value read fresh from the packet on that call**
(`pkg.ReadLong()`, SERVER:79830), not against the calling peer's own identity. A second `"PeerInfo"` RPC replayed on
the *same already-established connection* with a **different** claimed `uid` sails past this check (the old `uid`
is the only one in `m_peers`; the new claimed `uid` isn't), reaches `IL_04a4`, overwrites `peer.m_uid`, and calls
`m_zdoMan.AddPeer(peer)`/`m_routedRpc.AddPeer(peer)` again — both of which are unconditional `List.Add`s with no
duplicate check (`ZDOMan.AddPeer`, SERVER:76814-76820; `ZRoutedRpc.AddPeer`, SERVER:83573-83580, both confirmed this
pass) and both of which `Register<ZPackage>` a handler on the same `ZRpc` a second time. This module's postfix must
not assume vanilla's `IsConnected` check protects it.

**Guard, therefore**: key the mod's own burst-applied state by the `ZNetPeer` object identity (a
`ConditionalWeakTable<ZNetPeer, BurstState>`), not by `uid`, and check-and-set it before doing any Steam API call —
a replayed `PeerInfo` on the same `ZNetPeer` object is a no-op for this hook even though vanilla's own `AddPeer`
calls are not protected (that gap belongs to lever G1-L1, out of scope here, and should be fixed independently).

**RED-TEAM FIX** (RT1, major — same finding and fix shape as Hook 1): `RPC_PeerInfo` runs on **whichever side
receives** the RPC, which includes a genuine remote client receiving the server's reply as part of its own join
handshake — on that process `ZNet.instance.IsDedicated()` is hardcoded `false`, and the original sketch's
`ApplyBurst(peer, ZNet.instance.IsDedicated())` call had no gate beyond `Features.Transport.T2.Active`, so it would
apply the join-burst override to the *client's own* outbound socket to the server the moment any player with the
client mod installed and `T2` left at its M1 default joins — again silently delivering the "client-side T2 analog"
the Summary reserves for the explicit `Q10` opt-in. Fixed the same way as Hook 1: gate on `ZNet.instance.IsServer()`
(true for the dedicated server and a listen-server host, both of which should keep getting T2 by default) OR
`Features.Transport.Q10.Active` (the deliberate client opt-in).

```csharp
// src/Transport/JoinBurstController.cs
[HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
static class ZNet_RPCPeerInfo_JoinBurst
{
    static void Postfix(ZNet __instance, ZRpc rpc, ZPackage pkg)
    {
        if (!Features.Transport.T2.Active) return;
        // RED-TEAM FIX (RT1, major): same isAuthoritative/Q10 gate as Hook 1 — see the note above this code block.
        if (!ZNet.instance.IsServer() && !Features.Transport.Q10.Active) return;
        ZNetPeer peer = __instance.GetPeer(rpc); // same lookup vanilla just did
        if (peer == null || !peer.IsReady()) return; // handshake didn't succeed this call
        if (!(peer.m_socket is ZSteamSocket)) return; // T2 only defined for the Steam path

        if (JoinBurstController.s_state.TryGetValue(peer, out _)) return; // idempotent: already bursting/burst-done
        var state = new JoinBurstController.BurstState { AppliedAt = Time.realtimeSinceStartup };
        JoinBurstController.s_state.Add(peer, state);

        JoinBurstController.ApplyBurst(peer, ZNet.instance.IsDedicated());
        Telemetry.Transport.JoinBurstApplied(peer.m_uid);
    }
}

public static class JoinBurstController
{
    public class BurstState { public float AppliedAt; public bool Reverted; }
    internal static readonly ConditionalWeakTable<ZNetPeer, BurstState> s_state = new();

    public static void ApplyBurst(ZNetPeer peer, bool dedicated)
    {
        var sock = (ZSteamSocket)peer.m_socket;
        HSteamNetConnection con = AccessTools.FieldRefAccess<ZSteamSocket, HSteamNetConnection>(sock, "m_con");
        int rate = Config.T2_JoinBurstBps.Value;
        GCHandle h = GCHandle.Alloc(rate, GCHandleType.Pinned);
        try
        {
            const int scopeConnection = 4;
            if (dedicated)
            {
                SteamGameServerNetworkingUtils.SetConfigValue((ESteamNetworkingConfigValue)10, (ESteamNetworkingConfigScope)scopeConnection, (IntPtr)con.m_HSteamNetConnection, (ESteamNetworkingConfigDataType)1, h.AddrOfPinnedObject());
                SteamGameServerNetworkingUtils.SetConfigValue((ESteamNetworkingConfigValue)11, (ESteamNetworkingConfigScope)scopeConnection, (IntPtr)con.m_HSteamNetConnection, (ESteamNetworkingConfigDataType)1, h.AddrOfPinnedObject());
            }
            else
            {
                SteamNetworkingUtils.SetConfigValue((ESteamNetworkingConfigValue)10, (ESteamNetworkingConfigScope)scopeConnection, (IntPtr)con.m_HSteamNetConnection, (ESteamNetworkingConfigDataType)1, h.AddrOfPinnedObject());
                SteamNetworkingUtils.SetConfigValue((ESteamNetworkingConfigValue)11, (ESteamNetworkingConfigScope)scopeConnection, (IntPtr)con.m_HSteamNetConnection, (ESteamNetworkingConfigDataType)1, h.AddrOfPinnedObject());
            }
        }
        catch (Exception e) { ZLog.LogWarning($"[NoodlesMcDoodles] T2 join-burst reflection failed: {e}"); return; }
        finally { h.Free(); }
    }

    // Called once per second from a lightweight ZNet.Update postfix (not shown), for every peer with an
    // un-reverted BurstState: revert once GetSendQueueSize() drops under Q4's floor, or after a max duration.
    public static void TickReverts(IEnumerable<ZNetPeer> peers)
    {
        foreach (var peer in peers)
        {
            if (!s_state.TryGetValue(peer, out var st) || st.Reverted) continue;
            bool drained = peer.m_socket.GetSendQueueSize() < Config.Q4_CongestionFloorBytes.Value;
            bool timedOut = Time.realtimeSinceStartup - st.AppliedAt > Config.T2_MaxBurstSeconds.Value; // default 30s
            if (drained || timedOut)
            {
                RevertToGlobalRate(peer, ZNet.instance.IsDedicated()); // same call shape, value = T1's rate
                st.Reverted = true;
                Telemetry.Transport.JoinBurstReverted(peer.m_uid, drained ? "drained" : "timeout");
            }
        }
    }
}
```

**Flag off**: the postfix returns before touching `s_state` or calling any Steam API — the connection simply keeps
whatever scope the global config (T1, or vanilla's 153600 if T1 is also off) already set. No revert step is needed
because no override was ever applied.

**Lifecycle (explicit, since the task asks for it)**: (1) applied once, idempotently, right after handshake success
in `RPC_PeerInfo`; (2) held at 4,194,304 B/s while the connection's own `GetSendQueueSize()` (SERVER:86620-86636,
confirmed this pass — sums the software queue plus Steam's own `m_cbPendingReliable`/`m_cbPendingUnreliable`/
`m_cbSentUnackedReliable`) stays above Q4's 4,096 B floor, i.e. while there's still a real backlog to drain faster;
(3) reverted to T1's global value the moment the queue drains below that floor, or after a 30 s hard cap, whichever
comes first; (4) on disconnect (`ZNet.RemovePeer` — not itself patched by this hook) the `ConditionalWeakTable`
entry is simply unreachable once `peer` is unreferenced and is garbage-collected; no explicit cleanup call is
required, and a reconnect creates a new `ZNetPeer` object with a fresh (absent) table entry, so the burst logic
runs again from scratch — correct behavior, not a bug, since a reconnect is exactly a new join.

### Hook 3 — `ZRpc` ping/timeout (T4)

- **Type.Method**: direct writes to `private static float ZRpc.m_pingInterval` (SERVER:83859 / same field, CLIENT
  confirmed present in `ZRpc`'s shared-IL body — `ZRpc` has no build-specific branch anywhere in this doc's re-read,
  consistent with research/01's "byte-identical IL" finding for this class) and
  `private static float ZRpc.m_timeout` (SERVER:83861). Both are plain `private static float` fields, not `const`,
  reachable via `AccessTools.StaticFieldRefAccess<float>(typeof(ZRpc), "m_timeout")` — no Harmony patch needed for
  the write itself.
- **Patch kind**: (a) direct reflected static-field writes, called from three postfixes that re-assert the value
  whenever vanilla itself overwrites the static field; (b) `[HarmonyPostfix]` on `ZNet.Start()` (same hook as Hook 1,
  additional line), on `ZPlayFabSocket.OnNetworkJoined(object sender, string networkId)`
  (SERVER:91357-91365 / CLIENT:91840-91847 confirmed this pass, byte-identical body ending in
  `ZRpc.SetLongTimeout(enable: true);`), and on `ZPlayFabSocket.Accept()`
  (SERVER:91712-91719 / CLIENT:92200-92208 confirmed this pass, same shape).
- **Side**: both.
- **Why this hook**: `ZRpc.SetLongTimeout(bool)` (SERVER:84201-84210) is the *only* other writer of `m_timeout`
  (`grep -n "m_timeout ="` inside `ZRpc`'s own body finds exactly this method and the field initializer). Vanilla
  calls it with `false` (→30s) once, unconditionally, as the first statement of `ZNet.Start()`, and with `true`
  (→90s) from two PlayFab call sites that fire on **every** crossplay join/accept, not just the first. Since the
  field is `static`, not per-instance, any of these three vanilla call sites silently clobbers this mod's configured
  60 s value process-wide. Re-asserting after each is the only way to keep a stable configured value across a
  session that includes a PlayFab peer.

```csharp
// src/Transport/RpcTimeoutController.cs
public static class RpcTimeoutController
{
    public static void Reassert()
    {
        if (!Features.Transport.T4.Active) return;
        AccessTools.StaticFieldRefAccess<float>(typeof(ZRpc), "m_pingInterval") = Config.T4_PingIntervalSec.Value; // default 1.0f, vanilla's own value
        AccessTools.StaticFieldRefAccess<float>(typeof(ZRpc), "m_timeout") = Config.T4_TimeoutSec.Value;           // default 30.0f (vanilla; fixer pass)
    }
}

[HarmonyPatch(typeof(ZNet), "Start")]
static class ZNet_Start_TimeoutInit { static void Postfix() => RpcTimeoutController.Reassert(); } // combine with Hook 1's postfix in the real source file

[HarmonyPatch(typeof(ZPlayFabSocket), "OnNetworkJoined")]
static class PlayFab_OnNetworkJoined_ReassertTimeout { static void Postfix() => RpcTimeoutController.Reassert(); }

[HarmonyPatch(typeof(ZPlayFabSocket), "Accept")]
static class PlayFab_Accept_ReassertTimeout { static void Postfix() => RpcTimeoutController.Reassert(); }
```

**Flag off**: `Reassert()` returns immediately; `m_pingInterval`/`m_timeout` are left exactly as vanilla's own three
call sites set them (1s / 30s, 90s while any PlayFab peer's socket is mid-join/accept) — indistinguishable from an
unpatched game.

**Guard, per 10-LEVERS T4**: never call `SetLongTimeout(true)` reactively for a single struggling peer — the field
is process-wide, so that would loosen every peer's timeout. This hook only ever reasserts one configured value; it
never targets one peer.

### Hook 4 — Raw-TCP send buffer (T5)

- **Type.Method**: `private static void ZSocket2.ConfigureSocket(TcpClient socket)` — SERVER:84710-84714 /
  CLIENT:84940-84944 (confirmed this pass, identical body: `socket.SendBufferSize = 2048;` as the sole statement of
  interest, `NoDelay = true` alongside it).
- **Patch kind**: `[HarmonyPostfix]`.
- **Side**: both.
- **Why this hook and not a transpiler**: `ConfigureSocket` runs once per socket at connect/accept time (not a hot
  loop), so a postfix overwriting `socket.SendBufferSize` after vanilla's own assignment is simpler and equally
  safe — no IL-collision risk with another mod patching the same rare, narrow method the way a hot-path transpiler
  (Hook 5/6 below) would need to worry about.

```csharp
[HarmonyPatch(typeof(ZSocket2), "ConfigureSocket")]
static class ZSocket2_ConfigureSocket_BufferSize
{
    static void Postfix(TcpClient socket)
    {
        if (!Features.Transport.T5.Active) return;
        int size = Math.Min(Config.T5_SendBufferBytes.Value, 262144); // hard clamp, ignores an out-of-range config value
        socket.SendBufferSize = size;
    }
}
```

**Flag off**: postfix no-ops; `SendBufferSize` stays at vanilla's 2048.

### Hook 5 — Diagnostics (T8)

Three independent, all-additive pieces:

- **`ISocket.GetAndResetStats(out int totalSent, out int totalRecv)`** — no patch. Confirmed dead in vanilla
  (`grep -n "\.GetAndResetStats("` returns zero call sites in either assembly outside the interface/implementations
  themselves). Called directly, once per second per peer, from the telemetry loop (see §Telemetry).
- **`public void ZNet.GetNetStats(out float, out float, out int, out float, out float)`** — SERVER:81567-81574,
  confirmed this pass: still a five-`out`-param no-op stub (`localQuality = 0f; ... `) on the server build only;
  CLIENT:81759-81799 has the real per-peer-averaging implementation (out of scope to change; it already works for a
  listen-server host). Patch kind: `[HarmonyPostfix]`, **server build only** (guard on `IsDedicated()` even though
  the method only exists meaningfully differentiated per-build at the source level — the patch target is the same
  method name in the server DLL).
- **`ZSteamSocket.GetConnectionQuality`** (server build) — confirmed this pass to call the **client** class
  `SteamNetworkingSockets.GetConnectionRealTimeStatus` instead of `SteamGameServerNetworkingSockets.
  GetConnectionRealTimeStatus`, inconsistent with every sibling method in the same class/build
  (`GetSendQueueSize` SERVER:86620, `GetCurrentSendRate` SERVER:86648, both confirmed using the GameServer class).
  Patch kind: `[HarmonyPrefix]` returning `false` after computing the result with the correct class, **server build
  only**. Unreachable in vanilla (`ZNet.GetNetStats`'s stub never calls it on the server), so this patch cannot
  regress existing behavior; it only matters once the `GetNetStats` postfix above starts calling it.

```csharp
[HarmonyPatch(typeof(ZNet), "GetNetStats")]
static class ZNet_GetNetStats_ServerTelemetry
{
    static void Postfix(ZNet __instance, out float localQuality, out float remoteQuality, out int ping, out float outByteSec, out float inByteSec)
    {
        localQuality = remoteQuality = 0f; ping = 0; outByteSec = inByteSec = 0f; // keep vanilla shape if flag off or not dedicated
        if (!Features.Transport.T8.Active || !__instance.IsDedicated()) return;
        // aggregate across __instance.m_peers using peer.m_socket.GetSendQueueSize()/GetCurrentSendRate(),
        // type-guarding `is ZPlayFabSocket` before calling GetCurrentSendRate (throws NotImplementedException there)
        Telemetry.Transport.AggregateNetStats(__instance, out localQuality, out remoteQuality, out ping, out outByteSec, out inByteSec);
    }
}
```

**Flag off**: `GetNetStats` postfix leaves vanilla's zeros untouched; the `GetConnectionQuality` prefix, gated the
same way, never intercepts — vanilla's (unreachable, buggy) implementation stays exactly as shipped.

### Hook 6 — Unified `SendZDOs` budget (Q3, Q4, Q5, Q12, G2-N1)

- **Type.Method**: `private bool ZDOMan.SendZDOs(ZDOPeer peer, bool flush)` — SERVER:77055-77118 /
  CLIENT:77197-77256, re-confirmed this pass **byte-for-byte identical** between builds (same statements, same
  literal values, in the same order) by direct side-by-side read of both ranges.
- **Patch kind**: single `[HarmonyTranspiler]` (`CodeMatcher`, matching by opcode+operand value, never a fixed
  index — per 10-LEVERS T6/Q1 guards) replacing the four numeric literals this method contains (`10240` outer gate
  SERVER:77058, `10240` inner-budget base SERVER:77062 written as `int num = 10240 - sendQueueSize;`, `2048`
  congestion floor SERVER:77063) with reads of one shared config-backed struct, **plus** one new running-total check
  inserted into the per-item loop (Q12) and a size-checked/chunked rewrite of the `m_invalidSector` write block
  (G2-N1, SERVER:77075-77083). **RED-TEAM FIX** (RT2, minor): these three line numbers correct this doc's own
  earlier, mutually inconsistent citations (line 78 below said 77057/77062; this paragraph previously said
  77058/77061/77062; re-`grep -n`'d this pass — all three literal sites and the invalid-sector block are confirmed
  at 77058/77062/77063/77075-77083 on both builds).
- **Side**: both (server is the realistic trigger for high volume; the client build runs the identical method for
  its own upstream sends, gated separately by Q10).
- **Why one patch, not five**: `10-LEVERS.md`'s "Coupled levers" section states Q3+Q5 must move together (two
  distinct literals, same intended value) and Q3/Q5+Q12+G2-N1 all write into the *same* `zPackage` before/around the
  same per-item size check — a guard on only one of them is insufficient, since `peer.m_invalidSector` is written
  into the package **before** the per-item loop's own `zPackage.Size() > num` check even starts (SERVER:77068-77096
  order, confirmed this pass), so a size cap on the per-item loop alone cannot see how much of the 512 KiB ceiling
  the invalid-sector block already consumed. One coordinated patch with one shared "bytes written so far" counter is
  the only implementation that can reason correctly about the real ceiling.

```csharp
// src/Transport/ZdoSendBudget.cs — one transpiler target, all five literals/checks in one CodeMatcher pass
[HarmonyPatch(typeof(ZDOMan), "SendZDOs")]
static class ZDOMan_SendZDOs_Budget
{
    // Values read live from Config each call; when Features.Transport.<lever>.Active is false for an
    // individual sub-lever, its own field falls back to the vanilla literal (10240/10240/2048) —
    // this keeps the transpiler shape identical whether the flag is on or off; only the *value fed in* changes.
    static int OuterGateBytes  => Features.Transport.Q5.Active ? SharedBudget.Clamped(Config.Q5_OuterGateBytes.Value) : 10240;
    static int InnerBudgetBytes=> Features.Transport.Q3.Active ? SharedBudget.Clamped(Config.Q3_InnerBudgetBytes.Value) : 10240;
    static int CongestionFloor => Features.Transport.Q4.Active ? Config.Q4_CongestionFloorBytes.Value : 2048;

    // **RED-TEAM FIX** (both red teams, blocker): the original sketch used
    // `.SetOperandAndAdvance(0).InsertAndAdvance(CallGetter(...))` at each site. `SetOperandAndAdvance` only
    // rewrites the matched `Ldc_I4`'s operand (10240 -> 0) and advances past it; `InsertAndAdvance` then inserts
    // the `call <getter>` instruction *after* it rather than replacing it. Both instructions still execute, so each
    // site pushes two int32s (the mutated `ldc.i4 0` and the getter's return) onto the stack where the surrounding
    // comparison/subtraction expects exactly one — an unconditional stack-depth mismatch, independent of any config
    // value or kill-switch state, that fails CLR IL verification (`InvalidProgramException`) the moment the patched
    // `SendZDOs` is JIT-compiled/invoked. Confirmed against the actual Harmony `CodeMatcher` API: `SetOperandAndAdvance`
    // mutates operand only; it does not change the opcode or remove the instruction, and `InsertAndAdvance` inserts
    // without removing anything. Fixed below by replacing the matched instruction in place with
    // `SetInstructionAndAdvance`, which swaps the whole `Ldc_I4` instruction for the `call` (and carries over any
    // labels/exception-block markers on the original instruction), leaving exactly one push per site.
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
    {
        var m = new CodeMatcher(instructions, il);
        // 1) outer gate: `sendQueueSize > 10240` -> `sendQueueSize > OuterGateBytes`
        m.MatchStartForward(new CodeMatch(OpCodes.Ldc_I4, 10240)).ThrowIfInvalid("Q5 outer gate literal not found")
         .SetInstructionAndAdvance(CallGetter(nameof(OuterGateBytes)));
        // 2) inner budget base: `10240 - sendQueueSize` -> `InnerBudgetBytes - sendQueueSize`
        m.MatchStartForward(new CodeMatch(OpCodes.Ldc_I4, 10240)).ThrowIfInvalid("Q3 inner budget literal not found")
         .SetInstructionAndAdvance(CallGetter(nameof(InnerBudgetBytes)));
        // 3) congestion floor: `num < 2048` -> `num < CongestionFloor`
        m.MatchStartForward(new CodeMatch(OpCodes.Ldc_I4, 2048)).ThrowIfInvalid("Q4 floor literal not found")
         .SetInstructionAndAdvance(CallGetter(nameof(CongestionFloor)));
        // 4) Q12 + G2-N1: insert a running-total guard right before the per-item `zPackage.Size() > num` break,
        //    and wrap the invalidSector write loop with a chunk-and-size check (full detail omitted from this
        //    sketch; both reuse a single `SharedBudget.RunningTotal` int local threaded through the method via
        //    a companion ILGenerator local, checked against 524288 - framingMargin before every Write call).
        //    These are pure insertions (no literal replacement), so `InsertAndAdvance` is the correct call for them —
        //    the bug above was specific to the three literal-*replacement* sites, not to insertion in general.
        return ZdoSendBudgetIL.InsertQ12AndG2N1Guards(m).InstructionEnumeration();
    }
}

public static class SharedBudget
{
    public const int HardCeilingBytes = 131072; // Q3's own hard, non-admin-overridable clamp
    public static int Clamped(int configured) => Mathf.Clamp(configured, 2048, HardCeilingBytes);
}
```

The Q12 guard (conceptually, inserted per-item inside the existing `foreach (ZDO item2 in m_tempToSync)` loop,
SERVER:77091-77108): before `zPackage.Write(item2.m_uid)` et al., serialize the candidate item into a scratch
`ZPackage` first, and check `runningTotal + candidateSize + framingMargin < 524288`; if it would exceed, `break`
(not `continue` — items are pre-sorted by Q6's priority, so nothing later in the list is more urgent) without
touching `peer.m_zdos` for the skipped item, so `ShouldSend` retries it next rotation untouched.

The G2-N1 guard (conceptually, replacing SERVER:77075-77083's unconditional write loop): iterate
`peer.m_invalidSector` writing into `zPackage` while tracking the running total the same way; if the set is larger
than one chunk can safely hold (byte-budget-driven, not the fixed "43,000+ entries" 10-LEVERS cites as the
theoretical overflow point — this hook computes the real remaining byte budget directly instead of trusting that
count), write only what fits, and let the next `SendZDOs` call for this peer pick up the remainder.

**RED-TEAM FIX** (RT1, major): the original sketch said only "leave the rest in `peer.m_invalidSector` (do **not**
`.Clear()` it)" — but `m_invalidSector` is a plain `HashSet<ZDOID>` consumed today by an unconditional `foreach` +
`.Clear()` (confirmed, SERVER:76032/77075-77083), with no per-item removal mechanism the sketch could reuse.
Read literally, "don't Clear" alone does not remove the entries that *were* just written, so the next call's
`foreach` over the same, unmutated `HashSet` is very likely to yield the same leading subset again (a `HashSet`'s
enumeration order is a function of its internal bucket layout, which a partial-drain call does not touch), starving
every entry past the first chunk indefinitely — a silent per-sector desync masked by the very reconnect-based
regression test (§Test plan #1) that was supposed to catch it, since a reconnect forces a full resync regardless of
this bug. **Fix**: accumulate the IDs actually written this call into a scratch `List<ZDOID>` inside the chunk loop,
then call `peer.m_invalidSector.ExceptWith(writtenIds)` after the chunk (not `.Clear()`, and not a bare no-op) —
this is a genuine remove-as-written, not a "leave everything untouched" no-op — so each subsequent `SendZDOs` call
provably drains a disjoint slice of the backlog until the set is empty. `ExceptWith` is an in-place `HashSet<T>`
mutation (no new allocation beyond the scratch list, which is bounded by the chunk size), so this preserves the
"reuses the existing `HashSet<ZDOID>` with no new per-ZDO allocation" property the original sketch aimed for.

**Flag off (any single sub-lever)**: that sub-lever's getter falls back to the vanilla literal (10240/10240/2048);
the transpiler still runs (so a live toggle needs no re-patch) but produces IL numerically identical to vanilla
**once the `SetInstructionAndAdvance` fix above is in place** — this claim was false against the original
`SetOperandAndAdvance`+`InsertAndAdvance` sketch (that shape was mis-shapen both on and off, independent of any
flag; see the RED-TEAM FIX on the transpiler code above). **Flag off (whole `Transport` feature)**: the transpiler
is still applied (Harmony patches are static), but every getter above falls back to the vanilla literal and the
Q12/G2-N1 guards are compiled to always pass (never break early, never chunk) — net effect is the original method's
behavior, confirmed by the same "all getters return the vanilla constant" reasoning used throughout this doc.
**This "numerically identical IL" claim is asserted, not yet machine-checked** — per RT1's fix recommendation, the
Test plan (below) now requires an actual Harmony IL-dump diff (or an equivalent JIT-invoke smoke test across a
range of `sendQueueSize` values compared byte-for-byte against vanilla) as a **blocking** step before this hook is
exercised against any real connection, precisely because this is the highest-traffic, most safety-critical patch in
the document and "reasoned correctly from the C# sketch" is exactly the failure mode both red teams caught here.

### Hook 7 — Peers-per-tick (Q2)

- **Type.Method**: `private void ZDOMan.SendZDOToPeers2(float dt)` — SERVER:76883-76909 / CLIENT:77026-77049,
  re-confirmed this pass byte-for-byte identical (idle/armed state machine, `0.05f` round-start gate, one
  `SendZDOs` call per invocation, round-robin `m_nextSendPeer` advance).
- **Patch kind**: `[HarmonyPrefix]` returning `false` (fully replaces the method) — a transpiler is a worse fit here
  because the change is structural (loop k times instead of once), not a literal swap.
- **Side**: server-only in practice (`m_peers.Count` is normally 1 on a client, so k>1 has no effect there; the
  patch still applies on both builds for uniformity but is a no-op path for a normal client).
- **Why a full prefix-replacement**: `Q2`'s own mandatory guard (10-LEVERS) requires the k-peer loop to stay
  **strictly sequential on one thread within a single tick**, because `CreateSyncList`/`ServerSortSendZDOS`/
  `ClientSortSendZDOS` share `ZDOMan` **instance** scratch fields (`m_tempToSync`, `m_tempSectorObjects`,
  `m_tempToSyncDistant`, `m_tempRemoveList`, confirmed by direct read of `CreateSyncList`'s body, SERVER:77259-77307
  this pass) and a bare **static** `s_compareReceiver` (confirmed at the `ServerSortSendZDOS`/`ServerSendCompare`
  call site, SERVER:77337/77358 this pass — `s_compareReceiver = peer.m_peer.m_uid;` set immediately before
  `objects.Sort(ServerSendCompare)`, and `ServerSendCompare` reads it as a closure-free static comparator). A
  transpiler patching only the `0.05f` cadence (Q1, not in this doc's scope) would leave the one-peer-per-tick
  structure intact; Q2 specifically needs the call site itself rewritten into a loop.
- **Known live-server collision (RT2, major — see the "Other mods" cross-reference at the end of §Hooks)**:
  `ZenDragon-Zen_ModLib`, installed on Proxima Maxi, already carries a non-skip prefix on this exact method
  (`OptimizeNetworkZDOManSendToPeers2`, `research/13-mod-compat-census.md` lines 110-119, rated HIGH). This full
  prefix-replacement must not ship to Proxima until that patch's `[HarmonyPriority]` ordering against Hook 7 is
  verified live — see §Interactions with other subsystems, "Other mods," for the required fix.
- **RED-TEAM FIX** (RT1, minor — amplifies a pre-existing, disclaimed vanilla gap): `ZDOMan.AddPeer` (SERVER:76814-
  76820, confirmed: `m_peers.Add(new ZDOPeer{...})`) has no existing-entry check, and `RPC_PeerInfo`'s
  `IsConnected(uid)` replay gap (lever G1-L1, out of this doc's scope — see Hook 2's own re-entry-hazard note) can
  reach `AddPeer` a second time for what is physically one connection, leaving two `ZDOPeer` wrappers for it in
  `m_peers`. With `k==1` (vanilla), a stray duplicate is serviced once every two rounds like any other entry; with
  Q2's raised `k`, both wrappers for the same physical connection can be serviced **within the same tick**, so
  redundant `SendZDOs` calls to one churning connection cluster together faster and consume more of the Q3/Q5-raised
  per-peer budget on duplicate traffic — not data loss, but it measurably undercuts Q2's own fairness goal, sharpest
  under exactly the scenario §Test plan #3 already calls out (a disconnect/reconnect storm from one client). This is
  not a Hook 7 bug to fix (G1-L1 is out of scope here and should be fixed independently, per Hook 2's own note) —
  the mitigation is a one-line, purely-defensive addition to Hook 7's own loop: dedupe `peers` by the underlying
  `ZNetPeer` object identity (`peer.m_peer`, reachable via the same reflection field-get already used for `m_peer`
  in Hook 8's rewrite) before iterating, so a stray duplicate `ZDOPeer` is serviced at most once per k-window
  regardless of whether G1-L1 has been fixed yet.

**RED-TEAM FIX** (RT2, blocker): the original sketch declared `AccessTools.FieldRefAccess<ZDOMan, List<ZDOPeer>>(...)`.
`ZDOMan.ZDOPeer` is `private class ZDOPeer` nested inside `public class ZDOMan` on **both** builds (confirmed
SERVER:76008/CLIENT:76133, inside `public class ZDOMan` at SERVER:76006/CLIENT:76131) — a private nested type's name
cannot be spelled from any class other than its declaring type, not as a generic type argument and not as a method
parameter type. `List<ZDOPeer>` is exactly such a spelling, so this line is a compile error (`CS0122`) in this
module's own source, not a runtime issue — it blocks M1 from building at all. Fixed below by never naming `ZDOPeer`:
resolve `m_peers`' `FieldInfo` once (reflection does not enforce C# accessibility on `FieldInfo.GetValue`/`SetValue`,
only C# source-level type *names* are restricted) and read/write it as the non-generic `System.Collections.IList`
that any `List<T>` implements — `IList`'s own type is `public` and needs no unspeakable generic argument. Invoking
`SendZDOs` was already done via `MethodInfo.Invoke(object[])` in the original sketch, which was already
accessibility-safe (it boxes the `ZDOPeer` element as `object`), so that call is unchanged.

```csharp
[HarmonyPatch(typeof(ZDOMan), "SendZDOToPeers2")]
static class ZDOMan_SendZDOToPeers2_KPeers
{
    // Resolved once at class-init, not per-call — avoids re-resolving accessibility-blocked members every tick.
    static readonly FieldInfo s_peersField = AccessTools.Field(typeof(ZDOMan), "m_peers");
    static readonly MethodInfo s_sendZdos = AccessTools.Method(typeof(ZDOMan), "SendZDOs");

    static bool Prefix(ZDOMan __instance, float dt)
    {
        if (!Features.Transport.Q2.Active) return true; // run vanilla's original one-peer-per-tick body
        // List<ZDOPeer> boxed as the non-generic IList — never names the inaccessible private ZDOPeer type.
        var peers = (IList)s_peersField.GetValue(__instance);
        if (peers.Count == 0) return false;

        ref float sendTimer = ref AccessTools.FieldRefAccess<ZDOMan, float>(__instance, "m_sendTimer");
        ref int nextSendPeer = ref AccessTools.FieldRefAccess<ZDOMan, int>(__instance, "m_nextSendPeer");
        sendTimer += dt;
        if (nextSendPeer < 0)
        {
            if (sendTimer > Config.Q1_RoundStartGateSec.Value) { nextSendPeer = 0; sendTimer = 0f; } // Q1 not in this doc's scope; literal kept at 0.05f default
            return false;
        }

        int k = AdaptiveK.Compute(Config.Q2_PeersPerTick.Value); // default 4, capped 2-8, see Open questions
        for (int i = 0; i < k && nextSendPeer < peers.Count; i++) // strictly sequential — never Parallel.For / Task.Run
        {
            s_sendZdos.Invoke(__instance, new object[] { peers[nextSendPeer], false }); // peers[i] is boxed ZDOPeer
            nextSendPeer++;
        }
        if (nextSendPeer >= peers.Count) nextSendPeer = -1;
        return false;
    }
}
```

Note: `sendTimer`/`nextSendPeer` use the generic `AccessTools.FieldRefAccess<ZDOMan, float>`/`<ZDOMan, int>` form
unchanged from the original sketch — `float` and `int` are ordinary public types, so these two lines never hit the
`ZDOPeer` accessibility problem and needed no fix.

The bound is re-read from `peers.Count` on every loop iteration (not cached once), closing the fairness gap
10-LEVERS flags (`m_peers.Count` changing mid-round from a concurrent `RemovePeer`) automatically, since the `for`
condition itself re-checks it each pass.

**Flag off**: `Prefix` returns `true` immediately — Harmony runs the original method body unmodified, one peer per
tick, exactly vanilla.

### Hook 8 — Send priority bias (Q6)

- **Type.Method**: `private void ZDOMan.ServerSortSendZDOS(List<ZDO> objects, Vector3 refPos, ZDOPeer peer)` —
  SERVER:77358-77378 / CLIENT:77500 area (name/signature confirmed present on both this pass via `grep -n`), body
  re-confirmed this pass: `@object.m_tempSortValue = Vector3.Distance(position, refPos); ... @object.m_tempSortValue
  -= num * 1.5f;` then `objects.Sort(ServerSendCompare)` with `s_compareReceiver` set immediately before.
- **Patch kind**: `[HarmonyPostfix]` re-adjusting `m_tempSortValue` for objects in a server-simulated zone,
  **before** the existing `objects.Sort(ServerSendCompare)` call — implemented as a prefix that runs the per-object
  distance/staleness computation loop itself (copying vanilla's loop shape) plus one additional subtraction term,
  then calls the original sort logic; or, more simply, a full method replacement that is vanilla's own body plus
  one line. This doc recommends the **full-replacement prefix**, not a transpiler, because the bias term needs a
  lookup vanilla's loop doesn't have a slot for, and inserting a call mid-loop via IL patching is fragile for no
  benefit over copying the (short, ~20-line) method.
- **Side**: server-only (`ServerSortSendZDOS` is never called from the client branch of `CreateSyncList`, confirmed
  by the `if (ZNet.instance.IsServer())` branch structure at SERVER:77259-77307 this pass).
- **Why this hook, "effective yet memory efficient" (user's own note on Q6)**: the bias must not add a per-ZDO
  allocation, since this loop runs over every near-ring ZDO for every peer, every tick a peer is serviced. This
  design reuses the **already-allocated** `ZDO.m_tempSortValue` field vanilla itself writes every call (no new
  per-ZDO field, no new `Dictionary<ZDOID,*>`), and looks up "is this ZDO in a server-held zone" via an **O(1)**
  membership test against a `HashSet<Vector2s>` the ownership module (G3-L1/O6, out of this doc's scope) already
  maintains as its own live list of server-owned zones — this module only reads that set, allocating nothing of its
  own beyond one static reference to it.

**RED-TEAM FIX** (RT2, blocker — same root cause as Hook 7): the original `Prefix(ZDOMan __instance, List<ZDO>
objects, Vector3 refPos, ZDOPeer peer)` signature and the `Dictionary<ZDOID, ZDOPeer.PeerZDOInfo>` field-ref both
name the inaccessible private `ZDOPeer` type (and its equally-inaccessible nested `PeerZDOInfo`, which inherits
`ZDOPeer`'s effective privacy regardless of its own `public struct` modifier) — another `CS0122` compile error.
Harmony binds prefix parameters by **name**, at the IL level, not by C# overload resolution against the original
method's declared type, so a parameter Harmony cannot type-check against a nameable type is conventionally declared
`object` instead — this is a standard, documented Harmony pattern for patching a method with an inaccessible
parameter type, not a NoodlesMcDoodles-specific workaround. The field/member accesses inside the body are resolved
the same way Hook 7's were fixed: `AccessTools.Inner` finds the nested `Type` object at class-init without ever
spelling its name in source, and member reads go through cached `FieldInfo`s boxed to the widest nameable
type (`IDictionary` for the dictionary field; the boxed `PeerZDOInfo` struct's own `m_syncTime` needs one more
`FieldInfo.GetValue` hop, since `PeerZDOInfo` can't be cast to any public interface the way `List<T>`/`Dictionary<K,V>`
can — this is reflection all the way down for that one field, accepted here as the cost of vanilla having made the
type private, and cached at class-init so it's a fixed per-call reflection cost, not a re-resolution cost).

```csharp
[HarmonyPatch(typeof(ZDOMan), "ServerSortSendZDOS")]
static class ZDOMan_ServerSortSendZDOS_ZoneBias
{
    // Resolved once at class-init via AccessTools.Inner (never spells ZDOPeer/PeerZDOInfo in source).
    static readonly Type s_zdoPeerType = AccessTools.Inner(typeof(ZDOMan), "ZDOPeer");
    static readonly Type s_peerZdoInfoType = AccessTools.Inner(s_zdoPeerType, "PeerZDOInfo");
    static readonly FieldInfo s_zdosField = AccessTools.Field(s_zdoPeerType, "m_zdos");       // Dictionary<ZDOID, PeerZDOInfo>
    static readonly FieldInfo s_peerField = AccessTools.Field(s_zdoPeerType, "m_peer");       // ZNetPeer — publicly nameable once unboxed
    static readonly FieldInfo s_syncTimeField = AccessTools.Field(s_peerZdoInfoType, "m_syncTime"); // float
    static readonly MethodInfo s_serverSendCompare = AccessTools.Method(typeof(ZDOMan), "ServerSendCompare");
    static readonly Comparison<ZDO> s_compareDelegate =
        (Comparison<ZDO>)Delegate.CreateDelegate(typeof(Comparison<ZDO>), s_serverSendCompare);

    // `peer` is `object` (boxed ZDOPeer) — Harmony binds by parameter name, not by C#-checkable type, so this is
    // the standard workaround for patching a method whose original parameter type is inaccessible from our source.
    static bool Prefix(ZDOMan __instance, List<ZDO> objects, Vector3 refPos, object peer)
    {
        if (!Features.Transport.Q6.Active) return true; // vanilla body runs unmodified
        float time = Time.time;
        var peerZdos = (IDictionary)s_zdosField.GetValue(peer); // keys boxed ZDOID, values boxed PeerZDOInfo
        foreach (ZDO obj in objects)
        {
            Vector3 pos = obj.GetPosition();
            obj.m_tempSortValue = Vector3.Distance(pos, refPos);
            float staleness = 100f;
            if (peerZdos.Contains(obj.m_uid))
            {
                object info = peerZdos[obj.m_uid]; // boxed PeerZDOInfo
                float syncTime = (float)s_syncTimeField.GetValue(info);
                staleness = Mathf.Clamp(time - syncTime, 0f, 100f);
            }
            obj.m_tempSortValue -= staleness * 1.5f; // vanilla's own weight, unchanged
            if (ServerZoneMembership.IsServerHeld(ZoneSystem.GetZone(pos))) // O(1) HashSet<Vector2s> lookup, owned by G3-L1/O6
                obj.m_tempSortValue -= Config.Q6_ServerZoneBias.Value;      // additional, config-bound bias only
        }
        ref long compareReceiver = ref AccessTools.StaticFieldRefAccess<long>(typeof(ZDOMan), "s_compareReceiver");
        var netPeer = (ZNetPeer)s_peerField.GetValue(peer); // ZNetPeer is public — normal cast from here on
        compareReceiver = netPeer.m_uid;
        objects.Sort(s_compareDelegate); // ServerSendCompare(ZDO,ZDO) never names ZDOPeer, so this delegate binds fine
        return false;
    }
}
```

**Performance note (honest cost of the fix)**: the original sketch's goal — "no new per-ZDO allocation, O(1) lookup"
— still holds for the *lookup itself* (`IDictionary.Contains`/indexer is still O(1)), but `peerZdos[obj.m_uid]`
boxes the `PeerZDOInfo` struct on every hit (one small heap allocation per near-ring ZDO per peer per tick) and
`s_syncTimeField.GetValue(info)` is a reflection call, both slower than the direct field access the original sketch
implied. This is the unavoidable cost of vanilla having made `ZDOPeer`/`PeerZDOInfo` private — it was not priced into
the original "effective yet memory efficient" design intent (the user's own note on Q6). It is cheap enough not to
block M1 (Open question 5, expanded below, now covers measuring it), but if a live measurement on Proxima's near-ring
sizes shows it matters, the follow-up optimization is an IL-emitted fast accessor (a small `DynamicMethod` doing
`ldarg; ldfld; box; ret`, compiled once and cached as a delegate) instead of `FieldInfo.GetValue`, which avoids the
reflection dispatch cost while still never naming the private type in source — noted here rather than built now,
since it is an optimization, not a correctness fix.

The `ServerSendCompare` delegate is vanilla's own unmodified comparator (Prioritized-non-owned-first tier preserved
exactly), reflected and reused rather than reimplemented, so the anti-starvation/Prioritized-priority ordering
10-LEVERS' guard requires is inherited automatically, not re-derived.

**Flag off**: `Prefix` returns `true`; vanilla's `ServerSortSendZDOS` runs untouched, no zone lookup, no bias.

### Hook 9 — Destroy-list chunking (Q7)

- **Type.Method**: `private void ZDOMan.SendDestroyed()` — SERVER:76984-76996 / CLIENT:77126 area (confirmed
  present, name/shape matched this pass), body re-confirmed: writes the **entire** `m_destroySendList` into one
  `ZPackage` with zero size check, then one `ZRoutedRpc.instance.InvokeRoutedRPC(0L, "DestroyZDO", zPackage)` call,
  then `m_destroySendList.Clear()`.
- **Patch kind**: `[HarmonyPrefix]` returning `false` (full replacement — the chunking loop has no vanilla
  counterpart to transpile around).
- **Side**: both (send path identical; server is the realistic high-volume trigger).
- **Why this hook**: this is the second of the three independently-unbounded write paths 10-LEVERS identifies
  (alongside the invalid-sector block, Hook 6, and the per-item budget's advisory-not-hard nature). It is a single
  `InvokeRoutedRPC` call, which — per `research/01` §5's confirmed finding — goes through `ZRoutedRpc.RouteRPC`
  (SERVER:83643-83668, re-confirmed this pass has **zero** `GetSendQueueSize` check on any of its three send
  branches), so nothing else in the stack will ever slow this down; the mod has to.

**RED-TEAM FIX** (RT1, minor): the original sketch had no exception guard around the per-chunk `InvokeRoutedRPC`
call, unlike Hooks 1/2's Steam API calls elsewhere in this document, which are explicitly wrapped in
`try/finally`/`try/catch/finally`. Vanilla's own `SendDestroyed` calls `m_destroySendList.Clear()` **before**
`InvokeRoutedRPC` (SERVER:76996-76997, confirmed this pass), so a downstream throw there can never re-surface stale
destroy-list state on a retry — this sketch deliberately reverses that order (`Clear()` only after every chunk has
fired) to make "which chunks landed" honest, but that reversal means an uncaught exception partway through a
multi-thousand-ZDO destroy burst (this hook's own regression test scenario, §Test plan #2) would propagate out of
this `[HarmonyPrefix]`, skip the trailing `Clear()`/telemetry call, and abort whatever the tick's caller does after
`SendDestroyed()` returns — a different, less defensive failure mode than vanilla's. Fixed by wrapping each chunk's
send in `try/catch`, consistent with Hooks 1/2's pattern, and leaving the unsent tail in `m_destroySendList` (not
cleared) so the next tick's call picks it up — the same "skip, log, retry, never truncate" discipline this document
already uses for Q12/G2-N1.

```csharp
[HarmonyPatch(typeof(ZDOMan), "SendDestroyed")]
static class ZDOMan_SendDestroyed_Chunked
{
    static bool Prefix(ZDOMan __instance)
    {
        if (!Features.Transport.Q7.Active) return true;
        var list = AccessTools.FieldRefAccess<ZDOMan, List<ZDOID>>(__instance, "m_destroySendList");
        if (list.Count == 0) return false;

        int chunkSize = Config.Q7_ChunkIdCount.Value; // default 8000
        int sent = 0;
        int chunksSent = 0;
        while (sent < list.Count)
        {
            int n = Math.Min(chunkSize, list.Count - sent);
            var pkg = new ZPackage();
            pkg.Write(n);
            for (int i = 0; i < n; i++) pkg.Write(list[sent + i]);
            try
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(0L, "DestroyZDO", pkg);
            }
            catch (Exception e)
            {
                // Fail closed: stop chunking, leave list[sent..] in place (not cleared) so the next SendDestroyed
                // call for this ZDOMan retries the remainder — never truncate, never lose a destroy notification.
                ZLog.LogWarning($"[NoodlesMcDoodles] Q7 destroy-chunk send failed at chunk {chunksSent} ({sent}/{list.Count} IDs sent): {e}");
                list.RemoveRange(0, sent); // drop only the chunks that actually landed
                Telemetry.Transport.DestroyChunksSent(chunksSent);
                return false;
            }
            sent += n;
            chunksSent++;
        }
        list.Clear(); // only after every chunk has been fired this tick — HandleDestroyedZDO is confirmed idempotent
        Telemetry.Transport.DestroyChunksSent(chunksSent);
        return false;
    }
}
```

This sketch fires every chunk within the same tick (matching 10-LEVERS' "chunk within the same tick" note — a
correctness preference since `HandleDestroyedZDO` is idempotent, not a strict requirement). A future iteration can
add the queue-size-aware defer guard 10-LEVERS also lists (checking `GetSendQueueSize()` between chunks for a peer
near its congestion edge) — flagged in Open questions, since it needs per-peer routing that `InvokeRoutedRPC`'s
broadcast form doesn't expose without its own change.

**Flag off**: `Prefix` returns `true`; vanilla's single unbounded write runs exactly as shipped.

### Hook 10 — Force-send batch cap (Q8)

- **Type.Method**: `public void ZDOMan.ForceSendZDO(ZDOID id)` (SERVER:77753) and
  `public void ZDOMan.ForceSendZDO(long peerID, ZDOID id)` (SERVER:77761) — existing public API, no patch to the
  API surface itself.
- **Patch kind**: none — this is a **call-site discipline**, not a vanilla-code hook. Other subsystems (ownership
  handover, in particular) call this API after a claim/handover completes so observing peers resync promptly; this
  module's only responsibility is a helper that caps the batch.
- **Side**: either.

```csharp
// src/Transport/ForceSendBudget.cs
public static class ForceSendBudget
{
    public static void ForceSendMany(IEnumerable<ZDOID> ids, long? peerId = null)
    {
        int cap = Config.Q8_ForceSendBatchCap.Value; // default 1000
        int n = 0;
        foreach (var id in ids)
        {
            if (n++ >= cap) { Telemetry.Transport.ForceSendBatchCapped(n); break; }
            if (peerId.HasValue) ZDOMan.instance.ForceSendZDO(peerId.Value, id);
            else ZDOMan.instance.ForceSendZDO(id);
        }
    }
}
```

**Flag off**: callers should still go through this helper (it's the only sanctioned entry point for "force send many
ZDOs at once" in the codebase), but with `Features.Transport.Q8.Active == false` the helper falls back to an
uncapped loop calling vanilla's API directly one-for-one — identical to a caller that never used this helper at all.

### Hook 11 — Client-side send-loop twin (Q10)

- **Type.Method**: same `ZDOMan.SendZDOs`/`SendZDOToPeers2` methods as Hooks 6/7, applied against the **client**
  assembly's copy (byte-identical IL, confirmed above), **not** a new hook.
- **Patch kind**: identical transpiler/prefix, same source, compiled once, applied to whichever build the mod DLL
  is loaded into.
- **Side**: client (effect is real only when `ZNet.m_peers.Count` is meaningfully >1, i.e. a listen-server host; for
  a normal client connected to one dedicated server, `m_peers.Count == 1` so Q2's k-loop is a no-op and only Q3/Q4/
  Q5/Q12/G2-N1's budget values matter, raising how fast **this client** uploads **its own owned ZDOs** to the
  server).
- **Why a separate flag**: this is the one hook in this document whose benefit requires the **other player** to
  install the mod. It is gated behind its own `Features.Transport.Q10.Active`, defaulting **off**, independent of
  every server-side flag, so a server operator turning on Phase A does not silently change a connecting vanilla
  client's behavior (it can't — the client never runs this code) and a player choosing to install the client mod
  makes an explicit, separate opt-in.

**Flag off**: no different from any other individual sub-lever being off — the shared transpiler's getters fall back
to vanilla literals.

### Hook 12 — Oversized mod-added ZDO field guard (G6-N4)

- **Type.Method**: none in vanilla — this is a **library guard**, not a patch. `NoodlesMcDoodles.Core.SafeZdo`
  (or wherever the shared infra ships it) wraps every future call this mod makes to `ZDO.Set(string, byte[])`/
  `ZDO.Set(string, string)` for a mod-defined key.
- **Patch kind**: new library method, called by other modules (ownership, zonesim), not by this doc's own code
  (Phase A doesn't itself add any custom ZDO fields).
- **Side**: either.

```csharp
// src/Transport/SafeZdoWrite.cs (or Core, per the shared infra's final home)
public static class SafeZdoWrite
{
    private const int MaxBytes = 2048;

    public static bool TrySet(ZDO zdo, string key, byte[] value)
    {
        if (value.Length > MaxBytes)
        {
            ZLog.LogWarning($"[NoodlesMcDoodles] refused oversized ZDO write: key={key} bytes={value.Length} (cap={MaxBytes})");
            Telemetry.Transport.OversizedZdoWriteRejected(key, value.Length);
            return false; // caller must handle refusal — e.g. split across multiple keys/ZDOs; never truncate
        }
        zdo.Set(key, value);
        return true;
    }
}
```

**Flag off**: since this is a library guard other modules opt into by calling it (there is no vanilla behavior to
restore), "off" means callers bypass `SafeZdoWrite` and call `ZDO.Set` directly — which is exactly what happens if a
future module is written before this guard exists. This doc recommends every future ZDO-field-writing module route
through it regardless of any feature flag, since it only ever *rejects*, never *changes*, a write that would have
succeeded — but it is still wrapped in `Features.Transport.G6N4.Active` for consistency with the kill-switch
contract; when off, `TrySet` skips the size check and always calls `zdo.Set` directly.

---

## Data-loss analysis

1. **Oversized single message still reaches the 512 KiB Steam ceiling despite Hook 6.** `SendQueuedPackages`
   (`Peek()`s, never `Dequeue()`s, on failure — SERVER:86518-86546, re-confirmed this pass) is unpatched by this
   doc; Hook 6's running-total guard stops `SendZDOs` from ever *assembling* an over-ceiling package, but a single
   ZDO whose own serialized size already exceeds ~440 KB (a fully-modified 32-width `TerrainComp`, per 10-LEVERS'
   own measurement) would still be written whole by the per-item loop's serialize-first step before the guard can
   reject it, since the guard only measures the candidate *before adding it to the running total*, not before the
   candidate itself is serialized. **Mitigation**: the guard sketch above must reject (skip, not truncate) any
   single candidate whose own serialized size exceeds a per-item ceiling (recommend 256 KB) independent of the
   running-total check — this is a refinement this doc adds beyond 10-LEVERS' text, since the running-total
   framing alone under-specifies the single-oversized-item case. No such ZDO exists in vanilla content; this is a
   defense against a future content mod or against this project's own zonesim module ever serializing something too
   large onto one ZDO (10-LEVERS hazard #2, out of this doc's scope but the guard belongs here).
2. **`ZRoutedRpc.RouteRPC` has zero backpressure on any path (confirmed, out of this doc's direct scope).** Hook 9
   (Q7) chunks the *destroy* list specifically, but any other high-frequency routed RPC this mod or another mod adds
   still has no queue-size check anywhere in the stack. This is the single highest-impact unresolved hazard in the
   whole transport area (research/01 §5) and is explicitly deferred to Q9 (`NEEDS-LIVE-TEST`, not in this doc's
   scope) — flagged here so it is not mistaken for "solved" by Q7 alone.
3. **T2's reflection access to `ZSteamSocket.m_con` is fragile by construction.** It is a `private` field with no
   accessor (SERVER:86175, confirmed this pass); a future Valheim patch renaming or retyping it breaks `Hook 2`
   silently at the `AccessTools.FieldRefAccess` call. **Mitigation**: wrap in try/catch (shown in the sketch),
   fail closed (log once, skip the burst for that connection, do not throw into `RPC_PeerInfo`'s own call chain —
   an uncaught exception there would abort the rest of that method, including vanilla's own
   `SendPlayerList`/`SendAdminList`/`m_zdoMan.AddPeer` calls that come after the postfix point... actually the
   postfix runs *after* those, per Hook 2's citation, so a throw there cannot prevent `AddPeer` — still, fail
   closed rather than let an unhandled exception propagate into Harmony's dispatch loop, which can affect other
   postfixes on the same method from other mods).
4. **T4's static-field timeout write races a concurrent `ZNet.Start()`/PlayFab callback on a different thread —
   verified NOT a real risk.** `ZNet.Update`, `ZPlayFabSocket.OnNetworkJoined`/`Accept`, and this mod's own
   `Reassert()` calls (via Harmony postfixes on those exact methods) all run on Unity's main thread — Valheim has no
   networking code on a background thread apart from `ZPlayFabSocket`'s zlib worker (research/01 §9), which never
   touches `ZRpc.m_timeout`. No cross-thread write race exists for this specific field.
5. **Crash mid-`SetConfigValue`/`GCHandle.Alloc` (Hooks 1, 2).** Every `GCHandle.Alloc`/`Free` pair in this doc's
   sketches is wrapped in `try/finally` (Hook 1) or `try/catch/finally` (Hook 2) specifically so a `SetConfigValue`
   native-call failure cannot leak a pinned handle. A process crash between `Alloc` and `Free` leaks that one handle
   for the remaining process lifetime (bounded, since these are one-shot-per-connection calls, not per-tick) — not a
   data-loss path, since no `ZDO`/save state is touched by any Hook 1/2 code, only Steam API state that resets on
   process restart.
6. **Disconnect mid-join-burst (Hook 2).** Covered by design: the `ConditionalWeakTable<ZNetPeer, BurstState>` entry
   becomes unreachable and GC-collected once `peer` is dereferenced by `ZNet`'s own peer-removal path; no explicit
   revert call is needed or possible after disconnect (there is no connection left to configure), and no state this
   mod owns outlives the peer object, so nothing leaks or needs cleanup.
7. **Reconnect immediately after a burst-revert (Hook 2).** A reconnecting peer is, structurally, a **new**
   `ZNetPeer`/`ZSteamSocket` object (confirmed: `ZNet.Connect`/accept paths always construct fresh objects, no
   connection-object reuse anywhere in either build) — so it is impossible for stale `BurstState` from a prior
   connection to apply to it. The burst logic runs again from a clean idempotency check, exactly as a first join
   would; this is correct, not a bug (a fast reconnect *should* get its own fresh join burst).
8. **Save during handover — not applicable to this module directly.** No hook in this document reads or writes
   `ZDO` data, world save state, or ownership; `ZDOMan.Save` (out of scope) reads the live in-memory ZDO set
   directly, independent of what has or hasn't been sent over the wire yet (research/01 §5, hazard #2's own
   mitigation note, re-confirmed structurally true here: nothing in Hooks 6/7/9 removes, mutates, or defers *saving*
   of any ZDO — they only affect what has been transmitted and when). A reconnecting client after a mid-save
   disconnect gets a full resync regardless of this module's queue state.
9. **Kill switch flipped mid-operation.** Every hook above checks its own `Features.Transport.<Lever>.Active` flag
   fresh on every invocation (never caches it), so a runtime disable takes effect on the very next call to that
   method — mid-round for Hook 7 (Q2) simply means the *next* `SendZDOToPeers2` call reverts to one-peer-per-tick,
   not that an in-progress k-peer loop is interrupted unsafely (the loop itself is a plain synchronous `for`, it
   either completes or the whole tick completes; there is no partial-loop state to corrupt). Hook 2's already-applied
   per-connection Steam rate overrides are **not** automatically reverted by disabling the flag — per 10-LEVERS T1's
   own open question, whether a live Steam config revert on an *already-open* connection actually takes effect the
   way a fresh connection's config would is **[unverified]**, not determinable from the decompiles alone (see Open
   questions). The kill switch stopping *new* bursts from being granted is guaranteed; retroactively un-applying an
   already-granted one is not, and this doc does not claim otherwise.
10. **Q6's zone-membership lookup racing a concurrent ownership handover.** `ServerZoneMembership.IsServerHeld`
    (owned by the ownership module) is read, not written, by this hook, and Hook 7's own mandatory sequential-tick
    guard (never parallelize `SendZDOToPeers2`/`SendZDOs`/`ServerSortSendZDOS`) means this read always happens on
    the same single thread the ownership module's own zone-list writes must also occur on (Unity main thread, per
    every other main-thread-marshaling guard in this codebase) — no torn read is possible. Worst case is a one-tick
    stale view of which zones are server-held, which only affects *send order*, never *data*.

---

## Vanilla and version compatibility

- **Vanilla client + modded server.** Every server-side-only hook (T1/T3's global config, T2's per-connection
  override, T4's static-field reassertion, T5, T8, Q2, Q6, Q7, the server half of Hook 6) requires **zero**
  client-side cooperation — they operate on data the server already owns (its own outbound Steam config, its own
  `ZDOMan` state) or on RPC handler bodies that run identically regardless of whether the far end is modded (Hook 2
  patches the *server's* `RPC_PeerInfo` handler; a vanilla client sending a normal `PeerInfo` RPC triggers it exactly
  the same as a modded one would). A vanilla client benefits from a faster/safer server queue and a higher perceived
  download rate from the server, and is completely unaffected by (cannot detect) any of it beyond that. No custom
  RPC is required for anything in this document's server-side scope — everything here is a config value, a static
  field, or a transpiled literal on code both builds already ship.
- **Modded client + vanilla server.** Q10 (the client-side twin of Hook 6/7) has no effect at all against a vanilla
  server — vanilla's own `SendZDOs`/`SendZDOToPeers2` on the server side still runs at the vanilla 10240/2048/1-peer
  cadence, so a modded client's own faster upload of *its own owned ZDOs* is throttled the instant it reaches a
  vanilla server's unmodified receive/relay path. The modded client's local Steam `SendRateMax` override (a T1
  analog, out of this doc's server-scoped table but structurally identical code on the client build) **does** still
  raise the client's own outbound ceiling regardless of what the server runs, since that call only affects the
  client's own socket — this is the mechanism the Summary section's Arizona mitigation depends on, and it requires
  no server cooperation at all.
- **Modded version skew (old client, new server; or vice versa).** None of this document's config values, static
  fields, or transpiled literals are carried over the wire in any vanilla or custom RPC — every value here is
  process-local. A version mismatch between two modded installs at worst means they pick different local tunables
  (e.g. two different `Q3_InnerBudgetBytes` configs), which is exactly as safe as two differently-configured vanilla
  servers talking to differently-configured vanilla clients already is (the budget/gate values were never
  negotiated in vanilla either). This document does not itself define or require the shared infra's version-tagged
  capability handshake (G1-N1/P6, out of scope) — nothing here needs the far end to know the mod version.
- **PlayFab/crossplay sockets.** Hook 3 (T4) explicitly accounts for `ZPlayFabSocket.OnNetworkJoined`/`Accept`'s
  unconditional `SetLongTimeout(true)` calls. Hooks 1/2/4/5/6/7/9/10/11 do not touch `ZPlayFabSocket` at all except
  where a hook must **type-guard against it**: Hook 2 (T2) explicitly restricts itself to `is ZSteamSocket`, since
  PlayFab has no per-connection Steam config concept; Hook 5 (T8)'s telemetry aggregation must type-guard
  `is ZPlayFabSocket` before calling `GetCurrentSendRate()`, which throws `NotImplementedException` on that type
  (confirmed SERVER:91676-91679, re-verified structurally present this pass via the method's declared body).
  `ZPlayFabSocket`'s own `GetSendQueueSize()` heuristic (`0.25 * InFlightQueue.Bytes`, SERVER:91671-91674) still
  flows through Hook 6/7's budget math unchanged, so a PlayFab peer's Q3/Q5 raised budget is naturally scaled down
  by that transport's own heuristic exactly as vanilla's would be — 10-LEVERS' guard (leave `ZPlayFabSocket` peers
  at vanilla budget pending a live test) is a **stricter-than-necessary** recommendation this doc notes but does
  not hard-code, since the heuristic scaling already provides a structural (if untested) safety margin; flagged as
  an Open question below rather than silently adopting or silently overriding 10-LEVERS' more conservative stance.

---

## Configuration

**RED-TEAM FIX** (RT2, major): the original text here said these keys are "pushed to modded clients via the shared
infra's custom RPC after the capability handshake (P6-style)" — directly contradicting two other sections of this
same document: §Vanilla and version compatibility's "No custom RPC is required for anything in this document's
server-side scope," and its "This document does not itself define or require the shared infra's version-tagged
capability handshake (G1-N1/P6, out of scope)," and the Summary's own M2 rationale, which defers G1-N1 specifically
*because* "this doc assumes exists but doesn't itself build" the handshake/negotiation primitive — i.e., that
primitive is not available to lean on in M1's scope at all. Resolved in favor of the "no RPC needed" claim, because
it is also the mechanically correct one: every M1 hook (Hooks 1–9, 12) only ever configures **its own process's**
local behavior (this process's Steam socket, this process's `ZDOMan` send loop, this process's field guard) — no
hook needs the *other* side of a connection to know its value to function correctly, unlike, say, a protocol version
number that both ends must agree on. `Q10`'s row already documents the one genuinely client-local case. The table's
`Sync` column below now reads **`server-local`** (relabeled from the original "server-authoritative," which implied
an RPC push that does not exist in M1) for every key except `Q10` — meaning each key is read only by the server
process and only affects the server's own behavior; no key in this table is synced to a modded client in M1. A
future milestone that gives a client a reason to *know* one of these values (e.g., a client-side dashboard showing
the server's configured budget) would need the shared infra's handshake primitive first, at which point it stops
being this document's scope, consistent with G1-N1 already being filed under M2.

All keys live under a `[Transport]` BepInEx `ConfigFile` section. A modded client sets nothing from this table
locally except its own opt-in `Q10` client-side values, which are inherently local-only (they configure *that
client's own* upload behavior, and there is no value here for the server to push even if the handshake existed).

| Key | Default | Range | Sync |
|---|---|---|---|
| `Transport.T1_GlobalRateBps` | 262,144 (was 1,048,576; DESIGN.md Lead decision 7) | 153,600 – (0.7 × operator uplink, config-declared) | server-local |
| `Transport.T2_JoinBurstBps` | 4,194,304 | ≥ T1 value, ≤ 8× T1 | server-local |
| `Transport.T2_MaxBurstSeconds` | 30 | 5 – 120 | server-local |
| `Transport.T3_TimeoutConnectedMs` | 30,000 | 30,000 – 120,000 | server-local |
| `Transport.T4_PingIntervalSec` | 1.0 | 0.5 – 2.0 | server-local |
| `Transport.T4_TimeoutSec` | 30.0 | 30.0 – 90.0 | server-local |
| `Transport.T5_SendBufferBytes` | 131,072 | 2,048 – 262,144 (hard clamp) | server-local |
| `Transport.T8_Enabled` | true | bool | server-local |
| `Transport.Q2_PeersPerTick` | 4 | 1 – 8 (hard clamp; 1 = vanilla behavior) | server-local |
| `Transport.Q2_Adaptive` | true | bool — if true, `Q2_PeersPerTick` is a *ceiling*, not a fixed value (see Open questions) | server-local |
| `Transport.Q3_InnerBudgetBytes` | 32,768 | 10,240 – 131,072 (hard clamp) | server-local |
| `Transport.Q4_CongestionFloorBytes` | 4,096 | 512 – 8,192 | server-local |
| `Transport.Q5_OuterGateBytes` | 32,768 | **forced equal to `Q3_InnerBudgetBytes`** — see below | derived, not independently settable |
| `Transport.Q6_ServerZoneBias` | 50.0 | 0 – 200 (same units as vanilla's `staleness * 1.5f` term, i.e. up to ~2 zone-widths of extra distance-equivalent priority) | server-local |
| `Transport.Q7_ChunkIdCount` | 8,000 | 4,000 – 16,000 | server-local |
| `Transport.Q8_ForceSendBatchCap` | 1,000 | 100 – 5,000 | server-local |
| `Transport.Q10_Enabled` | false | bool, client-local only | **not synced** — opt-in per client |
| `Transport.Q12_FramingMarginBytes` | 4,096 | 1,024 – 16,384 | server-local |
| `Transport.G2N1_ChunkBytes` | 65,536 | 16,384 – 131,072 | server-local |
| `Transport.G6N4_MaxFieldBytes` | 2,048 | 256 – 8,192 | server-local |

**Q3/Q5 equality**: there is deliberately **one** config key (`Q3_InnerBudgetBytes`) exposed for the admin; the
config layer does not expose a separate `Q5` setting at all — `SharedBudget.Clamped(Config.Q3_InnerBudgetBytes.Value)`
is the single value both the outer-gate getter and the inner-budget getter in Hook 6 read (see the code sketch:
`OuterGateBytes` and `InnerBudgetBytes` are two properties, but Q3 and Q5's defaults above are shown equal only to
document the *value*, not to imply two independently-writable settings — the `.cfg` file itself will only ever
contain one entry for this pair). This structurally forecloses the "outer gate silently caps the inner budget"
hazard 10-LEVERS warns about, rather than merely documenting the constraint.

---

## Telemetry

`NoodlesMcDoodles.Core.Telemetry.Transport` counters, aggregated in memory, flushed as one periodic `LogInfo` line
(interval config-bound, default 60 s) — never per-event inside a 20 Hz loop, per the shared infra's P7-style
guidance:

- `GlobalRateApplied(rate, timeoutMs)` — logged once at startup; proves T1/T3 actually fired (vs. silently
  no-op'ing because the flag was off or `s_applied` was already true from a prior call).
- `JoinBurstApplied(peerUid)` / `JoinBurstReverted(peerUid, reason)` — per-peer counters (count of bursts applied,
  count reverted-by-drain vs. reverted-by-timeout); a burst that never reverts (neither counter fires within, say,
  5 minutes of a still-connected peer) is itself a diagnostic signal worth surfacing.
- `AggregateNetStats(...)` samples — per-peer `GetSendQueueSize()`/`GetCurrentSendRate()` high-water marks, sampled
  once per second, summarized (min/max/p95) per flush interval; this is the direct evidence Q2/Q3/Q5's tuning is or
  isn't saturating a peer's queue.
- `DestroyChunksSent(count)` — running total of chunked `SendDestroyed` invocations vs. how many chunks each needed;
  a sustained high chunk-count is a signal the destroy-list is growing faster than one round can drain, worth
  surfacing to whichever subsystem is generating the destroys (likely zonesim handovers).
- `ForceSendBatchCapped(attemptedCount)` — count of times a force-send call was truncated by Q8's cap; a
  consistently-hit cap means the cap is too low for real handover-driven force-send bursts.
- `OversizedZdoWriteRejected(key, bytes)` — G6-N4's rejection counter; in a correctly-behaving mod this should
  **always read zero** — any nonzero count is itself the proof this guard caught something before it could trip the
  boot loop, which is exactly the "prove nothing was lost" signal P7 asks every hook to provide.
- **Zero-data-loss proof for this module specifically**: none of Hooks 1–12 touch `CreateNewZDO`/`DestroyZDO`'s
  actual data path (only *when*/*how fast*/*in what order* an already-existing decision to send/destroy is
  executed) — so this module's telemetry is oriented at proving *queues never overflow into the boot-loop hazard*
  (`OversizedZdoWriteRejected`, the Q12 running-total guard's own skip-and-log path, not separately counted above
  but should be — add `Q12SkipLogged(zdoUid, wouldBeSize)` as a companion counter) rather than at proving ZDOs
  aren't lost, which is a claim this module doesn't need to make since it never deletes or mutates ZDO data.

---

## Interactions with other subsystems

- **Ownership/authority (G3, out of scope here)**: Hook 8 (Q6) reads a `HashSet<Vector2s>` of server-held zones that
  module must maintain and expose; Hook 2's join-burst and Hook 6's raised budgets both increase how much bandwidth
  is available right after a `SetOwner` call, which is exactly when the ownership module's own `OwnerRevision`-driven
  resend burst (research/10-LEVERS "Ownership-churn ↔ bandwidth coupling") happens — this module's levers make that
  burst land faster and safer, they do not cause it.
- **Zone simulation (G4, out of scope here)**: Hook 9 (Q7)'s chunking is the direct dependency any zone-handover
  feature that destroys/recreates many ZDOs at once needs; Hook 10 (Q8) is the direct dependency any handover
  feature needs to promptly resync observing peers. Both are designed as reusable primitives, not Phase-A-only
  code.
- **Gameplay under server authority (G5, out of scope here)**: no direct interaction — this module never touches
  gameplay RPCs (`ZRoutedRpc` traffic beyond `DestroyZDO`), only `ZDOData` and Steam/`ZRpc` transport plumbing.
- **Client smoothing (C*, `11-client-smoothing.md`, out of scope here)**: raised send cadence/budget (Q2/Q3/Q5) and
  send-priority reordering (Q6) both change how often and in what order a client *receives* position/state updates
  for nearby objects, which is exactly the input the smoothing module interpolates between — a Phase A change that
  makes updates arrive faster/more-regularly should, if anything, make the smoothing module's job easier (fresher
  data to interpolate toward), but the smoothing module should be re-tuned after Phase A ships rather than assumed
  unaffected.
- **Delta/compression (D*, `12-delta-and-compression.md`, out of scope here)**: this module raises how many bytes
  can go out per send opportunity; it does not change what's inside those bytes. A future delta-encoding module
  would reduce the *size* of what Hook 6's budget has to fit, directly increasing how many ZDOs fit per packet at
  the same byte budget — complementary, not competing, levers.
- **Persistence/data loss (G6, this doc implements G6-N4 as a library, P7-style telemetry pattern)**: P1–P8's own
  save/backup timing is untouched by this module; the one shared concern is G6-N3's kill-switch-must-flush-holds
  guard, which does not apply to this module (this module never calls `SetOwner`), and P4's general kill-switch
  contract, which every hook above implements via its own `Features.Transport.<Lever>.Active` check.
- **Other mods**: Hook 6's transpiler is the single highest-collision-risk patch in this document (a hot,
  20 Hz-per-peer method); the shared infra's `Harmony.GetPatchInfo` startup scan (P4 guard, shared infra contract)
  must specifically name any other mod already patching `ZDOMan.SendZDOs`/`SendZDOToPeers2`/`SendDestroyed`/
  `ServerSortSendZDOS` at startup, loudly, since Harmony's AND-of-prefixes / sequential-transpiler-application
  semantics mean a second mod's independent patch on the same method can silently interact with this one with no
  visible symptom until a live test surfaces it.

  **RED-TEAM FIX** (RT2, major): this is not a hypothetical for Hook 7 (Q2). `research/13-mod-compat-census.md`
  (this project's own Cecil-based compat census, lines 110-119/215-216/385-386) identifies
  `ZenDragon-Zen_ModLib`'s `Zen.FixVanilla.Optimizations.OptimizeNetworkZDOManSendToPeers2` as an existing
  **non-skip** prefix on `ZDOMan.SendZDOToPeers2` — the exact method Hook 7 fully replaces via `return false` — rated
  **HIGH** risk and explicitly flagged as "must be validated for coexistence, not assumed independent." Confirmed
  installed on the live target this pass: `ls /home/rohan/ArchShare/ProximaMaxi/BepInEx/plugins/` shows
  `ZenDragon-Zen_ModLib-1.14.16`. Per Harmony's own semantics (re-confirmed by research/13's own text, cited
  verbatim there): **all** prefixes on a patched method run regardless of what any of them returns — only the
  *original* method's execution is skipped when any prefix returns `false`. So Zen_ModLib's prefix runs on every
  `SendZDOToPeers2` call **alongside** Hook 7's full-replacement prefix, in whatever order Harmony's default
  (unpinned) priority resolves — order that is sensitive to plugin load order and can silently change across a
  BepInEx or mod update. If Zen_ModLib's patch reads or writes `ZDOMan`'s `m_nextSendPeer`/`m_sendTimer`/`m_peers`
  state (plausible given its name), the two mods' mutations can interleave unpredictably: peers silently skipped or
  double-serviced, with no visible symptom until a live test surfaces it — exactly the failure mode this section's
  prose already warns about in the abstract, but did not resolve for this specific, already-known case. **Fix,
  required before Q2 ships to Proxima** (this is a live-server go/no-go gate, not a nice-to-have): (1) read
  `Zen_ModLib`'s `OptimizeNetworkZDOManSendToPeers2` patch body directly (the DLL is local and its method bodies
  are not obfuscated, per research/13) to determine exactly what `ZDOMan` state it touches; (2) set an explicit
  `[HarmonyPriority]` on Hook 6's and Hook 7's patches relative to it, rather than relying on Harmony's default
  ordering; (3) the shared infra's startup conflict scan must **fail loud and auto-disable** `Q2`/`Q3`–`Q5` (not
  merely log) when `Zen_ModLib`'s patch is present and step (1)/(2) haven't been completed and verified live — the
  existing "loudly" language in this section is upgraded from a logging requirement to a stand-down requirement for
  this specific, already-known collision.

---

## Test plan

**RED-TEAM FIX** (RT1, blocking prerequisite added ahead of step 1): before any of the below, **statically verify
Hook 6's transpiled IL is stack-balanced and value-correct** — apply the patch in isolation (unit test, no live
connection needed) and either (a) dump the patched `SendZDOs` IL via Harmony's `Harmony.DEBUG = true` /
`PatchProcessor.GetOriginalMethod`+manual IL print and confirm exactly one `Ldc_I4`/`call` push per literal site
with no orphaned stack slot at `ret`, or (b) JIT-invoke the patched method directly (a small harness calling
`SendZDOs` with a stub `ZDOPeer`/`bool` across a range of `sendQueueSize` values) and confirm it returns without
`InvalidProgramException` and produces the same `bool`/`zPackage` shape as an unpatched call at matching config
values. This step is what would have caught the `SetOperandAndAdvance`+`InsertAndAdvance` stack-imbalance bug before
step 1 below ever touched a real connection; it stays in the plan as a standing regression check for any future edit
to this transpiler, not just this fix.

**Testbed (`libs-Tools/DEDICATED-SERVER-TESTBED/run-linux-1.0-server.sh`), single server + one real client**, before
any live-server deployment:

1. **Boot-loop regression (Q12/G2-N1/Hook 6)**: with all Phase A flags on, join, force a large `m_invalidSector`
   backlog (teleport repeatedly across many zone boundaries in a short window) and confirm no disconnect, that
   `OversizedZdoWriteRejected`/the Q12 skip-log path (if triggered at all) never coincides with a lost object on
   reconnect, **and** — since a reconnect forces a full resync and would mask the G2-N1 partial-drain bug the
   red-team fix above addresses — additionally confirm *without* reconnecting that a backlog spanning multiple
   `SendZDOs` calls is fully delivered (compare the teleported-through sectors' ZDO count on the client against the
   server after all chunks should have landed, staying connected throughout).
2. **Destroy-chunk regression (Q7)**: trigger a multi-thousand-ZDO destroy burst (e.g. `devcommands`
   area-clear on a densely built test zone) and confirm no disconnect, all chunks land (compare pre/post ZDO counts
   via console), and `DestroyChunksSent` reports a sane chunk count.
3. **k-peers-per-tick correctness (Q2)**: with 2+ real clients connected, confirm no sort-order corruption or
   crash under sustained play — this specifically exercises the "never parallelize, always re-read `peers.Count`"
   guard; a scripted disconnect/reconnect storm from one client while the other is mid-sync-round is the sharpest
   version of this test.
4. **Join-burst lifecycle (T2)**: connect a client, watch `JoinBurstApplied`/`JoinBurstReverted` fire exactly once
   each per connection, confirm the reverted rate matches T1's configured value (not vanilla's 153600) via T8's new
   telemetry.
5. **Timeout reassertion (T4)**: force a PlayFab/crossplay join (or, if unavailable in the testbed, call
   `ZPlayFabSocket.OnNetworkJoined`/`Accept` via a debug console command) and confirm `ZRpc.m_timeout` reads back the
   configured 60 s, not vanilla's 90 s, immediately after.
6. **Kill switch mid-session**: flip `Features.Transport.Active` off while a client is connected and mid-sync;
   confirm (a) no disconnect, (b) `SendZDOs`/`SendZDOToPeers2`/`SendDestroyed` visibly revert to vanilla cadence in
   the telemetry log, (c) re-enabling mid-session resumes Phase A behavior without a reconnect.
7. **T8 telemetry sanity**: confirm the server's own F2-equivalent/log output now shows nonzero
   ping/quality/byte-rate for a connected peer where vanilla would show zeros (this is the one directly-observable
   "did T8 work" signal without needing a packet capture).

**Live-server measurement (Proxima Maxi, via `NoodlesProbe` client-side telemetry and this module's own server-side
counters)**, once the testbed passes:

- Compare `baseline/2026-09-28-probe-solo/FINDINGS.md`'s upload p95 (115–148 KiB/s against vanilla's 150 KiB/s cap)
  against the same measurement with T1/T2 active — the probe's own send-queue/upload-rate columns are the direct
  before/after signal.
- With Arizona connected and running the client-side Q10 opt-in (the one lever this doc identifies as directly
  relevant to his case today), compare his own upload p95 before/after — this is the concrete test of the Summary
  section's central claim.
- Run a full session with 2+ real players and watch `AggregateNetStats`' p95 send-queue-size per peer; Q2's k=4
  default should show a measurably lower per-peer queue-size p95 than a k=1 baseline capture under the same
  player count.

---

## Open questions

1. **Q2's fixed-vs-adaptive value.** The user's own selection (`Q2 "2-"`) is incomplete. `10-LEVERS.md` recommends
   k=2–8, adaptive/frame-time-capped; `HANDOFF.md`'s assumed default (pending user confirmation) is a **fixed** k=4.
   This doc's config table exposes both (`Q2_PeersPerTick` as a ceiling, `Q2_Adaptive` as a bool) so either can ship
   without a code change, but the **default** (`Q2_Adaptive=true`, ceiling=4) needs the user's explicit sign-off
   before M1 ships, per the standing open item in `HANDOFF.md`.
2. **Whether a live per-connection Steam config revert (Hook 2's burst-to-global step) actually takes effect on an
   already-open connection**, or only on the *next* connection — `10-LEVERS.md` T1's own Open question 1 (carried
   from research/01) is unresolved from the decompile alone and directly affects whether Hook 2's revert step does
   anything observable. Needs a live test (packet-capture or `GetCurrentSendRate()` sampling before/after the
   revert call on a real connection).
3. **PlayFab peers and Q3/Q5's raised budget** — this doc took a less conservative stance than 10-LEVERS'
   "exempt `ZPlayFabSocket` peers pending a live test" guard, reasoning that `GetSendQueueSize`'s `0.25×` scaling
   heuristic already provides a structural safety margin. This reasoning is **not** itself live-tested; if a
   crossplay live test shows the heuristic's margin is insufficient, the exemption should be added back before
   shipping Q3/Q5 with a PlayFab population.
4. **Q7's queue-size-aware defer-between-chunks guard** (10-LEVERS' fuller Q7 guard #2) is not implemented in this
   doc's Hook 9 sketch, since `InvokeRoutedRPC(0L, ...)`'s broadcast form doesn't expose a per-peer send-queue check
   without restructuring the call into a per-peer loop — worth doing, deferred to avoid changing `SendDestroyed`'s
   broadcast semantics within Phase A's first cut.
5. **Real-world CPU cost of Hook 8's O(1) zone-membership lookup at Q2's raised k — RED-TEAM FIX (RT2, minor),
   scope widened.** The original version of this question considered only Hook 8's marginal per-lookup cost in
   isolation. `SendZDOToPeers2` (SERVER:76883-76909, confirmed this pass) has no per-iteration gate once a round is
   active — only the 0.05 s gate decides when a new round *starts* — so raising Q2's `k` from 1 to 2–8 multiplies,
   roughly linearly, how many times **per frame** the server now runs `CreateSyncList` + `ServerSortSendZDOS`'s
   per-object loop (with Hook 8's added zone-membership branch, plus the reflection-based field reads the Hook 8
   RED-TEAM FIX above introduces to work around `ZDOPeer`'s private accessibility) + `SendZDOs`'s per-item
   serialize/write loop, concentrated into one frame instead of spread over up to k frames. At `k`=8 with archserver's
   measured baseline (13% of one core, ~1.13M ZDOs, shared with two other Valheim servers), this is likely still
   cheap, but neither this doc nor 10-LEVERS states the arithmetic — the estimate needed before shipping the
   adaptive ceiling: sample single-peer `SendZDOs`+`ServerSortSendZDOS` cost (e.g. from `AggregateNetStats`,
   Hook 5/T8) at Proxima's real near-ring sizes, multiply by the candidate k ceiling, and state the resulting
   worst-case per-frame budget explicitly here or in `AdaptiveK.Compute`'s own tuning, rather than leaving the
   combined Q2×Q6 cost entirely to live observation.
6. **T2's exact numeric relationship to T1** — the config table enforces `T2 ≥ T1`, but whether 4× (the user's
   literal 4,194,304 vs. 1,048,576 targets) is the right ratio for a *join* burst specifically (as opposed to, say,
   an *ownership-handover* burst, which is a different scenario Q8/Hook 10 already covers via force-send rather
   than a raised rate) hasn't been measured against a real cold-join's actual byte volume.

---

## Red-team reconciliation

Both red-team reviews were re-verified this pass directly against the decompiles (never taken on faith), using the
same `grep -n`/`sed -n` discipline as the rest of this document. Every genuine issue is now fixed in place above,
marked **RED-TEAM FIX**; nothing was rejected, and nothing was downgraded out of M1 — the two blockers that would
have stopped M1 from building/running at all are fixed at the code-sketch level, and the majors are fixed at the
design level (an explicit gate, an explicit priority/verification requirement, or a resolved contradiction). No item
needed removal from its milestone.

| # | Source | Severity | Where | Disposition | Evidence this pass |
|---|---|---|---|---|---|
| 1 | RT1 #1 / RT2 #1 (same bug, filed independently by both reviews) | **Blocker** | Hook 6 transpiler (`ZDOMan_SendZDOs_Budget.Transpiler`) | **Fixed** — `SetOperandAndAdvance(0).InsertAndAdvance(...)` replaced with `SetInstructionAndAdvance(...)` at all three literal sites | Confirmed the flaw is real against Harmony's actual `CodeMatcher` API: `SetOperandAndAdvance` mutates only the matched instruction's operand and advances; it does not remove/replace the instruction. `InsertAndAdvance` inserts new instructions without removing anything. The combination leaves the original `Ldc_I4` (now `Ldc_I4 0`) *and* a new `call <getter>` both executing at each site — two pushes where the surrounding comparison/subtraction expects one, at SERVER:77058/77062/77063 (re-`grep -n`'d this pass, correcting the doc's own prior 77057/77061/77062 citations — see item 5 below). `SetInstructionAndAdvance` is a real, distinct `CodeMatcher` method that replaces the whole instruction in place, carrying over labels/exception-block markers. |
| 2 | RT1 #2 | **Major** | Hook 6, G2-N1 partial-drain guard | **Fixed** — track written IDs in a scratch list, `peer.m_invalidSector.ExceptWith(writtenIds)` after each chunk, never a bare "don't Clear" | Confirmed `m_invalidSector` is a plain `HashSet<ZDOID>` (SERVER:76032) consumed by an unconditional `foreach` + `.Clear()` (SERVER:77075-77083), with no existing per-item removal path the original "leave it, don't Clear" sketch could have relied on. Read literally, that sketch would re-enumerate the same leading subset every subsequent call (a `HashSet`'s enumeration order does not change between calls that don't mutate it), starving later entries — masked by the doc's own reconnect-based regression test, which forces a full resync regardless. Test plan updated (§Test plan #1) to also check a stay-connected multi-call drain, not just reconnect-then-check. |
| 3 | RT1 #3 | **Major** | Hooks 1 (T1/T3) and 2 (T2), "Side: both" vs. Summary's Q10-only claim | **Fixed** — both hooks now gate their non-authoritative-process branch behind `Features.Transport.Q10.Active`, using `ZNet.IsServer()` (true for dedicated server *and* listen-server host) to distinguish "this process is authoritative" from "this process is a genuine remote client" | Confirmed `ZNet.RPC_PeerInfo`'s shared tail (`m_zdoMan.AddPeer(peer); m_routedRpc.AddPeer(peer);`, SERVER:79989-79990) runs unconditionally after either handshake branch, and `IsDedicated()` is hardcoded per-build (`true` server, `false` client — SERVER:81194-81197/CLIENT:81380-81383) regardless of whether a client process is hosting or purely remote, so the original sketches' `IsDedicated()`-only branch selected an API class but never gated *applicability*. Confirmed `ZNet.IsServer()` (SERVER:81184-81186, `return m_isServer;`) is the correct, separate test for "authoritative," true for both a dedicated server and a listen-server host. The Milestones section's own stated intent ("client-side twin gated behind Q10") is now actually implemented by the code, not just asserted in prose. |
| 4 | RT2 #2 | **Blocker** | Hook 7 (`AccessTools.FieldRefAccess<ZDOMan, List<ZDOPeer>>`) and Hook 8 (`Prefix(..., ZDOPeer peer)`, `Dictionary<ZDOID, ZDOPeer.PeerZDOInfo>`) | **Fixed** — never spell `ZDOPeer`/`PeerZDOInfo` in source; Hook 7 reads `m_peers` as non-generic `IList` via a cached `FieldInfo`; Hook 8's `peer` parameter is `object` (standard Harmony pattern for an inaccessible original-method parameter type) and its internals go through `AccessTools.Inner`-resolved `Type`s and cached `FieldInfo`s, boxing to `IDictionary` where possible | Confirmed `private class ZDOPeer` nested inside `public class ZDOMan` on **both** builds (SERVER:76008/CLIENT:76133, inside `public class ZDOMan` at SERVER:76006/CLIENT:76131) — a private nested type cannot be named in C# source outside its declaring type, which is a plain compile error (`CS0122`), not a runtime issue, confirming this would have blocked M1 from building at all. Confirmed `PeerZDOInfo` is `public struct` but nested inside the private `ZDOPeer`, so it inherits the same unreachability. Flagged an honest performance cost (boxing + `FieldInfo.GetValue` per near-ring ZDO in Hook 8) as a documented, deferred follow-up optimization, not a blocker. |
| 5 | RT2 #3 | **Major** | Hook 7 (Q2) vs. `ZenDragon-Zen_ModLib`'s existing non-skip prefix on `ZDOMan.SendZDOToPeers2` | **Fixed at the design level** — required `[HarmonyPriority]` + live verification before Q2 ships to Proxima; the shared infra's startup conflict scan upgraded from "log" to "fail loud and auto-disable Q2/Q3–Q5" specifically for this known collision | Confirmed via `research/13-mod-compat-census.md` (lines 110-119/215-216/385-386, this project's own Cecil-based census) that `ZenDragon-Zen_ModLib`'s `OptimizeNetworkZDOManSendToPeers2` is a non-skip prefix on the exact method Hook 7 fully replaces, rated HIGH. Confirmed the DLL is actually installed: `ls /home/rohan/ArchShare/ProximaMaxi/BepInEx/plugins/` lists `ZenDragon-Zen_ModLib-1.14.16`. This is not fixable purely inside this document (it depends on a third-party mod's internals), so the disposition is a hard go/no-go gate on Q2's live rollout, not a code change here. |
| 6 | RT2 #4 | **Major** | §Configuration ("pushed to modded clients via the shared infra's custom RPC after the capability handshake") vs. §Vanilla and version compatibility ("No custom RPC is required...", "does not itself define or require the... handshake") vs. §Modded version skew ("every value here is process-local") vs. Summary's M2 deferral of G1-N1 ("assumes exists but doesn't itself build") | **Fixed** — resolved in favor of "no RPC, no handshake dependency in M1"; the Configuration table's `Sync` column relabeled from "server-authoritative" to **`server-local`** for every key except `Q10`, and the section header text corrected to match | This was a genuine three-way internal contradiction, not a wording nit — re-read all three passages this pass and confirmed they cannot all be true simultaneously. Resolved by checking the actual mechanics: none of Hooks 1–9/12 need the *other* side of a connection to know their configured value to function (each configures only its own process's local behavior), so the "process-local, no RPC" framing is both internally consistent with the rest of the document and matches how the hooks are actually built — the "pushed via RPC" framing was the odd one out and is corrected. |
| 7 | RT2 #5 | **Minor** | Citation drift: doc's own line 78 vs. Hook 6's lines 411-413 vs. the actual file | **Fixed** — all citations corrected to SERVER:77058 (outer gate) / 77062 (inner-budget complement) / 77063 (congestion floor); G2-N1's invalid-sector block citation corrected to SERVER:77075-77083 | Re-`grep -n`'d this pass: `sendQueueSize > 10240` → 77058; `int num = 10240 - sendQueueSize;` → 77062; `if (num < 2048)` → 77063; `if (peer.m_invalidSector.Count > 0)` → 77075, `peer.m_invalidSector.Clear();` → 77083. None of the document's four prior citations across these two passages fully agreed with each other or with the file. |
| 8 | RT1 #4 | **Minor** | Hook 7 (Q2) amplifying lever G1-L1's blast radius (duplicate `ZDOPeer` wrappers via `AddPeer`'s missing existing-entry check) | **Fixed** — added a cross-reference note in Hook 7 and a purely-defensive mitigation (dedupe `peers` by `ZNetPeer` object identity before iterating), without taking on G1-L1's fix itself (correctly out of scope here) | Confirmed `ZDOMan.AddPeer` (SERVER:76814-76820) is an unconditional `List.Add` with no existing-entry check, and Hook 2's own re-entry-hazard note already documents the `RPC_PeerInfo` replay path (G1-L1) that can reach it twice for one physical connection. Confirmed this is a fairness/efficiency concern (redundant sends to one connection under Q2's raised k), not a data-loss or correctness bug, matching RT1's own "minor" severity call. |
| 9 | RT1 #5 | **Minor** | Hook 9 (Q7 `SendDestroyed` chunking) — no exception guard around per-chunk `InvokeRoutedRPC`, unlike Hooks 1/2 | **Fixed** — wrapped each chunk's send in `try/catch`, logging and leaving the unsent tail in `m_destroySendList` (not cleared) for the next tick, consistent with this document's own "skip, log, retry, never truncate" discipline used elsewhere for Q12/G2-N1 | Confirmed vanilla's own `SendDestroyed` clears `m_destroySendList` *before* calling `InvokeRoutedRPC` (SERVER:76996-76997), the opposite order from this hook's chunked sketch (which clears only after all chunks fire, to keep "what landed" honest) — confirming the reordering was real and that an added exception guard was the correct, low-cost fix rather than reverting the ordering change. |
| 10 | RT2 #6 | **Minor** | Open question 5 — Hook 8's per-lookup cost considered in isolation from Q2's k-multiplier | **Fixed** — question rewritten to combine both, naming the specific measurement (single-peer `SendZDOs`+`ServerSortSendZDOS` cost × candidate k ceiling) needed before shipping the adaptive ceiling | Confirmed `SendZDOToPeers2` (SERVER:76883-76909) has no per-iteration gate once a round is active, so Q2's k does multiply per-frame cost roughly linearly, as RT2 argued; no existing measurement in this doc or 10-LEVERS combines the two, so this stays an open question (appropriately, since it needs a live measurement this review cannot itself produce) rather than a fix — but its scope is now accurate. |

**Net verdict**: both blockers (Hook 6's transpiler, Hooks 7/8's `ZDOPeer` accessibility) are fixed at the sketch
level and would need to be re-verified once real code exists (an IL-dump/JIT-invoke check for Hook 6, a compile check
for Hooks 7/8) — both are now called out as required, blocking test-plan steps, not just described as fixed in prose.
All four majors are fixed: two as code-level gates (Hooks 1/2's `Q10` gate), one as a hard live-verification gate on
Q2's rollout (Zen_ModLib), and one as a documentation-consistency fix with no code impact (the Configuration
section). All four minors are fixed. No issue from either red team was rejected — independent re-verification against
the decompiles this pass confirmed every one of them was a real, evidence-backed finding, not a false positive.
