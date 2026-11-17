using OpenTK.Mathematics;

namespace SolarSystem;

// PROTOTYPE (2026-11-17): arcade flight mode — gate race: the shared contract between
// the race logic (RaceSession), the gate visuals (RaceGates), the race HUD (RaceHud),
// the rock field and the window glue. See FlightContext.cs for the flight conventions
// (units, axes, matrices, HDR, log depth) — they all apply here too.
//
// The race in one paragraph: a track is a list of gates around Saturn's ring. The player
// presses Enter, the ship is put on the start line, a countdown runs, then the clock
// starts. Gates must be flown through in order; the next one is highlighted. Flying
// past a gate without going through it costs a time penalty and the race moves on to
// the following gate. After the last gate the time is compared with the best one and
// with the medal times. Enter starts again at any moment.
//
// Everything about a gate's place is "anchor-relative": relative to the centre of the
// anchor planet, in world axes — the same frame as ShipFlight.LocalPosition. Add
// FlightContext.AnchorPosition to get world coordinates (the planet moves every frame).

public enum RacePhase
{
    /// <summary>No race running: free flight; the track is visible, waiting for Enter (a
    /// standing start with a countdown) or for the ship to fly through the first gate (a
    /// flying start).</summary>
    Idle,
    /// <summary>On the start line, controls locked, <see cref="RaceView.Countdown"/> ticking down.</summary>
    Countdown,
    Running,
    /// <summary>The last gate is behind; the result is on screen, the ship flies on freely.</summary>
    Finished,
}

public enum GateStatus { Upcoming, Next, Passed, Missed }

public enum RaceEvent { None, Go, GatePassed, GateMissed, Finished }

/// <summary>The ring the track is laid out on, as seen from the start line. The three
/// axes are unit length and mutually perpendicular, with <c>Tangent = Up × Radial</c>;
/// they do not change during a flight.</summary>
public readonly struct RingFrame
{
    /// <summary>From the planet's centre to the start line, in the ring plane.</summary>
    public Vector3 Radial { get; init; }
    /// <summary>Direction of travel along the ring at the start line.</summary>
    public Vector3 Tangent { get; init; }
    /// <summary>Ring normal on the sunlit side.</summary>
    public Vector3 Up { get; init; }
    /// <summary>Planet radius and the ring band, world units from the planet's centre
    /// (Saturn: 3.4, 4.59 .. 8.16).</summary>
    public float AnchorRadius { get; init; }
    public float RingInner { get; init; }
    public float RingOuter { get; init; }

    /// <summary>Anchor-relative point at distance <paramref name="radius"/> from the
    /// planet's centre, <paramref name="angleRad"/> around the ring from the start line
    /// (positive = in the direction of travel) and <paramref name="height"/> above the
    /// ring plane.</summary>
    public Vector3 Point(float radius, float angleRad, float height)
        => (Radial * MathF.Cos(angleRad) + Tangent * MathF.Sin(angleRad)) * radius + Up * height;
}

/// <summary>One gate: a round opening the ship has to fly through.</summary>
public readonly struct RaceGate
{
    /// <summary>Centre of the opening (anchor-relative).</summary>
    public Vector3 LocalPosition { get; init; }
    /// <summary>Unit direction the ship is meant to travel through the gate.</summary>
    public Vector3 Forward { get; init; }
    /// <summary>Unit vector perpendicular to <see cref="Forward"/>: the top of the gate.</summary>
    public Vector3 Up { get; init; }
    /// <summary>Radius of the opening (world units; the ship is 0.05 long).</summary>
    public float Radius { get; init; }
    public bool IsFinish { get; init; }
    public Vector3 Right => Vector3.Cross(Forward, Up);
}

/// <summary>
/// The state of the race for one frame. <see cref="RaceSession"/> owns and updates it;
/// the gate visuals and the HUD only read it.
/// </summary>
public sealed class RaceView
{
    /// <summary>Localisation key of the track's name.</summary>
    public string TrackNameKey = "";
    public RacePhase Phase;
    public IReadOnlyList<RaceGate> Gates = [];
    /// <summary>One entry per gate.</summary>
    public GateStatus[] Status = [];
    /// <summary>Per gate: <see cref="TotalTime"/> at the moment it was passed or missed;
    /// NaN while it is still ahead.</summary>
    public float[] GateTime = [];
    /// <summary>Index of the gate to fly through next; <c>Gates.Count</c> once finished.</summary>
    public int NextGate;

    /// <summary>Goes up by one every time a run begins (Enter, or a flying start), so
    /// whoever keeps per-run state knows when to reset it.</summary>
    public int RunNumber;
    /// <summary>True when the current run began by flying through the first gate instead
    /// of from the start line.</summary>
    public bool FlyingStart;
    /// <summary><see cref="Clock"/> time of the current run's start signal (the end of the
    /// countdown, or the flight through the first gate); negative infinity before any.</summary>
    public float StartClock = float.NegativeInfinity;
    /// <summary><see cref="Clock"/> time a flying-start run was last called off because the
    /// pilot flew on past the gates (back to free flight); negative infinity before any.</summary>
    public float CalledOffClock = float.NegativeInfinity;

    /// <summary>Seconds, always running with <see cref="FlightContext.Dt"/> since the track
    /// was built (also when no race is on). Drive animations with it.</summary>
    public float Clock;
    /// <summary>Seconds left until the start (Countdown phase), otherwise 0.</summary>
    public float Countdown;
    /// <summary>Flying time since the start signal.</summary>
    public float Elapsed;
    /// <summary>Penalty seconds collected for missed gates.</summary>
    public float Penalty;
    public float PenaltyPerMiss = 2f;
    /// <summary>The time that counts: flying time plus penalties.</summary>
    public float TotalTime => Elapsed + Penalty;

    /// <summary>What happened most recently, when (<see cref="Clock"/> time) and at
    /// which gate — for flashes, bursts and banners.</summary>
    public RaceEvent LastEvent;
    public float LastEventClock = float.NegativeInfinity;
    public int LastEventGate = -1;

    /// <summary>Best finished <see cref="TotalTime"/> on this track; NaN when there is none.</summary>
    public float BestTime = float.NaN;
    /// <summary>The best run's <see cref="GateTime"/> values; empty when there is none.</summary>
    public float[] BestSplits = [];
    /// <summary>At the last gate: this run's time minus the best run's time there
    /// (negative = ahead); NaN when there is no best run.</summary>
    public float LastSplitDelta = float.NaN;

    /// <summary>Result of the last finished run; NaN / 0 before.</summary>
    public float FinishTime = float.NaN;
    /// <summary>0 none, 1 bronze, 2 silver, 3 gold.</summary>
    public int Medal;
    public bool NewRecord;
    /// <summary>Times to beat for each medal.</summary>
    public float GoldTime, SilverTime, BronzeTime;

    /// <summary>Where the ship is put for the countdown (anchor-relative position, unit axes).</summary>
    public Vector3 StartPosition, StartForward = -Vector3.UnitZ, StartUp = Vector3.UnitY;

    /// <summary>Spheres (xyz = anchor-relative centre, w = radius) the rock field keeps
    /// free of rocks: around every gate and along the stretches where the racing line
    /// goes through the ring plane.</summary>
    public IReadOnlyList<Vector4> ClearZones = [];

    /// <summary>World position of gate <paramref name="index"/>'s centre this frame.</summary>
    public Vector3 GateWorld(int index, FlightContext ctx) => ctx.AnchorPosition + Gates[index].LocalPosition;
}
