using System.Collections.Generic;
using BepInEx.Configuration;
using NoodlesMcDoodles.Core;

namespace NoodlesMcDoodles.Transport
{
    /// <summary>
    /// Q8 — batch-capped wrapper around <c>ZDOMan.ForceSendZDO(ZDOID)</c> / <c>ForceSendZDO(long, ZDOID)</c>
    /// (SERVER:77753/77761, CLIENT:77895/77903 — existing public API, confirmed identical signatures on both
    /// builds via <c>ilspycmd -il</c> this pass). docs/design/01 Hook 10.
    ///
    /// This is a **call-site discipline, not a vanilla hook** (no <c>[HarmonyPatch]</c> here): other
    /// subsystems (ownership handover, in a later milestone) call this helper after a claim/handover
    /// completes so observing peers resync promptly, instead of calling <c>ZDOMan.instance.ForceSendZDO</c>
    /// directly in an unbounded loop over a potentially large ID set.
    /// </summary>
    internal static class ForceSendBudget
    {
        public static ConfigEntry<int> Q8ForceSendBatchCapConfig;
        public static FeatureFlag Q8;

        public static void BindAndRegister()
        {
            Q8ForceSendBatchCapConfig = NoodlesConfig.File.Bind("Transport", "Q8_ForceSendBatchCap", 1000,
                new ConfigDescription(
                    "Max ZDOIDs force-sent per ForceSendMany call. Any remainder is simply not force-sent " +
                    "this call (force-send is a resync-promptness nicety, not a delivery guarantee — the " +
                    "ZDO itself still goes out on the next normal SendZDOs round regardless).",
                    new AcceptableValueRange<int>(100, 5000)));

            var q8Cfg = NoodlesConfig.File.Bind("Features", "Transport.Q8.Active", true,
                "Q8: cap ForceSendMany's batch size. Off = ForceSendMany falls back to an uncapped loop " +
                "calling ZDOMan.ForceSendZDO one-for-one, identical to a caller that never used this helper.");
            Q8 = Features.Register("Transport.Q8", q8Cfg);
        }

        /// <summary>
        /// The one sanctioned entry point for "force-send many ZDOs at once" in this mod. With Q8 off, this
        /// is an uncapped loop over vanilla's own API — identical to what any caller not using this helper
        /// would do, so disabling the flag never changes behaviour beyond removing the cap itself.
        /// </summary>
        public static void ForceSendMany(IEnumerable<ZDOID> ids, long? peerId = null)
        {
            if (ids == null || ZDOMan.instance == null) return;

            int cap = Q8.Active ? System.Math.Max(1, Q8ForceSendBatchCapConfig.Value) : int.MaxValue;
            int n = 0;
            foreach (var id in ids)
            {
                if (n >= cap)
                {
                    Telemetry_Transport.ForceSendBatchCapped(n);
                    return;
                }
                n++;
                if (peerId.HasValue) ZDOMan.instance.ForceSendZDO(peerId.Value, id);
                else ZDOMan.instance.ForceSendZDO(id);
            }
        }
    }
}
