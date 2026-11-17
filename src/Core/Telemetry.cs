using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

namespace NoodlesMcDoodles.Core
{
    /// <summary>Thread-safe process-lifetime counter (<see cref="System.Threading.Interlocked"/>-backed).</summary>
    internal sealed class Counter
    {
        private long _value;
        public long Value => System.Threading.Interlocked.Read(ref _value);
        public void Increment() => System.Threading.Interlocked.Increment(ref _value);
        public void Add(long n) => System.Threading.Interlocked.Add(ref _value, n);
    }

    /// <summary>
    /// Thread-safe per-peer counter, keyed by <see cref="ZNetPeer"/> object reference — same rationale as
    /// <see cref="PeerState"/>: a reconnect is a new object, so there's nothing stale to carry over.
    /// <see cref="ResetEachPeriod"/> matches doc 02's "reset each telemetry period" gauges (e.g. a per-period
    /// high-water mark) vs. the default lifetime-cumulative counters (e.g. a near-miss tally).
    /// </summary>
    internal sealed class PerPeerCounter
    {
        public bool ResetEachPeriod { get; }
        private readonly ConcurrentDictionary<ZNetPeer, long> _values = new ConcurrentDictionary<ZNetPeer, long>();

        internal PerPeerCounter(bool resetEachPeriod) => ResetEachPeriod = resetEachPeriod;

        public void Increment(ZNetPeer peer) => _values.AddOrUpdate(peer, 1, (_, v) => v + 1);
        public void Max(ZNetPeer peer, long candidate) => _values.AddOrUpdate(peer, candidate, (_, v) => candidate > v ? candidate : v);
        /// <summary>Last-observed per-peer value (a gauge, e.g. the send rate currently set for that connection).</summary>
        public void Set(ZNetPeer peer, long value) => _values[peer] = value;
        public void Remove(ZNetPeer peer) => _values.TryRemove(peer, out _);
        /// <summary>This peer's current value, 0 when it has none (1.0.3: LinkStatsRpc reads single peers).</summary>
        public long Get(ZNetPeer peer) => peer != null && _values.TryGetValue(peer, out long v) ? v : 0;
        internal ICollection<KeyValuePair<ZNetPeer, long>> Snapshot() => _values.ToArray();
        internal void ResetAll() => _values.Clear();
    }

    /// <summary>
    /// Shared telemetry registry every module writes into and Core alone flushes — one periodic
    /// <c>LogInfo</c> line per <c>Telemetry.LogIntervalSeconds</c>, formatted
    /// <c>"[NoodlesMcDoodles] telemetry key=value ..."</c> (doc 02 Hook 6), grep-able from
    /// <c>LogOutput.log</c>/<c>valheim_server.log</c> (see <see cref="NoodlesLog"/> for why <c>ZLog</c> reaches
    /// both files). A module never starts its own timer — it calls <see cref="Counter"/>/
    /// <see cref="PerPeerCounter"/>/<see cref="SetGauge"/>/<see cref="SetTag"/> with its own dotted or
    /// snake_case name whenever something happens, and <see cref="FlushAndLog"/> (driven by
    /// <see cref="NoodlesHeartbeat"/>, main-thread only) picks every registered name up automatically.
    /// 1.1.0: that full line reaches the main log only at LogLevel=Verbose (detail.log every period otherwise);
    /// Normal shows a one-line <c>status: ...</c> summary instead, while at least one player is connected.
    /// </summary>
    internal static class Telemetry
    {
        private static readonly ConcurrentDictionary<string, Counter> s_counters = new ConcurrentDictionary<string, Counter>();
        private static readonly ConcurrentDictionary<string, PerPeerCounter> s_perPeerCounters = new ConcurrentDictionary<string, PerPeerCounter>();
        private static readonly ConcurrentDictionary<string, double> s_gauges = new ConcurrentDictionary<string, double>();
        private static readonly ConcurrentDictionary<string, string> s_tags = new ConcurrentDictionary<string, string>();

        // 1.0.10 (claim-pass redesign): a module OUTSIDE Core (e.g. Ownership/ClaimScanner) that wants its own
        // "value since last flush" gauge -- the exact same shape as FrameTiming.PublishAndReset just below,
        // which Core calls directly only because FrameTiming happens to live here too -- registers a callback
        // here instead of Core needing a compile-time reference to that module. Never a second periodic driver
        // (CORE-API.md): every callback rides this same FlushAndLog call, at the same cadence.
        private static readonly List<Action> s_periodicPublishers = new List<Action>();

        /// <summary>
        /// Registers a callback invoked every <see cref="FlushAndLog"/>, right alongside
        /// <see cref="FrameTiming.PublishAndReset"/> -- for a module outside Core that keeps its own
        /// high-water-mark/rate/sum gauge and wants it published and reset exactly once per telemetry period.
        /// The callback is expected to call <see cref="SetGauge"/> itself and reset its own accumulator; a
        /// throwing callback is caught and logged so one module's bug can't blank the rest of this flush.
        /// </summary>
        public static void RegisterPeriodicPublisher(Action publish)
        {
            lock (s_periodicPublishers) s_periodicPublishers.Add(publish);
        }

        /// <summary>Get-or-create a named global counter. Safe to call from any thread, any time (including before the first flush).</summary>
        public static Counter GetCounter(string name) => s_counters.GetOrAdd(name, _ => new Counter());

        /// <summary>Get-or-create a named per-peer counter. <paramref name="resetEachPeriod"/> is fixed at first registration.</summary>
        public static PerPeerCounter GetPerPeerCounter(string name, bool resetEachPeriod = false) =>
            s_perPeerCounters.GetOrAdd(name, _ => new PerPeerCounter(resetEachPeriod));

        /// <summary>Last-observed numeric value (e.g. "the most recent save took this long"), not a running total.</summary>
        public static void SetGauge(string name, double value) => s_gauges[name] = value;

        /// <summary>Last-observed string state (e.g. kill_switch=on/off, auto_standdown=&lt;reason&gt;).</summary>
        public static void SetTag(string key, string value) => s_tags[key] = value;

        /// <summary>
        /// Called from every peer-disconnect path (see <see cref="PeerState"/>'s cleanup) so a per-peer
        /// counter never accumulates an entry for a peer that no longer exists.
        /// </summary>
        public static void RemovePeerFromAllCounters(ZNetPeer peer)
        {
            foreach (var pc in s_perPeerCounters.Values) pc.Remove(peer);
        }

        private static bool s_zdoMismatchExceededLastPeriod;

        // BUG FIX (integrator pass, reproduced live on the testbed): doc 02 line 237 is explicit that
        // "zdo_delta (created minus destroyed) [is] since the counter class's own last reset — not since
        // world start" and that trigger #1 should fire on drift that is sustained *independent of population*,
        // not on a one-time cumulative-since-boot total. The original implementation compared
        // GetCounter("zdos_created")-GetCounter("zdos_destroyed") directly — both raw process-lifetime
        // Counters that are NEVER reset — so zdo_delta was cumulative-since-process-start, not
        // since-last-reset. Reproduced on an empty (zero-player) testbed boot: world generation alone creates
        // ~80 ZDOs (location spawners etc.) with nothing to destroy without a player present, so the
        // cumulative delta exceeded the default tolerance (50) in the very first telemetry period and stayed
        // exceeded (nothing ever brings a lifetime-cumulative total back down) into the second, auto-tripping
        // Core.Enabled=false on a totally idle server within 2 minutes of every boot — which would do the same
        // on any populated server the moment normal, permanent world growth (built structures, terrain edits,
        // tamed animals — ZDOs that are legitimately created and correctly never destroyed) exceeds 50 net,
        // typically within the first couple of minutes of real play. Fixed by tracking a per-period baseline
        // (the actual "last reset") and feeding the mismatch check the delta since THAT, not since boot; the
        // raw cumulative zdos_created/zdos_destroyed counters in the log line are untouched (still lifetime
        // totals, as doc 02 line 409 lists them) — only the derived zdo_delta / AutoStandDown trigger #1 input
        // changes.
        private static long s_zdoDeltaBaselineCreated;
        private static long s_zdoDeltaBaselineDestroyed;

        /// <summary>Main-thread only (reads <see cref="Time.realtimeSinceStartup"/> and every <see cref="ZNetPeer"/>'s uid).</summary>
        public static void FlushAndLog()
        {
            MainThreadDispatcher.AssertMainThread();

            SetTag("kill_switch", KillSwitch.Enabled ? "on" : "off");
            FrameTiming.PublishAndReset();
            lock (s_periodicPublishers)
            {
                foreach (Action publish in s_periodicPublishers)
                {
                    try { publish(); }
                    catch (Exception e) { NoodlesLog.PatchException("Telemetry.RegisterPeriodicPublisher callback", e); }
                }
            }
            UpdateDeploymentModeTag();

            // 1.1.0: the compact status line is built first, from the same period's values the full line uses
            // (FrameTiming was just published; per-period per-peer counters are reset only below).
            string status = null;
            try { status = BuildStatusLine(); }
            catch (Exception e) { NoodlesLog.PatchException("Telemetry.BuildStatusLine", e); }

            var sb = new StringBuilder("telemetry t=");
            sb.Append(Time.realtimeSinceStartup.ToString("F1", CultureInfo.InvariantCulture));

            foreach (var kv in s_counters.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value.Value);

            long created = GetCounter("zdos_created").Value;
            long destroyed = GetCounter("zdos_destroyed").Value;
            // Since-last-reset delta (doc 02 line 237), not since-process-start: subtract off the baseline
            // captured at the end of the previous flush, then advance the baseline to now.
            long delta = (created - s_zdoDeltaBaselineCreated) - (destroyed - s_zdoDeltaBaselineDestroyed);
            s_zdoDeltaBaselineCreated = created;
            s_zdoDeltaBaselineDestroyed = destroyed;
            sb.Append(" zdo_delta=").Append(delta >= 0 ? "+" : "").Append(delta);

            foreach (var gaugeKv in s_gauges.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.Append(' ').Append(gaugeKv.Key).Append('=').Append(gaugeKv.Value.ToString("F1", CultureInfo.InvariantCulture));

            foreach (var pcKv in s_perPeerCounters.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                foreach (var peerValue in pcKv.Value.Snapshot())
                {
                    long uid = peerValue.Key != null ? peerValue.Key.m_uid : 0;
                    sb.Append(' ').Append(pcKv.Key).Append("_peer").Append(uid).Append('=').Append(peerValue.Value);
                }
                if (pcKv.Value.ResetEachPeriod) pcKv.Value.ResetAll();
            }

            foreach (var tagKv in s_tags.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.Append(' ').Append(tagKv.Key).Append('=').Append(tagKv.Value);

            // 1.1.0: the full key=value line (the exact `[NoodlesMcDoodles] telemetry t=...` line 1.0.x printed) is
            // main-log at LogLevel=Verbose only and goes to detail.log every period otherwise; the short status line
            // is what Normal shows (and only while a player is connected -- BuildStatusLine returns null otherwise).
            if (status != null) NoodlesLog.Info(status);
            NoodlesLog.Verbose(sb.ToString());

            CheckZdoMismatchAutoStandDown(delta);
        }

        private static long s_statusLinkStallsBaseline;
        private static long s_statusPatchExceptionsBaseline;

        /// <summary>
        /// One short line for an admin, answering "are fast players being held back / is the server choking": who is
        /// connected, what the server is sending, the per-player send-rate range and how many are still at vanilla's
        /// rate, frame time this period, link stalls and caught patch exceptions since the previous flush.
        /// Server (dedicated or host) only, and null while nobody is connected. Main-thread only (called from
        /// <see cref="FlushAndLog"/>). Example:
        /// <c>status: 3 players (2 modded) · sending 210 KiB/s · rate 150-256 KiB/s per player, 1 at vanilla · frame 33 ms avg / 104 ms max · 1 link stall · 0 patch errors</c>
        /// </summary>
        private static string BuildStatusLine()
        {
            // Deltas advance every flush, shown or not, so each status line covers exactly one period.
            long stallsNow = GetCounter("transport.linkStalls").Value;
            long patchExNow = Counters.PatchExceptions.Value;
            long stalls = stallsNow - s_statusLinkStallsBaseline;
            long patchEx = patchExNow - s_statusPatchExceptionsBaseline;
            s_statusLinkStallsBaseline = stallsNow;
            s_statusPatchExceptionsBaseline = patchExNow;

            if (ZNet.instance == null || !ZNet.instance.IsServer()) return null;
            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            int players = peers?.Count ?? 0;
            if (players == 0) return null;

            var sb = new StringBuilder("status: ");
            sb.Append(players).Append(players == 1 ? " player" : " players");
            if (s_gauges.TryGetValue("peers_modded", out double modded))
                sb.Append(" (").Append(((long)modded).ToString(CultureInfo.InvariantCulture)).Append(" modded)");

            // T9 publishes these only while it manages at least one connection.
            if (s_gauges.TryGetValue("transport_t9_managed", out double managed) && managed > 0)
            {
                if (s_gauges.TryGetValue("transport_t9_sent_bps", out double sentBps))
                    sb.Append(" · sending ").Append(KiB(sentBps)).Append(" KiB/s");

                if (s_perPeerCounters.TryGetValue("transport_t9_rate_bps", out PerPeerCounter rates))
                {
                    long min = long.MaxValue, max = 0;
                    int atVanilla = 0;
                    foreach (var kv in rates.Snapshot())
                    {
                        if (kv.Value <= 0) continue;
                        if (kv.Value < min) min = kv.Value;
                        if (kv.Value > max) max = kv.Value;
                        if (kv.Value <= NoodlesMcDoodles.Transport.SteamRateController.VanillaGlobalRateBps) atVanilla++;
                    }
                    if (max > 0)
                    {
                        sb.Append(" · rate ").Append(KiB(min));
                        if (max != min) sb.Append('-').Append(KiB(max));
                        sb.Append(" KiB/s per player, ").Append(atVanilla).Append(" at vanilla");
                    }
                }
            }

            if (s_gauges.TryGetValue("server_frame_ms_mean", out double frameMean) &&
                s_gauges.TryGetValue("server_frame_ms_max", out double frameMax))
            {
                sb.Append(" · frame ").Append(frameMean.ToString("F0", CultureInfo.InvariantCulture))
                  .Append(" ms avg / ").Append(frameMax.ToString("F0", CultureInfo.InvariantCulture)).Append(" ms max");
            }

            int stalledNow = 0;
            if (s_perPeerCounters.TryGetValue("transport_link_stalled", out PerPeerCounter stalled))
            {
                foreach (var kv in stalled.Snapshot()) if (kv.Value > 0) stalledNow++;
            }
            sb.Append(" · ").Append(stalls).Append(stalls == 1 ? " link stall" : " link stalls");
            if (stalledNow > 0) sb.Append(" (").Append(stalledNow).Append(" stalled now)");

            sb.Append(" · ").Append(patchEx).Append(patchEx == 1 ? " patch error" : " patch errors");

            if (s_tags.TryGetValue("loadshed_state", out string shed) && shed == "on") sb.Append(" · LOAD-SHEDDING");
            if (!KillSwitch.Enabled) sb.Append(" · kill switch OFF");
            return sb.ToString();
        }

        private static string KiB(double bytesPerSecond) =>
            (bytesPerSecond / 1024.0).ToString("F0", CultureInfo.InvariantCulture);

        /// <summary>
        /// Server-side summary of the whole deployment (NEW USER REQUIREMENT, 2026-09-28): a coarse
        /// <c>deployment_mode</c> tag plus <c>peers_modded</c>/<c>peers_vanilla</c>/<c>peers_pending</c> gauges,
        /// recomputed fresh every flush from <see cref="PeerState"/> — the same source of truth the per-peer log
        /// lines in <see cref="DeploymentLog"/> use, so the periodic summary and the per-transition lines can
        /// never disagree. No-ops (sets nothing) on a client or before <c>ZNet.instance</c> exists — this tag
        /// describes the SERVER's own population, not a client's one connection.
        ///
        /// L5 FIX (deployment-modes review, 2026-09-28): a peer still inside its handshake window is neither
        /// <c>Negotiated</c> nor <c>KnownVanilla</c> yet — the original version counted it in <c>total</c> but
        /// not in <c>modded</c>, so <c>modded == total</c> could be false (label "mixed") purely because a
        /// perfectly ordinary, still-negotiating peer hadn't settled yet, even when every ALREADY-settled peer
        /// was modded. The label is now derived from settled peers only (<c>peers_pending</c> exposes the rest
        /// separately, never folded into the label itself).
        ///
        /// STALE-PEER FIX (live-server telemetry leak, 2026-09-29 runtime review): this used to iterate
        /// <see cref="PeerState.Snapshot"/> directly — the WHOLE dictionary, trusting every entry in it was
        /// still connected. It wasn't a safe assumption: <see cref="PeerState"/>'s own disconnect cleanup patch
        /// turned out to have never been registered with <see cref="PatchRegistry"/> (see <c>PeerState.Init</c>),
        /// so a peer's entry outlived the peer itself for the rest of the process — live, this is exactly why
        /// <c>peers_vanilla=1.0</c> (and, earlier, <c>peers_modded=1</c>) kept reporting long after the peer's
        /// "Disposing socket"/"Closing socket &lt;steamid&gt;" and never came back down. Now fixed at the root
        /// (the patch is registered) AND, independently, here: this walks <c>ZNet.instance.GetPeers()</c> — the
        /// server's own live peer list — and looks each one up in <see cref="PeerState"/>, instead of trusting
        /// the dictionary's membership at all. A leaked/stale entry for a peer that's no longer connected is
        /// then simply never visited, regardless of whether anything ever removes it from the dictionary.
        /// </summary>
        private static void UpdateDeploymentModeTag()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            int total = peers?.Count ?? 0;
            int modded = 0, vanilla = 0;
            if (peers != null)
            {
                foreach (ZNetPeer peer in peers)
                {
                    // No entry yet (still mid-handshake, or negotiation is disabled) falls through uncounted here —
                    // it's still reflected below, via `pending = total - settled`, same as before this fix.
                    if (!PeerState.TryGet(peer, out PeerState state)) continue;
                    if (state.Negotiated) modded++;
                    else if (state.KnownVanilla) vanilla++;
                }
            }
            int settled = modded + vanilla;
            // Defensive floor only: `total` and the modded/vanilla loop above now both come from this SAME
            // `peers` read (previously two separate reads — PeerState.Snapshot() and ZNet.GetPeers() — really
            // could disagree at the instant either was taken); kept in case a future edit reintroduces a second read.
            int pending = Math.Max(0, total - settled);

            string mode = total == 0 ? "idle"
                : settled == 0 ? "pending" // every connected peer is still mid-handshake -- nothing settled to label yet
                : modded == settled ? "server+client" // every SETTLED peer negotiated -- any peers_pending are just not there yet
                : modded == 0 ? "server-only"
                : "mixed";

            SetTag("deployment_mode", mode);
            SetGauge("peers_modded", modded);
            SetGauge("peers_vanilla", vanilla);
            SetGauge("peers_pending", pending);
        }

        /// <summary>
        /// AutoStandDown trigger #1 (doc 02 Hook 5): a sustained |created-destroyed| drift beyond
        /// <c>Core.AutoStandDown.ZdoMismatchTolerance</c> across TWO CONSECUTIVE telemetry periods trips the
        /// kill switch. "Two consecutive" is implemented literally: this period's excess must follow last
        /// period's excess with no in-between period back under tolerance.
        /// </summary>
        private static void CheckZdoMismatchAutoStandDown(long delta)
        {
            if (!NoodlesConfig.AutoStandDownEnabled.Value || !NoodlesConfig.AutoStandDownZdoDeltaTrigger.Value)
            {
                s_zdoMismatchExceededLastPeriod = false;
                return;
            }

            long tolerance = NoodlesConfig.AutoStandDownZdoMismatchTolerance.Value;
            bool exceeds = Math.Abs(delta) > tolerance;

            if (exceeds && s_zdoMismatchExceededLastPeriod)
            {
                KillSwitch.SetEnabled(false,
                    $"ZDO created/destroyed delta ({delta}) exceeded tolerance ({tolerance}) for two consecutive " +
                    "telemetry periods", automatic: true);
                s_zdoMismatchExceededLastPeriod = false; // don't re-fire every subsequent period once already off
            }
            else
            {
                s_zdoMismatchExceededLastPeriod = exceeds;
            }
        }

        /// <summary>
        /// Core's own well-known counters (doc 02's Telemetry section), exposed as readable properties —
        /// all backed by the same generic registry above, so a `grep` for the snake_case name finds them
        /// whether accessed through here or through <see cref="Telemetry.GetCounter(string)"/> directly.
        /// </summary>
        public static class Counters
        {
            public static Counter HandshakeSent => Telemetry.GetCounter("handshake_sent");
            public static Counter HandshakeNegotiated => Telemetry.GetCounter("handshake_ok");
            public static Counter HandshakeRejectedReentry => Telemetry.GetCounter("handshake_rejected_reentry");
            public static Counter HandshakeVersionMismatch => Telemetry.GetCounter("handshake_version_mismatch");
            public static Counter HandshakeTimedOutAsVanilla => Telemetry.GetCounter("handshake_timeout_vanilla");

            public static Counter ConfigSyncPushed => Telemetry.GetCounter("configsync_pushed");
            public static Counter ConfigSyncRegisterFailed => Telemetry.GetCounter("configsync_register_failed");

            public static Counter ZdosCreated => Telemetry.GetCounter("zdos_created");
            public static Counter ZdosDestroyed => Telemetry.GetCounter("zdos_destroyed");

            public static PerPeerCounter SendBudgetNearMisses => Telemetry.GetPerPeerCounter("send_budget_near_miss");
            public static PerPeerCounter SteamSendQueueHighWaterBytes => Telemetry.GetPerPeerCounter("steam_sendq_hw", resetEachPeriod: true);

            public static Counter SavesCompleted => Telemetry.GetCounter("saves_completed");
            public static Counter SavesSkipped => Telemetry.GetCounter("saves_skipped");

            public static Counter SafeToggleEnables => Telemetry.GetCounter("safetoggle_enables");
            public static Counter SafeToggleRefusedDiskSpace => Telemetry.GetCounter("safetoggle_refused_disk");
            public static Counter SafeToggleRefusedSkippedSave => Telemetry.GetCounter("safetoggle_refused_skipped_save");

            public static Counter PatchExceptions => Telemetry.GetCounter("patch_exceptions");
            public static Counter AutoStandDownEvents => Telemetry.GetCounter("autostanddown_events");

            public static Counter OversizedZdoWriteRejected => Telemetry.GetCounter("oversized_zdo_write_rejected");
        }
    }
}
