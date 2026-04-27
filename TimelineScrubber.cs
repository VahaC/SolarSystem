using OpenTK.Mathematics;

namespace SolarSystem;

/// <summary>
/// Q9: Bottom-of-screen draggable timeline scrubber. Maps mouse-X across a
/// configurable bar to <c>simDays</c> over a window of ±<see cref="HalfRangeYears"/>
/// years around J2000. The scrubber is rendered with the existing
/// <see cref="BitmapFont"/> overlay (no extra GL state needed) — the bar itself
/// is drawn as a row of "█"/"░" block glyphs and the live position as a "▲"
/// caret beneath it. Hit-testing is plain rectangle math against the cursor
/// coordinates supplied each frame by <see cref="SolarSystemWindow"/>.
/// </summary>
public sealed class TimelineScrubber
{
    public bool Visible { get; set; }
    public double HalfRangeYears { get; set; } = 100.0;

    /// <summary>Returns true while the user is actively dragging the caret;
    /// the host should freeze its own time advance during this window.</summary>
    public bool IsDragging { get; private set; }

    private const float BarHeight = 14f;
    private const float Margin = 60f;
    private const float YOffset = 36f; // pixels from bottom edge

    private float _barX, _barY, _barW;
    private int _viewportW;

    /// <summary>Recompute layout for the current viewport. Width and X are
    /// resolved later in <see cref="Draw"/> from the actual rendered pixel
    /// width of the bar so they always line up with the visible cells
    /// (and stay centred horizontally regardless of viewport size).</summary>
    public void Layout(int viewportW, int viewportH)
    {
        _viewportW = viewportW;
        _barY = viewportH - YOffset;
    }

    public bool HitTest(Vector2 mouse)
    {
        if (!Visible) return false;
        if (_barW <= 0f) return false;
        return mouse.X >= _barX && mouse.X <= _barX + _barW
            && mouse.Y >= _barY - 6f && mouse.Y <= _barY + BarHeight + 6f;
    }

    /// <summary>Begin a drag if the cursor is inside the bar. Returns true to
    /// indicate the click was consumed by the scrubber.</summary>
    public bool TryBeginDrag(Vector2 mouse)
    {
        if (!HitTest(mouse)) return false;
        IsDragging = true;
        return true;
    }

    public void EndDrag() => IsDragging = false;

    /// <summary>Convert the live cursor X into a sim-days value while dragging.
    /// Returns null when no drag is active.</summary>
    public double? UpdateDrag(Vector2 mouse)
    {
        if (!IsDragging) return null;
        float t = MathHelper.Clamp((mouse.X - _barX) / MathF.Max(1f, _barW), 0f, 1f);
        double half = HalfRangeYears * 365.25;
        return MathHelper.Lerp(-(float)half, (float)half, t);
    }

    public void Draw(Renderer renderer, BitmapFont font, double simDays, Bookmarks? bookmarks = null)
    {
        if (!Visible) return;
        Layout(renderer.FramebufferSize.X, renderer.FramebufferSize.Y);

        double half = HalfRangeYears * 365.25;
        float frac = (float)MathHelper.Clamp((simDays + half) / (2.0 * half), 0.0, 1.0);

        const int Cells = 60;
        var sb = new System.Text.StringBuilder(Cells);
        int caretCell = (int)MathF.Round(frac * (Cells - 1));
        for (int i = 0; i < Cells; i++) sb.Append(i == caretCell ? '█' : '░');

        var bar = sb.ToString();

        // Resolve final width + X here so the hit-test and the click→days
        // mapping line up with the visible cells instead of any wider layout
        // rectangle. Centre the bar horizontally on the viewport.
        float measured = font.MeasureWidth(bar, BarHeight);
        if (measured > 1f)
        {
            _barW = measured;
            _barX = MathF.Max(Margin, (_viewportW - measured) * 0.5f);
        }

        var color = IsDragging
            ? new Vector4(1f, 0.85f, 0.4f, 0.95f)
            : new Vector4(0.7f, 0.85f, 1f, 0.85f);

        var date = OrbitalMechanics.J2000.AddDays(simDays);
        string label = $"{date:yyyy-MM-dd}   ({-HalfRangeYears:0} y .. +{HalfRangeYears:0} y around J2000)";
        renderer.DrawText(font, label, _barX, _barY - 18f, 12f, color);
        renderer.DrawText(font, bar, _barX, _barY, BarHeight, color);

        // Q8 / S12: render coloured ▼ tick marks above the bar for every
        // bookmark that falls within ±HalfRangeYears, tinted by kind. The
        // tick X coordinate is mapped through the same _barX/_barW that the
        // caret math uses, so ticks stay aligned with the bar when the
        // viewport is resized.
        if (bookmarks != null && bookmarks.All.Count > 0)
        {
            const string Tick = "▼";
            float tickSize = 10f;
            float halfTickW = font.MeasureWidth(Tick, tickSize) * 0.5f;
            float tickY = _barY - 4f;
            foreach (var ev in bookmarks.All)
            {
                if (Math.Abs(ev.SimDays) > half) continue;
                float t = (float)((ev.SimDays + half) / (2.0 * half));
                float tx = _barX + t * _barW - halfTickW;
                var tint = Bookmarks.KindColor(ev.Kind);
                tint.W = 0.95f;
                renderer.DrawText(font, Tick, tx, tickY, tickSize, tint);
            }
        }
    }
}
