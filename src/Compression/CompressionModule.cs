using System;
using System.Globalization;
using System.IO.Compression;
using BepInEx.Configuration;
using HarmonyLib;
using NoodlesMcDoodles.Core;
using NoodlesMcDoodles.Core.ConfigFiles;

namespace NoodlesMcDoodles.Compression
{
    /// <summary>
    /// D1/D2 (docs/design/06 Phase C3) — negotiated GZip compression of the ZDO sync stream between two
    /// confirmed NoodlesMcDoodles peers. Module entry point: config, feature flag, capability advertisement,
    /// patch registration, kill-switch flush. See docs/impl/COMPRESSION-IMPL.md for the full design and its
    /// deliberate deviations from docs/design/06's own H-D1a/b/c/d/e sketches.
    ///
    /// NO LONGER SHIPPED DARK (WP5, binding user decision, superseding this class's own earlier "SHIPPED DARK"
    /// note and DESIGN.md M7's "shipped dark" framing): the "Features"/"ZdoCompression.Active" key now defaults
    /// to true, one of "every feature ON by default" for the first live version, alongside O9/O10/ServerSpawning/
    /// etc. D2's own five live preconditions (docs/impl/COMPRESSION-IMPL.md "Known risks") are UNCHANGED and
    /// still not all exercised — the user's own decision is to ship this on now and run those live checks on
    /// NoodlesTest before Proxima, not to treat them as having already passed. This is safe to ship ahead of
    /// that live verification specifically because negotiation and fallback are structural, not configuration:
    /// <see cref="EffectiveActive"/>'s send-side gate is a local willingness flag ANDed with
    /// <see cref="Patch_ZDOMan_SendZDOs.IsConfirmedForSend"/>'s own <c>PeerState.HasCapability</c> check — a
    /// peer's compressed channel only ever engages once BOTH sides' Hello/HelloAck handshake has confirmed the
    /// OTHER side also runs this exact build, so flipping this default can never affect a vanilla peer (no
    /// capability bit, ever) or a peer running an older/incompatible NoodlesMcDoodles build (schema-mismatch
    /// nack, permanent downgrade to plain ZDOData — see <see cref="CompressionState.RecordAckOutcome"/>) — the
    /// exact "negotiated per peer only; vanilla peers untouched" property this default flip depends on.
    /// </summary>
    internal static class CompressionModule
    {
        // ---- Wire constants (not config — protocol-shape, not a tuning knob) --------------------------------
        public const int SchemaVersion = 1; // ZdoCompression.SchemaVersion, DESIGN.md: "fixed, build-time constant"
        public const string ZdocRpcName = "NoodlesMcDoodles.NMD_ZDOC";      // compressed ZDO batch payload
        public const string AckRpcName = "NoodlesMcDoodles.NMD_ZDOC_ACK";   // per-batch application-level ack/nack

        // ---- Config ------------------------------------------------------------------------------------------
        private static ConfigEntry<bool> s_activeConfig;
        private static ConfigEntry<int> s_ackTimeoutMsConfig;
        private static ConfigEntry<string> s_levelConfig; // string, not CompressionLevel — see BindConfig's own remarks

        public static FeatureFlag Active { get; private set; }

        private const string SyncKeyActive = "ZdoCompression.Active";
        private const string SyncKeyAckTimeoutMs = "ZdoCompression.AckTimeoutMs";

        [NoodlesModuleInit]
        internal static void Init()
        {
            BindConfig();
            Active = Features.Register("ZdoCompression", s_activeConfig);

            // Declares "this build has compression code" — a fixed, Init-time fact, independent of whether
            // Active is currently true (own-flag state is re-checked live everywhere it matters; see
            // EffectiveActive below). Matches Capabilities.ConfigSync's own always-on bit 0 pattern.
            Capabilities.Advertise(Capabilities.ZdoCompression);

            // Server-authoritative per DESIGN.md's Configuration reference ("Smoothing & compression": both
            // ZdoCompression.Active and AckTimeoutMs are "synced"). Providers are read fresh on every push
            // (CORE-API.md "Pushing a value over ConfigSync") — never a cached snapshot.
            ConfigSyncRpc.RegisterSyncedValue(SyncKeyActive, () => s_activeConfig.Value.ToString(CultureInfo.InvariantCulture));
            ConfigSyncRpc.RegisterSyncedValue(SyncKeyAckTimeoutMs, () => s_ackTimeoutMsConfig.Value.ToString(CultureInfo.InvariantCulture));

            PatchRegistry.Register("ZdoCompression", typeof(Patch_ZDOMan_AddPeer), PatchSide.Both);
            PatchRegistry.Register("ZdoCompression", typeof(Patch_ZDOMan_SendZDOs), PatchSide.Both);
            PatchRegistry.Register("ZdoCompression", typeof(Patch_ZDOPeer_ShouldSend), PatchSide.Both);
            PatchRegistry.Register("ZdoCompression", typeof(CompressionState.Patch_ZNet_Disconnect_CompressionCleanup), PatchSide.Both);

            // Live-authority feature (holds per-peer in-flight state whose absence must be safe): flush on the
            // global kill switch, exactly like every other holder of live state (CORE-API.md "Flush callbacks").
            KillSwitch.RegisterFlushCallback("ZdoCompression.InFlight", CompressionState.ClearAllPeers);

            // Data-loss #13 (docs/design/06): a LOCAL flag flip (this feature specifically, independent of the
            // global kill switch) must ALSO actively resolve in-flight state, not just stop granting new holds.
            // SettingChanged is not guaranteed to run on the main thread (see MainThreadDispatcher remarks), and
            // CompressionState.ClearAllPeers touches per-peer dictionaries that the send/receive patches also
            // touch from the main thread — marshal through the same dispatcher every other SettingChanged-driven,
            // state-touching path in this mod uses.
            s_activeConfig.SettingChanged += (_, __) =>
            {
                if (!s_activeConfig.Value) MainThreadDispatcher.Enqueue(CompressionState.ClearAllPeers);
            };

            // WP5 per-feature quarantine wiring (this lever previously lacked it, confirmed by direct read —
            // only the KillSwitch flush callback and this lever's own SettingChanged-to-off above existed).
            // A FeatureStandDown exception-burst trip on THIS feature's own patches now releases every peer's
            // in-flight state the same synchronous way ForceOff already does for every other quarantined
            // feature, rather than relying solely on the global kill switch ever tripping too. Same target as
            // the KillSwitch flush above (idempotent to call twice back-to-back — ClearAllPeers just empties an
            // already-empty dictionary the second time).
            Active.OnForcedOff = CompressionState.ClearAllPeers;
        }

        private static void BindConfig()
        {
            // 1.1.0: main file [2 - Network] CompressWorldSync (was [Features] ZdoCompression.Active; the ConfigSync
            // wire key SyncKeyActive above is unchanged so 1.0.x peers still interoperate). D1: negotiated GZip
            // compression of the ZDO sync stream, engaged per peer only once BOTH sides' handshake confirmed the other
            // runs this build (PeerState.HasCapability). Default ON (WP5, 'every feature ON by default'). Off = every
            // hook falls through to exactly vanilla ZDOData, same bytes, same code path, no restart needed.
            s_activeConfig = NoodlesConfig.CompressWorldSync;

            // Kept at DESIGN.md's own value (3000ms). Not a compression-gain dial (coordinator's "great
            // improvement, not overkill" note applies to the level/window trade-off below) — this is a safety
            // backstop for a genuine parse-layer bug, not a latency target: Steam/TCP/PlayFab already guarantee
            // reliable, in-order delivery, so a healthy ack round-trip (even Arizona<->New Jersey, ~char100-200ms
            // RTT) completes in well under a tenth of this timeout; shrinking it further wouldn't raise the
            // realistic ceiling on ordinary bytes-saved, only trims how long a genuinely broken connection keeps
            // a few ZDOs waiting before ShouldSend naturally reselects them (self-heals either way — see
            // CompressionState.IsInFlightUnacked). See docs/impl/COMPRESSION-IMPL.md's Defaults table.
            s_ackTimeoutMsConfig = NoodlesConfig.File.Bind("ZdoCompression", "AckTimeoutMs", 3000,
                new ConfigDescription(
                    "How long a sender waits for a batch's application-level ack before treating it as lost " +
                    "(the ZDOs in it become reselectable, never dropped). No vanilla equivalent. Generous by " +
                    "design: Steam/TCP/PlayFab already guarantee reliable, in-order delivery, so a healthy ack " +
                    "round-trip is near-free even cross-country; this only fires on a genuine decode/parse bug.",
                    new AcceptableValueRange<int>(1000, 10000)));

            // NEW key (not in DESIGN.md's table — that table only lists Active/AckTimeoutMs/SchemaVersion).
            // This is the actual "how hard do we squeeze" dial the coordinator's "not overkill" rule targets.
            // ZPackage.GetCompressed()/Utils.Compress hardcode GZipStream at CompressionLevel.Fastest
            // (UTILS:12483-12488) with no way to tune it; this module calls GZipStream directly instead (same
            // BCL class the game already loads, zero new dependency) specifically so this default can be
            // chosen from zip-sim's own measurement rather than inherited blindly. Decompression is level-
            // agnostic (gzip's decoder doesn't need to know what level encoded the stream), so this never
            // affects wire compatibility with the receive side, which still calls vanilla's own Utils.Decompress
            // unchanged. Restricted to Fastest/Optimal — SmallestSize is a newer BCL addition not guaranteed
            // present on the Mono corlib this game ships (research/12, citing Utils.BrotliCompressor's own
            // defensive Enum.IsDefined check, UTILS:522-530) and NoCompression defeats the feature's purpose.
            // String, not a ConfigEntry<CompressionLevel> directly: BepInEx's AcceptableValueList<T> requires
            // T : IEquatable<T>, which System.IO.Compression.CompressionLevel does not implement on this
            // project's target framework (confirmed at build time: CS0315) — a string with an explicit
            // allow-list reads identically in the .cfg file ("CompressionLevel = Fastest") and is just as
            // guarded against a stray invalid value; EffectiveCompressionLevel below parses it.
            s_levelConfig = NoodlesConfig.File.Bind("ZdoCompression", "CompressionLevel", nameof(CompressionLevel.Fastest),
                new ConfigDescription(
                    "GZip effort level for the compressed ZDO batch channel: Fastest or Optimal. No vanilla " +
                    "equivalent (vanilla never compresses this stream). Default is the knee of the curve " +
                    "measured by tools/zip-sim on realistic ZDO-batch byte shapes at 10-128 KiB: see " +
                    "docs/impl/COMPRESSION-IMPL.md's Defaults table for the exact ratio/CPU numbers this was " +
                    "picked from. 'Optimal' is safe opt-in headroom for an operator who has CPU to spare and " +
                    "wants the last few percent of ratio; it is not the default because zip-sim shows it costs " +
                    "materially more CPU per batch for a small additional bytes-saved return on this data shape.",
                    new AcceptableValueList<string>(nameof(CompressionLevel.Fastest), nameof(CompressionLevel.Optimal))));
        }

        // ---- Live, never-cached reads (FeatureFlag.Active convention — CORE-API.md "never cache it") --------

        /// <summary>
        /// The one gate every send-side hook checks. Server: its own local flag is authoritative. Client: the
        /// server-pushed ConfigSync overlay governs (DESIGN.md: "a single flag flip on the server can
        /// dark-launch or fully disable the feature cluster-wide") — but only once local safety conditions
        /// (kill switch, any force-off) are satisfied, and only once the overlay has actually arrived; before
        /// that this client stays on its own local default (false), the safe direction. This intentionally does
        /// NOT require the client's own local config bool to independently be true — that one key is the
        /// server-authoritative half of the pair; see docs/impl/COMPRESSION-IMPL.md "Config keys".
        /// </summary>
        public static bool EffectiveActive
        {
            get
            {
                bool locallySafe = KillSwitch.Enabled && !Active.IsForcedOff;
                if (!locallySafe) return false;

                if (ZNet.instance == null) return false;
                if (ZNet.instance.IsServer()) return Active.ConfiguredOn;

                if (ConfigSyncRpc.Overlay.TryGetValue(SyncKeyActive, out var raw) &&
                    bool.TryParse(raw, out var serverWantsIt))
                {
                    return serverWantsIt;
                }
                return false; // no push yet -> behave exactly like the shipped-dark default until the server confirms
            }
        }

        /// <summary>Same synced-overlay-with-fallback pattern as <see cref="EffectiveActive"/>, for the one numeric synced value.</summary>
        public static float EffectiveAckTimeoutSeconds
        {
            get
            {
                if (ZNet.instance != null && !ZNet.instance.IsServer() &&
                    ConfigSyncRpc.Overlay.TryGetValue(SyncKeyAckTimeoutMs, out var raw) &&
                    int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var overlayMs))
                {
                    return overlayMs / 1000f;
                }
                return s_ackTimeoutMsConfig.Value / 1000f;
            }
        }

        public static CompressionLevel EffectiveCompressionLevel =>
            string.Equals(s_levelConfig.Value, nameof(CompressionLevel.Optimal), StringComparison.OrdinalIgnoreCase)
                ? CompressionLevel.Optimal
                : CompressionLevel.Fastest; // fail-safe default for any unexpected value, not just the two allow-listed ones
    }
}
