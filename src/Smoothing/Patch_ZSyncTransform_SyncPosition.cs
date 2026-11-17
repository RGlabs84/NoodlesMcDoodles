using System;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.Smoothing
{
    /// <summary>
    /// H-C1 (docs/design/06-client-smoothing-and-compression.md) — buffered, time-based interpolation for
    /// non-owned objects, replacing vanilla's single-sample 0.2/0.5 lerp/slerp with an abrupt 5 m snap.
    ///
    /// <b>Ground truth, re-verified directly against both decompiles this pass (not copied from the blueprint on
    /// trust):</b> <c>private void SyncPosition(ZDO zdo, float dt, out bool usedLocalRotation)</c> —
    /// CLIENT:87922-88018, SERVER:87674-87766, byte-identical bodies (only the line offset differs). Called
    /// exactly once, from <c>ClientSync</c>'s non-kinematic branch — CLIENT:88020-88119/SERVER:87772-87871,
    /// also byte-identical — which itself opens with the owner gate <c>if (zDO.IsOwner()) return;</c>
    /// (CLIENT:88023-88026/SERVER:87775-87778), so this prefix only ever runs for objects THIS process does not
    /// own, exactly the surface vanilla itself restricts non-owner interpolation to.
    ///
    /// <b>ZERO DATA LOSS PROOF:</b> re-read in full this pass, <c>SyncPosition</c> and <c>ClientSync</c> contain
    /// no <c>zdo.Set(...)</c>/<c>SetOwner</c>/any ZDO write call anywhere in either method, on either assembly —
    /// both are provably read-ZDO, write-local-Transform-only. This patch only ever reads
    /// <c>zdo.GetPosition()/GetRotation()/GetVec3()/DataRevision/HasOwner()/GetConnectionZDOID()</c> (see
    /// <see cref="SmoothState.Ingest"/>) and writes <c>__instance.transform.position/rotation</c> — the same
    /// write surface vanilla's own code already uses, never a ZDO. Smoothing is render-only, full stop: even the
    /// mandatory fail-open catch below cannot turn this into a ZDO write, because no code path in this whole
    /// module (see <see cref="SmoothBuffer"/>, <see cref="SmoothState"/>) ever holds a reference capable of one.
    /// </summary>
    [HarmonyPatch(typeof(ZSyncTransform), "SyncPosition")]
    internal static class Patch_ZSyncTransform_SyncPosition
    {
        // Vanilla's own already-tuned snap threshold, reused verbatim — never a new number (task brief: "reuse
        // vanilla's 5 m snap threshold"). CLIENT:88012/SERVER:87764: `(num < 5f) ? Lerp(...) : position`.
        private const float VanillaSnapThresholdMeters = 5f;

        // 2026-09-28 review fix (MINOR, confirmed): this runs for every visible non-owned object every frame —
        // the only per-object-per-frame telemetry call site in the mod — so every counter is resolved ONCE here
        // (a ConcurrentDictionary string lookup each) rather than by name on every call.
        private static readonly Counter s_fallbackParented = Telemetry.GetCounter("smoothing.fallback.parented");
        private static readonly Counter s_fallbackTeleport = Telemetry.GetCounter("smoothing.fallback.teleport");
        private static readonly Counter s_fallbackNoSamples = Telemetry.GetCounter("smoothing.fallback.noSamples");
        private static readonly Counter s_takeovers = Telemetry.GetCounter("smoothing.takeovers");
        private static readonly Counter s_rotationTakeovers = Telemetry.GetCounter("smoothing.rotationTakeovers");
        private static readonly Counter s_fallbackException = Telemetry.GetCounter("smoothing.fallback.exception");

        // Harmony's convention for patching a method's `out` parameter: declare it `ref` in the patch signature.
        static bool Prefix(ZSyncTransform __instance, ZDO zdo, float dt, ref bool usedLocalRotation)
        {
            // C5 — dedicated-server skip-gate (folded into C1 per the blueprint's Levers table rather than a
            // separate patch-application gate: "Inline runtime check inside C1/C2's own prefix ... simpler and
            // equally correct"). The server process runs this exact same non-owner ClientSync/SyncPosition path
            // for every ZDO it doesn't own — e.g. connected players' own Characters (CLIENT:88023-88026 /
            // SERVER:87775-87778, confirmed identical) — but nothing on that process ever renders it, so there
            // is zero reason to run interpolation math there. Side.IsDedicatedServerProcess is Core's own
            // cached, zero-per-call-cost headless heuristic (resolved once from the graphics-device type, long
            // before this ever gets called) — reused here rather than a live ZNet.instance.IsDedicated() check
            // so this guard costs one bool read, not a null-check-plus-virtual-call, on every visible non-owned
            // object every frame.
            //
            // Only these three checks (side, flag, null) stay outside the try below — trivial, non-throwing
            // reads with nothing left to fail open FROM (2026-09-28 review fix, MAJOR: everything past this
            // point, including guard 4, used to run partly outside the try; an exception there escaped the
            // Harmony replacement uncaught, skipping NoodlesLog.PatchException's telemetry/AutoStandDown
            // feed entirely — the opposite of "fail open," since it also meant vanilla's OWN body never ran
            // that tick either. Now everything that can plausibly throw is inside one try.).
            if (Side.IsDedicatedServerProcess) return true;
            if (!Features_Smoothing.ClientSmoothing.Active) return true;
            if (zdo == null) return true; // vanilla's own body has no null-guard here either; matching, not worsening, vanilla.

            try
            {
                // Guard 4 (mandatory) — vanilla's own parented-object branch, re-checked every tick, mirrors
                // CLIENT:87925-87928/SERVER:87677-87680 exactly: `m_characterParentSync && zdo.HasOwner()` plus a
                // non-None SyncTransform connection ZDOID. That branch computes position/rotation relative to a
                // PARENT transform (a player sitting on a bench, standing on a moving ship) — never from
                // zdo.GetPosition(). Taking over here unconditionally would silently replace vanilla's correct
                // parent-relative computation with wrong absolute-position interpolation for a common case
                // (research/11 zero-data-loss guard 7) — so this is always deferred to vanilla, never smoothed.
                if (__instance.m_characterParentSync && zdo.HasOwner())
                {
                    ZDOID connId = zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.SyncTransform);
                    if (!connId.IsNone())
                    {
                        // Guard 3b: mark the buffer as needing a fresh start once we return here — a long parented
                        // excursion (sitting for minutes, riding a ship) must never leave a stale pre-parenting
                        // sample for TryInterpolate to blend against the moment un-parenting hands rendering back.
                        // Idempotent and cheap to call on every parented tick (research/11 Data-loss #3b).
                        SmoothState.For(__instance).NoteSuspended();
                        s_fallbackParented.Increment();
                        return true;
                    }
                }

                SmoothState state = SmoothState.For(__instance);
                float now = Time.realtimeSinceStartup;

                // Guard 3 (mandatory, RED-TEAM FIX) — teleport check at INGEST time, against the immediately
                // preceding RAW sample, never against TryInterpolate's blended output (see SmoothBuffer.Ingest's
                // remarks for why a render-time check can structurally never catch a teleport spread across the
                // buffered interval). Reuses vanilla's own 5 m threshold, not a new one.
                bool teleported = state.Ingest(zdo, VanillaSnapThresholdMeters, now);
                if (teleported)
                {
                    __instance.transform.position = zdo.GetPosition();
                    s_fallbackTeleport.Increment();
                    return false; // snapped this tick — nothing buffered to interpolate against yet
                }

                float playbackDelaySeconds = SmoothingModule.PlaybackDelayMs.Value / 1000f;
                float extrapolationCapSeconds = SmoothingModule.ExtrapolationCapMs.Value / 1000f; // C3 knob
                bool allowExtrapolation = zdo.HasOwner(); // mirrors vanilla's own dead-reckoning gate (CLIENT:88002/SERVER:87754)

                if (!state.TryInterpolate(now, playbackDelaySeconds, extrapolationCapSeconds, allowExtrapolation,
                        out Vector3 pos, out Quaternion rot))
                {
                    s_fallbackNoSamples.Increment();
                    return true; // buffer not ready (just spawned / just came into range) — vanilla runs this tick.
                }

                __instance.transform.position = pos;
                __instance.transform.rotation = rot;

                // Guard 5 (mandatory) — ClientSync's own rotation slerp (CLIENT:88076/SERVER:87828) lives
                // OUTSIDE SyncPosition, gated on this out-parameter, which defaults false (CLIENT:87924/
                // SERVER:87676). Returning false alone does not stop it from running afterward and blending the
                // rotation we just wrote 50% back toward the raw current-tick ZDO rotation — this write is what
                // actually takes rotation over.
                usedLocalRotation = true;

                s_takeovers.Increment();
                s_rotationTakeovers.Increment();
                return false;
            }
            catch (Exception e)
            {
                // Guard 1 (mandatory): fail open. See this class's own remarks above for the zero-data-loss
                // proof — neither this code nor the vanilla code it replaces can reach a ZDO write, so the
                // worst this catch ever does is leave one frame looking like vanilla's own crude lerp/snap.
                s_fallbackException.Increment();
                NoodlesLog.PatchException(nameof(Patch_ZSyncTransform_SyncPosition), e);
                return true;
            }
        }
    }
}
