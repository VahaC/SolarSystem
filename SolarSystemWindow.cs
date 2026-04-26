using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace SolarSystem;

public sealed class SolarSystemWindow : GameWindow
{
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
    private readonly Comet _comet = new();
    private readonly Constellations _constellations = new();
    // S9–S12.
    private readonly Probes _probes = new();
    private readonly LagrangePoints _lagrange = new();
    private readonly MeteorShowers _meteors = new();
    private readonly Bookmarks _bookmarks = new();
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
    /// <summary>R4: when true each planet's spin angle is evaluated at
    /// <c>simDays - r/c</c> (where r is its heliocentric distance), so the day/night
    /// terminator falls where it was when the photons currently illuminating it
    /// left the Sun. Distant planets show the largest visible delay (Neptune
    /// ~4 light-hours ≈ 90° of rotation).</summary>
    private bool _lightTime;
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
        _comet.Initialize();
        _constellations.Initialize();
        _probes.Initialize();
        _lagrange.Initialize();
        _meteors.Initialize();

        // A4: instanced quad particles size their billboards in clip space using
        // the current viewport, so push it once now and again on every resize.
        var initVp = new Vector2(ClientSize.X, ClientSize.Y);
        _solarWind.SetViewport(initVp);
        _solarFlares.SetViewport(initVp);
        _comet.SetViewport(initVp);
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
        extras.Add(_comet.Body);
        _extraBodies = [.. extras];

        _camera.Aspect = ClientSize.X / (float)ClientSize.Y;
        _camera.ResetDefault();

        // Q5: restore persisted UI state. Done last so it can override the freshly
        // initialised camera / toggles / scale mode.
        TryLoadPersistedState();
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        _renderer.FramebufferSize = new Vector2i(e.Width, e.Height);
        _camera.Aspect = e.Width / (float)Math.Max(1, e.Height);

        var vp = new Vector2(e.Width, e.Height);
        _solarWind.SetViewport(vp);
        _solarFlares.SetViewport(vp);
        _comet.SetViewport(vp);
        _belt.SetViewport(vp);
        _meteors.SetViewport(vp);
    }

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);

        if (!_paused)
            _simDays += _daysPerSecond * args.Time;

        // Update positions and axial rotation
        const double TwoPi = Math.PI * 2.0;
        foreach (var p in _planets)
        {
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
                rotDays -= rAU * LightDaysPerAU;
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

        // Moon orbits Earth in a circle inclined to the ecliptic. In compressed mode
        // the radius is artistic (visible without engulfing Venus); in real-scale mode
        // it uses the actual ~384,400 km converted via the same AU/km scale.
        {
            var earth = _planets[2];
            float moonRadius = OrbitalMechanics.RealScale
                ? (float)(384400.0 * OrbitalMechanics.KmToWorldRealScale)
                : MoonOrbitRadius;
            double moonAngle = (_simDays / MoonOrbitalPeriodDays) * TwoPi;
            float cx = (float)Math.Cos(moonAngle) * moonRadius;
            float cz = (float)Math.Sin(moonAngle) * moonRadius;
            float incl = MathHelper.DegreesToRadians(MoonOrbitInclinationDeg);
            float cy = cz * MathF.Sin(incl);
            cz *= MathF.Cos(incl);
            _moon.Position = earth.Position + new Vector3(cx, cy, cz);
            _moon.HelioAU = earth.HelioAU; // for info panel "distance from Sun" approximation
            double mAngle = (_simDays * 24.0 / _moon.RotationPeriodHours) * TwoPi;
            mAngle %= TwoPi;
            if (mAngle < 0) mAngle += TwoPi;
            _moon.RotationAngleRad = (float)mAngle;
        }

        // S6: Galilean moons + Titan. Same pattern as Earth's Moon: simple circular
        // orbits inclined to the ecliptic, with a per-moon phase offset so the four
        // Jovian moons don't start clustered on top of each other. Skipping the full
        // orbital-element solve here is fine — at the artistic radii used in
        // compressed mode the visual error is well below one pixel.
        foreach (var m in _moons)
        {
            var host = _planets[m.HostPlanetIndex];
            float r = OrbitalMechanics.RealScale
                ? (float)(m.RealOrbitRadiusKm * OrbitalMechanics.KmToWorldRealScale)
                : m.ArtisticOrbitRadius;
            double angle = (_simDays / m.OrbitalPeriodDays) * TwoPi
                           + m.PhaseDeg * OrbitalMechanics.DegToRad;
            float cx = (float)Math.Cos(angle) * r;
            float cz = (float)Math.Sin(angle) * r;
            float incl = MathHelper.DegreesToRadians(m.OrbitInclinationDeg);
            float cy = cz * MathF.Sin(incl);
            cz *= MathF.Cos(incl);
            m.Body.Position = host.Position + new Vector3(cx, cy, cz);
            m.Body.HelioAU = host.HelioAU;
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
            foreach (var p in _planets)
            {
                // A small absolute spacing keeps trails visible at slow sim speeds while
                // the ring buffer still drops old samples once full at high speeds.
                float spacing = OrbitalMechanics.RealScale ? 0.0005f : 0.01f;
                p.TrailPush(p.Position, spacing);
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
        else
        {
            var followed = GetBody(_focusIndex);
            if (followed != null) _camera.Target = followed.Position;
        }

        // Pause must also freeze the Sun's particle effects, otherwise the wind keeps
        // streaming and flares keep erupting while the planets are perfectly still.
        // Feed dt=0 (instead of skipping the call) so any internal state stays valid.
        float fxDt = _paused ? 0f : (float)args.Time;
        _solarWind.Update(fxDt, Vector3.Zero, SunRadius);
        _solarFlares.Update(fxDt, Vector3.Zero, SunRadius);

        // Asteroid belt + comet position track sim time even when paused (positions are
        // a pure function of _simDays, not an integration), so they stay correctly
        // placed after a date jump or while the simulation is frozen.
        _belt.Update(_simDays);
        _comet.UpdatePosition(_simDays);
        _comet.UpdateTail(fxDt, Vector3.Zero);

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
        if (!_seekActive && !_searchActive)
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
        Title = $"Solar System  |  {date:yyyy-MM-dd}  |  speed {speedStr}  |  [Space] pause  [, .] reverse/forward  [+/-] speed  [0-8] focus  [O] orbits  [T] trails  [L] labels  [W] wind  [F] flares  [R] scale  [D] dwarfs  [C] constellations";
    }

    /// <summary>One-shot keyboard handling. More reliable than polling KeyboardState.IsKeyPressed
    /// every frame: the GLFW key event fires exactly once per physical press, with no risk of
    /// missing the press window between two update ticks.</summary>
    protected override void OnKeyDown(KeyboardKeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.IsRepeat && !_seekActive) return;

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

        // Q3: Ctrl+F opens the name-search prompt. Handled before the F-key fallthrough
        // so it doesn't also toggle the solar flares.
        if (e.Key == Keys.F && (e.Modifiers & KeyModifiers.Control) != 0)
        {
            _searchActive = true;
            _searchBuffer = "";
            _searchSwallowNextChar = true;
            return;
        }

        // S12: Ctrl+B cycles eclipse / transit bookmarks. Handled before the bare B
        // key (currently unbound) so a future binding doesn't fight with it.
        if (e.Key == Keys.B && (e.Modifiers & KeyModifiers.Control) != 0)
        {
            var entry = _bookmarks.Next(_simDays);
            if (entry is { } ev)
            {
                _simDays = Bookmarks.ToSimDays(ev);
                ClearAllTrails();
                _seekFeedback = $"{ev.Kind}: {ev.Title} — {ev.Date:yyyy-MM-dd}";
                _seekFeedbackUntil = GLFW.GetTime() + 4.0;
            }
            return;
        }

        switch (e.Key)
        {
            case Keys.Escape:
                Close();
                break;

            case Keys.J:
                _seekActive = true;
                _seekBuffer = "";
                _seekFeedback = "";
                _seekSwallowNextChar = true;
                break;

            case Keys.Space: _paused = !_paused; break;
            case Keys.O: _showOrbits = !_showOrbits; break;
            case Keys.A: _showAxes = !_showAxes; break;
            case Keys.L: _showLabels = !_showLabels; break;
            case Keys.T: _showTrails = !_showTrails; if (!_showTrails) ClearAllTrails(); break;
            case Keys.D:
                _showDwarfs = !_showDwarfs;
                // If a dwarf was the active focus / selection, drop back to the Sun so
                // the camera doesn't keep tracking an invisible body.
                if (!_showDwarfs)
                {
                    if (_focusIndex >= _dwarfStart && _focusIndex < _planets.Length) FocusOn(-1);
                    if (_selectedIndex >= _dwarfStart && _selectedIndex < _planets.Length) _selectedIndex = -2;
                    // Clear stale dwarf trails so they don't reappear as a frozen line strip
                    // on the next toggle-on.
                    for (int i = _dwarfStart; i < _planets.Length; i++) _planets[i].TrailReset();
                }
                break;
            case Keys.W: _solarWind.Enabled = !_solarWind.Enabled; break;
            case Keys.F: _solarFlares.Enabled = !_solarFlares.Enabled; break;
            case Keys.R: ToggleRealScale(); break;
            case Keys.C: _showConstellations = !_showConstellations; _constellations.Enabled = _showConstellations; break;

            // S9–S11.
            case Keys.P: _showProbes = !_showProbes; break;
            case Keys.G: _showLagrange = !_showLagrange; break;
            case Keys.M:
                _showMeteors = !_showMeteors;
                if (!_showMeteors)
                {
                    _seekFeedback = "Meteor showers: OFF";
                }
                else if (_meteors.ActiveShowerName.Length > 0)
                {
                    _seekFeedback = $"Meteor showers: ON — {_meteors.ActiveShowerName} active";
                }
                else
                {
                    var next = _meteors.NextPeak(_simDays);
                    _seekFeedback = next is { } n
                        ? $"Meteor showers: ON — next: {n.Name} in {n.DaysUntil} day{(n.DaysUntil == 1 ? "" : "s")}"
                        : "Meteor showers: ON";
                }
                _seekFeedbackUntil = GLFW.GetTime() + 3.0;
                break;
            case Keys.Y:
                _lightTime = !_lightTime;
                _seekFeedback = _lightTime
                    ? "Light-time: ON (Sun lighting delayed by r/c)"
                    : "Light-time: OFF";
                _seekFeedbackUntil = GLFW.GetTime() + 2.5;
                break;

            // Q7: HUD overlay (FPS + particle counts).
            case Keys.GraveAccent: _showHud = !_showHud; break;

            // Q4: screenshot to ./screenshots/<timestamp>.png.
            case Keys.F12:
                if (OperatingSystem.IsWindows()) SaveScreenshot();
                break;

            case Keys.KeyPadAdd:
            case Keys.Equal:
            {
                // Operate on magnitude so reverse-time playback can also be sped up.
                double sign = _daysPerSecond < 0 ? -1.0 : 1.0;
                double mag = Math.Min(1000.0, Math.Abs(_daysPerSecond) * 1.5);
                _daysPerSecond = sign * mag;
                break;
            }
            case Keys.KeyPadSubtract:
            case Keys.Minus:
            {
                double sign = _daysPerSecond < 0 ? -1.0 : 1.0;
                double mag = Math.Max(0.1, Math.Abs(_daysPerSecond) / 1.5);
                _daysPerSecond = sign * mag;
                break;
            }

            // Reverse-time controls: ',' forces backward playback, '.' forces forward.
            // Magnitude is preserved so toggling direction doesn't change speed.
            case Keys.Comma:
                if (_daysPerSecond > 0) ClearAllTrails();
                _daysPerSecond = -Math.Abs(_daysPerSecond);
                break;
            case Keys.Period:
                if (_daysPerSecond < 0) ClearAllTrails();
                _daysPerSecond = Math.Abs(_daysPerSecond);
                break;

            case Keys.D0:
            case Keys.KeyPad0:
                _selectedIndex = -1;
                FocusOn(-1);
                Debug.WriteLine("[focus] Sun");
                break;

            case Keys.D1: case Keys.KeyPad1: FocusOn(0); _selectedIndex = 0; break;
            case Keys.D2: case Keys.KeyPad2: FocusOn(1); _selectedIndex = 1; break;
            case Keys.D3: case Keys.KeyPad3: FocusOn(2); _selectedIndex = 2; break;
            case Keys.D4: case Keys.KeyPad4: FocusOn(3); _selectedIndex = 3; break;
            case Keys.D5: case Keys.KeyPad5: FocusOn(4); _selectedIndex = 4; break;
            case Keys.D6: case Keys.KeyPad6: FocusOn(5); _selectedIndex = 5; break;
            case Keys.D7: case Keys.KeyPad7: FocusOn(6); _selectedIndex = 6; break;
            case Keys.D8: case Keys.KeyPad8: FocusOn(7); _selectedIndex = 7; break;
        }
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);
        _renderer.BeginScene();

        // Choose between the full body list (planets + dwarfs) and the major-only
        // slice in one place so every render pass sees a consistent view.
        Planet[] visible = _showDwarfs ? _planets : _majorPlanets;

        // R3: adaptive star brightness + saturation. Far from the Sun the Milky
        // Way is dimmed so distant planets aren't drowned by the panorama; close
        // to a planet the colour is punched up for a "near-orbit" feel.
        UpdateAdaptiveStars(visible);

        _renderer.DrawStars(_camera);
        if (_showConstellations) _constellations.Draw(_camera);
        if (_showOrbits)
        {
            _renderer.DrawOrbits(_camera, visible);
            _renderer.DrawCometOrbit(_camera, _comet);
        }
        if (_showTrails) _renderer.DrawTrails(_camera, visible);

        _renderer.DrawSun(_camera, Vector3.Zero, SunRadius);
        foreach (var p in visible)
            _renderer.DrawPlanet(_camera, p, Vector3.Zero);
        _renderer.DrawPlanet(_camera, _moon, Vector3.Zero);
        foreach (var m in _moons)
            _renderer.DrawPlanet(_camera, m.Body, Vector3.Zero);
        _renderer.DrawPlanet(_camera, _comet.Body, Vector3.Zero);

        // V3: cloud layer for any planet that has one (currently just Earth).
        // Drawn after the opaque planet pass so alpha-blending composites over
        // the surface — including the V4 night-side city-lights baked into PlanetFS.
        foreach (var p in visible)
            _renderer.DrawClouds(_camera, p, Vector3.Zero);

        var saturn = _planets[5];
        _renderer.DrawSaturnRing(_camera, saturn, Vector3.Zero);

        _belt.Draw(_camera);
        _comet.DrawTail(_camera);
        _solarWind.Draw(_camera);
        _solarFlares.Draw(_camera);

        // S9–S11: probe crosses, Lagrange-point diamonds, meteor streaks. All use
        // additive blending so they brighten the underlying scene without occluding it.
        if (_showProbes) _probes.Draw(_camera);
        if (_showLagrange) { _lagrange.Enabled = true; _lagrange.Draw(_camera); }
        if (_showMeteors) _meteors.Draw(_camera);

        if (_showAxes) _renderer.DrawPlanetAxes(_camera, visible);

        // Apply HDR bright-pass + Gaussian blur + additive composite to the
        // default framebuffer. All subsequent 2D overlays (labels, UI panels)
        // are drawn directly to the default framebuffer and therefore unaffected.
        _renderer.EndSceneAndApplyBloom();

        // V6: lens flare ghosts. Drawn after the composite so the chain isn't
        // smeared by bloom and looks like internal reflections in the lens
        // rather than a halo of the Sun itself.
        _renderer.DrawLensFlare(_camera, Vector3.Zero);

        // Labels
        if (_showLabels)
        {
            _renderer.DrawLabel(_font, _camera,
                new Vector3(0, SunRadius + 1.5f, 0), "Sun", 14, new Vector4(1, 0.9f, 0.5f, 0.95f));
            foreach (var p in visible)
                _renderer.DrawLabel(_font, _camera,
                    p.Position + new Vector3(0, p.VisualRadius + 1.0f, 0),
                    p.Name, 13, new Vector4(0.85f, 0.9f, 1f, 0.95f));
            _renderer.DrawLabel(_font, _camera,
                _moon.Position + new Vector3(0, _moon.VisualRadius + 0.5f, 0),
                _moon.Name, 12, new Vector4(0.85f, 0.85f, 0.85f, 0.9f));
            foreach (var m in _moons)
                _renderer.DrawLabel(_font, _camera,
                    m.Body.Position + new Vector3(0, m.Body.VisualRadius + 0.4f, 0),
                    m.Body.Name, 11, new Vector4(0.85f, 0.85f, 0.85f, 0.85f));
            _renderer.DrawLabel(_font, _camera,
                _comet.Body.Position + new Vector3(0, _comet.Body.VisualRadius + 0.5f, 0),
                _comet.Body.Name, 12, new Vector4(0.7f, 0.85f, 1f, 0.9f));
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
        _renderer.DrawText(_font, $"Date    {date:yyyy-MM-dd}", 12, 12, 16, white);
        string hudSpeed = _paused
            ? "Speed   PAUSED"
            : $"Speed   {(_daysPerSecond < 0 ? "-" : "")}{Math.Abs(_daysPerSecond):0.##} d/s {(_daysPerSecond < 0 ? "(reverse)" : "")}";
        _renderer.DrawText(_font, hudSpeed, 12, 32, 16, white);
        _renderer.DrawText(_font, $"Orbits  {(_showOrbits ? "on" : "off")}    Labels  {(_showLabels ? "on" : "off")}", 12, 52, 14, white);

        // Top-left help panel listing every available control.
        var dim = new Vector4(0.85f, 0.9f, 1f, 0.85f);
        const string help =
            "Controls\n" +
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
            "Ctrl+B      cycle eclipse / transit\n" +
            "J           jump to date / ±days\n" +
            "Ctrl+F      search bodies\n" +
            "F12         screenshot\n" +
            "~           FPS / particle HUD\n" +
            "W           toggle solar wind\n" +
            "F           toggle solar flares\n" +
            "R           real / compressed scale\n" +
            "Y           toggle light-time delay\n" +
            "Esc         quit";
        _renderer.DrawText(_font, help, 12, 78, 13, dim);

        // Info panel (multi-line) for selected body
        string info;
        var selectedBody = GetBody(_selectedIndex);
        if (selectedBody != null)
        {
            var p = selectedBody;
            double dist = Math.Sqrt(p.HelioAU.X * p.HelioAU.X + p.HelioAU.Y * p.HelioAU.Y + p.HelioAU.Z * p.HelioAU.Z);
            double dayHours = Math.Abs(p.RotationPeriodHours);
            string daySuffix = p.RotationPeriodHours < 0 ? " (retro)" : "";
            double yearDays = p.OrbitalPeriodYears * 365.25;
            info =
                $"{p.Name}\n" +
                $"Radius  {p.RealRadiusKm:0} km\n" +
                $"Day     {dayHours:0.##} h{daySuffix}\n" +
                $"Year    {yearDays:0.#} d  /  {p.OrbitalPeriodYears:0.###} y\n" +
                $"Dist    {dist:0.000} AU\n" +
                $"Tilt    {p.AxisTiltDeg:0.##} deg";
        }
        else if (_selectedIndex == -1)
        {
            info = "Sun\nRadius  695700 km\nDay     609.12 h (eq)\nMass    1.989e30 kg\nType    G2V star";
        }
        else
        {
            info = "Click a body for info\nDouble-click to focus\nDouble-click empty to unfocus";
        }
        // Draw multi-line panel anchored bottom-left.
        int lineCount = 1;
        foreach (char ch in info) if (ch == '\n') lineCount++;
        const float infoSize = 14f;
        float lineH = infoSize * (_font.LineHeight / _font.FontPixelSize);
        float panelY = _renderer.FramebufferSize.Y - 12f - lineCount * lineH;
        _renderer.DrawText(_font, info, 12, panelY, infoSize, white);

        // S11: live banner when a meteor shower is currently in its activity window.
        if (_showMeteors && _meteors.ActiveShowerName.Length > 0)
        {
            _renderer.DrawText(_font, $"☄ {_meteors.ActiveShowerName} active",
                _renderer.FramebufferSize.X - 260f, _renderer.FramebufferSize.Y - 30f, 13f,
                new Vector4(1f, 0.85f, 0.6f, 0.95f));
        }

        // Date-seek prompt: top-center modal overlay while active. Drawn after every
        // other UI so it can't be occluded.
        if (_seekActive)
        {
            string prompt = $"Jump to date (YYYY-MM-DD) or +/-N days:\n> {_seekBuffer}_";
            _renderer.DrawText(_font, prompt,
                _renderer.FramebufferSize.X * 0.5f - 200f, 20f, 16f,
                new Vector4(1f, 1f, 0.7f, 1f));
        }
        else if (_searchActive)
        {
            // Q3: search prompt + top matches preview.
            var matches = FindNameMatches(_searchBuffer, max: 5);
            var sb = new System.Text.StringBuilder();
            sb.Append("Search body (Enter=focus, Esc=cancel):\n> ").Append(_searchBuffer).Append('_');
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
        else if (_seekFeedback.Length > 0)
        {
            _renderer.DrawText(_font, _seekFeedback,
                _renderer.FramebufferSize.X * 0.5f - 160f, 20f, 14f,
                new Vector4(1f, 0.9f, 0.5f, 0.9f));
        }

        // Q6: hover tooltip beside the cursor.
        if (!_seekActive && !_searchActive)
        {
            string? tip = null;
            if (_hoverIndex == -1) tip = "Sun";
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
            string scale = OrbitalMechanics.RealScale ? "real" : "compressed";
            string hud =
                $"FPS       {_fpsValue:0.}\n" +
                $"Scale     {scale}\n" +
                $"Wind      {_solarWind.ActiveCount} / {_solarWind.MaxParticles}\n" +
                $"Flares    {_solarFlares.ActiveCount} / {_solarFlares.MaxParticles}\n" +
                $"Comet     {_comet.ActiveCount} / {_comet.MaxParticles}\n" +
                $"Belt      {_belt.Count}";
            _renderer.DrawText(_font, hud,
                _renderer.FramebufferSize.X - 220f, 12f, 14f,
                new Vector4(0.7f, 1f, 0.8f, 0.95f));
        }

        SwapBuffers();
    }

    // --- Mouse ---
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        var pos = new Vector2(MouseState.X, MouseState.Y);
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
        // is already current.
        if (index < _planets.Length)
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
        _comet.RebuildOrbit();

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

    /// <summary>Build a host-orbiting moon: load its texture, set its initial spin
    /// to match the host system's tidal locking convention, and wrap the resulting
    /// <see cref="Planet"/> with the orbital parameters needed by the per-frame
    /// position update in <see cref="OnUpdateFrame"/>.</summary>
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
                _simDays += delta;
                ok = true;
            }
            else if (DateTime.TryParse(s,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out DateTime dt))
            {
                _simDays = (dt - OrbitalMechanics.J2000).TotalDays;
                ok = true;
            }
        }

        if (ok)
        {
            ClearAllTrails();
            var newDate = OrbitalMechanics.J2000.AddDays(_simDays);
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
        TrySavePersistedState();
        _solarWind.Dispose();
        _solarFlares.Dispose();
        _belt.Dispose();
        _comet.Dispose();
        _constellations.Dispose();
        _probes.Dispose();
        _lagrange.Dispose();
        _meteors.Dispose();
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
        for (int i = 0; i < _planets.Length; i++) yield return (i, _planets[i].Name);
        for (int i = 0; i < _extraBodies.Length; i++) yield return (_planets.Length + i, _extraBodies[i].Name);
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

    // -------- Q4: screenshot -----------------------------------------------------

    [SupportedOSPlatform("windows")]
    private void SaveScreenshot()
    {
        try
        {
            int w = _renderer.FramebufferSize.X;
            int h = _renderer.FramebufferSize.Y;
            if (w <= 0 || h <= 0) return;

            // Read from the default framebuffer (post-bloom composite) as BGRA so it
            // maps directly onto a 32bppArgb GDI+ bitmap with no per-pixel swap.
            var pixels = new byte[w * h * 4];
            GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
            GL.ReadBuffer(ReadBufferMode.Back);
            GL.ReadPixels(0, 0, w, h, PixelFormat.Bgra, PixelType.UnsignedByte, pixels);

            Directory.CreateDirectory("screenshots");
            string path = Path.Combine("screenshots", $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png");

            using (var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                var rect = new System.Drawing.Rectangle(0, 0, w, h);
                var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
                int stride = data.Stride;
                // OpenGL origin is bottom-left, GDI+ is top-left → copy rows in reverse.
                unsafe
                {
                    byte* dst = (byte*)data.Scan0;
                    fixed (byte* srcBase = pixels)
                    {
                        for (int y = 0; y < h; y++)
                        {
                            byte* src = srcBase + (h - 1 - y) * w * 4;
                            byte* d = dst + y * stride;
                            System.Buffer.MemoryCopy(src, d, stride, w * 4);
                        }
                    }
                }
                bmp.UnlockBits(data);
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }

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

    // -------- Q5: persisted settings --------------------------------------------

    private sealed class PersistedState
    {
        public float Yaw { get; set; }
        public float Pitch { get; set; }
        public float Distance { get; set; }
        public float TargetX { get; set; }
        public float TargetY { get; set; }
        public float TargetZ { get; set; }
        public double DaysPerSecond { get; set; } = 1.0;
        public bool Paused { get; set; }
        public double SimDays { get; set; }
        public int FocusIndex { get; set; } = -1;
        public bool ShowOrbits { get; set; } = true;
        public bool ShowAxes { get; set; }
        public bool ShowLabels { get; set; } = true;
        public bool ShowTrails { get; set; } = true;
        public bool ShowDwarfs { get; set; } = true;
        public bool ShowConstellations { get; set; }
        public bool ShowHud { get; set; }
        public bool SolarWindEnabled { get; set; } = true;
        public bool SolarFlaresEnabled { get; set; } = true;
        public bool RealScale { get; set; }
        public bool LightTime { get; set; }
        // S9–S11 toggles.
        public bool ShowProbes { get; set; } = true;
        public bool ShowLagrange { get; set; }
        public bool ShowMeteors { get; set; } = true;
    }

    private void TryLoadPersistedState()
    {
        try
        {
            if (!File.Exists(StateFilePath)) return;
            var json = File.ReadAllText(StateFilePath);
            var s = JsonSerializer.Deserialize<PersistedState>(json);
            if (s == null) return;

            // RealScale must be applied first so VisualRadii and camera limits are
            // already correct when we restore the camera distance below.
            if (s.RealScale != OrbitalMechanics.RealScale) ToggleRealScale();

            _camera.Yaw = s.Yaw;
            _camera.Pitch = s.Pitch;
            _camera.Distance = Math.Clamp(s.Distance, _camera.MinDistance, _camera.MaxDistance);
            _camera.Target = new Vector3(s.TargetX, s.TargetY, s.TargetZ);

            _daysPerSecond = s.DaysPerSecond;
            _paused = s.Paused;
            _simDays = s.SimDays;
            _focusIndex = s.FocusIndex;
            _showOrbits = s.ShowOrbits;
            _showAxes = s.ShowAxes;
            _showLabels = s.ShowLabels;
            _showTrails = s.ShowTrails;
            _showDwarfs = s.ShowDwarfs;
            _showConstellations = s.ShowConstellations;
            _constellations.Enabled = _showConstellations;
            _showHud = s.ShowHud;
            _solarWind.Enabled = s.SolarWindEnabled;
            _solarFlares.Enabled = s.SolarFlaresEnabled;
            _lightTime = s.LightTime;

            _showProbes = s.ShowProbes;
            _showLagrange = s.ShowLagrange;
            _showMeteors = s.ShowMeteors;

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
                Paused = _paused,
                SimDays = _simDays,
                FocusIndex = _focusIndex,
                ShowOrbits = _showOrbits,
                ShowAxes = _showAxes,
                ShowLabels = _showLabels,
                ShowTrails = _showTrails,
                ShowDwarfs = _showDwarfs,
                ShowConstellations = _showConstellations,
                ShowHud = _showHud,
                SolarWindEnabled = _solarWind.Enabled,
                SolarFlaresEnabled = _solarFlares.Enabled,
                RealScale = OrbitalMechanics.RealScale,
                LightTime = _lightTime,
                ShowProbes = _showProbes,
                ShowLagrange = _showLagrange,
                ShowMeteors = _showMeteors,
            };
            string? dir = Path.GetDirectoryName(StateFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(StateFilePath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
            Debug.WriteLine($"[state] saved to {StateFilePath}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[state] save failed: {ex.Message}");
        }
    }
}
