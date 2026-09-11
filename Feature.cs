using System.Text;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace SolarSystem;

/// <summary>Where a feature shows up in the settings panel and the palette.</summary>
public enum FeatureCategory
{
    /// <summary>Things in the scene: orbits, labels, dwarfs, probes, …</summary>
    Bodies,
    /// <summary>How time / physics advance: scale, N-body, light-time, pause.</summary>
    Simulation,
    /// <summary>In-scene visual effects: wind, flares, aurora, atmosphere, PBR.</summary>
    Effects,
    /// <summary>Screen-space post-processing: bloom, FXAA, exposure, lens flare.</summary>
    PostFx,
    /// <summary>Overlays and chrome: HUD, timeline, bookmarks, audio, fullscreen.</summary>
    Interface,
    /// <summary>Profiler, hot-reload, GPU paths, recording.</summary>
    Developer,
    /// <summary>Physics sandbox: per-body mass multipliers. Appended after
    /// <see cref="Developer"/> so persisted tab indices from older saves stay valid;
    /// the panel orders it right after <see cref="Simulation"/>.</summary>
    Masses,
}

/// <summary>A single keyboard binding (key + exact modifier set).</summary>
public readonly record struct KeyBinding(Keys Key, KeyModifiers Mods = 0)
{
    private const KeyModifiers Relevant = KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt;

    /// <summary>True when the event's key and Ctrl/Shift/Alt bits match this
    /// binding exactly (CapsLock / NumLock / Super are ignored).</summary>
    public bool Matches(Keys key, KeyModifiers mods)
        => key == Key && (mods & Relevant) == (Mods & Relevant);

    /// <summary>Human-readable chord such as <c>Ctrl+Shift+P</c> or <c>~</c>.</summary>
    public string ToDisplayString()
    {
        var sb = new StringBuilder();
        if ((Mods & KeyModifiers.Control) != 0) sb.Append("Ctrl+");
        if ((Mods & KeyModifiers.Alt) != 0) sb.Append("Alt+");
        if ((Mods & KeyModifiers.Shift) != 0) sb.Append("Shift+");
        sb.Append(KeyName(Key));
        return sb.ToString();
    }

    public override string ToString() => ToDisplayString();

    public static string KeyName(Keys k) => k switch
    {
        Keys.GraveAccent => "~",
        Keys.Equal => "+",
        Keys.KeyPadAdd => "Num+",
        Keys.Minus => "-",
        Keys.KeyPadSubtract => "Num-",
        Keys.Comma => ",",
        Keys.Period => ".",
        Keys.Space => "Space",
        Keys.Escape => "Esc",
        Keys.Enter => "Enter",
        Keys.KeyPadEnter => "NumEnter",
        Keys.Tab => "Tab",
        Keys.Backspace => "Backspace",
        Keys.Delete => "Del",
        Keys.Insert => "Ins",
        Keys.Home => "Home",
        Keys.End => "End",
        Keys.PageUp => "PgUp",
        Keys.PageDown => "PgDn",
        Keys.Up => "Up",
        Keys.Down => "Down",
        Keys.Left => "Left",
        Keys.Right => "Right",
        Keys.Slash => "/",
        Keys.Backslash => "\\",
        Keys.Semicolon => ";",
        Keys.Apostrophe => "'",
        Keys.LeftBracket => "[",
        Keys.RightBracket => "]",
        >= Keys.D0 and <= Keys.D9 => ((char)('0' + (k - Keys.D0))).ToString(),
        >= Keys.KeyPad0 and <= Keys.KeyPad9 => "Num" + (k - Keys.KeyPad0),
        >= Keys.A and <= Keys.Z => ((char)('A' + (k - Keys.A))).ToString(),
        >= Keys.F1 and <= Keys.F25 => "F" + (1 + (k - Keys.F1)),
        _ => k.ToString(),
    };

    /// <summary>Parse a chord such as <c>"Ctrl+Shift+P"</c>, <c>"F1"</c>,
    /// <c>"~"</c>, <c>"Alt+Enter"</c>, <c>"Num1"</c>. Case-insensitive.
    /// Returns false on any unknown token.</summary>
    public static bool TryParse(string text, out KeyBinding binding)
    {
        binding = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var mods = (KeyModifiers)0;
        Keys? key = null;
        // '+' is both the separator and a key name: normalise the two spellings
        // that end in a literal plus ("Ctrl++", "Num+") before splitting.
        string s = text.Trim();
        if (s.EndsWith("num+", StringComparison.OrdinalIgnoreCase)) s = s[..^4] + "numadd";
        else if (s.EndsWith("++", StringComparison.Ordinal)) s = s[..^1] + "plus";
        else if (s == "+") s = "plus";
        var parts = new List<string>();
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '+' && i > start)
            {
                parts.Add(s[start..i]);
                start = i + 1;
            }
        }
        parts.Add(s[start..]);
        foreach (var raw in parts)
        {
            string p = raw.Trim();
            if (p.Length == 0) return false; // dangling '+' ("Ctrl+"); a literal plus arrives as "+" via "Ctrl++"

            switch (p.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= KeyModifiers.Control; continue;
                case "shift": mods |= KeyModifiers.Shift; continue;
                case "alt": mods |= KeyModifiers.Alt; continue;
            }
            if (key != null) return false; // two non-modifier tokens
            if (!TryParseKey(p, out var k)) return false;
            key = k;
        }
        if (key == null) return false;
        binding = new KeyBinding(key.Value, mods);
        return true;
    }

    private static bool TryParseKey(string p, out Keys key)
    {
        key = Keys.Unknown;
        string l = p.ToLowerInvariant();
        switch (l)
        {
            case "~": case "`": case "grave": key = Keys.GraveAccent; return true;
            case "+": case "=": case "plus": case "equal": key = Keys.Equal; return true;
            case "-": case "minus": key = Keys.Minus; return true;
            case ",": case "comma": key = Keys.Comma; return true;
            case ".": case "period": key = Keys.Period; return true;
            case "space": key = Keys.Space; return true;
            case "esc": case "escape": key = Keys.Escape; return true;
            case "enter": case "return": key = Keys.Enter; return true;
            case "tab": key = Keys.Tab; return true;
            case "backspace": key = Keys.Backspace; return true;
            case "del": case "delete": key = Keys.Delete; return true;
            case "ins": case "insert": key = Keys.Insert; return true;
            case "home": key = Keys.Home; return true;
            case "end": key = Keys.End; return true;
            case "pgup": case "pageup": key = Keys.PageUp; return true;
            case "pgdn": case "pagedown": key = Keys.PageDown; return true;
            case "up": key = Keys.Up; return true;
            case "down": key = Keys.Down; return true;
            case "left": key = Keys.Left; return true;
            case "right": key = Keys.Right; return true;
            case "/": case "slash": key = Keys.Slash; return true;
            case "\\": case "backslash": key = Keys.Backslash; return true;
            case ";": case "semicolon": key = Keys.Semicolon; return true;
            case "'": case "apostrophe": key = Keys.Apostrophe; return true;
            case "[": key = Keys.LeftBracket; return true;
            case "]": key = Keys.RightBracket; return true;
            case "num+": case "numadd": key = Keys.KeyPadAdd; return true;
            case "num-": case "numsub": key = Keys.KeyPadSubtract; return true;
            case "numenter": key = Keys.KeyPadEnter; return true;
        }
        if (l.Length == 1)
        {
            char c = l[0];
            if (c >= 'a' && c <= 'z') { key = Keys.A + (c - 'a'); return true; }
            if (c >= '0' && c <= '9') { key = Keys.D0 + (c - '0'); return true; }
            return false;
        }
        if (l.StartsWith("num") && l.Length == 4 && char.IsDigit(l[3]))
        {
            key = Keys.KeyPad0 + (l[3] - '0');
            return true;
        }
        if (l.Length >= 2 && l[0] == 'f' && int.TryParse(l.AsSpan(1), out int fn) && fn >= 1 && fn <= 25)
        {
            key = Keys.F1 + (fn - 1);
            return true;
        }
        return false;
    }
}

/// <summary>Anything that can be listed, searched, bound to a key and shown in
/// the settings panel: a boolean <see cref="Feature"/> or a fire-and-forget
/// <see cref="Command"/>.</summary>
public abstract class Entry
{
    /// <summary>Stable id — used for persistence, keybindings.json and palette search.</summary>
    public required string Id { get; init; }
    public required FeatureCategory Category { get; init; }
    /// <summary>Localisation key for the human label.</summary>
    public required string LabelKey { get; init; }
    /// <summary>Optional localisation key for a one-line description (hover /
    /// palette subtitle). Defaults to <c>ui.desc.&lt;id&gt;</c>.</summary>
    public string? DescKey { get; init; }
    /// <summary>Default key chords. Replaced wholesale by a keybindings.json entry.</summary>
    public List<KeyBinding> Bindings { get; } = new();
    /// <summary>Hide from the Ctrl+K palette (still bindable / persisted).</summary>
    public bool HideInPalette { get; init; }
    /// <summary>Hide from the generated help overlay (for families such as
    /// Ctrl+1…9 that get a single hand-written line instead).</summary>
    public bool HideInHelp { get; init; }
    /// <summary>Returns null when the entry is usable, otherwise a localisation
    /// key explaining why it's greyed out.</summary>
    public Func<string?>? Unavailable { get; init; }

    /// <summary>Dynamic label override (e.g. "Focus: Earth"); wins over <see cref="LabelKey"/>.</summary>
    public Func<string>? LabelFn { get; init; }

    public string Label => LabelFn?.Invoke() ?? Localization.T(LabelKey);
    public string Description
    {
        get
        {
            string key = DescKey ?? ("ui.desc." + Id);
            string v = Localization.T(key);
            return v == key ? "" : v;
        }
    }
    public bool IsAvailable => Unavailable == null || Unavailable() == null;

    public string BindingText
    {
        get
        {
            if (Bindings.Count == 0) return "";
            // Show only the first binding; numpad duplicates etc. stay hidden.
            return Bindings[0].ToDisplayString();
        }
    }

    public Entry WithKey(Keys key, KeyModifiers mods = 0)
    {
        Bindings.Add(new KeyBinding(key, mods));
        return this;
    }
}

/// <summary>A boolean switch. <see cref="Set"/> performs every side-effect
/// (clearing trails, resyncing the integrator, …) so the key handler, the
/// panel, the palette, presets and the persisted-state loader all go through
/// the same code path.</summary>
public sealed class Feature : Entry
{
    public required Func<bool> Get { get; init; }
    public required Action<bool> Set { get; init; }
    public bool Default { get; init; }
    /// <summary>Write to state.json. Off for transient states such as recording.</summary>
    public bool Persist { get; init; } = true;
    /// <summary>Property name in the pre-registry <c>state.json</c> layout, used
    /// once to migrate old saves.</summary>
    public string? LegacyKey { get; init; }
    /// <summary>Optional banner text shown after a toggle. Receives the new value.</summary>
    public Func<bool, string?>? Banner { get; init; }

    public bool Value => Get();

    /// <summary>Flip the switch. Returns false (and leaves the value alone)
    /// when the feature is unavailable.</summary>
    public bool Toggle()
    {
        if (!IsAvailable) return false;
        Set(!Get());
        return true;
    }

    /// <summary>Set to a specific value, skipping the side-effects when the
    /// value is already current.</summary>
    public void Apply(bool value)
    {
        if (!IsAvailable) return;
        if (Get() == value) return;
        Set(value);
    }
}

/// <summary>A multi-option selector (e.g. the simulation mode). Persisted as the
/// option index; the palette / a bound key cycle it, the panel shows
/// <c>◂ value ▸</c> arrows.</summary>
public sealed class Choice : Entry
{
    public required Func<int> Get { get; init; }
    public required Action<int> Set { get; init; }
    /// <summary>Localisation keys of the options, in index order.</summary>
    public required IReadOnlyList<string> OptionKeys { get; init; }
    public int Default { get; init; }
    /// <summary>Write to state.json (<c>Choices: {id: index}</c>).</summary>
    public bool Persist { get; init; } = true;
    /// <summary>Optional banner text after a change. Receives the new index.</summary>
    public Func<int, string?>? Banner { get; init; }

    public int Count => OptionKeys.Count;
    public int Value => Math.Clamp(Get(), 0, Math.Max(0, Count - 1));
    public string OptionLabel(int index) => Localization.T(OptionKeys[Math.Clamp(index, 0, Count - 1)]);
    public string ValueLabel => OptionLabel(Value);

    /// <summary>Select an option (clamped). Skips the side-effects when it is already
    /// current. Returns false when the entry is unavailable.</summary>
    public bool Apply(int index)
    {
        if (!IsAvailable) return false;
        index = Math.Clamp(index, 0, Math.Max(0, Count - 1));
        if (Get() == index) return true;
        Set(index);
        return true;
    }

    /// <summary>Move by <paramref name="delta"/> options, wrapping around.</summary>
    public bool Cycle(int delta = 1)
    {
        if (!IsAvailable || Count == 0) return false;
        int next = ((Value + delta) % Count + Count) % Count;
        Set(next);
        return true;
    }
}

/// <summary>A numeric setting: rendered as a slider row in the panel, listed in
/// the palette with its live value (Enter resets it to <see cref="Default"/>).
/// The registry does not persist sliders — whoever owns the value does (the
/// physics constants live in their own <c>state.json</c> section).</summary>
public sealed class Slider : Entry
{
    public required Func<double> Get { get; init; }
    public required Action<double> Set { get; init; }
    public double Min { get; init; }
    public double Max { get; init; } = 1.0;
    /// <summary>Nudge amount for the −/+ buttons: additive, or a multiplicative
    /// factor when <see cref="LogScale"/> is set.</summary>
    public double Step { get; init; } = 0.01;
    /// <summary>Map the track logarithmically between <see cref="Min"/> and <see cref="Max"/>
    /// (both must be &gt; 0). Right for multipliers spanning 0.01×…100×.</summary>
    public bool LogScale { get; init; }
    public double Default { get; init; }
    /// <summary>Composite format string for the value read-out.</summary>
    public string Format { get; init; } = "{0:0.##}";

    public double Value => Get();
    public string ValueText => string.Format(System.Globalization.CultureInfo.InvariantCulture, Format, Value);

    public void Apply(double value)
    {
        if (!IsAvailable) return;
        if (double.IsNaN(value)) return;
        Set(Math.Clamp(value, Min, Max));
    }

    public void ResetToDefault() => Apply(Default);
}

/// <summary>A non-boolean action: screenshot, focus a body, cycle language, …</summary>
public sealed class Command : Entry
{
    public required Action Run { get; init; }
    /// <summary>Optional live suffix shown after the label (e.g. current language).</summary>
    public Func<string>? Status { get; init; }
    /// <summary>Whether the palette should close after running. Toggles stay open
    /// so the user can flip several in a row; navigation commands close.</summary>
    public bool ClosesPalette { get; init; } = true;
    /// <summary>Also expose the command as a button row in the F1 panel, so
    /// mouse-only users can reach it.</summary>
    public bool ShowInPanel { get; init; }
}

/// <summary>A named bundle of feature values. Applied on top of defaults for the
/// scene categories only (Bodies / Simulation / Effects / PostFx) so it never
/// yanks the window out of fullscreen or hides the panel you clicked in.</summary>
public sealed class FeaturePreset
{
    public required string Id { get; init; }
    public required string LabelKey { get; init; }
    public Dictionary<string, bool> Overrides { get; init; } = new();
    /// <summary>Option indices for <see cref="Choice"/> entries (e.g. the simulation mode).</summary>
    public Dictionary<string, int> Choices { get; init; } = new();
    /// <summary>Dynamic label override (e.g. "Focus: Earth"); wins over <see cref="LabelKey"/>.</summary>
    public Func<string>? LabelFn { get; init; }

    public string Label => LabelFn?.Invoke() ?? Localization.T(LabelKey);
}
