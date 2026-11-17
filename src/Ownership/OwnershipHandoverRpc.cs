using System;
using System.Collections.Generic;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.Ownership
{
    /// <summary>
    /// O8 / H8 (docs/design/03-ownership-and-handover.md) — new RPC <c>RPC_RequestOwnershipHandover</c>: the
    /// sanctioned hand-back path for a server-held ship, the one O6-allowlisted class with no vanilla
    /// request/grant RPC of its own (<c>Container.RPC_RequestOpen</c>, <c>Vagon.RPC_RequestOwn</c> and
    /// <c>Sadle.RPC_RequestControl</c> all confirmed to keep working correctly even when the server is current
    /// owner; <c>Ship</c> confirmed this pass to register only <c>Stop</c>/<c>Forward</c>/<c>Backward</c>/<c>Rudder</c>,
    /// SERVER:140181-141062, and <c>Ship.UpdateOwner</c>'s own hand-back guard (SERVER:140858-140867) can never
    /// fire server-side because <c>Player.m_localPlayer</c> is permanently null on a dedicated server).
    ///
    /// Raw <c>ZRpc.Register</c>/<c>Invoke</c> (matching Negotiation.cs's own Hello/HelloAck, not
    /// <c>ZRoutedRpc</c>) per the census's own recommendation (docs/design/03 "Other mods", RT2 major #3): still
    /// passes through <c>ZRpc.HandlePackage</c> (Wonderland's ModEnforcement gate, currently inert on Proxima
    /// per DESIGN.md Lead decision 3) but skips <c>ZRoutedRpc</c>'s own five-deep guard stack entirely.
    ///
    /// <b>Client-side trigger</b>: a postfix on <c>Ship.UpdateOwner()</c> (the same per-tick method vanilla's own
    /// dead server-side hand-back lived in) — when the LOCAL player is aboard a ship it does not own and that
    /// ship's current owner is the negotiated server, sends this RPC (rate-limited; retried on timeout, counted
    /// as <c>ownership.o8.requestTimedOut</c> per the census's own RED-TEAM FIX for a silently-dropped RPC).
    /// </summary>
    internal static class OwnershipHandoverRpc
    {
        private const string HandoverRpcName = "NoodlesMcDoodles.v1.RequestOwnershipHandover";
        private const float RequestTimeoutSeconds = 5f;

        // RED-TEAM FIX (docs/design/03 RT1 major #5, carried over): a live server-side Ship.m_players read is the
        // PRECISE occupancy check the blueprint specifies, but -- same root constraint as O9's Ship/Vagon
        // exclusion (LatencyAwareOwnership.cs remarks) -- a bare dedicated server has no live Ship GameObject for
        // an idle, server-held ship at all (O8's own motivating case: every crew disconnected, nothing has
        // instantiated it since). ShipHasOccupant therefore verifies via POSITION PROXIMITY between the
        // requesting peer's own reported refPos and the ship ZDO's own position instead -- a live m_players check
        // isn't reachable here to fall back to more precisely without the zone-sim module's own instantiation,
        // out of this module's scope. A false grant's worst case is a player who isn't truly aboard becoming a
        // nearby idle ship's owner -- harmless (no data-loss surface), not the sub-millisecond-hold, two-writer
        // hazard this whole doc otherwise guards against.
        private const float OccupancyProxyRadiusMeters = 20f;

        // --- Server: registration + handler -----------------------------------------------------------------------

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_PeerInfo))]
        internal static class Patch_RPC_PeerInfo_RegisterHandoverRpc
        {
            static void Postfix(ZNet __instance, ZRpc rpc)
            {
                // Registered unconditionally, like ConfigSync (CORE-API.md: "registration is unconditional" so a
                // live off->on flip doesn't need a reconnect) -- the feature-flag check lives inside the handler.
                if (__instance == null || !__instance.IsServer()) return;
                try
                {
                    rpc.Register<ZPackage>(HandoverRpcName, RPC_RequestOwnershipHandover);
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_RPC_PeerInfo_RegisterHandoverRpc), e);
                }
            }
        }

        private static void RPC_RequestOwnershipHandover(ZRpc rpc, ZPackage pkg)
        {
            // Off -> the request is simply ignored, same as a vanilla server that doesn't know this RPC name.
            if (!Features_Ownership.OwnershipAuthority.Active) return;
            // LEAD FIX (CORE-API.md "Live-authority toggles"): O8's grant moves real ownership just like O6's
            // claims do -- gate it on the same armed state (see SimulationDistanceCeiling.OwnershipAuthorityArmed).
            if (!SimulationDistanceCeiling.OwnershipAuthorityArmed) return;

            try
            {
                ZDOID targetId = pkg.ReadZDOID();

                ZNetPeer peer = ZNetHelpers.GetPeer(ZNet.instance, rpc);
                if (peer == null) return; // disconnected mid-flight -- nobody to log a refusal against meaningfully

                // MINOR FIX (defense in depth, mixed-and-host runtime review, 2026-09-28): key the grant to the
                // REQUESTER's own negotiated state, matching ConfigSync's per-peer pattern (ConfigSyncRpc.cs:97)
                // and this feature's own client-side send gate below (this file, TryRequestHandover) -- this
                // handler previously trusted only the global armed/active flags plus the implicit assumption
                // that only a negotiated NoodlesMcDoodles client could ever construct this exact RPC name/payload.
                if (!PeerState.TryGet(peer, out var state) || !state.Negotiated || !state.HasCapability(Capabilities.OwnershipHandover))
                {
                    return;
                }

                ZDO zdo = ZDOMan.instance.GetZDO(targetId);
                if (zdo == null || !zdo.IsOwner()) return; // server must currently own it -- stale/no-op, not a "refusal" worth logging

                // MAJOR FIX (lead review): the original version trusted the client's ZDOID with only an
                // IsOwner()-plus-20m-proximity check -- ANY server-owned object type within range could be
                // granted away, not just ships. This RPC exists ONLY for ships (see class remarks: Container/
                // Vagon/Sadle all have their own vanilla request/grant RPCs already); require the target's own
                // prefab to actually carry a Ship component before granting anything.
                if (!IsShipPrefab(zdo))
                {
                    Telemetry.GetCounter("ownership.o8.refusedNotAShip").Increment();
                    NoodlesLog.Warning($"O8: refused a handover request for {targetId} from peer uid={peer.m_uid} " +
                        "-- its prefab is not a Ship (the only class this RPC exists for) -- an unexpected or " +
                        "hostile ZDOID.");
                    return;
                }

                if (!ShipHasOccupant(zdo, peer))
                {
                    Telemetry.GetCounter("ownership.o8.refusedNotOccupant").Increment();
                    NoodlesLog.Verbose($"O8: refused a handover request for ship {targetId} from peer uid={peer.m_uid} " +
                        "-- not within the occupancy proximity check.");
                    return;
                }

                // H8's own defensive step: route through O7 if somehow already held (e.g. a concurrent O6/O9
                // touch this exact tick) before granting.
                if (SetOwnerHoldGuard.IsHeld(targetId)) SetOwnerHoldGuard.Release(targetId);

                OwnershipApi.SetOwner(zdo, peer.m_uid, "O8 ownership handover grant");
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(RPC_RequestOwnershipHandover), e);
            }
        }

        /// <summary>MAJOR FIX (lead review): the mandatory class check a grant must pass before anything else — see the call site's own remarks.</summary>
        private static bool IsShipPrefab(ZDO zdo)
        {
            if (ZNetScene.instance == null) return false; // can't confirm -> refuse (fail closed)
            GameObject prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
            return prefab != null && prefab.GetComponent<Ship>() != null;
        }

        private static bool ShipHasOccupant(ZDO shipZdo, ZNetPeer requester)
        {
            float distance = Vector3.Distance(shipZdo.GetPosition(), requester.GetRefPos());
            return distance <= OccupancyProxyRadiusMeters;
        }

        // --- Client: trigger + timeout tracking ----------------------------------------------------------------------

        // ZDOID -> realtime the last request for it was sent. Cleared on success (IsOwner() becomes true) or
        // superseded by a fresh send after RequestTimeoutSeconds.
        private static readonly Dictionary<ZDOID, float> s_pendingRequests = new Dictionary<ZDOID, float>();

        [HarmonyPatch(typeof(Ship), "UpdateOwner")]
        internal static class Patch_Ship_UpdateOwner_ClientRequestTrigger
        {
            static void Postfix(Ship __instance)
            {
                if (!Features_Ownership.OwnershipAuthority.Active) return;
                if (ZNet.instance == null || ZNet.instance.IsServer()) return; // client-only trigger; vanilla's own guard already can't fire server-side (see class remarks)
                try
                {
                    TryRequestHandover(__instance);
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_Ship_UpdateOwner_ClientRequestTrigger), e);
                }
            }
        }

        private static void TryRequestHandover(Ship ship)
        {
            if (Ship.GetLocalShip() != ship) return; // the local player isn't aboard this particular ship

            ZNetView znv = ship.GetComponent<ZNetView>();
            if (znv == null || !znv.IsValid()) return;
            ZDO zdo = znv.GetZDO();
            ZDOID id = zdo.m_uid;

            if (znv.IsOwner())
            {
                s_pendingRequests.Remove(id); // already ours -- success, or always was
                return;
            }

            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            if (peers == null || peers.Count == 0) return;
            ZNetPeer serverPeer = peers[0]; // a client has exactly one peer: its connection to the server

            if (zdo.GetOwner() != serverPeer.m_uid) return; // owned by some other PLAYER, not the server -- O8 doesn't apply; vanilla's own Ship RPCs handle player<->player

            if (!PeerState.TryGet(serverPeer, out var state) || !state.Negotiated ||
                !state.HasCapability(Capabilities.OwnershipHandover))
            {
                return; // Lead decision 3: never send before a negotiated Hello confirms the server understands this
            }

            float now = Time.realtimeSinceStartup;
            if (s_pendingRequests.TryGetValue(id, out float sentAt))
            {
                if (now - sentAt < RequestTimeoutSeconds) return; // still waiting on a previous request
                Telemetry.GetCounter("ownership.o8.requestTimedOut").Increment(); // silently dropped -- see class remarks on the RPC pipeline collision surface
            }

            var pkg = new ZPackage();
            pkg.Write(id);
            serverPeer.m_rpc.Invoke(HandoverRpcName, pkg);
            s_pendingRequests[id] = now;
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect), new[] { typeof(ZNetPeer) })]
        internal static class Patch_ZNet_Disconnect_ClearPending
        {
            static void Postfix()
            {
                // A disconnect (either direction) invalidates every in-flight request this client tracked --
                // cheap and safe to just clear all of it rather than try to attribute per-peer.
                s_pendingRequests.Clear();
            }
        }

        // --- Init --------------------------------------------------------------------------------------------------

        [NoodlesModuleInit]
        internal static void Init()
        {
            Capabilities.Advertise(Capabilities.OwnershipHandover);

            PatchRegistry.Register("OwnershipAuthority", typeof(Patch_RPC_PeerInfo_RegisterHandoverRpc), PatchSide.Both);
            PatchRegistry.Register("OwnershipAuthority", typeof(Patch_Ship_UpdateOwner_ClientRequestTrigger), PatchSide.Both);
            PatchRegistry.Register("OwnershipAuthority", typeof(Patch_ZNet_Disconnect_ClearPending), PatchSide.Both);
        }
    }
}
