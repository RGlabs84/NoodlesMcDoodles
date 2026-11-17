using BepInEx.Configuration;
using NoodlesMcDoodles.Core;
using NoodlesMcDoodles.Core.ConfigFiles;

namespace NoodlesMcDoodles.Smoothing
{
    /// <summary>
    /// Module entry point for C1/C3/C5 (docs/design/06-client-smoothing-and-compression.md, DESIGN.md M6).
    /// Client-only rendering fix for non-owned objects (wildlife, dropped items, etc.) — see
    /// <see cref="Patch_ZSyncTransform_SyncPosition"/> for the actual hook and its zero-data-loss proof.
    ///
    /// C2 (kinematic/vehicle branch) is explicitly deferred per DESIGN.md's Deferred list and the task brief —
    /// not built here, no hook, no config, no stub.
    /// </summary>
    internal static class SmoothingModule
    {
        public static ConfigEntry<int> PlaybackDelayMs;
        public static ConfigEntry<int> ExtrapolationCapMs;
        internal static FeatureFlag ClientSmoothingFlag { get; private set; }

        [NoodlesModuleInit]
        internal static void Init()
        {
            ConfigFile config = NoodlesConfig.File;

            // 1.1.0: main file [4 - Client] BufferedSmoothing (was [ClientSmoothing] Active). C1, EXPERIMENTAL
            // (2026-09-28 lead decision: opt-in experiment, not the M6 deliverable — that is ShortExtrapolation):
            // buffered, time-based interpolation for non-owned objects (ZSyncTransform.SyncPosition), replacing
            // vanilla's single-sample 0.2/0.5 lerp/slerp and its 5m snap. Render-only, never writes a ZDO; inert on a
            // dedicated server (C5). Measured (tools/smooth-sim, SMOOTHING-IMPL.md 5.3): at 100ms delay, p95
            // smoothness is 53-67% better than vanilla but the worst single-frame snap is WORSE (6.25 vs 3.95 m/s
            // ordinary play, 25.2 vs 7.0 m/s congested far connection).
            ConfigEntry<bool> active = NoodlesConfig.BufferedSmoothing;
            ClientSmoothingFlag = Features.Register("ClientSmoothing", active);

            // 2026-09-28 config-defaults requirement: a default should be a clearly noticeable gain at ordinary
            // play (home-hosted, 2-10 players, one cross-country at ~60-80ms RTT), at the smallest value that
            // gets most of the gain — not the largest value that's still safe. See SMOOTHING-IMPL.md's Defaults
            // table for the full tools/smooth-sim evidence behind both values below.
            PlaybackDelayMs = config.Bind("ClientSmoothing", "PlaybackDelayMs", 100,
                new ConfigDescription("How far behind real time to render non-owned objects (ms). No vanilla " +
                    "equivalent — vanilla has no playback buffer at all, just an unbuffered 0.2-per-tick lerp " +
                    "toward whatever the latest update says. Default 100ms: tools/smooth-sim's ordinary-play " +
                    "sweep (20-150ms send jitter, no forced gaps) shows 100ms already captures ~94% of the " +
                    "steady-state smoothing gain over an unbuffered render (0ms) and has this module's best " +
                    "measured tracking accuracy in the whole sweep; 150-300ms squeezes out a little further " +
                    "smoothing at the cost of that much more added visual lag for no accuracy benefit — safe " +
                    "opt-in headroom for a player who wants maximum smoothness over responsiveness, not a " +
                    "correctness concern. A taste knob either way: shorter risks falling back to extrapolation " +
                    "more often under jitter, longer delays what you see other entities doing. Client-local " +
                    "only — each client's own choice, zero interaction with any other peer or the server.",
                    new AcceptableValueRange<int>(0, 300)));

            // BLUEPRINT VALUE -> SHIPPED VALUE: docs/design/06's own Configuration table defaults this to 2000ms
            // ("byte-identical to vanilla's own cap") within its already-reviewed 500-4000ms range. Lowered to
            // 500ms (the range's own floor, not a new number outside it) after tools/smooth-sim's gap sweep
            // (ordinary jitter + forced 1-3s dropped-batch gaps) measured WORSE worst-case overshoot from the
            // true position at 2000ms (~3.9m) than at 500-600ms (~2.4-2.6m): a longer cap keeps dead-reckoning
            // on stale, pre-gap velocity for longer while a real (direction-changing) creature has already moved
            // on, so shorter is both smoother through ordinary hiccups and safer through a genuine dropout here.
            ExtrapolationCapMs = config.Bind("ClientSmoothing", "ExtrapolationCapMs", 500,
                new ConfigDescription("Shared by two features: C1's own extrapolation-past-the-buffer knob " +
                    "(active only while [4 - Client] BufferedSmoothing=true) and ClientSmoothing.ShortExtrapolation's " +
                    "standalone patch on vanilla's own dead-reckoning timer (active only while THAT flag is " +
                    "true and ClientSmoothing is false) — one value, so the two features never disagree about " +
                    "what \"how long to trust stale velocity\" means. Vanilla hardcodes this at 2000ms " +
                    "(CLIENT:88005/SERVER:87757); 500ms measures better on worst-case overshoot during a " +
                    "dropped-batch gap for both features (tools/smooth-sim, SMOOTHING-IMPL.md section 5.4: " +
                    "2.42m vs 3.88m). Raise toward 4000ms only if live telemetry (T8) shows real send-round " +
                    "gaps regularly outlasting what 500ms rides through smoothly — the safe range is unchanged " +
                    "from the blueprint.",
                    new AcceptableValueRange<int>(500, 4000)));

            // Informational only (Capabilities.cs: "smoothing never needs the server") — C1 needs no RPC, no
            // negotiation, no wire format at all (docs/design/06: "Negotiation / data formats: none"). Advertised
            // per DESIGN.md's Cross-module seams table purely so the bit is on record for any future consumer.
            Capabilities.Advertise(Capabilities.ClientSmoothing);

            PatchRegistry.Register("ClientSmoothing", typeof(Patch_ZSyncTransform_SyncPosition), PatchSide.Both);

            // Task brief: "Flag off or kill switch = exactly vanilla ... drop per-object state." Two edges to
            // cover: this feature's OWN Active bool flipping off, and the global kill switch tripping (which
            // this module's own SettingChanged below cannot see, since it only fires for this entry). Both
            // funnel into the same DropAllState — see SmoothState's remarks for why an epoch bump, not a
            // literal table Clear(), is what "drop" means here on this project's net472 target.
            KillSwitch.RegisterFlushCallback("ClientSmoothing", SmoothState.DropAllState);
            active.SettingChanged += (_, __) =>
            {
                if (!ClientSmoothingFlag.Active) SmoothState.DropAllState();
            };
        }
    }

    /// <summary>Dotted-access convenience wrapper, matching CORE-API.md's suggested <c>Features.Module.Lever.Active</c> pattern.</summary>
    internal static class Features_Smoothing
    {
        public static FeatureFlag ClientSmoothing => SmoothingModule.ClientSmoothingFlag;
        public static FeatureFlag ShortExtrapolation => Patch_ZSyncTransform_SyncPosition_ShortExtrapolation.ShortExtrapolationFlag;
    }
}
