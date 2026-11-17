using SolarSystem;
using Xunit;
using Xunit.Abstractions;

namespace SolarSystem.Tests;

// PROTOTYPE (2026-11-17): arcade flight mode — sound: offline checks of the adaptive music
// (levels, silence, ducking, clicks, DC, determinism, tempo, key, arrangement, combo /
// medal layers, allocation, cost). No audio device is used.
public class FlightMusicTests
{
    private const int Sr = AudioFormat.SampleRate;
    private const int Block = 480;   // 10 ms, the game updates the state between blocks
    private readonly ITestOutputHelper _out;

    public FlightMusicTests(ITestOutputHelper output) => _out = output;

    // ---- helpers ----------------------------------------------------------------------

    /// <summary>Renders <paramref name="seconds"/> of music, calling <paramref name="update"/>
    /// with the time (seconds since the render started) before each 10 ms block.</summary>
    private static float[] Render(FlightMusic music, FlightSoundState state, double seconds, Action<double>? update = null)
    {
        int frames = (int)(seconds * Sr);
        var buf = new float[frames * 2];
        for (int f = 0; f < frames; f += Block)
        {
            update?.Invoke((double)f / Sr);
            int n = Math.Min(Block, frames - f);
            Assert.True(music.Mix(buf.AsSpan(f * 2, n * 2), n));
        }
        return buf;
    }

    private const double FreeEnd = 30, CountEnd = 33, RaceEnd = 73, FinishEnd = 85, ScenarioSeconds = 100;

    /// <summary>The scenario: 30 s free flight, 3 s countdown, 40 s race (progress 0 → 1,
    /// a gate every 1.6 s building the combo, one miss at 50 s, boost now and then), a
    /// gold finish for 12 s, then 15 s of free flight.</summary>
    private static void Scenario(FlightSoundState s, double t)
    {
        s.Active = 1f;
        s.Boost01 = 0f; s.Combo = 0; s.GateApproach01 = 0f; s.Medal = 0;
        if (t < FreeEnd) { s.RacePhase = 0; s.RaceProgress01 = 0f; s.Countdown = 0f; }
        else if (t < CountEnd) { s.RacePhase = 1; s.Countdown = (float)(CountEnd - t); }
        else if (t < RaceEnd)
        {
            double r = t - CountEnd;
            s.RacePhase = 2; s.Countdown = 0f;
            s.RaceProgress01 = (float)(r / (RaceEnd - CountEnd));
            int gates = (int)(r / 1.6);
            s.Combo = r < 17 ? gates : gates - (int)(17 / 1.6);   // the miss at 50 s
            s.GateApproach01 = (float)(r / 1.6 - gates);
            s.Boost01 = (t % 10) > 7 ? 1f : 0f;
        }
        else if (t < FinishEnd) { s.RacePhase = 3; s.RaceProgress01 = 1f; s.Medal = 3; }
        else { s.RacePhase = 0; s.RaceProgress01 = 0f; }
    }

    private static (double rms, double peak) Measure(float[] buf, double from, double to)
    {
        int a = (int)(from * Sr) * 2, b = Math.Min(buf.Length, (int)(to * Sr) * 2);
        double sum = 0, peak = 0;
        for (int i = a; i < b; i++) { sum += (double)buf[i] * buf[i]; peak = Math.Max(peak, Math.Abs(buf[i])); }
        return (Math.Sqrt(sum / Math.Max(1, b - a)), peak);
    }

    private static double Db(double x) => 20 * Math.Log10(Math.Max(x, 1e-12));

    private static double MaxDelta(float[] buf, double from, double to)
    {
        int a = Math.Max(2, (int)(from * Sr) * 2), b = Math.Min(buf.Length, (int)(to * Sr) * 2);
        double m = 0;
        for (int i = a; i < b; i++) m = Math.Max(m, Math.Abs(buf[i] - buf[i - 2]));
        return m;
    }

    /// <summary>Mono power at <paramref name="hz"/> via Goertzel over Hann-windowed 0.2 s frames.</summary>
    private static double Goertzel(float[] buf, double from, double to, double hz)
    {
        int len = Sr / 5;
        double total = 0;
        double w = 2 * Math.PI * hz / Sr, coeff = 2 * Math.Cos(w);
        for (int start = (int)(from * Sr); start + len <= (int)(to * Sr); start += len / 2)
        {
            double s1 = 0, s2 = 0;
            for (int i = 0; i < len; i++)
            {
                double win = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (len - 1));
                double x = 0.5 * (buf[(start + i) * 2] + buf[(start + i) * 2 + 1]) * win;
                double s0 = x + coeff * s1 - s2;
                s2 = s1; s1 = s0;
            }
            total += s1 * s1 + s2 * s2 - coeff * s1 * s2;
        }
        return total;
    }

    private static double HighBandFraction(float[] buf, double from, double to)
    {
        // Energy of the first difference relative to the signal: a cheap brightness measure.
        int a = Math.Max(2, (int)(from * Sr) * 2), b = (int)(to * Sr) * 2;
        double e = 0, d = 0;
        for (int i = a; i < b; i++) { e += buf[i] * buf[i]; double x = buf[i] - buf[i - 2]; d += x * x; }
        return d / Math.Max(e, 1e-20);
    }

    /// <summary>All traced notes of one render.</summary>
    private sealed record Note(FlightMusic.NoteKind Kind, int Value, long At);

    private static List<Note> Trace(FlightMusic m)
    {
        var list = new List<Note>();
        m.NoteTrace = (k, v, at) => list.Add(new Note(k, v, at));
        return list;
    }

    private static int Count(List<Note> notes, FlightMusic.NoteKind kind, double from, double to)
        => notes.Count(n => n.Kind == kind && n.At >= from * Sr && n.At < to * Sr);

    /// <summary>A fingerprint of every bar's content (kind, value, position in the bar),
    /// with bars counted from <paramref name="origin"/> seconds.</summary>
    private static long[] BarFingerprints(List<Note> notes, double origin, int bars)
    {
        var fp = new long[bars];
        long o = (long)(origin * Sr);
        foreach (var n in notes)
        {
            if (n.Kind is FlightMusic.NoteKind.Section) continue;
            long rel = n.At - o;
            if (rel < 0) continue;
            int bar = (int)(rel / FlightMusic.BarSamples);
            if (bar >= bars) continue;
            long pos = rel % FlightMusic.BarSamples;
            fp[bar] = fp[bar] * 1000003 + ((int)n.Kind * 131 + n.Value + 7) * 100003 + pos;
        }
        return fp;
    }

    // ---- levels -----------------------------------------------------------------------

    [Fact]
    public void FreeFlight_IsCalmerThanTheRace()
    {
        var s = new FlightSoundState { Active = 1f };
        var buf = Render(new FlightMusic(s), s, 130);
        var (rms, peak) = Measure(buf, 4, 130);
        _out.WriteLine($"free flight: {Db(rms):F1} dBFS RMS, {Db(peak):F1} dBFS peak");
        Assert.InRange(Db(rms), -28.0, -22.0);
        Assert.True(Db(peak) < -9.0, $"peak {Db(peak):F1} dBFS");
    }

    [Fact]
    public void Race_AtFullIntensity_IsLouderAndBrighter()
    {
        var s = new FlightSoundState { Active = 1f };
        var m = new FlightMusic(s);
        var buf = Render(m, s, 48, t =>
        {
            if (t < 16) { s.RacePhase = 0; }
            else { s.RacePhase = 2; s.RaceProgress01 = 1f; s.Combo = 20; }
        });
        var free = Measure(buf, 4, 16);
        var race = Measure(buf, 24, 48);
        _out.WriteLine($"free {Db(free.rms):F1} / race {Db(race.rms):F1} dBFS RMS, race peak {Db(race.peak):F1}");
        Assert.InRange(Db(race.rms), -21.5, -16.5);
        Assert.True(Db(Measure(buf, 0, 48).peak) < -7.0);
        Assert.True(Db(race.rms) > Db(free.rms) + 4.0);
        Assert.True(HighBandFraction(buf, 24, 48) > HighBandFraction(buf, 4, 16) * 1.5);
    }

    [Fact]
    public void WholeScenario_StaysUnderPeakCeiling_AndHasNoDcOrImbalance()
    {
        var s = new FlightSoundState();
        var buf = Render(new FlightMusic(s), s, ScenarioSeconds, t => Scenario(s, t));
        var (_, peak) = Measure(buf, 0, ScenarioSeconds);
        _out.WriteLine($"peak {Db(peak):F1} dBFS; free {Db(Measure(buf, 4, FreeEnd).rms):F1}, countdown {Db(Measure(buf, FreeEnd, CountEnd).rms):F1}, " +
                       $"race {Db(Measure(buf, CountEnd, RaceEnd).rms):F1} (last third {Db(Measure(buf, 60, RaceEnd).rms):F1}), outro {Db(Measure(buf, RaceEnd, FinishEnd).rms):F1}, free again {Db(Measure(buf, FinishEnd, ScenarioSeconds).rms):F1} dBFS RMS");
        Assert.True(Db(peak) < -7.0, $"peak {Db(peak):F1} dBFS");

        double sumL = 0, sumR = 0, eL = 0, eR = 0;
        for (int i = 0; i < buf.Length; i += 2) { sumL += buf[i]; sumR += buf[i + 1]; eL += buf[i] * buf[i]; eR += buf[i + 1] * buf[i + 1]; }
        int n = buf.Length / 2;
        _out.WriteLine($"DC L {sumL / n:E2} R {sumR / n:E2}; balance {10 * Math.Log10(eL / eR):F2} dB");
        Assert.True(Math.Abs(sumL / n) < 1e-3 && Math.Abs(sumR / n) < 1e-3);
        Assert.InRange(10 * Math.Log10(eL / eR), -1.0, 1.0);
    }

    // ---- silence, fades, ducking --------------------------------------------------------

    [Fact]
    public void Inactive_IsExactlySilent()
    {
        var s = new FlightSoundState { Active = 0f, RacePhase = 2, RaceProgress01 = 0.7f, Combo = 12 };
        var buf = Render(new FlightMusic(s), s, 3);
        Assert.All(buf, x => Assert.Equal(0f, x));
    }

    [Fact]
    public void Deactivated_FadesToExactSilenceWithinASecond_AndComesBack()
    {
        var s = new FlightSoundState();
        var buf = Render(new FlightMusic(s), s, 18, t => { s.Active = t < 6 || t >= 14 ? 1f : 0f; });
        for (int i = (int)(7.05 * Sr) * 2; i < 14 * Sr * 2; i++) Assert.Equal(0f, buf[i]);
        Assert.True(Db(Measure(buf, 5, 6).rms) > -40);
        Assert.True(Db(Measure(buf, 6.0, 6.3).rms) > Db(Measure(buf, 6.7, 7.0).rms) + 6, "fade must be gradual");
        Assert.True(Db(Measure(buf, 16, 18).rms) > -40, "music must come back");
        Assert.True(MaxDelta(buf, 5.5, 7.5) < 0.05 && MaxDelta(buf, 13.5, 15.5) < 0.05);
    }

    [Fact]
    public void Paused_DucksAtLeast30Db()
    {
        var a = new FlightSoundState { Active = 1f, RacePhase = 2, RaceProgress01 = 0.8f };
        var b = new FlightSoundState { Active = 1f, RacePhase = 2, RaceProgress01 = 0.8f };
        var bufA = Render(new FlightMusic(a), a, 14);
        var bufB = Render(new FlightMusic(b), b, 14, t => b.Paused = t >= 8 ? 1f : 0f);
        double ra = Measure(bufA, 9, 14).rms, rb = Measure(bufB, 9, 14).rms;
        _out.WriteLine($"paused attenuation {Db(ra) - Db(rb):F1} dB");
        Assert.True(Db(ra) - Db(rb) >= 30.0);
    }

    // ---- robustness ----------------------------------------------------------------------

    [Fact]
    public void OddStateValues_GiveFiniteBoundedOutput()
    {
        var s = new FlightSoundState();
        float[] odd = [float.NaN, float.PositiveInfinity, float.NegativeInfinity, -5f, 7f, 0.5f, 1f];
        int[] phases = [-3, 0, 1, 2, 3, 99, int.MinValue];
        int[] ints = [-7, 0, 3, 12, 25, 1000, int.MinValue, int.MaxValue];
        int k = 0;
        var buf = Render(new FlightMusic(s), s, 20, t =>
        {
            k++;
            s.Active = odd[k % odd.Length] is var v && float.IsNaN(v) ? 1f : v;
            s.Paused = odd[(k / 7) % odd.Length];
            s.RacePhase = phases[(k / 50) % phases.Length];
            s.RaceProgress01 = odd[(k / 3) % odd.Length];
            s.Boost01 = odd[(k / 5) % odd.Length];
            s.Countdown = odd[(k / 11) % odd.Length];
            s.Speed01 = odd[(k / 13) % odd.Length];
            s.GateApproach01 = odd[(k / 2) % odd.Length];
            s.Combo = ints[(k / 9) % ints.Length];
            s.Medal = ints[(k / 17) % ints.Length];
        });
        Assert.All(buf, x => Assert.True(float.IsFinite(x) && Math.Abs(x) <= FlightMusic.CeilingMax + 1e-4f));
    }

    [Fact]
    public void Mix_AddsToTheBuffer()
    {
        var s1 = new FlightSoundState { Active = 1f };
        var s2 = new FlightSoundState { Active = 1f };
        var clean = Render(new FlightMusic(s1), s1, 3);
        var m = new FlightMusic(s2);
        var buf = new float[clean.Length];
        Array.Fill(buf, 0.1f);
        for (int f = 0; f < clean.Length / 2; f += Block) m.Mix(buf.AsSpan(f * 2, Block * 2), Block);
        for (int i = 0; i < buf.Length; i++) Assert.Equal(0.1f + clean[i], buf[i], 1e-6f);
        Assert.True(Measure(clean, 2, 3).rms > 0);
    }

    [Fact]
    public void SameStates_GiveIdenticalSamples()
    {
        var s1 = new FlightSoundState();
        var s2 = new FlightSoundState();
        var a = Render(new FlightMusic(s1), s1, 30, t => Scenario(s1, t + 25));
        var b = Render(new FlightMusic(s2), s2, 30, t => Scenario(s2, t + 25));
        Assert.Equal(a, b);
    }

    [Fact]
    public void Mix_DoesNotAllocate_AndIsCheap()
    {
        var s = new FlightSoundState();
        var m = new FlightMusic(s);
        var buf = new float[Block * 2];
        for (int i = 0; i < 200; i++) { Scenario(s, 40 + i * 0.01); m.Mix(buf, Block); }   // warm up
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long before = GC.GetAllocatedBytesForCurrentThread();
        int blocks = (int)(ScenarioSeconds * Sr / Block);
        for (int i = 0; i < blocks; i++)
        {
            Scenario(s, i * (double)Block / Sr);
            m.Mix(buf, Block);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        sw.Stop();
        double load = sw.Elapsed.TotalSeconds / ScenarioSeconds * 100;
        _out.WriteLine($"allocated {allocated} bytes; {load:F2}% of one core");
        Assert.Equal(0, allocated);
        Assert.True(load < 10, $"{load:F2}% of a core");
    }

    // ---- clicks ---------------------------------------------------------------------------

    [Fact]
    public void PhaseChanges_DoNotClick()
    {
        var s = new FlightSoundState();
        var buf = Render(new FlightMusic(s), s, ScenarioSeconds, t => Scenario(s, t));
        double steady = Math.Max(MaxDelta(buf, 55, 70), MaxDelta(buf, 8, 29));
        double[] changes = [FreeEnd, CountEnd, RaceEnd, FinishEnd];
        foreach (double c in changes)
        {
            double d = MaxDelta(buf, c - 0.05, c + 0.6);
            _out.WriteLine($"phase change at {c} s: max step {d:F4} (steady {steady:F4})");
            Assert.True(d <= steady * 1.25 + 1e-3, $"click at {c} s: {d:F4} vs steady {steady:F4}");
        }
        // Noise drums (hats, snare) have large sample-to-sample steps by nature; this only
        // guards against a broken render.
        Assert.True(steady < 0.25, $"steady max step {steady:F4}");
    }

    // ---- tempo ----------------------------------------------------------------------------

    [Fact]
    public void Race_KeepsASteadyTempo_WithABackbeat()
    {
        var s = new FlightSoundState { Active = 1f, RacePhase = 2, RaceProgress01 = 0.3f };
        var m = new FlightMusic(s);
        var notes = Trace(m);
        var buf = Render(m, s, 16);
        int beat = Sr * 60 / (int)FlightMusic.Bpm;
        var kicks = notes.Where(n => n.Kind == FlightMusic.NoteKind.Kick).Select(n => n.At).ToList();
        Assert.True(kicks.Count >= 30);
        for (int i = 1; i < kicks.Count; i++) Assert.Equal(beat, kicks[i] - kicks[i - 1]);
        // Snare / clap on beats 2 and 4 of every bar.
        long first = kicks[0];
        foreach (int bar in Enumerable.Range(0, 7))
            foreach (int b in new[] { 1, 3 })
                Assert.Contains(notes, n => n.Kind == FlightMusic.NoteKind.Snare && n.At == first + (bar * 4 + b) * (long)beat);

        // And from the audio: autocorrelation of the low-band energy envelope peaks at one beat.
        int hop = 240;  // 5 ms
        int nEnv = buf.Length / 2 / hop;
        var env = new double[nEnv];
        double lp = 0;
        for (int i = 0; i < nEnv * hop; i++)
        {
            lp += 0.02 * (0.5 * (buf[i * 2] + buf[i * 2 + 1]) - lp);   // ~150 Hz one-pole
            env[i / hop] += lp * lp;
        }
        var flux = new double[nEnv];
        for (int i = 1; i < nEnv; i++) flux[i] = Math.Max(0, env[i] - env[i - 1]);
        int bestLag = 0; double best = double.MinValue;
        for (int lag = 60; lag <= 160; lag++)   // 0.3 .. 0.8 s
        {
            double acc = 0;
            for (int i = lag; i < nEnv; i++) acc += flux[i] * flux[i - lag];
            if (acc > best) { best = acc; bestLag = lag; }
        }
        double period = bestLag * hop / (double)Sr;
        _out.WriteLine($"onset period {period:F3} s (expected {60 / FlightMusic.Bpm:F3})");
        Assert.InRange(period, 0.5 * 0.98, 0.5 * 1.02);
    }

    // ---- key --------------------------------------------------------------------------------

    [Fact]
    public void EveryNote_IsInAMinor_AndRaceChordsFollowTheLoop()
    {
        int[] natural = [9, 11, 0, 2, 4, 5, 7];
        int[] pentatonic = [9, 0, 2, 4, 7];
        var s = new FlightSoundState();
        var m = new FlightMusic(s);
        var notes = Trace(m);
        Render(m, s, ScenarioSeconds, t => Scenario(s, t));
        int pitched = 0;
        foreach (var n in notes)
        {
            if (n.Kind is FlightMusic.NoteKind.Chord or FlightMusic.NoteKind.Section || n.Value < 0) continue;
            pitched++;
            Assert.Contains(n.Value % 12, natural);
            if (n.Kind is FlightMusic.NoteKind.Bell or FlightMusic.NoteKind.Counter) Assert.Contains(n.Value % 12, pentatonic);
        }
        Assert.True(pitched > 800, $"{pitched} pitched notes");
        foreach (var k in Enum.GetValues<FlightMusic.NoteKind>()) Assert.Contains(notes, n => n.Kind == k);

        // The race starts on Am and its main sections change chord every bar: Am F C G.
        var raceChords = notes.Where(n => n.Kind == FlightMusic.NoteKind.Chord && n.At >= CountEnd * Sr && n.At < (CountEnd + 16) * Sr).Select(n => n.Value).ToList();
        Assert.Equal([0, 1, 2, 3, 0, 1, 2, 3], raceChords);
        for (int c = 0; c < FlightMusic.ChordPitchClasses.Length; c++)
            Assert.Equal(3, FlightMusic.ChordPitchClasses[c].Distinct().Count());
    }

    [Fact]
    public void RenderedAmChord_HasEnergyOnChordTonesNotOnNeighbours()
    {
        var s = new FlightSoundState { Active = 1f };
        var buf = Render(new FlightMusic(s), s, 4);
        // The first chord (bars 1-2) is Am: A3 C4 E4 against G#3, C#4, D#4, F4, F#4.
        double inKey = 0, outKey = 0;
        foreach (int n in new[] { 57, 60, 64 }) inKey += Goertzel(buf, 2.0, 3.95, AudioMath.MidiToHz(n));
        foreach (int n in new[] { 56, 61, 63, 65, 66 }) outKey += Goertzel(buf, 2.0, 3.95, AudioMath.MidiToHz(n));
        double ratio = 10 * Math.Log10(inKey / 3 / (outKey / 5));
        _out.WriteLine($"Am chord tones vs neighbours: {ratio:F1} dB");
        Assert.True(ratio > 20);
    }

    // ---- arrangement ------------------------------------------------------------------------

    [Fact]
    public void Race_BuildsInBlocks_DropMainBreakFinal()
    {
        var s = new FlightSoundState();
        var m = new FlightMusic(s);
        var notes = Trace(m);
        Render(m, s, ScenarioSeconds, t => Scenario(s, t));
        double bar = 2.0, t0 = CountEnd;
        var sections = notes.Where(n => n.Kind == FlightMusic.NoteKind.Section && n.At >= t0 * Sr - 100 && n.At < RaceEnd * Sr)
                            .Select(n => (n.Value, bar: Math.Round((n.At / (double)Sr - t0) / bar))).ToList();
        _out.WriteLine("race sections: " + string.Join(", ", sections.Select(x => $"{x.Value}@bar{x.bar}")));
        Assert.Equal([FlightMusic.SecDrop, FlightMusic.SecMain, FlightMusic.SecBreak, FlightMusic.SecFinal], sections.Select(x => x.Value).ToList());
        Assert.Equal([0.0, 4.0, 8.0, 12.0], sections.Select(x => x.bar).ToList());

        // Drums and bass from the first bar (the drop); the crash lands on the start.
        Assert.True(Count(notes, FlightMusic.NoteKind.Kick, t0, t0 + 0.01) == 1);
        Assert.True(Count(notes, FlightMusic.NoteKind.Crash, t0, t0 + 0.01) == 1);
        Assert.True(Count(notes, FlightMusic.NoteKind.Bass, t0, t0 + 0.01) == 1);
        // The arpeggio enters at bar 3, the lead at bar 5.
        Assert.Equal(0, Count(notes, FlightMusic.NoteKind.Arp, t0, t0 + 2 * bar));
        Assert.True(Count(notes, FlightMusic.NoteKind.Arp, t0 + 2 * bar, t0 + 4 * bar) >= 30);
        Assert.Equal(0, Count(notes, FlightMusic.NoteKind.Lead, t0, t0 + 4 * bar));
        Assert.True(Count(notes, FlightMusic.NoteKind.Lead, t0 + 4 * bar, t0 + 8 * bar) >= 16);
        // The break drops the kick for two bars and rolls the snare in the lift.
        Assert.Equal(0, Count(notes, FlightMusic.NoteKind.Kick, t0 + 8 * bar, t0 + 10 * bar));
        Assert.True(Count(notes, FlightMusic.NoteKind.Snare, t0 + 11 * bar, t0 + 12 * bar) >= 16);
        // The final: open hats, crash, and the hook back.
        Assert.True(Count(notes, FlightMusic.NoteKind.OpenHat, t0 + 12 * bar, t0 + 16 * bar) >= 16);
        Assert.True(Count(notes, FlightMusic.NoteKind.Lead, t0 + 12 * bar, t0 + 16 * bar) >= 16);
        // Every block ends with a fill (toms or a snare run) except the break.
        Assert.True(Count(notes, FlightMusic.NoteKind.Tom, t0, RaceEnd) >= 5);
    }

    [Fact]
    public void Race_NoFourBarBlockRepeatsTheOneBefore()
    {
        var s = new FlightSoundState { Active = 1f, RacePhase = 2 };
        var m = new FlightMusic(s);
        var notes = Trace(m);
        Render(m, s, 64, t => { s.RaceProgress01 = (float)(t / 64); s.Combo = (int)(t / 1.6) % 25; });
        var fp = BarFingerprints(notes, 0, 32);
        for (int block = 1; block < 8; block++)
        {
            bool same = true;
            for (int b = 0; b < 4; b++) same &= fp[block * 4 + b] == fp[(block - 1) * 4 + b];
            Assert.False(same, $"block {block} repeats block {block - 1}");
        }
        int distinct = fp.Distinct().Count();
        _out.WriteLine($"race: {distinct} distinct bars of 32");
        Assert.True(distinct >= 24);
    }

    [Fact]
    public void FreeFlight_Has64BarCycleOfSections()
    {
        var s = new FlightSoundState { Active = 1f };
        var m = new FlightMusic(s);
        var notes = Trace(m);
        Render(m, s, 130);
        var fp = BarFingerprints(notes, 0, 64);
        int distinct = fp.Distinct().Count();
        _out.WriteLine($"free flight: {distinct} distinct bars of 64; sections " +
                       string.Join(",", notes.Where(n => n.Kind == FlightMusic.NoteKind.Section).Select(n => n.Value)));
        Assert.True(distinct >= 40, $"{distinct} distinct bars");
        for (int block = 1; block < 16; block++)
        {
            bool same = true;
            for (int b = 0; b < 4; b++) same &= fp[block * 4 + b] == fp[(block - 1) * 4 + b];
            Assert.False(same, $"block {block} repeats block {block - 1}");
        }
        // Drums come and go: the intro and the breakdown have none, the others a groove.
        Assert.Equal(0, Count(notes, FlightMusic.NoteKind.Kick, 0, 16));
        Assert.True(Count(notes, FlightMusic.NoteKind.Kick, 16, 32) >= 14);
        Assert.Equal(0, Count(notes, FlightMusic.NoteKind.Kick, 64, 80));
        Assert.True(Count(notes, FlightMusic.NoteKind.Lead, 32, 48) > 0, "hook fragments in section 3");
    }

    [Fact]
    public void Combo_AddsLayers_AndAMissRemovesThem()
    {
        var s = new FlightSoundState { Active = 1f, RacePhase = 2, RaceProgress01 = 0.2f };
        var m = new FlightMusic(s);
        var notes = Trace(m);
        Render(m, s, 24, t => s.Combo = t < 8 ? 0 : t < 16 ? 12 : 0);
        int shakeOff = Count(notes, FlightMusic.NoteKind.Shaker, 1, 8);
        int shakeOn = Count(notes, FlightMusic.NoteKind.Shaker, 9, 16);
        int counterOn = Count(notes, FlightMusic.NoteKind.Counter, 9, 16);
        int afterMiss = Count(notes, FlightMusic.NoteKind.Shaker, 16.2, 24) + Count(notes, FlightMusic.NoteKind.Counter, 16.2, 24);
        _out.WriteLine($"shaker {shakeOff} -> {shakeOn}, counter {counterOn}, after the miss {afterMiss}");
        Assert.Equal(0, shakeOff);
        Assert.True(shakeOn >= 28 && counterOn >= 6);
        Assert.Equal(0, afterMiss);
        // Every fifth gate in a row crashes on the next downbeat.
        Assert.True(Count(notes, FlightMusic.NoteKind.Crash, 8, 10.01) >= 1);
    }

    [Fact]
    public void Countdown_SnareBuildAccelerates_AndLandsOnTheStart()
    {
        var s = new FlightSoundState { Active = 1f };
        var m = new FlightMusic(s);
        var notes = Trace(m);
        var buf = Render(m, s, 12, t =>
        {
            if (t < 4) s.RacePhase = 0;
            else if (t < 7) { s.RacePhase = 1; s.Countdown = (float)(7 - t); }
            else { s.RacePhase = 2; s.Countdown = 0f; }
        });
        int[] perSecond = [Count(notes, FlightMusic.NoteKind.Snare, 4, 5), Count(notes, FlightMusic.NoteKind.Snare, 5, 6),
                           Count(notes, FlightMusic.NoteKind.Snare, 6, 6.5), Count(notes, FlightMusic.NoteKind.Snare, 6.5, 7)];
        _out.WriteLine($"countdown snares: {string.Join(", ", perSecond)}; riser {Db(Measure(buf, 4.2, 4.8).rms):F1} -> {Db(Measure(buf, 6.4, 7).rms):F1} dBFS");
        Assert.True(perSecond[0] == 2 && perSecond[1] == 4 && perSecond[2] >= 4 && perSecond[3] >= 8);
        Assert.Equal(0, Count(notes, FlightMusic.NoteKind.Kick, 4, 7));
        Assert.True(Db(Measure(buf, 6.4, 7).rms) > Db(Measure(buf, 4.2, 4.8).rms) + 3, "the build must grow");
        // The race's first kick and crash are within one 10 ms block of the start.
        var kick = notes.First(n => n.Kind == FlightMusic.NoteKind.Kick && n.At >= 7 * Sr);
        Assert.InRange(kick.At, 7 * Sr, 7 * Sr + Block);
        Assert.Contains(notes, n => n.Kind == FlightMusic.NoteKind.Crash && n.At == kick.At);
    }

    [Theory]
    [InlineData(3, 12)]
    [InlineData(2, 4)]
    [InlineData(1, 6)]
    [InlineData(0, 0)]
    public void Finish_OutroScalesWithTheMedal(int medal, int minLeadNotes)
    {
        var s = new FlightSoundState { Active = 1f, RacePhase = 2, RaceProgress01 = 1f };
        var m = new FlightMusic(s);
        var notes = Trace(m);
        Render(m, s, 24, t => { if (t >= 9) { s.RacePhase = 3; s.Medal = medal; } });
        int lead = Count(notes, FlightMusic.NoteKind.Lead, 10, 24);
        int drums = Count(notes, FlightMusic.NoteKind.Kick, 10, 24) + Count(notes, FlightMusic.NoteKind.Snare, 10, 24);
        _out.WriteLine($"medal {medal}: {lead} lead notes, {drums} drum hits in the outro");
        Assert.True(lead >= minLeadNotes);
        if (medal == 0) Assert.Equal(0, lead);
        if (medal == 3) Assert.Contains(notes, n => n.Kind == FlightMusic.NoteKind.Lead && n.Value == 81 && n.At >= 10 * Sr);
        // The outro starts on the next bar line with a crash.
        Assert.Contains(notes, n => n.Kind == FlightMusic.NoteKind.Crash && n.At == 10 * Sr);
        Assert.True(Count(notes, FlightMusic.NoteKind.Kick, 19, 24) < 3, "it settles");
    }

    [Theory]
    [InlineData(3, 4, true)]     // a gate passed
    [InlineData(3, 5, true)]     // two gates in one frame: one duck
    [InlineData(3, 3, false)]    // nothing happened
    [InlineData(3, 0, false)]    // a miss resets the combo: no duck
    public void GatePass_FromAComboRise_DucksTheLeadBriefly(int before, int after, bool ducks)
    {
        var s = new FlightSoundState { Active = 1f, RacePhase = 2, RaceProgress01 = 0.3f, Combo = before };
        var m = new FlightMusic(s);
        double minEarly = 1, at05 = 1, late = 0;
        Render(m, s, 6, t =>
        {
            if (t >= 5) s.Combo = after;
            if (t >= 5.02 && t < 5.3) minEarly = Math.Min(minEarly, m.LeadDuck);
            if (Math.Abs(t - 5.5) < 0.005) at05 = m.LeadDuck;
            if (t >= 5.9) late = m.LeadDuck;
        });
        _out.WriteLine($"{before} -> {after}: duck min {minEarly:F2}, at +0.5 s {at05:F2}, at +0.9 s {late:F2}");
        if (ducks) { Assert.True(minEarly < 0.6, $"min {minEarly:F2}"); Assert.True(at05 > 0.9 && late > 0.98); }
        else Assert.True(minEarly > 0.99);
    }

    // ---- listening aid --------------------------------------------------------------------

    /// <summary>Writes the whole scenario to the WAV named by FLIGHT_MUSIC_WAV (skipped when
    /// the variable is not set) for offline inspection.</summary>
    [Fact]
    public void WriteScenarioWav_WhenRequested()
    {
        string? path = Environment.GetEnvironmentVariable("FLIGHT_MUSIC_WAV");
        if (string.IsNullOrEmpty(path)) return;
        var s = new FlightSoundState();
        var buf = Render(new FlightMusic(s), s, ScenarioSeconds, t => Scenario(s, t));
        WavFile.Write(path, buf);
    }
}
