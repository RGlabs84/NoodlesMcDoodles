using System;
using System.Net.Sockets;
using HarmonyLib;
using NoodlesMcDoodles.Core;

namespace NoodlesMcDoodles.Transport
{
    /// <summary>
    /// Hook 4 (T5) — raw-TCP send buffer (docs/design/01 Hook 4). <c>ZSocket2.ConfigureSocket</c>
    /// (SERVER:84710-84714 / CLIENT:84940-84944, confirmed this pass: identical body, <c>NoDelay = true;
    /// SendBufferSize = 2048;</c>) runs once per socket at connect/accept time — not a hot loop — so a postfix
    /// overwriting vanilla's own assignment afterwards is simplest and safe: no IL-collision risk with another
    /// mod patching this same narrow, rare method the way a hot-path transpiler would need to worry about.
    /// </summary>
    [HarmonyPatch(typeof(ZSocket2), "ConfigureSocket")]
    internal static class Patch_ZSocket2_ConfigureSocket_BufferSize
    {
        static void Postfix(TcpClient socket)
        {
            try
            {
                if (!TransportLeverConfig.T5.Active) return;

                // RED-TEAM FIX (DATA-LOSS REVIEW, major — missing isAuthoritative/Q10 gate, same shape as
                // Timeouts.cs's RpcTimeoutController.Reassert() and every sibling Transport lever). T5 defaults
                // ON and 64x vanilla's own 2048-byte SendBufferSize; without this gate a plain non-authoritative
                // client's own raw-TCP socket got enlarged unconditionally on install, regardless of authority
                // or Q10 opt-in — exactly the unconditional, non-negotiated local-network-stack change the Q10
                // opt-in gate exists to prevent for every other Hook 1/2 lever.
                bool isAuthoritative = ZNet.instance != null && ZNet.instance.IsServer();
                if (!isAuthoritative && !TransportLeverConfig.Q10Active) return;

                int size = Math.Min(TransportLeverConfig.T5_SendBufferBytes.Value, 262144); // hard clamp regardless of an out-of-range config value
                socket.SendBufferSize = size;
                Telemetry.SetGauge("transport_t5_send_buffer_bytes", size);
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(Patch_ZSocket2_ConfigureSocket_BufferSize), e);
            }
        }
    }
}
