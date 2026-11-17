using System;
using System.Collections.Generic;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.Gameplay
{
    /// <summary>
    /// H5/H6 (docs/design/05) mandatory backstop for <see cref="HandoverGuard"/>. A remote (possibly vanilla,
    /// possibly this mod's own) owner's local "am I busy" state — <c>Container.IsInUse()</c>, an AI's own tick,
    /// a Smelter's accumulator — is never visible to any OTHER machine deciding whether to reclaim the object;
    /// those fields are only ever updated by the CURRENT owner (every one of them gated
    /// <c>if (m_nview.IsOwner()) {...}</c> — Container SERVER:121988-121992, Smelter's accumulator gate
    /// SERVER:142297-142311, BaseAI's owner gate SERVER:26018-26029). A guard that only reads local state is
    /// blind to a remote owner's real activity (blueprint Red Team 1 blocker #1).
    /// <para>
    /// The fix: sample <c>ZDO.DataRevision</c> (<c>public uint DataRevision { get; set; }</c>, SERVER:73541),
    /// vanilla replication metadata bumped by <c>IncreaseDataRevision()</c> on every <c>ZDO.Set(...)</c>
    /// overload (SERVER:73879 and call sites throughout) — a signal the server already receives for any ZDO it
    /// tracks, from ANY owner, modded or vanilla, with no new wire format and no client cooperation. An object
    /// whose DataRevision hasn't changed in <paramref name="quiescenceSeconds"/> (called with
    /// <see cref="NoodlesMcDoodles.Core.Features"/>-backed <c>HandoverGuard.QuiescenceSeconds</c>, per-call —
    /// never cached) is judged idle. This is deliberately coarse (seconds, not single-tick) — a backstop, not a
    /// replacement for a fast local check where one is actually observable (kept in <see cref="HandoverGuard"/>
    /// for Container/Vagon/Sadle/Ship).
    /// </para>
    /// <para>
    /// Self-expiring by construction, which is also what closes the naive-fix hazard (reading
    /// <c>ZDOVars.s_inUse</c> directly): nothing clears that flag on an ungraceful disconnect
    /// (<c>RemoveOrphanNonPersistentZDOS</c>, SERVER:77453, only sweeps non-persistent ZDOs; a placed container
    /// is persistent) — a raw flag read would live-lock forever. Here, a disconnected owner simply stops
    /// advancing DataRevision, so after <c>quiescenceSeconds</c> the object is correctly judged idle with no
    /// sticky state to live-lock on.
    /// </para>
    /// <para>
    /// Process-local only: never saved, never sent to a peer, self-heals on every connect/disconnect with no
    /// special-case cleanup needed (a stale entry for a ZDOID that stops being asked about simply stops being
    /// looked up; see <see cref="MaybeSweep"/> for the periodic trim that keeps the table from growing unbounded).
    /// </para>
    /// </summary>
    internal static class QuiescenceTracker
    {
        private readonly struct Seen
        {
            public readonly uint Revision;
            public readonly float SinceRealtime;
            public Seen(uint revision, float sinceRealtime) { Revision = revision; SinceRealtime = sinceRealtime; }
        }

        private static readonly Dictionary<ZDOID, Seen> s_seen = new Dictionary<ZDOID, Seen>();
        private static float s_lastSweep;

        // Only trimmed occasionally (not every call) — this table is small relative to world size (only ZDOs
        // HandoverGuard is ever asked about, not every ZDO the server knows of) and a stale entry is harmless
        // (worst case: one extra dictionary entry until the next sweep), so there is no correctness reason to
        // sweep aggressively, only a memory-growth one.
        private const float SweepIntervalSeconds = 120f;
        private const float StaleAfterSeconds = 600f;

        /// <summary>
        /// True once <paramref name="zdo"/>'s DataRevision has been unchanged for at least
        /// <paramref name="quiescenceSeconds"/>. First observation of a ZDOID (or a DataRevision change since
        /// the last observation) always returns false — "just changed, or first time seen this sweep: treat as
        /// active" (blueprint's own framing) — never a false "quiescent" on the very first ask.
        /// </summary>
        public static bool IsQuiescent(ZDO zdo, float quiescenceSeconds)
        {
            if (zdo == null) return false; // no ZDO, nothing to safely reclaim — caller's problem, not ours to assume
            try
            {
                MaybeSweep();

                ZDOID id = zdo.m_uid;
                uint revision = zdo.DataRevision;
                float now = Time.realtimeSinceStartup;

                if (!s_seen.TryGetValue(id, out Seen last) || last.Revision != revision)
                {
                    s_seen[id] = new Seen(revision, now);
                    return false;
                }

                return now - last.SinceRealtime >= quiescenceSeconds;
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(QuiescenceTracker), e);
                return false; // fail closed: unknown state is never "safe to reclaim"
            }
        }

        private static void MaybeSweep()
        {
            float now = Time.realtimeSinceStartup;
            if (now - s_lastSweep < SweepIntervalSeconds) return;
            s_lastSweep = now;

            // ToArray: safe to mutate s_seen while iterating a snapshot.
            foreach (var kv in new List<KeyValuePair<ZDOID, Seen>>(s_seen))
            {
                if (now - kv.Value.SinceRealtime > StaleAfterSeconds) s_seen.Remove(kv.Key);
            }
        }

        /// <summary>
        /// Kill-switch flush (see <c>KillSwitch.RegisterFlushCallback</c> in <see cref="GameplayInit"/>) — this
        /// table has no gameplay effect of its own (read-only bookkeeping consulted by a guard that is itself
        /// gated), but clearing it means every feature that reads it starts genuinely fresh ("just changed,
        /// treat as active") the moment the mod comes back on, rather than trusting minutes-old observations
        /// made before the outage.
        /// </summary>
        public static void Clear() => s_seen.Clear();
    }
}
