using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace NoodlesMcDoodles.Core
{
    /// <summary>
    /// The ConfigSync channel (doc 02, Hook 3 / P6). Registered UNCONDITIONALLY from a postfix on
    /// <c>ZNet.Awake</c> — only the push (server) and apply (client) logic check
    /// <see cref="Features.ConfigSync"/>, so a live off-&gt;on flip of that flag doesn't need a second world
    /// load to start working (doc 02's own RED-TEAM FIX for this hook).
    ///
    /// M1 payload is deliberately near-empty (schema version + zero key/value pairs) — "No values need to
    /// reach a client in M1 (every M1 lever is server-local); the channel is proven working with a
    /// near-empty payload now so later phases have a tested channel on day one" (DESIGN.md, Architecture).
    /// <see cref="RegisterSyncedValue"/> is the extension point a later module uses to actually put a value
    /// on the wire — see `docs/impl/CORE-API.md` "Pushing a value over ConfigSync".
    ///
    /// The client NEVER writes a received value into its own .cfg (task rule) — it lands in
    /// <see cref="Overlay"/>, a pure runtime dictionary a module's own code chooses to consult instead of its
    /// local <c>ConfigEntry</c> when a value is present there. Nothing in Core auto-applies an overlay value
    /// to anything; each module decides for itself which of its own settings, if any, should defer to a
    /// server override, and reads <see cref="Overlay"/> explicitly to do so.
    /// </summary>
    internal static class ConfigSyncRpc
    {
        private const string RpcName = "NoodlesMcDoodles.v1.ConfigSync";
        private const int SchemaVersion = 1;

        // Server side: key -> "give me the current value as a string" callback. Empty in M1 (see class remarks).
        private static readonly ConcurrentDictionary<string, Func<string>> s_syncedValues =
            new ConcurrentDictionary<string, Func<string>>();

        // Client side: the last-received server values. Read-only to every consumer but this file.
        private static readonly ConcurrentDictionary<string, string> s_overlay =
            new ConcurrentDictionary<string, string>();

        /// <summary>Client-side runtime overlay of server-pushed values. Never persisted, never written back to the local .cfg.</summary>
        public static IReadOnlyDictionary<string, string> Overlay => s_overlay;

        /// <summary>
        /// Server-side: a module registers a config key it wants pushed to negotiated clients. Safe to call
        /// at any time (including before ConfigSync's own Awake registration runs) — this only populates a
        /// dictionary the push logic reads at push time.
        /// </summary>
        public static void RegisterSyncedValue(string key, Func<string> currentValueProvider)
        {
            s_syncedValues[key] = currentValueProvider;
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Awake))]
        internal static class Patch_ZNet_Awake_Register
        {
            static void Postfix()
            {
                try
                {
                    ZRoutedRpc.instance.Register<ZPackage>(RpcName, RPC_ConfigSync);
                }
                catch (ArgumentException e)
                {
                    // ZRoutedRpc.Register does a raw Dictionary.Add on the 32-bit name hash (confirmed
                    // SERVER:83713-83743 — no pre-check, throws on collision) — this is the one hook in this
                    // mod where that's a real risk (routed RPC names share one global hash namespace with
                    // every other installed mod). Disable ONLY ConfigSync, never the whole kill switch.
                    NoodlesLog.Error($"ConfigSync RPC name collided with another mod's routed-RPC hash — " +
                        $"disabling ConfigSync only: {e}");
                    Features.ConfigSync.ForceOff($"RPC name hash collision: {e.Message}");
                    Telemetry.Counters.ConfigSyncRegisterFailed.Increment();
                }
            }
        }

        /// <summary>
        /// Server-side periodic scan, driven from <see cref="NoodlesHeartbeat"/> (never its own timer — see
        /// that class's remarks). Pushes to every peer that (a) completed negotiation and (b) advertised the
        /// ConfigSync capability bit, immediately on first becoming eligible (LastPushRealtime starts at 0,
        /// i.e. "never pushed", so the very next heartbeat tick after negotiation completes pushes — no need
        /// to wait out a full PushIntervalSeconds for the first delivery) and then every
        /// <c>ConfigSync.PushIntervalSeconds</c> after that (covers late capability adoption and reconnects,
        /// per doc 02).
        /// </summary>
        internal static void PushDueUpdates()
        {
            if (!Features.ConfigSync.Active) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            float now = Time.realtimeSinceStartup;
            float interval = NoodlesConfig.ConfigSyncPushIntervalSeconds.Value;

            foreach (var kv in PeerState.Snapshot())
            {
                ZNetPeer peer = kv.Key;
                PeerState state = kv.Value;
                if (!state.Negotiated || !state.HasCapability(Capabilities.ConfigSync)) continue;
                if (state.LastConfigSyncPushRealtime != 0f && (now - state.LastConfigSyncPushRealtime) < interval) continue;

                try
                {
                    var pkg = BuildPayload();
                    ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, RpcName, pkg);
                    state.LastConfigSyncPushRealtime = now;
                    Telemetry.Counters.ConfigSyncPushed.Increment();
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException("ConfigSyncRpc.PushDueUpdates", e);
                }
            }
        }

        private static ZPackage BuildPayload()
        {
            var pkg = new ZPackage();
            pkg.Write(SchemaVersion);
            pkg.Write(s_syncedValues.Count);
            foreach (var kv in s_syncedValues)
            {
                pkg.Write(kv.Key);
                string value;
                try { value = kv.Value() ?? string.Empty; }
                catch (Exception e) { NoodlesLog.PatchException($"ConfigSync value provider '{kv.Key}'", e); value = string.Empty; }
                pkg.Write(value);
            }
            return pkg;
        }

        /// <summary>Routed RPC handler — runs on whichever side received the push (client in the normal case; harmless if a server ever received one, it just overlays values nobody reads).</summary>
        private static void RPC_ConfigSync(long sender, ZPackage pkg)
        {
            if (!Features.ConfigSync.Active) return;
            try
            {
                int schemaVersion = pkg.ReadInt();
                if (schemaVersion != SchemaVersion)
                {
                    NoodlesLog.Warning($"ConfigSync schema version {schemaVersion} != {SchemaVersion}; ignoring payload.");
                    return;
                }
                int count = pkg.ReadInt();
                for (int i = 0; i < count; i++)
                {
                    string key = pkg.ReadString();
                    string value = pkg.ReadString();
                    s_overlay[key] = value; // runtime overlay ONLY — never written to the local .cfg.
                }
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(RPC_ConfigSync), e);
            }
        }

        [NoodlesModuleInit(10)]
        internal static void Init()
        {
            PatchRegistry.Register("ConfigSync", typeof(Patch_ZNet_Awake_Register), PatchSide.Both);
        }
    }
}
