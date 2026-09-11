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
        ["ui.help.hint"]       = "Tab — help · F1 — settings · Ctrl+K — commands · F3 — bookmarks",
        ["ui.audio.on"]        = "Audio: ON",
        ["ui.audio.off"]       = "Audio: OFF",
        ["ui.lang.toggled"]    = "Language: {0}",
        ["ui.settings.title"]  = "Settings",
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
        ["ui.campath.banner"]  = "Camera path",
        ["ui.tooltip.sun"]     = "Sun",
        // S13 / S14 / S15 toggle banners.
        ["ui.tidal.on"]        = "Tidal-lock arrows: ON",
        ["ui.tidal.off"]       = "Tidal-lock arrows: OFF",
        ["ui.alignment.on"]    = "Alignment indicator: ON",
        ["ui.alignment.off"]   = "Alignment indicator: OFF",
        ["ui.alignment.banner"]= "Alignment ({0}): {1}",
        // Physics sandbox: mode selector, constants, diagnostics.
        ["ui.settings.simmode"]           = "Simulation mode",
        ["ui.simmode.ephemeris"]          = "Ephemeris",
        ["ui.simmode.physics"]            = "Physics",
        ["ui.simmode.compare"]            = "Compare",
        ["ui.simmode.banner"]             = "Simulation: {0}",
        ["ui.settings.physics.g"]         = "Gravity G",
        ["ui.settings.physics.sunmass"]   = "Sun mass",
        ["ui.settings.physics.exponent"]  = "Gravity exponent n",
        ["ui.settings.physics.lightspeed"]= "Speed of light",
        ["ui.settings.physics.hud"]       = "Physics diagnostics",
        ["ui.settings.physics.collisions"]= "Collisions (merge on contact)",
        ["ui.physics.collisions.on"]      = "Collisions: ON — touching bodies merge",
        ["ui.physics.collisions.off"]     = "Collisions: OFF — bodies pass through each other",
        ["ui.physics.collision.banner"]   = "Collision: {0} merged into {1}",
        ["ui.unavailable.absorbed"]       = "absorbed in a collision",
        ["ui.settings.mass"]              = "Mass: {0}",
        ["ui.cmd.physics.resetconst"]     = "Reset constants",
        ["ui.cmd.physics.reinit"]         = "Restart from ephemeris",
        ["ui.cmd.physics.resetmasses"]    = "Reset masses",
        ["ui.tab.masses"]                 = "Masses",
        ["ui.unavailable.ephemeris"]      = "switch to Physics or Compare",
        ["ui.slider.reset"]               = "{0}: reset to {1}",
        ["ui.physics.banner.physics"]     = "Physics mode — N-body sandbox",
        ["ui.physics.banner.compare"]     = "Compare mode — physics vs ephemeris",
        ["ui.physics.progress"]           = "Integrating {0} → {1}  {2:0}%",
        ["ui.physics.reinit.done"]        = "Physics restarted from the ephemeris at {0}",
        ["ui.physics.const.reset"]        = "Constants reset to real values",
        ["ui.physics.masses.reset"]       = "Masses reset to real values",
        ["ui.physics.hud.step"]           = "step   {0:0.000} d (global)",
        ["ui.physics.hud.steps"]          = "steps  {0} global · {1} satellite · {2} belt",
        // ASCII on purpose: the bitmap font atlas has no glyphs for Δ / ₀ / ☉.
        ["ui.physics.hud.energy"]         = "dE/E0  {0}    d|L|/|L0|  {1}",
        ["ui.physics.hud.constants"]      = "G x{0:0.###}   Msun x{1:0.###}   n {2:0.00}   c x{3:0.###}",
        ["ui.physics.hud.elements.au"]    = "{0} (primary {1}):  a {2:0.0000} AU   e {3:0.0000}   P {4:0.0} d",
        ["ui.physics.hud.elements.km"]    = "{0} (primary {1}):  a {2:0} km   e {3:0.0000}   P {4:0.000} d",
        ["ui.physics.hud.unbound"]        = "{0}: unbound from {1} (hyperbolic)",
        ["ui.physics.hud.collisions"]     = "collisions  {0}  ({1})",
        ["ui.physics.hud.collision"]      = "  {0}  {1} -> {2}   {3:0.0} km/s   {4} J",
        ["ui.physics.hud.absorbed"]       = "{0}: absorbed by {1} on {2}",
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
        ["ui.settings.tidal"]         = "Tidal-lock arrows",
        ["ui.settings.alignment"]     = "Alignment indicator",
        ["ui.settings.lensflare"]     = "Lens flare",
        ["ui.settings.speed"]         = "Speed (d/s)",
        // A8: GPU compute path for the asteroid belt (toggle with F8).
        ["ui.settings.gpubelt"]       = "GPU asteroid belt",
        ["ui.gpubelt.on"]             = "GPU asteroid belt: ON (compute shader)",
        ["ui.gpubelt.off"]            = "GPU asteroid belt: OFF (CPU Kepler solve)",
        ["ui.gpubelt.unavailable"]    = "GPU asteroid belt unavailable on this driver",
        // Alt+Enter borderless fullscreen toggle.
        ["ui.settings.fullscreen"]    = "Fullscreen",
        ["ui.fullscreen.on"]          = "Fullscreen: ON",
        ["ui.fullscreen.off"]         = "Fullscreen: OFF",
        // A12: per-frame profiler overlay (F10).
        ["ui.settings.profiler"]      = "Profiler overlay",
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

        // ---- Feature registry: tabs, presets, panel chrome ----
        ["ui.tab.bodies"]             = "Bodies",
        ["ui.tab.simulation"]         = "Simulation",
        ["ui.tab.effects"]            = "Effects",
        ["ui.tab.postfx"]             = "Post-FX",
        ["ui.tab.interface"]          = "Interface",
        ["ui.tab.developer"]          = "Developer",
        ["ui.settings.presets"]       = "Presets:",
        ["ui.settings.reset"]         = "Reset tab",
        ["ui.settings.allon"]         = "All on",
        ["ui.settings.alloff"]        = "All off",
        ["ui.settings.footer.hint"]   = "Hover a row for details · ← → switch tabs · Esc closes",
        ["ui.preset.cinematic"]       = "Cinematic",
        ["ui.preset.realistic"]       = "Realistic",
        ["ui.preset.performance"]     = "Performance",
        ["ui.preset.minimal"]         = "Minimal",
        ["ui.preset.applied"]         = "Preset applied: {0}",
        // Feature labels that previously only existed as hotkeys.
        ["ui.settings.realscale"]     = "Real scale",
        ["ui.settings.pause"]         = "Pause",
        ["ui.settings.lighttime"]     = "Light-time delay",
        ["ui.settings.corona"]        = "Sun corona",
        ["ui.settings.atmosphere"]    = "Atmosphere",
        ["ui.settings.eclipses"]      = "Eclipses & shadows",
        ["ui.settings.oceanmask"]     = "Ocean specular",
        ["ui.settings.autoexposure"]  = "Auto-exposure",
        ["ui.settings.settings"]      = "Settings panel",
        ["ui.settings.toolbar"]       = "Toolbar",
        ["ui.settings.hud"]           = "FPS / particle HUD",
        ["ui.settings.bookmarks"]     = "Bookmarks sidebar",
        ["ui.settings.hotreload"]     = "GLSL hot-reload",
        ["ui.settings.record"]        = "Record video",
        // Commands.
        ["ui.cmd.speedup"]            = "Speed up",
        ["ui.cmd.speeddown"]          = "Slow down",
        ["ui.cmd.reverse"]            = "Play backward",
        ["ui.cmd.forward"]            = "Play forward",
        ["ui.cmd.focus.sun"]          = "Focus: Sun",
        ["ui.cmd.focus"]              = "Focus: {0}",
        ["ui.cmd.help"]               = "Help overlay",
        ["ui.cmd.language"]           = "Language",
        ["ui.cmd.search"]             = "Search body…",
        ["ui.cmd.palette"]            = "Command palette",
        ["ui.cmd.seek"]               = "Jump to date…",
        ["ui.cmd.screenshot"]         = "Screenshot",
        ["ui.cmd.bookmark.next"]      = "Next bookmark",
        ["ui.cmd.bookmark.prev"]      = "Previous bookmark",
        ["ui.cmd.path.play"]          = "Play camera path",
        ["ui.cmd.path.clear"]         = "Clear camera path",
        ["ui.cmd.waypoint"]           = "Record waypoint {0}",
        ["ui.cmd.waypoint.clear"]     = "Clear waypoint {0}",
        ["ui.cmd.quit"]               = "Quit",
        ["ui.help.mode.0"]            = "full",
        ["ui.help.mode.1"]            = "minimal",
        ["ui.help.mode.2"]            = "hidden",
        // Help overlay: static lines are "key|label"; the rest is generated.
        ["ui.help.mouse"] =
            "LMB drag|orbit camera\n" +
            "MMB drag|pan camera\n" +
            "Wheel|zoom\n" +
            "Click body|show info\n" +
            "Dbl-click|focus body\n" +
            "Dbl empty|unfocus",
        ["ui.help.extra"] =
            "0 / 1–8|focus Sun / planets\n" +
            "Ctrl+1…9|record camera waypoint\n" +
            "Ctrl+Shift+1…9|clear waypoint",
        ["ui.help.esc"]               = "close panel · twice to quit",
        ["ui.quit.confirm"]           = "Press Esc again to quit",
        // Palette / toolbar chrome.
        ["ui.palette.prompt"]         = "Command:",
        ["ui.palette.empty"]          = "No matches",
        ["ui.palette.hint"]           = "Enter — toggle / run · ↑↓ — move · Esc — close",
        ["ui.toolbar.settings"]       = "Settings",
        ["ui.toolbar.commands"]       = "Commands",
        ["ui.toolbar.speed.tip"]      = "Click to reset speed to 1 d/s",
        ["ui.toolbar.direction.tip"]  = "Toggle playback direction",
        // Banners that used to be hard-coded English in the key handler.
        ["ui.scale.banner.real"]      = "Scale: REAL (km-derived radii + log depth)",
        ["ui.scale.banner.compressed"]= "Scale: compressed",
        ["ui.lighttime.on"]           = "Light-time: ON (Sun lighting delayed by r/c)",
        ["ui.lighttime.off"]          = "Light-time: OFF",
        ["ui.meteors.off"]            = "Meteor showers: OFF",
        ["ui.meteors.on"]             = "Meteor showers: ON",
        ["ui.meteors.on.active"]      = "Meteor showers: ON — {0} active",
        ["ui.meteors.on.next"]        = "Meteor showers: ON — next: {0} in {1} day(s)",
        ["ui.campath.playing"]        = "Playing camera path…",
        ["ui.campath.need2"]          = "Need ≥ 2 waypoints — record with Ctrl+1..9",
        ["ui.campath.cleared"]        = "Camera path cleared",
        ["ui.campath.recorded"]       = "Waypoint {0} recorded ({1} total)",
        ["ui.campath.slotcleared"]    = "Waypoint {0} cleared",
        ["ui.unavailable.texture"]    = "texture missing",
        // One-line descriptions (panel footer / palette subtitle).
        ["ui.desc.speed"]             = "Simulation speed in days per real second; negative plays backward.",
        ["ui.desc.speed.up"]          = "Multiply simulation speed by 1.5 (max 1000 d/s).",
        ["ui.desc.speed.down"]        = "Divide simulation speed by 1.5 (min 0.1 d/s).",
        ["ui.desc.realscale"]         = "True km-derived radii and distances with logarithmic depth; planets become tiny dots.",
        ["ui.desc.pause"]             = "Freeze simulation time (particles and camera keep working).",
        ["ui.desc.lighttime"]         = "Delay each planet's lit longitude by r/c so the terminator matches the photons' departure time.",
        ["ui.desc.simmode"]           = "Ephemeris = analytic orbits (exact eclipses); Physics = one N-body integration for everything; Compare = physics plus translucent ephemeris ghosts.",
        ["ui.desc.physics.g"]         = "Multiply the gravitational constant. Bodies keep their current velocity, so a stronger pull bends orbits into tighter ellipses.",
        ["ui.desc.physics.sunmass"]   = "Multiply the Sun's mass (a circular orbit around a 2× Sun has a period √2 shorter).",
        ["ui.desc.physics.exponent"]  = "Exponent n in a = GM/rⁿ; 2 is inverse-square. Anything else makes perihelia precess.",
        ["ui.desc.physics.lightspeed"]= "Multiply the speed of light used by the light-time delay.",
        ["ui.desc.physics.hud"]       = "Card with energy drift since start, integrator steps per frame, collisions and the selected body's osculating elements.",
        ["ui.desc.physics.collisions"]= "Bodies whose surfaces touch merge: the heavier one survives at the centre of mass with the summed momentum, mass and volume; the lighter one disappears and hands its moons over. Off: bodies pass through each other.",
        ["ui.desc.physics.resetconst"]= "Put G, the Sun's mass, the exponent, the speed of light and every body mass back to real values.",
        ["ui.desc.physics.reinit"]    = "Re-seed every body from the ephemeris at the current date (also resets the energy reference).",
        ["ui.desc.physics.resetmasses"]= "Put the Sun's and every body's mass multiplier back to 1.",
        ["ui.desc.mass"]              = "Mass multiplier for this body (logarithmic, 0.01×…100×). Applies immediately without re-seeding.",
        ["ui.desc.meteors"]           = "Meteor streaks near Earth for ±3 days around each annual shower peak.",
        ["ui.desc.orbits"]            = "Orbit lines for planets, dwarfs and comets.",
        ["ui.desc.labels"]            = "Name labels above every body.",
        ["ui.desc.trails"]            = "Fading line behind each planet showing its recent path.",
        ["ui.desc.axes"]              = "Rotation-axis lines showing each planet's tilt.",
        ["ui.desc.dwarfs"]            = "Ceres, Pluto, Haumea, Makemake and Eris.",
        ["ui.desc.probes"]            = "Voyager 1 & 2, JWST and the ISS.",
        ["ui.desc.lagrange"]          = "L1–L5 markers for the Sun–Earth and Sun–Jupiter systems.",
        ["ui.desc.constellations"]    = "Constellation figures drawn on the celestial sphere.",
        ["ui.desc.tidal"]             = "Arrows on tidally locked moons pointing at their host.",
        ["ui.desc.alignment"]         = "Highlight and name groups of ≥3 planets within ~12° of heliocentric longitude.",
        ["ui.desc.focus.sun"]         = "Smoothly move the camera to the Sun.",
        ["ui.desc.solarwind"]         = "Particle stream flowing outward from the Sun.",
        ["ui.desc.solarflares"]       = "Erupting flare sprites on the Sun's surface.",
        ["ui.desc.corona"]            = "Animated granulation and pulse on the Sun's disc.",
        ["ui.desc.aurora"]            = "Polar aurora ribbons on Earth and Jupiter (brighter with solar wind on).",
        ["ui.desc.atmosphere"]        = "Rayleigh/Mie rim scattering on Earth, Mars, Venus, Titan and Neptune.",
        ["ui.desc.eclipses"]          = "Soft shadows cast by moons and planets onto other bodies.",
        ["ui.desc.pbr"]               = "Cook-Torrance GGX shading with per-body roughness (off = classic Phong).",
        ["ui.desc.oceanmask"]         = "Specular glint only on Earth's oceans, using the specular map texture.",
        ["ui.desc.bloom"]             = "HDR bright-pass + Gaussian glow around the Sun, flares and aurora.",
        ["ui.desc.autoexposure"]      = "Adapt exposure to average scene brightness before ACES tone mapping.",
        ["ui.desc.fxaa"]              = "Fast approximate anti-aliasing on the final image.",
        ["ui.desc.lensflare"]         = "Screen-space lens ghosts when the Sun is near the view centre.",
        ["ui.desc.settings"]          = "This panel.",
        ["ui.desc.toolbar"]           = "Bottom strip with pause, speed and the core view toggles.",
        ["ui.desc.hud"]               = "FPS, scale mode and live particle counts (top-right).",
        ["ui.desc.timeline"]          = "Draggable ±100-year timeline at the bottom of the screen.",
        ["ui.desc.bookmarks"]         = "Sidebar listing eclipses, transits and other dated events.",
        ["ui.desc.audio"]             = "Short click / whoosh cues on jumps and focus changes.",
        ["ui.desc.fullscreen"]        = "Borderless fullscreen on the current monitor.",
        ["ui.desc.help"]              = "Cycle the top-left cheat sheet: full → minimal → hidden.",
        ["ui.desc.language"]          = "Cycle through the UI languages found in data/lang.*.json.",
        ["ui.desc.search"]            = "Type a body name and press Enter to focus it.",
        ["ui.desc.palette"]           = "Search every setting and command by name.",
        ["ui.desc.seek"]              = "Jump to YYYY-MM-DD or offset by ±N days.",
        ["ui.desc.screenshot"]        = "Save a PNG of the current frame to screenshots/.",
        ["ui.desc.bookmark.next"]     = "Jump forward to the next eclipse / transit bookmark.",
        ["ui.desc.bookmark.prev"]     = "Jump back to the previous eclipse / transit bookmark.",
        ["ui.desc.path.play"]         = "Fly the camera through the recorded waypoints (6 s Catmull-Rom).",
        ["ui.desc.path.clear"]        = "Forget all nine camera waypoints.",
        ["ui.desc.quit"]              = "Close the application (state is saved).",
        ["ui.desc.profiler"]          = "Per-pass GPU and CPU frame times (bottom-right).",
        ["ui.desc.hotreload"]         = "Watch Resources/Shaders and recompile edited GLSL live.",
        ["ui.desc.gpubelt"]           = "Solve the asteroid belt's Kepler equation in a compute shader instead of on the CPU.",
        ["ui.desc.record"]            = "Dump every frame to recordings/ and encode an MP4 with ffmpeg on stop.",
    };

    private static Dictionary<string, string> _active = _en;
    /// <summary>Every key of the embedded English table (tests check that each
    /// shipped translation file covers all of them).</summary>
    internal static IReadOnlyCollection<string> EnglishKeys => _en.Keys;
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
