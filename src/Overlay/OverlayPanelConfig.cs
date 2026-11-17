using System.IO;
using BepInEx.Configuration;
using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.Overlay
{
    /// <summary>
    /// Panel placement/size/tab/open-state (OVERLAY SPEC point 3), persisted in ITS OWN BepInEx
    /// <see cref="ConfigFile"/> — <c>wubarrk.noodlesmcdoodles.overlay.cfg</c>, next to the mod's main
    /// <c>wubarrk.noodlesmcdoodles.cfg</c> but deliberately a SEPARATE file/object, never bound against
    /// <see cref="NoodlesConfig.Main"/> or <see cref="NoodlesConfig.File"/> (the advanced file). Their
    /// <see cref="ConfigWatcher"/> reloads a WHOLE .cfg on every write (so a headless operator's hand-edit applies live); a drag or resize can write
    /// several times a session, and routing that through the same file would mean every other module's
    /// SettingChanged handling gets needlessly re-triggered on every "you moved the panel 4px" write. This
    /// file has no watcher, and nothing else in the mod ever reads or reloads it.
    ///
    /// CLIENT ONLY: <see cref="Init"/> is called directly from <see cref="OverlayConfig.Init"/> (NOT its own
    /// <c>[NoodlesModuleInit]</c> — see remarks there on why this needs to run strictly before
    /// <see cref="OverlayController.Awake"/>, which reads <see cref="Open"/>/<see cref="Tab"/> immediately),
    /// itself only reached after that method's own dedicated-server/batch-mode guard has already returned.
    /// The guard below is defence in depth only, matching how <see cref="OverlayController.Awake"/> re-checks
    /// the same condition even though its creator already guarantees it.
    ///
    /// Every entry here is local/unsynced by nature (a player's own window placement) — same reasoning as
    /// Fatty's own HUD-panel config block (Fatty/Configuration/ConfigManager.cs "10 - HUD Panels", the
    /// pattern this whole class is modelled on).
    ///
    /// Written only from <see cref="OverlayGui"/> (a drag or resize's own mouse-up, via HandleDrag/
    /// HandleResize) and from <see cref="OnTabOrOpenChanged"/> (called from both
    /// <see cref="OverlayController.HandleToggleKey"/> — every F6 press, including the Hidden boundary — and
    /// <see cref="OverlayGui.Draw"/> — a direct click on a tab button, which never touches Hidden) — never per
    /// frame (OVERLAY SPEC point 3). BepInEx's own <c>ConfigEntry.Value</c> setter already only saves to disk
    /// when the new value actually differs from the old one; the equality checks below just make that
    /// guarantee explicit at each call site rather than leaving it implicit in BepInEx's own behaviour.
    /// </summary>
    internal static class OverlayPanelConfig
    {
        internal const string TabBasic = "Basic";
        internal const string TabDetailed = "Detailed";

        // Sentinel for "no saved X yet". A fixed pixel default would be wrong on most monitors, since the
        // panel's own default position is TOP-RIGHT (screen-width-dependent) rather than a fixed corner like
        // Fatty's own top-left default (Fatty/Configuration/ConfigManager.cs's foodBreakdownPanelX = 1f) --
        // OverlayGui.DefaultRect computes the real default from the CURRENT Screen.width instead whenever this
        // is NaN. Never a legitimate on-screen X (ClampToScreen only ever produces real, finite numbers), so a
        // genuine saved position can never be mistaken for "unset".
        internal const float UnsetX = float.NaN;

        private static ConfigFile s_file;

        public static ConfigEntry<float> X;
        public static ConfigEntry<float> Y;
        public static ConfigEntry<float> Width;
        public static ConfigEntry<float> Height;
        public static ConfigEntry<string> Tab;
        public static ConfigEntry<bool> Open;

        internal static void Init()
        {
            if (Side.IsDedicatedServerProcess || Application.isBatchMode)
            {
                return; // CLIENT ONLY -- see class remarks; this file must never be created on a dedicated server.
            }

            string path = Path.Combine(BepInEx.Paths.ConfigPath, "wubarrk.noodlesmcdoodles.overlay.cfg");
            s_file = new ConfigFile(path, true);

            X = s_file.Bind("Panel", "X", UnsetX,
                "Saved screen X position (pixels from the left edge) of the overlay panel. Saved automatically " +
                "when you drag or resize the panel. Unset on a fresh install -- the panel starts near the " +
                "top-right of your screen.");
            Y = s_file.Bind("Panel", "Y", 16f,
                "Saved screen Y position (pixels from the top edge) of the overlay panel. Saved automatically " +
                "when you drag or resize the panel.");
            Width = s_file.Bind("Panel", "Width", 320f,
                "Saved width of the overlay panel, in pixels. Saved automatically when you resize it.");
            Height = s_file.Bind("Panel", "Height", 480f,
                "Saved height of the overlay panel, in pixels. Saved automatically when you resize it.");
            Tab = s_file.Bind("Panel", "Tab", TabBasic,
                "Which tab (Basic or Detailed) the overlay last showed. Saved automatically when you switch tabs.");
            Open = s_file.Bind("Panel", "Open", false,
                "Whether the overlay panel was open (not hidden) when you last played. Saved automatically when " +
                "you show or hide it with the toggle key.");
        }

        /// <summary>True only for an exact "Detailed" match -- any other saved value (including "Basic", an
        /// older/foreign value, or a hand-edit typo) means Basic, the same tab F6 always opens to first, rather
        /// than risking an unrecognised OverlayTab state.</summary>
        internal static bool SavedTabIsDetailed => Tab != null && Tab.Value == TabDetailed;

        /// <summary>
        /// Called on every tab transition, from the two places that can cause one: <see cref="OverlayController.HandleToggleKey"/>
        /// (F6 -- the only path that ever crosses the Hidden boundary) and <see cref="OverlayGui.Draw"/> (a
        /// direct click on the Basic/Detailed tab button -- never Hidden). Compares against the PERSISTED
        /// values (not merely "did this session's tab just change") so it self-corrects even if a save was
        /// ever skipped -- writes <see cref="Open"/> only when the panel's Hidden-ness actually differs from
        /// what's saved, and <see cref="Tab"/> only while ending up non-Hidden and only when it actually
        /// differs -- so closing the panel never overwrites the tab it was last showing.
        /// </summary>
        internal static void OnTabOrOpenChanged(OverlayTab current)
        {
            if (s_file == null) return; // defence in depth -- Init already guarantees this is bound on a client only.

            bool isOpen = current != OverlayTab.Hidden;
            if (Open.Value != isOpen) Open.Value = isOpen;

            if (isOpen)
            {
                string name = current == OverlayTab.Detailed ? TabDetailed : TabBasic;
                if (Tab.Value != name) Tab.Value = name;
            }
        }
    }
}
