using System.Reflection;
using HarmonyLib;

namespace NoodlesMcDoodles.Core
{
    /// <summary>
    /// Small cached-reflection helpers for private vanilla members this mod's patches need but that have no
    /// public accessor. Resolved ONCE, at plugin Awake (fail loud at startup if resolution fails, never
    /// silently per-call at runtime) — per doc 02 Hook 1's own RED-TEAM FIX: the original draft resolved
    /// <c>ZNet.GetPeer(ZRpc)</c> via an uncached <c>AccessTools.Method(...).Invoke(...)</c> on every single
    /// call to the busiest hook in the doc, with no null-check and no try/catch, which would throw uncaught
    /// inside <c>ZRpc.HandlePackage</c>'s dispatch (SERVER:83952-83970 has no try/catch of its own around
    /// <c>value2.Invoke(this, package)</c>) if a future build ever renamed the method.
    /// </summary>
    internal static class ZNetHelpers
    {
        // private ZNetPeer GetPeer(ZRpc rpc) — SERVER:80550 / CLIENT:80743, confirmed private + identical
        // signature on both builds this pass. Resolved against the PUBLICIZED reference assembly's member
        // list (this project's build convention), so AccessTools finds it even though it's private at runtime.
        private static readonly MethodInfo s_getPeerMethod =
            AccessTools.Method(typeof(ZNet), "GetPeer", new[] { typeof(ZRpc) });

        /// <summary>True once <see cref="GetPeer"/> is safe to call — checked once at Awake, not per call.</summary>
        public static bool IsReady => s_getPeerMethod != null;

        /// <summary>
        /// Call once from plugin Awake, right after <see cref="Features.InitCoreFlags"/>. Every feature that
        /// depends on <see cref="GetPeer"/> (the handshake re-entry guard and the whole negotiation channel —
        /// both patch <c>ZNet.RPC_PeerInfo</c> and need the peer object) is force-disabled up front, loudly,
        /// if this reflection target ever fails to resolve — matching the doc's own "fail loud at startup, not
        /// silently per-call at runtime" guard, rather than leaving every later call to silently no-op.
        /// </summary>
        public static void ValidateOrDisableDependents()
        {
            if (IsReady) return;
            NoodlesLog.Error("ZNet.GetPeer(ZRpc) did not resolve via reflection — this build's ZNet layout no " +
                "longer matches what this mod expects. Disabling every feature that depends on it (handshake " +
                "re-entry guard, negotiation) up front rather than risk a null-reference at runtime.");
            Features.HandshakeReentryGuard.ForceOff("ZNet.GetPeer(ZRpc) failed to resolve at Awake");
            Features.Negotiation.ForceOff("ZNet.GetPeer(ZRpc) failed to resolve at Awake");
        }

        /// <summary>
        /// Wraps <c>ZNet.GetPeer(ZRpc)</c>. Returns null (never throws) if the reflection target failed to
        /// resolve at Awake — callers must already treat a null peer as "nothing to do, fall through to
        /// vanilla", which every caller of this method already does for the legitimate "peer not found yet"
        /// case, so this reuses that same path rather than needing a second null-check convention.
        /// </summary>
        public static ZNetPeer GetPeer(ZNet znet, ZRpc rpc)
        {
            if (s_getPeerMethod == null) return null;
            return (ZNetPeer)s_getPeerMethod.Invoke(znet, new object[] { rpc });
        }
    }
}
