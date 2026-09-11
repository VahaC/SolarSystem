using System.Diagnostics;
using System.IO;
using System.Text.Json;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace SolarSystem;

/// <summary>
/// Single source of truth for every toggle and command in the app. The key
/// handler, the F1 settings panel, the Ctrl+K palette, the help overlay, the
/// toolbar and <c>state.json</c> are all derived from this list, so adding a
/// feature means adding exactly one entry here (plus its localisation keys).
/// </summary>
public sealed class FeatureRegistry
{
    private readonly List<Entry> _entries = new();
    private readonly Dictionary<string, Entry> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FeaturePreset> _presets = new();

    public IReadOnlyList<Entry> Entries => _entries;
    public IReadOnlyList<FeaturePreset> Presets => _presets;
    public IEnumerable<Feature> Features => _entries.OfType<Feature>();
    public IEnumerable<Command> Commands => _entries.OfType<Command>();

    /// <summary>Categories whose features are touched by presets and "reset".</summary>
    public static readonly FeatureCategory[] SceneCategories =
    {
        FeatureCategory.Bodies, FeatureCategory.Simulation,
        FeatureCategory.Effects, FeatureCategory.PostFx,
    };

    public T Add<T>(T entry) where T : Entry
    {
        if (_byId.ContainsKey(entry.Id))
            throw new InvalidOperationException($"Duplicate feature id '{entry.Id}'");
        _entries.Add(entry);
        _byId[entry.Id] = entry;
        return entry;
    }

    public FeaturePreset AddPreset(FeaturePreset preset)
    {
        _presets.Add(preset);
        return preset;
    }

    public Entry? Find(string id) => _byId.TryGetValue(id, out var e) ? e : null;
    public Feature? FindFeature(string id) => Find(id) as Feature;

    public IEnumerable<Feature> FeaturesIn(FeatureCategory cat)
        => Features.Where(f => f.Category == cat);

    // ---- Keyboard dispatch ----------------------------------------------------

    /// <summary>Find the entry bound to this key chord, or null.</summary>
    public Entry? Resolve(Keys key, KeyModifiers mods)
    {
        foreach (var e in _entries)
            foreach (var b in e.Bindings)
                if (b.Matches(key, mods)) return e;
        return null;
    }

    /// <summary>Run whatever is bound to the chord. Returns the entry that
    /// handled it, or null when nothing is bound. For features the optional
    /// <paramref name="banner"/> callback receives the post-toggle banner text.</summary>
    public Entry? Dispatch(Keys key, KeyModifiers mods, Action<string>? banner = null)
    {
        var e = Resolve(key, mods);
        if (e == null) return null;
        Invoke(e, banner);
        return e;
    }

    /// <summary>Toggle a feature or run a command, surfacing a banner either way.</summary>
    public void Invoke(Entry e, Action<string>? banner = null)
    {
        switch (e)
        {
            case Feature f:
                if (!f.IsAvailable)
                {
                    string? why = f.Unavailable?.Invoke();
                    if (why != null) banner?.Invoke(Localization.T(why));
                    return;
                }
                f.Toggle();
                string? text = f.Banner?.Invoke(f.Value)
                    ?? $"{f.Label}: {Localization.T(f.Value ? "ui.on" : "ui.off").ToUpperInvariant()}";
                if (text.Length > 0) banner?.Invoke(text);
                break;
            case Command c:
                if (!c.IsAvailable)
                {
                    string? why = c.Unavailable?.Invoke();
                    if (why != null) banner?.Invoke(Localization.T(why));
                    return;
                }
                c.Run();
                break;
        }
    }

    /// <summary>Every (chord → entry) pair, for the help overlay and for the
    /// uniqueness test.</summary>
    public IEnumerable<(KeyBinding Binding, Entry Entry)> AllBindings()
    {
        foreach (var e in _entries)
            foreach (var b in e.Bindings)
                yield return (b, e);
    }

    // ---- keybindings.json ------------------------------------------------------

    /// <summary>Apply user overrides from a flat <c>{"id": "Ctrl+X, Num1"}</c>
    /// JSON object. An empty string unbinds. Unknown ids and unparsable chords
    /// are logged and skipped. Returns the number of ids changed.</summary>
    public int ApplyBindingOverrides(Dictionary<string, string> overrides, Action<string>? warn = null)
    {
        int changed = 0;
        foreach (var (id, chords) in overrides)
        {
            var e = Find(id);
            if (e == null) { warn?.Invoke($"keybindings: unknown id '{id}'"); continue; }
            var list = new List<KeyBinding>();
            bool ok = true;
            foreach (var part in chords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (KeyBinding.TryParse(part, out var b)) list.Add(b);
                else { warn?.Invoke($"keybindings: cannot parse '{part}' for '{id}'"); ok = false; }
            }
            if (!ok) continue;
            e.Bindings.Clear();
            e.Bindings.AddRange(list);
            changed++;
        }
        return changed;
    }

    /// <summary>Load <c>data/keybindings.json</c> (next to the binary or the CWD)
    /// if present. Comments and trailing commas are tolerated.</summary>
    public int TryLoadBindingsFile(Action<string>? warn = null)
    {
        try
        {
            string? path = ResolveDataFile("keybindings.json");
            if (path == null) return 0;
            var opts = new JsonSerializerOptions
            {
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            var ctx = new SolarSystemJsonContext(opts);
            var dict = JsonSerializer.Deserialize(File.ReadAllText(path), ctx.DictionaryStringString);
            if (dict == null) return 0;
            int n = ApplyBindingOverrides(dict, warn);
            Debug.WriteLine($"[keys] {n} binding(s) overridden from {path}");
            return n;
        }
        catch (Exception ex)
        {
            warn?.Invoke($"keybindings.json: {ex.Message}");
            return 0;
        }
    }

    private static string? ResolveDataFile(string fileName)
    {
        string a = Path.Combine(AppContext.BaseDirectory, "data", fileName);
        if (File.Exists(a)) return a;
        string b = Path.Combine("data", fileName);
        return File.Exists(b) ? b : null;
    }

    // ---- Persistence -------------------------------------------------------------

    /// <summary>Snapshot every persistable feature as <c>id → value</c>.</summary>
    public Dictionary<string, bool> Snapshot()
    {
        var d = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var f in Features)
            if (f.Persist) d[f.Id] = f.Value;
        return d;
    }

    /// <summary>Apply a saved snapshot. Ids that are missing keep their current
    /// value; unknown ids are ignored. Features are applied in registry order
    /// (so e.g. real-scale can be declared first and run before anything that
    /// depends on the scale mode).</summary>
    public void Restore(IReadOnlyDictionary<string, bool> values)
    {
        foreach (var f in Features)
        {
            if (!f.Persist) continue;
            if (values.TryGetValue(f.Id, out bool v)) f.Apply(v);
        }
    }

    /// <summary>One-shot migration from the pre-registry <c>state.json</c>
    /// layout (one PascalCase bool property per toggle). Reads the raw document
    /// so no DTO has to keep the old properties alive.</summary>
    public Dictionary<string, bool> MigrateLegacy(JsonElement root)
    {
        var d = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object) return d;
        foreach (var f in Features)
        {
            if (f.LegacyKey == null) continue;
            if (root.TryGetProperty(f.LegacyKey, out var prop)
                && (prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False))
                d[f.Id] = prop.GetBoolean();
        }
        return d;
    }

    // ---- Presets / reset -----------------------------------------------------------

    /// <summary>Reset every feature in <paramref name="cat"/> to its default.</summary>
    public void ResetCategory(FeatureCategory cat)
    {
        foreach (var f in FeaturesIn(cat)) f.Apply(f.Default);
    }

    /// <summary>Set every feature in <paramref name="cat"/> to <paramref name="value"/>.</summary>
    public void SetCategory(FeatureCategory cat, bool value)
    {
        foreach (var f in FeaturesIn(cat)) f.Apply(value);
    }

    /// <summary>Defaults for every scene category, then the preset's overrides.</summary>
    public void ApplyPreset(FeaturePreset preset)
    {
        foreach (var cat in SceneCategories) ResetCategory(cat);
        foreach (var (id, v) in preset.Overrides)
            FindFeature(id)?.Apply(v);
    }

    /// <summary>True when the current scene-category state equals what
    /// <paramref name="preset"/> would produce — used to highlight the active preset.</summary>
    public bool MatchesPreset(FeaturePreset preset)
    {
        foreach (var cat in SceneCategories)
            foreach (var f in FeaturesIn(cat))
            {
                if (!f.IsAvailable) continue;
                bool expected = preset.Overrides.TryGetValue(f.Id, out bool o) ? o : f.Default;
                if (f.Value != expected) return false;
            }
        return true;
    }

    // ---- Palette search -------------------------------------------------------------

    /// <summary>Rank entries against a free-text query. Empty query returns
    /// every palette-visible entry in registry order. Matching is
    /// case-insensitive against the localised label, the id and the
    /// description; label prefix &gt; label substring &gt; id &gt; description.</summary>
    public List<Entry> Search(string query, int max)
    {
        var result = new List<(Entry e, int rank, int order)>();
        string q = query.Trim();
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            if (e.HideInPalette) continue;
            if (q.Length == 0) { result.Add((e, 0, i)); continue; }
            int rank = Rank(e, q);
            if (rank >= 0) result.Add((e, rank, i));
        }
        result.Sort((a, b) =>
        {
            int c = a.rank.CompareTo(b.rank);
            return c != 0 ? c : a.order.CompareTo(b.order);
        });
        var top = new List<Entry>(Math.Min(max, result.Count));
        for (int i = 0; i < result.Count && i < max; i++) top.Add(result[i].e);
        return top;
    }

    private static int Rank(Entry e, string q)
    {
        string label = e.Label;
        int p = label.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        if (p == 0) return 0;
        if (p > 0) return label[p - 1] == ' ' ? 1 : 2;
        if (e.Id.Contains(q, StringComparison.OrdinalIgnoreCase)) return 3;
        if (e.Description.Contains(q, StringComparison.OrdinalIgnoreCase)) return 4;
        return -1;
    }
}
