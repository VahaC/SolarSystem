using System.Diagnostics;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace SolarSystem;

public sealed class SolarSystemWindow : GameWindow
{
    public const float SunRadius = 5f;

    // Moon orbit parameters (visual, not to scale).
    private const float MoonOrbitRadius = 4.0f;        // world units from Earth's center
    private const float MoonOrbitInclinationDeg = 5.145f;
    private const double MoonOrbitalPeriodDays = 27.321661;

    private readonly Renderer _renderer = new();
    private readonly Camera _camera = new();
    private readonly SolarWind _solarWind = new();
    private BitmapFont _font = null!;
    private Planet[] _planets = null!;
    private Planet _moon = null!;

    private double _simDays;          // days since J2000
    private double _daysPerSecond = 1.0;
    private bool _showOrbits = true;
    private bool _showAxes;
    private bool _showLabels = true;
    private int _focusIndex = -1;     // -1 = sun, 0..7 = planet
    private int _selectedIndex = -2;  // -2 = none, -1 = sun, 0..7 = planet

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
        _font = new BitmapFont();

        _planets = Planet.CreateAll();
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

        // Moon orbits Earth in a circle inclined to the ecliptic. Position is purely visual.
        {
            var earth = _planets[2];
            double moonAngle = (_simDays / MoonOrbitalPeriodDays) * TwoPi;
            float cx = (float)Math.Cos(moonAngle) * MoonOrbitRadius;
            float cz = (float)Math.Sin(moonAngle) * MoonOrbitRadius;
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

        if (_focusIndex >= 0)
            _camera.Target = _planets[_focusIndex].Position;

        _solarWind.Update((float)args.Time, Vector3.Zero, SunRadius);

        // Title with current sim date
        var date = OrbitalMechanics.J2000.AddDays(_simDays);
        Title = $"Solar System  |  {date:yyyy-MM-dd}  |  speed x{_daysPerSecond:0.##} days/s  |  [+/-] speed  [0-8] focus  [O] orbits  [L] labels  [W] wind";
    }

    /// <summary>One-shot keyboard handling. More reliable than polling KeyboardState.IsKeyPressed
    /// every frame: the GLFW key event fires exactly once per physical press, with no risk of
    /// missing the press window between two update ticks.</summary>
    protected override void OnKeyDown(KeyboardKeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.IsRepeat) return;

        switch (e.Key)
        {
            case Keys.Escape:
                Close();
                break;

            case Keys.O: _showOrbits = !_showOrbits; break;
            case Keys.A: _showAxes = !_showAxes; break;
            case Keys.L: _showLabels = !_showLabels; break;
            case Keys.W: _solarWind.Enabled = !_solarWind.Enabled; break;

            case Keys.KeyPadAdd:
            case Keys.Equal:
                _daysPerSecond = Math.Min(1000.0, _daysPerSecond * 1.5);
                break;
            case Keys.KeyPadSubtract:
            case Keys.Minus:
                _daysPerSecond = Math.Max(0.1, _daysPerSecond / 1.5);
                break;

            case Keys.D0:
            case Keys.KeyPad0:
                _focusIndex = -1;
                _selectedIndex = -1;
                _camera.ResetDefault();
                _camera.Target = Vector3.Zero;
                Debug.WriteLine("[focus] Sun (reset view)");
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
        _renderer.Clear();

        _renderer.DrawStars(_camera);
        if (_showOrbits) _renderer.DrawOrbits(_camera, _planets);

        _renderer.DrawSun(_camera, Vector3.Zero, SunRadius);
        foreach (var p in _planets)
            _renderer.DrawPlanet(_camera, p, Vector3.Zero);
        _renderer.DrawPlanet(_camera, _moon, Vector3.Zero);

        var saturn = _planets[5];
        _renderer.DrawSaturnRing(_camera, saturn);

        _solarWind.Draw(_camera);

        if (_showAxes) _renderer.DrawPlanetAxes(_camera, _planets);

        // Labels
        if (_showLabels)
        {
            _renderer.DrawLabel(_font, _camera,
                new Vector3(0, SunRadius + 1.5f, 0), "Sun", 14, new Vector4(1, 0.9f, 0.5f, 0.95f));
            foreach (var p in _planets)
                _renderer.DrawLabel(_font, _camera,
                    p.Position + new Vector3(0, p.VisualRadius + 1.0f, 0),
                    p.Name, 13, new Vector4(0.85f, 0.9f, 1f, 0.95f));
            _renderer.DrawLabel(_font, _camera,
                _moon.Position + new Vector3(0, _moon.VisualRadius + 0.5f, 0),
                _moon.Name, 12, new Vector4(0.85f, 0.85f, 0.85f, 0.9f));
        }

        // UI overlay
        var date = OrbitalMechanics.J2000.AddDays(_simDays);
        var white = new Vector4(1f, 1f, 1f, 0.95f);
        _renderer.DrawText(_font, $"Date    {date:yyyy-MM-dd}", 12, 12, 16, white);
        _renderer.DrawText(_font, $"Speed   {_daysPerSecond:0.##} d/s", 12, 32, 16, white);
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
            "+ / -       sim speed\n" +
            "O           toggle orbits\n" +
            "L           toggle labels\n" +
            "A           toggle axes\n" +
            "W           toggle solar wind\n" +
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
            _camera.Target = Vector3.Zero;
            _camera.Distance = MathF.Max(_camera.Distance, SunRadius * 6f);
        }
        else if (index >= 0 && index < _planets.Length)
        {
            _focusIndex = index;
            // Make sure the planet's world position is up-to-date for the *current* sim
            // time so the camera snaps directly onto it without a one-frame lag.
            var p = _planets[index];
            p.HelioAU = OrbitalMechanics.HeliocentricPosition(p, _simDays);
            float s = OrbitalMechanics.OrbitWorldScale(p.SemiMajorAxisAU);
            p.Position = new Vector3(
                (float)(p.HelioAU.X * s),
                (float)(p.HelioAU.Y * s),
                (float)(p.HelioAU.Z * s));
            _camera.Target = p.Position;
            _camera.Distance = MathF.Max(p.VisualRadius * 6f, 12f);
            Debug.WriteLine($"[focus] {p.Name} target={p.Position} dist={_camera.Distance:0.##}");
        }
    }

    protected override void OnUnload()
    {
        _solarWind.Dispose();
        _renderer.Dispose();
        _font.Dispose();
        base.OnUnload();
    }
}
