using System.Collections.Generic;
using System.Linq;

namespace NoodlesMcDoodles.ZoneSim
{
    /// <summary>
    /// Hook 2 (Z3) RED-TEAM FIX — docs/design/04: bounded grace-hold for a fed ZDO that stops being fed.
    ///
    /// <c>ZNetScene.RemoveObjects</c> (<c>SERVER:82242-82273</c>/<c>CLIENT:82472-82503</c>, confirmed identical
    /// both builds this session) earmarks every ZDO present in <c>currentNearObjects</c>/<c>currentDistantObjects</c>
    /// THIS TICK and destroys the local <c>GameObject</c> — and, if <c>!zDO.Persistent &amp;&amp; zDO.IsOwner()</c>
    /// (<c>SERVER:82267-82269</c>), the ZDO itself via a real network broadcast (<c>DestroyZDO</c> ->
    /// <c>ZRoutedRpc.InvokeRoutedRPC(0L, "DestroyZDO", ...)</c>, <c>SERVER:76976-76996</c>) — for every
    /// registered <c>ZNetView</c> NOT earmarked. <c>CreateDestroyObjects</c> runs this sweep unconditionally
    /// every 1/30s (<c>SERVER:82303-82312</c>), not gated by <c>Features.ServerZoneSim</c>. So any ZDO
    /// <see cref="Patch_ZNetScene_CreateObjects_ObjectFeed"/> fed on tick N that is not re-fed on tick N+1 (zone excluded by
    /// <see cref="ZoneCap"/>, peer moved/disconnected, or the kill switch flipped off) falls out of the
    /// earmarked set on the very next tick.
    ///
    /// This branch is unconditionally gated on <c>!zDO.Persistent</c> — it structurally CANNOT reach a
    /// <c>Persistent</c> ZDO, the only category Valheim's own save system writes to disk (docs/design/04's own
    /// zero-data-loss proof for this hook). The grace-hold below only ever bounds ordinary ring-boundary churn
    /// (a peer's ring crossing a zone edge, a transient <see cref="ZoneCap"/> flap, one missed
    /// <see cref="PokeRingDriver"/> tick) for non-Persistent content — it is not, and cannot be, a promise that
    /// a permanently-unfed zone's ephemeral content survives forever, since <c>ReleaseNearbyZDOS</c> is itself
    /// structurally unable to ever reassign a non-Persistent ZDO's ownership away from the server
    /// (<c>if (!tempNearObject.Persistent) { continue; }</c>, <c>SERVER:76957-76960</c>) — waiting longer would
    /// not let ownership move away the way it would for Persistent content.
    /// </summary>
    internal static class GraceHold
    {
        private readonly struct Entry
        {
            public readonly int TicksRemaining;
            public readonly bool IsNear; // which of the two lists this ZDO was last fed into
            public Entry(int ticksRemaining, bool isNear) { TicksRemaining = ticksRemaining; IsNear = isNear; }
        }

        private static readonly Dictionary<ZDO, Entry> s_held = new Dictionary<ZDO, Entry>();

        /// <summary>Called from <see cref="Patch_ZNetScene_CreateObjects_ObjectFeed"/>'s AppendFiltered for every ZDO that is
        /// <c>!Persistent &amp;&amp; IsOwner()</c> — the only category this hook can ever affect.</summary>
        public static void Refresh(ZDO zdo, bool isNear)
        {
            s_held[zdo] = new Entry(ZoneSimConfig.ObjectUnfeedGraceTicks.Value, isNear);
        }

        /// <summary>
        /// Called once per <c>CreateObjects</c> prefix call, after the ordinary feed pass has populated
        /// <paramref name="fedThisTick"/>. Re-appends anything still within its grace window that the ordinary
        /// path did NOT already re-earmark this tick; lets anything whose grace has expired (or whose ZDO is no
        /// longer valid) fall through to vanilla's own <c>RemoveObjects</c> sweep untouched.
        /// </summary>
        public static void TickAndReappend(HashSet<ZDO> fedThisTick, List<ZDO> near, List<ZDO> distant)
        {
            if (s_held.Count == 0) return;

            // Snapshot the keys: safe to mutate s_held (Remove/reassign) while iterating this copy.
            foreach (var zdo in s_held.Keys.ToList())
            {
                if (fedThisTick.Contains(zdo)) { s_held.Remove(zdo); continue; } // ordinary path re-earmarked it — no longer this hook's concern until it drops out again
                var entry = s_held[zdo];

                if (entry.TicksRemaining <= 0 || !zdo.IsValid())
                {
                    s_held.Remove(zdo); // grace expired, or the ZDO is gone — let vanilla's ordinary sweep run;
                    continue;           // never a Persistent ZDO, per this class's own remarks above.
                }
                // A prefab that threw in CreateObject, or a quarantined zone, must not be offered again: each
                // retry rethrows and aborts that tick's whole create/remove pass (InstantiateContainment).
                if (ZoneHealth.IsPoisoned(zdo.GetPrefab()) || ZoneHealth.IsQuarantined(zdo.GetSector()))
                {
                    s_held.Remove(zdo);
                    continue;
                }

                (entry.IsNear ? near : distant).Add(zdo);
                Telemetry_ZoneSim.ObjectsAppendedTotal.Increment();
                s_held[zdo] = new Entry(entry.TicksRemaining - 1, entry.IsNear);
            }
        }

        internal static int HeldCount => s_held.Count;
    }
}
