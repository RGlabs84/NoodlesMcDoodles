using System;
using System.Collections.Generic;
using HarmonyLib;
using NoodlesMcDoodles.Core;

namespace NoodlesMcDoodles.Gameplay
{
    /// <summary>
    /// S4 ship hand-back (docs/design/05, Hook H5 tail) — <c>Ship.UpdateOwner()</c>, SERVER:140858-140867,
    /// confirmed genuinely dead server-side:
    /// <code>
    /// private void UpdateOwner()
    /// {
    ///     if (m_nview.IsValid() &amp;&amp; m_nview.IsOwner() &amp;&amp; !(Player.m_localPlayer == null)
    ///         &amp;&amp; m_players.Count > 0 &amp;&amp; !IsPlayerInBoat(Player.m_localPlayer))
    ///     { ... m_nview.GetZDO().SetOwner(newOwnerID); ... }
    /// }
    /// </code>
    /// Same class of fix as S1 (<see cref="SpawnSystemGate"/>): bypass only the
    /// <c>Player.m_localPlayer == null</c> operand via <see cref="NullGuardIL"/>, same anchored-CodeMatcher,
    /// fail-closed discipline. The second, later read of <c>Player.m_localPlayer</c> at this method's
    /// <c>IsPlayerInBoat(Player.m_localPlayer)</c> call is deliberately left untouched — confirmed this pass
    /// that <c>Ship.IsPlayerInBoat(Player player)</c> (SERVER:140948) is just <c>m_players.Contains(player)</c>,
    /// which handles a null argument without throwing (never matches, since <c>m_players</c> only ever holds
    /// real, non-null <c>Player</c> references from <c>OnTriggerEnter</c>) — passing the still-null
    /// <c>Player.m_localPlayer</c> through there is harmless and needs no guard of its own.
    /// <para>
    /// Dormant (documented no-op) until the zone-simulation subsystem's per-peer instantiation hook populates
    /// <c>m_players</c> with real occupants via real <c>OnTriggerEnter</c>/<c>Exit</c> against real
    /// <c>Player</c> colliders server-side.
    /// </para>
    /// <para>
    /// <b>Interactions bullet (docs/design/05, Red Team 2 major #2):</b> two of the user's own other mods hold
    /// skip-capable Harmony prefixes on <c>ZDO.SetOwner</c> (<c>Wubarrk-GetOffMyLawn</c>'s
    /// <c>OwnershipHold/SetOwnerGuard</c>, <c>Wubarrk-Wonderland</c>'s <c>ItemFlow.ZdoSetOwnerPatch</c> —
    /// research/13-mod-compat-census.md §1). This transpiler patches <c>Ship.UpdateOwner</c>, NOT
    /// <c>ZDO.SetOwner</c> itself — there is no same-method Harmony priority to declare against those two
    /// prefixes (priority only orders patches on the SAME method), so the mitigation the blueprint actually
    /// asks for is applied instead: <see cref="Patch_Ship_UpdateOwner_OwnerChangeTelemetry"/> reads
    /// <c>zdo.GetOwner()</c> immediately before and after vanilla's (patched) body runs and counts whether the
    /// value actually changed, rather than assuming a call to <c>SetOwner</c> having happened means it took
    /// effect.
    /// </para>
    /// </summary>
    internal static class ShipHandback
    {
        internal static bool EffectiveLocalPlayerIsNull()
        {
            if (!Features_Gameplay.VehicleContainerHandover.Active) return Player.m_localPlayer == null; // off: byte-identical to vanilla
            return false;
        }

        [HarmonyPatch(typeof(Ship), "UpdateOwner")]
        internal static class Patch_Ship_UpdateOwner_NullBypass
        {
            [HarmonyTranspiler]
            internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, System.Reflection.Emit.ILGenerator generator)
            {
                try
                {
                    return NullGuardIL.ReplaceLocalPlayerNullCheck(
                        instructions, generator,
                        AccessTools.Method(typeof(ShipHandback), nameof(EffectiveLocalPlayerIsNull)));
                }
                catch (Exception e)
                {
                    NoodlesLog.Error($"S4: Ship.UpdateOwner anchor not found, leaving unpatched: {e.Message}");
                    Features_Gameplay.VehicleContainerHandover.ForceOff($"Ship.UpdateOwner transpiler anchor missing: {e.GetType().Name}");
                    return instructions;
                }
            }
        }

        internal readonly struct OwnerSnapshot
        {
            public readonly bool Eligible;
            public readonly ZDOID ZdoUid;
            public readonly long OwnerBefore;
            public OwnerSnapshot(bool eligible, ZDOID zdoUid, long ownerBefore) { Eligible = eligible; ZdoUid = zdoUid; OwnerBefore = ownerBefore; }
        }

        private static readonly AccessTools.FieldRef<Ship, ZNetView> s_shipNview =
            AccessTools.FieldRefAccess<Ship, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<Ship, List<Player>> s_shipPlayers =
            AccessTools.FieldRefAccess<Ship, List<Player>>("m_players");

        /// <summary>
        /// Postcondition telemetry (see class remarks). Deliberately checks only the CHEAP subset of vanilla's
        /// own guard (IsValid/IsOwner/m_players.Count&gt;0) before deciding to snapshot — not the private
        /// <c>IsPlayerInBoat</c> call — so this can occasionally count a call as "eligible" when vanilla's own,
        /// stricter condition would in fact have skipped it too; that only ever costs a slightly noisier
        /// "vetoed" count, never a false "confirmed", and this counter gates nothing (diagnostic only, not a
        /// LossSignal — a ship simply keeping its prior owner is not itself a data-loss event, the same
        /// fallback vanilla's own "no eligible new owner" path already produces).
        /// </summary>
        [HarmonyPatch(typeof(Ship), "UpdateOwner")]
        internal static class Patch_Ship_UpdateOwner_OwnerChangeTelemetry
        {
            [HarmonyPrefix]
            internal static void Prefix(Ship __instance, out OwnerSnapshot __state)
            {
                __state = default;
                if (!Features_Gameplay.VehicleContainerHandover.Active) return;
                try
                {
                    if (__instance == null) return;
                    ZNetView nview = s_shipNview(__instance);
                    if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;
                    List<Player> players = s_shipPlayers(__instance);
                    if (players == null || players.Count == 0) return;
                    ZDO zdo = nview.GetZDO();
                    if (zdo == null) return;
                    __state = new OwnerSnapshot(true, zdo.m_uid, zdo.GetOwner());
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_Ship_UpdateOwner_OwnerChangeTelemetry) + ".Prefix", e);
                }
            }

            [HarmonyPostfix]
            internal static void Postfix(OwnerSnapshot __state)
            {
                if (!__state.Eligible) return;
                try
                {
                    ZDO zdo = ZDOMan.instance?.GetZDO(__state.ZdoUid);
                    if (zdo == null) return;
                    if (zdo.GetOwner() != __state.OwnerBefore)
                        Telemetry.GetCounter("gameplay.s4.shipOwnerChangeConfirmed").Increment();
                    else
                        Telemetry.GetCounter("gameplay.s4.shipOwnerChangeVetoed").Increment();
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_Ship_UpdateOwner_OwnerChangeTelemetry) + ".Postfix", e);
                }
            }
        }
    }
}
