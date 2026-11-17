using System.Globalization;
using OpenTK.Mathematics;

namespace SolarSystem;

// PROTOTYPE (2026-11-17): arcade flight mode — hidden, scripted demo run used to look at
// the flight scene without opening a window (development tool).

/// <summary>
/// <c>--flight-demo</c>: start hidden, enter flight mode near Saturn's rings, fly a fixed
/// autopilot script at a fixed 60 fps time step, save a PNG every few frames, then exit.
/// The user's saved state is read but never written.
/// </summary>
public sealed class FlightDemoJob
{
    public const int Fps = 60;

    public double Seconds { get; init; } = 12.0;
    public double ShotEverySeconds { get; init; } = 1.0;
    /// <summary><c>chase</c> (the gameplay camera), <c>orbit</c> (circles the ship once),
    /// <c>side</c>, <c>front</c>, or <c>gate</c> (parked beside the next race gate).</summary>
    public string View { get; init; } = "chase";
    public string OutDir { get; init; } = "flight-demo";
    public Vector2i Size { get; init; } = new(1600, 900);
    /// <summary><c>--hud</c>: draw the flight HUD in the screenshots (off by default so
    /// they show the scene only).</summary>
    public bool ShowHud { get; init; }
    /// <summary><c>--stick x,y</c>: instead of the scripted pilot, fly as if the left mouse
    /// button were held with the cursor dragged this far from the screen centre, in stick
    /// radii (+x = right, +y = up). Exercises the mouse-steering path.</summary>
    public Vector2? Stick { get; init; }
    /// <summary><c>--race</c>: start the gate race at once (short countdown) and let a
    /// simple pilot steer at the next gate.</summary>
    public bool Race { get; init; }
    /// <summary><c>--flying-start</c>: stay in free flight and let the race pilot fly
    /// through the first gate — that alone has to start the race.</summary>
    public bool FlyingStart { get; init; }
    /// <summary><c>--wav file</c>: also render the flight's sound (offline, no audio
    /// device, exactly 800 samples per frame) and write it to this WAV file.</summary>
    public string? WavPath { get; init; }
    /// <summary><c>--solo engine|music|sfx</c>: render only that sound layer (for checking
    /// the balance of the mix).</summary>
    public string? Solo { get; init; }

    public int TotalFrames => Math.Max(1, (int)Math.Round(Seconds * Fps));
    public int ShotEveryFrames => Math.Max(1, (int)Math.Round(ShotEverySeconds * Fps));

    // Progress, advanced by the window.
    public int FrameIndex { get; set; }
    public int ShotIndex { get; set; }

    /// <summary>Parse the command line; null when <c>--flight-demo</c> is absent.</summary>
    public static FlightDemoJob? FromCli(string[] args)
    {
        if (args == null || Array.IndexOf(args, "--flight-demo") < 0) return null;
        double seconds = 12.0, every = 1.0;
        string view = "chase", outDir = "flight-demo";
        var size = new Vector2i(1600, 900);
        bool hud = false, race = false, flying = false;
        string? wav = null, solo = null;
        Vector2? stick = null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {a}");
            switch (a)
            {
                case "--flight-demo": break;
                case "--seconds": seconds = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--shot-every": every = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--view": view = Next().ToLowerInvariant(); break;
                case "--out": outDir = Next(); break;
                case "--size":
                {
                    var parts = Next().ToLowerInvariant().Split('x');
                    if (parts.Length != 2) throw new ArgumentException("--size expects WIDTHxHEIGHT");
                    size = new Vector2i(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
                    break;
                }
                case "--hud": hud = true; break;
                case "--race": race = true; break;
                case "--flying-start": flying = true; break;
                case "--wav": wav = Next(); break;
                case "--solo": solo = Next().ToLowerInvariant(); break;
                case "--stick":
                {
                    var parts = Next().Split(',');
                    if (parts.Length != 2) throw new ArgumentException("--stick expects X,Y");
                    stick = new Vector2(float.Parse(parts[0], CultureInfo.InvariantCulture), float.Parse(parts[1], CultureInfo.InvariantCulture));
                    break;
                }
                default: throw new ArgumentException($"Unknown flight-demo option: {a}");
            }
        }
        if (view is not ("chase" or "orbit" or "side" or "front" or "gate"))
            throw new ArgumentException("--view must be chase, orbit, side, front or gate");
        if (solo is not (null or "engine" or "music" or "sfx"))
            throw new ArgumentException("--solo must be engine, music or sfx");
        return new FlightDemoJob
        {
            Seconds = Math.Clamp(seconds, 0.1, 600.0),
            ShotEverySeconds = Math.Clamp(every, 1.0 / Fps, 600.0),
            View = view,
            OutDir = outDir,
            Size = size,
            ShowHud = hud,
            Stick = stick,
            Race = race,
            FlyingStart = flying && !race,
            WavPath = wav,
            Solo = solo,
        };
    }

    /// <summary>The scripted pilot: what the controls do at <paramref name="t"/> seconds.
    /// A 12 s loop — straight (dipping through the ring plane), right turn, boost, boost
    /// with a climbing left turn, barrel roll, braking right turn.</summary>
    public static ShipInput Autopilot(double t)
    {
        t %= 12.0;
        var i = new ShipInput();
        if (t < 2.0) { i.Pitch = -0.18f; }
        else if (t < 4.5) { i.Yaw = 0.6f; }
        else if (t < 5.5) { i.Boost = true; }
        else if (t < 8.0) { i.Boost = true; i.Yaw = -0.7f; i.Pitch = 0.3f; }
        else if (t < 10.0) { i.Roll = 1.0f; }
        else { i.Yaw = 0.6f; i.Brake = true; }
        return i;
    }
}
