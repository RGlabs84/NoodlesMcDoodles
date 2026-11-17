using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using NoodlesMcDoodles.Transport;
using UnityEngine;

namespace NoodlesMcDoodles.Overlay
{
    /// <summary>
    /// Fills an <see cref="OverlaySnapshot"/> from state the rest of the mod already maintains — no Harmony
    /// patch, no RPC, no ZDO write, nothing this file adds to the world. Every read here is either a public
    /// engine call already used elsewhere in this codebase (<c>ZNetScene.InActiveArea</c>,
    /// <c>ZNetPeer.m_socket.GetConnectionQuality</c> — see e.g. <see cref="Ownership.LatencyAwareOwnership"/> and
    /// <c>probe/NoodlesProbe.cs</c>) or an existing internal flag/state object this mod's own modules expose for
    /// exactly this kind of cross-module read (<see cref="Features"/>, <see cref="PeerState"/>,
    /// <see cref="AdaptiveRate.CurrentRateBps"/>, <see cref="Capabilities"/>, <see cref="Telemetry"/>'s own
    /// per-peer counters — see <see cref="SampleQueueAllowanceNow"/> below).
    ///
    /// Called at most once a second from <see cref="OverlayController.Update"/> — never from OnGUI (OVERLAY SPEC
    /// point 3). Any unexpected exception here is caught locally and rate-limit-logged as a plain warning; it
    /// deliberately never goes through <see cref="NoodlesLog.PatchException"/>, which feeds
    /// <see cref="KillSwitch.AutoStandDown"/>'s global exception-burst trip — a bug in this read-only display
    /// must never be able to switch the whole mod off for every player.
    /// </summary>
    internal static class OverlaySampler
    {
        // Same private field the probe reads (probe/NoodlesProbe.cs's own s_instancesField), via Harmony's
        // AccessTools instead of raw reflection to match every other private-engine-field read in this
        // assembly (e.g. Transport/AdaptiveRate.cs's s_con, Ownership/LoadShedder.cs's s_shipPlayers) — the
        // publicizer only changes what the COMPILER accepts, the real game DLL still has this field private.
        private static readonly AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>> s_instances =
            AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO, ZNetView>>("m_instances");

        internal static void Sample(OverlaySnapshot s, float now)
        {
            try
            {
                SampleModes(s);
            }
            catch (Exception e)
            {
                NoodlesLog.WarningRateLimited("overlay_sample_modes",
                    $"Overlay: Modes-tab sampling failed (showing stale/placeholder values): {e.GetType().Name}: {e.Message}", 3, 60f);
            }

            try
            {
                SampleNetwork(s);
            }
            catch (Exception e)
            {
                NoodlesLog.WarningRateLimited("overlay_sample_network",
                    $"Overlay: Network-tab sampling failed (showing stale/placeholder values): {e.GetType().Name}: {e.Message}", 3, 60f);
            }

            try
            {
                SampleZones(s);
            }
            catch (Exception e)
            {
                NoodlesLog.WarningRateLimited("overlay_sample_zones",
                    $"Overlay: Zones sampling failed (showing stale/placeholder values): {e.GetType().Name}: {e.Message}", 3, 60f);
            }

            try
            {
                SampleServerLink(s);
            }
            catch (Exception e)
            {
                NoodlesLog.WarningRateLimited("overlay_sample_serverlink",
                    $"Overlay: Download-link sampling failed (showing stale/placeholder values): {e.GetType().Name}: {e.Message}", 3, 60f);
            }
        }

        // ---- Modes tab -------------------------------------------------------------------------------------

        private static void SampleModes(OverlaySnapshot s)
        {
            // "This client": always answerable locally, connected or not (OVERLAY SPEC point 4's own list is
            // explicit that these are about THIS client, not the connection). FeatureFlag.Active already folds
            // in Core.Enabled and any per-feature ForceOff, matching "actually active right now, not just
            // configured" exactly (Core/Features.cs remarks).
            bool q10 = Features.Find("Transport.Q10")?.Active == true;
            s.Q10Value = q10 ? "On" : "Off";

            // Mirrors DeploymentLog.ClientFeatureSummary's own q13 test exactly (Core/DeploymentLog.cs) — Q13
            // additionally needs Q10 (the client's own upload path) and T9 (the rate it rides) active too.
            bool q13 = Features.Find("Transport.Q13")?.Active == true
                && Features.Find("Transport.Q13.Client")?.Active == true
                && q10
                && Features.Find("Transport.T9")?.Active == true;
            s.Q13Value = q13 ? "On" : "Off";

            // Mirrors Patch_ZSyncTransform_SyncPosition_ShortExtrapolation.ResolveCapSeconds's own precondition:
            // the standalone extrapolation patch only takes effect while C1's own buffered ClientSmoothing is OFF.
            bool shortExtrap = Features.Find("ClientSmoothing.ShortExtrapolation")?.Active == true
                && Features.Find("ClientSmoothing")?.Active != true;
            s.ExtrapolationValue = shortExtrap ? "On" : "Off";

            s.KillSwitchValue = KillSwitch.Enabled ? "On" : "Off";

            ZNet znet = ZNet.instance;
            if (znet == null)
            {
                s.ServerLine = "--";
                s.ModeLine = "--";
                s.ModeExplain = "Not connected yet.";
                s.CompressionValue = "--";
                return;
            }

            if (znet.IsServer())
            {
                // A listen host's own uploads already get the full server-side Transport treatment
                // (ClientSendLoop.ShouldApplyOnThisProcess: isAuthoritative || Q10.Active), so the client-only
                // Q10/Q13 flags change nothing here.
                s.Q10Value = "n/a (host)";
                s.Q13Value = "n/a (host)";
                SampleModesAuthoritative(s, znet);
                return;
            }

            SampleModesRemoteClient(s, znet);
        }

        /// <summary>Listen-server host: this one process fills both roles at once, so "the server" is always this build.</summary>
        private static void SampleModesAuthoritative(OverlaySnapshot s, ZNet znet)
        {
            s.ServerLine = $"Runs NoodlesMcDoodles {PluginInfo.PLUGIN_VERSION} (this machine)";
            s.CompressionValue = "--"; // "negotiated with this server" has no meaning talking to yourself

            // Same classification Telemetry.cs's own UpdateDeploymentModeTag uses (server-only tag there;
            // recomputed here from the same public inputs, PeerState.Snapshot()/ZNet.GetPeers(), so a listen
            // host reading its OWN overlay gets the identical label at this process's own 1s cadence rather
            // than waiting on Telemetry's separate ~60s flush).
            int modded = 0, vanilla = 0;
            foreach (KeyValuePair<ZNetPeer, PeerState> kv in PeerState.Snapshot())
            {
                if (kv.Value.Negotiated) modded++;
                else if (kv.Value.KnownVanilla) vanilla++;
            }
            int total = znet.GetPeers()?.Count ?? 0;
            int settled = modded + vanilla;

            if (total == 0)
            {
                s.ModeLine = "Server + client";
                s.ModeExplain = "You're hosting this world yourself: the server and your own client are the same process.";
            }
            else if (settled == 0)
            {
                s.ModeLine = "Pending";
                s.ModeExplain = "Players have connected but haven't finished saying hello yet.";
            }
            else if (modded == settled)
            {
                s.ModeLine = "Server + client";
                s.ModeExplain = "The server runs creatures, builds and doors near every player, and everyone's own client features are active too.";
            }
            else if (modded == 0)
            {
                s.ModeLine = "Server-only";
                s.ModeExplain = "The server runs creatures, builds and doors near you; connected players are on plain vanilla clients.";
            }
            else
            {
                s.ModeLine = "Mixed";
                s.ModeExplain = "Some players run NoodlesMcDoodles and some don't; the server helps every one of them either way.";
            }
        }

        /// <summary>A genuine remote client talking to some other process. Reuses exactly the state
        /// DeploymentLog.LogClientServerNegotiated/LogClientServerVanilla already log from.</summary>
        private static void SampleModesRemoteClient(OverlaySnapshot s, ZNet znet)
        {
            ZNetPeer server = znet.GetServerPeer();
            if (server == null || ZNet.GetConnectionStatus() != ZNet.ConnectionStatus.Connected || !PeerState.TryGet(server, out PeerState state))
            {
                s.ServerLine = "--";
                s.ModeLine = "--";
                s.ModeExplain = "Not connected yet.";
                s.CompressionValue = "--";
                return;
            }

            if (state.Negotiated)
            {
                s.ServerLine = $"Runs NoodlesMcDoodles {state.RemoteModVersion}";
                s.ModeLine = "Server + client";
                s.ModeExplain = "The server runs creatures, builds and doors near you, and your own client features are active too.";
                s.CompressionValue = state.HasCapability(Capabilities.ZdoCompression) ? "Yes" : "No";
            }
            else if (state.KnownVanilla)
            {
                s.ServerLine = "Vanilla server";
                s.ModeLine = "Client only";
                s.ModeExplain = "Players run the world, as in vanilla. Your client's own upload and smoothing features still apply.";
                s.CompressionValue = "No";
            }
            else
            {
                s.ServerLine = "--";
                s.ModeLine = "--";
                s.ModeExplain = "Still saying hello to the server...";
                s.CompressionValue = "--";
            }
        }

        // ---- Network tab (Basic tab's "Connection" + Detailed tab's "Link"/"Send queue") ----------------------

        // Send-queue peak over the last ~60 samples (<=1 Hz cadence, OVERLAY SPEC point 3 -- so "60 samples" is
        // "the last ~60 s" in practice). Fixed-size ring, no allocation per sample.
        private const int QueuePeakWindowSamples = 60;
        private static readonly int[] s_queueRing = new int[QueuePeakWindowSamples];
        private static int s_queueRingPos;
        private static int s_queueRingFilled;

        private static void SampleNetwork(OverlaySnapshot s)
        {
            ZNet znet = ZNet.instance;
            ZDOMan zdoman = ZDOMan.instance;
            ZNetScene scene = ZNetScene.instance;

            // Matches probe/NoodlesProbe.cs's own TryGetClientState gate: a listen host (IsServer()==true) has
            // no "connection to the server" to report on -- it IS the server -- so this whole tab is "--" there,
            // exactly like "not connected yet" on a plain client.
            if (znet == null || zdoman == null || scene == null || znet.IsServer())
            {
                ClearNetwork(s);
                return;
            }

            ZNetPeer server = znet.GetServerPeer();
            if (server == null || server.m_socket == null || ZNet.GetConnectionStatus() != ZNet.ConnectionStatus.Connected)
            {
                ClearNetwork(s);
                return;
            }

            server.m_socket.GetConnectionQuality(out float qLocal, out float qRemote, out int ping, out float outBps, out float inBps);
            int sendQueue = server.m_socket.GetSendQueueSize();

            s.PingValue = ping >= 0 ? ping.ToString(CultureInfo.InvariantCulture) + " ms" : "--"; // Steam reports -1 until its first round-trip sample
            s.UploadValue = FormatRatePerSecond(outBps);
            s.DownloadValue = FormatRatePerSecond(inBps);
            s.SendQueueValue = FormatSize(sendQueue);
            s.ConnQualityLocalValue = FormatQualityPct(qLocal);
            s.ConnQualityRemoteValue = FormatQualityPct(qRemote);

            // The client's own current upload rate when Q10 has T9 managing this connection (Transport/
            // AdaptiveRate.cs); 0 means T9 isn't managing it (Q10 off, T9 off, or no sample yet), i.e. this
            // client's upload is exactly vanilla's fixed rate.
            int rateBps = AdaptiveRate.CurrentRateBps(server);
            s.UploadLimitValue = rateBps > 0 ? FormatRatePerSecond(rateBps) : "vanilla";
            // T1_GlobalRateBps is the CONFIGURED ceiling (Transport/TransportInit.cs) -- what UploadLimitValue
            // above rises toward with T9 on, or is pinned at with T9 off; "set" vs "now" for the Detailed tab.
            s.UploadLimitSetValue = FormatRatePerSecond(TransportLeverConfig.T1GlobalRateBps);

            RecordQueueSample(sendQueue);
            s.SendQueuePeakValue = FormatSize(PeakQueueBytes());

            // Q13_ClientCeilingBytes' own ConfigDescription (Transport/QueueWindow.cs) literally documents it as
            // "a modded client's own upload budget in total (waiting data + round-trip allowance)" -- the single
            // config value that governs this client's own elevated ceiling (PeerBudget.For's client branch,
            // Transport/PeerBudget.cs:64-97, clamps the WAITING half against Q3_InnerBudgetBytes too, but with
            // this build's defaults Q13's own ceiling/2 is the binding one; Q13 off means this client's upload
            // is pinned at vanilla's 10240 regardless of Q3, see SampleQueueAllowanceNow's own gate below).
            s.QueueAllowanceSetValue = FormatSize(QueueWindow.ClientCeilingBytes.Value);
            s.QueueAllowanceNowValue = SampleQueueAllowanceNow(server);

            SampleOwnership(s, scene, znet, server);
        }

        private static string FormatQualityPct(float q)
        {
            // ISocket.GetConnectionQuality's local/remoteQuality (decompiled ZSteamSocket.cs:513-539): Steam's
            // own 0..1 connection-quality fraction; a failed status read zeroes every out param (never negative
            // on this build), but this still guards negative per OVERLAY SPEC 3a ("'--' when unknown or negative").
            if (q < 0f) return "--";
            int pct = (int)Math.Round(q * 100.0, MidpointRounding.AwayFromZero);
            return pct.ToString(CultureInfo.InvariantCulture) + "%";
        }

        private static void RecordQueueSample(int bytes)
        {
            s_queueRing[s_queueRingPos] = bytes;
            s_queueRingPos = (s_queueRingPos + 1) % QueuePeakWindowSamples;
            if (s_queueRingFilled < QueuePeakWindowSamples) s_queueRingFilled++;
        }

        private static int PeakQueueBytes()
        {
            int max = 0;
            for (int i = 0; i < s_queueRingFilled; i++)
            {
                if (s_queueRing[i] > max) max = s_queueRing[i];
            }
            return max;
        }

        /// <summary>
        /// "Queue allowance, now": the live budget <c>PeerBudget.For</c> (Transport/PeerBudget.cs:51-136) is
        /// actually using for this client's own upload right now. That function's real per-call result
        /// (<c>SharedBudget.InnerBudgetBytes</c> -> <c>PeerBudget.Current</c>, Transport/SharedBudget.cs:133-141)
        /// is only valid DURING a SendZDOs call, not from this once-a-second sampler -- so rather than
        /// re-deriving the byte math, this reads the same two Telemetry gauges PeerBudget.For/QueueWindow.Extend
        /// themselves publish (transport_q3_budget_bytes: PeerBudget.cs:124; transport_q13_allowance_bytes:
        /// QueueWindow.cs:125) via Telemetry's own read API (PerPeerCounter.Snapshot, Core/Telemetry.cs:38), and
        /// recombines them with the exact clamp QueueWindow.Extend's own return line uses (QueueWindow.cs:127).
        /// </summary>
        private static string SampleQueueAllowanceNow(ZNetPeer server)
        {
            // Mirrors PeerBudget.For's own early-return guard chain for a plain remote client (PeerBudget.cs:
            // 64-79) and its stall check (AdaptiveRate.IsStalled): any of these false means this client's own
            // budget is exactly vanilla's fixed gate -- a known constant, not something Telemetry has a fresher
            // sample for.
            bool elevated = SharedBudget.Q3.Active
                && QueueWindow.ClientBranchActive
                && TransportLeverConfig.T9Active
                && !AdaptiveRate.IsStalled(server);
            if (!elevated) return FormatSize(SharedBudget.VanillaInnerBudgetBytes) + " (vanilla)";

            if (!TryPeerCounterValue(Telemetry.GetPerPeerCounter("transport_q3_budget_bytes"), server, out long q3))
                return "--"; // elevated but nothing sent/measured yet this session

            long total = q3;
            if (TryPeerCounterValue(Telemetry.GetPerPeerCounter("transport_q13_allowance_bytes", resetEachPeriod: true), server, out long q13))
                total = Math.Min(q3 + q13, SharedBudget.HardCeilingBytes); // QueueWindow.Extend's own clamp, QueueWindow.cs:127

            return FormatSize(total);
        }

        private static bool TryPeerCounterValue(PerPeerCounter counter, ZNetPeer peer, out long value)
        {
            foreach (KeyValuePair<ZNetPeer, long> kv in counter.Snapshot())
            {
                if (kv.Key == peer)
                {
                    value = kv.Value;
                    return true;
                }
            }
            value = 0;
            return false;
        }

        /// <summary>
        /// Census of who owns the ZDOs instantiated around the player, bounded to the loaded area the same way
        /// the server itself decides what's "near" (ZNetScene.InActiveArea against the client's own reference
        /// position) -- the exact test probe/NoodlesProbe.cs's Sample() already uses for its own near-* counts,
        /// reused here rather than re-derived. Read-only: never touches ZDO.SetOwner or anything else.
        /// </summary>
        private static void SampleOwnership(OverlaySnapshot s, ZNetScene scene, ZNet znet, ZNetPeer server)
        {
            long me = ZDOMan.GetSessionID();
            long srv = server.m_uid;
            Vector3 refPos = znet.GetReferencePosition();

            Dictionary<ZDO, ZNetView> instances = s_instances(scene);
            int total = 0, meCount = 0, srvCount = 0, otherCount = 0, noneCount = 0;
            if (instances != null)
            {
                foreach (KeyValuePair<ZDO, ZNetView> kv in instances)
                {
                    ZDO zdo = kv.Key;
                    if (zdo == null || !ZNetScene.InActiveArea(zdo.GetPosition(), refPos)) continue;
                    total++;
                    long owner = zdo.GetOwner();
                    if (owner == 0L) noneCount++;
                    else if (owner == me) meCount++;
                    else if (owner == srv) srvCount++;
                    else otherCount++; // another player's uid, known or since-disconnected -- "other players" either way
                }
            }

            if (total <= 0)
            {
                s.ServerOwnValue = "0% (0)";
                s.YouOwnValue = "0% (0)";
                s.OtherOwnValue = "0% (0)";
                s.UnownedValue = "0% (0)";
                s.ServerFrac = s.YouFrac = s.OtherFrac = 0f;
                return;
            }

            s.ServerOwnValue = PctCount(srvCount, total);
            s.YouOwnValue = PctCount(meCount, total);
            s.OtherOwnValue = PctCount(otherCount, total);
            s.UnownedValue = PctCount(noneCount, total);
            s.ServerFrac = srvCount / (float)total;
            s.YouFrac = meCount / (float)total;
            s.OtherFrac = otherCount / (float)total;
        }

        private static void ClearNetwork(OverlaySnapshot s)
        {
            s.PingValue = "--";
            s.UploadValue = "--";
            s.DownloadValue = "--";
            s.SendQueueValue = "--";
            s.UploadLimitValue = "--";
            s.ServerOwnValue = "--";
            s.YouOwnValue = "--";
            s.OtherOwnValue = "--";
            s.UnownedValue = "--";
            s.ServerFrac = s.YouFrac = s.OtherFrac = 0f;

            s.UploadLimitSetValue = "--";
            s.ConnQualityLocalValue = "--";
            s.ConnQualityRemoteValue = "--";
            s.SendQueuePeakValue = "--";
            s.QueueAllowanceSetValue = "--";
            s.QueueAllowanceNowValue = "--";
            s_queueRingFilled = 0; // never carry a peak over from a previous connection
            s_queueRingPos = 0;
        }

        // ---- Detailed tab: "Download (server -> you)" / Basic tab's "Download limit" --------------------------

        /// <summary>
        /// The server's OWN view of its link to us (<see cref="ServerLinkStats"/>) -- our download is entirely
        /// the server's send rate to us, which only the server sets (T9, per player), so unlike upload there is
        /// no local client-side setting to read here; this is the one section of the overlay whose numbers come
        /// from another machine rather than this client's own state. Independent of <see cref="SampleNetwork"/>'s
        /// own connected/listen-host gating -- <see cref="ServerLinkStats.Available"/> already folds in "never
        /// received a push", "10s since the last one" (a vanilla/pre-1.0.3 server, or a dropped connection) and,
        /// by construction, a listen host talking to itself (nothing ever pushes to <see cref="ServerLinkStats"/>
        /// there). When it's false, OverlayGui's Detailed tab shows one explanatory line instead of the row
        /// values below, so only the Basic tab's single "Download limit" row needs its own "--" in that case.
        /// </summary>
        private static void SampleServerLink(OverlaySnapshot s)
        {
            bool available = ServerLinkStats.Available;
            s.ServerLinkAvailable = available;

            if (!available)
            {
                s.DownloadLimitValue = "--";
                s.DownloadLimitSetValue = "--";
                s.DownloadLimitNowValue = "--";
                s.DownloadActualValue = "--";
                s.ServerQueueForYouValue = "--";
                s.ServerQueueAllowanceValue = "--";
                s.ServerPingToYouValue = "--";
                s.ServerLinkStalled = false;
                return;
            }

            // 0 = not managed by the server's T9 -- vanilla's own fixed rate (153600 B/s == 150 KiB/s exactly),
            // per ServerLinkStats.RateNowBps's own doc comment. Basic tab gets the short form ("vanilla"); the
            // Detailed tab spells out the actual number since that row has the room for it.
            int rateNow = ServerLinkStats.RateNowBps;
            s.DownloadLimitValue = rateNow > 0 ? FormatRatePerSecond(rateNow) : "vanilla";
            s.DownloadLimitNowValue = rateNow > 0 ? FormatRatePerSecond(rateNow) : "vanilla (150 KiB/s)";

            s.DownloadLimitSetValue = FormatRatePerSecond(ServerLinkStats.RateCeilingBps);
            s.DownloadActualValue = FormatRatePerSecond(ServerLinkStats.SentBps);
            s.ServerQueueForYouValue = FormatSize(ServerLinkStats.QueueBytes);

            // 0 = not known yet (ServerLinkStats.QueueAllowanceBytes's own doc comment) -- distinct from a real
            // measured zero, so this is "--" rather than "0 KiB" the same way SampleQueueAllowanceNow above
            // treats "nothing measured yet" as unknown rather than zero.
            s.ServerQueueAllowanceValue = ServerLinkStats.QueueAllowanceBytes > 0
                ? FormatSize(ServerLinkStats.QueueAllowanceBytes)
                : "--";

            // The SERVER's own measured ping to us -- a different number from this client's own PingValue above
            // (that one comes from OUR socket's GetConnectionQuality); negative = unknown, same convention.
            s.ServerPingToYouValue = ServerLinkStats.PingMs >= 0
                ? ServerLinkStats.PingMs.ToString(CultureInfo.InvariantCulture) + " ms"
                : "--";

            s.ServerLinkStalled = ServerLinkStats.Stalled;
        }

        // ---- Detailed tab: "Zones around you" -------------------------------------------------------------------

        // The per-zone spawn-system object (ZoneSystem.PlaceZoneCtrl instantiates exactly one per generated
        // zone, decompiled ZoneSystem.cs:1358-1374) -- the same object vanilla itself uses to track which
        // connected peer simulates that zone's creatures/spawns, so its ZDO owner IS the zone's controller.
        private static readonly int s_zoneCtrlHash = "_ZoneCtrl".GetStableHashCode();

        // Reused across samples (cleared, never reallocated) -- OVERLAY SPEC's own "reuse lists/dictionaries".
        private static readonly Dictionary<Vector2s, long> s_zoneCtrlOwner = new Dictionary<Vector2s, long>(9);
        private static readonly Dictionary<long, int> s_zoneOwnerCounts = new Dictionary<long, int>(16);
        private static readonly List<KeyValuePair<long, int>> s_zoneTopScratch = new List<KeyValuePair<long, int>>(16);
        private static readonly Comparison<KeyValuePair<long, int>> s_byCountDescending = (a, b) => b.Value.CompareTo(a.Value);

        /// <summary>
        /// WHO controls the zone the player is standing in and its 8 neighbours, plus that zone's own object
        /// mix and top contributing players. Independent of <see cref="SampleNetwork"/>: a listen host has no
        /// "connection to the server" (that whole tab is "--" there) but still has zones with real controllers,
        /// including itself and any connected players -- exactly the case this section exists to show.
        /// </summary>
        private static void SampleZones(OverlaySnapshot s)
        {
            ZNet znet = ZNet.instance;
            ZoneSystem zs = ZoneSystem.instance;
            ZNetScene scene = ZNetScene.instance;
            if (znet == null || zs == null || scene == null)
            {
                ClearZones(s);
                return;
            }

            Vector3 refPos = znet.GetReferencePosition(); // same anchor SampleOwnership already uses for "near you"
            Vector2s here = ZoneSystem.GetZone(refPos);
            s.ZoneHereValue = "(" + here.x.ToString(CultureInfo.InvariantCulture) + ", " + here.y.ToString(CultureInfo.InvariantCulture) + ")";

            long me = ZDOMan.GetSessionID();
            ZNetPeer server = znet.GetServerPeer(); // null on a listen host (IsServer()==true) -- ZNet.cs GetServerPeer
            bool haveServer = server != null;
            long srv = haveServer ? server.m_uid : 0L;
            List<ZNet.PlayerInfo> players = znet.GetPlayerList(); // ZNet's own live list (ZNet.cs: `return m_players;`) -- no allocation

            s_zoneCtrlOwner.Clear();
            s_zoneOwnerCounts.Clear();
            int zoneTotal = 0, zoneSrv = 0, zoneMe = 0, zoneOther = 0;

            Dictionary<ZDO, ZNetView> instances = s_instances(scene);
            if (instances != null)
            {
                foreach (KeyValuePair<ZDO, ZNetView> kv in instances)
                {
                    ZDO zdo = kv.Key;
                    if (zdo == null) continue;
                    Vector2s sector = zdo.GetSector(); // == ZoneSystem.GetZone(zdo.GetPosition()) -- decompiled ZDO.cs:517-520

                    if (zdo.GetPrefab() == s_zoneCtrlHash)
                    {
                        s_zoneCtrlOwner[sector] = zdo.GetOwner(); // exactly one _ZoneCtrl per zone
                    }

                    if (sector.x != here.x || sector.y != here.y) continue; // rest of this loop body is "this zone only"
                    zoneTotal++;
                    long owner = zdo.GetOwner();
                    if (owner == 0L)
                    {
                        // unowned -- counted in zoneTotal (the % base) but not in any of the three buckets below
                    }
                    else if (owner == me) zoneMe++;
                    else if (haveServer && owner == srv) zoneSrv++;
                    else
                    {
                        zoneOther++;
                        s_zoneOwnerCounts.TryGetValue(owner, out int c);
                        s_zoneOwnerCounts[owner] = c + 1;
                    }
                }
            }

            // NW N NE / W Here E / SW S SE -- +y is north (ZoneSystem.GetZone uses point.z for .y; decompiled
            // ZoneSystem.cs:2973-2978), +x is east, matching the game's own map orientation.
            FillGridCell(s.ZoneGrid, 0, here.x - 1, here.y + 1, me, srv, haveServer, players);
            FillGridCell(s.ZoneGrid, 1, here.x, here.y + 1, me, srv, haveServer, players);
            FillGridCell(s.ZoneGrid, 2, here.x + 1, here.y + 1, me, srv, haveServer, players);
            FillGridCell(s.ZoneGrid, 3, here.x - 1, here.y, me, srv, haveServer, players);
            FillGridCell(s.ZoneGrid, 4, here.x, here.y, me, srv, haveServer, players);
            FillGridCell(s.ZoneGrid, 5, here.x + 1, here.y, me, srv, haveServer, players);
            FillGridCell(s.ZoneGrid, 6, here.x - 1, here.y - 1, me, srv, haveServer, players);
            FillGridCell(s.ZoneGrid, 7, here.x, here.y - 1, me, srv, haveServer, players);
            FillGridCell(s.ZoneGrid, 8, here.x + 1, here.y - 1, me, srv, haveServer, players);

            if (zoneTotal <= 0)
            {
                s.ZoneMixServerValue = "0% (0)";
                s.ZoneMixYouValue = "0% (0)";
                s.ZoneMixOtherValue = "0% (0)";
            }
            else
            {
                s.ZoneMixServerValue = PctCount(zoneSrv, zoneTotal);
                s.ZoneMixYouValue = PctCount(zoneMe, zoneTotal);
                s.ZoneMixOtherValue = PctCount(zoneOther, zoneTotal);
            }

            s_zoneTopScratch.Clear();
            foreach (KeyValuePair<long, int> kv in s_zoneOwnerCounts) s_zoneTopScratch.Add(kv);
            s_zoneTopScratch.Sort(s_byCountDescending); // largest first

            if (s_zoneTopScratch.Count == 0)
            {
                s.ZoneTopPlayers[0] = "--";
                s.ZoneTopPlayers[1] = "";
                s.ZoneTopPlayers[2] = "";
                s.ZoneTopPlayers[3] = "";
            }
            else
            {
                for (int i = 0; i < s.ZoneTopPlayers.Length; i++)
                {
                    if (i < s_zoneTopScratch.Count)
                    {
                        long uid = s_zoneTopScratch[i].Key;
                        int count = s_zoneTopScratch[i].Value;
                        string name = ResolvePlayerName(uid, players);
                        s.ZoneTopPlayers[i] = name + "  " + count.ToString(CultureInfo.InvariantCulture) + " objects";
                    }
                    else
                    {
                        s.ZoneTopPlayers[i] = "";
                    }
                }
            }
        }

        private static void FillGridCell(string[] grid, int index, int zx, int zy, long me, long srv, bool haveServer, List<ZNet.PlayerInfo> players)
        {
            if (s_zoneCtrlOwner.TryGetValue(new Vector2s(zx, zy), out long owner))
                grid[index] = OwnerLabel(owner, me, srv, haveServer, players);
            else
                grid[index] = "--"; // no _ZoneCtrl loaded for that zone (outside this client's active area)
        }

        /// <summary>owner uid -> plain label, shared by the zone grid and the per-player breakdown. Never a raw uid.</summary>
        private static string OwnerLabel(long owner, long me, long srv, bool haveServer, List<ZNet.PlayerInfo> players)
        {
            if (owner == 0L) return "Unowned";
            if (owner == me) return "You";
            if (haveServer && owner == srv) return "Server";
            return ResolvePlayerName(owner, players);
        }

        private static string ResolvePlayerName(long uid, List<ZNet.PlayerInfo> players)
        {
            if (players != null)
            {
                for (int i = 0; i < players.Count; i++)
                {
                    if (players[i].m_characterID.UserID == uid)
                    {
                        string name = players[i].m_name;
                        return string.IsNullOrEmpty(name) ? "Another player" : Truncate(name, 10);
                    }
                }
            }
            return "Another player"; // known uid, no matching PlayerInfo (e.g. since disconnected)
        }

        private static string Truncate(string text, int max) => text.Length > max ? text.Substring(0, max) : text;

        private static void ClearZones(OverlaySnapshot s)
        {
            s.ZoneHereValue = "--";
            for (int i = 0; i < s.ZoneGrid.Length; i++) s.ZoneGrid[i] = "--";
            s.ZoneMixServerValue = "--";
            s.ZoneMixYouValue = "--";
            s.ZoneMixOtherValue = "--";
            for (int i = 0; i < s.ZoneTopPlayers.Length; i++) s.ZoneTopPlayers[i] = "";
        }

        // ---- Formatting (all done here, never in OnGUI -- OVERLAY SPEC point 3) ------------------------------

        private static string PctCount(int n, int total)
        {
            int pct = (int)Math.Round(n * 100.0 / total, MidpointRounding.AwayFromZero);
            return pct.ToString(CultureInfo.InvariantCulture) + "% (" + n.ToString("N0", CultureInfo.InvariantCulture) + ")";
        }

        private static string FormatRatePerSecond(float bytesPerSecond) => FormatSize(bytesPerSecond) + "/s";

        private static string FormatSize(float bytes)
        {
            float kib = bytes / 1024f;
            string number = kib < 10f
                ? kib.ToString("0.0", CultureInfo.InvariantCulture)
                : Math.Round(kib, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
            return number + " KiB";
        }
    }
}
