using System;
using System.Collections.Generic;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.Gameplay
{
    /// <summary>
    /// H6 (docs/design/05) — the FAST PATH half of the S2 (creature AI)/S7 (production) hand-over guard: "did
    /// THIS machine, which currently believes it owns this ZDO, actually tick it recently". Purely local
    /// (per-ZDOID last-local-tick timestamp, process-only, never saved or sent) and only ever correct in one
    /// direction — the local process being the current owner. It cannot see a REMOTE peer's ticks (the same
    /// blind spot <see cref="QuiescenceTracker"/> exists to close for the general case); <see cref="HandoverGuard"/>
    /// always checks <see cref="TickedRecently"/> first (cheap, most precise when it applies) and falls back to
    /// <see cref="QuiescenceTracker.IsQuiescent"/> (works for any owner, coarser) — never the other order, and
    /// never skips the backstop just because the fast path returned false (false only means "not locally
    /// ticking", not "known idle").
    /// <para>
    /// Real, verified hooks (blueprint Red Team 2 blocker #1 rejected the previous draft's
    /// "MonoUpdaters.UpdateAI"/InvokeRepeating-callback targets — neither is a patchable Harmony target; see
    /// class remarks on each patch below for the corrected ones):
    /// </para>
    /// </summary>
    internal static class LivenessBookkeeper
    {
        /// <summary>
        /// Recent = within the last few seconds, deliberately looser than any single tick interval (BaseAI's
        /// own 0.05f fixed dt, SERVER:72275's <c>MonoUpdatersExtra.UpdateAI</c> caller; Smelter's 1s
        /// accumulator) so a single missed/late tick under load never flips this from true to false and back —
        /// it only needs to answer "is this ZDOID under active local simulation", not measure exact cadence.
        /// </summary>
        private const float RecentWindowSeconds = 3f;

        private static readonly Dictionary<ZDOID, float> s_lastLocalTick = new Dictionary<ZDOID, float>();

        // RUNTIME REVIEW FIX (2026-09-28, MAJOR): this table gains one entry per distinct creature/smelter
        // ZDOID ever ticked while ProductionHandoverGuard is on and was never pruned — a real leak on a
        // long-running server with many creatures. Same counted-insert sweep as LivenessBookkeeper's own
        // s_lastDrop table (see below): every PruneEveryNTickInserts calls, drop every entry already older than
        // the window an entry could still matter for. A margin beyond RecentWindowSeconds means the prune pass
        // itself can never be the reason TickedRecently's answer for a still-fresh entry changes — only entries
        // that are unambiguously stale either way are ever removed.
        private const int PruneEveryNTickInserts = 256;
        private const float TickPruneMarginSeconds = 5f;
        private static int s_tickInsertsSincePrune;

        public static void NoteLocalTick(ZDOID id)
        {
            if (id == ZDOID.None) return;
            s_lastLocalTick[id] = Time.realtimeSinceStartup;

            if (++s_tickInsertsSincePrune >= PruneEveryNTickInserts)
            {
                s_tickInsertsSincePrune = 0;
                PruneStaleTicks(Time.realtimeSinceStartup);
            }
        }

        private static void PruneStaleTicks(float now)
        {
            List<ZDOID> stale = null;
            foreach (var kv in s_lastLocalTick)
            {
                if (now - kv.Value > RecentWindowSeconds + TickPruneMarginSeconds)
                {
                    (stale ??= new List<ZDOID>()).Add(kv.Key);
                }
            }
            if (stale == null) return;
            foreach (ZDOID id in stale) s_lastLocalTick.Remove(id);
        }

        public static bool TickedRecently(ZDO zdo)
        {
            if (zdo == null) return false;
            try
            {
                if (!s_lastLocalTick.TryGetValue(zdo.m_uid, out float last)) return false;
                return Time.realtimeSinceStartup - last <= RecentWindowSeconds;
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(LivenessBookkeeper) + ".TickedRecently", e);
                return false;
            }
        }

        /// <summary>Kill-switch flush — see <see cref="QuiescenceTracker.Clear"/> remarks; identical rationale.
        /// Also called (see <see cref="GameplayInit"/>) whenever <c>ProductionHandoverGuard.Active</c> is
        /// switched off at runtime, not only on the global kill switch (2026-09-28 runtime review) — both
        /// tables below are fed exclusively by patches gated on that one flag, so once it's off neither table
        /// is read or written again until it's re-enabled; clearing immediately just frees the memory sooner
        /// instead of waiting for either a kill-switch flush or 256 more ticks/drops to trigger a partial prune.</summary>
        public static void Clear()
        {
            s_lastLocalTick.Clear();
            s_tickInsertsSincePrune = 0;
            s_lastDrop.Clear();
            s_insertsSincePrune = 0;
        }

        // --- ZNetView field access for types with no public GetZDO()/GetZDOID() of their own -----------------
        // Smelter/CharacterDrop are plain MonoBehaviours (no shared "networked component" base in this game),
        // each with its own private `ZNetView m_nview` field (confirmed SERVER: Smelter 141950, matching the
        // same-shaped field on every other ZNetView-backed MonoBehaviour in this codebase). Cached once via
        // AccessTools, matching this project's established pattern (see Transport/DestroyChunking.cs's
        // `m_destroySendList` access) rather than a per-call `GetComponent<ZNetView>()` — these run on hot,
        // per-tick paths (BaseAI every ~0.05s per creature) where a repeated component lookup is wasted cost a
        // cached field-ref delegate avoids entirely.
        private static readonly AccessTools.FieldRef<Smelter, ZNetView> s_smelterNview =
            AccessTools.FieldRefAccess<Smelter, ZNetView>("m_nview");

        // BaseAI's own m_nview is `protected` (SERVER: BaseAI class body, confirmed this pass), not public and
        // no GetNView()-style accessor exists on the type — same FieldRefAccess pattern as Smelter above, which
        // works regardless of the field's declared accessibility.
        private static readonly AccessTools.FieldRef<BaseAI, ZNetView> s_baseAiNview =
            AccessTools.FieldRefAccess<BaseAI, ZNetView>("m_nview");

        /// <summary>
        /// S2 — <c>BaseAI.UpdateAI(float dt)</c>, SERVER:26018, <c>public virtual bool UpdateAI(float dt)</c>.
        /// Confirmed this pass (re-grepped, matches blueprint H6): its own first two statements are
        /// <c>if (!m_nview.IsValid()) return false;</c> then <c>if (!m_nview.IsOwner()) { ...; return false; }</c>
        /// — vanilla itself only lets this proceed past its own gate on the owning machine, mirroring
        /// Smelter.UpdateSmelter's shape below. Exactly two subclasses override it (confirmed: SERVER:25649,
        /// SERVER:27879, the only two `override bool UpdateAI(float dt)` in the assembly) and both call
        /// <c>base.UpdateAI(dt)</c> as their OWN first statement — so one postfix on the base virtual method
        /// observes every creature's owner-gated tick with no per-subclass patching needed. Standing audit
        /// instruction (per blueprint): re-verify this base-call invariant whenever a new BaseAI subclass is
        /// added to the game; if a future override ever skips calling base, patch that override directly too.
        /// <para>
        /// Rejected target (blueprint Red Team 2 blocker #1, re-confirmed this pass): "MonoUpdaters.UpdateAI"
        /// does not exist as a method on <c>MonoUpdaters</c> (SERVER:72157) — the only symbol by that name is
        /// the unrelated extension method <c>MonoUpdatersExtra.UpdateAI(this List&lt;IUpdateAI&gt;, ...)</c>
        /// (SERVER:72275), and <c>InvokeRepeating</c> has no managed dispatcher to patch at all. Harmony would
        /// throw resolving either as a target; <see cref="PatchRegistry"/>'s own per-class isolation is what
        /// would have contained that (this feature forced off, nothing else affected), but the correct target
        /// avoids the failure entirely.
        /// </para>
        /// Bookkeeping-only: never alters `__result`, never affects gameplay by itself — see class remarks.
        /// </summary>
        [HarmonyPatch(typeof(BaseAI), nameof(BaseAI.UpdateAI))]
        internal static class Patch_BaseAI_UpdateAI_LivenessPostfix
        {
            [HarmonyPostfix]
            internal static void Postfix(BaseAI __instance, bool __result)
            {
                if (!Features_Gameplay.ProductionHandoverGuard.Active) return;
                // RUNTIME REVIEW FIX (major, mixed-and-host, 2026-09-28): registration is now PatchSide.Both
                // (GameplayInit.cs) so this installs on a listen host too — gate here on real runtime authority,
                // never Side.IsDedicatedServerProcess, so a plain client with the (now-on-by-default) flag still
                // behaves exactly as with the feature off.
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                try
                {
                    if (!__result || __instance == null) return; // false = didn't pass vanilla's own owner gate this call
                    ZNetView nview = s_baseAiNview(__instance);
                    if (nview != null && nview.IsValid()) NoteLocalTick(nview.GetZDO().m_uid);
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_BaseAI_UpdateAI_LivenessPostfix), e);
                }
            }
        }

        /// <summary>
        /// S7 — <c>Smelter.UpdateSmelter()</c>, SERVER:142297, confirmed this pass: owner gate
        /// (<c>if (!m_nview.IsOwner()) return;</c>) sits AFTER the always-safe <c>UpdateRoof/UpdateSmoke/
        /// UpdateState</c> calls and BEFORE the accumulator/<c>Spawn()</c> loop — a postfix cannot distinguish
        /// "ran past the gate" from "returned early" by return value alone (the method is `void`), so this
        /// reads the owner flag itself, exactly the same condition vanilla's own gate just evaluated a moment
        /// earlier in the same call.
        /// </summary>
        [HarmonyPatch(typeof(Smelter), "UpdateSmelter")]
        internal static class Patch_Smelter_UpdateSmelter_LivenessPostfix
        {
            [HarmonyPostfix]
            internal static void Postfix(Smelter __instance)
            {
                if (!Features_Gameplay.ProductionHandoverGuard.Active) return;
                // RUNTIME REVIEW FIX (major, mixed-and-host, 2026-09-28) — see Patch_BaseAI_UpdateAI_LivenessPostfix.
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                try
                {
                    if (__instance == null) return;
                    ZNetView nview = s_smelterNview(__instance);
                    if (nview != null && nview.IsValid() && nview.IsOwner()) NoteLocalTick(nview.GetZDO().m_uid);
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_Smelter_UpdateSmelter_LivenessPostfix), e);
                }
            }
        }

        // --- DoubleDropDetected (DESIGN.md Lead decision 4 / Telemetry section) --------------------------------
        // Pure, additive observers (never alter behaviour, never intercept — cannot fail open/closed because
        // they don't gate anything) on the two Instantiate-backed output paths H6's Data-loss analysis names:
        // CharacterDrop.OnDeath (duplicate loot) and Smelter.QueueProcessed (duplicate production output).
        // WearNTear's collapse path is deliberately NOT instrumented here — H7's guard for that race is itself
        // deferred/unbuilt (docs/design/05 "Open questions"), so there is no guard to cross-check a detector
        // against yet; adding one now would be a detector for a bug this module cannot yet distinguish from the
        // guard's own known gap.
        //
        // LEAD FIX (2026-09-28): the previous version flagged the same ZDOID observed twice within
        // DuplicateWindowSeconds, reasoning "QueueProcessed's own accumulator loop requires >= 1 real second of
        // simulated time between iterations". That reasoning was wrong and the bug it hid was real. Vanilla
        // Smelter.UpdateSmelter (SERVER:142297-142352) adds REAL elapsed wall-clock time (GetDeltaTime(), a
        // saved-DateTime delta, not a per-frame one) to an accumulator capped at 3600f, then runs
        // `while (accumulator >= 1f)` — one SIMULATED second per pass, ALL inside the SAME call, at the SAME
        // Time.realtimeSinceStartup. A base whose smelter/kiln/refinery sat away from any simulating owner for
        // a while (players away, a zone coming back under server simulation, a server restart) legitimately
        // calls QueueProcessed many times for the SAME ZDOID within that one UpdateSmelter() call — one owner
        // catching up, not two owners racing. The old ZDOID-only check could not tell the difference and would
        // call LossSignals.Report on this completely ordinary catch-up, tripping AutoStandDown and disabling
        // the whole mod on normal play (a modded recipe with a short m_secPerProduct would trip it even
        // without a long absence, on every production cycle).
        //
        // Fix: a single process can only meaningfully observe a LOCAL duplication — two DIFFERENT live
        // component instances (two distinct Unity object identities, via GetInstanceID()) both producing output
        // for the SAME ZDOID. The SAME instance calling QueueProcessed/OnDeath repeatedly — a catch-up loop, a
        // fast modded recipe, anything — is never flagged, however many times or however close together it
        // repeats: there is exactly one produced-output owner in that case, this process's own component
        // instance. Only a DIFFERENT instance ID reporting the same ZDOID within the window is a genuine local
        // signal (e.g. a stray duplicate GameObject from an object-creation bug) and gets flagged.
        //
        // What this can and cannot see: it can catch a same-process duplicate-instantiation bug (two GameObjects
        // bound to the same ZDOID inside this one server process). It CANNOT see a split-brain between two
        // different MACHINES each believing they own the ZDO — there is no local signal for that at all; that
        // is the Ownership module's job (a DataRevision/OwnerRevision regression on a server-held ZDO — the
        // LossSignals.Report seam in src/Core/Contracts.cs names this as that module's own signal, not this
        // one's).
        private const float DuplicateWindowSeconds = 2f;

        private readonly struct DropSighting
        {
            public readonly int InstanceId;
            public readonly float SeenAtRealtime;
            public DropSighting(int instanceId, float seenAtRealtime) { InstanceId = instanceId; SeenAtRealtime = seenAtRealtime; }
        }

        private static readonly Dictionary<ZDOID, DropSighting> s_lastDrop = new Dictionary<ZDOID, DropSighting>();

        // Unbounded-growth guard: one entry per distinct ZDOID ever seen dying/producing while this feature is
        // on, otherwise never removed — a real leak on a long-running server. Every PruneEveryNInserts calls,
        // drop every entry already older than DuplicateWindowSeconds: such an entry can never again be the
        // "last" half of a future duplicate comparison (a new sighting for that ZDOID would already read as
        // outside the window), so it is safe to forget.
        private const int PruneEveryNInserts = 256;
        private static int s_insertsSincePrune;

        private static void NoteAndCheckDuplicate(ZDOID id, int instanceId, string source)
        {
            if (id == ZDOID.None) return;
            float now = Time.realtimeSinceStartup;
            if (s_lastDrop.TryGetValue(id, out DropSighting last)
                && last.InstanceId != instanceId
                && now - last.SeenAtRealtime <= DuplicateWindowSeconds)
            {
                Telemetry.GetCounter("gameplay.handoverGuard.doubleDropDetected").Increment();
                LossSignals.Report("handoverGuard_doubleDrop",
                    $"{source} fired for {id} from two different live instances (#{last.InstanceId} then #{instanceId}) within {now - last.SeenAtRealtime:F2}s");
            }
            s_lastDrop[id] = new DropSighting(instanceId, now);

            if (++s_insertsSincePrune >= PruneEveryNInserts)
            {
                s_insertsSincePrune = 0;
                PruneStale(now);
            }
        }

        private static void PruneStale(float now)
        {
            List<ZDOID> stale = null;
            foreach (var kv in s_lastDrop)
            {
                if (now - kv.Value.SeenAtRealtime > DuplicateWindowSeconds)
                {
                    (stale ??= new List<ZDOID>()).Add(kv.Key);
                }
            }
            if (stale == null) return;
            foreach (ZDOID id in stale) s_lastDrop.Remove(id);
        }

        [HarmonyPatch(typeof(CharacterDrop), "OnDeath")]
        internal static class Patch_CharacterDrop_OnDeath_DoubleDropWatch
        {
            [HarmonyPostfix]
            internal static void Postfix(CharacterDrop __instance)
            {
                if (!Features_Gameplay.ProductionHandoverGuard.Active) return;
                // RUNTIME REVIEW FIX (blocker, mixed-and-host, 2026-09-28): registration is now PatchSide.Both
                // (GameplayInit.cs) — a listen host is a client build that IS the server, and this detector is
                // the safety net for exactly the dual-owner/InvokeRepeating drop race this flag exists to guard
                // against, so it must actually run there. Gate on real runtime authority, never
                // Side.IsDedicatedServerProcess, so a plain client with the (now-on-by-default) flag still
                // behaves exactly as with the feature off.
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                try
                {
                    if (__instance == null) return;
                    ZNetView nview = __instance.GetComponent<ZNetView>(); // rare event (once per death) — a live GetComponent call here is not a hot path
                    if (nview != null && nview.IsValid())
                        NoteAndCheckDuplicate(nview.GetZDO().m_uid, __instance.GetInstanceID(), "CharacterDrop.OnDeath");
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_CharacterDrop_OnDeath_DoubleDropWatch), e);
                }
            }
        }

        [HarmonyPatch(typeof(Smelter), "QueueProcessed")]
        internal static class Patch_Smelter_QueueProcessed_DoubleDropWatch
        {
            [HarmonyPostfix]
            internal static void Postfix(Smelter __instance)
            {
                if (!Features_Gameplay.ProductionHandoverGuard.Active) return;
                // RUNTIME REVIEW FIX (blocker, mixed-and-host, 2026-09-28) — see Patch_CharacterDrop_OnDeath_DoubleDropWatch.
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                try
                {
                    if (__instance == null) return;
                    ZNetView nview = s_smelterNview(__instance);
                    if (nview != null && nview.IsValid())
                        NoteAndCheckDuplicate(nview.GetZDO().m_uid, __instance.GetInstanceID(), "Smelter.QueueProcessed");
                }
                catch (Exception e)
                {
                    NoodlesLog.PatchException(nameof(Patch_Smelter_QueueProcessed_DoubleDropWatch), e);
                }
            }
        }
    }
}
