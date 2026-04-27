using OpenTK.Mathematics;

namespace SolarSystem;

/// <summary>
/// Q8 / S12 sidebar: a compact list of recent + upcoming bookmarks rendered
/// next to the right edge of the viewport. Toggle with <c>F3</c>.
///
/// The header doubles as the filter cycler — clicking the <c>◀</c> / <c>▶</c>
/// arrows rotates through "All" plus every <see cref="BookmarkKind"/> that
/// has at least one entry in the loaded catalogue. Clicking any data row
/// jumps the simulation to that bookmark's date. Hit testing follows the
/// same pixel-accurate, baseline-corrected pattern as
/// <see cref="SettingsPanel"/> so the visible coloured row is the clickable
/// row.
/// </summary>
public sealed class BookmarksSidebar
{
    public bool Visible { get; set; }

    /// <summary>How many already-passed bookmarks to show above the
    /// "current/next" cursor.</summary>
    public int RecentCount { get; set; } = 3;

    /// <summary>How many upcoming bookmarks to show below the cursor.</summary>
    public int UpcomingCount { get; set; } = 8;

    public struct Box(float x, float y, float w, float h)
    {
        public float X = x, Y = y, W = w, H = h;
        public readonly bool Contains(Vector2 p)
            => p.X >= X && p.X <= X + W && p.Y >= Y && p.Y <= Y + H;
    }

    private Box _bounds;
    private Box _prevBtn, _nextBtn;
    private readonly List<(Box Box, Bookmarks.Entry Entry)> _rowHits = new();
    private bool _panelHit;

    /// <summary>Returns true when the click was consumed by the sidebar
    /// (filter cycle, row jump, or just a click inside the panel rectangle
    /// to avoid camera fall-through). When <paramref name="jumpTo"/> is
    /// non-null the host should snap <c>simDays</c> to it.</summary>
    public bool TryHandleClick(Vector2 mouse, Bookmarks bookmarks, out Bookmarks.Entry? jumpTo)
    {
        jumpTo = null;
        if (!Visible) return false;

        if (_prevBtn.Contains(mouse)) { bookmarks.CycleFilter(-1); return true; }
        if (_nextBtn.Contains(mouse)) { bookmarks.CycleFilter(+1); return true; }

        foreach (var (box, entry) in _rowHits)
        {
            if (box.Contains(mouse)) { jumpTo = entry; return true; }
        }

        return _panelHit;
    }

    public void Draw(Renderer renderer, BitmapFont font, Bookmarks bookmarks, Vector2 mouse, double simDays, float topY = 150f)
    {
        _rowHits.Clear();
        if (!Visible) return;

        const float pixelSize = 13f;
        const float headerSize = 14f;
        const float lineH = 19f;
        const float pad = 10f;
        // Renderer.DrawText treats y as the glyph baseline (visible ink in
        // [y - pixelSize, y + 2]); offset hit boxes upward by this amount.
        const float textTopOffset = pixelSize + 2f;

        float panelW = 320f;
        // Anchored to the right edge, under the HUD. When the settings panel
        // is open the host pushes us down via <paramref name="topY"/> so the
        // two panels stack instead of overlapping.
        float panelX = renderer.FramebufferSize.X - panelW - 16f;
        float panelY = topY;

        int rows = RecentCount + UpcomingCount + 1;
        float panelH = pad * 2f + headerSize + 6f + rows * lineH + 6f;
        _bounds = new Box(panelX, panelY, panelW, panelH);
        _panelHit = _bounds.Contains(mouse);

        var titleColor = new Vector4(1f, 0.95f, 0.7f, 1f);
        var dimColor   = new Vector4(0.65f, 0.75f, 0.95f, 0.9f);
        var rowColor   = new Vector4(0.85f, 0.95f, 1f, 0.95f);
        var hotColor   = new Vector4(1f, 1f, 0.6f, 1f);
        var currentCol = new Vector4(1f, 0.85f, 0.4f, 1f);

        // ---- Header: ◀  Filter: <name> (n)  ▶ ----
        var filter = bookmarks.ActiveFilter;
        var filterCol = Bookmarks.KindColor(filter);
        string filterLabel = Bookmarks.KindLabel(filter);
        int filtCount = bookmarks.Filtered.Count;

        float hx = panelX + pad;
        float hy = panelY + pad + headerSize; // baseline for header line

        string left = "◀ ";
        string right = " ▶";
        string mid = $" {filterLabel} ({filtCount}) ";

        float leftW  = font.MeasureWidth(left,  headerSize);
        float midW   = font.MeasureWidth(mid,   headerSize);
        float rightW = font.MeasureWidth(right, headerSize);

        _prevBtn = new Box(hx, hy - textTopOffset, leftW, headerSize + 6f);
        _nextBtn = new Box(hx + leftW + midW, hy - textTopOffset, rightW, headerSize + 6f);

        var prevCol = _prevBtn.Contains(mouse) ? hotColor : titleColor;
        var nextCol = _nextBtn.Contains(mouse) ? hotColor : titleColor;

        renderer.DrawText(font, left,  hx,                 hy, headerSize, prevCol);
        renderer.DrawText(font, mid,   hx + leftW,         hy, headerSize, filterCol);
        renderer.DrawText(font, right, hx + leftW + midW,  hy, headerSize, nextCol);

        // ---- Body: recent (faded) + upcoming ----
        float y = hy + 8f + lineH;

        // Materialise both slices once so we know the full layout.
        var recent = bookmarks.Recent(simDays, RecentCount).ToList();
        recent.Reverse(); // oldest -> newest visually
        var upcoming = bookmarks.Upcoming(simDays, UpcomingCount).ToList();

        if (recent.Count == 0 && upcoming.Count == 0)
        {
            renderer.DrawText(font, "(no bookmarks)", panelX + pad, y, pixelSize, dimColor);
            return;
        }

        // Pad recent slice to RecentCount empty rows so the "now" cursor sits
        // at a consistent vertical position regardless of catalogue position.
        int blanks = RecentCount - recent.Count;
        for (int i = 0; i < blanks; i++)
        {
            renderer.DrawText(font, "·", panelX + pad, y, pixelSize, dimColor);
            y += lineH;
        }

        foreach (var ev in recent)
        {
            DrawRow(renderer, font, ev, simDays, panelX, panelW, y, pixelSize, lineH,
                    textTopOffset, mouse, hotColor, dimColor);
            y += lineH;
        }

        // "Now" separator with the current sim date.
        var nowDate = OrbitalMechanics.J2000.AddDays(simDays);
        renderer.DrawText(font, $"── {nowDate:yyyy-MM-dd} ──", panelX + pad, y, pixelSize, currentCol);
        y += lineH;

        foreach (var ev in upcoming)
        {
            DrawRow(renderer, font, ev, simDays, panelX, panelW, y, pixelSize, lineH,
                    textTopOffset, mouse, hotColor, rowColor);
            y += lineH;
        }
    }

    private void DrawRow(
        Renderer renderer, BitmapFont font, Bookmarks.Entry ev, double simDays,
        float panelX, float panelW, float y, float pixelSize, float lineH,
        float textTopOffset, Vector2 mouse, Vector4 hotColor, Vector4 baseColor)
    {
        const float pad = 10f;
        var box = new Box(panelX + pad * 0.5f, y - textTopOffset, panelW - pad, lineH);
        _rowHits.Add((box, ev));

        var bullet = "● ";
        float bulletW = font.MeasureWidth(bullet, pixelSize);
        var bulletCol = Bookmarks.KindColor(ev.Kind);

        bool hot = box.Contains(mouse);
        var col = hot ? hotColor : baseColor;

        // Compact relative date label (e.g. "in 47 d", "1.4 y ago").
        double delta = ev.SimDays - simDays;
        string rel = FormatDelta(delta);

        string main = $"{ev.Date:yyyy-MM-dd}  {ev.Title}";
        // Truncate if too long for the row width; assume monospace-ish bitmap font.
        const int MaxChars = 30;
        if (main.Length > MaxChars + 2) main = main[..MaxChars] + "…";

        renderer.DrawText(font, bullet, panelX + pad,           y, pixelSize, bulletCol);
        renderer.DrawText(font, main,   panelX + pad + bulletW, y, pixelSize, col);

        // Right-aligned relative time.
        float relW = font.MeasureWidth(rel, pixelSize);
        renderer.DrawText(font, rel, panelX + panelW - pad - relW, y, pixelSize,
            hot ? hotColor : new Vector4(col.X * 0.8f, col.Y * 0.8f, col.Z * 0.9f, col.W));
    }

    private static string FormatDelta(double days)
    {
        double abs = Math.Abs(days);
        string suffix = days >= 0 ? "" : " ago";
        string prefix = days >= 0 ? "in " : "";
        if (abs < 1.5)        return days >= 0 ? "today" : "today";
        if (abs < 90)         return $"{prefix}{(int)Math.Round(abs)} d{suffix}";
        if (abs < 365.25 * 2) return $"{prefix}{abs / 30.0:0.#} mo{suffix}";
        return $"{prefix}{abs / 365.25:0.#} y{suffix}";
    }
}
