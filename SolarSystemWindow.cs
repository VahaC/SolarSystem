using System.Diagnostics;
using System.IO;
using System.Text.Json;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace SolarSystem;

public sealed class SolarSystemWindow : GameWindow
{
    /// <summary>A7: when set before <see cref="GameWindow.Run"/>, switches the
    /// app into a deterministic offline renderer that overrides simulation
    /// time, writes one PNG per frame to <see cref="HeadlessRenderJob.OutDir"/>
    /// and closes when finished.</summary>
    public HeadlessRenderJob? Headless { get; init; }

    // A7 (interactive): F9 toggles in-app frame recording. Each rendered frame
    // is dumped to recordings/<timestamp>/frame_NNNNN.png; on stop, ffmpeg is
    // invoked when available on PATH (or via the SOLARSYSTEM_FFMPEG env var).
    private bool _recording;
    private string _recordDir = "";
    private int _recordFrameIndex;
    private double _recordStartedAt;
    private const int RecordTargetFps = 60;
    // A7: PNG encoding + disk I/O are dispatched to background tasks so the
    // render thread only does the GL readback + row-flip memcpy. A bounded
    // semaphore caps in-flight encodes so very long recordings don't OOM.
    private readonly List<Task> _recordEncodeTasks = new();
    private readonly System.Threading.SemaphoreSlim _recordEncodeGate =
        new(Math.Max(2, Environment.ProcessorCount - 1));

    /// <summary>Effective Sun radius in world units. Tracks the global scale mode:
    /// in compressed mode the Sun is artificially huge (5 units) so it dominates the
    /// scene; in real-scale mode it's derived from its real radius in kilometres.</summary>
    public static float SunRadius
        => OrbitalMechanics.RealScale
            ? (float)(695700.0 * OrbitalMechanics.KmToWorldRealScale)
            : 5f;

    // Moon orbit parameters (visual, NOT to scale — the real ratio Moon/Earth-orbit is
    // ~0.00257 AU which would be invisible. We inflate it just enough to clearly show
    // the Moon orbiting Earth while staying safely inside the Earth–Venus gap (~6 units
    // in world space after the a^0.45 distance compression in OrbitalMechanics).
    private const float MoonOrbitRadius = 2.5f;        // world units from Earth's center
    private const float MoonOrbitInclinationDeg = 5.145f;
    private const double MoonOrbitalPeriodDays = 27.321661;

    private readonly Renderer _renderer = new();
    private readonly Camera _camera = new();
    private readonly SolarWind _solarWind = new();
    private readonly SolarFlares _solarFlares = new();
    private readonly AsteroidBelt _belt = new();
    // S16: catalogue-driven set of comets (Halley, Hale-Bopp, NEOWISE, Encke, ...).
    private readonly Comets _comets = new();
    private readonly Constellations _constellations = new();
    // S13 / S14.
    private readonly TidalLock _tidalLock = new();
    private readonly PlanetaryAlignment _alignment = new();
    // Physics sandbox: one N-body world for every massive body (replaces the S15
    // majors-only integrator). Built in OnLoad from the same Planet / Moon records
    // the renderer uses; seeded from the ephemerides when the mode leaves Ephemeris.
    private readonly PhysicsConstants _physConst = new();
    private PhysicsWorld _world = null!;
    private SimulationMode _simMode = SimulationMode.Ephemeris;
    /// <summary>Pending seek target (days since J2000) while a jump is being integrated
    /// in per-frame chunks; null when the physics clock is caught up.</summary>
    private double? _physicsTarget;
    private bool _showPhysicsHud;
    // Collisions (physics modes): mirrored into PhysicsWorld.CollisionsEnabled; how many
    // world events have already been turned into banners / flashes; running impact
    // flashes (unified body index of the survivor, wall-clock start); world -> unified
    // index map so a collision can retarget focus / selection.
    private bool _collisionsEnabled = true;
    private int _announcedCollisions;
    private readonly List<(int body, double start)> _impactFlashes = new();
    private const double ImpactFlashSeconds = 3.0;
    private readonly Dictionary<int, int> _worldToUnified = new();
    private int[] _worldExtraIdx = [];
    private Planet[] _alignmentPlanets = [];
    /// <summary>Per-frame CPU budget for the integrator so a 100-year seek is spread
    /// over many frames instead of freezing the UI (headless renders are unbounded).</summary>
    private const int PhysicsMaxStepsPerFrame = 2000;
    private const double PhysicsFrameBudgetMs = 8.0;
    // World indices of the render bodies (-1 = not integrated).
    private int[] _worldPlanetIdx = [];
    private int _worldMoonIdx = -1;
    private int[] _worldMoonsIdx = [];
    private int[] _worldCometIdx = [];
    // Compare mode: ephemeris ("ghost") world positions refreshed every frame.
    private Vector3[] _ghostPlanets = [];
    private Vector3 _ghostMoon;
    private Vector3[] _ghostMoons = [];
    private Vector3[] _ghostComets = [];
    // S9–S12.
    private readonly Probes _probes = new();
    private readonly LagrangePoints _lagrange = new();
    private readonly MeteorShowers _meteors = new();
    private readonly Bookmarks _bookmarks = new();
    // V13.
    private readonly Aurora _aurora = new();
    // Q9 / Q10 / Q12 / Q15.
    private readonly TimelineScrubber _scrubber = new();
    private readonly CameraPath _camPath = new();
    private readonly SettingsPanel _settings = new();
    private readonly AudioService _audio = new();
    private readonly BookmarksSidebar _bookSidebar = new();
    // A12: per-frame profiler (GPU time-elapsed queries + CPU stopwatch per pass).
    private readonly FrameProfiler _profiler = new();
    // Feature registry (single source of truth for toggles / commands / keys),
    // the Ctrl+K palette and the bottom toolbar derived from it.
    private readonly FeatureRegistry _registry = new();
    private readonly CommandPalette _palette = new();
    private readonly Toolbar _toolbar = new();
    /// <summary>Esc on an empty screen arms a 2 s window; a second Esc inside it quits.</summary>
    private double _escArmedUntil = -1.0;
    private bool _showProfiler;
    private BitmapFont _font = null!;
    /// <summary>Indices 0..7 are the major planets (Mercury..Neptune); indices 8+
    /// are the IAU dwarf planets appended by <see cref="Planet.CreateDwarfPlanets"/>.
    /// They share the same orbit / trail / picking pipeline; only the digit-key
    /// focus shortcuts are restricted to the first eight.</summary>
    private Planet[] _planets = null!;
    /// <summary>Index in <see cref="_planets"/> where the dwarf-planet block starts.
    /// Equals the count returned by <see cref="Planet.CreateAll"/> at load time.</summary>
    private int _dwarfStart;
    /// <summary>Slice of <see cref="_planets"/> excluding dwarfs; cached so the
    /// per-frame draw / pick paths don't reallocate when dwarfs are hidden.</summary>
    private Planet[] _majorPlanets = null!;
    private Planet _moon = null!;
    /// <summary>Major satellites (Galileans + Titan) parented to their host planet.</summary>
    private Moon[] _moons = null!;
    private float[] _inflatedMoonsRadii = null!;

    // Date-seek (S5): when active, keystrokes are routed into the prompt buffer
    // instead of the normal sim controls. Submit with Enter, cancel with Escape.
    private bool _seekActive;
    private string _seekBuffer = "";
    private string _seekFeedback = "";
    private double _seekFeedbackUntil;
    /// <summary>The OS fires OnTextInput right after OnKeyDown for the same physical
    /// keystroke that opened the prompt. We swallow exactly one text-input event so the
    /// triggering 'J' (or 'j') doesn't end up as the first character of the buffer.</summary>
    private bool _seekSwallowNextChar;
    /// <summary>Snapshot of each planet's inflated VisualRadius captured at load.
    /// In real-scale mode VisualRadius is replaced with the real km-derived value;
    /// switching back restores these originals.</summary>
    private float[] _inflatedPlanetRadii = null!;
    private float _inflatedMoonRadius;

    // Q2: flat list of pickable / focusable non-planet bodies (Moon, major moons, comet).
    // Indices in <see cref="_selectedIndex"/> / <see cref="_focusIndex"/> >= _planets.Length
    // refer into this array, offset by _planets.Length.
    private Planet[] _extraBodies = [];

    // Q3: name search prompt state. Mirrors the date-seek prompt's lifecycle.
    private bool _searchActive;
    private string _searchBuffer = "";
    private bool _searchSwallowNextChar;

    // Q6: cursor position (window-local) and hover-pick result, refreshed on mouse move.
    private Vector2 _mousePos;
    private int _hoverIndex = -2;

    // Q7: HUD overlay (FPS / particle counts) toggled with the backtick key.
    private bool _showHud;
    private double _fpsAccum;
    private int _fpsFrames;
    private double _fpsValue;

    // Q14: help-overlay mode. 0 = full, 1 = minimal (date + speed only), 2 = hidden.
    private int _helpMode;

    // Q5: persisted UI state file.
    private static string StateFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SolarSystem", "state.json");

    private double _simDays;          // days since J2000
    private double _daysPerSecond = 1.0;
    private bool _paused;             // freezes sim time without resetting _daysPerSecond
    private bool _showOrbits = true;
    private bool _showAxes;
    private bool _showLabels = true;
    private bool _showTrails = true;
    private bool _showDwarfs = true;
    private bool _showConstellations;
    // S9–S11 visibility toggles.
    private bool _showProbes = true;
    private bool _showLagrange;
    private bool _showMeteors = true;
    /// <summary>V13: master toggle for the polar aurora ribbons (Earth + Jupiter).</summary>
    private bool _showAurora = true;
    /// <summary>S13: tidal-lock arrows on the Moon, Galileans and Titan.</summary>
    private bool _showTidalLock;
    /// <summary>S14: heliocentric-alignment indicator (line + banner when ≥3 planets are within ~12°).</summary>
    private bool _showAlignment = true;
    /// <summary>R4: when true each planet's spin angle is evaluated at
    /// <c>simDays - r/c</c> (where r is its heliocentric distance), so the day/night
    /// terminator falls where it was when the photons currently illuminating it
    /// left the Sun. Distant planets show the largest visible delay (Neptune
    /// ~4 light-hours ≈ 90° of rotation).</summary>
    private bool _lightTime;
    /// <summary>Borderless fullscreen toggle (Alt+Enter). Persisted in
    /// <see cref="PersistedState.Fullscreen"/> and surfaced as a row in the F1
    /// settings panel so it can be enabled/disabled without the keyboard
    /// shortcut.</summary>
    private bool _fullscreen;
    /// <summary>Speed of light in AU/day = c[km/s] * 86400 / km_per_AU
    /// = 299792.458 * 86400 / 1.495978707e8 ≈ 173.1446. Inverted so we can multiply.</summary>
    private const double LightDaysPerAU = 1.0 / 173.1446326742403;
    private int _focusIndex = -1;     // -1 = sun, 0..7 = planet
    private int _selectedIndex = -2;  // -2 = none, -1 = sun, 0..7 = planet

    // Smooth focus transition state. When active the camera's Target / Distance are
    // lerped from the captured start values toward the current focus over ~0.5s.
    private bool _focusTransitioning;
    private double _focusTransitionElapsed;
    private const double FocusTransitionSeconds = 0.5;
    private Vector3 _focusStartTarget;
    private float _focusStartDistance;
    private float _focusEndDistance;

    // Double-click detection (LMB).
    private double _lastClickTime = -10.0;
    private Vector2 _lastClickPos;
    private const double DoubleClickSeconds = 0.35;
    private const float DoubleClickMaxPx = 6f;

    public SolarSystemWindow(GameWindowSettings g, NativeWindowSettings n) : base(g, n) { }

    protected override void OnLoad()
    {
        base.OnLoad();
        _renderer.FramebufferSize = new Vector2i(ClientSize.X, ClientSize.Y);
        _renderer.Initialize();
        _solarWind.Initialize();
        _solarFlares.Initialize();
        _belt.Initialize();
        _comets.Initialize();
        _constellations.Initialize();
        _probes.Initialize();
        _lagrange.Initialize();
        _meteors.Initialize();
        _aurora.Initialize();
        _tidalLock.Initialize();
        _alignment.Initialize();
        _profiler.Initialize();
        // Q13: discover available languages and apply current OS culture if a
        // matching translation file ships next to the binary.
        Localization.DiscoverAvailable();
        var sysLang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (Localization.Available.Contains(sysLang, StringComparer.OrdinalIgnoreCase))
            Localization.SetLanguage(sysLang);

        // Q10: restore previously recorded camera-path waypoints.
        _camPath.TryLoadFromDisk();

        // A4: instanced quad particles size their billboards in clip space using
        // the current viewport, so push it once now and again on every resize.
        var initVp = new Vector2(ClientSize.X, ClientSize.Y);
        _solarWind.SetViewport(initVp);
        _solarFlares.SetViewport(initVp);
        _comets.SetViewport(initVp);
        _belt.SetViewport(initVp);
        _meteors.SetViewport(initVp);
        _font = new BitmapFont();

        _planets = Planet.CreateAll();
        _dwarfStart = _planets.Length;
        // S7: append the IAU dwarf planets so they automatically get orbit lines,
        // trails, picking and info-panel coverage with no further wiring.
        _planets = [.. _planets, .. Planet.CreateDwarfPlanets()];
        _majorPlanets = _planets[.._dwarfStart];
        Debug.WriteLine("---- Loading planet textures ----");
        foreach (var p in _planets)
        {
            byte r = (byte)(p.ProceduralColor.X * 255);
            byte g = (byte)(p.ProceduralColor.Y * 255);
            byte b = (byte)(p.ProceduralColor.Z * 255);
            p.TextureId = TextureManager.LoadOrProcedural(p.TextureFile, r, g, b, out p.TextureFromFile);
        }

        _renderer.BuildOrbits(_planets);

        // V3 + V4: load Earth's cloud layer and night-side city-lights textures if
        // present in textures/. Both are optional — if a file is missing the planet
        // simply renders without the corresponding effect.
        foreach (var p in _planets)
        {
            if (p.Name == "Earth")
            {
                if (TextureManager.TryLoadFile("8k_earth_clouds.jpg", out int clouds))
                    p.CloudTextureId = clouds;
                if (TextureManager.TryLoadFile("8k_earth_nightmap.jpg", out int night))
                    p.NightTextureId = night;
                // V15: ocean / specular mask. Either filename works.
                if (TextureManager.TryLoadFile("8k_earth_specular_map.png", out int spec)
                    || TextureManager.TryLoadFile("8k_earth_specular_map.jpg", out spec)
                    || TextureManager.TryLoadFile("8k_earth_specular_map.tif", out spec))
                    p.OceanMaskTextureId = spec;
                break;
            }
        }

        // The Moon: a small companion that orbits Earth, not the Sun. It reuses the planet
        // shader/sphere mesh via Renderer.DrawPlanet, but its Position is computed each frame
        // as Earth.Position + (rotating offset) instead of from heliocentric Kepler elements.
        _moon = new Planet
        {
            Name = "Moon",
            VisualRadius = 0.4f,
            RealRadiusKm = 1737.4,
            ProceduralColor = new Vector3(0.78f, 0.78f, 0.75f),
            TextureFile = "8k_moon.jpg",
            AxisTiltDeg = 6.68f,
            // Tidally locked: sidereal rotation period equals its orbital period.
            RotationPeriodHours = MoonOrbitalPeriodDays * 24.0,
            OrbitalPeriodYears = MoonOrbitalPeriodDays / 365.25,
            SemiMajorAxisAU = 0.00257, // ~384,400 km, used only for the info panel
        };
        _moon.TextureId = TextureManager.LoadOrProcedural(
            _moon.TextureFile,
            (byte)(_moon.ProceduralColor.X * 255),
            (byte)(_moon.ProceduralColor.Y * 255),
            (byte)(_moon.ProceduralColor.Z * 255),
            out _moon.TextureFromFile);

        // S6: Galilean moons of Jupiter (index 4) and Saturn's Titan (index 5).
        // Visual ("artistic") orbit radii are inflated so the moons sit clearly
        // outside their host's silhouette in compressed mode; real-scale mode
        // uses the published km values via OrbitalMechanics.KmToWorldRealScale.
        _moons =
        [
            CreateMoon("Io",       hostIndex: 4, realRadiusKm: 1821.6, color: new Vector3(0.95f, 0.85f, 0.45f),
                       texture: "8k_io.jpg",       visualRadius: 0.40f, axisTiltDeg: 0.0f,  rotationHours: 1.769138 * 24.0,
                       orbitKm: 421800.0,    artistic: 5.5f,  periodDays: 1.769138,  inclDeg: 0.05f, phaseDeg: 0),
            CreateMoon("Europa",   hostIndex: 4, realRadiusKm: 1560.8, color: new Vector3(0.85f, 0.78f, 0.62f),
                       texture: "8k_europa.jpg",   visualRadius: 0.38f, axisTiltDeg: 0.1f,  rotationHours: 3.551181 * 24.0,
                       orbitKm: 671100.0,    artistic: 7.0f,  periodDays: 3.551181,  inclDeg: 0.47f, phaseDeg: 90),
            CreateMoon("Ganymede", hostIndex: 4, realRadiusKm: 2634.1, color: new Vector3(0.70f, 0.65f, 0.58f),
                       texture: "8k_ganymede.jpg", visualRadius: 0.50f, axisTiltDeg: 0.33f, rotationHours: 7.154553 * 24.0,
                       orbitKm: 1070400.0,   artistic: 9.0f,  periodDays: 7.154553,  inclDeg: 0.20f, phaseDeg: 180),
            CreateMoon("Callisto", hostIndex: 4, realRadiusKm: 2410.3, color: new Vector3(0.50f, 0.45f, 0.40f),
                       texture: "8k_callisto.jpg", visualRadius: 0.48f, axisTiltDeg: 0.0f,  rotationHours: 16.689017 * 24.0,
                       orbitKm: 1882700.0,   artistic: 12.0f, periodDays: 16.689017, inclDeg: 0.20f, phaseDeg: 270),
            CreateMoon("Titan",    hostIndex: 5, realRadiusKm: 2574.7, color: new Vector3(0.80f, 0.62f, 0.30f),
                       texture: "8k_titan.jpg",    visualRadius: 0.48f, axisTiltDeg: 0.0f,  rotationHours: 15.945421 * 24.0,
                       orbitKm: 1221870.0,   artistic: 9.0f,  periodDays: 15.945421, inclDeg: 0.34875f, phaseDeg: 0),
        ];
        _inflatedMoonsRadii = new float[_moons.Length];
        for (int i = 0; i < _moons.Length; i++) _inflatedMoonsRadii[i] = _moons[i].Body.VisualRadius;

        // Snapshot the artistic radii so the R-key toggle can restore them.
        _inflatedPlanetRadii = new float[_planets.Length];
        for (int i = 0; i < _planets.Length; i++) _inflatedPlanetRadii[i] = _planets[i].VisualRadius;
        _inflatedMoonRadius = _moon.VisualRadius;

        // Q2: flat array of non-planet bodies that share the planet pick / focus pipeline.
        // Order matters — index = _planets.Length + position-in-this-array.
        var extras = new List<Planet> { _moon };
        foreach (var m in _moons) extras.Add(m.Body);
        foreach (var b in _comets.Bodies) extras.Add(b);
        _extraBodies = [.. extras];

        _camera.Aspect = ClientSize.X / (float)ClientSize.Y;
        _camera.ResetDefault();

        // Physics sandbox: build the N-body world over the same render bodies. It is
        // only seeded (Initialize) when the mode leaves Ephemeris, so this is cheap.
        BuildPhysicsWorld();

        // Feature registry first: the settings panel, the toolbar, the key map,
        // the palette and the persisted-state loader are all derived from it.
        BuildFeatureRegistry();
        _registry.TryLoadBindingsFile(msg => Debug.WriteLine("[keys] " + msg));
        BuildSettingsPanel();
        BuildToolbar();

        // Q5: restore persisted UI state.
        // initialised camera / toggles / scale mode.
        TryLoadPersistedState();

        // A7: headless renderer — pin sim time, force optional real-scale,
        // skip per-frame mouse / scrubber UI. Persisted state is intentionally
        // loaded first so the user's last toggle set defines the look of the
        // export; CLI overrides take precedence below.
        if (Headless != null)
        {
            if (Headless.RealScale != OrbitalMechanics.RealScale) ToggleRealScale();
            _paused = true;          // suppress the args.Time-based _simDays advance
            _simDays = Headless.FromSimDays;
            _physicsTarget = null;
            // --physics forces Physics mode (otherwise the persisted mode applies).
            // Either way a physics world is (re)seeded at the first frame — the
            // persisted state may already have seeded it at the saved date — and then
            // integrates exactly DaysPerFrame per frame with no CPU budget, so the
            // export is deterministic.
            if (Headless.Physics) SetSimulationMode(SimulationMode.Physics);
            if (_simMode != SimulationMode.Ephemeris)
            {
                SeedPhysics();
                ClearAllTrails();
            }
            try { Directory.CreateDirectory(Headless.OutDir); } catch { /* surfaces below on save */ }
            Console.WriteLine($"[render] {Headless.TotalFrames} frames @ dt={Headless.DaysPerFrame} d/frame -> {Headless.OutDir}");
        }
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        _renderer.FramebufferSize = new Vector2i(e.Width, e.Height);
        _camera.Aspect = e.Width / (float)Math.Max(1, e.Height);

        var vp = new Vector2(e.Width, e.Height);
        _solarWind.SetViewport(vp);
        _solarFlares.SetViewport(vp);
        _comets.SetViewport(vp);
        _belt.SetViewport(vp);
        _meteors.SetViewport(vp);
    }

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);

        // A7: in headless mode, simulation time is a pure function of frame
        // index — bypass the args.Time-based advance entirely so the export
        // is deterministic regardless of how fast the offscreen loop runs.
        if (Headless != null)
        {
            _simDays = Headless.FromSimDays + Headless.FrameIndex * Headless.DaysPerFrame;
        }

        // A6: drain any GLSL files that changed on disk and rebuild the affected
        // programs on the GL thread. No-op when hot-reload is off.
        if (ShaderSources.HotReloadEnabled)
        {
            int n = ShaderSources.PollPendingReloads(
                onSwap: name =>
                {
                    _seekFeedback = Localization.T("ui.hotreload.swap", name);
                    _seekFeedbackUntil = GLFW.GetTime() + 2.5;
                },
                onError: (name, msg) =>
                {
                    _seekFeedback = Localization.T("ui.hotreload.error", name, msg);
                    _seekFeedbackUntil = GLFW.GetTime() + 5.0;
                });
            if (n > 0) _audio.PlayTick();
        }

        // Q9: keep the scrubber's bar rectangle in sync with the live viewport
        // before any mouse-down hit-tests it.
        _scrubber.Layout(_renderer.FramebufferSize.X, _renderer.FramebufferSize.Y);

        // Q9: scrubber drag overrides normal sim-time advance.
        if (_scrubber.IsDragging)
        {
            var newDays = _scrubber.UpdateDrag(_mousePos);
            if (newDays.HasValue)
            {
                if (Math.Abs(newDays.Value - TargetSimDays) > 0.5)
                {
                    SetSimTime(newDays.Value);
                    ClearAllTrails();
                }
            }
        }
        else if (!_paused && _physicsTarget == null)
            _simDays += _daysPerSecond * args.Time;

        // Physics sandbox: integrate the world up to the requested time (or toward a
        // pending seek target, in per-frame chunks) and let the physics clock define
        // the frame's sim time. In Ephemeris mode nothing here runs.
        if (_simMode != SimulationMode.Ephemeris) AdvancePhysics();

        // Q10: camera-path playback drives Yaw/Pitch/Distance/Target directly,
        // so the per-frame "follow focused body" branch is suppressed below.
        _camPath.Update(args.Time, _camera);

        // Update positions and axial rotation
        const double TwoPi = Math.PI * 2.0;

        // Physics / Compare: every planet and dwarf takes its heliocentric position
        // from the N-body world; Ephemeris keeps the analytic Kepler solve.
        bool physicsDriven = _simMode != SimulationMode.Ephemeris;
        double lightDaysPerAU = physicsDriven ? _world.LightDaysPerAU : LightDaysPerAU;

        for (int pi = 0; pi < _planets.Length; pi++)
        {
            var p = _planets[pi];
            if (physicsDriven && _worldPlanetIdx[pi] >= 0)
                p.HelioAU = _world.HeliocentricPosition(_worldPlanetIdx[pi]);
            else
                p.HelioAU = OrbitalMechanics.HeliocentricPosition(p, _simDays);
            float s = OrbitalMechanics.OrbitWorldScale(p.SemiMajorAxisAU);
            p.Position = new Vector3(
                (float)(p.HelioAU.X * s),
                (float)(p.HelioAU.Y * s),
                (float)(p.HelioAU.Z * s));
            // R4: rotation is evaluated at (simDays - r/c) when light-time is on,
            // so the surface lit longitude matches when the photons currently
            // hitting it actually left the Sun.
            double rotDays = _simDays;
            if (_lightTime)
            {
                double rAU = Math.Sqrt(p.HelioAU.X * p.HelioAU.X
                                       + p.HelioAU.Y * p.HelioAU.Y
                                       + p.HelioAU.Z * p.HelioAU.Z);
                rotDays -= rAU * lightDaysPerAU;
            }
            if (p.RotationPeriodHours != 0.0)
            {
                double angle = (rotDays * 24.0 / p.RotationPeriodHours) * TwoPi;
                angle %= TwoPi;
                if (angle < 0) angle += TwoPi;
                p.RotationAngleRad = (float)angle;
            }
            // V3: cloud layer drifts slightly slower than the surface so it
            // counter-rotates relative to the ground.
            if (p.CloudTextureId != 0 && p.RotationPeriodHours != 0.0)
            {
                double cloudAngle = (rotDays * 24.0 / p.RotationPeriodHours - rotDays * 0.08) * TwoPi;
                cloudAngle %= TwoPi;
                if (cloudAngle < 0) cloudAngle += TwoPi;
                p.CloudRotationAngleRad = (float)cloudAngle;
            }
        }

        // Moon orbits Earth using the truncated Brown / ELP-2000 lunar theory
        // (Meeus Ch.47, see LunarEphemeris.cs). The geocentric ecliptic vector
        // (λ, β, Δ) we get back is calibrated against real ephemerides, so eclipse
        // bookmarks now produce the actual alignment they advertise (± a few minutes).
        // In compressed scale we keep the artistic radius MoonOrbitRadius so the
        // Moon stays comfortably visible inside the Earth–Venus gap; the angular
        // alignment that drives eclipses is preserved by normalising before scaling.
        {
            var earth = _planets[2];
            if (physicsDriven && _worldMoonIdx >= 0)
            {
                // Physics: the integrated geocentric offset, drawn to scale in real-scale
                // mode and radially stretched around the artistic radius otherwise, so a
                // Moon spiralling in or out under altered constants is visible.
                var offAU = _world.AbsolutePosition(_worldMoonIdx) - _world.AbsolutePosition(_worldPlanetIdx[2]);
                _moon.Position = earth.Position + PhysicsSatelliteOffsetWorld(offAU, MoonOrbitRadius, PhysicsWorld.MoonOrbitRadiusKm);
                _moon.HelioAU = _world.HeliocentricPosition(_worldMoonIdx);
            }
            else
            {
                _moon.Position = earth.Position + EphemerisMoonOffsetWorld(_simDays);
                _moon.HelioAU = earth.HelioAU; // info-panel "distance from Sun" approximation
            }
            double mAngle = (_simDays * 24.0 / _moon.RotationPeriodHours) * TwoPi;
            mAngle %= TwoPi;
            if (mAngle < 0) mAngle += TwoPi;
            _moon.RotationAngleRad = (float)mAngle;
        }

        // S6: Galilean moons + Titan. Io / Europa / Ganymede / Callisto get their
        // planetocentric mean longitudes u₁..u₄ from Meeus' low-precision Galilean
        // theory (Ch.44, see GalileanEphemeris.cs), so Jupiter transits and shadow
        // casts on the cloud tops happen at the historically correct dates.
        // Titan stays on its simple circular orbit — no eclipse bookmark depends on
        // it, and Saturn is too far away for naked-eye occultation effects to
        // matter at this scale.
        var galilean = GalileanEphemeris.MeanLongitudes(_simDays);
        for (int mi = 0; mi < _moons.Length; mi++)
        {
            var m = _moons[mi];
            var host = _planets[m.HostPlanetIndex];
            if (physicsDriven && _worldMoonsIdx[mi] >= 0)
            {
                var offAU = _world.AbsolutePosition(_worldMoonsIdx[mi]) - _world.AbsolutePosition(_worldPlanetIdx[m.HostPlanetIndex]);
                m.Body.Position = host.Position + PhysicsSatelliteOffsetWorld(offAU, m.ArtisticOrbitRadius, m.RealOrbitRadiusKm);
                m.Body.HelioAU = _world.HeliocentricPosition(_worldMoonsIdx[mi]);
            }
            else
            {
                m.Body.Position = host.Position + EphemerisSatelliteOffsetWorld(m, galilean, _simDays);
                m.Body.HelioAU = host.HelioAU;
            }
            if (m.Body.RotationPeriodHours != 0.0)
            {
                double a = (_simDays * 24.0 / m.Body.RotationPeriodHours) * TwoPi;
                a %= TwoPi;
                if (a < 0) a += TwoPi;
                m.Body.RotationAngleRad = (float)a;
            }
        }

        // Trails: append the current world position to each planet's ring buffer once
        // it has moved more than a planet-relative threshold. Skip while paused so the
        // trail doesn't degenerate into a single multi-stamped point.
        if (!_paused && _showTrails)
        {
            for (int pi = 0; pi < _planets.Length; pi++)
            {
                if (!WorldAlive(_worldPlanetIdx[pi])) continue;
                // A small absolute spacing keeps trails visible at slow sim speeds while
                // the ring buffer still drops old samples once full at high speeds.
                float spacing = OrbitalMechanics.RealScale ? 0.0005f : 0.01f;
                _planets[pi].TrailPush(_planets[pi].Position, spacing);
            }
        }

        // Smooth focus transition: lerp Target + Distance with a smoothstep ease.
        // Tracking a moving planet during the lerp uses the body's CURRENT position as
        // the dynamic end target so the camera arrives smoothly even as it orbits.
        if (_focusTransitioning)
        {
            _focusTransitionElapsed += args.Time;
            float t = (float)Math.Clamp(_focusTransitionElapsed / FocusTransitionSeconds, 0.0, 1.0);
            float s = t * t * (3f - 2f * t); // smoothstep
            Vector3 end = GetBody(_focusIndex)?.Position ?? Vector3.Zero;
            _camera.Target = Vector3.Lerp(_focusStartTarget, end, s);
            _camera.Distance = MathHelper.Lerp(_focusStartDistance, _focusEndDistance, s);
            if (t >= 1f) _focusTransitioning = false;
        }
        else if (!_camPath.IsPlaying)
        {
            var followed = GetBody(_focusIndex);
            if (followed != null) _camera.Target = followed.Position;
        }

        // Pause must also freeze the Sun's particle effects, otherwise the wind keeps
        // streaming and flares keep erupting while the planets are perfectly still.
        // Feed dt=0 (instead of skipping the call) so any internal state stays valid.
        float fxDt = _paused ? 0f : (float)args.Time;
        // A7: headless mode pins _paused = true so the analytic sim is deterministic,
        // but the Sun's particle systems would freeze. Feed them the per-frame fixed
        // dt so each rendered frame still has a full set of in-flight wind/flare sprites.
        if (Headless != null) fxDt = 1f / Math.Max(1, Headless.Fps);
        _solarWind.Update(fxDt, Vector3.Zero, SunRadius);
        _solarFlares.Update(fxDt, Vector3.Zero, SunRadius);

        // Asteroid belt + comet position track sim time even when paused (positions are
        // a pure function of _simDays, not an integration), so they stay correctly
        // placed after a date jump or while the simulation is frozen. In the physics
        // modes both are test particles riding the field the world recorded this frame.
        if (physicsDriven)
        {
            _belt.UpdatePhysics(_world);
            for (int ci = 0; ci < _comets.All.Length; ci++)
            {
                if (_worldCometIdx[ci] >= 0)
                    _comets.All[ci].ApplyHelioAU(_world.HeliocentricPosition(_worldCometIdx[ci]), _simDays);
                else
                    _comets.All[ci].UpdatePosition(_simDays);
            }
            if (_simMode == SimulationMode.Compare) ComputeGhostPositions(galilean);
        }
        else
        {
            _belt.Update(_simDays);
            _comets.UpdatePosition(_simDays);
        }
        _comets.UpdateTail(fxDt, Vector3.Zero);

        // S14: recompute alignment groups from the freshly-updated positions.
        _alignment.Enabled = _showAlignment;
        _alignmentPlanets = AliveSubset(_majorPlanets);
        _alignment.Update(_alignmentPlanets);
        _tidalLock.Enabled = _showTidalLock;

        // S9–S11: probes / Lagrange points / meteor showers. All read the planets
        // table that was just updated above, so they're spatially in sync.
        _probes.Update(_simDays, _planets, Vector3.Zero);
        _lagrange.Update(_planets, Vector3.Zero);
        _meteors.Enabled = _showMeteors;
        _meteors.Update(fxDt, _simDays, _planets);

        // Clear stale seek-feedback message after a few seconds.
        if (_seekFeedback.Length > 0 && GLFW.GetTime() > _seekFeedbackUntil)
            _seekFeedback = "";

        // Q6: refresh hover-pick once per frame; tooltip rendering reads _hoverIndex.
        // Skipped while a modal prompt is open so it doesn't fight with the prompt UI.
        if (!_seekActive && !_searchActive && !_palette.Active)
            _hoverIndex = TryPick(_mousePos);
        else
            _hoverIndex = -2;

        // Q7: smoothed FPS counter (1 Hz update so the digits don't jitter).
        _fpsAccum += args.Time;
        _fpsFrames++;
        if (_fpsAccum >= 0.5)
        {
            _fpsValue = _fpsFrames / _fpsAccum;
            _fpsAccum = 0;
            _fpsFrames = 0;
        }

        // Title with current sim date
        var date = OrbitalMechanics.J2000.AddDays(_simDays);
        string speedStr = _paused
            ? "PAUSED"
            : $"{(_daysPerSecond < 0 ? "◀ " : "")}x{Math.Abs(_daysPerSecond):0.##} days/s";
        Title = $"Solar System  |  {date:yyyy-MM-dd}  |  speed {speedStr}";
    }

    /// <summary>Show a transient banner at the top-centre of the screen.</summary>
    private void ShowBanner(string text) => ShowBanner(text, 2.0);

    private void ShowBanner(string text, double seconds)
    {
        _seekFeedback = text;
        _seekFeedbackUntil = GLFW.GetTime() + seconds;
    }

    /// <summary>One-shot keyboard handling. More reliable than polling KeyboardState.IsKeyPressed
    /// every frame: the GLFW key event fires exactly once per physical press, with no risk of
    /// missing the press window between two update ticks.
    ///
    /// Only the modal prompts (date seek, search, palette) and <c>Esc</c> are
    /// handled here; every other chord is resolved through the
    /// <see cref="FeatureRegistry"/> so the key map lives in exactly one place
    /// (and can be overridden from <c>data/keybindings.json</c>).</summary>
    protected override void OnKeyDown(KeyboardKeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.IsRepeat && !_seekActive) return;

        const KeyModifiers Chord = KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt;

        // Alt+Enter toggles borderless fullscreen. Handled before the modal
        // prompts so it works even while the date-seek / search prompt is open
        // (the prompts themselves use plain Enter to commit, never Alt+Enter).
        if ((e.Key == Keys.Enter || e.Key == Keys.KeyPadEnter)
            && (e.Modifiers & KeyModifiers.Alt) != 0)
        {
            var fs = _registry.FindFeature("fullscreen");
            if (fs != null) _registry.Invoke(fs, ShowBanner);
            return;
        }

        // Ctrl+K palette is modal and eats every key while open.
        if (_palette.Active)
        {
            _palette.HandleKey(e.Key, e.Modifiers, _registry, ShowBanner);
            return;
        }

        // Date-seek prompt swallows all keys except its own control set so the user
        // can type a date without triggering pause / focus / scale toggles.
        if (_seekActive)
        {
            switch (e.Key)
            {
                case Keys.Escape:
                    _seekActive = false;
                    _seekBuffer = "";
                    break;
                case Keys.Enter:
                case Keys.KeyPadEnter:
                    ApplyDateSeek();
                    break;
                case Keys.Backspace:
                    if (_seekBuffer.Length > 0)
                        _seekBuffer = _seekBuffer[..^1];
                    break;
            }
            return;
        }

        // Q3: name-search prompt. Same modal lifecycle as the date prompt above.
        if (_searchActive)
        {
            switch (e.Key)
            {
                case Keys.Escape:
                    _searchActive = false;
                    _searchBuffer = "";
                    break;
                case Keys.Enter:
                case Keys.KeyPadEnter:
                    ApplyNameSearch();
                    break;
                case Keys.Backspace:
                    if (_searchBuffer.Length > 0)
                        _searchBuffer = _searchBuffer[..^1];
                    break;
            }
            return;
        }

        // Esc closes the topmost panel. On an empty screen it has to be pressed
        // twice within 2 s to quit, so a stray press can no longer kill the session.
        if (e.Key == Keys.Escape && (e.Modifiers & Chord) == 0)
        {
            if (_settings.Visible) { _settings.Visible = false; return; }
            if (_bookSidebar.Visible) { _bookSidebar.Visible = false; return; }
            double now = GLFW.GetTime();
            if (now < _escArmedUntil) { Close(); return; }
            _escArmedUntil = now + 2.0;
            ShowBanner(Localization.T("ui.quit.confirm"), 2.0);
            return;
        }

        // Settings panel open: Left / Right switch tabs.
        if (_settings.Visible && (e.Key == Keys.Left || e.Key == Keys.Right) && (e.Modifiers & Chord) == 0)
        {
            _settings.CycleTab(e.Key == Keys.Right ? 1 : -1);
            return;
        }

        _registry.Dispatch(e.Key, e.Modifiers, ShowBanner);
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);
        // A12: per-frame profiler. BeginFrame harvests last frame's GPU
        // timer-query results; BeginPass/EndPass wrap each major render group.
        _profiler.Enabled = _showProfiler;
        _profiler.BeginFrame();
        _renderer.BeginScene();

        // Choose between the full body list (planets + dwarfs) and the major-only
        // slice in one place so every render pass sees a consistent view.
        // Bodies absorbed in a physics collision drop out of every pass as well.
        Planet[] visible = AliveSubset(_showDwarfs ? _planets : _majorPlanets);

        // V8: build the shadow caster list (planets + Moon + Galileans + Titan).
        // The Sun is the light source so it never casts. Capped at 16 by the renderer.
        BuildShadowCasters(visible);

        // R3: adaptive star brightness + saturation. Far from the Sun the Milky
        // Way is dimmed so distant planets aren't drowned by the panorama; close
        // to a planet the colour is punched up for a "near-orbit" feel.
        UpdateAdaptiveStars(visible);

        _profiler.BeginPass("sky");
        _renderer.DrawStars(_camera);
        if (_showConstellations) _constellations.Draw(_camera);
        if (_showOrbits)
        {
            _renderer.DrawOrbits(_camera, visible);
            for (int ci = 0; ci < _comets.All.Length; ci++)
                if (ExtraAlive(1 + _moons.Length + ci)) _renderer.DrawCometOrbit(_camera, _comets.All[ci]);
        }
        if (_showTrails) _renderer.DrawTrails(_camera, visible);
        _profiler.EndPass();

        _profiler.BeginPass("planets");
        _renderer.DrawSun(_camera, Vector3.Zero, SunRadius);
        foreach (var p in visible)
            _renderer.DrawPlanet(_camera, p, Vector3.Zero);
        if (WorldAlive(_worldMoonIdx)) _renderer.DrawPlanet(_camera, _moon, Vector3.Zero);
        for (int mi = 0; mi < _moons.Length; mi++)
            if (WorldAlive(_worldMoonsIdx[mi])) _renderer.DrawPlanet(_camera, _moons[mi].Body, Vector3.Zero);
        for (int ci = 0; ci < _comets.All.Length; ci++)
            if (WorldAlive(_worldCometIdx[ci])) _renderer.DrawPlanet(_camera, _comets.All[ci].Body, Vector3.Zero);

        // V3: cloud layer for any planet that has one (currently just Earth).
        // Drawn after the opaque planet pass so alpha-blending composites over
        // the surface — including the V4 night-side city-lights baked into PlanetFS.
        foreach (var p in visible)
            _renderer.DrawClouds(_camera, p, Vector3.Zero);

        var saturn = _planets[5];
        if (WorldAlive(_worldPlanetIdx[5])) _renderer.DrawSaturnRing(_camera, saturn, Vector3.Zero);

        // Physics sandbox, Compare mode: translucent ghost of every integrated body at
        // its ephemeris position plus a dashed link, so the divergence is legible.
        if (_simMode == SimulationMode.Compare) DrawGhosts();
        _profiler.EndPass();

        _profiler.BeginPass("particles");
        _belt.Draw(_camera);
        _comets.DrawTails(_camera);
        _solarWind.Draw(_camera);
        _solarFlares.Draw(_camera);
        DrawImpactFlashes();

        // S9–S11: probe crosses, Lagrange-point diamonds, meteor streaks. All use
        // additive blending so they brighten the underlying scene without occluding it.
        if (_showProbes) _probes.Draw(_camera);
        if (_showLagrange) { _lagrange.Enabled = true; _lagrange.Draw(_camera); }
        if (_showMeteors) _meteors.Draw(_camera);
        _profiler.EndPass();
        if (_showAxes) _renderer.DrawPlanetAxes(_camera, visible);

        // S13: tidal-lock arrows on every locked moon. The Moon, Galileans and
        // Titan are all locked to their hosts; the arrow tip points at the host.
        if (_showTidalLock)
        {
            _tidalLock.Draw(_camera, EnumerateTidalPairs());
        }

        // S14: heliocentric-alignment indicator (line through aligned majors).
        if (_showAlignment) _alignment.Draw(_camera, _alignmentPlanets);

        // V13: aurora ribbons at Earth + Jupiter poles. Drawn inside the HDR pass
        // so the bright crests feed the bloom composite. Intensity is boosted when
        // the solar wind is on ("strong solar activity").
        if (_showAurora)
        {
            float t = (float)GLFW.GetTime();
            float wind = _solarWind.Enabled ? 1.0f : 0.5f;
            var earth = _planets[2];
            if (WorldAlive(_worldPlanetIdx[2]))
                _aurora.DrawForBody(_camera, earth.Position, earth.VisualRadius, earth.AxisTiltDeg,
                    new Vector4(0.30f, 1.00f, 0.55f, 0.80f), 1.0f * wind, t);
            var jupiter = _planets[4];
            if (WorldAlive(_worldPlanetIdx[4]))
                _aurora.DrawForBody(_camera, jupiter.Position, jupiter.VisualRadius, jupiter.AxisTiltDeg,
                    new Vector4(0.85f, 0.45f, 1.00f, 0.85f), 0.85f * wind, t);
        }

        // Apply HDR bright-pass + Gaussian blur + additive composite to the
        // default framebuffer. All subsequent 2D overlays (labels, UI panels)
        // are drawn directly to the default framebuffer and therefore unaffected.
        _profiler.BeginPass("bloom");
        _renderer.EndSceneAndApplyBloom();

        // V6: lens flare ghosts. Drawn after the composite so the chain isn't
        // smeared by bloom and looks like internal reflections in the lens
        // rather than a halo of the Sun itself.
        _renderer.DrawLensFlare(_camera, Vector3.Zero);
        _profiler.EndPass();

        _profiler.BeginPass("ui");

        // Labels
        if (_showLabels)
        {
            _renderer.DrawLabel(_font, _camera,
                new Vector3(0, SunRadius + 1.5f, 0), "Sun", 14, new Vector4(1, 0.9f, 0.5f, 0.95f));
            foreach (var p in visible)
                _renderer.DrawLabel(_font, _camera,
                    p.Position + new Vector3(0, p.VisualRadius + 1.0f, 0),
                    p.Name, 13, new Vector4(0.85f, 0.9f, 1f, 0.95f));
            if (WorldAlive(_worldMoonIdx))
                _renderer.DrawLabel(_font, _camera,
                    _moon.Position + new Vector3(0, _moon.VisualRadius + 0.5f, 0),
                    _moon.Name, 12, new Vector4(0.85f, 0.85f, 0.85f, 0.9f));
            for (int mi = 0; mi < _moons.Length; mi++)
            {
                if (!WorldAlive(_worldMoonsIdx[mi])) continue;
                var m = _moons[mi];
                _renderer.DrawLabel(_font, _camera,
                    m.Body.Position + new Vector3(0, m.Body.VisualRadius + 0.4f, 0),
                    m.Body.Name, 11, new Vector4(0.85f, 0.85f, 0.85f, 0.85f));
            }
            for (int ci = 0; ci < _comets.All.Length; ci++)
            {
                if (!WorldAlive(_worldCometIdx[ci])) continue;
                var c = _comets.All[ci];
                _renderer.DrawLabel(_font, _camera,
                    c.Body.Position + new Vector3(0, c.Body.VisualRadius + 0.5f, 0),
                    c.Body.Name, 12, new Vector4(0.7f, 0.85f, 1f, 0.9f));
            }
        }

        // S9: probe labels (always drawn when probes are visible — they're the
        // whole point of the feature).
        if (_showProbes)
        {
            foreach (var pr in _probes.All)
            {
                if (!pr.Active) continue;
                _renderer.DrawLabel(_font, _camera, pr.Position, pr.Name, 11, pr.Color);
            }
        }

        // S10: Lagrange labels.
        if (_showLagrange)
        {
            foreach (var m in _lagrange.Markers)
                _renderer.DrawLabel(_font, _camera, m.Pos, m.Label, 10, m.Color);
        }

        // Constellation names — anchored to the camera so they sit at infinity on
        // the celestial sphere along with the line figures.
        if (_showConstellations)
        {
            var camPos = _camera.Eye;
            var col = new Vector4(0.65f, 0.8f, 1f, 0.75f);
            float r = MathF.Max(_camera.Distance, 50f) * 4f;
            foreach (var c in _constellations.Entries)
                _renderer.DrawLabel(_font, _camera, camPos + c.LabelDir * r, c.Name, 12, col);
        }

        // UI overlay
        var date = OrbitalMechanics.J2000.AddDays(_simDays);
        var white = new Vector4(1f, 1f, 1f, 0.95f);
        if (_helpMode != 2)
        {
            _renderer.DrawText(_font, $"{Localization.T("ui.date")}    {date:yyyy-MM-dd}", 12, 12, 16, white);
            string hudSpeed = _paused
                ? $"{Localization.T("ui.speed")}   {Localization.T("ui.paused")}"
                : $"{Localization.T("ui.speed")}   {(_daysPerSecond < 0 ? "-" : "")}{Math.Abs(_daysPerSecond):0.##} d/s {(_daysPerSecond < 0 ? Localization.T("ui.speed.reverse") : "")}";
            _renderer.DrawText(_font, hudSpeed, 12, 32, 16, white);
            if (_helpMode == 0)
                _renderer.DrawText(_font,
                    $"{Localization.T("ui.orbits")}  {(_showOrbits ? Localization.T("ui.on") : Localization.T("ui.off"))}    " +
                    $"{Localization.T("ui.labels")}  {(_showLabels ? Localization.T("ui.on") : Localization.T("ui.off"))}",
                    12, 52, 14, white);
        }

        // Top-left help panel. Generated from the feature registry (plus a few
        // hand-written mouse lines), so the cheat sheet can never drift from the
        // real key map — including user overrides from keybindings.json. Laid
        // out in as many columns as needed so it always fits the viewport.
        var dim = new Vector4(0.85f, 0.9f, 1f, 0.85f);
        // The Ctrl+K palette is modal and sits over the same top-left area, so
        // the cheat sheet is suppressed while it's open instead of bleeding
        // through the palette background.
        if (_helpMode == 0 && !_palette.Active)
        {
            var pairs = new List<(string key, string label)>();
            void AddStatic(string locKey)
            {
                foreach (var ln in Localization.T(locKey).Split('\n'))
                {
                    int sep = ln.IndexOf('|');
                    pairs.Add(sep < 0 ? ("", ln.Trim()) : (ln[..sep].Trim(), ln[(sep + 1)..].Trim()));
                }
            }
            AddStatic("ui.help.mouse");
            AddStatic("ui.help.extra");
            foreach (var e in _registry.Entries)
            {
                if (e.Bindings.Count == 0 || e.HideInHelp) continue;
                pairs.Add((e.BindingText, e.Label));
            }
            pairs.Add(("Esc", Localization.T("ui.help.esc")));

            const float topY = 78f;
            float bottomMargin = _scrubber.Visible ? 70f : 16f;
            if (_toolbar.Visible) bottomMargin += Toolbar.Height + 8f;
            // The selected-body info card (up to 7 lines) sits bottom-left in
            // this help mode; keep the cheat sheet clear of it.
            bottomMargin += 7f * 18f;
            float maxH = MathF.Max(120f, _renderer.FramebufferSize.Y - topY - bottomMargin);
            float maxW = MathF.Max(360f, _renderer.FramebufferSize.X - 24f);

            float pixelSize = 13f;
            float LineH(float ps) => _font.LineHeight * (ps / _font.FontPixelSize);

            // Pick the smallest column count that fits vertically.
            int cols = 1;
            int rowsPerCol = pairs.Count;
            while (rowsPerCol * LineH(pixelSize) > maxH && cols < 6)
            {
                cols++;
                rowsPerCol = (pairs.Count + cols - 1) / cols;
            }
            // Still too tall? Shrink the font (lower bound 8 px so glyphs stay legible).
            if (rowsPerCol * LineH(pixelSize) > maxH)
                pixelSize = MathF.Max(8f, pixelSize * maxH / (rowsPerCol * LineH(pixelSize)));

            // Column width = widest key + widest label; shrink the font further if
            // the columns would extend past the viewport edge.
            (float key, float label) Widths(float ps)
            {
                float kw = 0f, lw = 0f;
                foreach (var (k, l) in pairs)
                {
                    kw = MathF.Max(kw, _font.MeasureWidth(k, ps));
                    lw = MathF.Max(lw, _font.MeasureWidth(l, ps));
                }
                return (kw + 10f, lw + 18f);
            }
            var (keyW, labelW) = Widths(pixelSize);
            while (cols * (keyW + labelW) > maxW && pixelSize > 8f)
            {
                pixelSize = MathF.Max(8f, pixelSize - 0.5f);
                (keyW, labelW) = Widths(pixelSize);
            }

            _renderer.DrawText(_font, Localization.T("ui.help.title"), 12f, topY - 4f, 13f, dim);
            float lh = LineH(pixelSize);
            var keyCol = new Vector4(1f, 0.95f, 0.7f, 0.9f);
            for (int i = 0; i < pairs.Count; i++)
            {
                int c = i / rowsPerCol;
                int r = i % rowsPerCol;
                float x = 12f + c * (keyW + labelW);
                float y = topY + 14f + r * lh;
                _renderer.DrawText(_font, pairs[i].key, x, y, pixelSize, keyCol);
                _renderer.DrawText(_font, pairs[i].label, x + keyW, y, pixelSize, dim);
            }
        }
        else if (_helpMode == 1)
        {
            // Discovery hint so users always know how to reach the menus when
            // the full cheat sheet is collapsed.
            _renderer.DrawText(_font, Localization.T("ui.help.hint"),
                12f, 52f, 12f, new Vector4(0.7f, 0.8f, 1f, 0.75f));
        }

        // Info panel (multi-line) for selected body. Hidden in minimal / hidden
        // help modes (Q14: minimal = "just current speed + date").
        if (_helpMode == 0)
        {
            string info;
            var selectedBody = GetBody(_selectedIndex);
            if (selectedBody != null)
            {
                var p = selectedBody;
                double dist = Math.Sqrt(p.HelioAU.X * p.HelioAU.X + p.HelioAU.Y * p.HelioAU.Y + p.HelioAU.Z * p.HelioAU.Z);
                double dayHours = Math.Abs(p.RotationPeriodHours);
                string daySuffix = p.RotationPeriodHours < 0 ? Localization.T("ui.info.retro") : "";
                double yearDays = p.OrbitalPeriodYears * 365.25;
                info =
                    $"{p.Name}\n" +
                    $"{Localization.T("ui.info.radius")}  {p.RealRadiusKm:0} km\n" +
                    $"{Localization.T("ui.info.day")}     {dayHours:0.##} h{daySuffix}\n" +
                    $"{Localization.T("ui.info.year")}    {yearDays:0.#} d  /  {p.OrbitalPeriodYears:0.###} y\n" +
                    $"{Localization.T("ui.info.dist")}    {dist:0.000} AU\n" +
                    $"{Localization.T("ui.info.tilt")}    {p.AxisTiltDeg:0.##} deg";
            }
            else if (_selectedIndex == -1)
            {
                info =
                    $"{Localization.T("ui.body.sun")}\n" +
                    $"{Localization.T("ui.info.radius")}  695700 km\n" +
                    $"{Localization.T("ui.info.day")}     {Localization.T("ui.info.sun.day")}\n" +
                    $"{Localization.T("ui.info.mass")}    {Localization.T("ui.info.sun.mass")}\n" +
                    $"{Localization.T("ui.info.type")}    {Localization.T("ui.info.sun.type")}";
            }
            else
            {
                info = Localization.T("ui.click.body");
            }
            // Draw multi-line panel anchored bottom-left. Lift it above the
            // timeline scrubber bar when the scrubber is visible so they don't
            // overlap (Q9 sits at viewportH-36 with ~50 px of vertical chrome).
            int lineCount = 1;
            foreach (char ch in info) if (ch == '\n') lineCount++;
            const float infoSize = 14f;
            float lineH = infoSize * (_font.LineHeight / _font.FontPixelSize);
            float scrubberPad = _scrubber.Visible ? 56f : 0f;
            float panelY = _renderer.FramebufferSize.Y - 12f - scrubberPad - lineCount * lineH;
            _renderer.DrawText(_font, info, 12, panelY, infoSize, white);
        }

        // S11: live banner when a meteor shower is currently in its activity window.
        if (_showMeteors && _meteors.ActiveShowerName.Length > 0)
        {
            _renderer.DrawText(_font, $"☄ {Localization.T("ui.meteors.active", _meteors.ActiveShowerName)}",
                _renderer.FramebufferSize.X - 260f, _renderer.FramebufferSize.Y - 30f, 13f,
                new Vector4(1f, 0.85f, 0.6f, 0.95f));
        }

        // S14: alignment banner — list every active group, top-right above the meteor banner.
        if (_showAlignment && _alignment.ActiveGroups.Count > 0)
        {
            float y = _renderer.FramebufferSize.Y - 56f;
            foreach (var g in _alignment.ActiveGroups)
            {
                string txt = $"✦ {Localization.T("ui.alignment.banner", g.PlanetIndices.Length, g.Names)}";
                _renderer.DrawText(_font, txt,
                    _renderer.FramebufferSize.X - 360f, y, 13f,
                    new Vector4(1f, 0.95f, 0.6f, 0.95f));
                y -= 20f;
            }
        }

        // Physics sandbox: mode banner (top-right, under the FPS HUD) and the
        // diagnostics card (top-centre, clear of the right-hand panels). The seek
        // progress bar takes the top-centre feedback slot below.
        if (_simMode != SimulationMode.Ephemeris)
        {
            float px = _renderer.FramebufferSize.X - 360f;
            float py = 12f + (_showHud ? 6 * 18f + 12f : 0f);
            string modeKey = _simMode == SimulationMode.Compare ? "ui.physics.banner.compare" : "ui.physics.banner.physics";
            _renderer.DrawText(_font, $"⚙ {Localization.T(modeKey)}", px, py, 13f,
                new Vector4(0.65f, 1f, 0.85f, 0.95f));
            if (_showPhysicsHud)
            {
                // Below the top-left status lines and the top-centre seek feedback, and
                // never under the settings panel (which starts 476 px from the right edge).
                float hx = MathF.Min(_renderer.FramebufferSize.X * 0.5f - 300f, _renderer.FramebufferSize.X - 476f - 360f);
                DrawPhysicsHud(MathF.Max(12f, hx), 84f);
            }
        }

        // Date-seek prompt: top-center modal overlay while active. Drawn after every
        // other UI so it can't be occluded.
        if (_seekActive)
        {
            string prompt = Localization.T("ui.seek.prompt", _seekBuffer);
            _renderer.DrawText(_font, prompt,
                _renderer.FramebufferSize.X * 0.5f - 200f, 20f, 16f,
                new Vector4(1f, 1f, 0.7f, 1f));
        }
        else if (_searchActive)
        {
            // Q3: search prompt + top matches preview.
            var matches = FindNameMatches(_searchBuffer, max: 5);
            var sb = new System.Text.StringBuilder();
            sb.Append(Localization.T("ui.search.prompt", _searchBuffer));
            if (matches.Count > 0)
            {
                sb.Append('\n');
                for (int i = 0; i < matches.Count; i++)
                {
                    sb.Append(i == 0 ? "  > " : "    ");
                    sb.Append(matches[i].name).Append('\n');
                }
            }
            _renderer.DrawText(_font, sb.ToString(),
                _renderer.FramebufferSize.X * 0.5f - 200f, 20f, 16f,
                new Vector4(0.85f, 1f, 0.85f, 1f));
        }
        else if (_simMode != SimulationMode.Ephemeris && _physicsTarget is { } target && _world.Ready)
        {
            // Physics sandbox: a date jump is integrated over several frames — show
            // where the clock is on its way to the target instead of "Jumped to".
            double span = Math.Abs(target - _physicsSeekFrom);
            double done = Math.Abs(_simDays - _physicsSeekFrom);
            double pct = span > 0 ? Math.Clamp(100.0 * done / span, 0.0, 100.0) : 100.0;
            const int Cells = 24;
            int filled = (int)Math.Round(pct / 100.0 * Cells);
            var bar = new System.Text.StringBuilder(Cells);
            for (int i = 0; i < Cells; i++) bar.Append(i < filled ? '█' : '░');
            var targetDate = OrbitalMechanics.J2000.AddDays(target);
            var col = new Vector4(1f, 0.9f, 0.5f, 0.95f);
            _renderer.DrawText(_font,
                Localization.T("ui.physics.progress", date.ToString("yyyy-MM-dd"), targetDate.ToString("yyyy-MM-dd"), pct),
                _renderer.FramebufferSize.X * 0.5f - 160f, 20f, 14f, col);
            _renderer.DrawText(_font, bar.ToString(), _renderer.FramebufferSize.X * 0.5f - 160f, 38f, 13f, col);
        }
        else if (_seekFeedback.Length > 0)
        {
            _renderer.DrawText(_font, _seekFeedback,
                _renderer.FramebufferSize.X * 0.5f - 160f, 20f, 14f,
                new Vector4(1f, 0.9f, 0.5f, 0.9f));
        }

        // Q6: hover tooltip beside the cursor.
        if (!_seekActive && !_searchActive && !_palette.Active)
        {
            string? tip = null;
            if (_hoverIndex == -1) tip = Localization.T("ui.tooltip.sun");
            else
            {
                var hb = GetBody(_hoverIndex);
                if (hb != null)
                {
                    double dAU = Math.Sqrt(hb.HelioAU.X * hb.HelioAU.X + hb.HelioAU.Y * hb.HelioAU.Y + hb.HelioAU.Z * hb.HelioAU.Z);
                    tip = $"{hb.Name}\n{dAU:0.000} AU";
                }
            }
            if (tip != null)
            {
                _renderer.DrawText(_font, tip,
                    _mousePos.X + 14f, _mousePos.Y + 10f, 13f,
                    new Vector4(1f, 1f, 0.9f, 0.95f));
            }
        }

        // Q7: HUD overlay (FPS + particle counts + scale mode). Top-right corner.
        if (_showHud)
        {
            string scale = Localization.T(OrbitalMechanics.RealScale ? "ui.scale.real" : "ui.scale.compressed");
            string hud =
                $"{Localization.T("ui.hud.fps")}       {_fpsValue:0.}\n" +
                $"{Localization.T("ui.hud.scale")}     {scale}\n" +
                $"{Localization.T("ui.hud.wind")}      {_solarWind.ActiveCount} / {_solarWind.MaxParticles}\n" +
                $"{Localization.T("ui.hud.flares")}    {_solarFlares.ActiveCount} / {_solarFlares.MaxParticles}\n" +
                $"{Localization.T("ui.hud.comet")}     {_comets.TotalActive} / {_comets.TotalMax}\n" +
                $"{Localization.T("ui.hud.belt")}      {_belt.Count}";
            _renderer.DrawText(_font, hud,
                _renderer.FramebufferSize.X - 220f, 12f, 14f,
                new Vector4(0.7f, 1f, 0.8f, 0.95f));
        }

        // Q9: timeline scrubber (drawn behind tooltip / settings overlay) — pass the
        // bookmark catalogue so the bar can show coloured ticks for each event.
        _scrubber.Draw(_renderer, _font, _simDays, _bookmarks);

        // Bottom-centre toolbar (pause / speed / core toggles / menus). Lifted
        // above the timeline scrubber when that is visible.
        _toolbar.Draw(_renderer, _font, _mousePos,
            _scrubber.Visible ? _scrubber.Top - 6f : _renderer.FramebufferSize.Y - 10f);

        // Q12: in-app settings overlay. Drawn before the bookmarks sidebar so the
        // sidebar can stack underneath it via _settings.Bottom. Both panels are
        // suppressed while the modal Ctrl+K palette is open so it never overlaps
        // them; they reappear untouched when the palette closes.
        if (!_palette.Active)
        {
            _settings.Draw(_renderer, _font, _mousePos);

            // Q8 / S12: bookmarks sidebar (right-edge panel). When the settings panel
            // is open we push the sidebar below it; otherwise it sits at y=150.
            float sidebarTopY = _settings.Visible ? _settings.Bottom + 12f : 150f;
            _bookSidebar.Draw(_renderer, _font, _bookmarks, _mousePos, _simDays, sidebarTopY);
        }

        // Q10: small "PLAYING…" banner while a camera path is active.
        if (_camPath.IsPlaying)
        {
            _renderer.DrawText(_font, $"▶ {Localization.T("ui.campath.banner")}",
                _renderer.FramebufferSize.X * 0.5f - 60f,
                _renderer.FramebufferSize.Y - 60f, 14f,
                new Vector4(1f, 0.85f, 0.5f, 0.95f));
        }

        // A7 (interactive): bright red "● REC mm:ss N frames" banner pinned to
        // the top-centre while recording is active. Drawn before SwapBuffers so
        // it ends up baked into the saved frame too — handy as a watermark.
        if (_recording)
        {
            var elapsed = TimeSpan.FromSeconds(GLFW.GetTime() - _recordStartedAt);
            _renderer.DrawText(_font,
                Localization.T("ui.record.status", elapsed, _recordFrameIndex),
                _renderer.FramebufferSize.X * 0.5f - 80f, 8f, 14f,
                new Vector4(1f, 0.25f, 0.25f, 0.95f));
        }

        // Ctrl+K palette: modal, so it sits above every other panel.
        _palette.Draw(_renderer, _font, _mousePos);

        // A12: profiler overlay (per-pass GPU + CPU times). Drawn last so it sits
        // on top of everything else; toggled with F10.
        _profiler.EndPass();
        if (_showProfiler) DrawProfilerOverlay();

        SwapBuffers();
        _profiler.EndFrame();

        // A7 (interactive): dump every rendered frame while F9-recording is on.
        // Done after SwapBuffers (back-buffer still holds the just-shown image)
        // so the saved PNG matches what the user sees. Only the GL readback
        // happens on the render thread; PNG encode + file write are pushed to
        // a worker so capture FPS isn't dragged down to single digits.
        if (_recording)
        {
            string path = Path.Combine(_recordDir, $"frame_{_recordFrameIndex:D5}.png");
            try
            {
                if (CaptureBackBufferRgba(out var flipped, out int cw, out int ch))
                {
                    _recordFrameIndex++;
                    _recordEncodeGate.Wait();
                    _recordEncodeTasks.Add(Task.Run(() =>
                    {
                        try { EncodePngFromRgba(flipped, cw, ch, path); }
                        catch (Exception ex) { Debug.WriteLine($"[record] encode failed: {ex}"); }
                        finally { _recordEncodeGate.Release(); }
                    }));
                }
            }
            catch (Exception ex)
            {
                _recording = false;
                _seekFeedback = $"Record failed: {ex.Message}";
                _seekFeedbackUntil = GLFW.GetTime() + 3.0;
                Debug.WriteLine($"[record] save failed: {ex}");
            }
        }

        // A7: headless render — capture the freshly-presented frame to disk and
        // step the job. When the last frame writes successfully, optionally
        // invoke ffmpeg and close the window so the process exits cleanly.
        if (Headless != null)
        {
            string path = Path.Combine(Headless.OutDir, $"frame_{Headless.FrameIndex:D5}.png");
            SaveScreenshotTo(path);
            int done = Headless.FrameIndex + 1;
            if (done % 10 == 0 || done == Headless.TotalFrames)
            {
                Console.WriteLine(Localization.T("ui.render.progress",
                    done, Headless.TotalFrames, 100.0 * done / Math.Max(1, Headless.TotalFrames)));
            }
            Headless.FrameIndex = done;
            if (Headless.FrameIndex >= Headless.TotalFrames)
            {
                Console.WriteLine(Localization.T("ui.render.done", Headless.TotalFrames, Headless.OutDir));
                Headless.TryEncodeVideo();
                Close();
            }
        }
    }

    // --- Mouse ---
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        // Same source as the hover highlight (OnMouseMove), so the row that
        // lights up under the cursor is exactly the row a click hits.
        var pos = _mousePos;

        // Ctrl+K palette is modal: a click on a row runs it, anywhere else closes it.
        if (_palette.Active)
        {
            if (e.Button == MouseButton.Left && !_palette.TryHandleClick(pos, _registry, ShowBanner))
                _palette.Close();
            return;
        }
        // Q12: settings panel takes click priority when open.
        if (e.Button == MouseButton.Left && _settings.TryHandleClick(pos)) return;
        // Bottom toolbar buttons.
        if (e.Button == MouseButton.Left && _toolbar.TryHandleClick(pos)) return;
        // Q8 / S12: bookmarks sidebar — filter cycle / row jump.
        if (e.Button == MouseButton.Left && _bookSidebar.TryHandleClick(pos, _bookmarks, out var jumpTo))
        {
            if (jumpTo is { } ev)
            {
                SetSimTime(Bookmarks.ToSimDays(ev));
                ClearAllTrails();
                _audio.PlayTick();
                _seekFeedback = $"{ev.Kind}: {ev.Title} \u2014 {ev.Date:yyyy-MM-dd}";
                _seekFeedbackUntil = GLFW.GetTime() + 4.0;
            }
            return;
        }
        // Q9: timeline scrubber begins a drag.
        if (e.Button == MouseButton.Left && _scrubber.TryBeginDrag(pos)) return;

        if (e.Button == MouseButton.Left)
        {
            double now = GLFW.GetTime();
            int picked = TryPick(pos);
            bool isDouble = now - _lastClickTime <= DoubleClickSeconds &&
                            (pos - _lastClickPos).LengthSquared <= DoubleClickMaxPx * DoubleClickMaxPx;

            // Single click on a body always updates selection (info panel).
            if (picked != -2) _selectedIndex = picked;

            if (isDouble)
            {
                if (picked == -2)
                {
                    // Double-click on empty space: stop following the focused planet,
                    // but keep the camera right where it is (don't snap target to the Sun).
                    _focusIndex = -1;
                }
                else
                {
                    // Double-click on a body: focus camera on it.
                    FocusOn(picked);
                }
                _lastClickTime = -10.0; // consume
            }
            else
            {
                _lastClickTime = now;
                _lastClickPos = pos;
            }
        }
        _camera.HandleMouseDown(e, pos);
    }
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButton.Left) _scrubber.EndDrag();
        _camera.HandleMouseUp(e);
    }
    protected override void OnMouseMove(MouseMoveEventArgs e)
    {
        base.OnMouseMove(e);
        _mousePos = new Vector2(e.X, e.Y);
        _camera.HandleMouseMove(_mousePos);
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        // Let the settings panel grab the wheel first so users on small monitors
        // can scroll through the rows when the panel is taller than the
        // viewport. Camera zoom only applies when the cursor is outside.
        if (_settings.HandleScroll(_mousePos, e.OffsetY)) return;
        _camera.HandleScroll(e.OffsetY);
    }

    /// <summary>Project all bodies to screen and return the index of the one closest to <paramref name="screenPos"/>.
    /// -2 = none, -1 = sun, 0..7 = planet.</summary>
    private int TryPick(Vector2 screenPos)
    {
        var view = _camera.ViewMatrix;
        var proj = _camera.ProjectionMatrix;
        // Track the body whose pick-disk the click is inside, choosing the one whose
        // center is closest to the cursor when several disks overlap.
        float bestDist = float.MaxValue;
        int best = -2;

        bool TryProject(Vector3 world, out Vector2 sp)
        {
            var clip = new Vector4(world, 1f) * view * proj;
            if (clip.W <= 0) { sp = default; return false; }
            var ndc = clip.Xyz / clip.W;
            if (ndc.Z < -1 || ndc.Z > 1) { sp = default; return false; }
            sp = new Vector2(
                (ndc.X * 0.5f + 0.5f) * _renderer.FramebufferSize.X,
                (1f - (ndc.Y * 0.5f + 0.5f)) * _renderer.FramebufferSize.Y);
            return true;
        }

        // Camera right vector (world-space) extracted from the view matrix.
        var rightWorld = new Vector3(view.M11, view.M21, view.M31);

        // Adaptive pick radius: planet's projected screen radius (in px) inflated, with
        // a generous floor so far/tiny planets can still be hit by a sloppy click.
        float PickRadius(Vector3 center, float worldRadius)
        {
            if (!TryProject(center, out var c)) return 24f;
            if (!TryProject(center + rightWorld * worldRadius, out var e)) return 24f;
            float r = (e - c).Length;
            return MathF.Max(24f, r * 2.5f);
        }

        if (TryProject(Vector3.Zero, out var sunSp))
        {
            float d = (sunSp - screenPos).Length;
            float r = PickRadius(Vector3.Zero, SunRadius);
            if (d < r && d < bestDist) { bestDist = d; best = -1; }
        }
        for (int i = 0; i < _planets.Length; i++)
        {
            if (!_showDwarfs && i >= _dwarfStart) break;
            if (!WorldAlive(_worldPlanetIdx[i])) continue;
            if (!TryProject(_planets[i].Position, out var sp)) continue;
            float d = (sp - screenPos).Length;
            float r = PickRadius(_planets[i].Position, _planets[i].VisualRadius);
            if (d < r && d < bestDist) { bestDist = d; best = i; }
        }
        // Q2: also pick the Moon, the major moons (Galileans + Titan) and the comet.
        // They share the same projection pipeline; their unified index starts at
        // _planets.Length and increases with their position in _extraBodies.
        for (int i = 0; i < _extraBodies.Length; i++)
        {
            if (!ExtraAlive(i)) continue;
            var b = _extraBodies[i];
            if (!TryProject(b.Position, out var sp)) continue;
            float d = (sp - screenPos).Length;
            float r = PickRadius(b.Position, b.VisualRadius);
            if (d < r && d < bestDist) { bestDist = d; best = _planets.Length + i; }
        }
        return best;
    }

    private void FocusOn(int index)
    {
        if (index == -1)
        {
            _focusIndex = -1;
            BeginFocusTransition(Vector3.Zero, MathF.Max(_camera.Distance, SunRadius * 6f));
            return;
        }

        var body = GetBody(index);
        if (body == null) return;
        _focusIndex = index;

        // For planets we re-evaluate the heliocentric position against the current sim
        // time so the camera aims at where the body actually is right now. Moons and the
        // comet body are already updated each frame in OnUpdateFrame, so their Position
        // is already current. In the physics modes the integrated position is the
        // truth, so the analytic re-evaluation is skipped.
        if (index < _planets.Length && _simMode == SimulationMode.Ephemeris)
        {
            body.HelioAU = OrbitalMechanics.HeliocentricPosition(body, _simDays);
            float s = OrbitalMechanics.OrbitWorldScale(body.SemiMajorAxisAU);
            body.Position = new Vector3(
                (float)(body.HelioAU.X * s),
                (float)(body.HelioAU.Y * s),
                (float)(body.HelioAU.Z * s));
        }

        float endDist = MathF.Max(body.VisualRadius * 6f, _camera.MinDistance * 4f);
        BeginFocusTransition(body.Position, endDist);
        Debug.WriteLine($"[focus] {body.Name} target={body.Position} dist={endDist:0.##}");
    }

    /// <summary>Resolve a unified body index into a <see cref="Planet"/> reference.
    /// -1 (Sun) and -2 (none) both return <c>null</c>; planet indices map straight into
    /// <see cref="_planets"/>; indices &gt;= <c>_planets.Length</c> address
    /// <see cref="_extraBodies"/> (Moon, major moons, comet).</summary>
    private Planet? GetBody(int index)
    {
        if (index < 0 || _planets == null) return null;
        if (index < _planets.Length) return _planets[index];
        int j = index - _planets.Length;
        return j < _extraBodies.Length ? _extraBodies[j] : null;
    }

    /// <summary>Capture the current camera Target + Distance and kick off a smooth
    /// 0.5 s lerp toward <paramref name="endTarget"/> / <paramref name="endDistance"/>.
    /// During the transition the end-target is re-evaluated each frame against the
    /// focused body's live position (handled in <see cref="OnUpdateFrame"/>).</summary>
    private void BeginFocusTransition(Vector3 endTarget, float endDistance)
    {
        _focusStartTarget = _camera.Target;
        _focusStartDistance = _camera.Distance;
        _focusEndDistance = endDistance;
        _focusTransitionElapsed = 0.0;
        _focusTransitioning = true;
        _audio.PlayWhoosh();
        // endTarget is consumed implicitly via _focusIndex during the lerp; for the Sun
        // case (_focusIndex = -1) we still need a fixed target, but Vector3.Zero is
        // already the Sun's position so this just works.
        _ = endTarget;
    }

    private void ToggleRealScale()
    {
        OrbitalMechanics.RealScale = !OrbitalMechanics.RealScale;

        // Replace each body's VisualRadius with the value appropriate for the new mode.
        // Compressed mode restores the artistic radii captured at load; real mode derives
        // them from real kilometres via KmToWorldRealScale.
        for (int i = 0; i < _planets.Length; i++)
        {
            _planets[i].VisualRadius = OrbitalMechanics.RealScale
                ? (float)(_planets[i].RealRadiusKm * OrbitalMechanics.KmToWorldRealScale)
                : _inflatedPlanetRadii[i];
        }
        _moon.VisualRadius = OrbitalMechanics.RealScale
            ? (float)(_moon.RealRadiusKm * OrbitalMechanics.KmToWorldRealScale)
            : _inflatedMoonRadius;

        for (int i = 0; i < _moons.Length; i++)
        {
            _moons[i].Body.VisualRadius = OrbitalMechanics.RealScale
                ? (float)(_moons[i].Body.RealRadiusKm * OrbitalMechanics.KmToWorldRealScale)
                : _inflatedMoonsRadii[i];
        }

        // Orbit lines were uploaded once with the old scale — rebuild them.
        _renderer.BuildOrbits(_planets);
        _comets.RebuildOrbits();

        // Trails accumulated in the old world scale would suddenly jump on toggle.
        ClearAllTrails();

        // Real-scale planets are tiny (Earth ~0.002 units), so allow zooming in much
        // closer than the default. Compressed mode keeps a comfortable safety floor.
        _camera.MinDistance = OrbitalMechanics.RealScale ? 0.0005f : 2f;
        _camera.MaxDistance = OrbitalMechanics.RealScale ? 6000f : 4000f;

        // Re-fit the camera so the freshly rescaled scene fits on screen.
        if (_focusIndex < 0)
        {
            _camera.Distance = OrbitalMechanics.RealScale ? 3200f : 320f;
            _camera.Target = Vector3.Zero;
        }
        else
        {
            var b = GetBody(_focusIndex);
            if (b != null)
                _camera.Distance = MathF.Max(b.VisualRadius * 6f, _camera.MinDistance * 4f);
        }

        Debug.WriteLine($"[scale] {(OrbitalMechanics.RealScale ? "REAL (1 AU = 50 units)" : "compressed (a^0.45)")}");
    }

    private void ClearAllTrails()
    {
        if (_planets == null) return;
        foreach (var p in _planets) p.TrailReset();
    }

    // -------- Physics sandbox ----------------------------------------------------

    /// <summary>Sim time the app is heading for: the pending physics seek target, or
    /// the current clock when nothing is pending (always the clock in Ephemeris mode).</summary>
    private double TargetSimDays => _physicsTarget ?? _simDays;
    private double _physicsSeekFrom;

    /// <summary>Jump the simulation clock. Ephemeris mode snaps instantly; the physics
    /// modes register a seek target and integrate toward it over the following
    /// frames (see <see cref="AdvancePhysics"/>), showing a progress banner.</summary>
    private void SetSimTime(double days)
    {
        if (_simMode == SimulationMode.Ephemeris)
        {
            _simDays = days;
            _physicsTarget = null;
            return;
        }
        if (_physicsTarget == null) _physicsSeekFrom = _simDays;
        _physicsTarget = days;
    }

    /// <summary>Build the N-body world over the render bodies (Sun, planets, dwarfs,
    /// Moon, Galileans, Titan; comets as test particles) and cache the index maps.</summary>
    private void BuildPhysicsWorld()
    {
        var comets = _comets.Bodies.ToArray();
        _world = PhysicsWorld.Create(_planets, _dwarfStart, _moon, _moons, comets, _physConst);
        _worldPlanetIdx = new int[_planets.Length];
        for (int i = 0; i < _planets.Length; i++) _worldPlanetIdx[i] = _world.IndexOf(_planets[i].Name);
        _worldMoonIdx = _world.IndexOf(_moon.Name);
        _worldMoonsIdx = new int[_moons.Length];
        for (int i = 0; i < _moons.Length; i++) _worldMoonsIdx[i] = _world.IndexOf(_moons[i].Body.Name);
        _worldCometIdx = new int[comets.Length];
        for (int i = 0; i < comets.Length; i++) _worldCometIdx[i] = _world.IndexOf(comets[i].Name);
        _ghostPlanets = new Vector3[_planets.Length];
        _ghostMoons = new Vector3[_moons.Length];
        _ghostComets = new Vector3[comets.Length];
        _world.CollisionsEnabled = _collisionsEnabled;
        // Unified (pick / focus) index of every world body, and the world index of
        // every extra body, for collision bookkeeping.
        _worldToUnified.Clear();
        _worldToUnified[0] = -1;
        for (int i = 0; i < _planets.Length; i++) if (_worldPlanetIdx[i] >= 0) _worldToUnified[_worldPlanetIdx[i]] = i;
        _worldExtraIdx = new int[_extraBodies.Length];
        for (int j = 0; j < _extraBodies.Length; j++)
        {
            _worldExtraIdx[j] = _world.IndexOf(_extraBodies[j].Name);
            if (_worldExtraIdx[j] >= 0) _worldToUnified[_worldExtraIdx[j]] = _planets.Length + j;
        }
    }

    /// <summary>(Re)seed the world from the ephemerides at the current date and
    /// restart the belt; forgets any collision already announced.</summary>
    private void SeedPhysics()
    {
        _world.Initialize(_simDays);
        _belt.BeginPhysics(_world);
        _announcedCollisions = 0;
        _impactFlashes.Clear();
        SyncAbsorbedBodies();
    }

    /// <summary>True when the world body (or, outside the physics modes, any body) still
    /// exists; -1 stands for "not integrated" and is always alive.</summary>
    private bool WorldAlive(int worldIdx)
        => _simMode == SimulationMode.Ephemeris || worldIdx < 0 || !_world.Ready || _world.Bodies[worldIdx].Alive;

    private bool ExtraAlive(int extraIdx)
        => extraIdx < 0 || extraIdx >= _worldExtraIdx.Length || WorldAlive(_worldExtraIdx[extraIdx]);

    private int WorldToUnified(int worldIdx) => _worldToUnified.TryGetValue(worldIdx, out int u) ? u : -2;

    /// <summary>The bodies of <paramref name="all"/> (a prefix slice of <see cref="_planets"/>)
    /// that have not been absorbed; the same array when none has.</summary>
    private Planet[] AliveSubset(Planet[] all)
    {
        if (_simMode == SimulationMode.Ephemeris || !_world.Ready) return all;
        int dead = 0;
        for (int i = 0; i < all.Length; i++) if (!WorldAlive(_worldPlanetIdx[i])) dead++;
        if (dead == 0) return all;
        var arr = new Planet[all.Length - dead];
        int n = 0;
        for (int i = 0; i < all.Length; i++) if (WorldAlive(_worldPlanetIdx[i])) arr[n++] = all[i];
        return arr;
    }

    /// <summary>Per-body side effects of the world's alive flags: an absorbed comet stops
    /// emitting (its existing tail fades out on its own).</summary>
    private void SyncAbsorbedBodies()
    {
        for (int ci = 0; ci < _comets.All.Length; ci++)
            _comets.All[ci].TailEnabled = WorldAlive(_worldCometIdx[ci]);
    }

    private static string BodyDisplayName(string name) => name == "Sun" ? Localization.T("ui.body.sun") : name;

    /// <summary>Turn the world's new collision events into UI: a banner, a whoosh, an
    /// impact flash on the survivor, and focus / selection / trail cleanup for the body
    /// that no longer exists.</summary>
    private void ProcessCollisionEvents()
    {
        var events = _world.Collisions;
        for (int k = _announcedCollisions; k < events.Count; k++)
        {
            var ev = events[k];
            ShowBanner(Localization.T("ui.physics.collision.banner", BodyDisplayName(ev.AbsorbedName), BodyDisplayName(ev.SurvivorName)), 4.0);
            _audio.PlayWhoosh();
            int survivor = WorldToUnified(ev.Survivor);
            int absorbed = WorldToUnified(ev.Absorbed);
            if (absorbed != -2)
            {
                if (_focusIndex == absorbed) FocusOn(survivor == -2 ? -1 : survivor);
                if (_selectedIndex == absorbed) _selectedIndex = survivor;
                if (_hoverIndex == absorbed) _hoverIndex = -2;
                GetBody(absorbed)?.TrailReset();
            }
            if (survivor != -2) _impactFlashes.Add((survivor, GLFW.GetTime()));
        }
        if (events.Count != _announcedCollisions)
        {
            _announcedCollisions = events.Count;
            SyncAbsorbedBodies();
        }
    }

    /// <summary>Expanding, fading HDR fireball on every recent collision survivor (the
    /// Sun's halo sprite, additive, so the bloom pass ignites it).</summary>
    private void DrawImpactFlashes()
    {
        if (_impactFlashes.Count == 0) return;
        double now = GLFW.GetTime();
        for (int i = _impactFlashes.Count - 1; i >= 0; i--)
        {
            var (body, start) = _impactFlashes[i];
            float t = (float)((now - start) / ImpactFlashSeconds);
            if (t >= 1f || _simMode == SimulationMode.Ephemeris) { _impactFlashes.RemoveAt(i); continue; }
            Vector3 pos;
            float radius;
            if (body == -1) { pos = Vector3.Zero; radius = SunRadius; }
            else
            {
                var b = GetBody(body);
                if (b == null) { _impactFlashes.RemoveAt(i); continue; }
                pos = b.Position;
                radius = b.VisualRadius;
            }
            float fade = (1f - t) * (1f - t);
            float size = radius * (1.5f + 8f * t);
            _renderer.DrawGlowSprite(_camera, pos, size, new Vector3(1.0f, 0.70f, 0.40f) * (2.5f * fade));
            _renderer.DrawGlowSprite(_camera, pos, size * 0.45f, new Vector3(1.0f, 0.95f, 0.85f) * (3.0f * fade));
        }
    }

    /// <summary>Switch between Ephemeris / Physics / Compare. Leaving Ephemeris seeds the
    /// world from the ephemerides at the current date (and the belt from its Kepler
    /// orbits); returning to Ephemeris simply hands the bodies back to the analytic
    /// path. Physics ↔ Compare keeps the integrated state.</summary>
    private void SetSimulationMode(SimulationMode mode)
    {
        if (mode == _simMode) return;
        var old = _simMode;
        _simMode = mode;
        _physicsTarget = null;
        if (old == SimulationMode.Ephemeris)
        {
            SeedPhysics();
        }
        else if (mode == SimulationMode.Ephemeris)
        {
            _belt.EndPhysics();
            _impactFlashes.Clear();
            SyncAbsorbedBodies();   // everything exists again on the analytic path
        }
        ClearAllTrails();
    }

    /// <summary>Re-seed the running physics from the ephemerides at the current date.</summary>
    private void RestartPhysicsFromEphemeris()
    {
        if (_simMode == SimulationMode.Ephemeris) return;
        _physicsTarget = null;
        SeedPhysics();
        ClearAllTrails();
        ShowBanner(Localization.T("ui.physics.reinit.done", OrbitalMechanics.J2000.AddDays(_simDays).ToString("yyyy-MM-dd")), 2.5);
    }

    /// <summary>Per-frame integrator drive: catch the world up to the requested time
    /// within the CPU budget and adopt its clock as the frame's sim time.</summary>
    private void AdvancePhysics()
    {
        if (!_world.Ready) SeedPhysics();
        double goal = _physicsTarget ?? _simDays;
        int maxSteps = Headless != null ? int.MaxValue : PhysicsMaxStepsPerFrame;
        double budget = Headless != null ? double.PositiveInfinity : PhysicsFrameBudgetMs;
        bool done = _world.AdvanceTo(goal, maxSteps, budget);
        _simDays = _world.TimeDays;
        if (done) _physicsTarget = null;
        ProcessCollisionEvents();
    }

    /// <summary>Geocentric ELP-2000 offset of the Moon in world units — exactly the
    /// vector the pre-sandbox renderer computed (artistic radius in compressed mode,
    /// true km in real-scale mode). Shared by the Ephemeris path and Compare ghosts.</summary>
    private static Vector3 EphemerisMoonOffsetWorld(double simDays)
    {
        var lunar = LunarEphemeris.Compute(simDays);
        double lonRad = lunar.LongitudeDeg * OrbitalMechanics.DegToRad;
        double latRad = lunar.LatitudeDeg * OrbitalMechanics.DegToRad;
        double cosB = Math.Cos(latRad);
        // Geocentric ecliptic Cartesian (km), then mapped to world (x, z, -y).
        double mx = lunar.DistanceKm * cosB * Math.Cos(lonRad);
        double my = lunar.DistanceKm * cosB * Math.Sin(lonRad);
        double mz = lunar.DistanceKm * Math.Sin(latRad);
        Vector3 moonOffsetKm = new((float)mx, (float)mz, (float)-my);

        if (OrbitalMechanics.RealScale)
            return moonOffsetKm * (float)OrbitalMechanics.KmToWorldRealScale;
        Vector3 dir = moonOffsetKm.LengthSquared > 1e-6f
            ? Vector3.Normalize(moonOffsetKm)
            : Vector3.UnitX;
        return dir * MoonOrbitRadius;
    }

    /// <summary>Planetocentric offset of a Galilean / Titan in world units from the
    /// Meeus mean longitudes (Galileans) or uniform circular motion (Titan) — the
    /// pre-sandbox renderer's construction, unchanged.</summary>
    private static Vector3 EphemerisSatelliteOffsetWorld(Moon m,
        (double Io, double Europa, double Ganymede, double Callisto) galilean, double simDays)
    {
        const double TwoPi = Math.PI * 2.0;
        float r = OrbitalMechanics.RealScale
            ? (float)(m.RealOrbitRadiusKm * OrbitalMechanics.KmToWorldRealScale)
            : m.ArtisticOrbitRadius;

        double angle;
        switch (m.Body.Name)
        {
            case "Io":       angle = galilean.Io       * OrbitalMechanics.DegToRad; break;
            case "Europa":   angle = galilean.Europa   * OrbitalMechanics.DegToRad; break;
            case "Ganymede": angle = galilean.Ganymede * OrbitalMechanics.DegToRad; break;
            case "Callisto": angle = galilean.Callisto * OrbitalMechanics.DegToRad; break;
            default: // Titan and any future non-Galilean satellite.
                angle = (simDays / m.OrbitalPeriodDays) * TwoPi
                        + m.PhaseDeg * OrbitalMechanics.DegToRad;
                break;
        }

        // Negate the Z component so the moon orbits prograde (CCW as viewed from
        // the host's north pole, +Y), matching every real major satellite in the
        // solar system. Without the flip the (cos, sin) parametrisation runs
        // clockwise in our world-axis convention (X=east, -Z=north).
        float cx = (float)Math.Cos(angle) * r;
        float cz = -(float)Math.Sin(angle) * r;
        float incl = MathHelper.DegreesToRadians(m.OrbitInclinationDeg);
        float cy = cz * MathF.Sin(incl);
        cz *= MathF.Cos(incl);
        return new Vector3(cx, cy, cz);
    }

    /// <summary>Map an integrated planetocentric offset (AU) to world units: true km in
    /// real-scale mode; in compressed mode the artistic radius stretched by the ratio of
    /// the current distance to the real orbit radius, so eccentricity and escape stay visible.</summary>
    private static Vector3 PhysicsSatelliteOffsetWorld(Vector3d offAU, float artisticRadius, double realOrbitKm)
    {
        Vector3 km = new((float)(offAU.X * PhysicsWorld.AuKm), (float)(offAU.Y * PhysicsWorld.AuKm), (float)(offAU.Z * PhysicsWorld.AuKm));
        if (OrbitalMechanics.RealScale) return km * (float)OrbitalMechanics.KmToWorldRealScale;
        float len = km.Length;
        if (len < 1e-3f) return Vector3.Zero;
        return km / len * (artisticRadius * (float)(len / realOrbitKm));
    }

    /// <summary>Compare mode: where every integrated body would be on the analytic path.</summary>
    private void ComputeGhostPositions((double Io, double Europa, double Ganymede, double Callisto) galilean)
    {
        for (int pi = 0; pi < _planets.Length; pi++)
        {
            var p = _planets[pi];
            var h = OrbitalMechanics.HeliocentricPosition(p, _simDays);
            float s = OrbitalMechanics.OrbitWorldScale(p.SemiMajorAxisAU);
            _ghostPlanets[pi] = new Vector3((float)(h.X * s), (float)(h.Y * s), (float)(h.Z * s));
        }
        _ghostMoon = _ghostPlanets[2] + EphemerisMoonOffsetWorld(_simDays);
        for (int mi = 0; mi < _moons.Length; mi++)
            _ghostMoons[mi] = _ghostPlanets[_moons[mi].HostPlanetIndex] + EphemerisSatelliteOffsetWorld(_moons[mi], galilean, _simDays);
        for (int ci = 0; ci < _comets.All.Length; ci++)
        {
            var b = _comets.All[ci].Body;
            var h = OrbitalMechanics.HeliocentricPosition(b, _simDays);
            float s = OrbitalMechanics.OrbitWorldScale(b.SemiMajorAxisAU);
            _ghostComets[ci] = new Vector3((float)(h.X * s), (float)(h.Y * s), (float)(h.Z * s));
        }
    }

    private void DrawGhosts()
    {
        const float alpha = 0.3f;
        var link = new Vector4(1f, 0.65f, 0.3f, 0.85f);
        void Ghost(Planet p, Vector3 ghostPos)
        {
            _renderer.DrawPlanetGhost(_camera, p, Vector3.Zero, ghostPos, alpha);
            float len = (p.Position - ghostPos).Length;
            if (len > p.VisualRadius * 0.05f)
                _renderer.DrawDashedLine(_camera, ghostPos, p.Position, link, MathF.Max(len / 24f, 1e-4f));
        }
        for (int pi = 0; pi < _planets.Length; pi++)
        {
            if (!_showDwarfs && pi >= _dwarfStart) break;
            if (_worldPlanetIdx[pi] >= 0 && WorldAlive(_worldPlanetIdx[pi])) Ghost(_planets[pi], _ghostPlanets[pi]);
        }
        if (_worldMoonIdx >= 0 && WorldAlive(_worldMoonIdx)) Ghost(_moon, _ghostMoon);
        for (int mi = 0; mi < _moons.Length; mi++)
            if (_worldMoonsIdx[mi] >= 0 && WorldAlive(_worldMoonsIdx[mi])) Ghost(_moons[mi].Body, _ghostMoons[mi]);
        for (int ci = 0; ci < _comets.All.Length; ci++)
            if (_worldCometIdx[ci] >= 0 && WorldAlive(_worldCometIdx[ci])) Ghost(_comets.All[ci].Body, _ghostComets[ci]);
    }

    /// <summary>World index of the body the diagnostics card should describe: the
    /// selected body, else the focused one, else Earth.</summary>
    private int PhysicsHudBodyIndex()
    {
        var b = GetBody(_selectedIndex) ?? GetBody(_focusIndex);
        int idx = b != null ? _world.IndexOf(b.Name) : -1;
        if (idx < 0 && _planets.Length > 2) idx = _worldPlanetIdx[2];
        return idx;
    }

    private void DrawPhysicsHud(float x, float y)
    {
        if (!_world.Ready) return;
        var c = _physConst;
        var sb = new System.Text.StringBuilder();
        sb.Append(Localization.T("ui.physics.hud.step", _world.CurrentGlobalStepDays)).Append('\n');
        sb.Append(Localization.T("ui.physics.hud.steps", _world.LastGlobalSteps, _world.LastSatelliteSteps, _belt.LastPhysicsSteps)).Append('\n');
        double dE = _world.EnergyDrift();
        var L = _world.AngularMomentum();
        var L0 = _world.InitialAngularMomentum;
        double dL = L0.Length > 0 ? (L - L0).Length / L0.Length : 0.0;
        sb.Append(Localization.T("ui.physics.hud.energy", dE.ToString("+0.0e-0;-0.0e-0", System.Globalization.CultureInfo.InvariantCulture),
            dL.ToString("0.0e-0", System.Globalization.CultureInfo.InvariantCulture))).Append('\n');
        sb.Append(Localization.T("ui.physics.hud.constants", c.G, c.SunMassScale, c.GravityExponent, c.SpeedOfLightScale)).Append('\n');
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var hits = _world.Collisions;
        sb.Append(Localization.T("ui.physics.hud.collisions", hits.Count,
            Localization.T(_collisionsEnabled ? "ui.on" : "ui.off"))).Append('\n');
        for (int k = hits.Count - 1, shown = 0; k >= 0 && shown < 3; k--, shown++)
        {
            var ev = hits[k];
            sb.Append(Localization.T("ui.physics.hud.collision",
                OrbitalMechanics.J2000.AddDays(ev.TimeDays).ToString("yyyy-MM-dd"),
                BodyDisplayName(ev.AbsorbedName), BodyDisplayName(ev.SurvivorName),
                ev.RelativeSpeedAUPerDay * PhysicsWorld.AuKm / 86400.0,
                (ev.ImpactEnergy * PhysicsWorld.EnergyUnitJoules).ToString("0.0e0", inv))).Append('\n');
        }
        int bi = PhysicsHudBodyIndex();
        if (bi >= 0)
        {
            var body = _world.Bodies[bi];
            var el = _world.OsculatingElements(bi);
            string primary = body.Parent >= 0 ? BodyDisplayName(_world.Bodies[body.Parent].Name) : Localization.T("ui.body.sun");
            if (!body.Alive)
                sb.Append(Localization.T("ui.physics.hud.absorbed", body.Name,
                    BodyDisplayName(_world.Bodies[body.AbsorbedBy].Name),
                    OrbitalMechanics.J2000.AddDays(body.AbsorbedAtDays).ToString("yyyy-MM-dd")));
            else if (!el.Bound)
                sb.Append(Localization.T("ui.physics.hud.unbound", body.Name, primary));
            else if (body.Parent >= 0)
                sb.Append(Localization.T("ui.physics.hud.elements.km", body.Name, primary,
                    el.SemiMajorAxisAU * PhysicsWorld.AuKm, el.Eccentricity, el.PeriodDays));
            else
                sb.Append(Localization.T("ui.physics.hud.elements.au", body.Name, primary,
                    el.SemiMajorAxisAU, el.Eccentricity, el.PeriodDays));
        }
        _renderer.DrawText(_font, sb.ToString(), x, y, 12f, new Vector4(0.7f, 1f, 0.85f, 0.95f));
    }

    /// <summary>R3: derive the sky shader's brightness/saturation from the camera's
    /// position. Far from the Sun → dimmer (deep space); close to a body's surface
    /// → richer colour. Thresholds adapt to the current scale mode so the same
    /// "feel" carries over between compressed and real-scale layouts.</summary>
    private void UpdateAdaptiveStars(Planet[] visible)
    {
        float distSun = _camera.Eye.Length;
        float closestSurface = MathF.Max(0f, distSun - SunRadius);
        foreach (var p in visible)
            closestSurface = MathF.Min(closestSurface,
                MathF.Max(0f, (_camera.Eye - p.Position).Length - p.VisualRadius));
        foreach (var b in _extraBodies)
            closestSurface = MathF.Min(closestSurface,
                MathF.Max(0f, (_camera.Eye - b.Position).Length - b.VisualRadius));

        // Dim with distance from the Sun. Reference radii scale with the current
        // scale mode so Neptune sits near the "deep space" end in either layout.
        float brightNear = OrbitalMechanics.RealScale ? 50f   : 30f;
        float brightFar  = OrbitalMechanics.RealScale ? 1800f : 350f;
        float t = MathHelper.Clamp((distSun - brightNear) / (brightFar - brightNear), 0f, 1f);
        _renderer.StarsBrightness = MathHelper.Lerp(0.85f, 0.30f, t);

        // Saturation boost when hugging a planet's surface.
        float satNear = OrbitalMechanics.RealScale ? 0.05f : 1.5f;
        float satFar  = OrbitalMechanics.RealScale ? 5.0f  : 30f;
        float ts = MathHelper.Clamp((closestSurface - satNear) / (satFar - satNear), 0f, 1f);
        _renderer.StarsSaturation = MathHelper.Lerp(1.6f, 0.85f, ts);
    }

    /// <summary>V8: collect every opaque body that could cast a shadow on another
    /// body and hand it to the renderer. Capped at 16 (renderer-side limit); we
    /// prefer the Moon and the major moons since they're responsible for the most
    /// dramatic eclipses (lunar/solar, Galilean transits).</summary>
    private void BuildShadowCasters(Planet[] visible)
    {
        var spheres = new Vector4[16];
        int n = 0;
        // Moons first so they aren't crowded out if the cap is reached.
        if (n < spheres.Length && WorldAlive(_worldMoonIdx)) spheres[n++] = new Vector4(_moon.Position, _moon.VisualRadius);
        for (int mi = 0; mi < _moons.Length; mi++)
            if (n < spheres.Length && WorldAlive(_worldMoonsIdx[mi])) spheres[n++] = new Vector4(_moons[mi].Body.Position, _moons[mi].Body.VisualRadius);
        foreach (var p in visible)
            if (n < spheres.Length) spheres[n++] = new Vector4(p.Position, p.VisualRadius);
        _renderer.SetShadowCasters(spheres.AsSpan(0, n));
    }

    /// <summary>Build a host-orbiting moon: load its texture, set its initial spin
    /// to match the host system's tidal locking convention, and wrap the resulting
    /// <see cref="Planet"/> with the orbital parameters needed by the per-frame
    /// position update in <see cref="OnUpdateFrame"/>.</summary>
    /// <summary>S13: enumerate every tidally-locked moon together with its host
    /// planet. The Moon (host = Earth), the Galileans (host = Jupiter) and Titan
    /// (host = Saturn) are all spin-locked, so a single hub-axis arrow toward
    /// the host correctly indicates their permanent near-side orientation.</summary>
    private IEnumerable<(Planet moon, Planet host)> EnumerateTidalPairs()
    {
        if (WorldAlive(_worldMoonIdx) && WorldAlive(_worldPlanetIdx[2])) yield return (_moon, _planets[2]);
        for (int mi = 0; mi < _moons.Length; mi++)
        {
            var m = _moons[mi];
            if (WorldAlive(_worldMoonsIdx[mi]) && WorldAlive(_worldPlanetIdx[m.HostPlanetIndex]))
                yield return (m.Body, _planets[m.HostPlanetIndex]);
        }
    }

    private static Moon CreateMoon(string name, int hostIndex,
                                   double realRadiusKm, Vector3 color, string texture,
                                   float visualRadius, float axisTiltDeg, double rotationHours,
                                   double orbitKm, float artistic, double periodDays,
                                   float inclDeg, double phaseDeg)
    {
        var body = new Planet
        {
            Name = name,
            VisualRadius = visualRadius,
            RealRadiusKm = realRadiusKm,
            ProceduralColor = color,
            TextureFile = texture,
            AxisTiltDeg = axisTiltDeg,
            RotationPeriodHours = rotationHours,
            OrbitalPeriodYears = periodDays / 365.25,
            SemiMajorAxisAU = orbitKm / 1.495978707e8,
        };
        body.TextureId = TextureManager.LoadOrProcedural(
            body.TextureFile,
            (byte)(body.ProceduralColor.X * 255),
            (byte)(body.ProceduralColor.Y * 255),
            (byte)(body.ProceduralColor.Z * 255),
            out body.TextureFromFile);
        return new Moon(body, hostIndex, orbitKm, artistic, periodDays, inclDeg, phaseDeg);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_palette.Active)
        {
            _palette.AppendText(e.AsString, _registry);
            return;
        }
        if (_seekActive)
        {
            if (_seekSwallowNextChar) { _seekSwallowNextChar = false; return; }
            if (_seekBuffer.Length < 32)
                _seekBuffer += e.AsString;
            return;
        }
        if (_searchActive)
        {
            if (_searchSwallowNextChar) { _searchSwallowNextChar = false; return; }
            if (_searchBuffer.Length < 32)
                _searchBuffer += e.AsString;
        }
    }

    /// <summary>Parse the date-seek buffer and update <see cref="_simDays"/>.
    /// Accepts an absolute date (any format <see cref="DateTime.TryParse(string, out DateTime)"/>
    /// understands) or a signed integer day delta (e.g. "+30", "-365").</summary>
    /// <summary>Q10: handle Ctrl+1..9 (record) and Ctrl+Shift+1..9 (clear) for
    /// camera-path waypoints. Returns true when the keystroke was consumed so
    /// the caller can early-out before the regular digit-focus shortcut runs.</summary>
    private bool TryHandleCameraPathKey(KeyboardKeyEventArgs e)
    {
        int slot = e.Key switch
        {
            Keys.D1 or Keys.KeyPad1 => 1,
            Keys.D2 or Keys.KeyPad2 => 2,
            Keys.D3 or Keys.KeyPad3 => 3,
            Keys.D4 or Keys.KeyPad4 => 4,
            Keys.D5 or Keys.KeyPad5 => 5,
            Keys.D6 or Keys.KeyPad6 => 6,
            Keys.D7 or Keys.KeyPad7 => 7,
            Keys.D8 or Keys.KeyPad8 => 8,
            Keys.D9 or Keys.KeyPad9 => 9,
            _ => 0,
        };
        if (slot == 0) return false;
        if ((e.Modifiers & KeyModifiers.Shift) != 0)
        {
            _camPath.Clear(slot);
            _seekFeedback = $"Waypoint {slot} cleared";
        }
        else
        {
            _camPath.Record(slot, _camera);
            _audio.PlayTick();
            _seekFeedback = $"Waypoint {slot} recorded ({_camPath.Count} total)";
        }
        _seekFeedbackUntil = GLFW.GetTime() + 2.0;
        return true;
    }

    // -------- Feature registry: the one list every menu / key / save derives from --

    /// <summary>Populate <see cref="_registry"/>. Each entry's <c>Set</c> carries
    /// every side-effect the old hotkey switch used to perform (trail clears,
    /// integrator resync, focus fix-ups) so the panel, the palette, presets and
    /// the persisted-state loader all behave identically to the keyboard.
    /// Registration order matters in two places: it is the display order in the
    /// panel / palette / help, and it is the order <see cref="FeatureRegistry.Restore"/>
    /// applies saved values — so real-scale goes first (camera limits depend on it).</summary>
    private void BuildFeatureRegistry()
    {
        var r = _registry;

        // ---- Simulation ----------------------------------------------------------------
        r.Add(new Feature
        {
            Id = "realscale", Category = FeatureCategory.Simulation, LabelKey = "ui.settings.realscale",
            Get = () => OrbitalMechanics.RealScale,
            Set = v => { if (v != OrbitalMechanics.RealScale) ToggleRealScale(); },
            Default = false, LegacyKey = "RealScale",
            Banner = v => Localization.T(v ? "ui.scale.banner.real" : "ui.scale.banner.compressed"),
        }).WithKey(Keys.R);
        r.Add(new Feature
        {
            Id = "pause", Category = FeatureCategory.Simulation, LabelKey = "ui.settings.pause",
            Get = () => _paused, Set = v => _paused = v, Default = false, LegacyKey = "Paused",
            Banner = _ => "",
        }).WithKey(Keys.Space);
        r.Add(new Feature
        {
            Id = "lighttime", Category = FeatureCategory.Simulation, LabelKey = "ui.settings.lighttime",
            Get = () => _lightTime, Set = v => _lightTime = v, Default = false, LegacyKey = "LightTime",
            Banner = v => Localization.T(v ? "ui.lighttime.on" : "ui.lighttime.off"),
        });
        // Physics sandbox: the old boolean "nbody" switch became a three-way mode
        // selector (Ephemeris / Physics / Compare); saves with nbody=true migrate to
        // Physics in TryLoadPersistedState.
        r.Add(new Choice
        {
            Id = "simmode", Category = FeatureCategory.Simulation, LabelKey = "ui.settings.simmode",
            OptionKeys = new[] { "ui.simmode.ephemeris", "ui.simmode.physics", "ui.simmode.compare" },
            Get = () => (int)_simMode,
            Set = v => SetSimulationMode((SimulationMode)v),
            Default = (int)SimulationMode.Ephemeris,
            Banner = v => Localization.T("ui.simmode.banner", Localization.T("ui.simmode." + ((SimulationMode)v).ToString().ToLowerInvariant())),
        });
        Func<string?> physicsOnly = () => _simMode == SimulationMode.Ephemeris ? "ui.unavailable.ephemeris" : null;
        r.Add(new Slider
        {
            Id = "physics.g", Category = FeatureCategory.Simulation, LabelKey = "ui.settings.physics.g",
            Get = () => _physConst.G, Set = v => { _physConst.G = v; _world.RefreshMasses(); },
            Min = PhysicsConstants.GMin, Max = PhysicsConstants.GMax, Step = 1.1220184543, LogScale = true,
            Default = 1.0, Format = "×{0:0.###}", Unavailable = physicsOnly,
        });
        r.Add(new Slider
        {
            Id = "physics.sunmass", Category = FeatureCategory.Simulation, LabelKey = "ui.settings.physics.sunmass",
            Get = () => _physConst.SunMassScale, Set = v => { _physConst.SunMassScale = v; _world.RefreshMasses(); },
            Min = PhysicsConstants.SunMassMin, Max = PhysicsConstants.SunMassMax, Step = 1.1220184543, LogScale = true,
            Default = 1.0, Format = "×{0:0.###}", Unavailable = physicsOnly,
        });
        r.Add(new Slider
        {
            Id = "physics.exponent", Category = FeatureCategory.Simulation, LabelKey = "ui.settings.physics.exponent",
            Get = () => _physConst.GravityExponent, Set = v => _physConst.GravityExponent = v,
            Min = PhysicsConstants.ExponentMin, Max = PhysicsConstants.ExponentMax, Step = 0.01,
            Default = 2.0, Format = "{0:0.00}", Unavailable = physicsOnly,
        });
        r.Add(new Slider
        {
            Id = "physics.lightspeed", Category = FeatureCategory.Simulation, LabelKey = "ui.settings.physics.lightspeed",
            Get = () => _physConst.SpeedOfLightScale, Set = v => _physConst.SpeedOfLightScale = v,
            Min = PhysicsConstants.LightSpeedMin, Max = PhysicsConstants.LightSpeedMax, Step = 1.1220184543, LogScale = true,
            Default = 1.0, Format = "×{0:0.###}", Unavailable = physicsOnly,
        });
        r.Add(new Command
        {
            Id = "physics.resetconst", Category = FeatureCategory.Simulation, LabelKey = "ui.cmd.physics.resetconst",
            Run = () => { _physConst.Reset(); _world.RefreshMasses(); ShowBanner(Localization.T("ui.physics.const.reset"), 2.0); },
            ClosesPalette = false, ShowInPanel = true, Unavailable = physicsOnly,
        });
        r.Add(new Command
        {
            Id = "physics.reinit", Category = FeatureCategory.Simulation, LabelKey = "ui.cmd.physics.reinit",
            Run = RestartPhysicsFromEphemeris, ClosesPalette = false, ShowInPanel = true, Unavailable = physicsOnly,
        });
        r.Add(new Feature
        {
            Id = "physics.hud", Category = FeatureCategory.Simulation, LabelKey = "ui.settings.physics.hud",
            Get = () => _showPhysicsHud, Set = v => _showPhysicsHud = v, Default = false,
            Unavailable = physicsOnly, Banner = _ => "",
        });
        r.Add(new Feature
        {
            Id = "physics.collisions", Category = FeatureCategory.Simulation, LabelKey = "ui.settings.physics.collisions",
            Get = () => _collisionsEnabled,
            Set = v => { _collisionsEnabled = v; _world.CollisionsEnabled = v; },
            Default = true, Unavailable = physicsOnly,
            Banner = v => Localization.T(v ? "ui.physics.collisions.on" : "ui.physics.collisions.off"),
        });
        // Masses tab: one logarithmic slider per massive body (the Sun lives in the
        // Simulation tab as "Sun mass").
        foreach (var body in _world.Bodies)
        {
            if (!body.IsMassive || body.Kind == PhysicsBodyKind.Star) continue;
            string name = body.Name;
            var worldBody = body;
            r.Add(new Slider
            {
                Id = "mass." + name.ToLowerInvariant(), Category = FeatureCategory.Masses, LabelKey = "ui.settings.mass",
                LabelFn = () => Localization.T("ui.settings.mass", name),
                DescKey = "ui.desc.mass",
                Get = () => _physConst.GetBodyMassScale(name),
                Set = v => { _physConst.SetBodyMassScale(name, v); _world.RefreshMasses(); },
                Min = PhysicsConstants.BodyMassMin, Max = PhysicsConstants.BodyMassMax, Step = 1.1220184543, LogScale = true,
                Default = 1.0, Format = "×{0:0.###}",
                Unavailable = () => physicsOnly() ?? (worldBody.Alive ? null : "ui.unavailable.absorbed"),
            });
        }
        r.Add(new Command
        {
            Id = "physics.resetmasses", Category = FeatureCategory.Masses, LabelKey = "ui.cmd.physics.resetmasses",
            Run = () => { _physConst.ResetMasses(); _world.RefreshMasses(); ShowBanner(Localization.T("ui.physics.masses.reset"), 2.0); },
            ClosesPalette = false, ShowInPanel = true, Unavailable = physicsOnly,
        });
        r.Add(new Feature
        {
            Id = "meteors", Category = FeatureCategory.Simulation, LabelKey = "ui.settings.meteors",
            Get = () => _showMeteors, Set = v => _showMeteors = v, Default = true, LegacyKey = "ShowMeteors",
            Banner = v =>
            {
                if (!v) return Localization.T("ui.meteors.off");
                if (_meteors.ActiveShowerName.Length > 0)
                    return Localization.T("ui.meteors.on.active", _meteors.ActiveShowerName);
                var next = _meteors.NextPeak(_simDays);
                return next is { } n
                    ? Localization.T("ui.meteors.on.next", n.Name, n.DaysUntil)
                    : Localization.T("ui.meteors.on");
            },
        });
        r.Add(new Command
        {
            Id = "speed.up", Category = FeatureCategory.Simulation, LabelKey = "ui.cmd.speedup",
            Run = () => ScaleSpeed(1.5), ClosesPalette = false,
            Status = () => $"{Math.Abs(_daysPerSecond):0.##} d/s",
        }).WithKey(Keys.Equal).WithKey(Keys.KeyPadAdd);
        r.Add(new Command
        {
            Id = "speed.down", Category = FeatureCategory.Simulation, LabelKey = "ui.cmd.speeddown",
            Run = () => ScaleSpeed(1.0 / 1.5), ClosesPalette = false,
            Status = () => $"{Math.Abs(_daysPerSecond):0.##} d/s",
        }).WithKey(Keys.Minus).WithKey(Keys.KeyPadSubtract);
        r.Add(new Command
        {
            Id = "time.reverse", Category = FeatureCategory.Simulation, LabelKey = "ui.cmd.reverse",
            Run = () => SetDirection(backward: true), ClosesPalette = false,
        }).WithKey(Keys.Comma);
        r.Add(new Command
        {
            Id = "time.forward", Category = FeatureCategory.Simulation, LabelKey = "ui.cmd.forward",
            Run = () => SetDirection(backward: false), ClosesPalette = false,
        }).WithKey(Keys.Period);

        // ---- Bodies ----------------------------------------------------------------------
        r.Add(new Feature
        {
            Id = "orbits", Category = FeatureCategory.Bodies, LabelKey = "ui.settings.orbits",
            Get = () => _showOrbits, Set = v => _showOrbits = v, Default = true, LegacyKey = "ShowOrbits",
        }).WithKey(Keys.O);
        r.Add(new Feature
        {
            Id = "labels", Category = FeatureCategory.Bodies, LabelKey = "ui.settings.labels",
            Get = () => _showLabels, Set = v => _showLabels = v, Default = true, LegacyKey = "ShowLabels",
        }).WithKey(Keys.L);
        r.Add(new Feature
        {
            Id = "trails", Category = FeatureCategory.Bodies, LabelKey = "ui.settings.trails",
            Get = () => _showTrails,
            Set = v => { _showTrails = v; if (!v) ClearAllTrails(); },
            Default = true, LegacyKey = "ShowTrails",
        }).WithKey(Keys.T);
        r.Add(new Feature
        {
            Id = "axes", Category = FeatureCategory.Bodies, LabelKey = "ui.settings.axes",
            Get = () => _showAxes, Set = v => _showAxes = v, Default = false, LegacyKey = "ShowAxes",
        });
        r.Add(new Feature
        {
            Id = "dwarfs", Category = FeatureCategory.Bodies, LabelKey = "ui.settings.dwarfs",
            Get = () => _showDwarfs,
            Set = v =>
            {
                _showDwarfs = v;
                // If a dwarf was the active focus / selection, drop back to the Sun so
                // the camera doesn't keep tracking an invisible body.
                if (!v)
                {
                    if (_focusIndex >= _dwarfStart && _focusIndex < _planets.Length) FocusOn(-1);
                    if (_selectedIndex >= _dwarfStart && _selectedIndex < _planets.Length) _selectedIndex = -2;
                    // Clear stale dwarf trails so they don't reappear as a frozen line strip
                    // on the next toggle-on.
                    for (int i = _dwarfStart; i < _planets.Length; i++) _planets[i].TrailReset();
                }
            },
            Default = true, LegacyKey = "ShowDwarfs",
        });
        r.Add(new Feature
        {
            Id = "probes", Category = FeatureCategory.Bodies, LabelKey = "ui.settings.probes",
            Get = () => _showProbes, Set = v => _showProbes = v, Default = true, LegacyKey = "ShowProbes",
        });
        r.Add(new Feature
        {
            Id = "lagrange", Category = FeatureCategory.Bodies, LabelKey = "ui.settings.lagrange",
            Get = () => _showLagrange, Set = v => _showLagrange = v, Default = false, LegacyKey = "ShowLagrange",
        });
        r.Add(new Feature
        {
            Id = "constellations", Category = FeatureCategory.Bodies, LabelKey = "ui.settings.constellations",
            Get = () => _showConstellations,
            Set = v => { _showConstellations = v; _constellations.Enabled = v; },
            Default = false, LegacyKey = "ShowConstellations",
        });
        r.Add(new Feature
        {
            Id = "tidal", Category = FeatureCategory.Bodies, LabelKey = "ui.settings.tidal",
            Get = () => _showTidalLock, Set = v => _showTidalLock = v, Default = false, LegacyKey = "ShowTidalLock",
            Banner = v => Localization.T(v ? "ui.tidal.on" : "ui.tidal.off"),
        });
        r.Add(new Feature
        {
            Id = "alignment", Category = FeatureCategory.Bodies, LabelKey = "ui.settings.alignment",
            Get = () => _showAlignment, Set = v => _showAlignment = v, Default = true, LegacyKey = "ShowAlignment",
            Banner = v => Localization.T(v ? "ui.alignment.on" : "ui.alignment.off"),
        });
        r.Add(new Command
        {
            Id = "focus.sun", Category = FeatureCategory.Bodies, LabelKey = "ui.cmd.focus.sun",
            Run = () => { _selectedIndex = -1; FocusOn(-1); }, HideInHelp = true,
        }).WithKey(Keys.D0).WithKey(Keys.KeyPad0);
        for (int i = 0; i < 8; i++)
        {
            int idx = i;
            r.Add(new Command
            {
                Id = "focus." + (i + 1), Category = FeatureCategory.Bodies, LabelKey = "ui.cmd.focus",
                LabelFn = () => Localization.T("ui.cmd.focus", _planets[idx].Name),
                Run = () => { FocusOn(idx); _selectedIndex = idx; }, HideInHelp = true,
            }).WithKey(Keys.D1 + i).WithKey(Keys.KeyPad1 + i);
        }

        // ---- Effects ---------------------------------------------------------------------
        r.Add(new Feature
        {
            Id = "solarwind", Category = FeatureCategory.Effects, LabelKey = "ui.settings.solarwind",
            Get = () => _solarWind.Enabled, Set = v => _solarWind.Enabled = v, Default = true, LegacyKey = "SolarWindEnabled",
        });
        r.Add(new Feature
        {
            Id = "solarflares", Category = FeatureCategory.Effects, LabelKey = "ui.settings.solarflares",
            Get = () => _solarFlares.Enabled, Set = v => _solarFlares.Enabled = v, Default = true, LegacyKey = "SolarFlaresEnabled",
        });
        r.Add(new Feature
        {
            Id = "corona", Category = FeatureCategory.Effects, LabelKey = "ui.settings.corona",
            Get = () => _renderer.CoronaEnabled, Set = v => _renderer.CoronaEnabled = v, Default = true, LegacyKey = "CoronaEnabled",
        });
        r.Add(new Feature
        {
            Id = "aurora", Category = FeatureCategory.Effects, LabelKey = "ui.settings.aurora",
            Get = () => _showAurora, Set = v => { _showAurora = v; _aurora.Enabled = v; }, Default = true, LegacyKey = "ShowAurora",
        });
        r.Add(new Feature
        {
            Id = "atmosphere", Category = FeatureCategory.Effects, LabelKey = "ui.settings.atmosphere",
            Get = () => _renderer.AtmosphereEnabled, Set = v => _renderer.AtmosphereEnabled = v, Default = true, LegacyKey = "AtmosphereEnabled",
        });
        r.Add(new Feature
        {
            Id = "eclipses", Category = FeatureCategory.Effects, LabelKey = "ui.settings.eclipses",
            Get = () => _renderer.EclipsesEnabled, Set = v => _renderer.EclipsesEnabled = v, Default = true, LegacyKey = "EclipsesEnabled",
        });
        r.Add(new Feature
        {
            Id = "pbr", Category = FeatureCategory.Effects, LabelKey = "ui.settings.pbr",
            Get = () => _renderer.PbrEnabled, Set = v => _renderer.PbrEnabled = v, Default = true, LegacyKey = "PbrEnabled",
        });
        r.Add(new Feature
        {
            Id = "oceanmask", Category = FeatureCategory.Effects, LabelKey = "ui.settings.oceanmask",
            Get = () => _renderer.OceanMaskEnabled, Set = v => _renderer.OceanMaskEnabled = v, Default = true, LegacyKey = "OceanMaskEnabled",
            Unavailable = () => _planets[2].OceanMaskTextureId == 0 ? "ui.unavailable.texture" : null,
        });

        // ---- Post-processing -------------------------------------------------------------
        r.Add(new Feature
        {
            Id = "bloom", Category = FeatureCategory.PostFx, LabelKey = "ui.settings.bloom",
            Get = () => _renderer.BloomEnabled, Set = v => _renderer.BloomEnabled = v, Default = true, LegacyKey = "BloomEnabled",
        });
        r.Add(new Feature
        {
            Id = "autoexposure", Category = FeatureCategory.PostFx, LabelKey = "ui.settings.autoexposure",
            Get = () => _renderer.AutoExposureEnabled, Set = v => _renderer.AutoExposureEnabled = v, Default = true, LegacyKey = "AutoExposureEnabled",
        });
        r.Add(new Feature
        {
            Id = "fxaa", Category = FeatureCategory.PostFx, LabelKey = "ui.settings.fxaa",
            Get = () => _renderer.FxaaEnabled, Set = v => _renderer.FxaaEnabled = v, Default = true, LegacyKey = "FxaaEnabled",
        });
        r.Add(new Feature
        {
            Id = "lensflare", Category = FeatureCategory.PostFx, LabelKey = "ui.settings.lensflare",
            Get = () => _renderer.LensFlareEnabled, Set = v => _renderer.LensFlareEnabled = v, Default = true, LegacyKey = "LensFlareEnabled",
            Banner = v => Localization.T(v ? "ui.lensflare.on" : "ui.lensflare.off"),
        });

        // ---- Interface -------------------------------------------------------------------
        r.Add(new Feature
        {
            Id = "settings", Category = FeatureCategory.Interface, LabelKey = "ui.settings.settings",
            Get = () => _settings.Visible, Set = v => _settings.Visible = v, Default = false, LegacyKey = "SettingsVisible",
            Banner = _ => "",
        }).WithKey(Keys.F1);
        r.Add(new Command
        {
            Id = "palette", Category = FeatureCategory.Interface, LabelKey = "ui.cmd.palette",
            Run = () => _palette.Open(_registry), HideInPalette = true,
        }).WithKey(Keys.K, KeyModifiers.Control);
        r.Add(new Feature
        {
            Id = "toolbar", Category = FeatureCategory.Interface, LabelKey = "ui.settings.toolbar",
            Get = () => _toolbar.Visible, Set = v => _toolbar.Visible = v, Default = true,
        });
        r.Add(new Feature
        {
            Id = "hud", Category = FeatureCategory.Interface, LabelKey = "ui.settings.hud",
            Get = () => _showHud, Set = v => _showHud = v, Default = false, LegacyKey = "ShowHud",
            Banner = _ => "",
        }).WithKey(Keys.GraveAccent);
        r.Add(new Feature
        {
            Id = "timeline", Category = FeatureCategory.Interface, LabelKey = "ui.settings.timeline",
            Get = () => _scrubber.Visible, Set = v => _scrubber.Visible = v, Default = false, LegacyKey = "ScrubberVisible",
        });
        r.Add(new Feature
        {
            Id = "bookmarks", Category = FeatureCategory.Interface, LabelKey = "ui.settings.bookmarks",
            Get = () => _bookSidebar.Visible, Set = v => _bookSidebar.Visible = v, Default = false, LegacyKey = "BookmarksVisible",
            Banner = _ => "",
        }).WithKey(Keys.F3);
        r.Add(new Feature
        {
            Id = "audio", Category = FeatureCategory.Interface, LabelKey = "ui.settings.audio",
            Get = () => _audio.Enabled, Set = v => _audio.Enabled = v, Default = false, LegacyKey = "AudioEnabled",
            Banner = v => { if (v) _audio.PlayTick(); return Localization.T(v ? "ui.audio.on" : "ui.audio.off"); },
        });
        r.Add(new Feature
        {
            Id = "fullscreen", Category = FeatureCategory.Interface, LabelKey = "ui.settings.fullscreen",
            Get = () => _fullscreen, Set = SetFullscreen, Default = false, LegacyKey = "Fullscreen",
            Banner = _ => "", // SetFullscreen raises its own banner.
        }).WithKey(Keys.Enter, KeyModifiers.Alt);
        r.Add(new Command
        {
            Id = "help", Category = FeatureCategory.Interface, LabelKey = "ui.cmd.help",
            Run = () => _helpMode = (_helpMode + 1) % 3, ClosesPalette = false, ShowInPanel = true,
            Status = () => Localization.T("ui.help.mode." + _helpMode),
        }).WithKey(Keys.Tab);
        r.Add(new Command
        {
            Id = "language", Category = FeatureCategory.Interface, LabelKey = "ui.cmd.language",
            Run = () => ShowBanner(Localization.T("ui.lang.toggled", Localization.CycleNext())),
            ClosesPalette = false, ShowInPanel = true,
            Status = () => Localization.CurrentLanguage.ToUpperInvariant(),
        }).WithKey(Keys.F2);
        r.Add(new Command
        {
            Id = "search", Category = FeatureCategory.Interface, LabelKey = "ui.cmd.search",
            Run = () => { _searchActive = true; _searchBuffer = ""; _searchSwallowNextChar = true; },
        }).WithKey(Keys.F, KeyModifiers.Control);
        r.Add(new Command
        {
            Id = "seek", Category = FeatureCategory.Interface, LabelKey = "ui.cmd.seek",
            Run = () => { _seekActive = true; _seekBuffer = ""; _seekFeedback = ""; _seekSwallowNextChar = true; },
        }).WithKey(Keys.J);
        r.Add(new Command
        {
            Id = "screenshot", Category = FeatureCategory.Interface, LabelKey = "ui.cmd.screenshot",
            Run = SaveScreenshot, ShowInPanel = true,
        }).WithKey(Keys.F12);
        r.Add(new Command
        {
            Id = "bookmark.next", Category = FeatureCategory.Interface, LabelKey = "ui.cmd.bookmark.next",
            Run = () => JumpToBookmark(forward: true), ClosesPalette = false,
        }).WithKey(Keys.E, KeyModifiers.Control);
        r.Add(new Command
        {
            Id = "bookmark.prev", Category = FeatureCategory.Interface, LabelKey = "ui.cmd.bookmark.prev",
            Run = () => JumpToBookmark(forward: false), ClosesPalette = false,
        }).WithKey(Keys.E, KeyModifiers.Control | KeyModifiers.Shift);
        r.Add(new Command
        {
            Id = "path.play", Category = FeatureCategory.Interface, LabelKey = "ui.cmd.path.play",
            Run = () =>
            {
                if (_camPath.Play(6.0)) { _audio.PlayWhoosh(); ShowBanner(Localization.T("ui.campath.playing"), 2.5); }
                else ShowBanner(Localization.T("ui.campath.need2"), 2.5);
            },
        }).WithKey(Keys.P, KeyModifiers.Shift);
        r.Add(new Command
        {
            Id = "path.clear", Category = FeatureCategory.Interface, LabelKey = "ui.cmd.path.clear",
            Run = () => { _camPath.ClearAll(); ShowBanner(Localization.T("ui.campath.cleared"), 2.5); },
        }).WithKey(Keys.P, KeyModifiers.Control | KeyModifiers.Shift);
        for (int i = 1; i <= 9; i++)
        {
            int slot = i;
            r.Add(new Command
            {
                Id = "waypoint." + i, Category = FeatureCategory.Interface, LabelKey = "ui.cmd.waypoint",
                LabelFn = () => Localization.T("ui.cmd.waypoint", slot),
                Run = () =>
                {
                    _camPath.Record(slot, _camera);
                    _audio.PlayTick();
                    ShowBanner(Localization.T("ui.campath.recorded", slot, _camPath.Count), 2.0);
                },
                HideInHelp = true,
            }).WithKey(Keys.D0 + i, KeyModifiers.Control).WithKey(Keys.KeyPad0 + i, KeyModifiers.Control);
            r.Add(new Command
            {
                Id = "waypoint.clear." + i, Category = FeatureCategory.Interface, LabelKey = "ui.cmd.waypoint.clear",
                LabelFn = () => Localization.T("ui.cmd.waypoint.clear", slot),
                Run = () => { _camPath.Clear(slot); ShowBanner(Localization.T("ui.campath.slotcleared", slot), 2.0); },
                HideInHelp = true, HideInPalette = true,
            }).WithKey(Keys.D0 + i, KeyModifiers.Control | KeyModifiers.Shift)
              .WithKey(Keys.KeyPad0 + i, KeyModifiers.Control | KeyModifiers.Shift);
        }
        r.Add(new Command
        {
            Id = "quit", Category = FeatureCategory.Interface, LabelKey = "ui.cmd.quit",
            Run = Close, ShowInPanel = true,
        });

        // ---- Developer -------------------------------------------------------------------
        r.Add(new Feature
        {
            Id = "profiler", Category = FeatureCategory.Developer, LabelKey = "ui.settings.profiler",
            Get = () => _showProfiler, Set = v => _showProfiler = v, Default = false, LegacyKey = "ShowProfiler",
            Banner = v => Localization.T(v ? "ui.profiler.on" : "ui.profiler.off"),
        }).WithKey(Keys.F10);
        r.Add(new Feature
        {
            Id = "hotreload", Category = FeatureCategory.Developer, LabelKey = "ui.settings.hotreload",
            Get = () => ShaderSources.HotReloadEnabled, Set = ShaderSources.SetHotReload, Default = false, Persist = false,
            Banner = v => Localization.T(v ? "ui.hotreload.on" : "ui.hotreload.off"),
        });
        r.Add(new Feature
        {
            Id = "gpubelt", Category = FeatureCategory.Developer, LabelKey = "ui.settings.gpubelt",
            Get = () => _belt.UseGpuCompute, Set = v => _belt.UseGpuCompute = v, Default = true, LegacyKey = "GpuAsteroidsEnabled",
            Unavailable = () => _belt.GpuComputeAvailable ? null : "ui.gpubelt.unavailable",
            Banner = v => Localization.T(v ? "ui.gpubelt.on" : "ui.gpubelt.off"),
        });
        r.Add(new Feature
        {
            Id = "record", Category = FeatureCategory.Developer, LabelKey = "ui.settings.record",
            Get = () => _recording, Set = v => { if (v != _recording) ToggleRecording(); }, Default = false, Persist = false,
            Banner = _ => "", // ToggleRecording raises its own banners.
        }).WithKey(Keys.F9);

        // ---- Presets (scene categories only; see FeatureRegistry.ApplyPreset) ----------
        r.AddPreset(new FeaturePreset
        {
            Id = "cinematic", LabelKey = "ui.preset.cinematic",
            Overrides = new()
            {
                ["orbits"] = false, ["labels"] = false, ["probes"] = false, ["alignment"] = false,
                ["trails"] = true, ["meteors"] = true,
            },
        });
        r.AddPreset(new FeaturePreset
        {
            Id = "realistic", LabelKey = "ui.preset.realistic",
            Overrides = new()
            {
                ["realscale"] = true, ["lighttime"] = true,
                ["trails"] = false, ["solarwind"] = false, ["solarflares"] = false,
                ["lensflare"] = false, ["alignment"] = false,
            },
            Choices = new() { ["simmode"] = (int)SimulationMode.Physics },
        });
        r.AddPreset(new FeaturePreset
        {
            Id = "performance", LabelKey = "ui.preset.performance",
            Overrides = new()
            {
                ["bloom"] = false, ["fxaa"] = false, ["autoexposure"] = false, ["lensflare"] = false,
                ["aurora"] = false, ["solarwind"] = false, ["solarflares"] = false, ["corona"] = false,
                ["pbr"] = false, ["atmosphere"] = false, ["eclipses"] = false, ["oceanmask"] = false,
                ["meteors"] = false, ["trails"] = false, ["probes"] = false,
            },
        });
        r.AddPreset(new FeaturePreset
        {
            Id = "minimal", LabelKey = "ui.preset.minimal",
            Overrides = new()
            {
                ["trails"] = false, ["dwarfs"] = false, ["probes"] = false, ["alignment"] = false,
                ["meteors"] = false,
                ["solarwind"] = false, ["solarflares"] = false, ["corona"] = false, ["aurora"] = false,
                ["atmosphere"] = false, ["eclipses"] = false, ["pbr"] = false, ["oceanmask"] = false,
                ["bloom"] = false, ["autoexposure"] = false, ["fxaa"] = false, ["lensflare"] = false,
            },
        });
    }

    /// <summary>Multiply the simulation speed magnitude, keeping direction.
    /// Clamped to [0.1, 1000] d/s.</summary>
    private void ScaleSpeed(double factor)
    {
        double sign = _daysPerSecond < 0 ? -1.0 : 1.0;
        double mag = Math.Clamp(Math.Abs(_daysPerSecond) * factor, 0.1, 1000.0);
        _daysPerSecond = sign * mag;
    }

    /// <summary>Force playback direction; magnitude is preserved so toggling
    /// direction doesn't change speed. Trails are cleared on a reversal so
    /// they don't draw a stale arc.</summary>
    private void SetDirection(bool backward)
    {
        if (backward && _daysPerSecond > 0) ClearAllTrails();
        if (!backward && _daysPerSecond < 0) ClearAllTrails();
        _daysPerSecond = backward ? -Math.Abs(_daysPerSecond) : Math.Abs(_daysPerSecond);
    }

    /// <summary>S12 / Q8: snap sim time to the next (or previous) bookmark.</summary>
    private void JumpToBookmark(bool forward)
    {
        var entry = forward ? _bookmarks.Next(TargetSimDays) : _bookmarks.Prev(TargetSimDays);
        if (entry is { } ev)
        {
            SetSimTime(Bookmarks.ToSimDays(ev));
            ClearAllTrails();
            _audio.PlayTick();
            ShowBanner($"{ev.Kind}: {ev.Title} — {ev.Date:yyyy-MM-dd}", 4.0);
        }
    }

    /// <summary>Q12: build the settings panel rows from the registry. Toggle rows
    /// mirror every <see cref="Feature"/> (grouped by category tab, hotkey shown
    /// on the right, description in the footer); a speed slider heads the
    /// Simulation tab and a few commands flagged <see cref="Command.ShowInPanel"/>
    /// appear as button rows.</summary>
    private void BuildSettingsPanel()
    {
        _settings.Clear();
        _settings.Add(new SettingsPanel.SliderRow
        {
            Label  = "ui.settings.speed",
            Category = FeatureCategory.Simulation,
            Get    = () => (float)_daysPerSecond,
            Set    = v => _daysPerSecond = v,
            Min    = -1000f, Max = 1000f, Step = 1f, Format = "{0:0.##}",
            Description = () => Localization.T("ui.desc.speed"),
        });
        foreach (var e in _registry.Entries)
        {
            switch (e)
            {
                case Feature f:
                    _settings.Add(new SettingsPanel.ToggleRow
                    {
                        Label = f.LabelKey,
                        Category = f.Category,
                        Hint = f.BindingText,
                        Get = () => f.Value,
                        Toggle = () => _registry.Invoke(f, ShowBanner),
                        Description = () => f.Description,
                        Unavailable = f.Unavailable,
                    });
                    break;
                case Command c when c.ShowInPanel:
                    _settings.Add(new SettingsPanel.ButtonRow
                    {
                        Label = c.LabelKey,
                        Category = c.Category,
                        Hint = c.BindingText,
                        Run = () => _registry.Invoke(c, ShowBanner),
                        Status = c.Status,
                        Description = () => c.Description,
                        Unavailable = c.Unavailable,
                    });
                    break;
                case Choice ch:
                    _settings.Add(new SettingsPanel.ChoiceRow
                    {
                        Label = ch.LabelKey,
                        LabelFn = ch.LabelFn,
                        Category = ch.Category,
                        Hint = ch.BindingText,
                        ValueLabel = () => ch.ValueLabel,
                        Cycle = delta =>
                        {
                            if (!ch.Cycle(delta)) return;
                            string? text = ch.Banner?.Invoke(ch.Value) ?? $"{ch.Label}: {ch.ValueLabel}";
                            if (text.Length > 0) ShowBanner(text);
                        },
                        Description = () => ch.Description,
                        Unavailable = ch.Unavailable,
                    });
                    break;
                case Slider s:
                    _settings.Add(new SettingsPanel.SliderRow
                    {
                        Label = s.LabelKey,
                        LabelFn = s.LabelFn,
                        Category = s.Category,
                        Hint = s.BindingText,
                        Get = () => (float)s.Value,
                        Set = v => s.Apply(v),
                        Min = (float)s.Min, Max = (float)s.Max, Step = (float)s.Step,
                        LogScale = s.LogScale, Format = s.Format,
                        Description = () => s.Description,
                        Unavailable = s.Unavailable,
                    });
                    break;
            }
        }
        _settings.Presets = _registry.Presets;
        _settings.IsPresetActive = _registry.MatchesPreset;
        _settings.OnPreset = p =>
        {
            _registry.ApplyPreset(p);
            ShowBanner(Localization.T("ui.preset.applied", p.Label), 2.0);
        };
        _settings.OnReset = c => _registry.ResetCategory(c);
        _settings.OnSetAll = (c, v) => _registry.SetCategory(c, v);
    }

    /// <summary>Bottom-centre strip: pause, speed, direction, the four view
    /// toggles people reach for most, and the two menus.</summary>
    private void BuildToolbar()
    {
        _toolbar.Clear();
        Toolbar.Button FeatureButton(string id, Func<string>? label = null)
        {
            var f = _registry.FindFeature(id)!;
            return new Toolbar.Button
            {
                Label = label ?? (() => f.Label),
                Click = () => _registry.Invoke(f, ShowBanner),
                Active = () => f.Value,
                Tip = () => f.BindingText.Length > 0 ? $"{f.Description}  [{f.BindingText}]" : f.Description,
            };
        }
        _toolbar.Add(FeatureButton("pause", () => _paused ? "▶" : "▮▮"));
        _toolbar.Buttons[^1].MinWidth = 40f;
        _toolbar.Add(new Toolbar.Button
        {
            Label = () => "-", MinWidth = 32f,
            Click = () => ScaleSpeed(1.0 / 1.5),
            Tip = () => Localization.T("ui.desc.speed.down"),
        });
        _toolbar.Add(new Toolbar.Button
        {
            Label = () => $"{(_daysPerSecond < 0 ? "◀ " : "")}{Math.Abs(_daysPerSecond):0.##} d/s", MinWidth = 90f,
            Click = () => _daysPerSecond = _daysPerSecond < 0 ? -1.0 : 1.0,
            Tip = () => Localization.T("ui.toolbar.speed.tip"),
        });
        _toolbar.Add(new Toolbar.Button
        {
            Label = () => "+", MinWidth = 32f,
            Click = () => ScaleSpeed(1.5),
            Tip = () => Localization.T("ui.desc.speed.up"),
        });
        _toolbar.Add(new Toolbar.Button
        {
            Label = () => _daysPerSecond < 0 ? "◀◀" : "▶▶", MinWidth = 40f,
            Click = () => SetDirection(backward: _daysPerSecond > 0),
            Active = () => _daysPerSecond < 0,
            Tip = () => Localization.T("ui.toolbar.direction.tip"),
        });
        _toolbar.Add(FeatureButton("orbits"));
        _toolbar.Add(FeatureButton("labels"));
        _toolbar.Add(FeatureButton("trails"));
        _toolbar.Add(FeatureButton("realscale"));
        _toolbar.Add(FeatureButton("settings", () => Localization.T("ui.toolbar.settings")));
        _toolbar.Add(new Toolbar.Button
        {
            Label = () => Localization.T("ui.toolbar.commands"),
            Click = () => _palette.Open(_registry),
            Active = () => _palette.Active,
            Tip = () => Localization.T("ui.desc.palette") + "  [Ctrl+K]",
        });
    }

    private void ApplyDateSeek()
    {
        string s = _seekBuffer.Trim();
        bool ok = false;
        if (s.Length > 0)
        {
            if ((s[0] == '+' || s[0] == '-') && double.TryParse(
                    s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double delta))
            {
                SetSimTime(TargetSimDays + delta);
                ok = true;
            }
            else if (DateTime.TryParse(s,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out DateTime dt))
            {
                SetSimTime((dt - OrbitalMechanics.J2000).TotalDays);
                ok = true;
            }
        }

        if (ok)
        {
            ClearAllTrails();
            _audio.PlayTick();
            var newDate = OrbitalMechanics.J2000.AddDays(TargetSimDays);
            _seekFeedback = $"Jumped to {newDate:yyyy-MM-dd}";
        }
        else
        {
            _seekFeedback = $"Could not parse '{s}'";
        }
        _seekFeedbackUntil = GLFW.GetTime() + 3.0;
        _seekActive = false;
        _seekBuffer = "";
    }

    protected override void OnUnload()
    {
        // A7: a headless render run shouldn't clobber the interactive user's
        // saved camera / toggles.
        if (Headless == null) TrySavePersistedState();
        _solarWind.Dispose();
        _solarFlares.Dispose();
        _belt.Dispose();
        _comets.Dispose();
        _constellations.Dispose();
        _probes.Dispose();
        _lagrange.Dispose();
        _meteors.Dispose();
        _aurora.Dispose();
        _tidalLock.Dispose();
        _alignment.Dispose();
        _renderer.Dispose();
        _font.Dispose();
        base.OnUnload();
    }

    // -------- Q3: name search ----------------------------------------------------

    /// <summary>Enumerate every focusable body together with its unified index.
    /// Sun is reported as -1; planets / dwarfs use their <see cref="_planets"/> index;
    /// extras start at <c>_planets.Length</c>.</summary>
    private IEnumerable<(int idx, string name)> EnumerateBodies()
    {
        yield return (-1, "Sun");
        for (int i = 0; i < _planets.Length; i++)
            if (WorldAlive(_worldPlanetIdx[i])) yield return (i, _planets[i].Name);
        for (int i = 0; i < _extraBodies.Length; i++)
            if (ExtraAlive(i)) yield return (_planets.Length + i, _extraBodies[i].Name);
    }

    /// <summary>Find up to <paramref name="max"/> bodies whose names start with, then
    /// contain, the query (case-insensitive). Prefix matches rank above substring
    /// matches; ties broken by name length.</summary>
    private List<(int idx, string name)> FindNameMatches(string query, int max)
    {
        var result = new List<(int idx, string name, int rank)>();
        if (string.IsNullOrWhiteSpace(query)) return new List<(int, string)>();
        string q = query.Trim();
        foreach (var (idx, name) in EnumerateBodies())
        {
            int p = name.IndexOf(q, StringComparison.OrdinalIgnoreCase);
            if (p < 0) continue;
            // rank 0 = exact, 1 = prefix, 2 = substring.
            int rank = name.Length == q.Length ? 0 : (p == 0 ? 1 : 2);
            result.Add((idx, name, rank));
        }
        result.Sort((a, b) =>
        {
            int c = a.rank.CompareTo(b.rank);
            return c != 0 ? c : a.name.Length.CompareTo(b.name.Length);
        });
        var top = new List<(int idx, string name)>();
        for (int i = 0; i < Math.Min(max, result.Count); i++) top.Add((result[i].idx, result[i].name));
        return top;
    }

    private void ApplyNameSearch()
    {
        var matches = FindNameMatches(_searchBuffer, 1);
        if (matches.Count > 0)
        {
            int idx = matches[0].idx;
            _selectedIndex = idx;
            FocusOn(idx);
        }
        _searchActive = false;
        _searchBuffer = "";
    }

    // -------- A12: per-frame profiler overlay ----------------------------------

    private void DrawProfilerOverlay()
    {
        // Lay the profiler card in the bottom-right corner so it doesn't fight
        // the top-right Q7 HUD or the bottom-left help / info panels.
        var sb = new System.Text.StringBuilder();
        sb.Append(Localization.T("ui.profiler.title")).Append('\n');
        sb.Append(Localization.T("ui.profiler.frame", _profiler.FrameCpuMs)).Append('\n');
        double gpuTotal = 0, cpuTotal = 0;
        foreach (var p in _profiler.Passes) { gpuTotal += p.GpuMs; cpuTotal += p.CpuMs; }
        if (_profiler.GpuQueriesAvailable)
            sb.Append(Localization.T("ui.profiler.header.gpu")).Append('\n');
        else
            sb.Append(Localization.T("ui.profiler.header.cpu")).Append('\n');
        foreach (var p in _profiler.Passes)
        {
            string label = Localization.T("ui.profiler.pass." + p.Name);
            if (_profiler.GpuQueriesAvailable)
                sb.Append($"{label,-10} {p.GpuMs,5:0.00} | {p.CpuMs,5:0.00}\n");
            else
                sb.Append($"{label,-10} {p.CpuMs,5:0.00}\n");
        }
        if (_profiler.GpuQueriesAvailable)
            sb.Append($"{Localization.T("ui.profiler.total"),-10} {gpuTotal,5:0.00} | {cpuTotal,5:0.00}");
        else
            sb.Append($"{Localization.T("ui.profiler.total"),-10} {cpuTotal,5:0.00}");

        float width = 260f;
        float x = _renderer.FramebufferSize.X - width - 12f;
        float y = _renderer.FramebufferSize.Y - 220f;
        _renderer.DrawText(_font, sb.ToString(), x, y, 13f,
            new Vector4(0.85f, 1f, 0.7f, 0.95f));
    }

    // -------- Q4 / Q11: cross-platform screenshot via SkiaSharp ----------------

    private void SaveScreenshot()
    {
        try
        {
            Directory.CreateDirectory("screenshots");
            string path = Path.Combine("screenshots", $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            SaveScreenshotTo(path);
            _audio.PlayTick();
            _seekFeedback = $"Saved {path}";
            _seekFeedbackUntil = GLFW.GetTime() + 3.0;
            Debug.WriteLine($"[screenshot] {path}");
        }
        catch (Exception ex)
        {
            _seekFeedback = $"Screenshot failed: {ex.Message}";
            _seekFeedbackUntil = GLFW.GetTime() + 3.0;
            Debug.WriteLine($"[screenshot] failed: {ex}");
        }
    }

    /// <summary>A4/A11/A7: read the back-buffer RGBA8 and encode it as a PNG via
    /// SkiaSharp at <paramref name="path"/>. Used by both the F12 screenshot key
    /// and the headless render loop. Splits into a sync GL readback + memcpy and
    /// an encode/disk-write helper so F9 recording can do only the readback on
    /// the render thread and push encoding to a worker.</summary>
    internal void SaveScreenshotTo(string path)
    {
        if (!CaptureBackBufferRgba(out var flipped, out int w, out int h)) return;
        EncodePngFromRgba(flipped, w, h, path);
    }

    /// <summary>Read the default-framebuffer back-buffer as RGBA8 and flip rows
    /// from GL bottom-left into image-format top-left order. Returns false when
    /// the framebuffer has no area yet.</summary>
    private bool CaptureBackBufferRgba(out byte[] flipped, out int w, out int h)
    {
        w = _renderer.FramebufferSize.X;
        h = _renderer.FramebufferSize.Y;
        if (w <= 0 || h <= 0) { flipped = Array.Empty<byte>(); return false; }

        var pixels = new byte[w * h * 4];
        GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
        GL.ReadBuffer(ReadBufferMode.Back);
        GL.ReadPixels(0, 0, w, h, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

        flipped = new byte[pixels.Length];
        int stride = w * 4;
        for (int y = 0; y < h; y++)
            System.Buffer.BlockCopy(pixels, (h - 1 - y) * stride, flipped, y * stride, stride);
        return true;
    }

    /// <summary>Encode an already-flipped RGBA8 buffer to a PNG file. Safe to
    /// call from a worker thread (no GL state, no shared mutable state).</summary>
    private static void EncodePngFromRgba(byte[] flipped, int w, int h, string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var info = new SkiaSharp.SKImageInfo(w, h,
            SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
        using var bmp = new SkiaSharp.SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(flipped, 0, bmp.GetPixels(), flipped.Length);
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 95);
        using var fs = File.OpenWrite(path);
        data.SaveTo(fs);
    }

    // -------- Alt+Enter: borderless fullscreen toggle --------------------------

    /// <summary>Flip between borderless fullscreen and a normal window. Mirrors
    /// the keyboard binding (Alt+Enter) and the F1 settings-panel row so the
    /// two stay in sync. Idempotent: re-applying the same state is a no-op so
    /// the persisted-state restore on startup doesn't briefly flash a window
    /// resize.</summary>
    private void ToggleFullscreen() => SetFullscreen(!_fullscreen);

    private void SetFullscreen(bool fullscreen)
    {
        if (_fullscreen == fullscreen) return;
        _fullscreen = fullscreen;
        try
        {
            WindowState = fullscreen ? WindowState.Fullscreen : WindowState.Normal;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[fullscreen] toggle failed: {ex.Message}");
        }
        _seekFeedback = Localization.T(fullscreen ? "ui.fullscreen.on" : "ui.fullscreen.off");
        _seekFeedbackUntil = GLFW.GetTime() + 2.0;
    }

    // -------- A7 (interactive): F9 toggles in-app frame recording --------------

    /// <summary>Resolve an ffmpeg executable: explicit env-var override
    /// <c>SOLARSYSTEM_FFMPEG</c> takes precedence, otherwise plain <c>ffmpeg</c>
    /// (relies on <c>PATH</c>). Returns null when nothing usable is configured.</summary>
    private static string? ResolveFfmpeg()
    {
        var env = Environment.GetEnvironmentVariable("SOLARSYSTEM_FFMPEG");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        // Just trust PATH — Process.Start will fail loudly if it's not there and
        // ToggleRecording surfaces the failure on the banner.
        return "ffmpeg";
    }

    private void ToggleRecording()
    {
        if (!_recording)
        {
            _recordDir = Path.Combine("recordings", $"rec_{DateTime.Now:yyyyMMdd_HHmmss}");
            try { Directory.CreateDirectory(_recordDir); }
            catch (Exception ex)
            {
                _seekFeedback = $"Record failed: {ex.Message}";
                _seekFeedbackUntil = GLFW.GetTime() + 3.0;
                return;
            }
            _recordFrameIndex = 0;
            _recordStartedAt = GLFW.GetTime();
            _recording = true;
            _audio.PlayTick();
            _seekFeedback = Localization.T("ui.record.start", _recordDir);
            _seekFeedbackUntil = GLFW.GetTime() + 2.5;
            Debug.WriteLine($"[record] start -> {_recordDir}");
        }
        else
        {
            _recording = false;
            double elapsed = GLFW.GetTime() - _recordStartedAt;
            int frames = _recordFrameIndex;
            _audio.PlayTick();
            _seekFeedback = Localization.T("ui.record.stop", frames, elapsed);
            _seekFeedbackUntil = GLFW.GetTime() + 4.0;
            Debug.WriteLine($"[record] stop {frames} frames in {elapsed:0.0}s");

            // Snapshot pending encode tasks so the worker below can wait for
            // them to finish before invoking ffmpeg. The render thread does NOT
            // wait — UI keeps responding while encoding finishes asynchronously.
            var pending = _recordEncodeTasks.ToArray();
            _recordEncodeTasks.Clear();

            // Encode video at the actual capture rate so playback speed matches
            // wall-clock time. Clamp to >=1 so ffmpeg doesn't reject zero.
            int actualFps = (int)Math.Max(1, Math.Round(frames / Math.Max(0.001, elapsed)));

            // Best-effort encode in the background so the UI doesn't stall while
            // ffmpeg processes thousands of frames.
            string dir = _recordDir;
            string outFile = Path.Combine(dir, "out.mp4");
            string? ff = ResolveFfmpeg();
            if (ff != null && frames >= 2)
            {
                Task.Run(() =>
                {
                    // Drain any in-flight PNG encodes first; ffmpeg expects the
                    // full frame_NNNNN.png sequence to be on disk.
                    try { Task.WaitAll(pending); }
                    catch (Exception ex) { Debug.WriteLine($"[record] encode wait failed: {ex}"); }
                    bool ok = HeadlessRenderJob.TryEncodePngSequence(ff, dir, actualFps, outFile, out string err);
                    if (ok)
                    {
                        _seekFeedback = Localization.T("ui.record.encoded", outFile);
                        _seekFeedbackUntil = GLFW.GetTime() + 5.0;
                    }
                    else
                    {
                        // Surface the failure so the user doesn't end up staring at
                        // a 0-byte out.mp4 wondering what happened. err contains the
                        // tail of ffmpeg's stderr (or the launch exception message).
                        _seekFeedback = $"ffmpeg failed: {err}";
                        _seekFeedbackUntil = GLFW.GetTime() + 8.0;
                    }
                });
            }
            else if (frames < 2)
            {
                _seekFeedback = $"Recorded {frames} frame(s) — need ≥ 2 to encode video";
                _seekFeedbackUntil = GLFW.GetTime() + 5.0;
            }
        }
    }

    // -------- Q5: persisted settings --------------------------------------------

    // A11: was private; promoted to internal so SolarSystemJsonContext (the
    // System.Text.Json source-generated context) can reference the type.
    /// <summary>On-disk layout of <c>state.json</c>. Every boolean toggle lives in
    /// <see cref="Features"/> (id → value, produced by <see cref="FeatureRegistry.Snapshot"/>);
    /// only the non-boolean bits keep dedicated properties. Saves written by the
    /// pre-registry layout (one PascalCase bool per toggle) are migrated on load
    /// via <see cref="FeatureRegistry.MigrateLegacy"/>.</summary>
    internal sealed class PersistedState
    {
        public float Yaw { get; set; }
        public float Pitch { get; set; }
        public float Distance { get; set; }
        public float TargetX { get; set; }
        public float TargetY { get; set; }
        public float TargetZ { get; set; }
        public double DaysPerSecond { get; set; } = 1.0;
        public double SimDays { get; set; }
        public int FocusIndex { get; set; } = -1;
        public int  HelpMode { get; set; }
        public string Language { get; set; } = "en";
        public FeatureCategory SettingsTab { get; set; } = FeatureCategory.Bodies;
        public Dictionary<string, bool>? Features { get; set; }
        /// <summary>Multi-option entries (<see cref="Choice"/>), id → option index.</summary>
        public Dictionary<string, int>? Choices { get; set; }
        /// <summary>Physics sandbox constants (not booleans, so not part of <see cref="Features"/>).</summary>
        public PhysicsConstants.Dto? Physics { get; set; }
    }

    private void TryLoadPersistedState()
    {
        try
        {
            if (!File.Exists(StateFilePath)) return;
            var json = File.ReadAllText(StateFilePath);
            // A11: AOT-friendly source-generated typeinfo.
            var s = JsonSerializer.Deserialize(json, SolarSystemJsonContext.Default.PersistedState);
            if (s == null) return;

            Dictionary<string, bool> toggles;
            bool legacyNBody = false;
            if (s.Features != null)
            {
                toggles = s.Features;
                legacyNBody = toggles.TryGetValue("nbody", out bool nb) && nb;
            }
            else
            {
                // Pre-registry save: pick the old per-toggle properties straight
                // out of the raw document so the DTO doesn't have to carry them.
                using var doc = JsonDocument.Parse(json);
                toggles = _registry.MigrateLegacy(doc.RootElement);
                legacyNBody = doc.RootElement.TryGetProperty("NBodyEnabled", out var nbProp)
                              && nbProp.ValueKind == JsonValueKind.True;
                Debug.WriteLine($"[state] migrated {toggles.Count} legacy toggle(s)");
            }

            // Physics sandbox constants (clamped on load) and the mode selector go
            // first: the clock must be set before the mode seeds the world at the
            // saved date, and the mode must be active before the toggles are applied
            // (physics-only switches such as physics.hud are refused while the mode
            // is Ephemeris). A pre-sandbox "nbody = true" save becomes Physics mode.
            if (s.Physics != null) _physConst.LoadDto(s.Physics);
            var choices = s.Choices ?? new Dictionary<string, int>(StringComparer.Ordinal);
            if (!choices.ContainsKey("simmode") && legacyNBody)
                choices["simmode"] = (int)SimulationMode.Physics;
            _simDays = s.SimDays;
            _physicsTarget = null;
            _registry.RestoreChoices(choices);

            // Then the toggles: real-scale is registered first, so VisualRadii and
            // camera limits are already correct when we clamp Distance below.
            _registry.Restore(toggles);

            _camera.Yaw = s.Yaw;
            _camera.Pitch = s.Pitch;
            _camera.Distance = Math.Clamp(s.Distance, _camera.MinDistance, _camera.MaxDistance);
            _camera.Target = new Vector3(s.TargetX, s.TargetY, s.TargetZ);

            _daysPerSecond = s.DaysPerSecond;
            _focusIndex = s.FocusIndex;
            _helpMode = Math.Clamp(s.HelpMode, 0, 2);
            _settings.ActiveTab = Enum.IsDefined(s.SettingsTab) ? s.SettingsTab : FeatureCategory.Bodies;
            if (!string.IsNullOrEmpty(s.Language)) Localization.SetLanguage(s.Language);

            Debug.WriteLine($"[state] loaded from {StateFilePath}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[state] load failed: {ex.Message}");
        }
    }

    private void TrySavePersistedState()
    {
        try
        {
            var s = new PersistedState
            {
                Yaw = _camera.Yaw,
                Pitch = _camera.Pitch,
                Distance = _camera.Distance,
                TargetX = _camera.Target.X,
                TargetY = _camera.Target.Y,
                TargetZ = _camera.Target.Z,
                DaysPerSecond = _daysPerSecond,
                SimDays = _simDays,
                FocusIndex = _focusIndex,
                HelpMode = _helpMode,
                Language = Localization.CurrentLanguage,
                SettingsTab = _settings.ActiveTab,
                Features = _registry.Snapshot(),
                Choices = _registry.SnapshotChoices(),
                Physics = _physConst.ToDto(),
            };
            string? dir = Path.GetDirectoryName(StateFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // A11: AOT-friendly source-generated typeinfo, options bound at
            // context construction so WriteIndented still applies.
            var ctx = new SolarSystemJsonContext(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StateFilePath, JsonSerializer.Serialize(s, ctx.PersistedState));
            Debug.WriteLine($"[state] saved to {StateFilePath}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[state] save failed: {ex.Message}");
        }
    }
}
