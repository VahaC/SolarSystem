// PROTOTYPE (2026-11-17): arcade flight mode — gate race: the track ("Ring Run") and the race rules.
using System.Globalization;
using OpenTK.Mathematics;

namespace SolarSystem;

// The track is a Catmull-Rom racing line through hand-placed control points around the
// ring (lane changes, height waves, two dives through the ring plane, a stretch along
// the rock field's sparse lane, a final straight). Gates sit at equal distances along
// that line. The rules work on the ship's swept path each frame (previous position →
// current position), so a fast ship cannot slip through a gate between two frames.

/// <summary>Builds the track and runs the race: countdown, gate passes, clock, result.</summary>
public sealed class RaceSession
{
    /// <summary>The state everything else reads.</summary>
    public RaceView View { get; } = new();

    /// <summary>Length of the countdown started by <see cref="Start"/> (seconds).</summary>
    public float CountdownSeconds { get; set; } = 3f;

    /// <summary>File the best times are kept in; null or empty = keep nothing on disk.</summary>
    public string? BestTimesPath { get; set; }

    /// <summary>A gate counts as passed when the ship's path crosses its plane within
    /// this multiple of its radius. Generous on purpose: the position is the ship's
    /// centre, and a wing tip over the glowing rim still looks like "through" to the pilot.</summary>
    public const float PassTolerance = 1.25f;

    /// <summary>A forward crossing of the next gate's plane further than this from its
    /// centre is ignored (the ship is somewhere else entirely, not flying past the gate).</summary>
    public const float MissRange = 4f;

    /// <summary>A run begun by a flying start is called off (back to free flight, nothing
    /// recorded) when this many gates in a row are missed: flying through the first gate
    /// may have been a coincidence, and a pilot who is not racing should not be showered
    /// with penalties. A run started from the line with Enter always goes to the finish.</summary>
    public const int FlyingStartMissLimit = 3;

    /// <summary>A jump longer than this in one frame is a teleport (restart, reset), not flight.</summary>
    public const float TeleportDistance = 1f;

    /// <summary>Distance from the start position to the first gate along the line.</summary>
    public const float FirstGateDistance = 0.5f;

    /// <summary>Length of the racing line from the start position to the finish gate.</summary>
    public float TrackLength { get; private set; }

    /// <summary>Per gate: its distance from the start along the racing line.</summary>
    public IReadOnlyList<float> GateDistance => _gateS;

    private float[] _gateS = [];
    private Vector3[] _linePts = [];
    private float[] _lineS = [];
    private Vector3 _lastShipLocal;
    private bool _hasLast;
    private int _missStreak;   // gates missed in a row in the current run

    // ---- Track ---------------------------------------------------------------------------

    /// <summary>Lay the track out on this ring and load the best times. Leaves the race
    /// in <see cref="RacePhase.Idle"/>.</summary>
    public void Build(RingFrame frame)
    {
        var v = View;
        v.TrackNameKey = "ui.race.track.ringrun";

        float width = frame.RingOuter - frame.RingInner;
        float mid = frame.RingInner + 0.5f * width;
        float sparse = frame.RingInner + 0.67f * width;   // the rock field's sparse lane
        const float S = 0.85f;                           // distance scale of the layout

        // (distance along the ring at mid-band, radius, height above the ring plane)
        (float s, float r, float h)[] layout =
        [
            (0.0f,  mid,         0.10f),   // start line
            (0.6f,  mid,         0.10f),
            (1.3f,  mid + 0.12f, 0.15f),
            (2.0f,  mid + 0.38f, 0.20f),   // lane change outwards, climbing
            (2.7f,  mid + 0.40f, 0.14f),
            (3.4f,  mid + 0.15f, 0.07f),
            (4.1f,  mid - 0.25f, 0.10f),   // lane change inwards
            (4.8f,  mid - 0.40f, 0.17f),
            (5.5f,  mid - 0.30f, 0.10f),
            (6.2f,  mid - 0.15f, 0.00f),   // dive 1 through the ring plane
            (6.9f,  mid,        -0.12f),
            (7.6f,  mid + 0.15f,-0.12f),   // the far side
            (8.3f,  mid + 0.30f, 0.00f),   // back up
            (9.0f,  0.5f * (mid + sparse) + 0.1f, 0.08f),
            (9.7f,  sparse,      0.06f),   // the sparse lane
            (10.5f, sparse,      0.06f),
            (11.3f, sparse,      0.07f),
            (12.0f, mid + 0.45f, 0.05f),
            (12.7f, mid + 0.15f,-0.10f),   // dive 2
            (13.4f, mid - 0.10f,-0.13f),
            (14.1f, mid - 0.25f, 0.00f),   // back up
            (14.8f, mid - 0.20f, 0.13f),
            (15.5f, mid - 0.05f, 0.16f),
            (16.3f, mid,         0.14f),   // the final straight
            (17.3f, mid,         0.13f),
            (18.3f, mid,         0.13f),   // finish
        ];

        var ctrl = new Vector3[layout.Length + 2];
        for (int i = 0; i < layout.Length; i++)
            ctrl[i + 1] = frame.Point(layout[i].r, layout[i].s * S / mid, layout[i].h);
        // Phantom end points: leave the start exactly along the ring's tangent, carry on
        // straight through the finish.
        ctrl[0] = ctrl[1] - frame.Tangent * (ctrl[2] - ctrl[1]).Length;
        ctrl[^1] = ctrl[^2] + (ctrl[^2] - ctrl[^3]);
        BuildLine(ctrl);

        float lineEnd = _lineS[^1];
        TrackLength = lineEnd;
        int count = Math.Clamp((int)MathF.Round((lineEnd - FirstGateDistance) / 0.66f) + 1, 18, 26);
        float step = (lineEnd - FirstGateDistance) / (count - 1);

        var gates = new List<RaceGate>(count);
        _gateS = new float[count];
        for (int i = 0; i < count; i++)
        {
            float s = FirstGateDistance + i * step;
            _gateS[i] = s;
            var pos = LinePoint(s);
            var fwd = LineDirection(s);
            var up = (frame.Up - fwd * Vector3.Dot(fwd, frame.Up)).Normalized();
            float height = Vector3.Dot(pos, frame.Up);
            float inPlane = (pos - frame.Up * height).Length;
            bool finish = i == count - 1;
            float radius = finish ? 0.16f
                : i == 0 ? 0.14f                                   // a generous warm-up gate
                : MathF.Abs(inPlane - sparse) < 0.05f ? 0.11f      // tighter in the sparse lane
                : MathF.Abs(height) < 0.05f ? 0.13f                // through the ring plane
                : 0.12f;
            gates.Add(new RaceGate { LocalPosition = pos, Forward = fwd, Up = up, Radius = radius, IsFinish = finish });
        }
        v.Gates = gates;
        v.Status = new GateStatus[count];
        v.GateTime = new float[count];

        // Medal times from the line's length: bronze is a clean run at cruise speed plus
        // 10 %; silver flies a quarter of the distance at boost speed, gold half of it.
        float cruise = TrackLength / ShipFlight.CruiseSpeed;
        float Mixed(float boostShare) => TrackLength * (boostShare / ShipFlight.BoostSpeed + (1f - boostShare) / ShipFlight.CruiseSpeed);
        v.BronzeTime = MathF.Ceiling(cruise * 1.10f * 2f) / 2f;
        v.SilverTime = MathF.Ceiling(Mixed(0.25f) * 2f) / 2f;
        v.GoldTime = MathF.Ceiling(Mixed(0.5f) * 2f) / 2f;

        v.StartPosition = ctrl[1];
        v.StartForward = frame.Tangent;
        v.StartUp = frame.Up;

        // Rock-free zones: a tube along the whole line, a bubble at every gate, the start.
        var zones = new List<Vector4>();
        int tube = (int)MathF.Ceiling(lineEnd / 0.095f);
        for (int j = 0; j <= tube; j++) zones.Add(new Vector4(LinePoint(lineEnd * j / tube), 0.09f));
        foreach (var g in gates) zones.Add(new Vector4(g.LocalPosition, g.Radius * 1.8f + 0.03f));
        zones.Add(new Vector4(v.StartPosition, 0.25f));
        v.ClearZones = zones;

        LoadBest();
        ResetRun(RacePhase.Idle);
    }

    private void BuildLine(Vector3[] c)
    {
        const int Sub = 48;
        var pts = new List<Vector3>();
        var cum = new List<float>();
        for (int seg = 1; seg < c.Length - 2; seg++)
        {
            Vector3 p0 = c[seg - 1], p1 = c[seg], p2 = c[seg + 1], p3 = c[seg + 2];
            for (int k = seg == 1 ? 0 : 1; k <= Sub; k++)
            {
                float t = k / (float)Sub, t2 = t * t, t3 = t2 * t;
                var p = 0.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                                + (3f * p1 - p0 - 3f * p2 + p3) * t3);
                cum.Add(pts.Count == 0 ? 0f : cum[^1] + (p - pts[^1]).Length);
                pts.Add(p);
            }
        }
        _linePts = [.. pts];
        _lineS = [.. cum];
    }

    /// <summary>Anchor-relative point on the racing line <paramref name="s"/> units from
    /// the start; continues straight beyond both ends.</summary>
    public Vector3 LinePoint(float s)
    {
        if (_linePts.Length < 2) return View.StartPosition;
        if (s <= 0f) return _linePts[0] + (_linePts[1] - _linePts[0]).Normalized() * s;
        float end = _lineS[^1];
        if (s >= end) return _linePts[^1] + (_linePts[^1] - _linePts[^2]).Normalized() * (s - end);
        int i = Array.BinarySearch(_lineS, s);
        if (i >= 0) return _linePts[i];
        i = ~i;   // first index with _lineS[i] > s
        float f = (s - _lineS[i - 1]) / MathF.Max(1e-6f, _lineS[i] - _lineS[i - 1]);
        return Vector3.Lerp(_linePts[i - 1], _linePts[i], f);
    }

    /// <summary>Unit direction of the racing line at <paramref name="s"/>.</summary>
    public Vector3 LineDirection(float s)
        => (LinePoint(s + 0.01f) - LinePoint(s - 0.01f)).Normalized();

    // ---- Race control --------------------------------------------------------------------

    /// <summary>Begin a race (again) from the start line: the countdown starts now.</summary>
    public void Start()
    {
        ResetRun(RacePhase.Countdown);
        View.Countdown = CountdownSeconds;
        View.RunNumber++;
        View.FlyingStart = false;
    }

    /// <summary>Drop the current race and go back to free flight.</summary>
    public void Abort() => ResetRun(RacePhase.Idle);

    private void ResetRun(RacePhase phase)
    {
        var v = View;
        v.Phase = phase;
        v.NextGate = 0;
        v.Countdown = 0f;
        v.Elapsed = 0f;
        v.Penalty = 0f;
        v.LastSplitDelta = float.NaN;
        v.FinishTime = float.NaN;
        v.Medal = 0;
        v.NewRecord = false;
        v.LastEvent = RaceEvent.None;
        v.LastEventClock = float.NegativeInfinity;
        v.LastEventGate = -1;
        for (int i = 0; i < v.Status.Length; i++)
        {
            v.Status[i] = i == 0 ? GateStatus.Next : GateStatus.Upcoming;
            v.GateTime[i] = float.NaN;
        }
        _missStreak = 0;
        _hasLast = false;   // the ship is about to be put on the start line
    }

    /// <summary>Advance the race by <see cref="FlightContext.Dt"/>, using the ship's
    /// position in <paramref name="ctx"/> (already updated for this frame). Does nothing
    /// while paused (<c>Dt == 0</c>).</summary>
    public void Update(FlightContext ctx)
    {
        float dt = ctx.Dt;
        if (!(dt > 0f)) return;
        var v = View;
        v.Clock += dt;
        var ship = ctx.ShipPosition - ctx.AnchorPosition;

        if (v.Phase == RacePhase.Countdown)
        {
            v.Countdown -= dt;
            if (v.Countdown <= 1e-4f)
            {
                v.Elapsed = MathF.Max(0f, -v.Countdown);   // the part of the frame after the signal
                v.Countdown = 0f;
                v.Phase = RacePhase.Running;
                v.StartClock = v.Clock;
                Raise(RaceEvent.Go, 0);
            }
        }
        else if (v.Phase == RacePhase.Running)
        {
            v.Elapsed += dt;
            if (_hasLast && (ship - _lastShipLocal).Length <= TeleportDistance)
                Resolve(_lastShipLocal, ship);
        }
        else if (_hasLast && v.Gates.Count > 0 && (ship - _lastShipLocal).Length <= TeleportDistance
                 && Cross(v.Gates[0], _lastShipLocal, ship, 0f, out _, out float lateral)
                 && lateral <= v.Gates[0].Radius * PassTolerance)
        {
            // Flying start: in free flight (or after a finished run) flying through the
            // first gate starts a race on the spot — the clock runs from that gate.
            var from = _lastShipLocal;
            ResetRun(RacePhase.Running);
            v.RunNumber++;
            v.FlyingStart = true;
            v.StartClock = v.Clock;
            Resolve(from, ship);   // passes the first gate (and anything else on this frame's path)
        }
        _lastShipLocal = ship;
        _hasLast = true;
    }

    /// <summary>Resolve every gate the segment <paramref name="a"/> → <paramref name="b"/>
    /// passes or misses, in order along the segment.</summary>
    private void Resolve(Vector3 a, Vector3 b)
    {
        var v = View;
        int count = v.Gates.Count;
        float tFrom = 0f;
        for (int guard = 0; guard <= count && v.Phase == RacePhase.Running && v.NextGate < count; guard++)
        {
            int n = v.NextGate;
            if (Cross(v.Gates[n], a, b, tFrom, out float t, out float lateral) && lateral <= MissRange)
            {
                if (lateral <= v.Gates[n].Radius * PassTolerance) Pass(n); else Miss(n);
                tFrom = t;
                continue;
            }

            // The ship flew through a later gate first: everything before it was missed.
            int hit = -1;
            float tHit = float.MaxValue;
            for (int k = n + 1; k < count; k++)
                if (Cross(v.Gates[k], a, b, tFrom, out float tk, out float lk)
                    && lk <= v.Gates[k].Radius * PassTolerance && tk < tHit)
                { hit = k; tHit = tk; }
            if (hit < 0) break;
            for (int k = n; k < hit; k++) Miss(k);
            Pass(hit);
            tFrom = tHit;
        }

        // Judged after the whole step, so gates skipped on the way to one that was flown
        // through (misses, then a pass) do not count as flying away.
        if (v.Phase == RacePhase.Running && v.FlyingStart && _missStreak >= FlyingStartMissLimit)
        {
            ResetRun(RacePhase.Idle);
            v.CalledOffClock = v.Clock;
        }
    }

    /// <summary>Does the segment cross the gate's plane in its forward direction (at or
    /// after segment parameter <paramref name="tFrom"/>)? Gives where, and how far from
    /// the gate's centre.</summary>
    private static bool Cross(in RaceGate g, Vector3 a, Vector3 b, float tFrom, out float t, out float lateral)
    {
        float da = Vector3.Dot(a - g.LocalPosition, g.Forward);
        float db = Vector3.Dot(b - g.LocalPosition, g.Forward);
        t = 0f; lateral = float.MaxValue;
        if (!(da < 0f && db >= 0f)) return false;
        t = da / (da - db);
        if (t < tFrom) return false;
        var p = a + (b - a) * t;
        var off = p - g.LocalPosition;
        lateral = (off - g.Forward * Vector3.Dot(off, g.Forward)).Length;
        return true;
    }

    private void Pass(int n)
    {
        var v = View;
        _missStreak = 0;
        v.Status[n] = GateStatus.Passed;
        v.GateTime[n] = v.TotalTime;
        UpdateSplit(n);
        Raise(RaceEvent.GatePassed, n);
        Advance(n);
    }

    private void Miss(int n)
    {
        var v = View;
        _missStreak++;
        v.Penalty += v.PenaltyPerMiss;
        v.Status[n] = GateStatus.Missed;
        v.GateTime[n] = v.TotalTime;
        UpdateSplit(n);
        Raise(RaceEvent.GateMissed, n);
        Advance(n);
    }

    private void UpdateSplit(int n)
    {
        var v = View;
        v.LastSplitDelta = v.BestSplits.Length == v.Gates.Count && float.IsFinite(v.BestSplits[n])
            ? v.GateTime[n] - v.BestSplits[n]
            : float.NaN;
    }

    private void Advance(int n)
    {
        var v = View;
        v.NextGate = n + 1;
        if (v.NextGate < v.Gates.Count) { v.Status[v.NextGate] = GateStatus.Next; return; }

        v.Phase = RacePhase.Finished;
        v.FinishTime = v.TotalTime;
        v.Medal = v.FinishTime <= v.GoldTime ? 3 : v.FinishTime <= v.SilverTime ? 2 : v.FinishTime <= v.BronzeTime ? 1 : 0;
        v.NewRecord = float.IsNaN(v.BestTime) || v.FinishTime < v.BestTime;
        if (v.NewRecord)
        {
            v.BestTime = v.FinishTime;
            v.BestSplits = (float[])v.GateTime.Clone();
            SaveBest();
        }
        Raise(RaceEvent.Finished, n);
    }

    private void Raise(RaceEvent ev, int gate)
    {
        View.LastEvent = ev;
        View.LastEventClock = View.Clock;
        View.LastEventGate = gate;
    }

    // ---- Best times on disk ----------------------------------------------------------------
    // One line per track: key <TAB> layout signature <TAB> best time <TAB> splits (comma
    // separated), invariant culture. The signature (gate count and layout version) throws
    // away a record set on a different layout of the track. Bump LayoutVersion whenever
    // the control points change.

    private const int LayoutVersion = 1;

    private string Signature
        => string.Create(CultureInfo.InvariantCulture, $"{View.Gates.Count}:v{LayoutVersion}");

    private void LoadBest()
    {
        var v = View;
        v.BestTime = float.NaN;
        v.BestSplits = [];
        if (string.IsNullOrEmpty(BestTimesPath)) return;
        try
        {
            if (!File.Exists(BestTimesPath)) return;
            foreach (var line in File.ReadAllLines(BestTimesPath))
            {
                var parts = line.Split('\t');
                if (parts.Length != 4 || parts[0] != v.TrackNameKey || parts[1] != Signature) continue;
                if (!TryParse(parts[2], out float best)) continue;
                var raw = parts[3].Split(',');
                if (raw.Length != v.Gates.Count) continue;
                var splits = new float[raw.Length];
                bool ok = true;
                for (int i = 0; i < raw.Length && ok; i++) ok = TryParse(raw[i], out splits[i]);
                if (!ok) continue;
                v.BestTime = best;
                v.BestSplits = splits;
            }
        }
        catch (Exception)
        {
            // An unreadable file means no record, never a broken race.
            v.BestTime = float.NaN;
            v.BestSplits = [];
        }
    }

    private static bool TryParse(string s, out float value)
        => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
           && float.IsFinite(value) && value >= 0f;

    private void SaveBest()
    {
        var v = View;
        if (string.IsNullOrEmpty(BestTimesPath)) return;
        try
        {
            var lines = new List<string>();
            if (File.Exists(BestTimesPath))
            {
                try
                {
                    foreach (var line in File.ReadAllLines(BestTimesPath))
                        if (line.Length > 0 && !line.StartsWith(v.TrackNameKey + "\t", StringComparison.Ordinal))
                            lines.Add(line);
                }
                catch (Exception) { lines.Clear(); }
            }
            var inv = CultureInfo.InvariantCulture;
            var splits = string.Join(",", v.BestSplits.Select(x => x.ToString("R", inv)));
            lines.Add($"{v.TrackNameKey}\t{Signature}\t{v.BestTime.ToString("R", inv)}\t{splits}");
            var dir = Path.GetDirectoryName(Path.GetFullPath(BestTimesPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = BestTimesPath + ".tmp";
            File.WriteAllLines(tmp, lines);
            File.Move(tmp, BestTimesPath, overwrite: true);
        }
        catch (Exception)
        {
            // Keeping the record in memory is enough when the disk says no.
        }
    }
}
