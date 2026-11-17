using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using NoodlesMcDoodles.Core;

namespace NoodlesMcDoodles.Transport
{
    /// <summary>
    /// Q7 — chunk <c>ZDOMan.SendDestroyed</c>'s write instead of writing the whole
    /// <c>m_destroySendList</c> into one <see cref="ZPackage"/> with zero size check
    /// (SERVER:76984-76996 / CLIENT:77126 area, byte-identical body confirmed via <c>ilspycmd -il</c> this
    /// pass — only assembly-reference metadata differs between builds, not a single opcode/operand).
    /// docs/design/01 Hook 9.
    ///
    /// Full <c>[HarmonyPrefix]</c> replacement, not a transpiler — the chunking loop has no vanilla
    /// counterpart to transpile around (Hook 9's own "Patch kind" reasoning).
    /// </summary>
    internal static class DestroyChunking
    {
        public static ConfigEntry<int> Q7ChunkIdCountConfig;
        public static FeatureFlag Q7;

        // m_destroySendList is `private readonly List<ZDOID>` (SERVER:76092) — resolved once via AccessTools,
        // read/written through FieldRefAccess rather than naming a member the decompile shows as private,
        // regardless of whether the build-time assembly publicizer also makes it directly nameable (this
        // pattern is correct either way and matches every other hook's own established RED-TEAM-FIXED style).
        private static readonly AccessTools.FieldRef<ZDOMan, List<ZDOID>> s_destroySendListRef =
            AccessTools.FieldRefAccess<ZDOMan, List<ZDOID>>("m_destroySendList");

        public static void BindAndRegister()
        {
            Q7ChunkIdCountConfig = NoodlesConfig.File.Bind("Transport", "Q7_ChunkIdCount", 8000,
                new ConfigDescription(
                    "IDs per chunk when Q7 sends ZDOMan.SendDestroyed's backlog (~12 bytes/ID; 8000 ~= 96 KB).",
                    new AcceptableValueRange<int>(4000, 16000)));

            var q7Cfg = NoodlesConfig.File.Bind("Features", "Transport.Q7.Active", true,
                "Q7: chunk ZDOMan.SendDestroyed's write instead of one unbounded write. Off = vanilla's " +
                "single unbounded ZPackage write, unchanged.");
            Q7 = Features.Register("Transport.Q7", q7Cfg);

            PatchRegistry.Register("Transport.Q7", typeof(Patch_ZDOMan_SendDestroyed_Chunked), PatchSide.Both);
        }

        [HarmonyPatch(typeof(ZDOMan), "SendDestroyed")]
        internal static class Patch_ZDOMan_SendDestroyed_Chunked
        {
            [HarmonyPrefix]
            internal static bool Prefix(ZDOMan __instance)
            {
                // CLIENT-DLL-REVIEW MINOR: gate on the same eligibility test every sibling M1 lever in this
                // directory (Q3/Q4/Q12/G2-N1, SharedBudget.cs) already applies — a plain remote client without
                // Q10's opt-in must get vanilla's own unbounded write here too, not just on the authoritative
                // side. Previously this only checked Q7.Active, so an opted-out client would still have had its
                // OWN (rare — a real client almost never owns enough ZDOs to fill m_destroySendList) destroy
                // notifications chunked despite never opting in.
                if (!Q7.Active || !SharedBudget.EligibleForThisProcess()) return true; // vanilla's own unbounded write, unchanged

                // Fetching the list is the only step that can fail with NOTHING yet sent — a failure here is
                // safe to fall back to vanilla's own path (`return true`). Every step after this point may
                // already have sent a chunk over the wire, so any later failure must return `false` (never
                // fall back to `true`, which would let vanilla ALSO run and re-send the chunks already sent).
                List<ZDOID> list;
                try
                {
                    list = s_destroySendListRef(__instance);
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_ZDOMan_SendDestroyed_Chunked), e);
                    return true;
                }
                if (list == null || list.Count == 0) return false; // matches vanilla's own early-return (SERVER:76986-76989)

                try
                {
                    int chunkSize = Math.Max(1, Q7ChunkIdCountConfig.Value);
                    int sent = 0;
                    int chunksSent = 0;

                    while (sent < list.Count)
                    {
                        // BACKPRESSURE (server-bandwidth research, SAA re-check — docs/design/01's own Q7
                        // guard 2 was never implemented until now, confirmed by direct read of the loop below
                        // before this fix): SendDestroyed's InvokeRoutedRPC(0L, ...) fans this SAME chunk out
                        // to EVERY ready peer at once (ground truth: ZRoutedRpc.InvokeRoutedRPC/RouteRPC,
                        // SERVER:83600-83669 — target 0 walks every peer in m_peers and calls that peer's own
                        // peer.m_rpc.Invoke), so a back-to-back chunk blast with no gap can pile more onto a
                        // peer whose queue is already near Q4's own congestion floor — exactly the moment SAA's
                        // area-handover/fallback traffic and a destroy burst can land on the same peer at once.
                        // Deferring here is strictly safer than vanilla, never worse: vanilla always sends the
                        // WHOLE unbounded list in one shot regardless of any peer's queue depth, whereas this
                        // always sends at least the FIRST chunk (bounded, ~96 KB by default) before ever
                        // checking, so a fresh backlog is never left completely undrained by this guard.
                        if (chunksSent > 0 && AnyConnectedPeerNearCongestion())
                        {
                            list.RemoveRange(0, sent); // same lossless partial-progress shape as the catch below
                            Telemetry_Transport.DestroyChunksSent(chunksSent, sent);
                            // list.Count is already the REMAINING count post-RemoveRange -- not list.Count - sent.
                            NoodlesLog.DetailRateLimited("q7_backpressure",
                                $"Q7: deferring the remaining destroy-notify chunks ({list.Count} IDs left) " +
                                "to the next round -- a connected peer's send queue is already near congestion.", 3, 60f);
                            return false;
                        }

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
                            // RED-TEAM FIX (RT1, minor, docs/design/01 Hook 9): fail closed — stop chunking,
                            // drop only the chunks that actually landed, leave list[sent..] in place (NOT
                            // cleared) so the next SendDestroyed call for this ZDOMan retries the remainder.
                            // Vanilla itself clears m_destroySendList BEFORE InvokeRoutedRPC (SERVER:76996-
                            // 76997); this reversed order (clear only after every chunk lands) is what makes
                            // "which chunks actually landed" honest, so a mid-burst exception can never
                            // silently lose a destroy notification the way clearing-first would risk.
                            NoodlesLog.PatchException(nameof(Patch_ZDOMan_SendDestroyed_Chunked), e);
                            list.RemoveRange(0, sent);
                            Telemetry_Transport.DestroyChunksSent(chunksSent, sent);
                            return false;
                        }

                        sent += n;
                        chunksSent++;
                    }

                    list.Clear(); // only after every chunk has fired this call — RPC_DestroyZDO is idempotent per ID
                    Telemetry_Transport.DestroyChunksSent(chunksSent, sent);
                    return false;
                }
                catch (Exception e)
                {
                    // Anything else unexpected past this point may already have sent a chunk over the wire —
                    // `false` (never fall back to vanilla here, which would re-send whatever already landed).
                    // Whatever remains in `list` (nothing removed by this catch) is retried on the next call.
                    NoodlesLog.PatchException(nameof(Patch_ZDOMan_SendDestroyed_Chunked), e);
                    return false;
                }
            }

            /// <summary>
            /// The backpressure check above's own predicate. Bounded by connected-peer count (never more than a
            /// handful at this project's own target profile — DESIGN.md/COMPRESSION-IMPL.md both cite Proxima's
            /// 2-10 players); reuses <see cref="SharedBudget.CongestionFloorBytes"/>, the SAME live Q4 threshold
            /// Transport's own SendZDOs budget path (SendZdosPatch.cs) already checks against, including its
            /// existing eligibility/flag-off fallback to vanilla's 2048 — never a second, independently-drifting
            /// threshold.
            /// </summary>
            private static bool AnyConnectedPeerNearCongestion()
            {
                ZNet znet = ZNet.instance;
                if (znet == null) return false;
                int floor = SharedBudget.CongestionFloorBytes;
                foreach (ZNetPeer peer in znet.GetConnectedPeers())
                {
                    if (peer?.m_socket == null || !peer.IsReady()) continue;
                    if (peer.m_socket.GetSendQueueSize() >= floor) return true;
                }
                return false;
            }
        }
    }
}
