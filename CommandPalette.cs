using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace SolarSystem;

/// <summary>
/// Ctrl+K command palette. A modal prompt (same lifecycle as the date-seek and
/// name-search prompts) that live-filters every <see cref="FeatureRegistry"/>
/// entry by label / id / description. <c>Up</c>/<c>Down</c> move the cursor,
/// <c>Enter</c> toggles the feature or runs the command, <c>Esc</c> closes.
/// Toggling a feature keeps the palette open so several switches can be
/// flipped in a row; running a navigation command closes it.
/// </summary>
public sealed class CommandPalette
{
    public bool Active { get; private set; }
    public string Query { get; private set; } = "";
    public int Selected { get; private set; }
    /// <summary>The key press that opened the palette may also produce a text
    /// event (a plain-letter binding does; Ctrl chords don't). Swallow exactly
    /// one character, but only if it arrives right after <see cref="Open"/> —
    /// otherwise a palette opened from the toolbar would eat the first typed
    /// letter.</summary>
    public bool SwallowNextChar { get; set; }
    private long _openedAt;
    private const double SwallowWindowMs = 150.0;

    public const int MaxResults = 9;

    private readonly List<Entry> _matches = new();
    private readonly List<SettingsPanel.Box> _rowHits = new();
    private SettingsPanel.Box _bounds;

    public IReadOnlyList<Entry> Matches => _matches;

    public void Open(FeatureRegistry registry)
    {
        Active = true;
        Query = "";
        Selected = 0;
        SwallowNextChar = true;
        _openedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        Refresh(registry);
    }

    public void Close()
    {
        Active = false;
        Query = "";
        _matches.Clear();
    }

    public void Refresh(FeatureRegistry registry)
    {
        _matches.Clear();
        _matches.AddRange(registry.Search(Query, MaxResults));
        if (Selected >= _matches.Count) Selected = Math.Max(0, _matches.Count - 1);
    }

    public void AppendText(string text, FeatureRegistry registry)
    {
        if (SwallowNextChar)
        {
            SwallowNextChar = false;
            double sinceOpenMs = System.Diagnostics.Stopwatch.GetElapsedTime(_openedAt).TotalMilliseconds;
            if (sinceOpenMs < SwallowWindowMs) return;
        }
        if (Query.Length >= 40) return;
        Query += text;
        Selected = 0;
        Refresh(registry);
    }

    /// <summary>Handle a key while the palette is open. Always returns true —
    /// the palette is modal and eats every key.</summary>
    public bool HandleKey(Keys key, KeyModifiers mods, FeatureRegistry registry, Action<string>? banner)
    {
        switch (key)
        {
            case Keys.Escape:
                Close();
                return true;
            case Keys.Backspace:
                if (Query.Length > 0)
                {
                    Query = Query[..^1];
                    Selected = 0;
                    Refresh(registry);
                }
                return true;
            case Keys.Up:
                if (_matches.Count > 0) Selected = (Selected - 1 + _matches.Count) % _matches.Count;
                return true;
            case Keys.Down:
            case Keys.Tab:
                if (_matches.Count > 0) Selected = (Selected + 1) % _matches.Count;
                return true;
            case Keys.Enter:
            case Keys.KeyPadEnter:
                Execute(registry, banner);
                return true;
        }
        return true;
    }

    private void Execute(FeatureRegistry registry, Action<string>? banner)
    {
        if (Selected < 0 || Selected >= _matches.Count) return;
        var e = _matches[Selected];
        registry.Invoke(e, banner);
        if (e is Command c && c.ClosesPalette) Close();
        else Refresh(registry);
    }

    /// <summary>Mouse click on a result row selects + executes it. Clicks
    /// anywhere else inside the palette box are consumed; outside → false.</summary>
    public bool TryHandleClick(Vector2 mouse, FeatureRegistry registry, Action<string>? banner)
    {
        if (!Active) return false;
        for (int i = 0; i < _rowHits.Count; i++)
        {
            if (_rowHits[i].Contains(mouse))
            {
                Selected = i;
                Execute(registry, banner);
                return true;
            }
        }
        return _bounds.Contains(mouse);
    }

    public void Draw(Renderer renderer, BitmapFont font, Vector2 mouse)
    {
        _rowHits.Clear();
        if (!Active) return;

        const float w = 560f;
        const float pad = 12f;
        const float lineH = 24f;
        const float fs = 14f;
        float x = renderer.FramebufferSize.X * 0.5f - w * 0.5f;
        float y = 60f;
        int rows = _matches.Count;
        float h = pad * 2f + 30f + rows * lineH + (rows > 0 ? 24f : 0f);
        _bounds = new SettingsPanel.Box(x, y, w, h);

        renderer.FillRect(x, y, w, h, new Vector4(0.02f, 0.03f, 0.07f, 0.97f));
        renderer.FillRect(x, y, w, 30f + pad, new Vector4(0.10f, 0.14f, 0.24f, 0.95f));

        // Prompt line.
        string prompt = Localization.T("ui.palette.prompt");
        renderer.DrawText(font, prompt, x + pad, y + pad + 10f, 12f, new Vector4(0.55f, 0.65f, 0.85f, 0.9f));
        float pw = font.MeasureWidth(prompt, 12f);
        renderer.DrawText(font, Query + "_", x + pad + pw + 8f, y + pad + 12f, fs + 1f, new Vector4(1f, 1f, 0.8f, 1f));

        float ry = y + pad + 30f + lineH - 6f;
        var dim = new Vector4(0.55f, 0.65f, 0.85f, 0.85f);
        var normal = new Vector4(0.85f, 0.95f, 1f, 0.95f);
        var hot = new Vector4(1f, 1f, 0.6f, 1f);
        var onMark = new Vector4(0.45f, 1f, 0.6f, 1f);
        var off = new Vector4(0.45f, 0.5f, 0.6f, 0.7f);

        if (rows == 0)
        {
            renderer.DrawText(font, Localization.T("ui.palette.empty"), x + pad, ry, 13f, dim);
            return;
        }

        for (int i = 0; i < rows; i++)
        {
            var e = _matches[i];
            var box = new SettingsPanel.Box(x, ry - fs - 4f, w, lineH);
            _rowHits.Add(box);
            bool sel = i == Selected;
            bool hover = box.Contains(mouse);
            if (sel) renderer.FillRect(x + 4f, box.Y, w - 8f, lineH, new Vector4(0.25f, 0.35f, 0.6f, 0.55f));
            else if (hover) renderer.FillRect(x + 4f, box.Y, w - 8f, lineH, new Vector4(0.25f, 0.35f, 0.55f, 0.25f));

            bool available = e.IsAvailable;
            var col = !available ? off : sel ? hot : normal;
            string mark;
            Vector4 markCol = col;
            if (e is Feature f)
            {
                bool on = available && f.Value;
                mark = on ? "[x]" : "[ ]";
                if (on) markCol = onMark;
            }
            else mark = " ▸ ";
            renderer.DrawText(font, mark, x + pad, ry, fs, markCol);

            string label = e.Label;
            if (e is Command c && c.Status != null)
            {
                string st = c.Status();
                if (st.Length > 0) label += "  " + st;
            }
            renderer.DrawText(font, label, x + pad + 34f, ry, fs, col);

            // Category tag + hotkey on the right.
            string tag = SettingsPanel.TabLabel(e.Category);
            string hint = e.BindingText;
            float hx = x + w - pad;
            if (hint.Length > 0)
            {
                float hw = font.MeasureWidth(hint, 12f);
                hx -= hw;
                renderer.DrawText(font, hint, hx, ry, 12f, sel ? normal : dim);
                hx -= 14f;
            }
            float tw = font.MeasureWidth(tag, 11f);
            renderer.DrawText(font, tag, hx - tw, ry, 11f, new Vector4(0.5f, 0.6f, 0.8f, 0.6f));
            ry += lineH;
        }

        // Description of the selected entry.
        if (Selected >= 0 && Selected < rows)
        {
            string desc = _matches[Selected].Description;
            if (desc.Length == 0) desc = Localization.T("ui.palette.hint");
            var lines = SettingsPanel.Wrap(font, desc, 12f, w - pad * 2f, 1);
            if (lines.Count > 0)
                renderer.DrawText(font, lines[0], x + pad, ry + 2f, 12f, dim);
        }
    }
}
