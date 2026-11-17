using System.Diagnostics;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace SolarSystem;

// PROTOTYPE (2026-11-17): arcade flight mode — the glue between the window and the
// flight systems (ShipFlight / ChaseCamera / ShipRenderer / ShipFx / RockField).
// SolarSystemWindow.cs only calls the hooks in this file.
//
// FLIGHT-GLUE-READY — read the "Demo runner" notes right below before using it.
//
// Demo runner (development tool, no window appears):
//   <exe> --flight-demo [--seconds 12] [--shot-every 1] [--view chase|orbit|side|front]
//         [--out <dir>] [--size 1600x900] [--hud] [--stick X,Y] [--race] [--wav <file>]
//         [--solo engine|music|sfx] [--flying-start]
//   * AUDIO-GLUE-READY: --wav <file> renders the flight's sound offline (no audio device
//     is opened, nothing is heard) into a 16-bit stereo 48 kHz WAV, exactly 800 samples
//     per frame; done.txt then also reports the mix's peak and RMS in dBFS. --solo renders
//     one layer of the mix alone (to check the balance; see FlightAudio for the levels).
//   * Every run starts from the same date with sound and music on, whatever the user's
//     saved state holds, so the same command always gives the same race and the same WAV.
//   * RACE-GLUE-READY: --race starts the gate race at once (1 s countdown) with a simple
//     pilot that steers at the next gate; done.txt then also reports the race (phase,
//     gates passed / missed, time). --view gate parks the camera beside the next gate.
//     --flying-start instead leaves the ship in free flight and lets the same pilot fly
//     through the first gate, which starts the race without Enter.
//   * --hud draws the flight HUD into the screenshots; --stick X,Y flies by the mouse
//     joystick alone, held X,Y stick radii from the screen centre (+X right, +Y up).
//   * Writes <dir>\flight_000.png, flight_001.png, ... then <dir>\done.txt (frame count
//     and average frame time) and exits with code 0.
//   * If anything throws (for example a GLSL compile error), the full exception text is
//     written to <dir>\error.txt and the exit code is 1 — read that file.
//   * The exe is a GUI-subsystem program: PowerShell's `& exe ...` does NOT wait for it and
//     shows no output. Wait for it explicitly, e.g.
//       Start-Process -FilePath <exe> -ArgumentList '--flight-demo','--view','chase','--out','<dir>' -Wait
//     or run it from the Bash tool (which waits). Then read done.txt / error.txt and the PNGs.
//   * Each run takes roughly 10–20 s, most of it loading the 8K planet textures.
//   * Views: chase = the gameplay camera; orbit = circles the ship once, close up;
//     side = beside the ship; front = ahead of the ship looking back.

public sealed partial class SolarSystemWindow
{
    /// <summary>When set before <see cref="OpenTK.Windowing.Desktop.GameWindow.Run()"/>,
    /// runs the hidden scripted flight demo (see <see cref="FlightDemoJob"/>).</summary>
    public FlightDemoJob? FlightDemo { get; init; }

    private bool _flightActive;
    private readonly FlightContext _flightCtx = new();
    private readonly ShipFlight _shipFlight = new();
    private readonly ChaseCamera _chaseCam = new();
    private readonly ShipRenderer _shipRenderer = new();
    private readonly ShipFx _shipFx = new();
    private readonly RockField _rockField = new();
    // Gate race: rules + track, gate visuals, race display.
    private readonly RaceSession _race = new();
    private readonly RaceGates _raceGates = new();
    private readonly RaceHud _raceHud = new();
    private readonly RingDust _ringDust = new();
    // Sound: mixer + shared state + one-shot effects (created on first use).
    private readonly FlightAudio _flightAudio = new();
    private ShipInput _lastFlightInput;
    private float _audioPrevBoost, _audioBoostIgniteTime = -10f, _audioRockHitTime = -10f;
    private bool _audioPrevDry;
    private int _audioPrevCountdown;
    private RaceEvent _audioSeenEvent;
    private float _audioSeenEventClock = float.NegativeInfinity;
    private int _audioSeenEventGate = -1;
    private float _audioPrevSpeed01, _audioAccel, _audioPrevRockProximity, _audioRockPassTime = -10f;
    private bool _audioPrevBrake, _audioHitThisFrame;
    /// <summary>Gates passed in a row without a miss in the current race.</summary>
    private int _raceCombo;
    /// <summary>How near a rock has to come (world units from the ship's centre) to count
    /// as a near miss for the sound.</summary>
    private const float RockNearRadius = 0.11f;
    private readonly List<float> _demoAudio = new();
    private float[] _demoAudioBlock = new float[AudioFormat.SampleRate / FlightDemoJob.Fps * AudioFormat.Channels];
    private int _racePassedSeen;
    /// <summary>The run (<see cref="RaceView.RunNumber"/>) the per-run state belongs to.</summary>
    private int _raceRunSeen;
    /// <summary>Boost reserve 0 .. 1 and whether it ran dry (boost stays off until it has
    /// recovered a little).</summary>
    private float _boostEnergy = 1f;
    private bool _boostDry;
    private const float BoostDrainPerSecond = 1f / 3.5f;
    private const float BoostRefillPerSecond = 1f / 10f;
    private const float BoostPerGate = 0.25f;
    private const float BoostRecoverLevel = 0.2f;
    private FlightSetup? _flightRestore;
    private readonly Stopwatch _demoClock = new();
    private int _demoTimedFrames;

    /// <summary>Index of the planet flight mode starts at (Saturn).</summary>
    private const int FlightAnchorPlanet = 5;
    /// <summary>Saturn's ring band in planet radii — the same numbers the planet and
    /// ring shaders use.</summary>
    private const float RingInnerFactor = 1.35f, RingOuterFactor = 2.4f;
    /// <summary>Simulation speed while flying: slow enough to feel real-time, fast
    /// enough that the planet visibly turns.</summary>
    private const double FlightDaysPerSecond = 0.02;
    /// <summary>World units to km for the speed read-out (Saturn: 3.4 units = 58 232 km).</summary>
    private const double FlightKmPerUnit = 58232.0 / 3.4;

    /// <summary>What the user had before flight mode took the scene over.</summary>
    private sealed class FlightSetup
    {
        public bool RealScale;
        public SimulationMode SimMode;
        public double SimDays, DaysPerSecond;
        public bool Paused, Orbits, Trails, Axes, Probes, Lagrange, Alignment, TidalLock, Constellations;
        public float Yaw, Pitch, Distance, Fov;
        public Vector3 Target;
        public int FocusIndex, SelectedIndex;
    }

    private void InitializeFlight()
    {
        _shipRenderer.Initialize();
        _shipFx.Initialize();
        _rockField.Initialize();
        _raceGates.Initialize();
        _raceHud.Initialize();
        _ringDust.Initialize();
        ResizeFlight(new Vector2(ClientSize.X, ClientSize.Y));
    }

    private void ResizeFlight(Vector2 viewport)
    {
        _shipFx.SetViewport(viewport);
        _rockField.SetViewport(viewport);
        _raceGates.SetViewport(viewport);
        _ringDust.SetViewport(viewport);
    }

    private void DisposeFlight()
    {
        _shipRenderer.Dispose();
        _shipFx.Dispose();
        _rockField.Dispose();
        _raceGates.Dispose();
        _raceHud.Dispose();
        _ringDust.Dispose();
        _flightAudio.Dispose();
    }

    private void RegisterFlightEntries(FeatureRegistry r)
    {
        r.Add(new Command
        {
            Id = "flight", Category = FeatureCategory.Simulation, LabelKey = "ui.cmd.flight",
            Run = ToggleFlight,
            Status = () => Localization.T(_flightActive ? "ui.flight.status.on" : "ui.flight.status.off"),
            ShowInPanel = true,
        }).WithKey(Keys.F);
        r.Add(new Feature
        {
            Id = "sound", Category = FeatureCategory.Interface, LabelKey = "ui.settings.sound",
            Get = () => _flightAudio.SoundEnabled,
            Set = v => { _flightAudio.SoundEnabled = v; _flightAudio.ApplySettings(); },
            Default = true,
        }).WithKey(Keys.M);
        r.Add(new Feature
        {
            Id = "music", Category = FeatureCategory.Interface, LabelKey = "ui.settings.music",
            Get = () => _flightAudio.MusicEnabled,
            Set = v => { _flightAudio.MusicEnabled = v; _flightAudio.ApplySettings(); },
            Default = true,
        });
        r.Add(new Slider
        {
            Id = "sound.volume", Category = FeatureCategory.Interface, LabelKey = "ui.settings.sound.volume",
            Get = () => _flightAudio.Volume,
            Set = v => { _flightAudio.Volume = (float)v; _flightAudio.ApplySettings(); },
            Min = 0.0, Max = 1.0, Step = 0.05, Default = 0.8, Format = "{0:0.00}",
        });
    }

    private void ToggleFlight()
    {
        if (_flightActive)
        {
            ExitFlight();
            ShowBanner(Localization.T("ui.flight.off"), 2.5);
        }
        else
        {
            EnterFlight();
            ShowBanner(Localization.T("ui.flight.on"), 4.0);
        }
    }

    /// <summary>Seconds this frame advances: the wall clock, or a fixed step in the demo
    /// so its output does not depend on how fast the hidden window renders.</summary>
    private double SimFrameSeconds(double wallSeconds)
        => FlightDemo != null ? 1.0 / FlightDemoJob.Fps : wallSeconds;

    // ---- Entering / leaving ---------------------------------------------------------------

    private void EnterFlight()
    {
        if (_flightActive) return;
        _flightRestore = new FlightSetup
        {
            RealScale = OrbitalMechanics.RealScale,
            SimMode = _simMode,
            SimDays = TargetSimDays,
            DaysPerSecond = _daysPerSecond,
            Paused = _paused,
            Orbits = _showOrbits, Trails = _showTrails, Axes = _showAxes, Probes = _showProbes,
            Lagrange = _showLagrange, Alignment = _showAlignment, TidalLock = _showTidalLock,
            Constellations = _showConstellations,
            Yaw = _camera.Yaw, Pitch = _camera.Pitch, Distance = _camera.Distance, Fov = _camera.FovDeg,
            Target = _camera.Target,
            FocusIndex = _focusIndex, SelectedIndex = _selectedIndex,
        };

        // Flight is flown in the artistic scale (big planets) on the analytic orbits.
        if (OrbitalMechanics.RealScale) ToggleRealScale();
        if (_simMode != SimulationMode.Ephemeris) SetSimulationMode(SimulationMode.Ephemeris);
        _physicsTarget = null;
        _simDays = BestLitRingDate(_simDays);
        _daysPerSecond = FlightDaysPerSecond;
        _paused = false;
        // No sandbox overlays in the scene: just space, the planet and the ship.
        _showOrbits = _showTrails = _showAxes = _showProbes = false;
        _showLagrange = _showAlignment = _showTidalLock = _showConstellations = false;
        _focusIndex = -1;
        _focusTransitioning = false;
        ClearAllTrails();

        _flightActive = true;
        PlaceShipAtSpawn();

        // Sound: the mixer starts on the first flight; the ship powers up.
        _flightAudio.Offline = FlightDemo != null;
        _flightAudio.EnsureStarted();
        _flightAudio.State.Active = 1f;
        _audioPrevBoost = 0f;
        _audioPrevDry = false;
        _audioPrevCountdown = 0;
        _audioSeenEvent = _race.View.LastEvent;
        _audioSeenEventClock = _race.View.LastEventClock;
        _audioSeenEventGate = _race.View.LastEventGate;
        _audioPrevSpeed01 = 0f;
        _audioAccel = 0f;
        _audioPrevRockProximity = 0f;
        _audioPrevBrake = false;
        _raceCombo = 0;
        _flightAudio.Play(FlightSoundEvent.EnterFlight);
    }

    private void ExitFlight()
    {
        if (!_flightActive) return;
        _flightActive = false;
        _stickActive = false;
        _race.Abort();
        _flightAudio.Play(FlightSoundEvent.ExitFlight);
        _flightAudio.State.Active = 0f;
        _camera.ClearOverride();
        if (_flightRestore is not { } s) return;
        _flightRestore = null;

        if (OrbitalMechanics.RealScale != s.RealScale) ToggleRealScale();
        _physicsTarget = null;
        _simDays = s.SimDays;
        if (s.SimMode != SimulationMode.Ephemeris) SetSimulationMode(s.SimMode);
        _daysPerSecond = s.DaysPerSecond;
        _paused = s.Paused;
        _showOrbits = s.Orbits; _showTrails = s.Trails; _showAxes = s.Axes; _showProbes = s.Probes;
        _showLagrange = s.Lagrange; _showAlignment = s.Alignment; _showTidalLock = s.TidalLock;
        _showConstellations = s.Constellations;
        ClearAllTrails();
        _camera.Yaw = s.Yaw;
        _camera.Pitch = s.Pitch;
        _camera.Distance = s.Distance;
        _camera.FovDeg = s.Fov;
        _camera.Target = s.Target;
        _focusIndex = s.FocusIndex;
        _selectedIndex = s.SelectedIndex;
        _focusTransitioning = false;
    }

    /// <summary>Unit normal of the anchor planet's ring plane — the same rotation the
    /// ring mesh gets (tilt about world Z).</summary>
    private static Vector3 RingNormalOf(Planet p)
        => Vector3.TransformNormal(Vector3.UnitY, Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(p.AxisTiltDeg))).Normalized();

    /// <summary>The rings only look good when the Sun stands well above their plane, and
    /// that depends on where the planet is on its orbit. Search forward from
    /// <paramref name="fromDays"/> (one planet year, monthly steps) for the best-lit date.</summary>
    private double BestLitRingDate(double fromDays)
    {
        var p = _planets[FlightAnchorPlanet];
        var n = RingNormalOf(p);
        double best = fromDays, bestLit = -1.0;
        double span = Math.Max(1.0, p.OrbitalPeriodYears) * 365.25;
        for (double d = 0.0; d <= span; d += 30.0)
        {
            var h = OrbitalMechanics.HeliocentricPosition(p, fromDays + d);
            var toSun = -new Vector3((float)h.X, (float)h.Y, (float)h.Z).Normalized();
            double lit = Math.Abs(Vector3.Dot(n, toSun));
            // Take the first date that is nearly as good as it gets (sin 26.7° ≈ 0.45).
            if (lit > bestLit + 1e-4) { bestLit = lit; best = fromDays + d; }
            if (lit >= 0.42) break;
        }
        return best;
    }

    /// <summary>Fill the per-frame anchor fields (the planet moved since last frame).</summary>
    private void FillFlightAnchor()
    {
        var p = _planets[FlightAnchorPlanet];
        var c = _flightCtx;
        c.AnchorPosition = p.Position;
        c.AnchorRadius = p.VisualRadius;
        c.AnchorColor = p.ProceduralColor;
        c.HasRing = true;
        c.RingNormal = RingNormalOf(p);
        c.RingInner = p.VisualRadius * RingInnerFactor;
        c.RingOuter = p.VisualRadius * RingOuterFactor;
        c.SunPosition = Vector3.Zero;
    }

    /// <summary>Put the ship inside the ring band on the sunlit side of the planet, a
    /// little above the lit face of the rings, heading along the ring and slightly inward
    /// so the planet fills one side of the view.</summary>
    private void PlaceShipAtSpawn()
    {
        var p = _planets[FlightAnchorPlanet];
        // The planet's position for the (possibly new) date — OnUpdateFrame refreshes it
        // every frame, but flight can be entered between two updates.
        p.HelioAU = OrbitalMechanics.HeliocentricPosition(p, _simDays);
        float s = OrbitalMechanics.OrbitWorldScale(p.SemiMajorAxisAU);
        p.Position = new Vector3((float)(p.HelioAU.X * s), (float)(p.HelioAU.Y * s), (float)(p.HelioAU.Z * s));
        FillFlightAnchor();
        var c = _flightCtx;
        c.Dt = 0f;
        c.Time = 0f;

        var n = c.RingNormal;
        var toSun = (c.SunPosition - c.AnchorPosition).Normalized();
        float side = Vector3.Dot(n, toSun) >= 0f ? 1f : -1f;
        var up = n * side;                                   // toward the lit face
        var sunInPlane = toSun - n * Vector3.Dot(n, toSun);
        sunInPlane = sunInPlane.LengthSquared > 1e-8f ? sunInPlane.Normalized() : Vector3.Cross(n, Vector3.UnitX).Normalized();
        // 40° around the planet from the sub-solar direction: the planet's lit face is ahead.
        var radial = Vector3.Transform(sunInPlane, Quaternion.FromAxisAngle(up, MathHelper.DegreesToRadians(40f))).Normalized();
        var tangent = Vector3.Cross(up, radial).Normalized();
        float radius = 0.5f * (c.RingInner + c.RingOuter);

        // The race track is laid out on this ring, starting right here.
        var frame = new RingFrame
        {
            Radial = radial, Tangent = tangent, Up = up,
            AnchorRadius = c.AnchorRadius, RingInner = c.RingInner, RingOuter = c.RingOuter,
        };
        _race.BestTimesPath = FlightDemo != null ? null : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SolarSystem", "race-best.txt");
        _race.Build(frame);
        _rockField.SetClearZones(_race.View.ClearZones);
        _raceRunSeen = _race.View.RunNumber;
        _racePassedSeen = 0;
        _boostEnergy = 1f;
        _boostDry = false;

        // Free flight starts a little before the start line and above it, heading along
        // the ring and slightly inward: the first gates are ahead, the planet off to the left.
        const float back = -0.07f;
        var radialHere = (radial * MathF.Cos(back) + tangent * MathF.Sin(back)).Normalized();
        var tangentHere = Vector3.Cross(up, radialHere).Normalized();
        var forward = (tangentHere * MathF.Cos(MathHelper.DegreesToRadians(18f))
                       - radialHere * MathF.Sin(MathHelper.DegreesToRadians(18f))
                       - up * 0.06f).Normalized();
        PlaceShip(frame.Point(radius, back, 0.2f), forward, up);
    }

    /// <summary>Put the ship somewhere new (anchor-relative) and snap the camera and the
    /// effects to it.</summary>
    private void PlaceShip(Vector3 localPosition, Vector3 forward, Vector3 up)
    {
        var c = _flightCtx;
        _shipFlight.Reset(localPosition, forward, up);
        _shipFlight.WriteTo(c);
        _chaseCam.Reset(c);
        _chaseCam.ApplyTo(_camera);
        _shipFx.Reset(c);
    }

    // ---- Gate race ------------------------------------------------------------------------

    /// <summary>Enter: put the ship on the start line and start the countdown (also
    /// restarts a race in progress).</summary>
    private void StartRace()
    {
        var v = _race.View;
        if (v.Gates.Count == 0) return;
        _race.Start();
        PlaceShip(v.StartPosition, v.StartForward, v.StartUp);
        // Standing start: the ship holds on the line and launches at the signal.
        _shipFlight.Velocity = Vector3.Zero;
        _shipFlight.WriteTo(_flightCtx);
        _boostEnergy = 1f;
        _boostDry = false;
        _paused = false;
        _racePassedSeen = 0;
        _raceCombo = 0;
    }

    /// <summary>Keys flight mode takes before the feature registry. Returns true when the
    /// key was used.</summary>
    private bool HandleFlightKey(OpenTK.Windowing.Common.KeyboardKeyEventArgs e)
    {
        if (!_flightActive || e.IsRepeat) return false;
        const KeyModifiers Chord = KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt;
        // Shift may be held (boost) when Enter is pressed.
        if ((e.Key == Keys.Enter || e.Key == Keys.KeyPadEnter) && (e.Modifiers & Chord & ~KeyModifiers.Shift) == 0)
        {
            StartRace();
            return true;
        }
        return false;
    }

    /// <summary>The boost reserve: drains while boosting, refills slowly, and cuts the
    /// boost out when empty until it has recovered a little.</summary>
    private void ApplyBoostEnergy(ref ShipInput input, float dt)
    {
        if (_boostDry && _boostEnergy >= BoostRecoverLevel) _boostDry = false;
        if (_boostDry || _boostEnergy <= 0f) input.Boost = false;
        if (dt <= 0f) return;
        if (input.Boost)
        {
            _boostEnergy -= BoostDrainPerSecond * dt;
            if (_boostEnergy <= 0f) { _boostEnergy = 0f; _boostDry = true; }
        }
        else _boostEnergy = MathF.Min(1f, _boostEnergy + BoostRefillPerSecond * dt);
    }

    /// <summary>After the race advanced: every gate passed tops the boost reserve up.
    /// Counted from the gate statuses rather than the last event, so two gates (or a gate
    /// and the finish) in one frame both count.</summary>
    private void ReactToRaceEvents()
    {
        // A new run began — from the start line (Enter) or by flying through the first
        // gate: a full boost reserve and a clean slate.
        if (_race.View.RunNumber != _raceRunSeen)
        {
            _raceRunSeen = _race.View.RunNumber;
            _racePassedSeen = 0;
            _raceCombo = 0;
            _boostEnergy = 1f;
            _boostDry = false;
            // A flying start has no countdown: the start signal sounds with the first gate.
            if (_race.View.FlyingStart) _flightAudio.Play(FlightSoundEvent.Go);
        }

        int passed = 0;
        foreach (var s in _race.View.Status) if (s == GateStatus.Passed) passed++;
        if (passed > _racePassedSeen)
        {
            _boostEnergy = MathF.Min(1f, _boostEnergy + BoostPerGate * (passed - _racePassedSeen));
            if (_boostEnergy >= BoostRecoverLevel) _boostDry = false;
        }
        _racePassedSeen = passed;
    }

    /// <summary>The demo's race pilot: steer at the next gate — first at a point in front
    /// of it so the ship lines up with its axis, then straight through — boosting when it
    /// is far and lined up, braking when a sharp correction is needed close in.</summary>
    private ShipInput RacePilot()
    {
        var c = _flightCtx;
        var v = _race.View;
        // --flying-start: no Enter — aim at the first gate from free flight.
        bool flying = FlightDemo is { FlyingStart: true } && v.Phase == RacePhase.Idle;
        if ((v.Phase != RacePhase.Running && !flying) || v.NextGate >= v.Gates.Count) return DemoPilot();
        var g = v.Gates[v.NextGate];
        var gatePos = v.GateWorld(v.NextGate, c);
        float dist = (gatePos - c.ShipPosition).Length;
        float along = Vector3.Dot(gatePos - c.ShipPosition, g.Forward);     // > 0 while the gate is ahead on its axis
        var target = along > 0.12f
            ? gatePos - g.Forward * MathF.Min(0.28f, along * 0.45f)
            : gatePos + g.Forward * 0.2f;
        var to = target - c.ShipPosition;
        if (to.LengthSquared < 1e-10f) return new ShipInput();
        var dir = to.Normalized();
        float x = Vector3.Dot(dir, c.ShipRight), y = Vector3.Dot(dir, c.ShipUp), z = Vector3.Dot(dir, c.ShipForward);
        float yawErr = MathF.Atan2(x, z);
        float pitchErr = MathF.Atan2(y, MathF.Sqrt(x * x + z * z));
        float off = MathF.Max(MathF.Abs(yawErr), MathF.Abs(pitchErr));
        return new ShipInput
        {
            Yaw = MathHelper.Clamp(yawErr * 2.2f, -1f, 1f),
            Pitch = MathHelper.Clamp(pitchErr * 2.2f, -1f, 1f),
            Boost = dist > 0.45f && off < 0.12f,
            Brake = dist < 0.35f && off > 0.5f,
        };
    }

    // ---- Per frame ------------------------------------------------------------------------

    // Mouse steering (see MouseStick): where the left button went down and where the
    // cursor is now, in window pixels.
    private bool _stickActive;
    private Vector2 _stickOrigin, _stickCursor;

    private ShipInput ReadFlightInput()
    {
        if (FlightDemo is { } demo)
        {
            if (demo.Stick is not { } held) { _stickActive = false; return demo.Race || demo.FlyingStart ? RacePilot() : DemoPilot(); }
            // --stick: pretend the left button is held with the cursor dragged this far
            // from the screen centre, and fly by the mouse alone.
            var size = new Vector2(_renderer.FramebufferSize.X, _renderer.FramebufferSize.Y);
            _stickActive = true;
            _stickOrigin = size * 0.5f;
            _stickCursor = _stickOrigin + new Vector2(held.X, -held.Y) * MouseStick.RadiusPx(size.Y);
            return AddMouseStick(new ShipInput());
        }

        var input = new ShipInput();
        var kb = KeyboardState;
        if (_seekActive || _searchActive || _palette.Active
            || kb.IsKeyDown(Keys.LeftAlt) || kb.IsKeyDown(Keys.RightAlt))
        {
            _stickActive = false;
            return input;
        }
        float Axis(Keys plusA, Keys plusB, Keys minusA, Keys minusB)
            => (kb.IsKeyDown(plusA) || kb.IsKeyDown(plusB) ? 1f : 0f) - (kb.IsKeyDown(minusA) || kb.IsKeyDown(minusB) ? 1f : 0f);
        input.Pitch = Axis(Keys.W, Keys.Up, Keys.S, Keys.Down);
        input.Yaw = Axis(Keys.D, Keys.Right, Keys.A, Keys.Left);
        input.Roll = Axis(Keys.E, Keys.E, Keys.Q, Keys.Q);
        input.Boost = kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift);
        input.Brake = kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.X);

        // Mouse: holding the left button turns the cursor into a joystick centred on the
        // point where the button went down. It adds to the keyboard.
        bool down = MouseState.IsButtonDown(MouseButton.Left);
        if (down && !_stickActive) _stickOrigin = _mousePos;
        _stickActive = down;
        _stickCursor = _mousePos;
        return AddMouseStick(input);
    }

    private ShipInput AddMouseStick(ShipInput input)
    {
        if (!_stickActive) return input;
        var s = MouseStick.Deflection(_stickOrigin, _stickCursor, MouseStick.RadiusPx(_renderer.FramebufferSize.Y));
        input.Yaw = MathHelper.Clamp(input.Yaw + s.X, -1f, 1f);
        input.Pitch = MathHelper.Clamp(input.Pitch + s.Y, -1f, 1f);
        return input;
    }

    /// <summary>The demo's pilot: takes the timing of boost, brake, roll and turns from
    /// <see cref="FlightDemoJob.Autopilot"/> and steers so the ship stays inside the ring —
    /// skimming just above the lit face, swaying between lanes of the band — instead of
    /// wandering off into empty space.</summary>
    private ShipInput DemoPilot()
    {
        var c = _flightCtx;
        float t = c.Time % 12f;
        var script = FlightDemoJob.Autopilot(c.Time);
        if (script.Roll != 0f) return script;                 // barrel roll: hands off

        var toSun = (c.SunPosition - c.AnchorPosition).Normalized();
        var up = c.RingNormal * (Vector3.Dot(c.RingNormal, toSun) >= 0f ? 1f : -1f);
        var rel = c.ShipPosition - c.AnchorPosition;
        float h = Vector3.Dot(rel, up);
        var inPlane = rel - up * h;
        float r = inPlane.Length;
        if (r < 1e-4f) return script;
        var radial = inPlane / r;
        var tangent = Vector3.Cross(up, radial);
        // Upside down (after a roll) the pitch and yaw controls act the other way round.
        float sense = Vector3.Dot(c.ShipUp, up) >= 0f ? 1f : -1f;

        var input = new ShipInput { Boost = script.Boost, Brake = script.Brake };

        // Height: glide down to the rocks, then weave a ship-length above and below them.
        float target = 0.03f + 0.035f * MathF.Sin(c.Time * 0.9f);
        float wantClimb = MathHelper.Clamp((target - h) * 2.5f, -0.3f, 0.3f);
        float climb = Vector3.Dot(c.ShipForward, up);
        input.Pitch = MathHelper.Clamp((wantClimb - climb) * 4f, -1f, 1f) * sense;

        // Heading: along the ring, drifting toward a lane that follows the scripted turns
        // (the ship's right is the outward side).
        float lane = 0.5f * (c.RingInner + c.RingOuter) + 0.9f * script.Yaw;
        var want = (tangent + radial * MathHelper.Clamp((lane - r) * 1.5f, -0.6f, 0.6f)).Normalized();
        var fwd = c.ShipForward - up * climb;
        if (fwd.LengthSquared > 1e-6f)
        {
            fwd.Normalize();
            // Positive when the wanted heading is counter-clockwise (to the left) seen from above.
            float left = Vector3.Dot(Vector3.Cross(fwd, want), up);
            input.Yaw = MathHelper.Clamp(-left * 3f, -1f, 1f) * sense;
        }

        // Right after the barrel roll: bring the wings level before the next turn.
        if (t >= 10f && t < 10.6f)
        {
            input.Yaw = 0f;
            input.Roll = MathHelper.Clamp(Vector3.Dot(c.ShipRight, up) * 2f, -1f, 1f);
        }
        return input;
    }

    /// <summary>Fly one frame. Runs after the planets were placed for this frame and
    /// instead of the orbit camera's follow logic.</summary>
    private void UpdateFlight(double wallSeconds)
    {
        // The date-seek and search prompts are sandbox overlays that flight mode does
        // not draw: never leave one open (it would swallow the keyboard unseen).
        _seekActive = false;
        _searchActive = false;

        var c = _flightCtx;
        float dt = _paused ? 0f : (float)Math.Min(SimFrameSeconds(wallSeconds), 0.1);
        c.Dt = dt;
        c.Time += dt;
        FillFlightAnchor();

        var input = ReadFlightInput();
        ApplyBoostEnergy(ref input, dt);
        _lastFlightInput = input;
        // During the countdown the ship holds on the start line.
        if (_race.View.Phase != RacePhase.Countdown)
        {
            _shipFlight.Update(dt, input, c);
            BumpOffRocks(dt);
        }
        _shipFlight.WriteTo(c);
        c.BoostEnergy01 = _boostEnergy;
        _race.Update(c);
        ReactToRaceEvents();
        UpdateFlightAudio();
        _flightAudio.Update(dt);
        _chaseCam.Update(c);
        _chaseCam.ApplyTo(_camera);
        if (FlightDemo != null) ApplyDemoView(FlightDemo);

        _rockField.Update(c);
        _ringDust.Update(c);
        _raceGates.Update(c, _race.View);
        _shipFx.Update(c);
    }

    /// <summary>Rocks are solid: push the ship out of any boulder it touches, bounce it off
    /// a little and scrub some speed. (Pebbles and gravel are too small to matter.)</summary>
    private void BumpOffRocks(float dt)
    {
        if (dt <= 0f) return;
        var c = _flightCtx;
        var shipWorld = c.AnchorPosition + _shipFlight.LocalPosition;
        if (!_rockField.Collide(shipWorld, ShipCollisionRadius, c, out var push)) return;
        if (push.LengthSquared < 1e-12f) return;
        _shipFlight.LocalPosition += push;
        var n = push.Normalized();
        float into = Vector3.Dot(_shipFlight.Velocity, n);
        // Sound: a thump, louder the harder the hit, from the side the rock is on.
        if (into < 0f && c.Time - _audioRockHitTime > 0.12f)
        {
            _audioRockHitTime = c.Time;
            float strength = Math.Clamp(-into / ShipFlight.BoostSpeed, 0.08f, 1f);
            _flightAudio.Play(new FlightSoundCue
            {
                Event = FlightSoundEvent.RockHit, Strength = strength, Speed01 = c.Speed01,
                Pan = Vector3.Dot(-n, c.ShipRight),
            });
            _audioHitThisFrame = true;
        }
        if (into < 0f) _shipFlight.Velocity -= n * (into * 1.3f);
        _shipFlight.Velocity *= 0.9f;
    }

    /// <summary>Tell the sound what the flight is doing this frame and fire the one-shot
    /// effects for whatever just happened (boost, countdown, gates, finish).</summary>
    private void UpdateFlightAudio()
    {
        var c = _flightCtx;
        var v = _race.View;
        var s = _flightAudio.State;
        s.Active = 1f;
        s.Paused = _paused ? 1f : 0f;
        s.Throttle01 = c.Throttle01;
        s.Boost01 = c.Boost01;
        s.Speed01 = c.Speed01;
        s.BoostEnergy01 = _boostEnergy;
        s.Turn01 = Math.Clamp(MathF.Max(MathF.Max(MathF.Abs(_lastFlightInput.Yaw), MathF.Abs(_lastFlightInput.Pitch)),
            MathF.Abs(_lastFlightInput.Roll) * 0.5f), 0f, 1f);
        s.TurnPan = Math.Clamp(_lastFlightInput.Yaw + 0.5f * _lastFlightInput.Roll, -1f, 1f);

        // How deep in the ring the ship is: 1 in the plane inside the band, 0 about 0.3 away.
        var rel = c.ShipPosition - c.AnchorPosition;
        float h = Vector3.Dot(rel, c.RingNormal);
        float r = (rel - c.RingNormal * h).Length;
        float inBand = Math.Clamp(MathF.Min(r - c.RingInner, c.RingOuter - r) / 0.3f, 0f, 1f);
        s.RingProximity01 = Math.Clamp(1f - MathF.Abs(h) / 0.3f, 0f, 1f) * inBand;

        s.RacePhase = (int)v.Phase;
        s.Countdown = v.Phase == RacePhase.Countdown ? v.Countdown : 0f;
        s.RaceProgress01 = v.Gates.Count == 0 ? 0f
            : v.Phase == RacePhase.Running ? Math.Clamp(v.NextGate / (float)v.Gates.Count, 0f, 1f)
            : v.Phase == RacePhase.Finished ? 1f : 0f;
        float gatePan = 0f;
        if (v.NextGate >= 0 && v.NextGate < v.Gates.Count)
        {
            var toGate = v.GateWorld(v.NextGate, c) - c.ShipPosition;
            if (toGate.LengthSquared > 1e-10f) gatePan = Math.Clamp(Vector3.Dot(toGate.Normalized(), c.ShipRight), -1f, 1f);
        }
        s.NextGatePan = gatePan;
        // Approach to the next gate while racing: 0 a unit away, 1 at the gate.
        float approach = 0f;
        if (v.Phase == RacePhase.Running && v.NextGate >= 0 && v.NextGate < v.Gates.Count)
            approach = Math.Clamp(1f - (v.GateWorld(v.NextGate, c) - c.ShipPosition).Length, 0f, 1f);
        s.GateApproach01 = approach;
        s.Combo = v.Phase == RacePhase.Running || v.Phase == RacePhase.Finished ? _raceCombo : 0;
        s.Medal = v.Phase == RacePhase.Finished ? v.Medal : 0;
        s.Brake01 = _lastFlightInput.Brake ? 1f : 0f;

        // The nearest big rock: how close and on which side (a wider probe than the
        // collision sphere, so near misses register).
        float rockProximity = 0f, rockPan = 0f;
        if (_rockField.Collide(c.ShipPosition, RockNearRadius, c, out var away) && away.LengthSquared > 1e-12f)
        {
            rockProximity = Math.Clamp(away.Length / (RockNearRadius - ShipCollisionRadius), 0f, 1f);
            rockPan = Math.Clamp(Vector3.Dot(-away.Normalized(), c.ShipRight), -1f, 1f);
        }
        s.RockProximity01 = rockProximity;
        s.RockPan = rockPan;

        if (c.Dt <= 0f) return;   // paused: no new events

        // Acceleration, as a fraction of a hard boost kick (smoothed a little).
        float accelNow = Math.Clamp((c.Speed01 - _audioPrevSpeed01) / c.Dt / 1.5f, -1f, 1f);
        _audioAccel += (accelNow - _audioAccel) * 0.2f;
        _audioPrevSpeed01 = c.Speed01;
        s.Accel = _audioAccel;

        // A near miss: a rock sweeps close by without a hit.
        if (rockProximity > 0.3f && _audioPrevRockProximity <= 0.3f && !_audioHitThisFrame
            && c.Speed01 > 0.12f && c.Time - _audioRockPassTime > 0.2f)
        {
            _audioRockPassTime = c.Time;
            _flightAudio.Play(new FlightSoundCue
            {
                Event = FlightSoundEvent.RockPass, Strength = rockProximity, Speed01 = c.Speed01, Pan = rockPan,
            });
        }
        _audioPrevRockProximity = rockProximity;
        _audioHitThisFrame = false;

        // The air brake going on.
        if (_lastFlightInput.Brake && !_audioPrevBrake)
            _flightAudio.Play(new FlightSoundCue { Event = FlightSoundEvent.BrakeOn, Speed01 = c.Speed01 });
        _audioPrevBrake = _lastFlightInput.Brake;

        // Boost kicking in / cutting out.
        if (c.Boost01 > 0.08f && _audioPrevBoost <= 0.08f && c.Time - _audioBoostIgniteTime > 0.6f)
        {
            _audioBoostIgniteTime = c.Time;
            _flightAudio.Play(FlightSoundEvent.BoostIgnite);
        }
        _audioPrevBoost = c.Boost01;
        if (_boostDry && !_audioPrevDry) _flightAudio.Play(FlightSoundEvent.BoostEmpty);
        _audioPrevDry = _boostDry;

        // Countdown: one blip per second shown (3, 2, 1).
        int count = v.Phase == RacePhase.Countdown ? (int)MathF.Ceiling(v.Countdown) : 0;
        if (count >= 1 && count != _audioPrevCountdown)
            _flightAudio.Play(new FlightSoundCue { Event = FlightSoundEvent.CountdownTick, Count = count });
        _audioPrevCountdown = count;

        // Race events.
        if (v.LastEvent != _audioSeenEvent || v.LastEventClock != _audioSeenEventClock || v.LastEventGate != _audioSeenEventGate)
        {
            _audioSeenEvent = v.LastEvent;
            _audioSeenEventClock = v.LastEventClock;
            _audioSeenEventGate = v.LastEventGate;
            bool hasBest = !float.IsNaN(v.LastSplitDelta);
            var gateCue = new FlightSoundCue
            {
                GateIndex = Math.Max(0, v.LastEventGate), GateCount = v.Gates.Count,
                Split = hasBest ? v.LastSplitDelta : 0f, HasBest = hasBest,
                Speed01 = c.Speed01,
            };
            switch (v.LastEvent)
            {
                case RaceEvent.Go:
                    _raceCombo = 0;
                    _flightAudio.Play(FlightSoundEvent.Go);
                    break;
                case RaceEvent.GatePassed:
                    _raceCombo++;
                    gateCue.Event = FlightSoundEvent.GatePassed;
                    gateCue.Combo = _raceCombo;
                    _flightAudio.Play(gateCue);
                    break;
                case RaceEvent.GateMissed:
                    _raceCombo = 0;
                    gateCue.Event = FlightSoundEvent.GateMissed;
                    gateCue.Pan = gatePan;
                    _flightAudio.Play(gateCue);
                    break;
                case RaceEvent.Finished:
                    // The finish gate sounds like any other when it was flown through.
                    if (v.Status.Length > 0 && v.Status[^1] == GateStatus.Passed)
                    {
                        _raceCombo++;
                        gateCue.Event = FlightSoundEvent.GatePassed;
                        gateCue.GateIndex = v.Gates.Count - 1;
                        gateCue.Combo = _raceCombo;
                        _flightAudio.Play(gateCue);
                    }
                    _flightAudio.Play(new FlightSoundCue
                    {
                        Event = FlightSoundEvent.Finish, Medal = v.Medal, NewRecord = v.NewRecord,
                        GateCount = v.Gates.Count, Combo = _raceCombo,
                    });
                    break;
            }
            s.Combo = _raceCombo;
        }
    }

    /// <summary>The ship as a sphere for rock collisions — a little smaller than the hull,
    /// so near misses stay misses.</summary>
    private const float ShipCollisionRadius = 0.4f * ShipLayout.Length;

    /// <summary>Demo-only inspection cameras (the gameplay camera is "chase").</summary>
    private void ApplyDemoView(FlightDemoJob demo)
    {
        var c = _flightCtx;
        float l = ShipLayout.Length;
        Vector3 eye, look = c.ShipPosition, upDir = c.ShipUp;
        switch (demo.View)
        {
            case "gate":
            {
                var v = _race.View;
                if (v.Gates.Count == 0) return;
                int i = Math.Min(v.NextGate, v.Gates.Count - 1);
                var g = v.Gates[i];
                look = v.GateWorld(i, c);
                eye = look - g.Forward * (g.Radius * 2.4f) + g.Right * (g.Radius * 2.8f) + g.Up * (g.Radius * 1.3f);
                upDir = g.Up;
                break;
            }
            case "orbit":
            {
                float a = (float)(MathHelper.TwoPi * c.Time / Math.Max(0.1, demo.Seconds));
                float el = MathHelper.DegreesToRadians(20f);
                eye = c.ShipPosition
                      + (c.ShipRight * MathF.Sin(a) - c.ShipForward * MathF.Cos(a)) * (3.2f * l * MathF.Cos(el))
                      + c.ShipUp * (3.2f * l * MathF.Sin(el));
                break;
            }
            case "side":
                eye = c.ShipPosition + c.ShipRight * (4.0f * l) + c.ShipUp * (0.6f * l) - c.ShipForward * (1.0f * l);
                look = c.ShipPosition - c.ShipForward * (1.5f * l);
                break;
            case "front":
                eye = c.ShipPosition + c.ShipForward * (4.0f * l) + c.ShipUp * (0.8f * l) + c.ShipRight * (1.2f * l);
                break;
            default:
                return;
        }
        _camera.EyeOverride = eye;
        _camera.ViewOverride = Matrix4.LookAt(eye, look, upDir);
        _camera.Target = look;
        _camera.Distance = MathF.Max((eye - look).Length, 1e-4f);
        _camera.FovDeg = 55f;
    }

    // ---- Drawing --------------------------------------------------------------------------

    /// <summary>Opaque flight geometry: after the planets, before the translucent clouds
    /// and ring (so the ring blends over rocks and ship that are behind it).</summary>
    private void DrawFlightOpaque()
    {
        // Renderer hooks for the ring close-up and the flight post-processing; off (and
        // the picture unchanged) outside flight mode.
        _renderer.RingCloseUp = _flightActive ? 1f : 0f;
        _renderer.SceneTime = _flightCtx.Time;
        if (!_flightActive)
        {
            _renderer.FlightPost = Vector4.Zero;
            return;
        }
        var c = _flightCtx;
        _renderer.FlightPost = new Vector4(1f, c.Speed01, c.Boost01, c.Time);
        // The point on screen the ship is flying toward.
        var heading = c.ShipVelocity.LengthSquared > 1e-8f ? c.ShipVelocity.Normalized() : c.ShipForward;
        var clip = new Vector4(c.ShipPosition + heading * 2f, 1f) * _camera.ViewMatrix * _camera.ProjectionMatrix;
        _renderer.FlightPostCenter = clip.W > 1e-4f
            ? new Vector2(Math.Clamp(clip.X / clip.W * 0.5f + 0.5f, -0.5f, 1.5f), Math.Clamp(clip.Y / clip.W * 0.5f + 0.5f, -0.5f, 1.5f))
            : new Vector2(0.5f, 0.5f);

        _rockField.Draw(_camera, _flightCtx);
        _shipRenderer.Draw(_camera, _flightCtx);
    }

    /// <summary>Additive flight effects, last thing in the HDR scene pass.</summary>
    private void DrawFlightFx()
    {
        if (!_flightActive) return;
        _ringDust.Draw(_camera, _flightCtx);
        _raceGates.Draw(_camera, _flightCtx, _race.View);
        _shipFx.Draw(_camera, _flightCtx);
    }

    /// <summary>The only 2D overlay in flight mode: speed, boost and the controls.</summary>
    private void DrawFlightHud()
    {
        if (FlightDemo is { ShowHud: false }) return;
        _raceHud.Draw(_renderer, _font, _camera, _flightCtx, _race.View, _boostEnergy);
        DrawMouseStick();
        var c = _flightCtx;
        float w = _renderer.FramebufferSize.X, h = _renderer.FramebufferSize.Y;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string speed = (c.Speed * FlightKmPerUnit).ToString("#,0", inv).Replace(',', ' ');
        const int Cells = 16;
        int filled = (int)MathF.Round(MathHelper.Clamp(_boostEnergy, 0f, 1f) * Cells);
        var bar = new System.Text.StringBuilder(Cells);
        for (int i = 0; i < Cells; i++) bar.Append(i < filled ? '█' : '░');

        var bright = new Vector4(0.80f, 0.95f, 1.00f, 0.95f);
        var dim = new Vector4(0.75f, 0.85f, 1.00f, 0.55f);
        _renderer.DrawText(_font, Localization.T("ui.flight.hud.speed", speed), 24f, h - 104f, 22f, bright);
        _renderer.DrawText(_font, Localization.T("ui.flight.hud.boost", bar.ToString()), 24f, h - 76f, 13f,
            _boostDry ? new Vector4(1f, 0.45f, 0.35f, 0.95f)
            : c.Boost01 > 0.05f ? new Vector4(1f, 0.85f, 0.45f, 0.95f) : dim);
        // Two lines: keyboard, then mouse.
        _renderer.DrawText(_font, Localization.T("ui.flight.hud.keys"), 24f, h - 54f, 12f, dim);
        if (_paused)
            _renderer.DrawText(_font, Localization.T("ui.paused"), w * 0.5f - 40f, 28f, 18f, bright);
        if (_seekFeedback.Length > 0)
            _renderer.DrawText(_font, _seekFeedback, w * 0.5f - 160f, 60f, 14f, new Vector4(1f, 0.9f, 0.5f, 0.9f));
    }

    /// <summary>While the mouse is steering, show the joystick: a ring of full deflection
    /// around the press point, and a knob where the cursor is (held to the ring).</summary>
    private void DrawMouseStick()
    {
        if (!_stickActive) return;
        float radius = MouseStick.RadiusPx(_renderer.FramebufferSize.Y);
        var o = _stickOrigin;
        var d = _stickCursor - o;
        float len = d.Length;
        if (len > radius) { d *= radius / len; len = radius; }
        var ring = new Vector4(0.75f, 0.90f, 1.00f, 0.28f);
        var line = new Vector4(0.75f, 0.90f, 1.00f, 0.55f);
        var knob = new Vector4(1.00f, 0.90f, 0.55f, 0.95f);

        const int Dots = 64;
        for (int i = 0; i < Dots; i++)
        {
            float a = i * MathHelper.TwoPi / Dots;
            _renderer.FillRect(o.X + MathF.Cos(a) * radius - 1f, o.Y + MathF.Sin(a) * radius - 1f, 2f, 2f, ring);
        }
        _renderer.FillRect(o.X - 6f, o.Y - 0.5f, 12f, 1f, line);
        _renderer.FillRect(o.X - 0.5f, o.Y - 6f, 1f, 12f, line);
        int steps = (int)(len / 9f);
        for (int k = 1; k < steps; k++)
        {
            var p = o + d * (k / (float)steps);
            _renderer.FillRect(p.X - 1f, p.Y - 1f, 2f, 2f, line);
        }
        _renderer.FillRect(o.X + d.X - 5f, o.Y + d.Y - 5f, 10f, 10f, knob);
    }

    // ---- Demo runner ----------------------------------------------------------------------

    /// <summary>The date every demo run starts from (days since J2000).</summary>
    private const double FlightDemoSimDays = 10000.0;

    private void BeginFlightDemo()
    {
        if (FlightDemo is not { } demo) return;
        Directory.CreateDirectory(demo.OutDir);
        foreach (var name in new[] { "done.txt", "error.txt" })
        {
            string f = Path.Combine(demo.OutDir, name);
            if (File.Exists(f)) File.Delete(f);
        }
        // Nothing the demo produces may depend on what the user's state.json happens to
        // hold. The date matters more than it looks: the track is laid out from the Sun's
        // direction, so the date decides which rocks lie beside it, and with them the
        // autopilot's race time (it moved by 0.12 s after one ordinary run of the app).
        _simDays = FlightDemoSimDays;
        _flightAudio.SoundEnabled = true;
        _flightAudio.MusicEnabled = true;
        _flightAudio.Volume = 0.8f;
        _flightAudio.Solo = demo.Solo;
        EnterFlight();
        if (demo.Race)
        {
            _race.CountdownSeconds = 1f;
            StartRace();
        }
    }

    /// <summary>After each presented frame of the demo: save the due screenshot, and
    /// close the window when the script is over.</summary>
    private void FlightDemoAfterFrame()
    {
        if (FlightDemo is not { } demo) return;
        // The first frames include shader warm-up and texture uploads: time the rest.
        if (demo.FrameIndex == 10) { _demoClock.Restart(); _demoTimedFrames = 0; }
        else if (demo.FrameIndex > 10) _demoTimedFrames++;

        if (demo.WavPath != null && _flightAudio.Started)
        {
            int frames = AudioFormat.SampleRate / FlightDemoJob.Fps;
            _flightAudio.Engine.Render(_demoAudioBlock, frames);
            _demoAudio.AddRange(_demoAudioBlock);
        }

        demo.FrameIndex++;
        if (demo.FrameIndex % demo.ShotEveryFrames == 0 || demo.FrameIndex == demo.TotalFrames)
        {
            double elapsed = _demoClock.Elapsed.TotalMilliseconds;
            SaveScreenshotTo(Path.Combine(demo.OutDir, $"flight_{demo.ShotIndex:D3}.png"));
            demo.ShotIndex++;
            // Keep the PNG encode out of the frame-time figure.
            _demoClock.Restart();
            _demoAccumulatedMs += elapsed;
        }
        if (demo.FrameIndex < demo.TotalFrames) return;

        double total = _demoAccumulatedMs + _demoClock.Elapsed.TotalMilliseconds;
        double avg = _demoTimedFrames > 0 ? total / _demoTimedFrames : 0.0;
        string summary = FormattableString.Invariant(
            $"frames {demo.TotalFrames}, screenshots {demo.ShotIndex}, average frame {avg:0.00} ms ({(avg > 0 ? 1000.0 / avg : 0):0} fps), view {demo.View}");
        var rv = _race.View;
        int passed = rv.Status.Count(s => s == GateStatus.Passed), missed = rv.Status.Count(s => s == GateStatus.Missed);
        summary += FormattableString.Invariant(
            $"{Environment.NewLine}race: phase {rv.Phase}, gates passed {passed} missed {missed} of {rv.Gates.Count}, time {rv.TotalTime:0.00} s (penalty {rv.Penalty:0.0}), finish {rv.FinishTime:0.00}, medal {rv.Medal}");
        if (demo.WavPath != null)
        {
            var samples = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_demoAudio);
            string? dir = Path.GetDirectoryName(Path.GetFullPath(demo.WavPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            WavFile.Write(demo.WavPath, samples);
            double peak = 0.0, sumSq = 0.0;
            foreach (float x in samples) { peak = Math.Max(peak, Math.Abs(x)); sumSq += (double)x * x; }
            double rms = samples.Length > 0 ? Math.Sqrt(sumSq / samples.Length) : 0.0;
            static double Db(double x) => x > 1e-9 ? 20.0 * Math.Log10(x) : -180.0;
            summary += FormattableString.Invariant(
                $"{Environment.NewLine}audio: {samples.Length / AudioFormat.Channels / (double)AudioFormat.SampleRate:0.00} s, peak {Db(peak):0.0} dBFS, rms {Db(rms):0.0} dBFS, sources at end {_flightAudio.Engine.SourceCount}");
        }
        File.WriteAllText(Path.Combine(demo.OutDir, "done.txt"), summary + Environment.NewLine);
        Console.WriteLine("[flight-demo] " + summary);
        Close();
    }

    private double _demoAccumulatedMs;
}
