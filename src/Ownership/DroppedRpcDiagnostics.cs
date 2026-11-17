using System;
using System.Collections.Generic;
using HarmonyLib;
using NoodlesMcDoodles.Core;

namespace NoodlesMcDoodles.Ownership
{
    /// <summary>
    /// 1.0.11 diagnostic (live Proxima 2026-10-01): during INIGO's 17-minute session vanilla logged
    /// "Failed to find rpc method 1130726949" nine times (RPC_Damage, five of them within four seconds) and once
    /// for 1663900104 (RPC_OnHit): a player's hit reached the server's copy of an object the server owned, and
    /// that copy had never registered the handler (Destructible/MineRock5/WearNTear/Character register
    /// RPC_Damage only if their ZDO exists in Awake), so the hit was dropped. Vanilla's warning names only the
    /// hash. This prefix on <c>ZNetView.HandleRoutedRPC</c> names the object, its owner and the sender, so the
    /// next occurrence says which prefab is affected. One dictionary lookup per routed RPC on the normal path;
    /// observational only (void, never skips the original); server only.
    /// </summary>
    [HarmonyPatch(typeof(ZNetView), nameof(ZNetView.HandleRoutedRPC))]
    internal static class Patch_ZNetView_HandleRoutedRPC_DroppedRpcDiagnostics
    {
        private static readonly Counter s_dropped = Telemetry.GetCounter("ownership.droppedRoutedRpc");
        private static readonly Dictionary<int, string> s_knownNames = new Dictionary<int, string>();

        private static readonly string[] s_names =
        {
            "RPC_Damage", "RPC_OnHit", "RPC_Remove", "RPC_Repair", "RPC_HealthChanged", "RPC_SetTag",
            "RPC_Pick", "UseDoor", "RPC_UseDoor", "RequestOpen", "RPC_RequestOpen", "RPC_SetConnected",
            "RPC_SetAreaHealth", "RPC_CreateFragments", "RPC_Grow", "RPC_Shake", "RPC_AddNoise", "RPC_Stagger",
            "RPC_Heal", "RPC_SetTamed", "RPC_ClearCachedSupport", "RequestOwn", "RPC_RequestOwn",
        };

        private static string MethodName(int hash)
        {
            if (s_knownNames.Count == 0)
                foreach (string n in s_names) s_knownNames[n.GetStableHashCode()] = n;
            return s_knownNames.TryGetValue(hash, out string name) ? name : hash.ToString();
        }

        static void Prefix(ZNetView __instance, ZRoutedRpc.RoutedRPCData rpcData)
        {
            try
            {
                if (!KillSwitch.Enabled) return; // review: the kill switch turns this off too
                if (__instance == null || rpcData == null) return;
                if (__instance.m_functions.ContainsKey(rpcData.m_methodHash)) return; // the normal case
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

                s_dropped.Increment();
                ZDO zdo = __instance.GetZDO();
                string prefab = Utils.GetPrefabName(__instance.gameObject);
                string method = MethodName(rpcData.m_methodHash);
                NoodlesLog.WarningRateLimited(NoodlesLogLevel.Normal, "droppedRpc." + prefab + "." + rpcData.m_methodHash,
                    $"dropped routed RPC {method} for '{prefab}' (zdo {zdo?.m_uid}, owner {zdo?.GetOwner()}, " +
                    $"server-owned {(zdo != null && zdo.IsOwner())}) from peer {rpcData.m_senderPeerID} to " +
                    $"{rpcData.m_targetPeerID}: this server copy never registered it, so the server ignores it " +
                    "(a call aimed at everybody may still be handled by players).", 3, 300f);
            }
            catch (Exception e)
            {
                // Never NoodlesLog.PatchException here: that feeds the global stand-down trigger, and this is
                // a diagnostic.
                NoodlesLog.WarningRateLimited("droppedRpc.diagError",
                    $"dropped-RPC diagnostic threw: {e.GetType().Name}: {e.Message}", 3, 300f);
            }
        }

        [NoodlesModuleInit]
        internal static void Init()
        {
            PatchRegistry.Register("Diagnostics.DroppedRoutedRpc", typeof(Patch_ZNetView_HandleRoutedRPC_DroppedRpcDiagnostics), PatchSide.ServerOnly);
        }
    }
}
