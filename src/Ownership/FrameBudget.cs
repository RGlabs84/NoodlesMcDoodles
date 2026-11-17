using BepInEx.Configuration;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.Ownership
{
    /// <summary>
    /// WP-D (scratchpad/saa/final.md §3/§7/§8) — the frame-time half of O11's "robust error correction and
    /// failbacks" requirement: a sticky, hysteresis-gated "is the server currently over its frame-time budget"
    /// signal, read by <see cref="LoadShedder"/> to decide whether near-field SAA claiming should actively be
    /// handing zones back to vanilla right now.
    ///
    /// Deliberately does NOT run its own EWMA — <see cref="NoodlesMcDoodles.Core.FrameTiming.RecentMeanMs"/>
    /// (Core/FrameTiming.cs) already smooths raw per-frame time with a ~20-frame exponential average for Q13's
    /// own, unrelated use; this reads that same already-smoothed signal rather than smoothing it a second time
    /// (which would only add lag, not accuracy), then applies its OWN two-threshold/two-dwell hysteresis on top
    /// — exactly the shape O9's <c>RttMarginMs</c>/<c>RttDwellSeconds</c> already established for RTT (a
    /// pre-smoothed input, a margin test, a continuous-dwell requirement before acting), just with an ENTER and
    /// an EXIT threshold/dwell pair instead of one of each, since a load-shedding valve specifically must not
    /// flap right at a single boundary the way a one-shot margin test would.
    ///
    /// Read-only with respect to game state (like <c>FrameTiming</c> itself) — this file patches nothing and
    /// holds no ownership state of its own; <see cref="LoadShedder"/> is the only consumer of any real
    /// consequence. <see cref="Tick"/> is called once per <c>NoodlesHeartbeat</c> frame, from
    /// <see cref="LoadShedder.Tick"/> (the one call site this task's file scope grants in that file) — never
    /// gated on <c>OwnershipAuthority.Active</c> itself, unlike the shedding decision that consumes it: frame
    /// time is a real, continuous server property regardless of whether O6 happens to be claiming anything
    /// right now, and gating this half on that flag would let a stale dwell timer from a long
    /// OwnershipAuthority-off window falsely look "continuously satisfied" the instant it turns back on.
    /// </summary>
    internal static class FrameBudget
    {
        // --- Config (final.md §7: "margin shape mirrors O9's RttMarginMs" / hysteresis "mirrors O9's RttDwellSeconds") ---

        private static ConfigEntry<float> s_shedEnterMs;
        private static ConfigEntry<float> s_shedExitMs;
        private static ConfigEntry<float> s_shedEnterDwellSeconds;
        private static ConfigEntry<float> s_shedExitDwellSeconds;

        /// <summary>Sticky state — see class remarks. False (not over budget) until proven otherwise; a fresh
        /// boot's first few frames (before <c>FrameTiming</c> has any real data, <c>RecentMeanMs==0</c>) never
        /// falsely trip this, since <see cref="Tick"/> treats a non-positive reading as "no data yet", not
        /// "instantly fine".</summary>
        internal static bool IsOverBudget { get; private set; }

        // Dwell timers: wall-clock (Time.realtimeSinceStartup, matching O9's own RunReassignmentPass/DwellState
        // convention in LatencyAwareOwnership.cs), reset to -1 ("not currently qualifying") whenever the
        // condition being timed isn't true THIS tick, so a brief dip below/above threshold restarts the dwell
        // requirement rather than letting two separate short excursions silently add up into one continuous one.
        private static float s_enterQualifyingSinceRealtime = -1f;
        private static float s_exitQualifyingSinceRealtime = -1f;

        /// <summary>
        /// Called once per heartbeat frame. Cheap: a handful of float compares, plus — at most once per real
        /// ENTER/EXIT transition — one log line and a couple of telemetry writes.
        /// </summary>
        internal static void Tick()
        {
            double meanMs = NoodlesMcDoodles.Core.FrameTiming.RecentMeanMs;
            if (meanMs <= 0) return; // not yet authoritative, or no frame recorded yet -- nothing to evaluate

            float enterMs = Mathf.Clamp(s_shedEnterMs.Value, 20f, 500f);
            float exitMs = Mathf.Clamp(s_shedExitMs.Value, 20f, 500f);
            // Guards the PAIR, not just each value individually (the config's own AcceptableValueRange already
            // does that): an operator setting ExitMs above EnterMs would otherwise permanently invert the
            // hysteresis (once tripped, "below ExitMs" could never happen if ExitMs > EnterMs and the server is
            // hovering in between) with no way to recover short of another config edit.
            if (exitMs > enterMs) exitMs = enterMs;

            float enterDwell = Mathf.Clamp(s_shedEnterDwellSeconds.Value, 1f, 30f);
            float exitDwell = Mathf.Clamp(s_shedExitDwellSeconds.Value, 1f, 30f);
            float now = Time.realtimeSinceStartup;

            if (!IsOverBudget)
            {
                if (meanMs < enterMs) { s_enterQualifyingSinceRealtime = -1f; return; }
                if (s_enterQualifyingSinceRealtime < 0f) { s_enterQualifyingSinceRealtime = now; return; }
                if (now - s_enterQualifyingSinceRealtime < enterDwell) return;

                IsOverBudget = true;
                s_enterQualifyingSinceRealtime = -1f;
                s_exitQualifyingSinceRealtime = -1f;
                Telemetry.GetCounter("ownership.loadshed.entered").Increment();
                Telemetry.SetTag("loadshed_state", "on");
                NoodlesLog.Warning(NoodlesLogLevel.Normal, $"O-LoadShed: server frame-mean ({meanMs:F0}ms) stayed above ShedEnterMs " +
                    $"({enterMs:F0}ms) for {enterDwell:F0}s -- entering load-shedding: near-field SAA claiming " +
                    "will start handing the busiest held zones back to vanilla until frame time recovers.");
            }
            else
            {
                if (meanMs > exitMs) { s_exitQualifyingSinceRealtime = -1f; return; }
                if (s_exitQualifyingSinceRealtime < 0f) { s_exitQualifyingSinceRealtime = now; return; }
                if (now - s_exitQualifyingSinceRealtime < exitDwell) return;

                IsOverBudget = false;
                s_enterQualifyingSinceRealtime = -1f;
                s_exitQualifyingSinceRealtime = -1f;
                Telemetry.GetCounter("ownership.loadshed.exited").Increment();
                Telemetry.SetTag("loadshed_state", "off");
                NoodlesLog.Info($"O-LoadShed: server frame-mean ({meanMs:F0}ms) stayed below ShedExitMs " +
                    $"({exitMs:F0}ms) for {exitDwell:F0}s -- exiting load-shedding: shed zones become eligible " +
                    "for near-field SAA claiming again (actual re-claim pace still governed by O6's own " +
                    "MaxZonesClaimedPerCycle).");
            }
        }

        /// <summary>
        /// Puts the hysteresis state machine back to a known-clean baseline — called from
        /// <see cref="LoadShedder.ClearAllState"/> (this module's own kill-switch flush). Not itself registered
        /// with the kill switch (this file holds no ownership state of any consequence on its own; it is purely
        /// a derived signal), but exposed so the one file that DOES hold real state can reset both together.
        /// </summary>
        internal static void ResetState()
        {
            IsOverBudget = false;
            s_enterQualifyingSinceRealtime = -1f;
            s_exitQualifyingSinceRealtime = -1f;
        }

        [NoodlesModuleInit]
        internal static void Init()
        {
            ConfigFile config = NoodlesConfig.File;

            s_shedEnterMs = config.Bind("OwnershipAuthority.LoadShed", "ShedEnterMs", 100f,
                new ConfigDescription("WP-D (scratchpad/saa/final.md) -- server frame-mean " +
                    "(server_frame_ms_mean / FrameTiming.RecentMeanMs) threshold, held continuously for " +
                    "ShedEnterDwellSeconds, before near-field SAA claiming starts shedding held zones back to " +
                    "vanilla. No vanilla equivalent. Margin shape mirrors O9's RttMarginMs. NEEDS-LIVE-TEST " +
                    "under real Proxima density (final.md §9: 'frame-mean under real density with FarOrShared " +
                    "armed -- the one number neither grounding nor either critic could produce') -- a reasoned " +
                    "starting point, not yet measured against a populated server's own idle frame time.",
                    new AcceptableValueRange<float>(20f, 500f)));

            s_shedExitMs = config.Bind("OwnershipAuthority.LoadShed", "ShedExitMs", 60f,
                new ConfigDescription("Recovery threshold -- frame-mean must drop BELOW this, held continuously " +
                    "for ShedExitDwellSeconds, before shed zones become claimable again. Lower than ShedEnterMs " +
                    "by design (hysteresis, prevents flapping right at one boundary); clamped at runtime to " +
                    "never exceed ShedEnterMs even if misconfigured.",
                    new AcceptableValueRange<float>(20f, 500f)));

            s_shedEnterDwellSeconds = config.Bind("OwnershipAuthority.LoadShed", "ShedEnterDwellSeconds", 4f,
                new ConfigDescription("How long frame-mean must stay continuously above ShedEnterMs before " +
                    "load-shedding actually engages. No vanilla equivalent. Mirrors O9's RttDwellSeconds shape.",
                    new AcceptableValueRange<float>(1f, 30f)));

            s_shedExitDwellSeconds = config.Bind("OwnershipAuthority.LoadShed", "ShedExitDwellSeconds", 8f,
                new ConfigDescription("How long frame-mean must stay continuously below ShedExitMs before " +
                    "load-shedding disengages. Longer than ShedEnterDwellSeconds by design -- recovering is " +
                    "never urgent, and a longer exit dwell avoids re-claiming a zone only to shed it again a " +
                    "few seconds later on a noisy frame-time signal.",
                    new AcceptableValueRange<float>(1f, 30f)));
        }
    }
}
