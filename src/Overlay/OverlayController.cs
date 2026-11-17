using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.Overlay
{
    /// <summary>Which panel (if any) the overlay is currently showing. F6 cycles Hidden -> Basic -> Detailed -> Hidden.</summary>
    internal enum OverlayTab
    {
        Hidden,
        Basic,
        Detailed,
    }

    /// <summary>
    /// The overlay's one MonoBehaviour, on its own hidden GameObject (created by <see cref="OverlayConfig.Init"/>,
    /// client-only — see that class for the dedicated-server/batch-mode gate; this component never exists on a
    /// dedicated server at all, so there is nothing here to additionally disable on that side).
    ///
    /// Owns exactly the state a session's overlay needs to remember (current tab -- Basic or Detailed,
    /// collapsed-to-pill, window position) and the two things OVERLAY SPEC point 3 requires stay apart: <see cref="Update"/> samples
    /// (never more than once a second, via <see cref="OverlaySampler"/>) and reads the toggle key; <see cref="OnGUI"/>
    /// only ever hands the already-cached <see cref="_snapshot"/> to <see cref="OverlayGui"/> to draw. No Harmony
    /// patch, no RPC, no ZDO write, no file write anywhere in this class.
    /// </summary>
    internal sealed class OverlayController : MonoBehaviour
    {
        private readonly OverlaySnapshot _snapshot = new OverlaySnapshot();

        private OverlayTab _tab = OverlayTab.Hidden; // hidden at the start of every session, per OVERLAY SPEC point 2 -- ShowOnStart is applied once, in Awake, below.
        private bool _collapsed;
        private Rect _rect; // (0,0,0,0) until OverlayGui.Draw seeds it from Screen.width/height on first paint.

        private float _nextSample;
        private bool _loggedShownOnce;

        private void Awake()
        {
            // Defence in depth: OverlayConfig.Init already refuses to create this component at all on a
            // dedicated server or in batch mode -- this only guards a future refactor that adds a second call site.
            if (Side.IsDedicatedServerProcess || Application.isBatchMode)
            {
                enabled = false;
                return;
            }

            // OVERLAY SPEC point 3: "open = ShowOnStart || saved Open; tab = saved Tab." OverlayConfig.Init
            // already called OverlayPanelConfig.Init() before creating this component (see that call site's own
            // remarks), so Open/Tab are guaranteed bound here. _rect stays at its default (0,0,0,0) either way --
            // OverlayGui.Draw's own lazy seed (`if (rect.width <= 0f) rect = DefaultRect();`) reads the same
            // saved X/Y/Width/Height on its first call, whether that first call happens because ShowOnStart/saved
            // Open just opened the panel immediately or because the player pressed the toggle key later.
            bool open = OverlayConfig.ShowOnStart.Value || OverlayPanelConfig.Open.Value;
            if (open)
            {
                _tab = OverlayPanelConfig.SavedTabIsDetailed ? OverlayTab.Detailed : OverlayTab.Basic;
            }
        }

        private void Update()
        {
            try
            {
                // 1.0.10 (user: "the network menu is open before a player exists"): no panel and no hotkey until
                // the local player exists, so never on the main menu, character select or loading screen. A panel
                // left open last session (saved Open) or ShowOnStart waits here and appears once in the world, and
                // hides again on logout.
                if (Player.m_localPlayer == null) return;

                HandleToggleKey();

                if (_tab == OverlayTab.Hidden) return;

                float now = Time.realtimeSinceStartup;
                if (now < _nextSample) return;
                _nextSample = now + 1f;
                OverlaySampler.Sample(_snapshot, now);
            }
            catch (System.Exception e)
            {
                // Never let a bug in this read-only display take down the rest of the mod's Update loop, and
                // never route through NoodlesLog.PatchException -- that feeds the GLOBAL exception-burst
                // auto-stand-down trigger, which must never fire because of a display-only overlay.
                NoodlesLog.WarningRateLimited("overlay_update", $"Overlay: Update failed: {e.GetType().Name}: {e.Message}", 3, 60f);
            }
        }

        private void OnGUI()
        {
            if (_tab == OverlayTab.Hidden || Player.m_localPlayer == null) return; // see Update: in-world only

            if (!_loggedShownOnce)
            {
                _loggedShownOnce = true;
                NoodlesLog.Info("Overlay: shown for the first time this session (F6 to cycle Basic/Detailed/hidden).");
            }

            try
            {
                OverlayGui.Draw(_snapshot, ref _rect, ref _collapsed, ref _tab);
            }
            catch (System.Exception e)
            {
                NoodlesLog.WarningRateLimited("overlay_ongui", $"Overlay: draw failed, hiding until next toggle: {e.GetType().Name}: {e.Message}", 3, 60f);
                _tab = OverlayTab.Hidden; // fail closed to "not drawing" rather than risk spamming a broken draw every frame.
            }
        }

        /// <summary>
        /// Hidden -> Network -> Modes -> Hidden on every press, ignored while the player is typing (chat,
        /// console, a text field) exactly like other Valheim mods guard their own hotkeys — see
        /// Fatty/Patches/HudPatches.cs's CanAcceptHotkey, which this mirrors (its Menu/InventoryGui checks are
        /// deliberately NOT included: OVERLAY SPEC point 2 wants the two tabs clickable and the window
        /// draggable while a menu/inventory has the cursor free, so F6 must keep working then too).
        /// </summary>
        private void HandleToggleKey()
        {
            if (!OverlayConfig.ToggleKey.Value.IsDown()) return;
            if (!CanAcceptHotkey()) return;

            _tab = Next(_tab);
            _collapsed = false; // F6 always reveals the full panel, never leaves it stuck as a pill.

            // The only path that ever crosses the Hidden boundary (open/close), so this is also the only place
            // that needs to persist OverlayPanelConfig.Open -- OverlayGui.Draw's own click-driven tab-changed
            // check calls the same method for the Basic<->Detailed case, which never touches Hidden at all.
            OverlayPanelConfig.OnTabOrOpenChanged(_tab);

            if (_tab != OverlayTab.Hidden)
            {
                // Sample immediately so the panel never shows a full second of placeholder "--" just because
                // it happened to appear between two 1Hz ticks.
                OverlaySampler.Sample(_snapshot, Time.realtimeSinceStartup);
                _nextSample = Time.realtimeSinceStartup + 1f;
            }
        }

        private static OverlayTab Next(OverlayTab t)
        {
            switch (t)
            {
                case OverlayTab.Hidden: return OverlayTab.Basic;
                case OverlayTab.Basic: return OverlayTab.Detailed;
                default: return OverlayTab.Hidden;
            }
        }

        private static bool CanAcceptHotkey() =>
            (Chat.instance == null || !Chat.instance.HasFocus()) && !Console.IsVisible() && !TextInput.IsVisible();
    }
}
