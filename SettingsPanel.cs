using OpenTK.Mathematics;

namespace SolarSystem;

/// <summary>
/// Q12 (v2): In-app settings panel. Hand-rolled "ImGui-lite" overlay rendered
/// with the existing <see cref="BitmapFont"/> + <see cref="Renderer.FillRect"/>
/// path so it works on any OpenGL context without bringing in a full
/// immediate-mode UI.
///
/// Rows are grouped into <see cref="FeatureCategory"/> tabs. Each row is a
/// toggle (<c>[ ]</c> / <c>[x]</c>), a slider with <c>-</c>/<c>+</c> nudge hit
/// boxes, or a button. The right edge of every row shows the keyboard chord
/// bound to it (if any) and the footer shows the hovered row's description.
/// Scene tabs additionally expose preset buttons plus <c>Reset</c> /
/// <c>All on</c> / <c>All off</c>. Click coordinates are tested against the
/// row's bounding box captured at the moment the panel is rendered.
/// </summary>
public sealed class SettingsPanel
{
    public bool Visible { get; set; }

    public abstract class Row
    {
        public required string Label;
        public FeatureCategory Category = FeatureCategory.Bodies;
        /// <summary>Right-aligned hint, typically the hotkey chord.</summary>
        public string Hint = "";
        /// <summary>Optional description resolver (already localised).</summary>
        public Func<string>? Description;
        /// <summary>Returns a localisation key when the row is greyed out.</summary>
        public Func<string?>? Unavailable;
        public Box Bounds;
        public Box Minus;
        public Box Plus;

        public bool IsAvailable => Unavailable == null || Unavailable() == null;
    }

    public sealed class ToggleRow : Row
    {
        public required Func<bool> Get;
        public required Action Toggle;
    }

    public sealed class SliderRow : Row
    {
        public required Func<float> Get;
        public required Action<float> Set;
        public float Min;
        public float Max;
        public float Step;
        public string Format = "{0:0.##}";
        /// <summary>Pixel rectangle of the rendered <c>[████░░░]</c> bar — used
        /// for click→value mapping so the cursor lines up with the visible cells
        /// instead of the (much wider) full row Bounds.</summary>
        public Box Track;
    }

    /// <summary>A one-shot action row (screenshot, cycle language, …). The
    /// optional <see cref="Status"/> text is appended after an arrow.</summary>
    public sealed class ButtonRow : Row
    {
        public required Action Run;
        public Func<string>? Status;
    }

    public struct Box(float x, float y, float w, float h)
    {
        public float X = x, Y = y, W = w, H = h;
        public readonly bool Contains(Vector2 p) => p.X >= X && p.X <= X + W && p.Y >= Y && p.Y <= Y + H;
        public static readonly Box Empty = new(0, -1, 0, 0);
    }

    private readonly List<Row> _rows = new();

    public void Add(Row row) => _rows.Add(row);
    public void Clear() => _rows.Clear();

    // ---- Tabs / presets wiring (set by the host) --------------------------------

    public FeatureCategory ActiveTab { get; set; } = FeatureCategory.Bodies;

    /// <summary>Tabs shown in the header, in order.</summary>
    public IReadOnlyList<FeatureCategory> Tabs { get; set; } = new[]
    {
        FeatureCategory.Bodies, FeatureCategory.Simulation, FeatureCategory.Effects,
        FeatureCategory.PostFx, FeatureCategory.Interface, FeatureCategory.Developer,
    };

    public IReadOnlyList<FeaturePreset> Presets { get; set; } = Array.Empty<FeaturePreset>();
    public Action<FeaturePreset>? OnPreset { get; set; }
    public Func<FeaturePreset, bool>? IsPresetActive { get; set; }
    public Action<FeatureCategory>? OnReset { get; set; }
    public Action<FeatureCategory, bool>? OnSetAll { get; set; }
    /// <summary>Which tabs get the preset / reset / all-on-off rows.</summary>
    public Func<FeatureCategory, bool> IsSceneTab { get; set; }
        = c => Array.IndexOf(FeatureRegistry.SceneCategories, c) >= 0;

    private readonly List<(Box Box, FeatureCategory Tab)> _tabHits = new();
    private readonly List<(Box Box, FeaturePreset Preset)> _presetHits = new();
    private Box _resetBtn = Box.Empty, _allOnBtn = Box.Empty, _allOffBtn = Box.Empty, _closeBtn = Box.Empty;

    /// <summary>Returns true if the click was consumed by the panel (tab switched,
    /// toggle flipped, slider nudged, button pressed) — false to let the host
    /// handle it (e.g. as camera input).</summary>
    public bool TryHandleClick(Vector2 mouse)
    {
        if (!Visible) return false;

        foreach (var (box, tab) in _tabHits)
            if (box.Contains(mouse)) { ActiveTab = tab; _scroll = 0f; return true; }
        foreach (var (box, preset) in _presetHits)
            if (box.Contains(mouse)) { OnPreset?.Invoke(preset); return true; }
        if (_resetBtn.Contains(mouse)) { OnReset?.Invoke(ActiveTab); return true; }
        if (_allOnBtn.Contains(mouse)) { OnSetAll?.Invoke(ActiveTab, true); return true; }
        if (_allOffBtn.Contains(mouse)) { OnSetAll?.Invoke(ActiveTab, false); return true; }
        if (_closeBtn.Contains(mouse)) { Visible = false; return true; }

        foreach (var row in _rows)
        {
            if (row is ToggleRow t && t.Bounds.Contains(mouse))
            {
                if (t.IsAvailable) t.Toggle();
                return true;
            }
            if (row is ButtonRow b && b.Bounds.Contains(mouse))
            {
                if (b.IsAvailable) b.Run();
                return true;
            }
            if (row is SliderRow s)
            {
                if (s.Minus.Contains(mouse))
                {
                    s.Set(MathHelper.Clamp(s.Get() - s.Step, s.Min, s.Max));
                    return true;
                }
                if (s.Plus.Contains(mouse))
                {
                    s.Set(MathHelper.Clamp(s.Get() + s.Step, s.Min, s.Max));
                    return true;
                }
                if (s.Track.W > 0f && s.Track.Contains(mouse))
                {
                    float t01 = MathHelper.Clamp((mouse.X - s.Track.X) / MathF.Max(1f, s.Track.W), 0f, 1f);
                    float v = s.Min + (s.Max - s.Min) * t01;
                    if (s.Step > 0f) v = MathF.Round(v / s.Step) * s.Step;
                    s.Set(MathHelper.Clamp(v, s.Min, s.Max));
                    return true;
                }
            }
        }
        // Click anywhere else inside the panel rectangle — consume but do nothing
        // so the click doesn't fall through to the camera.
        return _panelHit;
    }

    private bool _panelHit;

    /// <summary>Vertical scroll offset (in pixels) applied to the row list when
    /// the panel content is taller than the viewport. Mouse-wheel events
    /// forwarded via <see cref="HandleScroll"/> nudge this value.</summary>
    private float _scroll;

    /// <summary>Total content height computed during the last <see cref="Draw"/>
    /// call. Used to clamp <see cref="_scroll"/>.</summary>
    private float _contentH;

    /// <summary>Visible rectangle of the scroll viewport (panel rect minus the
    /// chrome). Stored so <see cref="HandleScroll"/> only consumes wheel
    /// events when the cursor is actually over the panel.</summary>
    private Box _viewport;

    /// <summary>Forward a mouse-wheel delta to the panel. Returns <c>true</c>
    /// when the cursor is over the panel and the event should be treated as
    /// consumed (so it doesn't also zoom the camera).</summary>
    public bool HandleScroll(Vector2 mouse, float offsetY)
    {
        if (!Visible) return false;
        if (!_viewport.Contains(mouse)) return false;
        // 22 px per "click" matches the row height so each wheel notch advances
        // exactly one toggle row.
        _scroll = MathF.Max(0f, MathF.Min(_contentH - _viewport.H, _scroll - offsetY * LineH));
        if (_scroll < 0f) _scroll = 0f;
        return true;
    }

    /// <summary>Cycle to the next / previous tab (keyboard: Left / Right while open).</summary>
    public void CycleTab(int delta)
    {
        int i = 0;
        for (int k = 0; k < Tabs.Count; k++) if (Tabs[k] == ActiveTab) { i = k; break; }
        i = ((i + delta) % Tabs.Count + Tabs.Count) % Tabs.Count;
        ActiveTab = Tabs[i];
        _scroll = 0f;
    }

    // --- Test hooks -------------------------------------------------------
    // Exposed as `internal` (paired with [InternalsVisibleTo("SolarSystem.Tests")]
    // in the main csproj) so unit tests can verify the scroll-clamp and
    // hit-test invariants without spinning up an OpenGL context.
    internal float ScrollOffsetForTests => _scroll;
    internal float ContentHeightForTests => _contentH;
    internal Box ViewportForTests => _viewport;
    internal IReadOnlyList<Row> RowsForTests => _rows;
    /// <summary>Seed the scroll viewport / content-height state that
    /// <see cref="Draw"/> normally computes, so the click and scroll logic can
    /// be exercised in tests without a renderer.</summary>
    internal void SeedForTests(float contentH, Box viewport)
    {
        _contentH = contentH;
        _viewport = viewport;
    }
    internal void SeedTabHitForTests(Box box, FeatureCategory tab) => _tabHits.Add((box, tab));
    internal void SeedPresetHitForTests(Box box, FeaturePreset preset) => _presetHits.Add((box, preset));
    internal void SeedToolButtonsForTests(Box reset, Box allOn, Box allOff)
    {
        _resetBtn = reset; _allOnBtn = allOn; _allOffBtn = allOff;
    }

    /// <summary>Bottom Y of the panel rectangle in screen pixels, valid after
    /// <see cref="Draw"/> runs. Used by <see cref="BookmarksSidebar"/> to stack
    /// itself underneath this panel when both are open.</summary>
    public float Bottom { get; private set; }

    // ---- Layout constants ----------------------------------------------------------

    public const float LineH = 22f;
    private const float Pad = 12f;
    private const float PixelSize = 14f;
    private const float PanelW = 460f;
    private const float PanelY = 150f;
    private const float TitleH = 26f;
    private const float TabH = 26f;
    private const float ToolsH = 24f;
    private const float FooterH = 40f;
    // Renderer.DrawText treats y as the glyph baseline — visible ink sits in
    // approximately [y - pixelSize, y + 2]. Inflate the click bounds upward by
    // this amount so the clickable area lines up with the rendered row.
    private const float TextTopOffset = PixelSize + 2f;

    private static readonly Vector4 BgColor      = new(0.02f, 0.03f, 0.07f, 0.86f);
    private static readonly Vector4 ChromeColor  = new(0.10f, 0.14f, 0.24f, 0.90f);
    private static readonly Vector4 TabActiveBg  = new(0.22f, 0.32f, 0.55f, 0.95f);
    private static readonly Vector4 HoverBg      = new(0.25f, 0.35f, 0.55f, 0.35f);
    private static readonly Vector4 TitleColor   = new(1f, 0.95f, 0.7f, 1f);
    private static readonly Vector4 RowColor     = new(0.85f, 0.95f, 1f, 0.95f);
    private static readonly Vector4 HotColor     = new(1f, 1f, 0.6f, 1f);
    private static readonly Vector4 DimColor     = new(0.55f, 0.65f, 0.85f, 0.85f);
    private static readonly Vector4 OffColor     = new(0.45f, 0.5f, 0.6f, 0.7f);
    private static readonly Vector4 OnMark       = new(0.45f, 1f, 0.6f, 1f);
    private static readonly Vector4 BtnBg        = new(0.16f, 0.22f, 0.38f, 0.9f);
    private static readonly Vector4 BtnActiveBg  = new(0.35f, 0.55f, 0.35f, 0.95f);

    public static string TabLabel(FeatureCategory c) => Localization.T("ui.tab." + c.ToString().ToLowerInvariant());

    public void Draw(Renderer renderer, BitmapFont font, Vector2 mouse)
    {
        _tabHits.Clear();
        _presetHits.Clear();
        _resetBtn = _allOnBtn = _allOffBtn = _closeBtn = Box.Empty;
        if (!Visible) return;

        bool sceneTab = IsSceneTab(ActiveTab);
        float panelX = renderer.FramebufferSize.X - PanelW - 16f;
        float panelY = PanelY;
        float chromeH = Pad + TitleH + TabH + (sceneTab ? ToolsH * 2f : 0f) + 4f;

        int visibleRows = 0;
        foreach (var r in _rows) if (r.Category == ActiveTab) visibleRows++;
        float contentH = visibleRows * LineH;
        // Clamp the panel to the viewport so the bottom rows can't fall off the
        // screen on small monitors. When the rows don't fit, the user can
        // mouse-wheel to scroll through them (see HandleScroll).
        float maxPanelH = MathF.Max(160f, renderer.FramebufferSize.Y - panelY - 16f);
        float panelH = MathF.Min(chromeH + contentH + FooterH + Pad, maxPanelH);
        float viewportH = MathF.Max(LineH, panelH - chromeH - FooterH - Pad);
        _contentH = contentH;
        _viewport = new Box(panelX, panelY + chromeH, PanelW, viewportH);
        float maxScroll = MathF.Max(0f, contentH - viewportH);
        if (_scroll > maxScroll) _scroll = maxScroll;
        if (_scroll < 0f) _scroll = 0f;
        Bottom = panelY + panelH;

        _panelHit = mouse.X >= panelX && mouse.X <= panelX + PanelW
                 && mouse.Y >= panelY && mouse.Y <= panelY + panelH;

        // ---- Background + chrome ----
        renderer.FillRect(panelX, panelY, PanelW, panelH, BgColor);
        renderer.FillRect(panelX, panelY, PanelW, Pad + TitleH, ChromeColor);

        // ---- Title + close ----
        float y = panelY + Pad + 14f;
        renderer.DrawText(font, Localization.T("ui.settings.title"), panelX + Pad, y, 14f, TitleColor);
        string close = "[Esc]";
        float closeW = font.MeasureWidth(close, 12f);
        _closeBtn = new Box(panelX + PanelW - Pad - closeW - 6f, panelY + 4f, closeW + 12f, TitleH);
        renderer.DrawText(font, close, panelX + PanelW - Pad - closeW, y, 12f,
            _closeBtn.Contains(mouse) ? HotColor : DimColor);

        // ---- Tabs ----
        float tabY = panelY + Pad + TitleH;
        float tabFont = 13f;
        float totalW = 0f;
        foreach (var t in Tabs) totalW += font.MeasureWidth(TabLabel(t), tabFont) + 16f;
        float avail = PanelW - Pad * 2f;
        if (totalW > avail) tabFont = MathF.Max(9f, tabFont * avail / totalW);
        float tx = panelX + Pad;
        foreach (var t in Tabs)
        {
            string lbl = TabLabel(t);
            float w = font.MeasureWidth(lbl, tabFont) + 16f;
            var box = new Box(tx, tabY, w, TabH);
            _tabHits.Add((box, t));
            bool active = t == ActiveTab;
            if (active) renderer.FillRect(box.X, box.Y + 2f, box.W, box.H - 2f, TabActiveBg);
            else if (box.Contains(mouse)) renderer.FillRect(box.X, box.Y + 2f, box.W, box.H - 2f, HoverBg);
            renderer.DrawText(font, lbl, tx + 8f, tabY + TabH - 8f, tabFont,
                active ? HotColor : (box.Contains(mouse) ? RowColor : DimColor));
            tx += w;
        }
        renderer.FillRect(panelX, tabY + TabH, PanelW, 1f, TabActiveBg);

        // ---- Presets + tools (scene tabs only) ----
        float rowsTop = tabY + TabH + 4f;
        if (sceneTab)
        {
            float py = rowsTop;
            float px = panelX + Pad;
            string presetsLbl = Localization.T("ui.settings.presets");
            renderer.DrawText(font, presetsLbl, px, py + ToolsH - 7f, 12f, DimColor);
            px += font.MeasureWidth(presetsLbl, 12f) + 8f;
            foreach (var p in Presets)
            {
                bool on = IsPresetActive?.Invoke(p) ?? false;
                px = DrawButton(renderer, font, p.Label, px, py + 2f, ToolsH - 4f, mouse, on, out var box) + 6f;
                _presetHits.Add((box, p));
            }
            py += ToolsH;
            px = panelX + Pad;
            px = DrawButton(renderer, font, Localization.T("ui.settings.reset"), px, py + 2f, ToolsH - 4f, mouse, false, out _resetBtn) + 6f;
            px = DrawButton(renderer, font, Localization.T("ui.settings.allon"), px, py + 2f, ToolsH - 4f, mouse, false, out _allOnBtn) + 6f;
            DrawButton(renderer, font, Localization.T("ui.settings.alloff"), px, py + 2f, ToolsH - 4f, mouse, false, out _allOffBtn);
            rowsTop = py + ToolsH;
        }

        // ---- Rows ----
        float viewTop = _viewport.Y;
        float viewBottom = viewTop + viewportH;
        float firstY = viewTop + LineH - 4f - _scroll; // baseline of the first row
        float ry = firstY;
        Row? hovered = null;
        foreach (var row in _rows)
        {
            if (row.Category != ActiveTab)
            {
                ZeroBounds(row);
                continue;
            }
            // Off-screen rows are completely skipped — no glyph generation, no
            // hit-box, so a click at that screen location can't accidentally
            // toggle a hidden row.
            float rowTop = ry - TextTopOffset;
            float rowBot = rowTop + LineH;
            if (rowTop < viewTop - 2f || rowBot > viewBottom + 2f)
            {
                ZeroBounds(row);
                ry += LineH;
                continue;
            }
            row.Bounds = new Box(panelX + Pad, rowTop, PanelW - Pad * 2f, LineH);
            bool hot = row.Bounds.Contains(mouse);
            if (hot) { hovered = row; renderer.FillRect(row.Bounds.X - 4f, row.Bounds.Y, row.Bounds.W + 8f, row.Bounds.H, HoverBg); }
            bool available = row.IsAvailable;
            string label = Localization.T(row.Label);
            var col = !available ? OffColor : hot ? HotColor : RowColor;
            float rowX = panelX + Pad;

            if (row is ToggleRow t)
            {
                bool on = available && t.Get();
                renderer.DrawText(font, on ? "[x]" : "[ ]", rowX, ry, PixelSize, on ? OnMark : col);
                renderer.DrawText(font, label, rowX + 30f, ry, PixelSize, col);
                if (!available && row.Unavailable != null)
                {
                    string why = Localization.T(row.Unavailable()!);
                    float lw = font.MeasureWidth(label, PixelSize);
                    renderer.DrawText(font, "  — " + why, rowX + 30f + lw, ry, 11f, OffColor);
                }
            }
            else if (row is ButtonRow b)
            {
                string status = b.Status?.Invoke() ?? "";
                renderer.DrawText(font, "▸", rowX + 6f, ry, PixelSize, col);
                renderer.DrawText(font, label, rowX + 30f, ry, PixelSize, col);
                if (status.Length > 0)
                {
                    float lw = font.MeasureWidth(label, PixelSize);
                    renderer.DrawText(font, "  " + status, rowX + 30f + lw, ry, 12f, DimColor);
                }
            }
            else if (row is SliderRow s)
            {
                float v = s.Get();
                float t01 = MathHelper.Clamp((v - s.Min) / MathF.Max(1e-6f, s.Max - s.Min), 0f, 1f);
                const int Cells = 12;
                int filled = (int)MathF.Round(t01 * Cells);
                var bar = new System.Text.StringBuilder();
                for (int i = 0; i < Cells; i++) bar.Append(i < filled ? '█' : '░');
                string barStr = bar.ToString();
                string prefix = $"{label}  ";
                string minus  = "- ";
                string open   = "[";
                string close2 = "] ";
                string plus   = "+  ";
                string value  = string.Format(s.Format, v);
                // Pixel-accurate hit boxes: measure each segment with the font so
                // the click→value math, the nudge boxes and the rendered glyphs all
                // line up regardless of label length or font.
                float prefixW = font.MeasureWidth(prefix, PixelSize);
                float minusW  = font.MeasureWidth(minus,  PixelSize);
                float openW   = font.MeasureWidth(open,   PixelSize);
                float barW    = font.MeasureWidth(barStr, PixelSize);
                float closeW2 = font.MeasureWidth(close2, PixelSize);
                float plusW   = font.MeasureWidth(plus,   PixelSize);
                s.Minus = new Box(rowX + prefixW, rowTop, minusW, LineH);
                s.Track = new Box(rowX + prefixW + minusW + openW, rowTop, barW, LineH);
                s.Plus  = new Box(rowX + prefixW + minusW + openW + barW + closeW2, rowTop, plusW, LineH);
                renderer.DrawText(font, prefix + minus + open + barStr + close2 + plus + value, rowX, ry, PixelSize, col);
            }

            // Right-aligned hotkey hint.
            if (row.Hint.Length > 0)
            {
                float hw = font.MeasureWidth(row.Hint, 12f);
                renderer.DrawText(font, row.Hint, panelX + PanelW - Pad - hw, ry, 12f, hot ? RowColor : DimColor);
            }
            ry += LineH;
        }

        // Scroll indicator.
        if (maxScroll > 0f)
        {
            float trackH = viewportH;
            float thumbH = MathF.Max(12f, trackH * viewportH / contentH);
            float thumbY = viewTop + (trackH - thumbH) * (_scroll / maxScroll);
            renderer.FillRect(panelX + PanelW - 5f, viewTop, 3f, trackH, new Vector4(1f, 1f, 1f, 0.08f));
            renderer.FillRect(panelX + PanelW - 5f, thumbY, 3f, thumbH, new Vector4(1f, 1f, 1f, 0.35f));
        }

        // ---- Footer: hovered row description (word-wrapped, 2 lines max) ----
        float footY = panelY + panelH - FooterH - Pad + 4f;
        renderer.FillRect(panelX, footY - 4f, PanelW, 1f, TabActiveBg);
        string desc = hovered?.Description?.Invoke() ?? "";
        if (desc.Length == 0) desc = Localization.T("ui.settings.footer.hint");
        float fy = footY + 14f;
        foreach (var line in Wrap(font, desc, 12f, PanelW - Pad * 2f, 2))
        {
            renderer.DrawText(font, line, panelX + Pad, fy, 12f, DimColor);
            fy += 16f;
        }
    }

    private static void ZeroBounds(Row row)
    {
        row.Bounds = Box.Empty;
        if (row is SliderRow sk)
        {
            sk.Minus = Box.Empty;
            sk.Plus = Box.Empty;
            sk.Track = Box.Empty;
        }
    }

    /// <summary>Draw a small text button; returns the x coordinate just past it.</summary>
    private static float DrawButton(Renderer renderer, BitmapFont font, string label,
        float x, float y, float h, Vector2 mouse, bool active, out Box box)
    {
        const float fs = 12f;
        float w = font.MeasureWidth(label, fs) + 14f;
        box = new Box(x, y, w, h);
        bool hot = box.Contains(mouse);
        renderer.FillRect(x, y, w, h, active ? BtnActiveBg : hot ? TabActiveBg : BtnBg);
        renderer.DrawText(font, label, x + 7f, y + h - 6f, fs, active || hot ? HotColor : RowColor);
        return x + w;
    }

    /// <summary>Greedy word wrap to at most <paramref name="maxLines"/> lines.</summary>
    public static List<string> Wrap(BitmapFont font, string text, float pixelSize, float maxW, int maxLines)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text)) return lines;
        var cur = new System.Text.StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = cur.Length == 0 ? word : cur + " " + word;
            if (font.MeasureWidth(candidate, pixelSize) <= maxW || cur.Length == 0)
            {
                cur.Clear();
                cur.Append(candidate);
            }
            else
            {
                lines.Add(cur.ToString());
                cur.Clear();
                cur.Append(word);
                if (lines.Count == maxLines) return lines;
            }
        }
        if (cur.Length > 0 && lines.Count < maxLines) lines.Add(cur.ToString());
        return lines;
    }
}
