using OpenTK.Mathematics;

namespace SolarSystem;

/// <summary>
/// Bottom-centre strip of text buttons for the handful of actions people
/// reach for constantly: pause, speed, direction, the core view toggles and
/// the two menus. Everything else lives in the F1 panel / Ctrl+K palette.
/// Drawn through <see cref="Renderer.FillRect"/> + <see cref="Renderer.DrawText"/>,
/// so it needs no extra GL state.
/// </summary>
public sealed class Toolbar
{
    public bool Visible { get; set; } = true;

    public sealed class Button
    {
        /// <summary>Live label (localised / state-dependent).</summary>
        public required Func<string> Label;
        public required Action Click;
        /// <summary>Highlight when true (e.g. toggle currently on).</summary>
        public Func<bool>? Active;
        /// <summary>Tooltip / description (already localised).</summary>
        public Func<string>? Tip;
        /// <summary>Optional fixed width in px (for labels that change length).</summary>
        public float MinWidth;
        public SettingsPanel.Box Bounds;
    }

    private readonly List<Button> _buttons = new();
    private SettingsPanel.Box _bounds;

    public IReadOnlyList<Button> Buttons => _buttons;
    public void Add(Button b) => _buttons.Add(b);
    public void Clear() => _buttons.Clear();

    /// <summary>Top edge of the strip after the last <see cref="Draw"/>, so other
    /// bottom-anchored overlays can stay clear of it.</summary>
    public float Top { get; private set; } = float.MaxValue;

    /// <summary>Tip of the hovered button after the last <see cref="Draw"/> ("" when none).</summary>
    public string HoverTip { get; private set; } = "";

    public bool TryHandleClick(Vector2 mouse)
    {
        if (!Visible) return false;
        foreach (var b in _buttons)
        {
            if (b.Bounds.Contains(mouse))
            {
                b.Click();
                return true;
            }
        }
        return _bounds.Contains(mouse);
    }

    public const float Height = 30f;

    /// <summary>Draw the strip with its bottom edge at <paramref name="bottomY"/>.</summary>
    public void Draw(Renderer renderer, BitmapFont font, Vector2 mouse, float bottomY)
    {
        HoverTip = "";
        Top = float.MaxValue;
        if (!Visible || _buttons.Count == 0) return;

        const float fs = 13f;
        const float padX = 10f;
        const float gap = 4f;
        const float btnH = Height - 8f;

        // Measure first so the whole strip can be centred.
        float total = 0f;
        var widths = new float[_buttons.Count];
        for (int i = 0; i < _buttons.Count; i++)
        {
            float w = font.MeasureWidth(_buttons[i].Label(), fs) + padX * 2f;
            w = MathF.Max(w, _buttons[i].MinWidth);
            widths[i] = w;
            total += w + (i > 0 ? gap : 0f);
        }
        float stripW = total + 8f;
        float x0 = renderer.FramebufferSize.X * 0.5f - stripW * 0.5f;
        float y0 = bottomY - Height;
        _bounds = new SettingsPanel.Box(x0, y0, stripW, Height);
        Top = y0;

        renderer.FillRect(x0, y0, stripW, Height, new Vector4(0.02f, 0.03f, 0.07f, 0.72f));

        float x = x0 + 4f;
        float by = y0 + 4f;
        for (int i = 0; i < _buttons.Count; i++)
        {
            var b = _buttons[i];
            b.Bounds = new SettingsPanel.Box(x, by, widths[i], btnH);
            bool hot = b.Bounds.Contains(mouse);
            bool active = b.Active?.Invoke() ?? false;
            var bg = active
                ? new Vector4(0.30f, 0.50f, 0.32f, 0.95f)
                : hot ? new Vector4(0.25f, 0.35f, 0.6f, 0.9f) : new Vector4(0.14f, 0.19f, 0.32f, 0.85f);
            renderer.FillRect(x, by, widths[i], btnH, bg);
            string lbl = b.Label();
            float lw = font.MeasureWidth(lbl, fs);
            renderer.DrawText(font, lbl, x + (widths[i] - lw) * 0.5f, by + btnH - 6f, fs,
                active || hot ? new Vector4(1f, 1f, 0.7f, 1f) : new Vector4(0.85f, 0.92f, 1f, 0.95f));
            if (hot && b.Tip != null) HoverTip = b.Tip();
            x += widths[i] + gap;
        }

        if (HoverTip.Length > 0)
        {
            float tw = font.MeasureWidth(HoverTip, 12f);
            float tx = renderer.FramebufferSize.X * 0.5f - tw * 0.5f;
            float ty = y0 - 6f;
            renderer.FillRect(tx - 8f, ty - 16f, tw + 16f, 20f, new Vector4(0.02f, 0.03f, 0.07f, 0.85f));
            renderer.DrawText(font, HoverTip, tx, ty, 12f, new Vector4(0.85f, 0.92f, 1f, 0.95f));
        }
    }
}
