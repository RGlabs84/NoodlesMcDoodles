using NoodlesMcDoodles.Core;
using UnityEngine;

namespace NoodlesMcDoodles.Overlay
{
    /// <summary>
    /// Draws <see cref="OverlaySnapshot"/> in Njord's own flat black/gold HUD style
    /// (Njord/HUD/NjordHudManager.cs: a 70%-black IMGUI panel, gold headers, cream labels, the same five font
    /// sizes) — deliberately NOT Fatty's gilt-frame look (GiltFrameTheme's ornaments/gilt band are not used
    /// anywhere here). The panel background, its colour/opacity, and the proportion bar are drawn the exact way
    /// Njord draws its own background and HP bar: tint <see cref="GUI.color"/>, draw a plain box/texture,
    /// restore it.
    ///
    /// The two-tab / collapsible-pill / drag-by-title-strip / resize-by-corner-grip / scroll-per-tab STRUCTURE
    /// is Fatty's idea (Fatty/Patches/FeastLedgerGui.cs), ported as a concept rather than as code: this class
    /// never uses <c>GUI.Window</c> (Fatty's own header explains a real hardware-cursor/GUI-matrix bug that
    /// came from combining GUI.Window with several other IMGUI-drawing plugins sharing one profile) or Fatty's
    /// matrix workaround/click-logging — dragging and resizing are the same plain <c>Event.current</c>
    /// technique <see cref="NjordHudManager.HandleDragging"/> already uses for dragging, just scoped to the
    /// title strip's (or the corner grip's) own Rect instead of the whole panel, and the collapse/pill buttons
    /// are ordinary <c>GUI.Button</c>s on rects that never overlap the drag strip, so a click can never be
    /// mistaken for a drag or vice versa.
    ///
    /// Panel geometry (X/Y/Width/Height), the active tab and whether the panel is open are persisted in
    /// <see cref="OverlayPanelConfig"/> — a SEPARATE BepInEx ConfigFile from the mod's main .cfg, written only
    /// on a drag/resize's own mouse-up (<see cref="HandleDrag"/>/<see cref="HandleResize"/>) or a tab/open
    /// change (<see cref="Draw"/>'s own tab-changed check; <see cref="OverlayController"/> handles the
    /// Hidden-boundary case, which this class never sees) — never per frame. See that class's own remarks.
    ///
    /// Every string drawn here comes from the already-formatted <see cref="OverlaySnapshot"/> or from a
    /// compile-time string literal (a literal reference allocates nothing, unlike building one at draw time) —
    /// no concatenation, interpolation or LINQ runs inside <see cref="Draw"/> or anything it calls (OVERLAY SPEC
    /// point 3). Styles and the one shared 1x1 texture are created once, lazily, the first time the panel is
    /// actually drawn (mirrors both NjordHudManager.CreateStylesIfNeeded and probe/NoodlesProbe.cs's own remark
    /// that "GUIStyle/GUI.skin are only valid inside OnGUI").
    ///
    /// Basic tab = the original Network tab, plus two summary lines at the bottom. Detailed tab folds in the
    /// original Modes tab's "This client"/"Server" rows alongside four sections (Upload, Send queue, Download,
    /// Zones around you) — there is no third tab; F6 still only ever cycles Hidden -> Basic -> Detailed -> Hidden
    /// (see <see cref="OverlayController"/>).
    /// </summary>
    internal static class OverlayGui
    {
        private const float Padding = 10f;
        private const float TitleHeight = 22f;
        private const float TabHeight = 20f;
        private const float CollapseBtnSize = 20f;
        private const float PillWidth = 120f;
        private const float PillHeight = 26f;

        // OVERLAY SPEC point 1: resizable, clamped to a sane range. Max is screen size minus a margin rather
        // than a fixed number, so it never lets the panel grow bigger than the screen (or stays needlessly
        // small on a large one) -- recomputed against the CURRENT Screen.width/height everywhere it's used
        // (ClampWidth/ClampHeight below), so a mid-session resolution change is honoured too, not just startup.
        private const float MinPanelWidth = 260f;
        private const float MinPanelHeight = 220f;
        private const float MaxSizeMargin = 40f;

        // OVERLAY SPEC point 2: a two-column Row()'s label column is a FRACTION of the panel's CURRENT width
        // (clamped to a sane pixel range), replacing the old fixed 168px RowLabelWidth constant, so it scales
        // as the panel is resized instead of eating an ever-larger or ever-smaller share of it.
        private const float LabelWidthFraction = 0.55f;
        // The vertical scrollbar's width plus a margin, reserved whether or not the bar is showing, so a right-aligned
        // value never ends underneath it (live 2026-09-29: the Detailed tab's values were clipped to "256", "0.").
        private const float ScrollbarReserve = 20f;
        private const float MinLabelWidth = 90f;
        private const float MaxLabelWidth = 220f;

        private const float GripSize = 16f;
        private const float GripMargin = 2f;

        // Njord/HUD/NjordHudManager.cs's own palette, unchanged, plus one darker shade (DimGold) this panel
        // needs for an inactive tab that Njord itself never had a use for.
        private static readonly Color GoldHeader = new Color(0.9f, 0.8f, 0.5f);
        private static readonly Color GoldSection = new Color(0.85f, 0.75f, 0.55f);
        private static readonly Color Cream = new Color(0.95f, 0.9f, 0.8f);
        private static readonly Color DimGold = new Color(0.55f, 0.47f, 0.32f);

        private const string OwnershipHeader = "── Who runs the world around you ──";
        private const string ConnectionHeader = "── Connection ──────────────────";
        private const string UploadHeader = "── Upload (you -> server) ──────";
        private const string QueueHeader = "── Send queue ──────────────────";
        private const string DownloadHeader = "── Download (server -> you) ────";
        private const string ZonesHeader = "── Zones around you ────────────";
        private const string ServerHeader = "── Server ──────────────────────";
        private const string ClientHeader = "── This client ───────────────";
        private const string TopPlayersLabel = "Players controlling objects near you";
        private const string DownloadUnavailableNote =
            "The server doesn't report download numbers (vanilla server or older NoodlesMcDoodles).";
        private const string StalledNote = "Held at vanilla by the server's stall guard.";

        private static bool _stylesReady;
        private static GUIStyle _titleStyle;
        private static GUIStyle _tabActiveStyle;
        private static GUIStyle _tabInactiveStyle;
        private static GUIStyle _sectionStyle;
        private static GUIStyle _labelStyle;
        private static GUIStyle _labelRightStyle;
        private static GUIStyle _smallStyle;
        private static GUIStyle _zoneCellStyle;
        private static GUIStyle _zoneHereStyle;
        private static GUIStyle _collapseBtnStyle;
        private static GUIStyle _pillStyle;
        private static Texture2D _whiteTex;

        // Drag state -- deliberately static, like NjordHudManager's own _dragging/_dragStartMouse/_dragStartHud:
        // there is exactly one overlay instance for the whole process, so this needs no per-instance home.
        private static bool _dragging;
        private static Vector2 _dragStartMouse;
        private static Vector2 _dragStartRect;

        // Resize state, same pattern as the drag state above -- see HandleResize.
        private static bool _resizing;
        private static Vector2 _resizeStartMouse;
        private static Vector2 _resizeStartSize;

        // This draw's inner content width / two-column label width, recomputed once per Draw() call from the
        // CURRENT rect.width (replaces the old fixed InnerWidth/RowLabelWidth consts now that the panel is
        // resizable). Read by Row/LeftRow/DrawZoneGridRow/DrawProportionBar/DrawTab, all only ever called from
        // within Draw().
        private static float _innerWidth;
        private static float _labelWidth;
        private static float _contentWidth; // _innerWidth minus ScrollbarReserve: the width every scrolled row is laid out in

        // Scroll position, kept separately per tab (OVERLAY SPEC point 2's own "keep the scroll position per
        // tab") so switching tabs never carries one tab's scroll offset into the other.
        private static Vector2 _scrollBasic;
        private static Vector2 _scrollDetailed;

        // Six-dot triangular resize handle (the same glyph most desktop toolkits use for a bottom-right corner
        // grip), anchored to the grip rect's own bottom-right corner -- (0,0) is the corner-most dot, larger
        // values step up and to the left. Drawn with plain axis-aligned squares (the same _whiteTex every other
        // flat shape in this file already uses) rather than rotated lines, so it needs no matrix save/restore.
        private static readonly Vector2[] s_gripDots =
        {
            new Vector2(0f, 0f),
            new Vector2(0f, 1f), new Vector2(1f, 0f),
            new Vector2(0f, 2f), new Vector2(1f, 1f), new Vector2(2f, 0f),
        };

        internal static void Draw(OverlaySnapshot snap, ref Rect rect, ref bool collapsed, ref OverlayTab tab)
        {
            EnsureStyles();

            if (rect.width <= 0f) rect = DefaultRect();
            bool interactive = Cursor.visible; // matches OVERLAY SPEC point 2: clicking/dragging only while the cursor is free.

            if (collapsed)
            {
                // Computed as a LOCAL copy, not written into rect.width/height: rect's width/height are the
                // persisted EXPANDED panel size, which must survive being collapsed to a pill and back (OVERLAY
                // SPEC point 3 -- there is no separate "pill size" key, the pill is always PillWidth x PillHeight).
                Rect pillRect = ClampToScreen(new Rect(rect.x, rect.y, PillWidth, PillHeight));
                rect.x = pillRect.x;
                rect.y = pillRect.y;
                DrawPill(pillRect, ref collapsed, interactive);
                return;
            }

            rect.width = ClampWidth(rect.width);
            rect.height = ClampHeight(rect.height);
            rect = ClampToScreen(rect);

            _innerWidth = rect.width - Padding * 2f;
            _contentWidth = Mathf.Max(100f, _innerWidth - ScrollbarReserve);
            _labelWidth = Mathf.Clamp(_contentWidth * LabelWidthFraction, MinLabelWidth, MaxLabelWidth);

            DrawBackground(rect);

            // The drag-hit rect covers the title text only, NOT the collapse button's own column -- so a click
            // on the button is never also read as the start of a drag (Fatty/Patches/FeastLedgerGui.cs's own
            // "drag region is the whole title band MINUS the toggle button" note, same reasoning here).
            Rect titleStrip = new Rect(rect.x, rect.y, rect.width - CollapseBtnSize - Padding * 2f, TitleHeight + Padding);

            Rect inner = new Rect(rect.x + Padding, rect.y + Padding, _innerWidth, rect.height - Padding * 2f);
            GUILayout.BeginArea(inner);
            GUILayout.BeginVertical();

            GUILayout.BeginHorizontal();
            GUILayout.Label("NoodlesMcDoodles", _titleStyle);
            GUILayout.FlexibleSpace();
            if (interactive)
            {
                if (GUILayout.Button("▼", _collapseBtnStyle, GUILayout.Width(CollapseBtnSize), GUILayout.Height(TitleHeight)))
                {
                    collapsed = true;
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4f);

            // Read the active tab once, before either DrawTab call can change it on a click; also the "before"
            // value for this draw's own tab-changed check further down (a click on a tab button is the one tab
            // change OverlayController's F6 handler never sees, so it has to be caught here instead).
            bool basicActive = tab == OverlayTab.Basic;
            bool detailedActive = tab == OverlayTab.Detailed;
            OverlayTab tabAtEntry = tab;
            GUILayout.BeginHorizontal();
            DrawTab("Basic", basicActive, interactive, ref tab, OverlayTab.Basic, _innerWidth * 0.5f);
            DrawTab("Detailed", detailedActive, interactive, ref tab, OverlayTab.Detailed, _innerWidth * 0.5f);
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);

            // OVERLAY SPEC point 2: a vertical-only scroll view (GUIStyle.none for the horizontal bar disables
            // it outright rather than relying on content never overflowing) so every row is reachable at any
            // panel height, with a separate remembered scroll position per tab.
            Vector2 scroll = tab == OverlayTab.Basic ? _scrollBasic : _scrollDetailed;
            scroll = GUILayout.BeginScrollView(scroll, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.ExpandHeight(true));
            GUILayout.BeginVertical(GUILayout.Width(_contentWidth)); // every row fits left of the scrollbar
            if (tab == OverlayTab.Basic) DrawBasicTab(snap);
            else DrawDetailedTab(snap);
            GUILayout.EndVertical();
            GUILayout.EndScrollView();
            if (tab == OverlayTab.Basic) _scrollBasic = scroll;
            else _scrollDetailed = scroll;

            GUILayout.EndVertical();
            GUILayout.EndArea();

            HandleDrag(titleStrip, ref rect, interactive);
            HandleResize(ref rect, interactive);

            // Catches a click on a tab button (F6's own Hidden<->Basic/Detailed transitions are persisted from
            // OverlayController.HandleToggleKey instead, the only place that ever sees the Hidden boundary --
            // Draw() isn't even called while hidden). Same OverlayPanelConfig entry point either way.
            if (tab != tabAtEntry)
            {
                OverlayPanelConfig.OnTabOrOpenChanged(tab);
            }
        }

        // ---- Basic tab -----------------------------------------------------------------------------------------

        private static void DrawBasicTab(OverlaySnapshot s)
        {
            FullWidth(OwnershipHeader, _sectionStyle);
            Row("Server owns", s.ServerOwnValue);
            Row("You own", s.YouOwnValue);
            Row("Other players own", s.OtherOwnValue);
            Row("Unowned", s.UnownedValue);

            Rect lastRow = GUILayoutUtility.GetLastRect();
            Rect barRect = new Rect(lastRow.x, lastRow.yMax + 4f, _contentWidth, 6f);
            DrawProportionBar(barRect, s);
            GUILayout.Space(6f + 8f); // reserve the space the bar occupied -- drawn outside GUILayout, like NjordHudManager's own HP bar.

            GUILayout.Space(4f);
            FullWidth(ConnectionHeader, _sectionStyle);
            Row("Ping", s.PingValue);
            Row("Upload, actual", s.UploadValue);
            Row("Download, actual", s.DownloadValue);
            Row("Send queue", s.SendQueueValue);
            Row("Upload limit", s.UploadLimitValue);
            Row("Download limit", s.DownloadLimitValue);

            GUILayout.Space(8f);
            LeftRow("Server:", s.ServerLine);
            LeftRow("Mode:", s.ModeLine);
        }

        /// <summary>
        /// One thin bar, three solid gold-family segments (server/you/other-players) left to right in that
        /// order over a faint track; whatever's left unfilled is "unowned" -- never drawn over, so it reads as
        /// the track colour rather than needing a fourth, dimmer-still shade of the same palette.
        /// </summary>
        private static void DrawProportionBar(Rect r, OverlaySnapshot s)
        {
            Color prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.10f);
            GUI.DrawTexture(r, _whiteTex);

            float x = r.x;
            x = DrawSegment(x, r, s.ServerFrac, new Color(0.95f, 0.82f, 0.35f, 0.95f));
            x = DrawSegment(x, r, s.YouFrac, new Color(0.85f, 0.75f, 0.55f, 0.95f));
            DrawSegment(x, r, s.OtherFrac, new Color(0.55f, 0.47f, 0.32f, 0.95f));

            GUI.color = prev;
        }

        private static float DrawSegment(float x, Rect bar, float frac, Color color)
        {
            if (frac <= 0f) return x;
            float w = bar.width * frac;
            GUI.color = color;
            GUI.DrawTexture(new Rect(x, bar.y, w, bar.height), _whiteTex);
            return x + w;
        }

        // ---- Detailed tab ---------------------------------------------------------------------------------------

        private static void DrawDetailedTab(OverlaySnapshot s)
        {
            FullWidth(UploadHeader, _sectionStyle);
            Row("Upload limit, set", s.UploadLimitSetValue);
            Row("Upload limit, now", s.UploadLimitValue);
            Row("Upload, actual", s.UploadValue);
            Row("Ping", s.PingValue);
            Row("Quality, local", s.ConnQualityLocalValue);
            Row("Quality, remote", s.ConnQualityRemoteValue);

            GUILayout.Space(8f);
            FullWidth(QueueHeader, _sectionStyle);
            Row("Queue, now", s.SendQueueValue);
            Row("Queue, peak 60s", s.SendQueuePeakValue);
            Row("Queue allowance, set", s.QueueAllowanceSetValue);
            Row("Queue allowance, now", s.QueueAllowanceNowValue);

            GUILayout.Space(8f);
            FullWidth(DownloadHeader, _sectionStyle);
            if (s.ServerLinkAvailable)
            {
                Row("Limit, set by server", s.DownloadLimitSetValue);
                Row("Limit, now", s.DownloadLimitNowValue);
                Row("Actual", s.DownloadActualValue);
                Row("Actual, measured here", s.DownloadValue);
                Row("Server queue for you", s.ServerQueueForYouValue);
                Row("Allowance", s.ServerQueueAllowanceValue);
                Row("Server's ping to you", s.ServerPingToYouValue);
                if (s.ServerLinkStalled)
                {
                    FullWidth(StalledNote, _smallStyle);
                }
            }
            else
            {
                FullWidth(DownloadUnavailableNote, _smallStyle);
            }

            GUILayout.Space(8f);
            FullWidth(ZonesHeader, _sectionStyle);
            LeftRow("Zone:", s.ZoneHereValue);
            GUILayout.Space(2f);
            DrawZoneGrid(s.ZoneGrid);
            GUILayout.Space(4f);
            Row("Server", s.ZoneMixServerValue);
            Row("You", s.ZoneMixYouValue);
            Row("Other players", s.ZoneMixOtherValue);
            GUILayout.Space(4f);
            FullWidth(TopPlayersLabel, _smallStyle);
            DrawZoneTopPlayers(s.ZoneTopPlayers);

            GUILayout.Space(8f);
            FullWidth(ServerHeader, _sectionStyle);
            LeftRow("Server:", s.ServerLine);
            LeftRow("Mode:", s.ModeLine);
            FullWidth(s.ModeExplain, _smallStyle);

            GUILayout.Space(8f);
            FullWidth(ClientHeader, _sectionStyle);
            Row("Upload (Q10)", s.Q10Value);
            Row("Queue allowance (Q13)", s.Q13Value);
            Row("Extrapolation", s.ExtrapolationValue);
            Row("Compression", s.CompressionValue);
            Row("Kill switch", s.KillSwitchValue);
            Row("Version", PluginInfo.PLUGIN_VERSION);
        }

        /// <summary>NW N NE / W Here E / SW S SE, row-major over <paramref name="grid"/> (OverlaySnapshot.ZoneGrid).</summary>
        private static void DrawZoneGrid(string[] grid)
        {
            DrawZoneGridRow(grid, 0);
            DrawZoneGridRow(grid, 3);
            DrawZoneGridRow(grid, 6);
        }

        private static void DrawZoneGridRow(string[] grid, int startIndex)
        {
            GUILayout.BeginHorizontal();
            for (int i = 0; i < 3; i++)
            {
                int idx = startIndex + i;
                GUIStyle style = idx == 4 ? _zoneHereStyle : _zoneCellStyle; // index 4 is always "Here" -- marked gold.
                GUILayout.Label(grid[idx], style, GUILayout.Width(_contentWidth / 3f));
            }
            GUILayout.EndHorizontal();
        }

        /// <summary>Up to 4 already-formatted "name  N objects" lines; "" (OverlaySnapshot's default for an
        /// unused slot) draws nothing, so a zone with fewer than 4 other contributors just shows fewer rows.</summary>
        private static void DrawZoneTopPlayers(string[] rows)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                if (string.IsNullOrEmpty(rows[i])) continue;
                FullWidth(rows[i], _labelStyle);
            }
        }

        // ---- Small drawing helpers ----------------------------------------------------------------------------

        /// <summary>One line (or wrapped lines) exactly as wide as the scrolled content, left of the scrollbar.</summary>
        private static void FullWidth(string text, GUIStyle style)
        {
            GUILayout.Label(text, style, GUILayout.Width(_contentWidth));
        }

        /// <summary>Label left (a fraction of the current panel width) / value right-aligned and word-wrapped in
        /// the remaining space -- OVERLAY SPEC point 2's own two-column layout, reused by most Detailed rows too.</summary>
        private static void Row(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _labelStyle, GUILayout.Width(_labelWidth));
            GUILayout.Label(value, _labelRightStyle, GUILayout.Width(Mathf.Max(40f, _contentWidth - _labelWidth - 4f))); // fixed width: wraps, never clips
            GUILayout.EndHorizontal();
        }

        /// <summary>Label then value, both left-aligned and flowing naturally -- used for the Server:/Mode:/Zone:
        /// lines, whose values ("Runs NoodlesMcDoodles 0.1.1", "(12, -5)", ...) are longer than a narrow
        /// right-aligned value column comfortably fits. Word-wraps (via the now-wrapping _labelStyle) rather
        /// than clipping when a value is long.</summary>
        private static void LeftRow(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _labelStyle, GUILayout.Width(52f));
            GUILayout.Label(value, _labelStyle, GUILayout.Width(Mathf.Max(40f, _contentWidth - 56f)));
            GUILayout.EndHorizontal();
        }

        private static void DrawTab(string label, bool active, bool interactive, ref OverlayTab tab, OverlayTab which, float width)
        {
            GUIStyle style = active ? _tabActiveStyle : _tabInactiveStyle;
            bool clicked = false;
            if (interactive)
            {
                clicked = GUILayout.Button(label, style, GUILayout.Width(width), GUILayout.Height(TabHeight));
            }
            else
            {
                GUILayout.Label(label, style, GUILayout.Width(width), GUILayout.Height(TabHeight));
            }

            if (active)
            {
                Rect r = GUILayoutUtility.GetLastRect();
                Color prev = GUI.color;
                GUI.color = GoldHeader;
                GUI.DrawTexture(new Rect(r.x, r.yMax - 2f, r.width, 2f), _whiteTex);
                GUI.color = prev;
            }

            if (clicked) tab = which;
        }

        private static void DrawPill(Rect rect, ref bool collapsed, bool interactive)
        {
            if (interactive)
            {
                if (GUI.Button(rect, "NOODLES", _pillStyle)) collapsed = false;
            }
            else
            {
                GUI.Box(rect, "NOODLES", _pillStyle);
            }
        }

        private static void DrawBackground(Rect rect)
        {
            Color prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, OverlayConfig.Opacity.Value);
            GUI.Box(rect, GUIContent.none);
            GUI.color = prev;
        }

        /// <summary>Plain Event.current drag, scoped to <paramref name="hitRect"/> only -- the same technique as
        /// NjordHudManager.HandleDragging, just not tied to the whole panel's Rect. Committed to
        /// <see cref="OverlayPanelConfig"/> only on the drag's own mouse-up, never per frame (OVERLAY SPEC point 3).</summary>
        private static void HandleDrag(Rect hitRect, ref Rect rect, bool interactive)
        {
            if (!interactive)
            {
                _dragging = false;
                return;
            }

            Event e = Event.current;
            if (e == null) return;

            if (!_dragging && e.type == EventType.MouseDown && e.button == 0 && hitRect.Contains(e.mousePosition))
            {
                _dragging = true;
                _dragStartMouse = e.mousePosition;
                _dragStartRect = new Vector2(rect.x, rect.y);
                e.Use();
            }
            else if (_dragging && e.type == EventType.MouseDrag && e.button == 0)
            {
                Vector2 delta = e.mousePosition - _dragStartMouse;
                rect.x = _dragStartRect.x + delta.x;
                rect.y = _dragStartRect.y + delta.y;
                e.Use();
            }
            else if (_dragging && (e.type == EventType.MouseUp || e.rawType == EventType.MouseUp))
            {
                _dragging = false;
                e.Use();
                OverlayPanelConfig.X.Value = rect.x;
                OverlayPanelConfig.Y.Value = rect.y;
            }
        }

        /// <summary>
        /// Bottom-right corner grip, handled exactly like <see cref="HandleDrag"/> (a plain Event.current drag)
        /// but applied to <paramref name="rect"/>'s SIZE instead of its position -- mirrors Fatty's own
        /// HandleResize (Fatty/Patches/FeastLedgerGui.cs), just in screen space directly rather than via a
        /// GUI.Window callback (this class never uses GUI.Window at all -- see the class remarks). Clamped every
        /// frame to <see cref="MinPanelWidth"/>/<see cref="MinPanelHeight"/> and the current screen size minus
        /// <see cref="MaxSizeMargin"/> (OVERLAY SPEC point 1). Committed to <see cref="OverlayPanelConfig"/> only
        /// on the drag's own mouse-up, never per frame.
        /// </summary>
        private static void HandleResize(ref Rect rect, bool interactive)
        {
            Rect grip = new Rect(rect.xMax - GripSize - GripMargin, rect.yMax - GripSize - GripMargin, GripSize, GripSize);
            DrawResizeGrip(grip);

            if (!interactive)
            {
                _resizing = false;
                return;
            }

            Event e = Event.current;
            if (e == null) return;

            if (!_resizing && e.type == EventType.MouseDown && e.button == 0 && grip.Contains(e.mousePosition))
            {
                _resizing = true;
                _resizeStartMouse = e.mousePosition;
                _resizeStartSize = new Vector2(rect.width, rect.height);
                e.Use();
            }
            else if (_resizing && e.type == EventType.MouseDrag && e.button == 0)
            {
                Vector2 delta = e.mousePosition - _resizeStartMouse;
                rect.width = ClampWidth(_resizeStartSize.x + delta.x);
                rect.height = ClampHeight(_resizeStartSize.y + delta.y);
                e.Use();
            }
            else if (_resizing && (e.type == EventType.MouseUp || e.rawType == EventType.MouseUp))
            {
                _resizing = false;
                e.Use();
                OverlayPanelConfig.Width.Value = rect.width;
                OverlayPanelConfig.Height.Value = rect.height;
            }
        }

        private static void DrawResizeGrip(Rect grip)
        {
            Color prev = GUI.color;
            GUI.color = GoldHeader;

            const float dot = 2.5f;
            const float step = 4.5f;
            for (int i = 0; i < s_gripDots.Length; i++)
            {
                float gx = grip.xMax - (s_gripDots[i].x + 1f) * step;
                float gy = grip.yMax - (s_gripDots[i].y + 1f) * step;
                GUI.DrawTexture(new Rect(gx, gy, dot, dot), _whiteTex);
            }

            GUI.color = prev;
        }

        private static float ClampWidth(float w)
        {
            float max = Screen.width >= 1 ? Mathf.Max(MinPanelWidth, Screen.width - MaxSizeMargin) : Mathf.Max(MinPanelWidth, w);
            return Mathf.Clamp(w, MinPanelWidth, max);
        }

        private static float ClampHeight(float h)
        {
            float max = Screen.height >= 1 ? Mathf.Max(MinPanelHeight, Screen.height - MaxSizeMargin) : Mathf.Max(MinPanelHeight, h);
            return Mathf.Clamp(h, MinPanelHeight, max);
        }

        /// <summary>
        /// A fresh install's default panel (OVERLAY SPEC point 2: 320x480 at the current top-right, matching
        /// <see cref="OverlayPanelConfig"/>'s own Width/Height/Y defaults), or a returning session's saved
        /// geometry -- either way clamped to the CURRENT screen, so a resolution change (or a save carried over
        /// from a bigger monitor) can never leave the panel stranded off-screen or oversized.
        /// </summary>
        private static Rect DefaultRect()
        {
            float width = ClampWidth(OverlayPanelConfig.Width.Value);
            float height = ClampHeight(OverlayPanelConfig.Height.Value);

            // X has no fixed default -- "top-right" depends on the CURRENT screen width, which OverlayPanelConfig
            // can't know at bind time. NaN is its sentinel for "never saved" (see that class's own remarks); any
            // real saved position is a normal float and never compares equal to NaN, so this can't misfire on a
            // genuine saved value.
            float savedX = OverlayPanelConfig.X.Value;
            float x = float.IsNaN(savedX) ? Mathf.Max(16f, Screen.width - width - 16f) : savedX;
            float y = OverlayPanelConfig.Y.Value;

            return ClampToScreen(new Rect(x, y, width, height));
        }

        private static Rect ClampToScreen(Rect r)
        {
            if (Screen.width < 1 || Screen.height < 1) return r; // a transient 0-sized screen (alt-tab/minimise) -- never clamp to junk.
            const float Keep = 24f;
            float minX = Keep - r.width;
            float maxX = Mathf.Max(minX, Screen.width - Keep);
            r.x = Mathf.Clamp(r.x, minX, maxX);
            r.y = Mathf.Clamp(r.y, 0f, Mathf.Max(0f, Screen.height - Keep));
            return r;
        }

        private static void EnsureStyles()
        {
            if (_stylesReady) return;
            _stylesReady = true;

            _whiteTex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            _whiteTex.SetPixel(0, 0, Color.white);
            _whiteTex.Apply();

            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                normal = { textColor = GoldHeader }
            };

            _tabActiveStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = GoldHeader },
                hover = { textColor = GoldHeader }
            };

            _tabInactiveStyle = new GUIStyle(_tabActiveStyle)
            {
                fontStyle = FontStyle.Normal,
                normal = { textColor = DimGold },
                hover = { textColor = GoldSection }
            };

            _sectionStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                normal = { textColor = GoldSection },
                wordWrap = false,               // a header's trailing "────" clips at the content edge instead of wrapping
                clipping = TextClipping.Clip
            };

            // wordWrap = true (OVERLAY SPEC point 2): text wraps to the current panel width instead of running
            // past it or being clipped -- this is the one change that makes both Row() (via _labelRightStyle,
            // which inherits it) and LeftRow() wrap their values.
            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                wordWrap = true,
                normal = { textColor = Cream }
            };

            _labelRightStyle = new GUIStyle(_labelStyle) { alignment = TextAnchor.MiddleRight };

            _smallStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                wordWrap = true,
                normal = { textColor = Cream }
            };

            _zoneCellStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Cream }
            };

            _zoneHereStyle = new GUIStyle(_zoneCellStyle)
            {
                fontStyle = FontStyle.Bold,
                normal = { textColor = GoldHeader }
            };

            _collapseBtnStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = GoldHeader, background = null },
                hover = { textColor = GoldHeader, background = null },
                active = { textColor = GoldHeader, background = null }
            };

            _pillStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            _pillStyle.normal.textColor = GoldHeader;
            _pillStyle.normal.background = MakeTex(new Color(0f, 0f, 0f, 0.85f));
            _pillStyle.hover.textColor = GoldHeader;
            _pillStyle.hover.background = _pillStyle.normal.background;
            _pillStyle.active.textColor = GoldHeader;
            _pillStyle.active.background = _pillStyle.normal.background;
        }

        // HideAndDontSave for the same reason Fatty/Patches/FeastLedgerGui.cs's own MakeTex flags its textures:
        // without it Unity collects these on the next scene load, and _stylesReady latching true means they'd
        // never be rebuilt -- the pill's own background would silently start drawing from a destroyed texture
        // after one world reload, on the one control that's the sole way back into a collapsed panel.
        private static Texture2D MakeTex(Color c)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            tex.SetPixel(0, 0, c);
            tex.Apply();
            return tex;
        }
    }
}
