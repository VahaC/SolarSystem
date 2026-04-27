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
        // Persistent discovery hint shown below the date/speed line when the
        // full cheat sheet is collapsed (Tab cycle), so first-time users on a
        // small monitor still know how to reach every menu.
        ["ui.help.hint"]       = "Tab — help · F1 — settings · F3 — bookmarks · Ctrl+F — search",
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
            "F4          tidal-lock arrows (S13)\n" +
            "F5          alignment indicator (S14)\n" +
            "F6          N-body mode (S15)\n" +
            "F7          GLSL hot-reload (A6)\n" +
            "F8          GPU asteroid belt (A8)\n" +
            "F9          start / stop video recording (A7)\n" +
            "F10         per-pass profiler overlay (A12)\n" +
            "Z           toggle lens flare (V6)\n" +
            "Alt+Enter   toggle fullscreen\n" +
            "Tab         help full / mini / off (Q14)\n" +
            "S           toggle audio cues (Q15)\n" +
            "Esc         quit",
        ["ui.campath.banner"]  = "Camera path",
        ["ui.tooltip.sun"]     = "Sun",
        // S13 / S14 / S15 toggle banners.
        ["ui.tidal.on"]        = "Tidal-lock arrows: ON",
        ["ui.tidal.off"]       = "Tidal-lock arrows: OFF",
        ["ui.alignment.on"]    = "Alignment indicator: ON",
        ["ui.alignment.off"]   = "Alignment indicator: OFF",
        ["ui.alignment.banner"]= "Alignment ({0}): {1}",
        ["ui.nbody.on"]        = "N-body mode: ON (mutual gravity)",
        ["ui.nbody.off"]       = "N-body mode: OFF (analytic Kepler)",
        ["ui.nbody.banner"]    = "N-body mode (S15)",
        ["ui.lensflare.on"]    = "Lens flare: ON",
        ["ui.lensflare.off"]   = "Lens flare: OFF",
        // A6: GLSL hot-reload (F7).
        ["ui.hotreload.on"]    = "GLSL hot-reload: ON (watching Resources/Shaders)",
        ["ui.hotreload.off"]   = "GLSL hot-reload: OFF",
        ["ui.hotreload.swap"]  = "Reloaded shader: {0}",
        ["ui.hotreload.error"] = "Shader error in {0}: {1}",
        // A7: headless render banner (only seen if --render is launched with a window).
        ["ui.render.progress"] = "Rendering frame {0}/{1} ({2:0.0}%)",
        ["ui.render.done"]     = "Render complete: {0} frames -> {1}",
        // A7 (interactive): F9 toggles in-app recording to a timestamped folder.
        ["ui.record.start"]    = "● REC started -> {0}",
        ["ui.record.stop"]     = "■ REC stopped: {0} frames in {1:0.0}s",
        ["ui.record.encoded"]  = "■ REC encoded -> {0}",
        ["ui.record.status"]   = "● REC {0:mm\\:ss}  {1} frames",
        // Settings panel (F1) row labels.
        ["ui.settings.orbits"]        = "Orbits",
        ["ui.settings.trails"]        = "Trails",
        ["ui.settings.labels"]        = "Labels",
        ["ui.settings.axes"]          = "Axes",
        ["ui.settings.dwarfs"]        = "Dwarfs",
        ["ui.settings.constellations"]= "Constellations",
        ["ui.settings.probes"]        = "Probes",
        ["ui.settings.lagrange"]      = "Lagrange",
        ["ui.settings.meteors"]       = "Meteors",
        ["ui.settings.aurora"]        = "Aurora",
        ["ui.settings.solarwind"]     = "Solar wind",
        ["ui.settings.solarflares"]   = "Solar flares",
        ["ui.settings.bloom"]         = "Bloom",
        ["ui.settings.fxaa"]          = "FXAA",
        ["ui.settings.pbr"]           = "PBR",
        ["ui.settings.audio"]         = "Audio",
        ["ui.settings.timeline"]      = "Timeline",
        ["ui.settings.tidal"]         = "Tidal lock (S13)",
        ["ui.settings.alignment"]     = "Alignment (S14)",
        ["ui.settings.nbody"]         = "N-body (S15)",
        ["ui.settings.lensflare"]     = "Lens flare",
        ["ui.settings.speed"]         = "Speed (d/s)",
        // A8: GPU compute path for the asteroid belt (toggle with F8).
        ["ui.settings.gpubelt"]       = "GPU asteroids (A8)",
        ["ui.gpubelt.on"]             = "GPU asteroid belt: ON (compute shader)",
        ["ui.gpubelt.off"]            = "GPU asteroid belt: OFF (CPU Kepler solve)",
        ["ui.gpubelt.unavailable"]    = "GPU asteroid belt unavailable on this driver",
        // Alt+Enter borderless fullscreen toggle.
        ["ui.settings.fullscreen"]    = "Fullscreen (Alt+Enter)",
        ["ui.fullscreen.on"]          = "Fullscreen: ON",
        ["ui.fullscreen.off"]         = "Fullscreen: OFF",
        // A12: per-frame profiler overlay (F10).
        ["ui.settings.profiler"]      = "Profiler (A12)",
        ["ui.profiler.on"]            = "Profiler: ON",
        ["ui.profiler.off"]           = "Profiler: OFF",
        ["ui.profiler.title"]         = "Profiler (F10)",
        ["ui.profiler.frame"]         = "frame      {0,5:0.00} ms",
        ["ui.profiler.header.gpu"]    = "pass        gpu | cpu (ms)",
        ["ui.profiler.header.cpu"]    = "pass         cpu (ms)",
        ["ui.profiler.total"]         = "total",
        ["ui.profiler.pass.sky"]      = "sky",
        ["ui.profiler.pass.planets"]  = "planets",
        ["ui.profiler.pass.particles"]= "particles",
        ["ui.profiler.pass.bloom"]    = "bloom",
        ["ui.profiler.pass.ui"]       = "ui",
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
            // A11: AOT-friendly source-generated typeinfo.
            var dict = JsonSerializer.Deserialize(json, SolarSystemJsonContext.Default.DictionaryStringString);
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
