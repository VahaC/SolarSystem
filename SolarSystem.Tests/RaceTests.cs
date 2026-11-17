// PROTOTYPE (2026-11-17): arcade flight mode — gate race: tests for the track and the race rules.
using OpenTK.Mathematics;
using Xunit;

namespace SolarSystem.Tests;

/// <summary>
/// Pins the "Ring Run" track's geometry (flyable, inside the ring, rock-free) and the race
/// rules of <see cref="RaceSession"/>: swept-path passes, misses, penalties, countdown,
/// pause, finish, splits, best-time persistence and restart. No OpenGL needed.
/// </summary>
public class RaceTests
{
    private const float Fps = 60f;
    private static readonly Vector3 Anchor = new(100f, -20f, 50f);

    private static RingFrame Saturn() => new()
    {
        Radial = Vector3.UnitX,
        Up = Vector3.UnitY,
        Tangent = Vector3.Cross(Vector3.UnitY, Vector3.UnitX),
        AnchorRadius = 3.4f,
        RingInner = 4.59f,
        RingOuter = 8.16f,
    };

    private static RaceSession Build(string? path = null)
    {
        var r = new RaceSession { BestTimesPath = path };
        r.Build(Saturn());
        return r;
    }

    private static FlightContext Ctx() => new() { AnchorPosition = Anchor };

    /// <summary>One frame with the ship at anchor-relative <paramref name="local"/>.</summary>
    private static void Step(RaceSession r, FlightContext c, Vector3 local, float dt = 1f / Fps)
    {
        c.Dt = dt;
        c.ShipPosition = Anchor + local;
        r.Update(c);
    }

    /// <summary>Start and sit through the countdown on the start line.</summary>
    private static FlightContext StartAndWait(RaceSession r)
    {
        var c = Ctx();
        r.Start();
        int n = (int)MathF.Ceiling(r.CountdownSeconds * Fps) + 1;
        for (int i = 0; i < n && r.View.Phase == RacePhase.Countdown; i++) Step(r, c, r.View.StartPosition);
        Assert.Equal(RacePhase.Running, r.View.Phase);
        return c;
    }

    /// <summary>Fly along the racing line at <paramref name="speed"/>, displaced by
    /// <paramref name="offset"/>(s) (anchor-relative), until finished or past the end.</summary>
    private static void FlyLine(RaceSession r, FlightContext c, float speed, Func<float, Vector3>? offset = null, float until = float.NaN)
    {
        float end = float.IsNaN(until) ? r.TrackLength + 1f : until;
        for (float s = 0f; s < end && r.View.Phase == RacePhase.Running; )
        {
            s += speed / Fps;
            Step(r, c, r.LinePoint(s) + (offset?.Invoke(s) ?? Vector3.Zero));
        }
    }

    private static float Angle(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(a, b), -1f, 1f));

    // ---- Track ---------------------------------------------------------------------------

    [Fact]
    public void Track_HasSaneGates()
    {
        var r = Build();
        var v = r.View;
        Assert.Equal("ui.race.track.ringrun", v.TrackNameKey);
        Assert.InRange(v.Gates.Count, 18, 26);
        Assert.Equal(v.Gates.Count, v.Status.Length);
        Assert.Equal(v.Gates.Count, v.GateTime.Length);
        for (int i = 0; i < v.Gates.Count; i++)
        {
            var g = v.Gates[i];
            Assert.Equal(i == v.Gates.Count - 1, g.IsFinish);
            if (g.IsFinish) Assert.InRange(g.Radius, 0.15f, 0.17f);
            else Assert.InRange(g.Radius, 0.09f, 0.14f);
            Assert.InRange(g.Forward.Length, 0.999f, 1.001f);
            Assert.InRange(g.Up.Length, 0.999f, 1.001f);
            Assert.True(MathF.Abs(Vector3.Dot(g.Forward, g.Up)) < 1e-3f);
            Assert.True(Vector3.Dot(g.Up, Vector3.UnitY) > 0.8f);
        }
        Assert.True(v.Gates[^1].Radius > v.Gates[0].Radius - 1e-6f);
    }

    [Fact]
    public void Track_SpacingAndTurnsAreFlyable()
    {
        var r = Build();
        var g = r.View.Gates;
        for (int i = 1; i < g.Count; i++)
        {
            float d = (g[i].LocalPosition - g[i - 1].LocalPosition).Length;
            Assert.InRange(d, 0.35f, 0.8f);
            Assert.True(Angle(g[i].Forward, g[i - 1].Forward) / d <= 1.5f, $"gate {i}: too sharp");
        }
        // The line itself, sampled every 0.1 units.
        for (float s = 0f; s + 0.1f <= r.TrackLength; s += 0.05f)
            Assert.True(Angle(r.LineDirection(s), r.LineDirection(s + 0.1f)) / 0.1f <= 1.5f, $"line at {s}: too sharp");
    }

    [Fact]
    public void Track_StaysInsideTheRingBand()
    {
        var r = Build();
        var f = Saturn();
        int crossings = 0;
        float lastH = 0.10f;
        bool sparse = false;
        for (float s = 0f; s <= r.TrackLength; s += 0.02f)
        {
            var p = r.LinePoint(s);
            float h = Vector3.Dot(p, f.Up);
            float rad = (p - f.Up * h).Length;
            Assert.InRange(rad, f.RingInner + 0.3f, f.RingOuter - 0.3f);
            Assert.True(p.Length > f.AnchorRadius + 1f);
            Assert.InRange(h, -0.2f, 0.22f);
            if (MathF.Sign(h) != MathF.Sign(lastH) && h != 0f) { crossings++; lastH = h; }
            float frac = (rad - f.RingInner) / (f.RingOuter - f.RingInner);
            sparse |= frac is >= 0.64f and <= 0.70f;
        }
        Assert.Equal(4, crossings);   // two dives: down through the plane and back up, twice
        Assert.True(sparse);
    }

    [Fact]
    public void Track_StartsStraightAtTheFirstGate()
    {
        var r = Build();
        var v = r.View;
        var f = Saturn();
        Assert.Equal(f.Point(0.5f * (f.RingInner + f.RingOuter), 0f, 0.10f), v.StartPosition);
        Assert.Equal(f.Tangent, v.StartForward);
        Assert.Equal(f.Up, v.StartUp);
        var to = v.Gates[0].LocalPosition - v.StartPosition;
        Assert.InRange(Vector3.Dot(to, v.StartForward), 0.45f, 0.55f);
        Assert.True((to - v.StartForward * Vector3.Dot(to, v.StartForward)).Length < 0.03f);
        Assert.True(Vector3.Dot(v.Gates[0].Forward, v.StartForward) > 0.995f);
    }

    [Fact]
    public void Track_ClearZonesCoverGatesAndLine()
    {
        var r = Build();
        var v = r.View;
        Assert.InRange(v.ClearZones.Count, 150, 250);
        foreach (var g in v.Gates)
            Assert.Contains(v.ClearZones, z => (z.Xyz - g.LocalPosition).Length < 1e-4f && z.W >= g.Radius * 1.8f);
        Assert.Contains(v.ClearZones, z => (z.Xyz - v.StartPosition).Length < 1e-4f && z.W >= 0.25f);
        for (float s = 0f; s <= r.TrackLength; s += 0.02f)
        {
            var p = r.LinePoint(s);
            Assert.Contains(v.ClearZones, z => (z.Xyz - p).Length <= z.W * 0.75f);
        }
    }

    [Fact]
    public void Track_MedalTimesAreOrdered()
    {
        var v = Build().View;
        Assert.True(v.GoldTime > 0f && v.GoldTime < v.SilverTime && v.SilverTime < v.BronzeTime);
    }

    // ---- Rules ---------------------------------------------------------------------------

    [Fact]
    public void Countdown_GoesAtTheRightTime()
    {
        var r = Build();
        var c = Ctx();
        r.CountdownSeconds = 3f;
        r.Start();
        Assert.Equal(RacePhase.Countdown, r.View.Phase);
        Assert.Equal(3f, r.View.Countdown);
        for (int i = 0; i < 179; i++) Step(r, c, r.View.StartPosition);
        Assert.Equal(RacePhase.Countdown, r.View.Phase);
        Assert.Equal(0f, r.View.Elapsed);
        Step(r, c, r.View.StartPosition);
        Assert.Equal(RacePhase.Running, r.View.Phase);
        Assert.Equal(RaceEvent.Go, r.View.LastEvent);
        Assert.Equal(0f, r.View.Countdown);
        Assert.InRange(r.View.Elapsed, 0f, 1e-3f);
        Assert.InRange(r.View.Clock, 2.99f, 3.01f);
    }

    [Fact]
    public void FlyingTheLine_PassesEveryGateInOrder()
    {
        var r = Build();
        var c = StartAndWait(r);
        var v = r.View;
        int last = -1;
        for (float s = 0f; s < r.TrackLength + 1f && v.Phase == RacePhase.Running; )
        {
            s += 0.5f / Fps;
            Step(r, c, r.LinePoint(s));
            Assert.True(v.NextGate >= last);
            if (v.LastEvent == RaceEvent.GateMissed) Assert.Fail($"missed gate {v.LastEventGate}");
            last = v.NextGate;
        }
        Assert.Equal(RacePhase.Finished, v.Phase);
        Assert.All(v.Status, st => Assert.Equal(GateStatus.Passed, st));
        Assert.Equal(0f, v.Penalty);
        Assert.Equal(v.Gates.Count, v.NextGate);
        Assert.Equal(RaceEvent.Finished, v.LastEvent);
        for (int i = 1; i < v.GateTime.Length; i++) Assert.True(v.GateTime[i] > v.GateTime[i - 1]);
    }

    [Fact]
    public void SkippingAGate_GivesOneMissAndTheRaceGoesOn()
    {
        var r = Build();
        var c = StartAndWait(r);
        var v = r.View;
        const int Skip = 5;
        float sk = r.GateDistance[Skip];
        var side = v.Gates[Skip].Right * 0.35f;
        int misses = 0;
        RaceEvent seen = RaceEvent.None; float seenClock = float.NaN;
        for (float s = 0f; s < r.TrackLength + 1f && v.Phase == RacePhase.Running; )
        {
            s += 0.5f / Fps;
            float w = Math.Clamp(1f - MathF.Abs(s - sk) / 0.25f, 0f, 1f);
            Step(r, c, r.LinePoint(s) + side * MathF.Min(1f, w * 2f));
            if (v.LastEventClock != seenClock || v.LastEvent != seen)
            {
                seen = v.LastEvent; seenClock = v.LastEventClock;
                if (seen == RaceEvent.GateMissed) { misses++; Assert.Equal(Skip, v.LastEventGate); Assert.Equal(Skip + 1, v.NextGate); }
            }
        }
        Assert.Equal(1, misses);
        Assert.Equal(GateStatus.Missed, v.Status[Skip]);
        Assert.Equal(1, v.Status.Count(st => st == GateStatus.Missed));
        Assert.Equal(v.PenaltyPerMiss, v.Penalty);
        Assert.Equal(RacePhase.Finished, v.Phase);
        Assert.Equal(v.Elapsed + v.PenaltyPerMiss, v.FinishTime, 3);
    }

    [Fact]
    public void FastPassBetweenFrames_StillCounts()
    {
        var r = Build();
        var c = StartAndWait(r);
        var g = r.View.Gates[0];
        Step(r, c, g.LocalPosition - g.Forward * 0.3f);
        Step(r, c, g.LocalPosition + g.Forward * 0.3f);   // 36 units/s: no sample near the gate
        Assert.Equal(GateStatus.Passed, r.View.Status[0]);
        Assert.Equal(1, r.View.NextGate);
        Assert.Equal(GateStatus.Next, r.View.Status[1]);
        Assert.Equal(RaceEvent.GatePassed, r.View.LastEvent);
        Assert.Equal(0, r.View.LastEventGate);
    }

    [Fact]
    public void OneLongFrameThroughSeveralGates_ResolvesThemInOrder()
    {
        var r = Build();
        var c = StartAndWait(r);
        // Teleport-free jump along the line through gates 0 and 1 in one frame.
        Step(r, c, r.LinePoint(r.GateDistance[0] - 0.05f));
        Step(r, c, r.LinePoint(r.GateDistance[0] - 0.05f) + (r.LinePoint(r.GateDistance[1] + 0.05f) - r.LinePoint(r.GateDistance[0] - 0.05f)));
        Assert.True(r.View.NextGate >= 1);
        Assert.Equal(GateStatus.Passed, r.View.Status[0]);
    }

    [Fact]
    public void CrossingBackwards_DoesNotCount()
    {
        var r = Build();
        var c = Ctx();
        var g = r.View.Gates[0];
        r.Start();
        for (int i = 0; i < 400 && r.View.Phase == RacePhase.Countdown; i++) Step(r, c, g.LocalPosition + g.Forward * 0.2f);
        Assert.Equal(RacePhase.Running, r.View.Phase);
        for (int i = 0; i <= 30; i++) Step(r, c, g.LocalPosition + g.Forward * (0.2f - 0.4f * i / 30f));
        Assert.Equal(0, r.View.NextGate);
        Assert.Equal(GateStatus.Next, r.View.Status[0]);
        Assert.Equal(RaceEvent.Go, r.View.LastEvent);
        for (int i = 0; i <= 30; i++) Step(r, c, g.LocalPosition + g.Forward * (-0.2f + 0.4f * i / 30f));
        Assert.Equal(GateStatus.Passed, r.View.Status[0]);
    }

    [Fact]
    public void Paused_ChangesNothing()
    {
        var r = Build();
        var c = StartAndWait(r);
        var v = r.View;
        var g = v.Gates[0];
        Step(r, c, g.LocalPosition - g.Forward * 0.1f);
        float clock = v.Clock, elapsed = v.Elapsed;
        Step(r, c, g.LocalPosition + g.Forward * 0.1f, dt: 0f);
        Assert.Equal(clock, v.Clock);
        Assert.Equal(elapsed, v.Elapsed);
        Assert.Equal(0, v.NextGate);
        Assert.Equal(GateStatus.Next, v.Status[0]);
        r.Start();
        Step(r, c, v.StartPosition, dt: 0f);
        Assert.Equal(r.CountdownSeconds, v.Countdown);
        Assert.Equal(clock, v.Clock);
    }

    [Fact]
    public void Finish_SetsTimeMedalAndRecord()
    {
        var r = Build();
        var v = r.View;
        var c = StartAndWait(r);
        FlyLine(r, c, ShipFlight.BoostSpeed);
        Assert.Equal(RacePhase.Finished, v.Phase);
        Assert.Equal(v.TotalTime, v.FinishTime);
        Assert.InRange(v.FinishTime, (r.GateDistance[^1]) / ShipFlight.BoostSpeed - 0.1f, r.GateDistance[^1] / ShipFlight.BoostSpeed + 0.1f);
        Assert.Equal(3, v.Medal);
        Assert.True(v.NewRecord);
        Assert.Equal(v.FinishTime, v.BestTime);
        Assert.Equal(v.GateTime, v.BestSplits);
        Assert.Equal(RaceEvent.Finished, v.LastEvent);
        Assert.Equal(v.Gates.Count - 1, v.LastEventGate);
        float best = v.BestTime;

        c = StartAndWait(r);
        FlyLine(r, c, ShipFlight.CruiseSpeed);
        Assert.Equal(RacePhase.Finished, v.Phase);
        Assert.False(v.NewRecord);
        Assert.Equal(best, v.BestTime);
        Assert.Equal(1, v.Medal);   // a clean run at cruise speed earns bronze
    }

    [Fact]
    public void SplitDelta_ComparesWithTheBestRun()
    {
        var r = Build();
        var v = r.View;
        var c = StartAndWait(r);
        FlyLine(r, c, 0.5f, until: r.GateDistance[3] + 0.05f);
        Assert.True(float.IsNaN(v.LastSplitDelta));   // no best run yet
        FlyLine(r, c, 0.5f);
        float[] best = (float[])v.BestSplits.Clone();

        c = StartAndWait(r);
        FlyLine(r, c, 0.6f, until: r.GateDistance[3] + 0.05f);
        Assert.Equal(4, v.NextGate);
        Assert.True(v.LastSplitDelta < 0f);
        Assert.Equal(v.GateTime[3] - best[3], v.LastSplitDelta, 4);
    }

    [Fact]
    public void BestTimes_RoundTripThroughAFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "solarsystem-race-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "best.txt");
        try
        {
            var a = Build(path);
            var c = StartAndWait(a);
            FlyLine(a, c, 0.7f);
            Assert.True(File.Exists(path));

            var b = Build(path);
            Assert.Equal(a.View.BestTime, b.View.BestTime);
            Assert.Equal(a.View.BestSplits, b.View.BestSplits);

            File.WriteAllText(path, "ui.race.track.ringrun\tgarbage\t1,2\n\0\0\0 not a record");
            var d = Build(path);
            Assert.True(float.IsNaN(d.View.BestTime));
            Assert.Empty(d.View.BestSplits);
            c = StartAndWait(d);
            FlyLine(d, c, 0.7f);
            Assert.True(d.View.NewRecord);
            Assert.Equal(d.View.BestTime, Build(path).View.BestTime);

            var none = Build(null);
            Assert.True(float.IsNaN(none.View.BestTime));
            c = StartAndWait(none);
            FlyLine(none, c, 0.7f);
            Assert.True(none.View.NewRecord);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Restart_MidRace_ResetsEverything()
    {
        var r = Build();
        var v = r.View;
        var c = StartAndWait(r);
        FlyLine(r, c, 0.5f, s => s > 1f ? v.Gates[0].Right * 0.5f : Vector3.Zero, until: r.GateDistance[6]);
        Assert.True(v.NextGate > 2);
        Assert.True(v.Penalty > 0f);

        r.Start();
        Assert.Equal(RacePhase.Countdown, v.Phase);
        Assert.Equal(0, v.NextGate);
        Assert.Equal(0f, v.Elapsed);
        Assert.Equal(0f, v.Penalty);
        Assert.Equal(GateStatus.Next, v.Status[0]);
        Assert.All(v.Status.Skip(1), st => Assert.Equal(GateStatus.Upcoming, st));
        Assert.All(v.GateTime, t => Assert.True(float.IsNaN(t)));
        Assert.True(float.IsNaN(v.FinishTime));
        Assert.True(float.IsNaN(v.LastSplitDelta));
        Assert.Equal(RaceEvent.None, v.LastEvent);
        // The jump back to the start line is not a flight through gates.
        Step(r, c, v.StartPosition);
        Assert.Equal(0, v.NextGate);

        r.Abort();
        Assert.Equal(RacePhase.Idle, v.Phase);
    }

    [Fact]
    public void IgnoringEveryGate_StillFinishes()
    {
        var r = Build();
        var v = r.View;
        var c = StartAndWait(r);
        var f = Saturn();
        // Fly along the track 0.4 units further out than the line: outside every opening.
        FlyLine(r, c, 0.6f, s =>
        {
            var p = r.LinePoint(s);
            var flat = p - f.Up * Vector3.Dot(p, f.Up);
            return flat.Normalized() * 0.4f * Math.Clamp(s / 0.3f, 0f, 1f);
        });
        Assert.Equal(RacePhase.Finished, v.Phase);
        Assert.All(v.Status, st => Assert.Equal(GateStatus.Missed, st));
        Assert.Equal(v.Gates.Count * v.PenaltyPerMiss, v.Penalty, 3);
        Assert.Equal(RaceEvent.Finished, v.LastEvent);
    }

    [Fact]
    public void FlyingStraightOn_KeepsResolvingGates()
    {
        var r = Build();
        var v = r.View;
        var c = StartAndWait(r);
        for (float d = 0f; d < 3f; d += 0.6f / Fps) Step(r, c, v.StartPosition + v.StartForward * d);
        Assert.True(v.NextGate >= 4, $"only reached gate {v.NextGate}");
        for (int i = 0; i < v.NextGate; i++) Assert.NotEqual(GateStatus.Upcoming, v.Status[i]);
    }

    [Fact]
    public void ThroughALaterGateFirst_MissesTheOnesBefore()
    {
        var r = Build();
        var v = r.View;
        var c = StartAndWait(r);
        // Get beyond gates 0..2 without crossing their planes (a jump longer than
        // TeleportDistance is not flight), then fly through gate 3.
        var g = v.Gates[3];
        Step(r, c, g.LocalPosition - g.Forward * 0.2f);
        Step(r, c, g.LocalPosition - g.Forward * 0.1f);
        Step(r, c, g.LocalPosition + g.Forward * 0.1f);
        Assert.Equal(GateStatus.Passed, v.Status[3]);
        Assert.Equal(4, v.NextGate);
        Assert.Equal(3 * v.PenaltyPerMiss, v.Penalty);
    }

    // ---- Flying start --------------------------------------------------------------------

    /// <summary>Free flight along a straight line through a point near the first gate.</summary>
    private static void FlyThroughFirstGate(RaceSession r, FlightContext c, Vector3 offset, float direction = 1f)
    {
        var g = r.View.Gates[0];
        for (int i = 0; i <= 40; i++)
            Step(r, c, g.LocalPosition + offset + g.Forward * (direction * (-0.2f + 0.4f * i / 40f)));
    }

    [Fact]
    public void FreeFlight_ThroughTheFirstGate_StartsTheRaceAndCountsTheGate()
    {
        var r = Build();
        var v = r.View;
        var c = Ctx();
        Assert.Equal(RacePhase.Idle, v.Phase);
        int run = v.RunNumber;

        // Free flight somewhere else does nothing.
        for (int i = 0; i < 30; i++) Step(r, c, v.StartPosition + v.StartForward * (0.002f * i));
        Assert.Equal(RacePhase.Idle, v.Phase);

        FlyThroughFirstGate(r, c, Vector3.Zero);
        Assert.Equal(RacePhase.Running, v.Phase);
        Assert.True(v.FlyingStart);
        Assert.Equal(run + 1, v.RunNumber);
        Assert.Equal(GateStatus.Passed, v.Status[0]);
        Assert.Equal(GateStatus.Next, v.Status[1]);
        Assert.Equal(1, v.NextGate);
        Assert.Equal(RaceEvent.GatePassed, v.LastEvent);
        Assert.Equal(0, v.LastEventGate);
        Assert.Equal(0f, v.Penalty);
        // The clock runs from the gate: after 0.2 units at 0.01 units a frame, about 20
        // frames (the crossing itself lands a frame either side of the middle).
        Assert.Equal(0f, v.GateTime[0], 3);
        Assert.InRange(v.Elapsed, 18.5f / Fps, 21.5f / Fps);
        Assert.InRange(v.Clock - v.StartClock, 18.5f / Fps, 21.5f / Fps);

        // From there it is an ordinary race.
        FlyLine(r, c, 1.2f);
        // (FlyLine restarts from the line's beginning: the jump back is a teleport, then
        // gate 0's plane is crossed again, which no longer matters.)
        Assert.Equal(RacePhase.Finished, v.Phase);
        Assert.Equal(GateStatus.Passed, v.Status[^1]);
    }

    [Fact]
    public void FreeFlight_PastOrBackwardsThroughTheFirstGate_StartsNothing()
    {
        var r = Build();
        var v = r.View;
        var c = Ctx();
        var g = v.Gates[0];

        // Beside the gate, outside the tolerance: no race, no penalty, no event.
        FlyThroughFirstGate(r, c, g.Right * (g.Radius * RaceSession.PassTolerance + 0.01f));
        Assert.Equal(RacePhase.Idle, v.Phase);
        Assert.Equal(0f, v.Penalty);
        Assert.Equal(RaceEvent.None, v.LastEvent);

        // Through the opening against the direction of travel.
        Step(r, c, g.LocalPosition + g.Forward * 0.2f);
        FlyThroughFirstGate(r, c, Vector3.Zero, direction: -1f);
        Assert.Equal(RacePhase.Idle, v.Phase);

        // Through any other gate.
        var g3 = v.Gates[3];
        Step(r, c, g3.LocalPosition - g3.Forward * 0.1f);
        Step(r, c, g3.LocalPosition + g3.Forward * 0.1f);
        Assert.Equal(RacePhase.Idle, v.Phase);
        Assert.Equal(0, v.RunNumber);
    }

    [Fact]
    public void WingTipOverTheRim_StillCounts_ButClearlyOutsideDoesNot()
    {
        var r = Build();
        var c = Ctx();
        var g = r.View.Gates[0];
        // The centre a fifth of a radius outside the ring: the hull overlaps the rim.
        FlyThroughFirstGate(r, c, g.Up * (g.Radius * 1.2f));
        Assert.Equal(GateStatus.Passed, r.View.Status[0]);

        var r2 = Build();
        var c2 = StartAndWait(r2);
        var g2 = r2.View.Gates[0];
        for (int i = 0; i <= 40; i++)
            Step(r2, c2, g2.LocalPosition + g2.Up * (g2.Radius * 1.4f) + g2.Forward * (-0.2f + 0.4f * i / 40f));
        Assert.Equal(GateStatus.Missed, r2.View.Status[0]);
    }

    [Fact]
    public void FlyingStart_ThenFlyingAway_CallsTheRaceOffAfterThreeMissesInARow()
    {
        var r = Build();
        var v = r.View;
        var c = Ctx();
        FlyThroughFirstGate(r, c, Vector3.Zero);
        Assert.Equal(RacePhase.Running, v.Phase);

        // Carry on high above the track: every gate's plane is crossed far outside its opening.
        int misses = 0;
        float seenClock = v.LastEventClock;
        for (float s = r.GateDistance[0] + 0.2f; s < r.TrackLength && v.Phase == RacePhase.Running; s += 0.6f / Fps)
        {
            Step(r, c, r.LinePoint(s) + Vector3.UnitY * 0.5f);
            if (v.LastEvent == RaceEvent.GateMissed && v.LastEventClock != seenClock) { misses++; seenClock = v.LastEventClock; }
        }

        // Two penalties were shown; the third miss ends the run instead: back to free
        // flight with a clean track and nothing recorded.
        Assert.Equal(RaceSession.FlyingStartMissLimit - 1, misses);
        Assert.Equal(RacePhase.Idle, v.Phase);
        Assert.True(float.IsFinite(v.CalledOffClock));
        Assert.Equal(0f, v.Penalty);
        Assert.Equal(0, v.NextGate);
        Assert.Equal(GateStatus.Next, v.Status[0]);
        Assert.All(v.Status.Skip(1), st => Assert.Equal(GateStatus.Upcoming, st));
        Assert.True(float.IsNaN(v.BestTime));
        Assert.Equal(1, v.RunNumber);

        // The first gate starts the next run as before.
        FlyThroughFirstGate(r, c, Vector3.Zero);
        Assert.Equal(RacePhase.Running, v.Phase);
        Assert.Equal(2, v.RunNumber);
    }

    [Fact]
    public void FlyingStart_MissingTwoThenPassing_KeepsTheRaceOn()
    {
        var r = Build();
        var v = r.View;
        var c = Ctx();
        FlyThroughFirstGate(r, c, Vector3.Zero);
        float from = r.GateDistance[1] - 0.25f, to = r.GateDistance[2] + 0.25f;
        FlyLine(r, c, 0.6f, s => s > from && s < to ? Vector3.UnitY * 0.5f : Vector3.Zero);
        Assert.Equal(RacePhase.Finished, v.Phase);
        Assert.Equal(GateStatus.Missed, v.Status[1]);
        Assert.Equal(GateStatus.Missed, v.Status[2]);
        Assert.Equal(2, v.Status.Count(st => st == GateStatus.Missed));
        Assert.Equal(2 * v.PenaltyPerMiss, v.Penalty);
        Assert.True(float.IsNegativeInfinity(v.CalledOffClock));
    }

    [Fact]
    public void AfterAFinishedRun_TheFirstGateStartsANewOne_AndEnterStillWorks()
    {
        var r = Build();
        var v = r.View;
        var c = StartAndWait(r);
        Assert.False(v.FlyingStart);
        Assert.Equal(1, v.RunNumber);
        FlyLine(r, c, 1.2f);
        Assert.Equal(RacePhase.Finished, v.Phase);
        float best = v.BestTime;

        // Back at the first gate (a teleport in the test), then through it.
        FlyThroughFirstGate(r, c, Vector3.Zero);
        Assert.Equal(RacePhase.Running, v.Phase);
        Assert.True(v.FlyingStart);
        Assert.Equal(2, v.RunNumber);
        Assert.Equal(1, v.NextGate);
        Assert.True(float.IsNaN(v.FinishTime));
        Assert.Equal(best, v.BestTime);

        // Enter mid-race: back to the line, countdown, a standing start.
        r.Start();
        Assert.Equal(RacePhase.Countdown, v.Phase);
        Assert.False(v.FlyingStart);
        Assert.Equal(3, v.RunNumber);
    }
}
