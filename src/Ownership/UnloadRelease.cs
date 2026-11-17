using System;
using System.Collections.Generic;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.Ownership
{
    /// <summary>
    /// 1.0.10 (user: close the unload gap). The server gives up ownership of a persistent object at the moment it
    /// unloads it, not up to a claim cycle later.
    ///
    /// When a held zone leaves every peer's ring, ObjectFeed stops feeding it, and vanilla
    /// <c>ZNetScene.RemoveObjects</c> (Decompiled_1.0.16 ZNetScene.cs: a byte earmark from <c>Time.frameCount</c> on
    /// every ZDO in this tick's two lists, then every instance whose ZDO lacks the earmark is destroyed) drops the
    /// instances of everything no longer fed. A persistent ZDO survives that, still server-owned, with nothing on
    /// the server simulating it until ClaimScanner gets round to releasing the zone: frozen from every client's
    /// point of view, and any RPC routed to it finds no instance.
    ///
    /// This prefix repeats vanilla's earmark first (the same byte on the same two lists, so vanilla's own pass
    /// is unchanged) and hands back each persistent, server-owned object about to lose its instance. With the
    /// round-4 <c>!zdo.Created</c> claim refusal this keeps one rule in both directions: the server never owns an
    /// object it has not loaded.
    /// <list type="bullet">
    /// <item>Non-persistent objects are left alone: vanilla destroys the ones it owns right after, and GraceHold
    /// already governs those.</item>
    /// <item>A released object is unowned, so the next player to come near takes it, exactly as vanilla would.
    /// ClaimScanner then finds nothing server-owned left in the zone and drops its membership.</item>
    /// <item>A release a third-party SetOwner prefix refuses is left as it was and counted here
    /// (<c>ownership.unloadReleaseRefused</c>) and as <c>ownership.o11.releaseRefusedByHolder</c>; since 1.0.12 it is
    /// no longer an <c>ownership.setOwnerVetoed</c> (ModeQuarantine) input.</item>
    /// </list>
    /// </summary>
    [HarmonyPatch(typeof(ZNetScene), "RemoveObjects")]
    internal static class Patch_ZNetScene_RemoveObjects_UnloadRelease
    {
        private static readonly AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>> s_instances =
            AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO, ZNetView>>("m_instances");

        private static readonly Counter s_released = Telemetry.GetCounter("ownership.unloadReleased");
        private static readonly Counter s_refused = Telemetry.GetCounter("ownership.unloadReleaseRefused");

        // Collected first, released after the scan: never change ownership while enumerating m_instances.
        private static readonly List<ZDO> s_toRelease = new List<ZDO>();

        [HarmonyPriority(Priority.Last)] // after any other prefix that adjusts this tick's lists
        static void Prefix(ZNetScene __instance, List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects)
        {
            if (!Side.IsDedicatedServerProcess || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!Features_Ownership.OwnershipAuthority.Active && !O10SharedAreaOwnership.Active) return;

            try
            {
                byte earmark = (byte)((uint)Time.frameCount & 0xFFu);
                foreach (ZDO zdo in currentNearObjects) zdo.TempRemoveEarmark = earmark;
                foreach (ZDO zdo in currentDistantObjects) zdo.TempRemoveEarmark = earmark;

                long sessionId = ZDOMan.GetSessionID();
                s_toRelease.Clear();
                foreach (ZNetView view in s_instances(__instance).Values)
                {
                    ZDO zdo = view != null ? view.GetZDO() : null;
                    if (zdo == null || zdo.TempRemoveEarmark == earmark) continue; // stays loaded this tick
                    if (!zdo.Persistent || zdo.GetOwner() != sessionId) continue;
                    s_toRelease.Add(zdo);
                }

                foreach (ZDO zdo in s_toRelease)
                {
                    if (OwnershipApi.SetOwner(zdo, 0, "Ownership.UnloadRelease: server unloading an object it owns"))
                        s_released.Increment();
                    else
                        s_refused.Increment();
                }
                s_toRelease.Clear();
            }
            catch (Exception e)
            {
                s_toRelease.Clear();
                NoodlesLog.PatchException(nameof(Patch_ZNetScene_RemoveObjects_UnloadRelease), e);
            }
        }

        [NoodlesModuleInit]
        internal static void Init()
        {
            PatchRegistry.Register("OwnershipAuthority", typeof(Patch_ZNetScene_RemoveObjects_UnloadRelease), PatchSide.ServerOnly);
        }
    }
}
