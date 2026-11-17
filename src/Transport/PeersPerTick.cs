using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.Transport
{
    /// <summary>
    /// Q2 (docs/design/01 Hook 7) — services up to <c>Q2_PeersPerTick</c> peers per
    /// <c>ZDOMan.SendZDOToPeers2</c> round instead of vanilla's one
    /// (SERVER:76883-76909 / CLIENT:77026-77049, byte-identical: idle/armed state machine off
    /// <c>m_sendTimer</c>/<c>m_nextSendPeer</c>, one <c>SendZDOs</c> call per invocation, round-robin advance).
    ///
    /// Full prefix-replacement, not a transpiler: <c>CreateSyncList</c>/<c>ServerSortSendZDOS</c>/
    /// <c>ClientSortSendZDOS</c> share <c>ZDOMan</c> instance scratch fields and the static
    /// <c>s_compareReceiver</c> field, so the k-peer loop must stay strictly sequential on one thread within a
    /// single tick (docs/design/01 Hook 7) — a structural change, not a literal swap.
    /// </summary>
    internal static class PeersPerTickModule
    {
        internal static FeatureFlag Q2 { get; private set; }
        internal static BepInEx.Configuration.ConfigEntry<int> Q2PeersPerTick { get; private set; }
        internal static BepInEx.Configuration.ConfigEntry<bool> Q2Adaptive { get; private set; }

        [NoodlesModuleInit]
        internal static void Init()
        {
            var q2ActiveConfig = NoodlesConfig.File.Bind("Features", "Transport.Q2.Active", true,
                new BepInEx.Configuration.ConfigDescription(
                    "Q2: server (or listen-server host) sends to up to Q2_PeersPerTick peers per " +
                    "SendZDOToPeers2 round instead of vanilla's one. Off = vanilla one-peer-per-tick loop, " +
                    "unconditionally. Also yields automatically (see transport.q2.yieldedTo) when another mod " +
                    "already has a prefix on ZDOMan.SendZDOToPeers2 (DESIGN.md Lead decision 2)."));
            Q2 = Features.Register("Transport.Q2", q2ActiveConfig);

            Q2PeersPerTick = NoodlesConfig.File.Bind("Transport", "Q2_PeersPerTick", 4,
                new BepInEx.Configuration.ConfigDescription(
                    "Ceiling on peers serviced per SendZDOToPeers2 round; 1 = vanilla. Hard-clamped in code to " +
                    "1-8 regardless of what this file contains.",
                    new BepInEx.Configuration.AcceptableValueRange<int>(1, 8)));
            Q2Adaptive = NoodlesConfig.File.Bind("Transport", "Q2_Adaptive", true,
                new BepInEx.Configuration.ConfigDescription(
                    "If true, Q2_PeersPerTick is a ceiling AdaptiveK backs off from under sustained frame-time " +
                    "pressure, rather than a value always used as-is."));

            // Lead decision 2: "re-checked when config changes" — any of these three could change whether Q2
            // should even be considered for patching (Active) or just change its own effective k, but a
            // re-check is cheap and this only fires on an actual edit, so we re-check on all three uniformly.
            q2ActiveConfig.SettingChanged += (_, __) => MainThreadDispatcher.Enqueue(Q2YieldGuard.Recheck);
            Q2PeersPerTick.SettingChanged += (_, __) => MainThreadDispatcher.Enqueue(Q2YieldGuard.Recheck);
            Q2Adaptive.SettingChanged += (_, __) => MainThreadDispatcher.Enqueue(Q2YieldGuard.Recheck);

            // PatchSide.Both, not ServerOnly: a listen-server host runs the CLIENT executable
            // (Side.IsDedicatedServerProcess == false there) but is still ZNet.IsServer() == true at runtime and
            // must get the real k-peer loop. The prefix itself decides "is this process authoritative right
            // now" (ZNet.IsServer()) — see the Q10 gate below — which PatchSide's process-type heuristic can't
            // express. For a genuine remote client (m_peers.Count is normally 1), the k-loop is naturally inert
            // regardless, matching Hook 7's own "no-op path for a normal client" note.
            PatchRegistry.Register("Transport.Q2", typeof(Patch_ZDOMan_SendZDOToPeers2_KPeers), PatchSide.Both);
        }
    }

    /// <summary>
    /// DESIGN.md Lead decision 2: Q2 must stand aside when another mod already replaces
    /// <c>ZDOMan.SendZDOToPeers2</c> (on the live target, Zen_ModLib's non-skip
    /// <c>OptimizeNetworkZDOManSendToPeers2</c> prefix does exactly this, more aggressively than our own
    /// ceiling — running both would double the send loop). Checked via <c>Harmony.GetPatchInfo</c> rather than
    /// refusing to patch at all, because Harmony's own semantics mean ALL prefixes on a method run regardless
    /// of what any of them return (only the original's execution is skipped when any prefix returns false) — so
    /// "yield" here means "our own prefix still gets attached (for the config-live-toggle guarantee every other
    /// hook has) but immediately returns true", not "don't patch". This class owns no HarmonyPatch attributes
    /// itself; it's the shared, cheap-to-poll decision both <see cref="Patch_ZDOMan_SendZDOToPeers2_KPeers"/> and
    /// (Q10 opt-in) the client build's own SendZDOs budget patch read before doing anything.
    /// </summary>
    internal static class Q2YieldGuard
    {
        // Re-checked on every Q2-relevant config change (Lead decision 2's explicit requirement) via the
        // SettingChanged handlers in PeersPerTickModule.Init(), AND self-throttled here to a few seconds so a
        // third-party mod that patches SendZDOToPeers2 late (a BepInEx plugin load-order case Harmony gives us
        // no event for) is still caught without needing its own config edit to trigger a re-check. This is not
        // "starting a new timer" (CORE-API.md's "don't start your own periodic driver") — it's a plain elapsed-
        // time guard inside a method already being called from the hot per-tick prefix below, the same pattern
        // NoodlesHeartbeat itself uses for its own telemetry-flush cadence.
        private const float MinRecheckIntervalSeconds = 5f;

        private static volatile bool s_yielding;
        private static string s_yieldedToOwner;
        private static float s_lastCheckRealtime = float.NegativeInfinity;

        public static bool ShouldYield => s_yielding;

        /// <summary>Called from the hot prefix; only actually re-inspects Harmony's patch table when due.</summary>
        public static void CheckIfDue()
        {
            float now = Time.realtimeSinceStartup;
            if (now - s_lastCheckRealtime < MinRecheckIntervalSeconds) return;
            Recheck();
        }

        public static void Recheck()
        {
            s_lastCheckRealtime = Time.realtimeSinceStartup;
            try
            {
                var original = AccessTools.Method(typeof(ZDOMan), "SendZDOToPeers2");
                var info = Harmony.GetPatchInfo(original);
                var foreignPrefix = info?.Prefixes?.FirstOrDefault(p => p.owner != PluginInfo.PLUGIN_GUID);
                bool yielding = foreignPrefix != null;

                if (yielding && (!s_yielding || foreignPrefix.owner != s_yieldedToOwner))
                {
                    s_yieldedToOwner = foreignPrefix.owner;
                    // Two distinct names, deliberately: Lead decision 2's own log format is a TAG
                    // ("transport.q2.yieldedTo=<owner>", the exact grep target it names) carrying the owner
                    // string; "counts it in telemetry" is a separate numeric counter under its own name, so the
                    // two never collide into one flush line trying to hold both a count and a string.
                    Telemetry.GetCounter("transport.q2.yieldedToEvents").Increment();
                    Telemetry.SetTag("transport.q2.yieldedTo", foreignPrefix.owner);
                    NoodlesLog.Warning(NoodlesLogLevel.Normal, $"Q2 yields to another mod's existing prefix on ZDOMan.SendZDOToPeers2 " +
                        $"(owner='{foreignPrefix.owner}') — running vanilla's own one-peer-per-tick body instead " +
                        "of installing a second full replacement (DESIGN.md Lead decision 2).");
                }
                else if (!yielding && s_yielding)
                {
                    NoodlesLog.Info($"Q2: the conflicting prefix on ZDOMan.SendZDOToPeers2 (previously " +
                        $"'{s_yieldedToOwner}') is gone — Q2 resumes its own k-peers-per-tick behavior.");
                    s_yieldedToOwner = null;
                }

                s_yielding = yielding;
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(Q2YieldGuard), e);
                s_yielding = true; // fail open to vanilla if we can't even determine whether it's safe to run.
            }
        }
    }

    /// <summary>
    /// Q2's ceiling-vs-fixed-k policy (docs/design/01 Open question 1: "Q2_Adaptive as a bool" exposes both
    /// without a code change). When adaptive, backs the effective k off from the configured ceiling under
    /// sustained frame-time pressure — Open question 5's own concern (raising k multiplies, roughly linearly,
    /// how many times per FRAME the server now runs CreateSyncList + ServerSortSendZDOS + SendZDOs, since
    /// SendZDOToPeers2 has no per-iteration gate once a round is active) is exactly what this throttle exists to
    /// keep honest at runtime rather than leaving entirely to live observation. Reads <c>Time.unscaledDeltaTime</c>
    /// — already computed by Unity every frame, so this samples an EWMA of it for free, no allocation, O(1).
    /// </summary>
    internal static class AdaptiveK
    {
        private const float EwmaAlpha = 0.1f;

        // ~25ms: comfortably inside a dedicated server's own tick budget (Valheim's fixed-step default is
        // 50Hz/20ms; this floor is deliberately looser so ordinary jitter never trips the backoff, only sustained
        // pressure does).
        private const float HealthyFrameSeconds = 1f / 40f;

        private static float s_ewmaFrameSeconds = HealthyFrameSeconds;

        public static int Compute(int configuredCeiling)
        {
            int ceiling = Mathf.Clamp(configuredCeiling, 1, 8); // hard clamp regardless of the .cfg's raw value
            if (!PeersPerTickModule.Q2Adaptive.Value) return ceiling; // fixed k = the ceiling itself

            float dt = Time.unscaledDeltaTime;
            if (dt > 0f) s_ewmaFrameSeconds += EwmaAlpha * (dt - s_ewmaFrameSeconds);

            if (s_ewmaFrameSeconds <= HealthyFrameSeconds) return ceiling; // healthy: use the full ceiling

            // Under sustained pressure, back off proportionally to how far over budget the frame is — never
            // below 1 (vanilla's own effective rate), never above the configured ceiling.
            float overBy = s_ewmaFrameSeconds / HealthyFrameSeconds;
            return Mathf.Clamp(Mathf.FloorToInt(ceiling / overBy), 1, ceiling);
        }
    }

    [HarmonyPatch(typeof(ZDOMan), "SendZDOToPeers2")]
    internal static class Patch_ZDOMan_SendZDOToPeers2_KPeers
    {
        // Resolved once at class-init, not per-call. ZDOMan.ZDOPeer is `private class ZDOPeer` nested inside
        // `public class ZDOMan` on BOTH builds (SERVER:76006/76008, CLIENT:76131/76133) — BUT this project
        // compiles against a Publicize=true reference assembly (NoodlesMcDoodles.csproj) and Mono does not
        // enforce member accessibility at runtime, so `ZDOMan.ZDOPeer` CAN be named directly in source here,
        // exactly as PeerBudget.cs's own `Patch_ZDOMan_SendZDOs_PeerBudget.Prefix(ZDOMan.ZDOPeer peer, ...)` and
        // SendPriority.cs's own Prefix already do (both compile and run today) — this file's own earlier
        // "cannot be spelled in source" comment was simply wrong; `m_peers` is still read via the non-generic
        // IList below (no reason to also retype that field), but the RT1 dedupe fix reads ZDOPeer.m_peer via a
        // cached FieldInfo purely for symmetry/no behaviour reason to change it now.
        private static readonly FieldInfo s_peersField = AccessTools.Field(typeof(ZDOMan), "m_peers");
        private static readonly MethodInfo s_sendZdos = AccessTools.Method(typeof(ZDOMan), "SendZDOs");
        private static readonly Type s_zdoPeerType = AccessTools.Inner(typeof(ZDOMan), "ZDOPeer");
        private static readonly FieldInfo s_zdoPeerPeerField = AccessTools.Field(s_zdoPeerType, "m_peer");

        // WP5 FIX (found live this pass, booting `noodles`/2531 with a fresh config): SendZDOs is
        // `private bool ZDOMan.SendZDOs(ZDOPeer, bool)` (confirmed against ground truth, SERVER:77055). The
        // return type must match exactly (AccessTools.MethodDelegate throws ArgumentException "method return
        // type is incompatible" otherwise — already hit and fixed, see the return-value comment below) AND,
        // confirmed by THIS boot's own TypeInitializationException, so must every PARAMETER type: a delegate
        // declaring `object zdoPeer` in place of the real `ZDOPeer` throws ArgumentException "method arguments
        // are incompatible" at class-init — HarmonyLib 2.9.0's AccessTools.MethodDelegate does not itself
        // insert a cast/box for a loosely-typed reference-type parameter the way an earlier version of this
        // comment assumed; the delegate's parameter types must match the original method's exactly. Since
        // `ZDOMan.ZDOPeer` can be named directly here (see the field doc above), the fix is to declare it
        // exactly rather than routing it through `object` at all — simpler than the box-and-hope approach,
        // and there is no longer any reason to keep this one parameter looser than the return type already is.
        private delegate bool SendZdosDelegate(ZDOMan instance, ZDOMan.ZDOPeer zdoPeer, bool flush);
        private static readonly SendZdosDelegate s_sendZdosDelegate = AccessTools.MethodDelegate<SendZdosDelegate>(s_sendZdos);

        // RUNTIME REVIEW FIX (minor): this prefix runs every tick during an active send round, and k>1 is the
        // default (Q2_PeersPerTick=4) — reusing one static scratch set instead of a fresh `new HashSet<object>()`
        // per call removes a per-tick Gen0 allocation from a named hot path (review brief: SendZDOToPeers2 "runs
        // every tick for every peer"). Safe as a static: ZDOMan ticking (and this Harmony prefix) only ever runs
        // on the single main game thread, never re-entrantly for the same ZDOMan within one call.
        private static readonly HashSet<object> s_seenNetPeersScratch = new HashSet<object>();

        static bool Prefix(ZDOMan __instance, float dt)
        {
            if (!PeersPerTickModule.Q2.Active) return true; // vanilla one-peer-per-tick body runs unmodified

            Q2YieldGuard.CheckIfDue();
            if (Q2YieldGuard.ShouldYield) return true; // Lead decision 2: another mod already owns this method

            // Q10: a genuine remote client (not this process's own server/listen-host) only gets Q2's behavior
            // if it explicitly opted in — for a normal remote client m_peers.Count is 1 so this is usually moot,
            // but a listen-server host is ZNet.IsServer()==true and must always get the real k-loop regardless
            // of Q10 (Q10 gates the CLIENT-only case, never the authoritative one). Same pattern as Hook 1/2's
            // own T1/T3/T2 gate (docs/design/01, RED-TEAM FIX RT1 #3); shared helper lives in ClientSendLoop.cs.
            if (!ClientSendLoopModule.ShouldApplyOnThisProcess()) return true;

            try
            {
                var peers = (IList)s_peersField.GetValue(__instance);
                if (peers.Count == 0) return false; // matches vanilla's own early-return for this call

                ref float sendTimer = ref AccessTools.FieldRefAccess<ZDOMan, float>(__instance, "m_sendTimer");
                ref int nextSendPeer = ref AccessTools.FieldRefAccess<ZDOMan, int>(__instance, "m_nextSendPeer");
                sendTimer += dt;
                if (nextSendPeer < 0)
                {
                    if (sendTimer > 0.05f) { nextSendPeer = 0; sendTimer = 0f; } // 0.05f: vanilla's own round-start gate (Q1, out of this doc's scope)
                    return false;
                }

                int k = AdaptiveK.Compute(PeersPerTickModule.Q2PeersPerTick.Value);

                // RT1 FIX (docs/design/01 Hook 7): G1-L1's own re-entry gap can leave two ZDOPeer wrappers for
                // one physical connection in m_peers; with k>1 both can be serviced in the same tick. Dedupe by
                // the underlying ZNetPeer's object identity so a stray duplicate is serviced at most once per
                // k-window — purely defensive, doesn't fix G1-L1 itself (out of scope here).
                HashSet<object> seenNetPeers = null;
                if (k > 1)
                {
                    s_seenNetPeersScratch.Clear(); // reused scratch set, not a fresh allocation — see field doc above
                    seenNetPeers = s_seenNetPeersScratch;
                }

                // The loop condition re-reads peers.Count on every pass (never cached), closing the fairness gap
                // from a concurrent RemovePeer mid-round automatically.
                for (int i = 0; i < k && nextSendPeer < peers.Count; i++)
                {
                    // WP5 FIX: cast to the real type here instead of leaving it `object` — see the delegate's
                    // own doc comment above. `peers` is genuinely `List<ZDOMan.ZDOPeer>` under the non-generic
                    // IList view, so this is an ordinary, always-safe reference-type downcast, not a risky one.
                    var zdoPeer = (ZDOMan.ZDOPeer)peers[nextSendPeer];
                    nextSendPeer++;

                    if (seenNetPeers != null)
                    {
                        object netPeer = s_zdoPeerPeerField.GetValue(zdoPeer);
                        if (netPeer != null && !seenNetPeers.Add(netPeer)) continue; // stray duplicate wrapper — skip, still advanced
                    }

                    s_sendZdosDelegate(__instance, zdoPeer, false); // return value discarded — same as the MethodInfo.Invoke call this replaces
                }

                if (nextSendPeer >= peers.Count) nextSendPeer = -1;
                return false;
            }
            catch (Exception e)
            {
                // Fail open to vanilla for this call. Safe even mid-loop: m_sendTimer/m_nextSendPeer are the
                // same fields vanilla's own body reads/writes, so letting vanilla run afterward on whatever
                // state we left them in is exactly as consistent as vanilla ever leaving itself.
                NoodlesLog.PatchException(nameof(Patch_ZDOMan_SendZDOToPeers2_KPeers), e);
                return true;
            }
        }
    }
}
