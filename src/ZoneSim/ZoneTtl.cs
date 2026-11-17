using System;
using System.Collections.Generic;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.ZoneSim
{
    /// <summary>
    /// Hook 4 (Z4) — docs/design/04: <c>ZoneSystem.m_zoneTTL</c> config write plus the mandatory backlog-drain
    /// fix for <c>UpdateTTL</c>'s single-destroy-per-tick limit.
    /// </summary>
    internal static class ZoneTtl
    {
        /// <summary>
        /// <c>ZoneSystem.m_zoneTTL</c> — public field, default <c>4f</c> (<c>SERVER:113087</c>/<c>CLIENT:113898</c>,
        /// confirmed same declaration both builds). Written at <c>ZNet.Awake</c> (postfix, a SEPARATE patch from
        /// Core/Rails' own <c>ZNet.Awake</c> postfix — DESIGN.md's own "same-method conflicts" pattern: each
        /// postfix independent, order-irrelevant) and on live config change. Gated on <c>IsServer()</c> — this
        /// project ships one DLL for both processes, and an ungated write would also raise a CLIENT install's
        /// own idle-terrain lifetime (not data loss, just unnecessary RSS/CPU on the player's own machine).
        /// </summary>
        [HarmonyPatch(typeof(ZNet), "Awake")]
        internal static class Patch_ZNet_Awake_ApplyZoneTtl
        {
            [HarmonyPostfix]
            internal static void Postfix()
            {
                try { ApplyZoneTtl(); }
                catch (Exception e) { NoodlesLog.PatchException(nameof(Patch_ZNet_Awake_ApplyZoneTtl), e); }
            }
        }

        internal static void ApplyZoneTtl()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZoneSystem.instance == null) return;

            ZoneSystem.instance.m_zoneTTL = ZoneSimConfig.ServerZoneSimFlag.Active
                ? Mathf.Clamp(ZoneSimConfig.ZoneTTLSeconds.Value, 4f, 20f)
                : ZoneSimConfig.VanillaZoneTTLSeconds;
        }

        /// <summary>
        /// RED-TEAM FIX (RT2 major #4) — full replacement (<c>__runOriginal = false</c>) of
        /// <c>ZoneSystem.UpdateTTL(float dt)</c> (private, <c>SERVER:115548-115562</c>/<c>CLIENT:116359-116373</c>,
        /// confirmed identical both builds). Vanilla's own loop ages every zone's <c>m_ttl</c>, then destroys AT
        /// MOST ONE expired, instance-free zone per call (a <c>break</c> immediately after its one
        /// <c>Destroy</c>+<c>Remove</c> pair — sized for vanilla's single reference position, where that's
        /// never more than one zone anyway). Under this module, N peers each carrying up to a
        /// <c>PokeRingRadius</c> ring can leave hundreds of real zones live; if a large group disperses, the
        /// kill switch flips off, or many rings simply age out together, dozens of zones could cross the TTL
        /// threshold in the same tick but vanilla would only tear down one per <c>~0.05-0.1s</c> call — a
        /// backlog holding CPU/RAM for seconds to tens of seconds.
        ///
        /// This replacement ages every zone exactly as vanilla does (<c>zone.m_ttl += dt</c>), then collects
        /// every expired, instance-free zone key into a temporary buffer FIRST (never mutating the dictionary
        /// while enumerating it — vanilla's own safety depended entirely on stopping immediately after its one
        /// removal; removing the <c>break</c> without collecting first would throw
        /// <c>InvalidOperationException</c> on the next <c>MoveNext()</c>), then destroys/removes every
        /// collected entry in a second pass — draining the whole backlog per tick instead of one entry at a
        /// time. Same TTL comparison, same <c>HaveInstanceInSector</c> guard, same <c>Destroy</c>+<c>Remove</c>
        /// pair as vanilla — this only changes how many eligible zones are drained per call, never what makes a
        /// zone eligible or what "destroy" does to it (a zone's terrain ROOT only; never touches individual
        /// object instances — <see cref="Patch_ZNetScene_CreateObjects_ObjectFeed"/>'s grace-hold is the separate mechanism for those).
        /// </summary>
        [HarmonyPatch(typeof(ZoneSystem), "UpdateTTL")]
        internal static class Patch_ZoneSystem_UpdateTTL_BacklogDrain
        {
            private static readonly List<(Vector2s zone, object data)> s_expiredBuffer = new List<(Vector2s, object)>();

            [HarmonyPrefix]
            internal static bool Prefix(ZoneSystem __instance, float dt)
            {
                // ADDITIONAL FIX found while sweeping for V1's pattern (deployment-modes review, 2026-09-28):
                // ServerZoneSimFlag.Active alone gated this, same hazard class as SnapGuard's own V1 finding.
                // UpdateTTL(0.1f) is called UNCONDITIONALLY from ZoneSystem.Update (SERVER:113806, before the
                // IsServer() split) — a plain client runs it on its own local ZoneSystem every tick. Without the
                // runtime-authority check, a client with the flag on (its own default) would have this backlog
                // drain (`__runOriginal = false`) fully replace vanilla's own single-destroy-per-tick TTL sweep
                // on the CLIENT'S OWN terrain roots, not just the server's.
                if (!ZoneSimConfig.ServerZoneSimFlag.Active || !ZoneSimConfig.IsAuthoritativeProcess()) return true; // vanilla single-destroy-per-tick when off/not authoritative
                if (!ZoneSystemAccess.IsReady) return true; // reflection targets failed to resolve — fail open to vanilla

                try
                {
                    RunBacklogDrain(__instance, dt);
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_ZoneSystem_UpdateTTL_BacklogDrain), e);
                    return true; // fall back to vanilla's own (slower, but correct) single-destroy path this call
                }
                return false; // never run vanilla's own version when this ran successfully
            }

            private static void RunBacklogDrain(ZoneSystem instance, float dt)
            {
                var entries = ZoneSystemAccess.SnapshotZoneEntries(instance);
                foreach (var (zone, data) in entries) ZoneSystemAccess.AddZoneTtl(data, dt);

                s_expiredBuffer.Clear();
                float ttlLimit = instance.m_zoneTTL; // public field — see ApplyZoneTtl above
                foreach (var (zone, data) in entries)
                {
                    if (ZoneSystemAccess.ZoneTtl(data) > ttlLimit && !ZNetScene.instance.HaveInstanceInSector(zone))
                    {
                        s_expiredBuffer.Add((zone, data));
                    }
                }

                foreach (var (zone, data) in s_expiredBuffer)
                {
                    UnityEngine.Object.Destroy(ZoneSystemAccess.ZoneRoot(data));
                    ZoneSystemAccess.RemoveZone(instance, zone);
                }

                Telemetry_ZoneSim.SetZonesTtlDestroyedThisTick(s_expiredBuffer.Count);
            }
        }
    }
}
