using System;
using HarmonyLib;
using NoodlesMcDoodles.Core;

namespace NoodlesMcDoodles.Compression
{
    /// <summary>
    /// H-D1c (docs/design/06) — guard 7, in-flight suppression. <c>CreateSyncList</c>
    /// (SERVER:77259-77271/CLIENT:77401-77413) rescans a peer's whole candidate set through
    /// <c>ShouldSend</c> on EVERY round with no notion of "already offered, awaiting ack" — only "known" (has
    /// an <c>m_zdos</c> entry) or "not known". Because H-D1b defers the <c>m_zdos</c> write until ack, without
    /// this postfix every unacked ZDO would be reselected and recompressed every round until ack lands — for
    /// any connection at or above the send-round interval (effectively all real, non-LAN connections), a
    /// sustained resend loop that could cost more bandwidth/CPU than plain ZDOData.
    ///
    /// Deliberately NOT gated on <see cref="CompressionModule.EffectiveActive"/> (unlike every other hook in
    /// this module): <see cref="CompressionState.IsInFlightUnacked"/> is already cheap when nothing is tracked
    /// for a peer (a single dictionary miss), and gating this check on the live flag would create a real
    /// correctness gap if the flag flips off mid-session while a batch is genuinely in flight — the suppression
    /// would stop, vanilla's own plain-ZDOData path would immediately resend the same ZDOs with a fresh
    /// revision stamp, and a LATE-arriving ack for the original compressed batch could then overwrite that
    /// fresher <c>m_zdos</c> entry with stale revision numbers. Always checking (regardless of the flag) means
    /// any residual in-flight state is resolved the same way regardless of when the flag changed: by its own
    /// ack, or by its own timeout — never by silently stopping mid-resolution.
    /// </summary>
    /// <remarks>
    /// LEAD FIX (data-loss review, confirmed): a force-sent ZDO (present in __instance.m_forceSend) is NEVER
    /// suppressed here, even while genuinely in flight. Reason, confirmed by direct read of AddForceSendZdos
    /// (SERVER:77310-77332): it calls this SAME ShouldSend and treats a false result as "this ZDO no longer
    /// needs forcing" (if ShouldSend is false it queues the id for removal from m_forceSend right after the
    /// scan). Vanilla's own world only ever produces false there when the peer's m_zdos already has this
    /// revision - a genuinely permanent "no longer needed" signal. This guard's own suppression is not that:
    /// it means "temporarily withheld, awaiting THIS batch's ack", which AddForceSendZdos cannot tell apart
    /// from vanilla's permanent signal. Suppressing a force-sent ZDO here would make CreateSyncList (called
    /// every round, including while the ZDO's own first batch is still unacked) strip its force-send mark
    /// before delivery is ever confirmed - the same "reachable only via force-send, now orphaned" failure this
    /// fix's sibling change in Patch_ZDOMan_SendZDOs closes for the exception path, reintroduced through this
    /// path instead. Cost of the exemption: a force-sent ZDO can be redundantly repacked into more than one
    /// in-flight batch while awaiting its first ack (harmless - CommitInFlight is idempotent per batch, and a
    /// repeat is naturally bounded: m_forceSend only actually clears once some batch containing it is truly
    /// acked). Force-sent ZDOs are a narrow category (out-of-range portal relinks and similar), so this trades
    /// a small, bounded, correctness-neutral inefficiency for never orphaning one.
    ///
    /// The SAME hazard, for a MUCH wider category, applies to a client's own m_clientChangeQueue
    /// (ZDOMan-level, not per-peer - a client's whole set of "my own owned ZDOs with a pending change to push
    /// to the server"). CreateSyncList's client branch (SERVER:77289-77305, confirmed) has the identical
    /// shape: `if (ShouldSend(zdo)) toSync.Add(...); else m_tempRemoveList.Add(...)`, then removes every id in
    /// m_tempRemoveList from m_clientChangeQueue right after. Since CreateSyncList's client branch iterates
    /// ONLY m_clientChangeQueue's own members (it has no other candidate source, unlike the server's
    /// range-based scan), a client-owned ZDO stripped from that queue while merely in flight - not yet acked,
    /// not yet failed - would never be reconsidered again if its batch later nacks or times out: exactly the
    /// same "the only path back in was just closed" failure, but for ordinary client-owned traffic, not just
    /// force-sent objects. Exempted the same way, for the same reason.
    ///
    /// 1.0.8: <see cref="CompressionState.IsInFlightUnacked"/> no longer means "suppress unconditionally" —
    /// it now also lets a ZDO whose CURRENT revision has moved past whatever's in flight through as a
    /// superseding send (the stutter fix: a ZDO changing every send round used to be held back for a whole
    /// ack round-trip; see that method's own remarks). This postfix's own job doesn't change: still just
    /// "ask the one place that knows, and suppress iff it says so."
    /// </remarks>
    [HarmonyPatch(typeof(ZDOMan.ZDOPeer), nameof(ZDOMan.ZDOPeer.ShouldSend))]
    internal static class Patch_ZDOPeer_ShouldSend
    {
        static void Postfix(ZDOMan.ZDOPeer __instance, ZDO zdo, ref bool __result)
        {
            try
            {
                if (!__result || __instance?.m_peer == null || zdo == null) return;
                if (__instance.m_forceSend.Contains(zdo.m_uid)) return; // see class remarks - never suppress a force-sent id
                if (ZDOMan.instance != null && ZDOMan.instance.m_clientChangeQueue.Contains(zdo.m_uid)) return; // see class remarks - same reason, client's own outbound queue
                if (CompressionState.IsInFlightUnacked(__instance.m_peer, zdo))
                {
                    __result = false; // same revision already offered in an unacked compressed batch — don't reselect every round
                }
                // else: either nothing in flight for this id, or it's a 1.0.8 superseding send (a newer
                // revision than whatever's in flight) — either way vanilla's own answer (true) stands, and
                // CompressionState already counted whichever case applied.
            }
            catch (Exception e)
            {
                NoodlesLog.PatchException(nameof(Patch_ZDOPeer_ShouldSend), e);
                // __result (vanilla's own answer) is left untouched — fail open to "yes, consider sending it",
                // which is exactly what would happen with this feature absent entirely.
            }
        }
    }
}
