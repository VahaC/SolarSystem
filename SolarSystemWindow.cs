using System.Diagnostics;
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

    private double _simDays;          // days since J2000
    private double _daysPerSecond = 1.0;
    private bool _paused;             // freezes sim time without resetting _daysPerSecond
    private bool _showOrbits = true;
    private bool _showAxes;
    private bool _showLabels = true;
    private bool _showTrails = true;
    private bool _showDwarfs = true;
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

        _camera.Aspect = ClientSize.X / (float)ClientSize.Y;
        _camera.ResetDefault();
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        _renderer.FramebufferSize = new Vector2i(e.Width, e.Height);
        _camera.Aspect = e.Width / (float)Math.Max(1, e.Height);
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
            if (p.RotationPeriodHours != 0.0)
            {
                double angle = (_simDays * 24.0 / p.RotationPeriodHours) * TwoPi;
                angle %= TwoPi;
                if (angle < 0) angle += TwoPi;
                p.RotationAngleRad = (float)angle;
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
            Vector3 end = _focusIndex >= 0 ? _planets[_focusIndex].Position : Vector3.Zero;
            _camera.Target = Vector3.Lerp(_focusStartTarget, end, s);
            _camera.Distance = MathHelper.Lerp(_focusStartDistance, _focusEndDistance, s);
            if (t >= 1f) _focusTransitioning = false;
        }
        else if (_focusIndex >= 0)
        {
            _camera.Target = _planets[_focusIndex].Position;
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

        // Clear stale seek-feedback message after a few seconds.
        if (_seekFeedback.Length > 0 && GLFW.GetTime() > _seekFeedbackUntil)
            _seekFeedback = "";

        // Title with current sim date
        var date = OrbitalMechanics.J2000.AddDays(_simDays);
        string speedStr = _paused
            ? "PAUSED"
            : $"{(_daysPerSecond < 0 ? "◀ " : "")}x{Math.Abs(_daysPerSecond):0.##} days/s";
        Title = $"Solar System  |  {date:yyyy-MM-dd}  |  speed {speedStr}  |  [Space] pause  [, .] reverse/forward  [+/-] speed  [0-8] focus  [O] orbits  [T] trails  [L] labels  [W] wind  [F] flares  [R] scale  [D] dwarfs";
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
                    if (_focusIndex >= _dwarfStart) FocusOn(-1);
                    if (_selectedIndex >= _dwarfStart) _selectedIndex = -2;
                    // Clear stale dwarf trails so they don't reappear as a frozen line strip
                    // on the next toggle-on.
                    for (int i = _dwarfStart; i < _planets.Length; i++) _planets[i].TrailReset();
                }
                break;
            case Keys.W: _solarWind.Enabled = !_solarWind.Enabled; break;
            case Keys.F: _solarFlares.Enabled = !_solarFlares.Enabled; break;
            case Keys.R: ToggleRealScale(); break;

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

        _renderer.DrawStars(_camera);
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

        var saturn = _planets[5];
        _renderer.DrawSaturnRing(_camera, saturn);

        _belt.Draw(_camera);
        _comet.DrawTail(_camera);
        _solarWind.Draw(_camera);
        _solarFlares.Draw(_camera);

        if (_showAxes) _renderer.DrawPlanetAxes(_camera, visible);

        // Apply HDR bright-pass + Gaussian blur + additive composite to the
        // default framebuffer. All subsequent 2D overlays (labels, UI panels)
        // are drawn directly to the default framebuffer and therefore unaffected.
        _renderer.EndSceneAndApplyBloom();

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
            "LMB drag    orbit camera\n" +
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
            "J           jump to date / ±days\n" +
            "W           toggle solar wind\n" +
            "F           toggle solar flares\n" +
            "R           real / compressed scale\n" +
            "Esc         quit";
        _renderer.DrawText(_font, help, 12, 78, 13, dim);

        // Info panel (multi-line) for selected body
        string info;
        if (_selectedIndex >= 0)
        {
            var p = _planets[_selectedIndex];
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

        // Date-seek prompt: top-center modal overlay while active. Drawn after every
        // other UI so it can't be occluded.
        if (_seekActive)
        {
            string prompt = $"Jump to date (YYYY-MM-DD) or +/-N days:\n> {_seekBuffer}_";
            _renderer.DrawText(_font, prompt,
                _renderer.FramebufferSize.X * 0.5f - 200f, 20f, 16f,
                new Vector4(1f, 1f, 0.7f, 1f));
        }
        else if (_seekFeedback.Length > 0)
        {
            _renderer.DrawText(_font, _seekFeedback,
                _renderer.FramebufferSize.X * 0.5f - 160f, 20f, 14f,
                new Vector4(1f, 0.9f, 0.5f, 0.9f));
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
        _camera.HandleMouseMove(new Vector2(e.X, e.Y));
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
        return best;
    }

    private void FocusOn(int index)
    {
        if (index == -1)
        {
            _focusIndex = -1;
            BeginFocusTransition(Vector3.Zero, MathF.Max(_camera.Distance, SunRadius * 6f));
        }
        else if (index >= 0 && index < _planets.Length)
        {
            _focusIndex = index;
            // Make sure the planet's world position is up-to-date for the *current* sim
            // time so the transition aims at where the planet actually is right now.
            var p = _planets[index];
            p.HelioAU = OrbitalMechanics.HeliocentricPosition(p, _simDays);
            float s = OrbitalMechanics.OrbitWorldScale(p.SemiMajorAxisAU);
            p.Position = new Vector3(
                (float)(p.HelioAU.X * s),
                (float)(p.HelioAU.Y * s),
                (float)(p.HelioAU.Z * s));
            float endDist = MathF.Max(p.VisualRadius * 6f, _camera.MinDistance * 4f);
            BeginFocusTransition(p.Position, endDist);
            Debug.WriteLine($"[focus] {p.Name} target={p.Position} dist={endDist:0.##}");
        }
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
        else if (_focusIndex < _planets.Length)
        {
            _camera.Distance = MathF.Max(_planets[_focusIndex].VisualRadius * 6f, _camera.MinDistance * 4f);
        }

        Debug.WriteLine($"[scale] {(OrbitalMechanics.RealScale ? "REAL (1 AU = 50 units)" : "compressed (a^0.45)")}");
    }

    private void ClearAllTrails()
    {
        if (_planets == null) return;
        foreach (var p in _planets) p.TrailReset();
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
        if (!_seekActive) return;
        if (_seekSwallowNextChar) { _seekSwallowNextChar = false; return; }
        // Cap to avoid pathological pastes; only printable characters land here.
        if (_seekBuffer.Length < 32)
            _seekBuffer += e.AsString;
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
        _solarWind.Dispose();
        _solarFlares.Dispose();
        _belt.Dispose();
        _comet.Dispose();
        _renderer.Dispose();
        _font.Dispose();
        base.OnUnload();
    }
}
