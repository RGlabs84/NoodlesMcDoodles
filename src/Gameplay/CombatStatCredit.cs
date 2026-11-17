using System;
using HarmonyLib;
using NoodlesMcDoodles.Core;

namespace NoodlesMcDoodles.Gameplay
{
    /// <summary>
    /// S3 (docs/design/05, Hook H2) — <c>Character.RPC_Damage(long, HitData)</c>, SERVER:2413/CLIENT:2414.
    /// Confirmed this pass, exact ordering: the stat-credit check runs BEFORE the owner gate —
    /// <code>
    /// if (hit.GetAttacker() == Player.m_localPlayer) { Game.instance.IncrementPlayerStat(...); ... }
    /// if (!m_nview.IsOwner()) return;
    /// </code>
    /// <c>Player.m_localPlayer</c> is permanently null on a dedicated server, so this comparison can never be
    /// true there — once a target is server-owned, PlayerHits/EnemyHits stat credit silently stops for every
    /// hit against it. No crash, no data loss (Data-loss analysis: "cosmetic only, not a data-loss path") —
    /// purely additive, so a postfix that derives the same credit from data the method already has, instead of
    /// the client-only singleton, is the smallest correct fix.
    /// <para>
    /// <b>Deviation from the blueprint's sketch (documented, not silent):</b> the blueprint's own H2 code
    /// routes credit to "the attacking peer's own client-side stat tracker via a namespaced RPC" but explicitly
    /// leaves that RPC's wire shape as an open question ("deferred to whichever subsystem owns the shared
    /// negotiated-RPC channel... since this module shouldn't invent a second one" — docs/design/05 Open
    /// questions). This module owns no negotiated-RPC channel of its own (Core/Negotiation.cs and
    /// Core/Capabilities.cs are Core's), and the server has no per-player <c>PlayerProfile</c> to write to
    /// directly (a client-local file). Rather than invent an unreviewed wire protocol for a lever the blueprint
    /// itself calls "cosmetic, not data-loss", this ships as detection + telemetry only: it proves, per hit,
    /// that the credit WOULD now be attributable and to which peer, without actually restoring the client-side
    /// stat write. Genuinely useful on its own (the Telemetry section's own stated purpose — "proves S3's
    /// companion patch is restoring stat credit... for server-owned-target hits" — holds for the detection
    /// half), and leaves the RPC decision to whichever future pass wires the client-visible half, instead of
    /// guessing at a payload shape no one has reviewed.
    /// </para>
    /// </summary>
    internal static class CombatStatCredit
    {
        [HarmonyPatch(typeof(Character), "RPC_Damage")]
        internal static class Patch_Character_RPC_Damage_StatCredit
        {
            [HarmonyPostfix]
            internal static void Postfix(HitData hit)
            {
                if (!Features_Gameplay.ServerCombatStats.Active) return;
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                try
                {
                    if (hit == null || hit.m_attacker == ZDOID.None) return;
                    // Resolve via the ZDO layer, not Character.GetAttacker()'s component lookup: the attacking
                    // Character's GameObject need not be locally instantiated on the server for its ZDO (and
                    // therefore its current owner) to be known. hit.m_attacker is `public ZDOID`, confirmed
                    // SERVER:130503.
                    ZDO attackerZdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(hit.m_attacker) : null;
                    long owner = attackerZdo?.GetOwner() ?? 0L;
                    if (owner == 0L) return;

                    // ZNet.GetPeer(long uid) — public, SERVER:80538 — resolves the ZNetPeer object PeerState's
                    // per-peer counters are keyed by; null (peer not connected, e.g. the server itself owns it,
                    // or the attacker already disconnected) is a legitimate "nothing to credit", not an error.
                    ZNetPeer ownerPeer = ZNet.instance.GetPeer(owner);
                    if (ownerPeer == null) return;

                    Telemetry.GetPerPeerCounter("gameplay.s3.creditsApplied").Increment(ownerPeer);
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_Character_RPC_Damage_StatCredit), e);
                }
            }
        }
    }
}
