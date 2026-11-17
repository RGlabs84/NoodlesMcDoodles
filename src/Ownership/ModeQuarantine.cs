using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using NoodlesMcDoodles.Core;

namespace NoodlesMcDoodles.Ownership
{
    /// <summary>
    /// WP-E (scratchpad/saa/final.md §3/§8) — O11's own independent quarantine lever: "Per-feature quarantine
    /// (Mode auto-downgrades to SharedFar on an exception/veto burst, requires operator action to re-raise) --
    /// ... sequenced strictly after the safety-net spec's generic Core primitive." <c>Core.FeatureStandDown</c>
    /// is that generic primitive (a rolling exception-count window per FEATURE, tripping <c>ForceOff</c>); this
    /// file is "a dedicated instance of the same shape", not a call into <c>FeatureStandDown</c> itself:
    /// <c>NearFieldClaimVeto</c>/<c>NearFieldClaimPolicy</c> deliberately have no <c>FeatureFlag</c> of their
    /// own (by design -- see <c>NearFieldClaimPolicy.cs</c>'s own class remarks), so the only EXISTING lever a
    /// real exception/veto burst there could reach is <c>FeatureStandDown</c>'s per-patch-class tracking under
    /// the shared "OwnershipAuthority" feature name -- which would force off ALL of OwnershipAuthority (O6,
    /// far-band included) on a burst, not just narrow near-field claiming back to SharedFar. This file gives
    /// O11 its own, narrower backstop instead.
    ///
    /// <b>Mechanism</b>: <see cref="ForceSharedFar"/> is backed by a real <c>FeatureFlag</c> (registered under
    /// <see cref="FlagName"/>, "ModeQuarantine") purely for its existing ForceOff/ClearForcedOff/IsForcedOff
    /// latch -- this flag gates no patch of its own, and its own <c>Active</c> is never read by this file
    /// (see <see cref="Tick"/>'s own gating, which reads <c>ConfiguredOn</c> instead -- <c>Active</c> would go
    /// false the instant this trips, which would stop this file from ever updating its own delta baselines
    /// again, defeating a clean "noodles clear" recovery). Reusing <c>FeatureFlag</c> this way means "an
    /// operator's 'noodles clear' re-raises it" (<c>NearFieldClaimPolicy.cs</c>'s own config text and startup
    /// log line, written well ahead of this file existing) is already true for free: <c>AdminCommand.HandleClear</c>
    /// looks up any registered flag BY NAME, so "noodles clear ModeQuarantine" works with zero changes to that
    /// file.
    ///
    /// <b>Trip signals</b> (<see cref="Tick"/>, polled once per heartbeat frame via <see cref="LoadShedder.Tick"/>'s
    /// own fan-out -- a sample-the-delta design, not a push callback, since <c>NearFieldClaimVeto</c>/
    /// <c>ZoneClaimSweep</c>/<c>SetOwnerHoldGuard</c> are out of this task's file scope and already increment
    /// the counters below unconditionally, for other reasons, today):
    /// <list type="bullet">
    /// <item><c>ownership.o11.playerZdoClaimAttempted</c> (<c>NearFieldClaimPolicy.cs</c>'s own comment: "the
    /// one counter that must stay 0... a loss-risk SIGNAL, not a routine metric") -- ANY growth at all trips
    /// immediately, no window, no threshold. This one is never routine.</item>
    /// <item><c>ownership.setOwnerVetoed</c> (<c>SetOwnerHoldGuard.cs</c>'s <c>AuthorizedSetOwner</c>
    /// postcondition check, plus <c>ClaimScanner.cs</c>'s own claim verification -- "an authorized
    /// write silently didn't take") -- a rolling count over <see cref="VetoBurstWindowSeconds"/>, mirroring
    /// <c>FeatureStandDown</c>'s own queue-of-timestamps shape, tripping past <see cref="VetoBurstLimit"/>.
    /// NOTE: this counter is shared with O9/O10's own <c>AuthorizedSetOwner</c>-mediated writes (both route
    /// through the identical postcondition check) -- a burst driven purely by O9 activity, with OwnershipAuthority
    /// (O6) itself never near-field-claiming anything, can still trip this. Accepted deliberately: this file
    /// only ticks at all while <c>OwnershipAuthority.Active</c> is true (see <see cref="Tick"/>'s own
    /// early-out), and "retreat Mode to SharedFar on ANY elevated SetOwner-veto rate, even one this file can't
    /// attribute precisely to O6 vs O9/O10" is the more conservative of the two possible readings of "robust
    /// error correction" -- not a bug. 1.0.12: a refused hand-back to nobody (newUid 0: any release) is NOT counted
    /// here -- <c>AuthorizedSetOwner</c> routes it to <c>ownership.o11.releaseRefusedByHolder</c>. A mod holding an
    /// object on purpose refusing to give it back is not a mod fighting near-field claiming, and this feature's
    /// retreat to SharedFar cannot make it let go (live Proxima 2026-10-01 14:38: 57 refused releases of harvested
    /// onions in 60 s tripped this and left the server in SharedFar for a day). Refused claims and handovers still count.</item>
    /// </list>
    /// Deliberately does NOT treat <c>NearFieldClaimPolicy</c>'s own per-reason veto counters
    /// (<c>ownership.o11.vetoed.player</c> / <c>.pickableNearPlayer</c> / <c>.compatExcluded</c> /
    /// <c>.clientClaimGrace</c> / <c>.modeNotAllowed</c> / <c>.shipClaimDisabled</c>) as burst signal -- those
    /// are O11's OWN policy doing exactly what it is designed to do (a live player nearby, a pickable near one,
    /// Mode's own class allowlist, ...); a busy, healthy, heavily-populated FarOrShared server produces MANY of
    /// these by construction, and treating their growth as trouble would punish the feature for working.
    /// </summary>
    internal static class ModeQuarantine
    {
        internal const string FlagName = "ModeQuarantine";

        // --- Config ------------------------------------------------------------------------------------------

        private static ConfigEntry<bool> s_active;
        private static ConfigEntry<int> s_vetoBurstLimit;
        private static ConfigEntry<float> s_vetoBurstWindowSeconds;

        private static FeatureFlag s_flag;

        /// <summary>Read by <c>NearFieldClaimPolicy.EffectiveMode</c> every zone/object this module evaluates.
        /// Cheap (one bool read via <c>FeatureFlag.IsForcedOff</c>) -- see class remarks for why this is a
        /// FeatureFlag's own latch rather than a private field here.</summary>
        internal static bool ForceSharedFar => s_flag != null && s_flag.IsForcedOff;

        // --- Rolling veto-burst window ---------------------------------------------------------------------
        //
        // Plain Queue<DateTime>, NOT ConcurrentQueue (unlike FeatureStandDown's own queue): Tick() only ever
        // runs from NoodlesHeartbeat.Update() (main thread, via LoadShedder.Tick()'s own fan-out) -- there is
        // no off-thread caller here the way a raw Harmony-patch exception catch block can be, so the extra
        // concurrency safety FeatureStandDown needs for ITS queue would be pure overhead on this one.

        private static readonly Queue<DateTime> s_vetoTimestamps = new Queue<DateTime>();
        private static long s_lastSetOwnerVetoedValue;
        private static long s_lastPlayerZdoClaimAttemptedValue;
        private static bool s_baselineInitialized;

        /// <summary>
        /// Called once per heartbeat frame (via <see cref="LoadShedder.Tick"/>'s own fan-out -- the one new
        /// call site this task's file scope grants, in a DIFFERENT owned file). Cheap: two counter reads and,
        /// ordinarily, nothing else -- the timestamp queue is only touched when <c>ownership.setOwnerVetoed</c>
        /// actually moved since the last tick.
        /// </summary>
        internal static void Tick()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!Features_Ownership.OwnershipAuthority.Active) return; // nothing this file protects is even running otherwise -- see class remarks on why this, not .Active on this file's own flag, is the gate
            if (s_flag == null || !s_flag.ConfiguredOn) return;

            long claimAttempted = Telemetry.GetCounter("ownership.o11.playerZdoClaimAttempted").Value;
            long vetoed = Telemetry.GetCounter("ownership.setOwnerVetoed").Value;

            if (!s_baselineInitialized)
            {
                // First live sample -- establish the baseline without judging whatever accumulated before this
                // file started watching (Telemetry counters are lifetime-cumulative by design, see Telemetry.cs
                // remarks, so a fresh boot could otherwise see a nonzero "delta" against an implicit zero baseline).
                s_lastPlayerZdoClaimAttemptedValue = claimAttempted;
                s_lastSetOwnerVetoedValue = vetoed;
                s_baselineInitialized = true;
                return;
            }

            long claimAttemptedDelta = claimAttempted - s_lastPlayerZdoClaimAttemptedValue;
            s_lastPlayerZdoClaimAttemptedValue = claimAttempted; // updated unconditionally, tripped or not, so a stale huge delta can never appear the tick after an operator clears this

            if (claimAttemptedDelta > 0 && !ForceSharedFar)
            {
                Trip($"ownership.o11.playerZdoClaimAttempted grew by {claimAttemptedDelta} -- the near-field " +
                    "claim veto is the only thing standing between O6's bulk claim and a connected player's " +
                    "own avatar ZDO right now; this should never happen at all (see NearFieldClaimPolicy.cs's " +
                    "own remarks) -- quarantining Mode to SharedFar until an operator investigates and runs " +
                    $"'noodles clear {FlagName}'.");
                return; // already tripped this tick -- no need to also evaluate the rolling window below
            }

            long vetoedDelta = vetoed - s_lastSetOwnerVetoedValue;
            s_lastSetOwnerVetoedValue = vetoed; // same unconditional-update reasoning as above

            if (vetoedDelta <= 0 || ForceSharedFar)
            {
                Telemetry.SetGauge("ownership.modeQuarantine.vetoBurstWindowCount", s_vetoTimestamps.Count);
                return;
            }

            DateTime now = DateTime.UtcNow;
            // Caps how many timestamps one tick's delta can enqueue -- bounds worst-case memory/CPU for a
            // single pathological spike; only ever matters once the burst has already, many times over,
            // exceeded the limit tested below, so nothing is lost by not enqueueing the full raw delta.
            int toEnqueue = (int)Math.Min(vetoedDelta, 200L);
            for (int i = 0; i < toEnqueue; i++) s_vetoTimestamps.Enqueue(now);

            float windowSeconds = s_vetoBurstWindowSeconds.Value;
            while (s_vetoTimestamps.Count > 0 && (now - s_vetoTimestamps.Peek()).TotalSeconds > windowSeconds)
            {
                s_vetoTimestamps.Dequeue();
            }
            Telemetry.SetGauge("ownership.modeQuarantine.vetoBurstWindowCount", s_vetoTimestamps.Count);

            int limit = s_vetoBurstLimit.Value;
            if (s_vetoTimestamps.Count > limit)
            {
                Trip($"ownership.setOwnerVetoed burst: {s_vetoTimestamps.Count} in the last {windowSeconds:0}s " +
                    $"(limit {limit}) -- an authorized ownership write silently not taking this often suggests " +
                    "a third-party mod's own SetOwner hook is actively fighting near-field claiming (see " +
                    "SetOwnerHoldGuard.cs class remarks) -- quarantining Mode to SharedFar until an operator " +
                    $"investigates and runs 'noodles clear {FlagName}'.");
            }
        }

        private static void Trip(string reason)
        {
            Telemetry.GetCounter("ownership.modeQuarantine.tripped").Increment();
            s_flag.ForceOff(reason); // logs its own "feature 'ModeQuarantine' forced off: ..." line (FeatureFlag.ForceOff) -- no separate log needed here
            s_vetoTimestamps.Clear(); // nothing more to accumulate once tripped -- a later 'noodles clear' starts the window fresh, not mid-count
        }

        [NoodlesModuleInit]
        internal static void Init()
        {
            ConfigFile config = NoodlesConfig.File;

            // Round-3 install-gate fix: range max 500 -> 5000 (default stays 50). The trips seen on a copy of the live
            // world (FarOrShared -> SharedFar within seconds of every boot) were a COUNTING bug, fixed in
            // ZoneClaimSweep.ClaimZone + NearFieldClaimVeto: our own policy refusals and objects vanilla never tried
            // to hand over were counted as third-party vetoes. With that fixed, the same world logged 0 real vetoes.
            s_vetoBurstLimit = config.Bind("OwnershipAuthority.ModeQuarantine", "VetoBurstLimit", 50,
                new ConfigDescription("ownership.setOwnerVetoed occurrences within VetoBurstWindowSeconds before " +
                    "[3 - Server World] Mode auto-downgrades (effective value only -- see the startup/deployment " +
                    "log line) to SharedFar. No vanilla equivalent. Deliberately much higher than " +
                    "Core.FeatureStandDown's own ExceptionBurstLimit default (3) -- a single silently-vetoed " +
                    "claim or handover is routine and expected whenever GetOffMyLawn/Wonderland (or another " +
                    "installed mod) happens to hold a ZDO this module also touches (see SetOwnerHoldGuard.cs " +
                    "class remarks); this threshold is sized to catch a SUSTAINED fight, not one ordinary " +
                    "overlap. Only claims and handovers another mod actually refused are counted (our own policy " +
                    "refusals, objects vanilla never tried to hand over, and a refused hand-back to nobody -- a mod " +
                    "holding an object on purpose -- are not).",
                    new AcceptableValueRange<int>(5, 5000)));

            s_vetoBurstWindowSeconds = config.Bind("OwnershipAuthority.ModeQuarantine", "VetoBurstWindowSeconds", 60f,
                new ConfigDescription("Rolling window VetoBurstLimit is measured over. Matches Core.AutoStandDown's " +
                    "own global exception-burst window (60s) for a consistent order of magnitude across this " +
                    "mod's several burst detectors.",
                    new AcceptableValueRange<float>(10f, 300f)));

            s_active = config.Bind("OwnershipAuthority.ModeQuarantine", "Active", true,
                new ConfigDescription("WP-E (scratchpad/saa/final.md) -- whether this burst detector watches at " +
                    "all. When it trips, [3 - Server World] Mode's EFFECTIVE value (see the startup/deployment " +
                    "log line) is forced down to SharedFar until an operator runs 'noodles clear ModeQuarantine' " +
                    "-- the raw Mode config value itself is untouched either way. This is also this file's " +
                    "FeatureFlag backing entry (see class remarks) -- turning it off here disables the WATCHER, " +
                    "not any already-tripped quarantine (use 'noodles clear ModeQuarantine' for that). DEFAULT " +
                    "ON: this is O11's only independent quarantine lever -- turning it off removes O6/near-field's " +
                    "own automatic backoff without also touching OwnershipAuthority itself."));

            s_flag = Features.Register(FlagName, s_active);

            // Per-feature quarantine hygiene note. CORRECTED (round-2 review, "quarantine" lens major finding):
            // this flag itself still holds no physical resource of its OWN to release on ForceOff (unlike
            // O6/O9/O10, which hold real zones/ZDOs) -- forcing it off only changes what ForceSharedFar
            // returns, which NearFieldClaimPolicy.EffectiveMode re-reads live on its very next call, so no NEW
            // near-field claim can land from the instant Trip() runs. That much is true and needs no fix.
            //
            // But "nothing to release" is NOT also true of near-field zones O6's near-field pass had ALREADY
            // claimed before the trip -- releasing THOSE depends entirely on a DIFFERENT file's mechanism:
            // ZoneClaimSweep.RunClaimCycle's own release-diffing loop (src/Ownership/ZoneClaimSweep.cs, a file
            // this task does not own), which notices, on its own next cycle, that ComputeCandidateZones no
            // longer lists the zone once EffectiveMode's downgrade makes nearFieldEligible false. Verified
            // against that file this pass: the diffing loop only calls ServerZoneMembership.Release (bookkeeping)
            // -- it never performs an actual per-ZDO ownership release (the OwnershipApi.SetOwner(zdo, 0, ...)
            // pattern ZoneClaimSweep.ReleaseAllHeldZones/ReleaseZoneOwnership already use, correctly, for the
            // unrelated kill-switch/full-force-off path). So a previously near-field-claimed ZDO's raw owner
            // field stays the server's session id, UNCHANGED, until either (a) a connected peer's own TRUE
            // (unpadded, vanilla ~96m) active area naturally reaches it via vanilla's normal per-peer
            // ReleaseNearbyZDOS sweep, or (b) every peer leaves even the WIDER padded band too (up to ~288m),
            // re-qualifying the zone as genuine far-band and letting O6 re-claim (and re-book-keep) it normally
            // regardless of quarantine. A peer who lingers in the gap between those two radii (an ordinary
            // base-building distance) leaves that zone's ZDOs in this limbo for as long as the quarantine
            // stays tripped -- not unsafe for FarOrShared's own routed-RPC/no-transfer classes (no client-
            // visible ownership-transfer effect either way), but slower to actually revert than this comment
            // used to claim, and worth an operator knowing about.
            //
            // No OnForcedOff release callback is wired HERE for that reason: the correct fix is a real per-ZDO
            // release inside ZoneClaimSweep.cs's own diffing loop (mirroring ReleaseZoneOwnership's already-
            // reviewed shape, restricted to zones actually dropping out of near-field eligibility) -- not a
            // second, duplicated release path grown in this file against a private helper this file can't reach
            // (this codebase's own "don't reimplement, avoid drift" discipline applies here too, and a new
            // data-affecting release path with no way to boot-test it this pass is exactly the wrong place to
            // improvise). See the round-2 fixer report for this exact fix, precisely described for whoever
            // owns that file next.
        }
    }
}
