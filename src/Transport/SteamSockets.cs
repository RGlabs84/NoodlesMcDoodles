using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace NoodlesMcDoodles.Transport
{
    /// <summary>
    /// Finds the real <see cref="ZSteamSocket"/> under a peer's socket (1.0.3). ServerSync, embedded in dozens of
    /// mods on a typical modded server (about 30 on Proxima), wraps a joining peer's socket in its own
    /// BufferingSocket during the server's RPC_PeerInfo; when each copy's config push finishes it sets BOTH
    /// <c>ZRpc.m_socket</c> and <c>ZNetPeer.m_socket</c> to "whatever that wrapper wrapped", which with nested
    /// copies is often another copy's wrapper, and a copy that never finishes leaves one in place for the whole
    /// session. Every such wrapper forwards to the socket it holds in a field named <c>Original</c>, so following
    /// that chain finds the Steam connection underneath. Before this, every <c>peer.m_socket is ZSteamSocket</c>
    /// check skipped a wrapped peer: live on Proxima 2026-09-29, T9 evaluated a joining player once (a join-burst
    /// stall), never again, and left the connection pinned at vanilla's rate and 10 KB queue.
    /// </summary>
    internal static class SteamSockets
    {
        private const int MaxLayers = 64;

        private sealed class Resolved
        {
            public ZSteamSocket Steam;
            public int Layers;
        }

        // A wrapper's Original is readonly, so the chain under a given outer socket never changes: resolve once.
        private static readonly ConditionalWeakTable<ISocket, Resolved> s_resolved = new ConditionalWeakTable<ISocket, Resolved>();
        private static readonly Dictionary<Type, FieldInfo> s_originalField = new Dictionary<Type, FieldInfo>();

        /// <summary>
        /// True only for a real PlayFab (crossplay) connection. ServerSync's BufferingSocket DERIVES from
        /// ZPlayFabSocket, so a plain <c>is ZPlayFabSocket</c> test also matches a Steam connection it wraps: live
        /// on Proxima that made compression treat every modded Steam player as crossplay and never compress to
        /// them. A wrapper around a real PlayFab socket still counts as PlayFab.
        /// </summary>
        internal static bool IsPlayFab(ISocket socket) => socket is ZPlayFabSocket && !TryGet(socket, out _);

        /// <summary>True with the Steam socket when <paramref name="socket"/> is one, or wraps one.</summary>
        internal static bool TryGet(ISocket socket, out ZSteamSocket steam) => TryGet(socket, out steam, out _);

        /// <summary>As above; <paramref name="layers"/> is how many wrappers sit on top (0 = not wrapped).</summary>
        internal static bool TryGet(ISocket socket, out ZSteamSocket steam, out int layers)
        {
            steam = socket as ZSteamSocket;
            layers = 0;
            if (steam != null) return true;
            if (socket == null) return false;

            Resolved r = s_resolved.GetValue(socket, Resolve);
            steam = r.Steam;
            layers = r.Layers;
            return steam != null;
        }

        private static Resolved Resolve(ISocket outer)
        {
            var r = new Resolved();
            ISocket current = outer;
            try
            {
                while (current != null && r.Layers < MaxLayers)
                {
                    if (current is ZSteamSocket s)
                    {
                        r.Steam = s;
                        return r;
                    }
                    FieldInfo f = OriginalField(current.GetType());
                    if (f == null) break; // not a forwarding wrapper we understand (PlayFab, a mod's own socket, ...)
                    current = f.GetValue(current) as ISocket;
                    r.Layers++;
                }
            }
            catch (Exception)
            {
                // Reflection over another mod's private type: treat anything odd as "no Steam socket", which is
                // exactly the pre-1.0.3 behaviour (the peer is simply left at vanilla).
            }
            r.Steam = null;
            return r;
        }

        private static FieldInfo OriginalField(Type t)
        {
            if (s_originalField.TryGetValue(t, out FieldInfo f)) return f;
            f = t.GetField("Original", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null && !typeof(ISocket).IsAssignableFrom(f.FieldType)) f = null;
            s_originalField[t] = f;
            return f;
        }
    }
}
