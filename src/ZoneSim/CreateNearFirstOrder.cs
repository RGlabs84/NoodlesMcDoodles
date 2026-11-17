using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.ZoneSim
{
    /// <summary>
    /// Round 4 (zone-load-stall fix, task item 2): create near players first. Vanilla
    /// <c>ZNetScene.CreateObjectsSorted</c> (confirmed this session against the real decompile: SERVER:82156-
    /// 82169) sorts its per-frame create candidates by <c>Utils.DistanceSqr(ZNet.instance.GetReferencePosition(),
    /// object position)</c>, then <c>ZDOCompare</c> (Type, then that distance). On a dedicated server
    /// <c>GetReferencePosition()</c> is permanently <c>Vector3.zero</c> (no local character ever spawns there),
    /// so vanilla's own "nearest first" is nearest-to-world-spawn, not nearest to any actual player. Round 3's
    /// time-budgeted create loop (<see cref="CreateObjectsBudget"/>) can leave a multi-thousand-object backlog
    /// draining over many seconds; with vanilla's own ordering, objects right next to a connected player wait
    /// exactly as long as ones at the dead reference position -- backwards for a server whose whole purpose is
    /// simulating real territory around real peers.
    ///
    /// <c>CreateDistantObjects</c> (confirmed this session: SERVER:82204-82224) does NOT sort its own list at
    /// all -- no <c>m_tempSortValue</c> write, no <c>.Sort()</c> call anywhere in that method; it simply walks
    /// <c>currentDistantObjects</c> in whatever order <see cref="Patch_ZNetScene_CreateObjects_ObjectFeed"/>/
    /// vanilla's own range scan already built it in. The task brief's own "(and CreateDistantObjects if it
    /// sorts similarly)" therefore does not apply -- there is nothing to transpile there, only
    /// <c>CreateObjectsSorted</c>'s single sort key needs changing.
    ///
    /// Mechanism: <c>Utils.DistanceSqr(Vector3, Vector3)</c> (public static, assembly_utils.dll, exactly one
    /// overload, confirmed this session) is called exactly once in <c>CreateObjectsSorted</c>'s body. The
    /// transpiler below matches that one <c>call</c> instruction by its OWN resolved <see cref="MethodInfo"/>
    /// operand (not a literal/opcode-sequence guess) and swaps ONLY the operand for
    /// <see cref="EffectiveDistanceSqr"/> below, which has the identical <c>(Vector3, Vector3) -&gt; float</c>
    /// signature -- nothing that pushes the two arguments, and nothing that consumes the result, is touched, so
    /// the stack shape is provably unchanged regardless of the exact surrounding instructions. On a plain
    /// client, or a ZoneSim-off/non-authoritative process, <see cref="EffectiveDistanceSqr"/> calls straight
    /// through to <c>Utils.DistanceSqr</c> -- byte-identical vanilla behaviour, never just "close to vanilla."
    /// </summary>
    internal static class CreateNearFirstOrder
    {
        [HarmonyPatch(typeof(ZNetScene), "CreateObjectsSorted")]
        internal static class Patch_ZNetScene_CreateObjectsSorted_NearFirst
        {
            private static readonly MethodInfo s_distanceSqr = AccessTools.Method(typeof(Utils), nameof(Utils.DistanceSqr));
            private static readonly MethodInfo s_effectiveDistanceSqr =
                AccessTools.Method(typeof(CreateNearFirstOrder), nameof(EffectiveDistanceSqr));

            [HarmonyTranspiler]
            internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                if (s_distanceSqr == null || s_effectiveDistanceSqr == null)
                {
                    NoodlesLog.Error("NoodlesMcDoodles Z-near-first anchor: Utils.DistanceSqr or its own " +
                        "replacement helper did not resolve via reflection -- leaving CreateObjectsSorted's sort " +
                        "key completely unpatched (vanilla's own distance-to-reference-position order).");
                    return instructions;
                }

                try
                {
                    var matcher = new CodeMatcher(instructions)
                        .MatchStartForward(new CodeMatch(OpCodes.Call, s_distanceSqr))
                        .ThrowIfInvalid("NoodlesMcDoodles Z-near-first anchor: call Utils.DistanceSqr(Vector3,Vector3) " +
                            "not found in ZNetScene.CreateObjectsSorted");
                    matcher.SetOperandAndAdvance(s_effectiveDistanceSqr);
                    return matcher.InstructionEnumeration();
                }
                catch (Exception e)
                {
                    NoodlesLog.Error("NoodlesMcDoodles Z-near-first transpiler (CreateObjectsSorted) anchor not " +
                        $"found -- leaving this method completely unpatched (vanilla's own sort key, pre-round-4 " +
                        $"behaviour) rather than risk a partial/unsafe patch: {e}");
                    return instructions; // CodeMatcher copies its input, so this is still the pristine original sequence
                }
            }
        }

        // Cached once per real Unity frame (Time.frameCount), looked up lazily -- only actually computed the
        // first time EffectiveDistanceSqr is called on a given frame, so a frame with no create tick at all
        // (the common case between CreateDestroyHz ticks) never touches ZNet.GetPeers(). CreateObjectsSorted's
        // own create/destroy tick never runs more than once per frame (ZNetScene.Update is a plain Unity
        // Update, called at most once per frame by the engine), so this is exactly "once per tick" per the task
        // brief, never stale within the tick it's used.
        private static int s_cachedFrame = -1;
        private static readonly List<Vector3> s_peerPositions = new List<Vector3>();

        /// <summary>
        /// Same signature and return type as <c>Utils.DistanceSqr(Vector3, Vector3)</c> -- see class remarks for
        /// why that makes the transpiler above a pure operand swap. <paramref name="referencePosition"/> is
        /// vanilla's own <c>ZNet.instance.GetReferencePosition()</c> local, still pushed by CreateObjectsSorted's
        /// unmodified code exactly as before; used as the fallback whenever this process is not an authoritative
        /// ZoneSim server, or has no ready connected peer yet (nothing to be "near" to) -- both cases are
        /// byte-identical to calling vanilla's own <c>Utils.DistanceSqr</c> directly.
        /// </summary>
        private static float EffectiveDistanceSqr(Vector3 referencePosition, Vector3 position)
        {
            if (!ZoneSimConfig.ServerZoneSimFlag.Active || !ZoneSimConfig.IsAuthoritativeProcess())
                return Utils.DistanceSqr(referencePosition, position);

            RefreshPeerPositionsIfNeeded();
            if (s_peerPositions.Count == 0) return Utils.DistanceSqr(referencePosition, position);

            float best = float.MaxValue;
            for (int i = 0; i < s_peerPositions.Count; i++)
            {
                float d = Utils.DistanceSqr(s_peerPositions[i], position);
                if (d < best) best = d;
            }
            return best;
        }

        private static void RefreshPeerPositionsIfNeeded()
        {
            int frame = Time.frameCount;
            if (frame == s_cachedFrame) return;
            s_cachedFrame = frame;
            s_peerPositions.Clear();

            List<ZNetPeer> peers = ZNet.instance?.GetPeers();
            if (peers == null) return;
            for (int i = 0; i < peers.Count; i++)
            {
                ZNetPeer peer = peers[i];
                // Same sentinel PokeRingDriver.cs's own PeerReadyState already uses for "has this peer ever
                // reported a real position" -- kept in sync with that definition rather than re-deriving it.
                if (!PeerReadyState.HasFirstRealPosition(peer)) continue;
                s_peerPositions.Add(peer.GetRefPos());
            }
        }
    }
}
