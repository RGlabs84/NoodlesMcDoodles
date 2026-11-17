using System;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.Transport
{
    /// <summary>
    /// Q6 (docs/design/01 Hook 8) — re-weights <c>ZDOMan.ServerSortSendZDOS</c>'s existing per-object priority
    /// score so an object in a server-simulated zone (per <see cref="ServerZoneMembership.IsServerHeld"/>, Core
    /// infrastructure that is Lead decision 1's always-empty-until-M3 registry) sorts earlier in a peer's send
    /// list, ahead of otherwise-equal-priority objects owned by a player. SERVER:77358-77378 /
    /// CLIENT:77500-77515, byte-identical bodies, confirmed this pass.
    ///
    /// Full prefix-replacement (vanilla's own ~20-line body plus one bias term), not a transpiler — the bias
    /// term needs a zone-membership lookup vanilla's loop has no slot for.
    /// </summary>
    internal static class SendPriorityModule
    {
        internal static FeatureFlag Q6 { get; private set; }
        internal static BepInEx.Configuration.ConfigEntry<float> Q6ServerZoneBias { get; private set; }

        [NoodlesModuleInit]
        internal static void Init()
        {
            var q6ActiveConfig = NoodlesConfig.File.Bind("Features", "Transport.Q6.Active", true,
                new BepInEx.Configuration.ConfigDescription(
                    "Q6: bias ServerSortSendZDOS's priority score toward objects in a server-held zone. " +
                    "Always false-lookup (no effect) until the Ownership module (M3) starts claiming zones — " +
                    "see ServerZoneMembership. Off = vanilla's own distance/staleness sort, unmodified."));
            Q6 = Features.Register("Transport.Q6", q6ActiveConfig);

            Q6ServerZoneBias = NoodlesConfig.File.Bind("Transport", "Q6_ServerZoneBias", 50.0f,
                new BepInEx.Configuration.ConfigDescription(
                    "Additional subtraction applied to m_tempSortValue (lower sorts earlier) for objects in a " +
                    "server-held zone — same units as vanilla's own 'staleness * 1.5f' term.",
                    new BepInEx.Configuration.AcceptableValueRange<float>(0f, 200f)));

            // RUNTIME REVIEW FIX (minor): ServerSortSendZDOS is only ever invoked from CreateSyncList's
            // `IsServer()` branch (SERVER:77259-77307) — but `IsServer()` (runtime authority) is true for a
            // listen-server host too, not just the dedicated-server BUILD. PatchSide.ServerOnly gates on
            // Side.IsDedicatedServerProcess, a load-time BUILD-type heuristic (headless vs. not) — SharedBudget.cs's
            // own EligibleForThisProcess() doc comment explains exactly why that heuristic is the wrong tool for
            // this job. Using it here would silently skip patching Q6 on a listen-server host (a client-build
            // process that IS authoritative), unlike every sibling M1 lever in this directory (Q2, T1/T2/T3),
            // which all use PatchSide.Both plus a runtime `IsServer()` guard in the prefix instead. Matching that
            // established pattern here too.
            PatchRegistry.Register("Transport.Q6", typeof(Patch_ZDOMan_ServerSortSendZDOS_ZoneBias), PatchSide.Both);
        }
    }

    [HarmonyPatch(typeof(ZDOMan), "ServerSortSendZDOS")]
    internal static class Patch_ZDOMan_ServerSortSendZDOS_ZoneBias
    {
        // ZDOMan.ZDOPeer and PeerZDOInfo are private in the game, but this project compiles against a publicized
        // assembly_valheim (NoodlesMcDoodles.csproj, Publicize=true) and Unity's Mono does not enforce member access
        // at runtime (the user's live mods rely on the same). Typed access keeps this per-object loop free of the
        // boxing and FieldInfo.GetValue calls an `object peer` + reflection version needed (runtime review 2026-09-28).
        private static readonly Comparison<ZDO> s_compareDelegate =
            (Comparison<ZDO>)Delegate.CreateDelegate(typeof(Comparison<ZDO>), AccessTools.Method(typeof(ZDOMan), "ServerSendCompare"));

        static bool Prefix(ZDOMan __instance, System.Collections.Generic.List<ZDO> objects, Vector3 refPos, ZDOMan.ZDOPeer peer)
        {
            if (!SendPriorityModule.Q6.Active) return true; // vanilla body runs unmodified, no zone lookup, no bias

            // RUNTIME REVIEW FIX (minor, see PatchSide.Both above): this method is invoked on whichever process
            // is genuinely authoritative for the caller (ZNet.IsServer()==true — dedicated server OR listen-host),
            // never on a real remote client (CreateSyncList's own IsServer() branch never calls it there at all).
            // With PatchSide.Both now offering this class to every process, a real client falling through here
            // would have ZNet.instance.IsServer()==false; fail open to vanilla's untouched body. ZDOMan has no
            // IsServer() of its own (unlike ZNet) — same ZNet.instance-based check ClientSendLoopModule's
            // ShouldApplyOnThisProcess() uses for this exact "authority, not build type" question.
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return true;

            try
            {
                float time = Time.time;
                var peerZdos = peer.m_zdos;

                foreach (ZDO obj in objects)
                {
                    Vector3 pos = obj.GetPosition();
                    obj.m_tempSortValue = Vector3.Distance(pos, refPos);

                    float staleness = 100f;
                    if (peerZdos.TryGetValue(obj.m_uid, out ZDOMan.ZDOPeer.PeerZDOInfo info))
                        staleness = Mathf.Clamp(time - info.m_syncTime, 0f, 100f);
                    obj.m_tempSortValue -= staleness * 1.5f; // vanilla's own weight, unchanged

                    // O(1) HashSet<Vector2s> lookup — always false until the Ownership module (M3) claims a
                    // zone (Lead decision 1). No new per-ZDO allocation of our own: reuses vanilla's own
                    // m_tempSortValue field and ServerZoneMembership's already-allocated set.
                    if (ServerZoneMembership.IsServerHeld(ZoneSystem.GetZone(pos)))
                    {
                        obj.m_tempSortValue -= SendPriorityModule.Q6ServerZoneBias.Value;
                        // Stays at 0 through all of M1 (ServerZoneMembership is always-empty until the
                        // Ownership module claims a zone in M3, Lead decision 1) — a nonzero reading here,
                        // once M3 ships, is the direct proof Q6's bias is actually being applied.
                        Telemetry.GetCounter("transport.q6.serverHeldZdosBiased").Increment();
                    }
                }

                ref long compareReceiver = ref AccessTools.StaticFieldRefAccess<long>(typeof(ZDOMan), "s_compareReceiver");
                compareReceiver = peer.m_peer.m_uid;
                objects.Sort(s_compareDelegate); // ServerSendCompare(ZDO,ZDO) never names ZDOPeer, so this delegate binds fine

                return false;
            }
            catch (Exception e)
            {
                // Fail open to vanilla. Safe even after a partial loop: vanilla's own body (if we return true)
                // recomputes m_tempSortValue for every object in `objects` unconditionally before sorting, so a
                // partially-biased list here is fully overwritten, never left half-sorted.
                NoodlesLog.PatchException(nameof(Patch_ZDOMan_ServerSortSendZDOS_ZoneBias), e);
                return true;
            }
        }
    }
}
