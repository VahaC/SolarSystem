// PROTOTYPE (2026-11-17): arcade flight mode — sound: tests for the flight soundscape.
using Xunit;

namespace SolarSystem.Tests;

/// <summary>
/// Renders <see cref="FlightSoundscape"/> offline (no audio device) with fixed and changing
/// <see cref="FlightSoundState"/> values and pins its levels, silence / ducking, safety
/// (no NaN, bounded peaks, no clicks, no DC), stereo balance, determinism and how it reacts
/// to the pilot and the world: speed, boost, turning, accelerating / decelerating, the air
/// brake, passing rocks, the ring, approaching gates and the start-line countdown.
/// </summary>
public class FlightSoundscapeTests
{
    private const int SR = AudioFormat.SampleRate;
    private const int Block = 800;              // 60 game frames per second at 48 kHz
    private const float CeilingMinus7 = 0.4467f;

    private sealed record Scenario(float Throttle, float Speed, float Boost = 0f, float Turn = 0f, float Ring = 0f)
    {
        public float Accel { get; init; }
        public float Brake { get; init; }
        public float Energy { get; init; } = 1f;
        public float Rock { get; init; }
        public float RockPan { get; init; }
        public int Phase { get; init; }
        public float Countdown { get; init; }
        public float Gate { get; init; }
        public float TurnPan { get; init; }
    }

    private static readonly Scenario Idle = new(0.5f, 0f);
    private static readonly Scenario Cruise = new(0.5f, 0.25f);
    private static readonly Scenario FullBoost = new(1f, 1f, 1f);

    private static void Apply(FlightSoundState s, Scenario c)
    {
        s.Active = 1f;
        s.Throttle01 = c.Throttle;
        s.Speed01 = c.Speed;
        s.Boost01 = c.Boost;
        s.Turn01 = c.Turn;
        s.RingProximity01 = c.Ring;
        s.Accel = c.Accel;
        s.Brake01 = c.Brake;
        s.BoostEnergy01 = c.Energy;
        s.RockProximity01 = c.Rock;
        s.RockPan = c.RockPan;
        s.RacePhase = c.Phase;
        s.Countdown = c.Countdown;
        s.GateApproach01 = c.Gate;
        s.TurnPan = c.TurnPan;
    }

    /// <summary>Renders <paramref name="seconds"/>, calling <paramref name="drive"/> once per
    /// 60 Hz block with the time in seconds (like the game thread does).</summary>
    private static float[] Render(float seconds, Action<FlightSoundState, float> drive, int block = Block,
        FlightSoundscape? scape = null, FlightSoundState? state = null)
    {
        state ??= new FlightSoundState();
        scape ??= new FlightSoundscape(state);
        int total = (int)(seconds * SR);
        var buf = new float[total * 2];
        for (int pos = 0; pos < total; pos += block)
        {
            int n = Math.Min(block, total - pos);
            drive(state, pos / (float)SR);
            Assert.True(scape.Mix(buf.AsSpan(pos * 2, n * 2), n));
        }
        return buf;
    }

    private static float[] RenderSteady(Scenario c, float seconds = 4f) => Render(seconds, (s, _) => Apply(s, c));

    /// <summary>Interleaved slice [from, to) in seconds.</summary>
    private static ReadOnlySpan<float> Slice(float[] b, float from, float to)
    {
        int a = (int)(from * SR) * 2, e = Math.Min(b.Length, (int)(to * SR) * 2);
        return b.AsSpan(a, e - a);
    }

    private static double Rms(ReadOnlySpan<float> x)
    {
        double sum = 0;
        foreach (float v in x) sum += (double)v * v;
        return Math.Sqrt(sum / Math.Max(1, x.Length));
    }

    private static double Db(double v) => 20 * Math.Log10(Math.Max(v, 1e-12));

    private static float Peak(ReadOnlySpan<float> x)
    {
        float p = 0f;
        foreach (float v in x) p = MathF.Max(p, MathF.Abs(v));
        return p;
    }

    /// <summary>Largest jump between consecutive samples of the same channel.</summary>
    private static float MaxDelta(ReadOnlySpan<float> x)
    {
        float m = 0f;
        for (int i = 2; i < x.Length; i++) m = MathF.Max(m, MathF.Abs(x[i] - x[i - 2]));
        return m;
    }

    /// <summary>RMS of the first difference: a cheap "high-band energy" (rises 6 dB/oct).</summary>
    private static double DiffRms(ReadOnlySpan<float> x)
    {
        double sum = 0;
        for (int i = 2; i < x.Length; i++) { double d = x[i] - x[i - 2]; sum += d * d; }
        return Math.Sqrt(sum / Math.Max(1, x.Length - 2));
    }

    private static double ChannelRms(ReadOnlySpan<float> x, int ch)
    {
        double sum = 0; int n = 0;
        for (int i = ch; i < x.Length; i += 2) { sum += (double)x[i] * x[i]; n++; }
        return Math.Sqrt(sum / Math.Max(1, n));
    }

    /// <summary>The frequency in [lo, hi] with the most energy in the mono mix (Goertzel scan).</summary>
    private static double DominantHz(ReadOnlySpan<float> x, double lo, double hi, double step = 0.5)
    {
        int n = x.Length / 2;
        double best = 0, bestHz = lo;
        for (double f = lo; f <= hi; f += step)
        {
            double w = 2 * Math.PI * f / SR, k = 2 * Math.Cos(w), s1 = 0, s2 = 0;
            for (int i = 0; i < n; i++)
            {
                double s0 = 0.5 * (x[2 * i] + x[2 * i + 1]) + k * s1 - s2;
                s2 = s1; s1 = s0;
            }
            double p = s1 * s1 + s2 * s2 - k * s1 * s2;
            if (p > best) { best = p; bestHz = f; }
        }
        return bestHz;
    }

    /// <summary>Standard deviation (dB) of the mono level measured in windows of <paramref name="window"/> seconds.</summary>
    private static double LevelStd(ReadOnlySpan<float> x, float window)
    {
        int w = (int)(window * SR), n = x.Length / 2 / w;
        var levels = new double[n];
        for (int k = 0; k < n; k++)
        {
            double sum = 0;
            for (int i = 0; i < w; i++) { double m = 0.5 * (x[2 * (k * w + i)] + x[2 * (k * w + i) + 1]); sum += m * m; }
            levels[k] = Db(Math.Sqrt(sum / w));
        }
        double mean = levels.Average();
        return Math.Sqrt(levels.Sum(v => (v - mean) * (v - mean)) / Math.Max(1, n));
    }

    /// <summary>Share of the mono energy above <paramref name="hz"/> (4th-order high-pass).</summary>
    private static double EnergyShareAbove(ReadOnlySpan<float> x, float hz)
    {
        var h1 = new Biquad(); var h2 = new Biquad();
        h1.SetHighPass(hz, 0.54f); h2.SetHighPass(hz, 1.31f);
        double all = 0, high = 0;
        for (int i = 0; i < x.Length; i += 2)
        {
            float m = 0.5f * (x[i] + x[i + 1]);
            float h = h2.Process(h1.Process(m));
            all += (double)m * m; high += (double)h * h;
        }
        return high / Math.Max(all, 1e-30);
    }

    /// <summary>Side (L − R) level relative to mid (L + R), in dB: how wide the image is.</summary>
    private static double SideDb(ReadOnlySpan<float> b)
    {
        double diff = 0, sum = 0;
        for (int i = 0; i < b.Length; i += 2)
        {
            diff += (double)(b[i] - b[i + 1]) * (b[i] - b[i + 1]);
            sum += (double)(b[i] + b[i + 1]) * (b[i] + b[i + 1]);
        }
        return Db(Math.Sqrt(diff)) - Db(Math.Sqrt(sum));
    }

    [Fact]
    public void Cruise_SitsAroundMinus26_5DbfsRms()
    {
        var b = Slice(RenderSteady(Cruise), 1f, 4f);
        Assert.InRange(Db(Rms(b)), -27.5, -25.5);
        Assert.True(Peak(b) < CeilingMinus7);
    }

    [Fact]
    public void FullBoost_SitsAroundMinus19_5DbfsRms()
    {
        var b = Slice(RenderSteady(FullBoost), 1f, 4f);
        Assert.InRange(Db(Rms(b)), -20.5, -18.5);
        Assert.True(Peak(b) <= CeilingMinus7);
    }

    [Fact]
    public void Standing_IsQuietAroundMinus33DbfsRms()
    {
        var b = Slice(RenderSteady(Idle), 1f, 4f);
        Assert.InRange(Db(Rms(b)), -36, -30);
    }

    [Fact]
    public void StartLineCountdown_SitsAroundMinus31Dbfs_AndRevsNervously()
    {
        var idle = Slice(RenderSteady(Idle, 6f), 1f, 6f);
        var line = Slice(Render(6f, (s, t) => Apply(s, Idle with { Phase = 1, Countdown = MathF.Max(0f, 5f - t) })), 1f, 6f);
        Assert.InRange(Db(Rms(line)), -32.5, -29.5);
        // Revving blips: the 50 ms level moves far more than when just standing.
        Assert.True(LevelStd(line, 0.05f) > LevelStd(idle, 0.05f) + 1.0,
            $"start line {LevelStd(line, 0.05f):F2} dB vs standing {LevelStd(idle, 0.05f):F2} dB");
    }

    [Fact]
    public void NoSampleAboveMinus8Dbfs_InAnyState()
    {
        Scenario[] all =
        [
            Idle, Cruise, FullBoost, new(0.12f, 0.25f),
            new(1f, 1f, 1f, 1f, 1f), new(0.5f, 0.25f, 0f, 1f, 1f), new(1f, 0.6f, 0.5f, 0.5f, 1f),
            FullBoost with { Accel = 1f, Turn = 1f, Ring = 1f, Rock = 1f, RockPan = 1f, Energy = 0f },
            new Scenario(0.12f, 1f, 0f, 1f, 1f) { Accel = -1f, Brake = 1f, Rock = 1f, RockPan = -1f },
            FullBoost with { Phase = 2, Gate = 1f, Ring = 1f, Accel = 1f },
            Idle with { Phase = 1, Countdown = 0.5f },
        ];
        foreach (var c in all)
            Assert.True(Peak(RenderSteady(c, 3f)) <= CeilingMinus7, $"peak too high for {c}");
    }

    [Fact]
    public void Inactive_IsExactlySilent_AndFadesOutToExactSilence()
    {
        var never = Render(2f, (s, _) => { Apply(s, FullBoost); s.Active = 0f; });
        Assert.All(never, v => Assert.Equal(0f, v));

        var b = Render(4f, (s, t) => { Apply(s, FullBoost); s.Active = t < 2f ? 1f : 0f; });
        Assert.True(Peak(Slice(b, 1.5f, 2f)) > 0.05f);
        foreach (float v in Slice(b, 2.45f, 4f)) Assert.Equal(0f, v);
    }

    [Fact]
    public void Paused_DucksAtLeast30Db()
    {
        double cruise = Rms(Slice(RenderSteady(Cruise), 1f, 4f));
        var p = Render(4f, (s, t) => { Apply(s, Cruise); s.Paused = t >= 1f ? 1f : 0f; });
        double paused = Rms(Slice(p, 2f, 4f));
        Assert.True(Db(paused) <= Db(cruise) - 30, $"paused {Db(paused):F1} vs cruise {Db(cruise):F1}");
    }

    [Fact]
    public void ExtremeAndInvalidStateValues_StayFiniteAndBounded()
    {
        float[] weird = [float.NaN, float.PositiveInfinity, float.NegativeInfinity, -5f, 100f, 1e30f];
        var b = Render(3f, (s, t) =>
        {
            int i = (int)(t * 60f);
            s.Active = 1f;
            s.Paused = weird[i % weird.Length] > 50f ? 0f : weird[(i + 1) % weird.Length];
            s.Throttle01 = weird[i % weird.Length];
            s.Boost01 = weird[(i + 2) % weird.Length];
            s.Speed01 = weird[(i + 3) % weird.Length];
            s.Turn01 = weird[(i + 4) % weird.Length];
            s.RingProximity01 = weird[(i + 5) % weird.Length];
            s.RacePhase = i;
            s.Accel = weird[(i + 1) % weird.Length];
            s.Brake01 = weird[(i + 2) % weird.Length];
            s.BoostEnergy01 = weird[(i + 3) % weird.Length];
            s.RockProximity01 = weird[(i + 4) % weird.Length];
            s.RockPan = weird[(i + 5) % weird.Length];
            s.GateApproach01 = weird[i % weird.Length];
            s.Countdown = weird[(i + 1) % weird.Length];
            s.Combo = i * 1000;
            s.TurnPan = weird[(i + 3) % weird.Length];
        });
        foreach (float v in b)
        {
            Assert.True(float.IsFinite(v));
            Assert.True(MathF.Abs(v) <= CeilingMinus7);
        }
    }

    public static TheoryData<string> Jumps => ["active-on", "speed-reset", "boost-on", "boost-off", "ring-on", "turn-on", "pause-toggle", "active-off",
        "brake-on", "rock-on", "accel-up", "gate-release", "turn-flip"];

    /// <summary>When a state value jumps between two 60 Hz frames, the samples right after
    /// the jump move no faster than the steady sound on either side of it (no click).</summary>
    [Theory]
    [MemberData(nameof(Jumps))]
    public void AbruptStateJumps_DoNotClick(string jump)
    {
        (Scenario before, Scenario after) = jump switch
        {
            "speed-reset" => (Cruise, Idle),
            "boost-on" => (Cruise, FullBoost),
            "boost-off" => (FullBoost, Cruise),
            "ring-on" => (Cruise, Cruise with { Ring = 1f }),
            "turn-on" => (Cruise, Cruise with { Turn = 1f }),
            "turn-flip" => (Cruise with { Turn = 1f, TurnPan = -1f }, Cruise with { Turn = 1f, TurnPan = 1f }),
            "brake-on" => (Cruise, Cruise with { Brake = 1f, Throttle = 0.12f }),
            "rock-on" => (Cruise, Cruise with { Rock = 1f, RockPan = 1f }),
            "accel-up" => (Cruise, Cruise with { Accel = 1f }),
            "gate-release" => (Cruise with { Phase = 2, Gate = 1f }, Cruise with { Phase = 2 }),
            _ => (Cruise, Cruise),
        };
        var b = Render(4f, (s, t) =>
        {
            bool late = t >= 2f;
            Apply(s, late ? after : before);
            if (jump == "active-on") s.Active = late ? 1f : 0f;
            if (jump == "active-off") s.Active = late ? 0f : 1f;
            if (jump == "pause-toggle") s.Paused = t >= 2f && t < 3f ? 1f : 0f;
        });
        float steady = MathF.Max(MaxDelta(Slice(b, 1f, 1.95f)), MaxDelta(Slice(b, 3.5f, 4f)));
        if (jump is "active-on") steady = MaxDelta(Slice(RenderSteady(Cruise, 2f), 1f, 2f));
        float transition = MaxDelta(Slice(b, 1.98f, 2.3f));
        float transition2 = jump == "pause-toggle" ? MaxDelta(Slice(b, 2.98f, 3.3f)) : 0f;
        Assert.True(MathF.Max(transition, transition2) <= steady * 1.5f + 0.002f,
            $"{jump}: transition delta {MathF.Max(transition, transition2):F4} vs steady {steady:F4}");
    }

    [Fact]
    public void NoDcOffset_AndBalancedStereo()
    {
        foreach (var c in new[] { Idle, Cruise, FullBoost, Cruise with { Ring = 1f, Turn = 0.7f }, Cruise with { Brake = 1f, Phase = 2, Gate = 0.8f } })
        {
            var b = Slice(RenderSteady(c, 5f), 1f, 5f);
            double l = 0, r = 0;
            for (int i = 0; i < b.Length; i += 2) { l += b[i]; r += b[i + 1]; }
            int n = b.Length / 2;
            Assert.InRange(l / n, -2e-3, 2e-3);
            Assert.InRange(r / n, -2e-3, 2e-3);
            Assert.InRange(Db(ChannelRms(b, 0)) - Db(ChannelRms(b, 1)), -1.0, 1.0);
        }
    }

    [Fact]
    public void TwinEngines_AreNotMono()
    {
        var b = Slice(RenderSteady(Cruise), 1f, 4f);
        double diff = 0;
        for (int i = 0; i < b.Length; i += 2) diff += (double)(b[i] - b[i + 1]) * (b[i] - b[i + 1]);
        double side = Math.Sqrt(diff / (b.Length / 2));
        Assert.True(Db(side) > Db(Rms(b)) - 20, "expected some stereo width between the engines");
    }

    [Fact]
    public void SameStates_GiveIdenticalSamples_RegardlessOfBlockSize()
    {
        void Drive(FlightSoundState s, float t)
        {
            Apply(s, t < 1f ? Cruise : FullBoost with { Ring = 1f, Turn = 0.5f });
        }
        var a = Render(2f, Drive);
        var b = Render(2f, Drive);
        Assert.Equal(a, b);

        // With constant states the block size of the mixer callbacks does not matter.
        var c = Render(1.5f, (s, _) => Apply(s, Cruise with { Ring = 1f }), block: 800);
        var d = Render(1.5f, (s, _) => Apply(s, Cruise with { Ring = 1f }), block: 333);
        Assert.Equal(c, d);
    }

    [Fact]
    public void Boost_IsLouderAndBrighterThanCruise()
    {
        var cruise = Slice(RenderSteady(Cruise), 1f, 4f);
        var boost = Slice(RenderSteady(FullBoost), 1f, 4f);
        Assert.True(Db(Rms(boost)) > Db(Rms(cruise)) + 5);
        double brightCruise = DiffRms(cruise) / Rms(cruise);
        double brightBoost = DiffRms(boost) / Rms(boost);
        Assert.True(brightBoost > brightCruise * 2, $"brightness boost {brightBoost:F4} cruise {brightCruise:F4}");
    }

    [Fact]
    public void EngineFundamental_RisesWithSpeed()
    {
        double prev = 0;
        foreach (float speed in new[] { 0f, 0.25f, 0.5f })
        {
            var b = Slice(RenderSteady(new Scenario(0.5f, speed), 2.5f), 1.5f, 2.5f);
            double hz = DominantHz(b, 45, 140);
            Assert.True(hz > prev + 2, $"speed {speed}: {hz} Hz not above {prev} Hz");
            prev = hz;
        }
    }

    [Fact]
    public void RingProximity_AddsHighGlitter()
    {
        var plain = Slice(RenderSteady(Cruise), 1f, 4f);
        var ring = Slice(RenderSteady(Cruise with { Ring = 1f }), 1f, 4f);
        Assert.True(DiffRms(ring) > DiffRms(plain) * 1.5);
    }

    [Fact]
    public void Mix_AddsToWhatIsAlreadyInTheBuffer()
    {
        var s1 = new FlightSoundState(); Apply(s1, Cruise);
        var s2 = new FlightSoundState(); Apply(s2, Cruise);
        var a = new FlightSoundscape(s1);
        var b = new FlightSoundscape(s2);
        var clean = new float[Block * 2];
        var prefilled = new float[Block * 2];
        Array.Fill(prefilled, 0.25f);
        for (int k = 0; k < 30; k++)
        {
            Array.Clear(clean);
            Array.Fill(prefilled, 0.25f);
            a.Mix(clean, Block);
            b.Mix(prefilled, Block);
        }
        Assert.True(Peak(clean) > 0.001f);
        for (int i = 0; i < clean.Length; i++) Assert.Equal(clean[i] + 0.25f, prefilled[i], 1e-6f);
    }

    [Fact]
    public void EveryState_HasAMidRangeVoice_AudibleOnSmallSpeakers()
    {
        Assert.True(EnergyShareAbove(Slice(RenderSteady(Cruise), 1f, 4f), 150f) >= 0.40);
        Assert.True(EnergyShareAbove(Slice(RenderSteady(FullBoost), 1f, 4f), 150f) >= 0.60);
        Assert.True(EnergyShareAbove(Slice(RenderSteady(Idle), 1f, 4f), 150f) >= 0.35);
    }

    [Fact]
    public void SteadyCruise_StillPulses_AndEvolves()
    {
        var b = Slice(RenderSteady(Cruise, 9f), 1f, 9f);
        Assert.True(LevelStd(b, 0.02f) > 1.5, $"20 ms level std {LevelStd(b, 0.02f):F2} dB");   // the thrum
        // No two seconds alike: second-to-second level differences from the slow surge.
        Assert.True(LevelStd(b, 1f) > 0.3, $"1 s level std {LevelStd(b, 1f):F2} dB");
    }

    [Fact]
    public void Speed_CoversAWidePitchAndBrightnessRange()
    {
        static double Bright(ReadOnlySpan<float> x) => DiffRms(x) / Rms(x);
        var stand = Slice(RenderSteady(Idle, 2.5f), 1.5f, 2.5f);
        var cruise = Slice(RenderSteady(Cruise, 2.5f), 1.5f, 2.5f);
        var fast = Slice(RenderSteady(new Scenario(0.5f, 0.6f), 2.5f), 1.5f, 2.5f);
        double hzStand = DominantHz(stand, 45, 200, 1), hzFast = DominantHz(fast, 45, 200, 1);
        Assert.True(hzFast > hzStand * 1.9, $"standing {hzStand} Hz, fast {hzFast} Hz: less than an octave");
        Assert.True(Bright(cruise) > Bright(stand) * 1.2 && Bright(fast) > Bright(cruise) * 1.2,
            $"brightness {Bright(stand):F4} / {Bright(cruise):F4} / {Bright(fast):F4}");
    }

    [Fact]
    public void Turning_DipsThePitch_AddsGrowl_AndWidens()
    {
        var straight = Slice(RenderSteady(Cruise, 2.5f), 1.5f, 2.5f);
        var turning = Slice(RenderSteady(Cruise with { Turn = 1f }, 2.5f), 1.5f, 2.5f);
        Assert.True(DominantHz(turning, 60, 110) < DominantHz(straight, 60, 110) - 2);
        Assert.True(SideDb(turning) > SideDb(straight) + 2, $"side {SideDb(turning):F1} vs {SideDb(straight):F1} dB");
        Assert.True(LevelStd(turning, 0.02f) > LevelStd(straight, 0.02f));
    }

    [Fact]
    public void Accelerating_SpoolsUpAheadOfTheSpeed_DeceleratingSagsAndCrackles()
    {
        var steady = Slice(RenderSteady(Cruise, 2.5f), 1.5f, 2.5f);
        var up = Slice(RenderSteady(Cruise with { Accel = 1f }, 2.5f), 1.5f, 2.5f);
        var down = Slice(RenderSteady(Cruise with { Accel = -1f }, 2.5f), 1.5f, 2.5f);
        double hz = DominantHz(steady, 60, 140), hzUp = DominantHz(up, 60, 140), hzDown = DominantHz(down, 50, 140);
        Assert.True(hzUp > hz * 1.15, $"spool {hzUp} Hz vs {hz} Hz");
        Assert.True(hzDown < hz * 0.9, $"sag {hzDown} Hz vs {hz} Hz");
        Assert.True(DiffRms(up) / Rms(up) > DiffRms(steady) / Rms(steady) * 1.3, "spool should brighten (turbo whine)");
        // Overrun crackles: bright pops on top of a lower engine (about twice the high-band share).
        Assert.True(DiffRms(down) / Rms(down) > DiffRms(steady) / Rms(steady) * 1.4,
            $"brightness {DiffRms(down) / Rms(down):F4} vs {DiffRms(steady) / Rms(steady):F4}");
    }

    [Fact]
    public void AirBrake_AddsAnAudibleHiss()
    {
        var plain = Slice(RenderSteady(Cruise with { Throttle = 0.12f }), 1f, 4f);
        var brake = Slice(RenderSteady(Cruise with { Throttle = 0.12f, Brake = 1f }), 1f, 4f);
        Assert.True(DiffRms(brake) > DiffRms(plain) * 2);
        Assert.True(Db(Rms(brake)) > Db(Rms(plain)) + 1);
    }

    [Fact]
    public void PassingRock_SwellsOnItsSide()
    {
        foreach (float pan in new[] { -1f, 1f })
        {
            var b = Slice(RenderSteady(Cruise with { Rock = 0.9f, RockPan = pan }, 3f), 1f, 3f);
            double lr = Db(ChannelRms(b, 0)) - Db(ChannelRms(b, 1));
            Assert.True(pan < 0 ? lr > 3 : lr < -3, $"pan {pan}: L-R {lr:F1} dB");
        }
        var none = Slice(RenderSteady(Cruise, 3f), 1f, 3f);
        var near = Slice(RenderSteady(Cruise with { Rock = 0.9f }, 3f), 1f, 3f);
        Assert.True(Db(Rms(near)) > Db(Rms(none)) + 1.5, $"rock {Db(Rms(near)):F1} vs none {Db(Rms(none)):F1} dB");
    }

    [Fact]
    public void Ring_IsClearlyAudible()
    {
        var plain = Slice(RenderSteady(Cruise), 1f, 4f);
        var ring = Slice(RenderSteady(Cruise with { Ring = 1f }), 1f, 4f);
        Assert.True(Db(Rms(ring)) > Db(Rms(plain)) + 2);
    }

    [Fact]
    public void GateApproach_BuildsTension_ThenReleasesWhenPassed()
    {
        // Racing at cruise; the next gate approaches over 2 s from t = 1 and is passed at t = 3.
        var b = Render(4.5f, (s, t) => Apply(s, Cruise with { Phase = 2, Gate = t < 1f || t >= 3f ? 0f : (t - 1f) / 2f }));
        double before = Rms(Slice(b, 0.5f, 1f)), build = Rms(Slice(b, 2.8f, 3f)), after = Rms(Slice(b, 3.9f, 4.4f));
        // The same race without a gate: identical until the riser starts.
        var noGate = Render(4.5f, (s, _) => Apply(s, Cruise with { Phase = 2 }));
        Assert.Equal(Rms(Slice(noGate, 1.5f, 1.9f)), Rms(Slice(b, 1.5f, 1.9f)), 9);   // approach below 0.55: silent
        before = Rms(Slice(noGate, 2.8f, 3f));
        Assert.True(Db(build) > Db(before) + 1, $"build {Db(build):F1} vs {Db(before):F1}");
        Assert.True(Db(build) < Db(before) + 4, $"riser must stay under the engine: {Db(build):F1} vs {Db(before):F1}");
        before = Rms(Slice(noGate, 3.9f, 4.4f));
        Assert.True(Db(after) < Db(before) + 1, $"after {Db(after):F1} vs {Db(before):F1}");
        // Outside a race the approach value is ignored.
        var free = Render(3f, (s, _) => Apply(s, Cruise with { Phase = 0, Gate = 1f }));
        var plain = Render(3f, (s, _) => Apply(s, Cruise));
        Assert.Equal(plain, free);
    }

    [Fact]
    public void LowBoostReserve_MakesTheRoarSputter()
    {
        var full = Slice(RenderSteady(FullBoost, 4f), 1.5f, 4f);
        var dry = Slice(RenderSteady(FullBoost with { Energy = 0.03f }, 4f), 1.5f, 4f);
        Assert.True(LevelStd(dry, 0.02f) > LevelStd(full, 0.02f) + 1.0,
            $"sputter {LevelStd(dry, 0.02f):F2} dB vs {LevelStd(full, 0.02f):F2} dB");
    }

    [Fact]
    public void BoostRelease_SweepsDown_InsteadOfStopping()
    {
        var b = Render(4f, (s, t) => Apply(s, t < 2f ? FullBoost : Cruise));
        double hold = Rms(Slice(b, 1.5f, 2f)), tail = Rms(Slice(b, 2.2f, 2.5f)), settled = Rms(Slice(b, 3.5f, 4f));
        Assert.True(Db(tail) < Db(hold) && Db(tail) > Db(settled) + 2,
            $"hold {Db(hold):F1} tail {Db(tail):F1} settled {Db(settled):F1}");
    }

    [Fact]
    public void HardTurn_LeansTheEngineIntoTheTurn_AndBalancesOverBothSides()
    {
        double Lr(Scenario c)
        {
            var b = Slice(RenderSteady(c, 3f), 1f, 3f);
            Assert.True(ChannelRms(b, 0) > 0.01 && ChannelRms(b, 1) > 0.01, "no channel may go silent");
            return Db(ChannelRms(b, 0)) - Db(ChannelRms(b, 1));
        }
        double left = Lr(Cruise with { Turn = 1f, TurnPan = -1f });
        double right = Lr(Cruise with { Turn = 1f, TurnPan = 1f });
        Assert.InRange(left, 2.0, 8.0);      // hard left: louder on the left, a few dB
        Assert.InRange(right, -8.0, -2.0);   // hard right: the mirror
        // A gentle turn leans less than a hard one.
        Assert.True(Math.Abs(Lr(Cruise with { Turn = 0.3f, TurnPan = 1f })) < Math.Abs(right));

        // Equal left and right turns: balanced on average.
        var flight = Slice(Render(8f, (s, t) => Apply(s, Cruise with { Turn = 0.8f, TurnPan = (int)(t / 1f) % 2 == 0 ? -1f : 1f })), 1f, 7f);
        Assert.InRange(Db(ChannelRms(flight, 0)) - Db(ChannelRms(flight, 1)), -1.0, 1.0);
    }

    [Fact]
    public void Ceiling_IsOnlyASafety_PeaksStayLow()
    {
        Scenario[] ordinary =
        [
            Idle, Cruise, Cruise with { Turn = 1f, TurnPan = 1f }, Cruise with { Ring = 1f, Turn = 0.6f },
            Cruise with { Ring = 1f, Rock = 0.9f, RockPan = -1f }, Cruise with { Phase = 2, Gate = 1f },
            Cruise with { Throttle = 0.12f, Brake = 1f, Accel = -1f }, Idle with { Phase = 1, Countdown = 0.5f },
        ];
        foreach (var c in ordinary)
            Assert.True(Db(Peak(Slice(RenderSteady(c, 3f), 0.5f, 3f))) <= -12.0, $"peak too high for {c}");
        Assert.True(Db(Peak(Slice(RenderSteady(FullBoost with { Ring = 1f, Turn = 0.5f }, 3f), 0.5f, 3f))) <= -10.0);
    }

    [Fact]
    public void TopEnd_StaysTame_TheBodyCarriesTheVoice()
    {
        var boost = Slice(RenderSteady(FullBoost), 1f, 4f);
        Assert.True(EnergyShareAbove(boost, 2000f) <= 0.22, $"boost above 2 kHz {EnergyShareAbove(boost, 2000f):P1}");
        Assert.True(EnergyShareAbove(boost, 8000f) <= 0.025, $"boost above 8 kHz {EnergyShareAbove(boost, 8000f):P1}");
        var ring = Slice(RenderSteady(Cruise with { Ring = 1f }), 1f, 4f);
        Assert.True(EnergyShareAbove(ring, 2000f) <= 0.15, $"ring above 2 kHz {EnergyShareAbove(ring, 2000f):P1}");
    }
}
