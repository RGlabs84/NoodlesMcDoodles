using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.Ownership
{
    /// <summary>
    /// O4 / G4-N1 (docs/design/03-ownership-and-handover.md, Hook H6) — tightens the CLIENT's own reference-
    /// position report cadence, the input G3-N1's padding and O6's peer-overlap exclusion are sized against.
    /// Client-local: activates only once this client has negotiated with a server this mod recognizes, falling
    /// back to vanilla's 2.0s against any server that doesn't answer (a vanilla server, or simply one where this
    /// client's own <c>OwnershipAuthority.Active</c> is off) — the mandatory guard H6 itself specifies, so a
    /// player who keeps this client mod installed never imposes extra load on an unrelated server.
    ///
    /// RED-TEAM FIX (docs/design/03 RT2 minor, confirmed this pass): <c>ZNet.SendPeriodicData</c>'s
    /// <c>m_periodicSendTimer >= 2f</c> gate (SERVER:80383-80388) is evaluated BEFORE the <c>IsServer()</c> split
    /// (SERVER:80389) — it is shared by the SERVER's own <c>SendNetTime</c>/<c>SendPlayerList</c>/
    /// <c>SendHistoricalPlayerList</c> broadcast branch and the CLIENT's <c>SendServerSyncPlayerData</c> reporting
    /// branch, not confined to "the reporting branch" the surrounding prose implies. <see cref="ActiveIntervalOrVanilla"/>
    /// therefore checks <c>IsServer()</c> itself and returns the untouched vanilla literal there, every time —
    /// never a bare process-global static — so this DLL loading on the server too can never alter the server's
    /// own broadcast cadence.
    /// </summary>
    internal static class ClientRefPosCadence
    {
        internal const float VanillaInterval = 2f; // SERVER:80383, confirmed this pass.

        private static ConfigEntry<float> s_interval;

        /// <summary>Read by the transpiled call site on every tick — live, never cached, per the shared-infra contract.</summary>
        internal static float ActiveIntervalOrVanilla()
        {
            if (ZNet.instance == null || ZNet.instance.IsServer()) return VanillaInterval;
            if (!Features_Ownership.OwnershipAuthority.Active) return VanillaInterval;

            // A client has exactly one ZNetPeer: its connection to the server (Valheim's star topology — other
            // players are ZDOs this client receives, never separate peers here).
            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            if (peers == null || peers.Count == 0) return VanillaInterval;

            if (!PeerState.TryGet(peers[0], out var state) || !state.Negotiated) return VanillaInterval; // H6's mandatory guard

            return Mathf.Clamp(s_interval.Value, 0.2f, 1.0f);
        }

        [HarmonyPatch(typeof(ZNet), "SendPeriodicData")]
        internal static class Patch_ZNet_SendPeriodicData_Cadence
        {
            private static readonly MethodInfo s_getter = AccessTools.Method(typeof(ClientRefPosCadence), nameof(ActiveIntervalOrVanilla));

            [HarmonyTranspiler]
            internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            {
                var m = new CodeMatcher(instructions, generator);
                m.MatchStartForward(new CodeMatch(OpCodes.Ldc_R4, VanillaInterval));
                if (!m.IsValid)
                {
                    NoodlesLog.Error("O4: SendPeriodicData's 2f cadence literal not found -- leaving it completely " +
                        "unpatched.");
                    Features_Ownership.OwnershipAuthority?.ForceOff("O4 transpiler anchor missing");
                    return instructions;
                }

                var old = m.Instruction;
                var replacement = new CodeInstruction(OpCodes.Call, s_getter);
                if (old.labels != null && old.labels.Count > 0) replacement.labels.AddRange(old.labels);
                if (old.blocks != null && old.blocks.Count > 0) replacement.blocks.AddRange(old.blocks);
                m.SetInstruction(replacement);

                return m.InstructionEnumeration();
            }
        }

        [NoodlesModuleInit]
        internal static void Init()
        {
            s_interval = NoodlesConfig.File.Bind("ClientRefPos", "Interval", 0.5f,
                new ConfigDescription("O4/G4-N1 -- how often this client reports its own reference position, " +
                    "once negotiated with a NoodlesMcDoodles server (else vanilla's 2.0s, always). Vanilla: fixed " +
                    "2.0s. Default 0.5s keeps the server's own OwnershipAuthority.ReleaseZdosInterval default " +
                    "(1.0s) never more than half a report-interval stale -- the smallest interval that stays " +
                    "ahead of the server's own sweep cadence, not an arbitrarily fast poll; the payload itself is " +
                    "a few bytes, so even this 4x-vanilla rate costs nothing that matters on a residential " +
                    "uplink. 0.2s remains available as opt-in headroom if OwnershipAuthority.ReleaseZdosInterval " +
                    "is later tuned faster than 1.0s.",
                    new AcceptableValueRange<float>(0.2f, 1.0f)));

            // PatchSide.Both, not ClientOnly: the literal being replaced sits in a gate SHARED with the server's
            // own broadcast branch (see class remarks) -- the method must be patched on both sides for the
            // client-side tightening to exist at all, with ActiveIntervalOrVanilla itself responsible for never
            // altering the server's own cadence.
            PatchRegistry.Register("OwnershipAuthority", typeof(Patch_ZNet_SendPeriodicData_Cadence), PatchSide.Both);
        }
    }
}
