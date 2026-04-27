using System.IO;
using System.Text.Json;

namespace SolarSystem;

/// <summary>
/// Q13: Lightweight string-table localisation. UI labels are looked up by key;
/// missing keys fall back to the embedded English defaults so calling sites
/// never need a null check. Translations live in <c>data/lang.&lt;code&gt;.json</c>
/// (e.g. <c>lang.uk.json</c>); the file is a flat
/// <c>{"key": "translated"}</c> JSON object so contributors can add languages
/// without touching the C# code. Switching language at runtime is a no-op
/// re-read — no GL state needs invalidating.
/// </summary>
public static class Localization
{
    private static readonly Dictionary<string, string> _en = new(StringComparer.Ordinal)
    {
        ["ui.date"]            = "Date",
        ["ui.speed"]           = "Speed",
        ["ui.paused"]          = "PAUSED",
        ["ui.orbits"]          = "Orbits",
        ["ui.labels"]          = "Labels",
        ["ui.on"]              = "on",
        ["ui.off"]             = "off",
        ["ui.scale.real"]      = "real",
        ["ui.scale.compressed"]= "compressed",
        ["ui.click.body"]      = "Click a body for info\nDouble-click to focus\nDouble-click empty to unfocus",
        ["ui.help.title"]      = "Controls",
        ["ui.audio.on"]        = "Audio: ON",
        ["ui.audio.off"]       = "Audio: OFF",
        ["ui.lang.toggled"]    = "Language: {0}",
        ["ui.settings.title"]  = "Settings panel — click to toggle, [Esc] close",
        ["ui.scrubber.hint"]   = "drag to seek",
        // Info panel (bottom-left selected body card)
        ["ui.info.radius"]     = "Radius",
        ["ui.info.day"]        = "Day",
        ["ui.info.year"]       = "Year",
        ["ui.info.dist"]       = "Dist",
        ["ui.info.tilt"]       = "Tilt",
        ["ui.info.mass"]       = "Mass",
        ["ui.info.type"]       = "Type",
        ["ui.info.retro"]      = " (retro)",
        ["ui.info.sun.day"]    = "609.12 h (eq)",
        ["ui.info.sun.mass"]   = "1.989e30 kg",
        ["ui.info.sun.type"]   = "G2V star",
        ["ui.body.sun"]        = "Sun",
        // Modal prompts and feedback banners
        ["ui.seek.prompt"]     = "Jump to date (YYYY-MM-DD) or +/-N days:\n> {0}_",
        ["ui.search.prompt"]   = "Search body (Enter=focus, Esc=cancel):\n> {0}_",
        // HUD overlay (top-right)
        ["ui.hud.fps"]         = "FPS",
        ["ui.hud.scale"]       = "Scale",
        ["ui.hud.wind"]        = "Wind",
        ["ui.hud.flares"]      = "Flares",
        ["ui.hud.comet"]       = "Comet",
        ["ui.hud.belt"]        = "Belt",
        // Speed-line suffix
        ["ui.speed.reverse"]   = "(reverse)",
        // Meteor banner
        ["ui.meteors.active"]  = "{0} active",
        // Help overlay (top-left controls list)
        ["ui.help.body"] =
            "RMB drag    orbit camera\n" +
            "MMB drag    pan camera\n" +
            "Wheel       zoom\n" +
            "Click body  show info\n" +
            "Dbl-click   focus body\n" +
            "Dbl empty   unfocus\n" +
            "0           Sun  /  1-8 planet\n" +
            "Space       pause / resume\n" +
            ", / .       reverse / forward\n" +
            "+ / -       sim speed\n" +
            "O           toggle orbits\n" +
            "L           toggle labels\n" +
            "T           toggle trails\n" +
            "A           toggle axes\n" +
            "D           toggle dwarf planets\n" +
            "C           toggle constellations\n" +
            "P           toggle probes\n" +
            "G           toggle Lagrange points\n" +
            "M           toggle meteor showers\n" +
            "Ctrl+E      cycle bookmarks (S12)\n" +
            "F3          bookmarks sidebar (Q8)\n" +
            "J           jump to date / +/-days\n" +
            "Ctrl+F      search bodies\n" +
            "F12         screenshot\n" +
            "~           FPS / particle HUD\n" +
            "W           toggle solar wind\n" +
            "F           toggle solar flares\n" +
            "R           real / compressed scale\n" +
            "Y           toggle light-time delay\n" +
            "B           toggle bloom (V1)\n" +
            "H           toggle eclipses (V8)\n" +
            "N           toggle atmosphere (V9)\n" +
            "E           toggle auto-exposure (V10)\n" +
            "X           toggle FXAA (V11)\n" +
            "U           toggle sun corona (V12)\n" +
            "K           toggle aurora (V13)\n" +
            "I           toggle PBR shading (V14)\n" +
            "Q           toggle ocean specular (V15)\n" +
            "V           timeline scrubber (Q9)\n" +
            "Ctrl 1-9    record waypoint (Q10)\n" +
            "Shift+P     play camera path (Q10)\n" +
            "F1          settings panel (Q12)\n" +
            "F2          cycle language (Q13)\n" +
            "Tab         help full / mini / off (Q14)\n" +
            "S           toggle audio cues (Q15)\n" +
            "Esc         quit",
        ["ui.campath.banner"]  = "Camera path",
        ["ui.tooltip.sun"]     = "Sun",
    };

    private static Dictionary<string, string> _active = _en;
    public static string CurrentLanguage { get; private set; } = "en";
    public static IReadOnlyList<string> Available { get; private set; } = new[] { "en" };

    /// <summary>Look up a key; returns the embedded English default when the key
    /// is missing in both the active and the fallback table.</summary>
    public static string T(string key)
    {
        if (_active.TryGetValue(key, out var v)) return v;
        return _en.TryGetValue(key, out var def) ? def : key;
    }

    public static string T(string key, params object?[] args)
        => string.Format(T(key), args);

    /// <summary>Switch to a language code (e.g. "uk", "en"). Falls back to
    /// English silently if the file is missing or unreadable.</summary>
    public static void SetLanguage(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Equals("en", StringComparison.OrdinalIgnoreCase))
        {
            _active = _en;
            CurrentLanguage = "en";
            return;
        }
        try
        {
            string? path = Resolve($"lang.{code}.json");
            if (path == null || !File.Exists(path)) { _active = _en; CurrentLanguage = "en"; return; }
            var json = File.ReadAllText(path);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            _active = dict ?? _en;
            CurrentLanguage = code.ToLowerInvariant();
        }
        catch { _active = _en; CurrentLanguage = "en"; }
    }

    /// <summary>Discover available <c>lang.*.json</c> files next to the running
    /// binary; "en" is always included.</summary>
    public static void DiscoverAvailable()
    {
        var found = new List<string> { "en" };
        try
        {
            string dirA = Path.Combine(AppContext.BaseDirectory, "data");
            string dirB = Path.Combine(Environment.CurrentDirectory, "data");
            foreach (var dir in new[] { dirA, dirB })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "lang.*.json"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (name.StartsWith("lang.", StringComparison.OrdinalIgnoreCase))
                    {
                        var code = name.Substring(5);
                        if (!found.Contains(code, StringComparer.OrdinalIgnoreCase)) found.Add(code);
                    }
                }
            }
        }
        catch { /* ignore */ }
        Available = found;
    }

    /// <summary>Cycle to the next available language. Returns the new code.</summary>
    public static string CycleNext()
    {
        if (Available.Count <= 1) return CurrentLanguage;
        int i = 0;
        for (int k = 0; k < Available.Count; k++)
            if (string.Equals(Available[k], CurrentLanguage, StringComparison.OrdinalIgnoreCase)) { i = k; break; }
        var next = Available[(i + 1) % Available.Count];
        SetLanguage(next);
        return CurrentLanguage;
    }

    private static string? Resolve(string fileName)
    {
        string a = Path.Combine(AppContext.BaseDirectory, "data", fileName);
        if (File.Exists(a)) return a;
        string b = Path.Combine("data", fileName);
        return File.Exists(b) ? b : null;
    }
}
