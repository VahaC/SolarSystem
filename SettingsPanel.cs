using OpenTK.Mathematics;

namespace SolarSystem;

/// <summary>
/// Q12: In-app settings panel. Hand-rolled "ImGui-lite" overlay rendered with
/// the existing <see cref="BitmapFont"/> path so it works on any OpenGL context
/// without bringing in a full immediate-mode UI. Each row is either a toggle
/// (drawn as <c>[ ]</c> / <c>[x]</c>) or a slider with <c>-</c>/<c>+</c> nudge
/// hit boxes; click coordinates are tested against the row's bounding box at
/// the moment the panel is rendered.
/// </summary>
public sealed class SettingsPanel
{
    public bool Visible { get; set; }

    public abstract class Row
    {
        public required string Label;
        public Box Bounds;
        public Box Minus;
        public Box Plus;
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

    public struct Box(float x, float y, float w, float h)
    {
        public float X = x, Y = y, W = w, H = h;
        public readonly bool Contains(Vector2 p) => p.X >= X && p.X <= X + W && p.Y >= Y && p.Y <= Y + H;
    }

    private readonly List<Row> _rows = new();

    public void Add(Row row) => _rows.Add(row);

    /// <summary>Returns true if the click was consumed by the panel (toggle
    /// flipped or slider nudged), false to let the host handle it (e.g. as
    /// camera input).</summary>
    public bool TryHandleClick(Vector2 mouse)
    {
        if (!Visible) return false;
        // Closing the panel by clicking outside is left to the host.
        foreach (var row in _rows)
        {
            if (row is ToggleRow t && t.Bounds.Contains(mouse))
            {
                t.Toggle();
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
    /// title bar). Stored so <see cref="HandleScroll"/> only consumes wheel
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
        _scroll = MathF.Max(0f, MathF.Min(_contentH - _viewport.H, _scroll - offsetY * 22f));
        if (_scroll < 0f) _scroll = 0f;
        return true;
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

    /// <summary>Bottom Y of the panel rectangle in screen pixels, valid after
    /// <see cref="Draw"/> runs. Used by <see cref="BookmarksSidebar"/> to stack
    /// itself underneath this panel when both are open.</summary>
    public float Bottom { get; private set; }

    public void Draw(Renderer renderer, BitmapFont font, Vector2 mouse)
    {
        if (!Visible) return;

        const float pad = 12f;
        const float pixelSize = 14f;
        const float lineH = 22f;
        // Renderer.DrawText treats y as the glyph baseline — visible ink sits in
        // approximately [y - pixelSize, y + 2]. Inflate the click bounds upward by
        // this amount so the clickable area lines up with the rendered row.
        const float textTopOffset = pixelSize + 2f;
        float panelX = renderer.FramebufferSize.X - 360f;
        // Pushed below the HUD overlay (top-right FPS / particle counts) which
        // occupies roughly y = 12..120 when visible.
        float panelY = 150f;
        float panelW = 340f;
        float titleH = 24f;
        float contentH = _rows.Count * lineH;
        // Clamp the panel to the viewport so the bottom rows can't fall off the
        // screen on small monitors. When the rows don't fit, the user can
        // mouse-wheel to scroll through them (see HandleScroll).
        float maxPanelH = MathF.Max(120f, renderer.FramebufferSize.Y - panelY - 16f);
        float panelH = MathF.Min(pad * 2 + titleH + contentH, maxPanelH);
        float viewportH = panelH - pad * 2 - titleH;
        _contentH = contentH;
        _viewport = new Box(panelX, panelY + pad + titleH, panelW, viewportH);
        // Clamp scroll in case rows count, viewport size or font changed since
        // the last frame.
        float maxScroll = MathF.Max(0f, contentH - viewportH);
        if (_scroll > maxScroll) _scroll = maxScroll;
        if (_scroll < 0f) _scroll = 0f;
        Bottom = panelY + panelH;

        _panelHit = mouse.X >= panelX && mouse.X <= panelX + panelW
                 && mouse.Y >= panelY && mouse.Y <= panelY + panelH;

        var titleColor = new Vector4(1f, 0.95f, 0.7f, 1f);
        var rowColor   = new Vector4(0.85f, 0.95f, 1f, 0.95f);
        var hotColor   = new Vector4(1f, 1f, 0.6f, 1f);

        renderer.DrawText(font, Localization.T("ui.settings.title"),
            panelX, panelY + pad, 13f, titleColor);

        // First row baseline matches the original layout (panelY + pad + 22)
        // shifted up by the scroll offset.
        float firstY = panelY + pad + 22f - _scroll;
        float viewTop = panelY + pad + titleH;
        float viewBottom = viewTop + viewportH;
        float y = firstY;
        foreach (var row in _rows)
        {
            // Off-screen rows are completely skipped — no glyph generation, no
            // hit-box, so a click at that screen location can't accidentally
            // toggle a hidden row.
            float rowTop = y - textTopOffset;
            float rowBot = rowTop + lineH;
            if (rowBot < viewTop || rowTop > viewBottom)
            {
                row.Bounds = new Box(0, -1, 0, 0);
                if (row is SliderRow sk)
                {
                    sk.Minus = new Box(0, -1, 0, 0);
                    sk.Plus = new Box(0, -1, 0, 0);
                    sk.Track = new Box(0, -1, 0, 0);
                }
                y += lineH;
                continue;
            }
            row.Bounds = new Box(panelX + pad, rowTop, panelW - pad * 2f, lineH);
            string text;
            // Resolve the label key on every draw so language toggles take effect immediately.
            string label = Localization.T(row.Label);
            if (row is ToggleRow t)
            {
                bool on = t.Get();
                text = $"[{(on ? 'x' : ' ')}] {label}";
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
                string close  = "] ";
                string plus   = "+  ";
                string value  = string.Format(s.Format, v);
                text = prefix + minus + open + barStr + close + plus + value;
                // Pixel-accurate hit boxes: measure each segment with the font so
                // the click→value math, the nudge boxes and the rendered glyphs all
                // line up regardless of label length or font.
                float rowX = panelX + pad;
                float prefixW = font.MeasureWidth(prefix, pixelSize);
                float minusW  = font.MeasureWidth(minus,  pixelSize);
                float openW   = font.MeasureWidth(open,   pixelSize);
                float barW    = font.MeasureWidth(barStr, pixelSize);
                float closeW  = font.MeasureWidth(close,  pixelSize);
                float plusW   = font.MeasureWidth(plus,   pixelSize);
                s.Minus = new Box(rowX + prefixW, y - textTopOffset, minusW, lineH);
                s.Track = new Box(rowX + prefixW + minusW + openW, y - textTopOffset, barW, lineH);
                s.Plus  = new Box(rowX + prefixW + minusW + openW + barW + closeW, y - textTopOffset, plusW, lineH);
            }
            else continue;

            var col = row.Bounds.Contains(mouse) ? hotColor : rowColor;
            renderer.DrawText(font, text, panelX + pad, y, pixelSize, col);
            y += lineH;
        }
    }

    public void Clear() => _rows.Clear();
}
