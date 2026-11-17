using System.Runtime.CompilerServices;
using UnityEngine;

namespace NoodlesMcDoodles.Smoothing
{
    /// <summary>
    /// Per-<c>ZSyncTransform</c>-instance smoothing state — the Unity/ZDO-aware boundary around the pure
    /// <see cref="SmoothBuffer"/>. Converts <c>Vector3</c>/<c>Quaternion</c>/<c>ZDO</c> reads at the two call
    /// sites (ingest, render) and nowhere else, so the actual interpolation rule stays 100% pure (see
    /// <see cref="SmoothBuffer"/>'s remarks).
    ///
    /// <b>Lifetime (task brief: "per-instance state tied to the component's lifetime"):</b> keyed by
    /// <see cref="ConditionalWeakTable{TKey,TValue}"/> on the <c>ZSyncTransform</c> component reference itself —
    /// deliberately NOT a hand-maintained <c>Dictionary&lt;ZDOID,...&gt;</c> (research/11 zero-data-loss guard 2:
    /// "could silently accumulate entries for despawned objects over a long session") and NOT a second sibling
    /// <c>MonoBehaviour</c> added via <c>AddComponent</c> (the blueprint's other suggested option): a
    /// <c>ConditionalWeakTable</c> entry is tied EVEN MORE directly to the instance's own lifetime than a sibling
    /// component would be — it holds no strong reference to the key, so once nothing else references a
    /// destroyed object's <c>ZSyncTransform</c> wrapper, both the key and this state become collectible
    /// automatically, with no manual purge, no `Update()`/`OnDestroy()` overhead, and no extra component visible
    /// on the GameObject. Exactly the same pattern <c>src/Transport/AdaptiveRate.cs</c> already uses for
    /// per-<c>ZNetPeer</c> state in this codebase (<c>ConditionalWeakTable&lt;ZNetPeer, RateState&gt;</c>).
    /// </summary>
    internal sealed class SmoothState
    {
        private static readonly ConditionalWeakTable<ZSyncTransform, SmoothState> s_states =
            new ConditionalWeakTable<ZSyncTransform, SmoothState>();

        // Bumped on every ClientSmoothing.Active true->false transition (config edit or the global kill switch —
        // see SmoothingModule.Init) so a live re-enable never silently resumes interpolation against samples
        // buffered before the feature was switched off (task brief: "kill switch = ... drop per-object state").
        // ConditionalWeakTable<K,V> on this project's net472 target has no Clear(), and we do not want a second,
        // hand-maintained key list purely to enable one (that would reintroduce the exact leak shape this class's
        // lifetime design avoids) — an epoch each instance lazily compares itself against gives the same
        // observable result (stale state is never read again) with no extra bookkeeping and no allocation on the
        // common (epoch unchanged) path.
        private static int s_generation;

        public static void DropAllState() => System.Threading.Interlocked.Increment(ref s_generation);

        public static SmoothState For(ZSyncTransform instance) =>
            s_states.GetValue(instance, _ => new SmoothState());

        private SmoothBuffer _buffer = new SmoothBuffer();
        private int _generation = s_generation;

        private SmoothBuffer Buffer
        {
            get
            {
                if (_generation != s_generation)
                {
                    _buffer = new SmoothBuffer();
                    _generation = s_generation;
                }
                return _buffer;
            }
        }

        public void NoteSuspended() => Buffer.NoteSuspended();

        /// <summary>
        /// Reads position/rotation/velocity/revision straight off the ZDO — the ONLY reads this whole module
        /// ever performs against it (see <see cref="Patch_ZSyncTransform_SyncPosition"/>'s remarks for the
        /// zero-data-loss proof: no <c>zdo.Set</c>/<c>SetOwner</c> call exists anywhere in this module).
        /// </summary>
        public bool Ingest(ZDO zdo, float snapThresholdMeters, float nowRealtime)
        {
            Vector3 pos = zdo.GetPosition();
            Quaternion rot = zdo.GetRotation();
            Vector3 vel = zdo.GetVec3(ZDOVars.s_velHash, Vector3.zero); // same field vanilla's own dead-reckoning reads (CLIENT:88006/SERVER:87758)
            return Buffer.Ingest(
                zdo.DataRevision,
                pos.x, pos.y, pos.z,
                rot.x, rot.y, rot.z, rot.w,
                vel.x, vel.y, vel.z,
                nowRealtime, snapThresholdMeters);
        }

        public bool TryInterpolate(
            float nowRealtime, float playbackDelaySeconds, float extrapolationCapSeconds, bool allowExtrapolation,
            out Vector3 pos, out Quaternion rot)
        {
            bool ok = Buffer.TryGetRenderPose(
                nowRealtime, playbackDelaySeconds, extrapolationCapSeconds, allowExtrapolation,
                out float px, out float py, out float pz,
                out float qx, out float qy, out float qz, out float qw);

            pos = ok ? new Vector3(px, py, pz) : default;
            rot = ok ? new Quaternion(qx, qy, qz, qw) : Quaternion.identity;
            return ok;
        }
    }
}
