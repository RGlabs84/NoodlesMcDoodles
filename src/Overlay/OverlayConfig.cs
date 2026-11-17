using BepInEx.Configuration;
using NoodlesMcDoodles.Core;
using NoodlesMcDoodles.Core.ConfigFiles;
using UnityEngine;

namespace NoodlesMcDoodles.Overlay
{
    /// <summary>
    /// Module entry point for the client-side status overlay (OVERLAY SPEC): an F6-toggled, two-tab (Basic /
    /// Detailed) IMGUI panel in Njord's black/gold HUD style, showing the probe's own live numbers in plain,
    /// refined wording instead of raw "me/server/you" and never a raw uid. Binds this module's three config
    /// keys, then — client-only — creates its own hidden <see cref="OverlayController"/> GameObject, matching
    /// exactly how <see cref="Gameplay.DoorAutoCloseScanner.EnsureCreated"/> and
    /// <see cref="ZoneSim.PokeRingDriver"/> create their own standalone driver components.
    ///
    /// CLIENT ONLY, absolutely: on a dedicated server (or any batch-mode process) this Init() binds config —
    /// so the shared .cfg file stays complete and self-documenting on every install, the same convention every
    /// other client-only module (ClientSmoothing, Transport.Q10) already follows — and returns before creating
    /// anything at all. No Harmony patch is registered anywhere in this module (there is nothing here for
    /// <see cref="PatchRegistry"/> to apply), so a dedicated server's runtime behaviour is provably unchanged:
    /// the whole overlay is a client GameObject-and-MonoBehaviour that never exists on that process.
    /// </summary>
    internal static class OverlayConfig
    {
        public static ConfigEntry<KeyboardShortcut> ToggleKey;
        public static ConfigEntry<bool> ShowOnStart;
        public static ConfigEntry<float> Opacity;

        [NoodlesModuleInit]
        internal static void Init()
        {
            // 1.1.0: main file [4 - Client] PanelKey / PanelShowOnStart / PanelOpacity (were [Overlay] ToggleKey /
            // ShowOnStart / Opacity). The panel's saved position stays in its own overlay .cfg (OverlayPanelConfig).
            ToggleKey = NoodlesConfig.PanelKey;

            ShowOnStart = NoodlesConfig.PanelShowOnStart;

            Opacity = NoodlesConfig.PanelOpacity;

            if (Side.IsDedicatedServerProcess || Application.isBatchMode)
            {
                return; // CLIENT ONLY -- see class remarks. Never creates anything on a dedicated server.
            }

            // Bound here, directly, rather than via its own [NoodlesModuleInit]: OverlayController.Awake()
            // (triggered synchronously by AddComponent below) reads OverlayPanelConfig.Open/Tab immediately to
            // restore last session's panel state, so those entries must already be bound by the time that call
            // happens. A second independent module-init would tie-break alphabetically against this one
            // ("OverlayConfig" < "OverlayPanelConfig") and could run AFTER the GameObject it's needed by --
            // calling it explicitly, in order, right here removes that race entirely. See OverlayPanelConfig's
            // own class remarks.
            OverlayPanelConfig.Init();

            var go = new GameObject("NoodlesMcDoodles.Overlay.OverlayController");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<OverlayController>();
        }
    }
}
