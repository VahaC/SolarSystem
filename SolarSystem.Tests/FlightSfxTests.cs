// PROTOTYPE (2026-11-17): arcade flight mode — sound: tests for the one-shot effects.
using Xunit;

namespace SolarSystem.Tests;

/// <summary>
/// Renders every <see cref="FlightSfx"/> effect offline (no audio device) and checks the
/// numbers nobody can hear here: level window, bounded length, clean start and end, no DC,
/// no NaN, additive mixing, panning, the gate melody, the combo escalation, checkpoint and
/// split accents, gate-to-gate variety, medal / strength ordering and determinism.
/// </summary>
public class FlightSfxTests
{
    private const int Sr = AudioFormat.SampleRate;
    private const int Block = 480;

    /// <summary>A rendered effect.</summary>
    private sealed record Rendered(float[] Samples, int Frames, bool Ended, int BlocksAfterEnd)
    {
        public float Peak()
        {
            float p = 0f;
            foreach (float s in Samples) p = MathF.Max(p, MathF.Abs(s));
            return p;
        }

        public float PeakDb() => 20f * MathF.Log10(MathF.Max(Peak(), 1e-9f));

        public double Energy(int channel, int fromFrame = 0, int toFrame = int.MaxValue)
        {
            double e = 0;
            int end = Math.Min(Frames, toFrame);
            for (int i = fromFrame; i < end; i++) e += Samples[2 * i + channel] * (double)Samples[2 * i + channel];
            return e;
        }

        public float[] Mono()
        {
            var m = new float[Frames];
            for (int i = 0; i < Frames; i++) m[i] = Samples[2 * i] + Samples[2 * i + 1];
            return m;
        }
    }

    private static Rendered Render(IAudioSource src, float prefill = 0f, float maxSeconds = 6f)
    {
        int maxFrames = (int)(maxSeconds * Sr);
        var buf = new float[maxFrames * 2];
        if (prefill != 0f) Array.Fill(buf, prefill);
        int pos = 0;
        bool ended = false;
        while (pos + Block <= maxFrames)
        {
            bool more = src.Mix(buf.AsSpan(pos * 2, Block * 2), Block);
            pos += Block;
            if (!more) { ended = true; break; }
        }
        // Once over, it must stay over and add nothing.
        int after = 0;
        if (ended)
        {
            var probe = new float[Block * 2];
            for (int k = 0; k < 3; k++)
                if (!src.Mix(probe, Block) && probe.All(s => s == 0f)) after++;
        }
        Array.Resize(ref buf, pos * 2);
        return new Rendered(buf, pos, ended, after);
    }

    private static FlightSoundCue Gate(int index, int combo, float split = 0f, bool hasBest = false, float speed = 0.3f, float pan = 0f)
        => new()
        {
            Event = FlightSoundEvent.GatePassed, GateIndex = index, GateCount = 25, Combo = combo,
            Split = split, HasBest = hasBest, Speed01 = speed, Pan = pan,
        };

    private static FlightSoundCue Cue(FlightSoundEvent ev, float pan = 0f, float strength = 0f, float speed = 0.3f, int medal = 0, bool record = false, int count = 3)
        => new() { Event = ev, Pan = pan, Strength = strength, Speed01 = speed, Medal = medal, NewRecord = record, Count = count };

    private static Rendered Make(in FlightSoundCue cue)
    {
        var src = FlightSfx.Create(cue);
        Assert.NotNull(src);
        return Render(src!);
    }

    /// <summary>The cases of the level table, by name.</summary>
    private static FlightSoundCue CaseCue(string name, float pan = 0f)
    {
        FlightSoundCue c = name switch
        {
            "gate-first" => Gate(0, 1),
            "gate-mid-combo4" => Gate(7, 4),
            "gate-combo8-ahead" => Gate(12, 8, -1f, true),
            "gate-combo20-behind" => Gate(22, 20, 1f, true),
            "gate-checkpoint-combo15" => Gate(14, 15, -0.5f, true, 0.9f),
            "gate-last-combo25" => Gate(24, 25, speed: 1f),
            "miss" => Cue(FlightSoundEvent.GateMissed),
            "tick3" => Cue(FlightSoundEvent.CountdownTick, count: 3),
            "tick1" => Cue(FlightSoundEvent.CountdownTick, count: 1),
            "go" => Cue(FlightSoundEvent.Go),
            "finish-none" => Cue(FlightSoundEvent.Finish, medal: 0),
            "finish-bronze" => Cue(FlightSoundEvent.Finish, medal: 1),
            "finish-silver-record" => Cue(FlightSoundEvent.Finish, medal: 2, record: true),
            "finish-gold" => Cue(FlightSoundEvent.Finish, medal: 3),
            "finish-gold-record" => Cue(FlightSoundEvent.Finish, medal: 3, record: true),
            "boost" => Cue(FlightSoundEvent.BoostIgnite),
            "boost-empty" => Cue(FlightSoundEvent.BoostEmpty),
            "hit-soft" => Cue(FlightSoundEvent.RockHit, strength: 0f),
            "hit-mid" => Cue(FlightSoundEvent.RockHit, strength: 0.5f),
            "hit-hard" => Cue(FlightSoundEvent.RockHit, strength: 1f),
            "pass-close-fast" => Cue(FlightSoundEvent.RockPass, pan: 0.8f, strength: 1f, speed: 1f),
            "pass-far-slow" => Cue(FlightSoundEvent.RockPass, pan: -0.6f, strength: 0.3f, speed: 0.2f),
            "brake" => Cue(FlightSoundEvent.BrakeOn, speed: 0.6f),
            "enter" => Cue(FlightSoundEvent.EnterFlight),
            "exit" => Cue(FlightSoundEvent.ExitFlight),
            _ => throw new ArgumentException(name),
        };
        if (pan != 0f) c.Pan = pan;
        return c;
    }

    /// <summary>Case, lowest and highest allowed peak (dBFS, pan centre), longest allowed
    /// duration (s).</summary>
    public static TheoryData<string, float, float, float> Cases => new()
    {
        { "gate-first", -11f, -8f, 3f },
        { "gate-mid-combo4", -10.5f, -7.5f, 3f },
        { "gate-combo8-ahead", -10f, -6.5f, 3f },
        { "gate-combo20-behind", -9f, -6f, 3f },
        { "gate-checkpoint-combo15", -9f, -6f, 3.5f },
        { "gate-last-combo25", -9f, -6f, 3f },
        { "miss", -11.5f, -8f, 1.2f },
        { "tick3", -12f, -8.5f, 0.8f },
        { "tick1", -11f, -8f, 1.3f },
        { "go", -10.5f, -7.5f, 2.5f },
        { "finish-none", -15.5f, -11f, 4f },
        { "finish-bronze", -13.5f, -9f, 4f },
        { "finish-silver-record", -11.5f, -7.5f, 4.5f },
        { "finish-gold", -9f, -6f, 5f },
        { "finish-gold-record", -9f, -6f, 5f },
        { "boost", -11.5f, -8.5f, 1.2f },
        { "boost-empty", -12f, -8.5f, 1.2f },
        { "hit-soft", -24f, -13f, 1f },
        { "hit-mid", -16f, -10f, 1.3f },
        { "hit-hard", -11f, -8f, 1.6f },
        { "pass-close-fast", -16.5f, -14f, 0.6f },
        { "pass-far-slow", -22f, -15f, 0.7f },
        { "brake", -17f, -13f, 0.8f },
        { "enter", -12.5f, -8.5f, 2.6f },
        { "exit", -13f, -9f, 1.9f },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Effect_IsCleanBoundedAndInItsLevelWindow(string name, float minDb, float maxDb, float maxSeconds)
    {
        var r = Make(CaseCue(name));
        float[] s = r.Samples;

        Assert.All(s, x => Assert.True(float.IsFinite(x)));
        Assert.True(r.Ended, "never returned false");
        Assert.Equal(3, r.BlocksAfterEnd);
        Assert.True(r.Frames <= maxSeconds * Sr, $"lasted {r.Frames / (float)Sr:F2} s");

        float peakDb = r.PeakDb();
        Assert.InRange(peakDb, minDb, maxDb);

        // Clean start: the first sample is silent, the first millisecond is quiet.
        Assert.True(MathF.Abs(s[0]) < 1e-4f && MathF.Abs(s[1]) < 1e-4f);
        float firstMs = 0f;
        for (int i = 0; i < 96; i++) firstMs = MathF.Max(firstMs, MathF.Abs(s[i]));
        Assert.True(firstMs < 0.3f * r.Peak(), $"first ms peak {firstMs}");

        // Clean end: the last 20 ms are below about -66 dBFS (it returned false once silent).
        float tail = 0f;
        for (int i = s.Length - 2 * Sr / 50; i < s.Length; i++) tail = MathF.Max(tail, MathF.Abs(s[i]));
        Assert.True(tail < 5e-4f, $"tail peak {tail}");

        // No hard discontinuities anywhere.
        float maxDelta = 0f;
        for (int i = 2; i < s.Length; i++) maxDelta = MathF.Max(maxDelta, MathF.Abs(s[i] - s[i - 2]));
        Assert.True(maxDelta < 0.25f, $"max sample-to-sample jump {maxDelta}");

        // No DC offset.
        for (int c = 0; c < 2; c++)
        {
            double sum = 0;
            for (int i = c; i < s.Length; i += 2) sum += s[i];
            Assert.True(Math.Abs(sum / r.Frames) < 2e-4, $"DC {sum / r.Frames} on channel {c}");
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Effect_HardPannedNeverPeaksAboveMinus5Db(string name, float minDb, float maxDb, float maxSeconds)
    {
        _ = (minDb, maxDb, maxSeconds);
        Assert.True(Make(CaseCue(name, -1f)).PeakDb() <= -5f);
        Assert.True(Make(CaseCue(name, 1f)).PeakDb() <= -5f);
    }

    [Fact]
    public void Mix_AddsToTheBufferInsteadOfOverwriting()
    {
        var alone = Render(FlightSfx.Create(Gate(6, 12))!);
        var onTop = Render(FlightSfx.Create(Gate(6, 12))!, prefill: 0.25f);
        Assert.Equal(alone.Frames, onTop.Frames);
        for (int i = 0; i < alone.Samples.Length; i++)
            Assert.Equal(alone.Samples[i] + 0.25f, onTop.Samples[i], 1e-5f);
    }

    [Theory]
    [InlineData(FlightSoundEvent.GatePassed)]
    [InlineData(FlightSoundEvent.RockHit)]
    [InlineData(FlightSoundEvent.BoostIgnite)]
    [InlineData(FlightSoundEvent.CountdownTick)]
    [InlineData(FlightSoundEvent.GateMissed)]
    public void Pan_PutsTheEnergyOnTheRightSide(FlightSoundEvent ev)
    {
        FlightSoundCue At(float pan) => ev == FlightSoundEvent.GatePassed ? Gate(9, 12, pan: pan, speed: 1f) : Cue(ev, pan, strength: 0.5f);
        var left = Make(At(-1f));
        var right = Make(At(1f));
        var centre = Make(At(0f));
        Assert.True(left.Energy(0) > 4 * left.Energy(1));
        Assert.True(right.Energy(1) > 4 * right.Energy(0));
        double ratio = centre.Energy(0) / centre.Energy(1);
        Assert.InRange(ratio, 0.75, 1.33);
    }

    // ---- the gate ----------------------------------------------------------------------

    [Fact]
    public void GateMelody_IsPentatonic_NeverSameNoteThreeTimes_AndClimbsToA6()
    {
        int[] pentatonic = [0, 3, 5, 7, 10];
        var notes = Enumerable.Range(0, 25).Select(g => FlightSfx.GateNote(g, 25)).ToArray();
        foreach (int n in notes)
        {
            Assert.Contains((n - 69 + 120) % 12, pentatonic);
            Assert.InRange(n, 69, 93);
        }
        for (int g = 2; g < 25; g++)
            Assert.False(notes[g] == notes[g - 1] && notes[g] == notes[g - 2], $"gate {g}: three in a row");
        int repeats = Enumerable.Range(1, 24).Count(g => notes[g] == notes[g - 1]);
        Assert.True(repeats <= 2, $"{repeats} repeated notes");
        // Leaps: at least four intervals of a fourth or more.
        Assert.True(Enumerable.Range(1, 24).Count(g => Math.Abs(notes[g] - notes[g - 1]) >= 5) >= 4);
        // It climbs: each phrase of five reaches higher than the last; the end is the top A6.
        for (int p = 1; p < 5; p++)
            Assert.True(notes.Skip(5 * p).Take(5).Max() > notes.Skip(5 * (p - 1)).Take(5).Max());
        Assert.Equal(69, notes[0]);
        Assert.Equal(93, notes[24]);
        // Other gate counts map onto the same phrase (first A4, last A6).
        Assert.Equal(69, FlightSfx.GateNote(0, 12));
        Assert.Equal(93, FlightSfx.GateNote(11, 12));
    }

    [Fact]
    public void GateHit_TheLoudestNoteIsTheMelodyNote()
    {
        for (int g = 0; g < 25; g++)
        {
            float[] mono = Make(Gate(g, 1)).Mono();
            int loudest = LoudestSemitone(mono, 0.05f, 0.45f, 60, 96), note = FlightSfx.GateNote(g, 25);
            // A checkpoint's stinger doubles the note an octave up: the same note name.
            if (FlightSfx.IsCheckpoint(g, 25)) Assert.Equal(note % 12, loudest % 12);
            else Assert.Equal(note, loudest);
        }
    }

    [Fact]
    public void GateHit_EscalatesWithTheCombo_AndResetsAfterAMiss()
    {
        Assert.Equal([0, 0, 1, 1, 2, 2, 2, 2, 2, 3, 3], Enumerable.Range(1, 11).Select(FlightSfx.ComboTier).ToArray());
        // The same gate with a growing streak: more energy each tier; harmony adds partials
        // (tier 1), the saws and sparkle add brightness (tier 2), the chord stab and shimmer
        // add more partials again (tier 3).
        double[] energy = new double[4], bright = new double[4];
        int[] partials = new int[4];
        int[] combos = [1, 3, 6, 12];
        for (int t = 0; t < 4; t++)
        {
            var r = Make(Gate(7, combos[t]));
            float[] m = r.Mono();
            energy[t] = r.Energy(0) + r.Energy(1);
            partials[t] = CountTonalPeaks(m, 0.08f, 0.4f);
            float[] w = m[(Sr / 20)..(Sr * 4 / 10)];
            bright[t] = HighShare(w, 1500) * w.Sum(x => x * (double)x);    // energy above 1.5 kHz
        }
        for (int t = 1; t < 4; t++)
            Assert.True(energy[t] > 1.15 * energy[t - 1], $"tier {t}: energy {energy[t]:F1} vs {energy[t - 1]:F1}");
        Assert.True(partials[1] > partials[0], $"tier 1: {partials[1]} partials vs {partials[0]}");
        Assert.True(bright[2] > 1.3 * bright[1], $"tier 2: energy above 1.5 kHz {bright[2]:F2} vs {bright[1]:F2}");
        Assert.True(partials[3] > partials[2], $"tier 3: {partials[3]} partials vs {partials[2]}");
        // After a miss the combo restarts at 1: the hit is plain again (same as a fresh start).
        Assert.Equal(Make(Gate(13, 1)).Samples, Make(Gate(13, 1)).Samples);
        Assert.True(Make(Gate(13, 14)).Energy(0) > 2 * Make(Gate(13, 1)).Energy(0));
    }

    [Fact]
    public void GateHit_CheckpointsAreBiggerStingers()
    {
        foreach (int g in (int[])[4, 9, 14, 19])
        {
            Assert.True(FlightSfx.IsCheckpoint(g, 25));
            var cp = Make(Gate(g, 2));
            var before = Make(Gate(g - 1, 2));
            // The stinger rings on after the pass: compare from 100 ms on.
            Assert.True(cp.Energy(0, Sr / 10) > 1.3 * before.Energy(0, Sr / 10), $"gate {g}: energy {cp.Energy(0, Sr / 10):F1} vs {before.Energy(0, Sr / 10):F1}");
            Assert.True(cp.Frames > before.Frames, $"gate {g}: length");
        }
        Assert.False(FlightSfx.IsCheckpoint(24, 25));
        Assert.False(FlightSfx.IsCheckpoint(3, 25));
    }

    [Fact]
    public void GateHit_AheadIsBrighterAndAddsAFlourish_BehindIsDarker()
    {
        float[] ahead = Make(Gate(8, 2, -1.5f, true)).Mono()[..(Sr * 4 / 10)];
        float[] none = Make(Gate(8, 2)).Mono()[..(Sr * 4 / 10)];
        float[] behind = Make(Gate(8, 2, 1.5f, true)).Mono()[..(Sr * 4 / 10)];
        // The flourish: the three pentatonic notes above the octave, clearly there when ahead.
        int note = FlightSfx.GateNote(8, 25);
        double a = 0, n = 0;
        foreach (int m in PentaAbove(note + 12, 3))
        {
            a += Goertzel(ahead, 0, ahead.Length, AudioMath.MidiToHz(m));
            n += Goertzel(none, 0, none.Length, AudioMath.MidiToHz(m));
        }
        Assert.True(a > 5 * n, $"flourish {a:E2} vs {n:E2}");
        double hb = HarmonicShare(behind, FlightSfx.GateNote(8, 25)), hn = HarmonicShare(none, FlightSfx.GateNote(8, 25));
        Assert.True(hb < 0.85 * hn, $"behind {hb:F3} vs neutral {hn:F3}");
    }

    [Fact]
    public void GateHit_ConsecutiveGatesAreNeverIdentical_AndFasterIsBrighterAndWider()
    {
        // Same note, same combo tier, neighbouring gates: still not the same sound.
        for (int g = 1; g < 25; g++)
        {
            var a = Make(Gate(g - 1, 12)).Mono();
            var b = Make(Gate(g, 12)).Mono();
            Assert.True(BandCorrelation(a, b, 0.6f) < 0.995, $"gates {g - 1} and {g}");
        }
        var slow = Make(Gate(5, 1, speed: 0f));
        var fast = Make(Gate(5, 1, speed: 1f));
        float[] slowM = slow.Mono()[..(Sr / 5)], fastM = fast.Mono()[..(Sr / 5)];
        Assert.True(HighShare(fastM, 3000) > 1.3 * HighShare(slowM, 3000), $"{HighShare(fastM, 3000):F3} vs {HighShare(slowM, 3000):F3}");
        Assert.True(SideShare(fast, Sr / 3) > 1.3 * SideShare(slow, Sr / 3), $"{SideShare(fast, Sr / 3):F3} vs {SideShare(slow, Sr / 3):F3}");
    }

    [Fact]
    public void GateHit_HasATransientAndWideBandEnergy()
    {
        // The physical pass: real energy above 3 kHz in the first 150 ms and a punchy start.
        var r = Make(Gate(3, 1));
        float[] m = r.Mono();
        Assert.True(HighShare(m[..(Sr * 15 / 100)], 3000) > 0.05, $"{HighShare(m[..(Sr * 15 / 100)], 3000):F3}");
        float early = 0f;
        for (int i = 0; i < Sr / 25; i++) early = MathF.Max(early, MathF.Abs(m[i]));
        float all = m.Max(MathF.Abs);
        Assert.True(early > 0.6f * all, "the loudest moment is in the first 40 ms");
    }

    // ---- the other events --------------------------------------------------------------

    [Fact]
    public void Countdown_EachTickIsMoreTense_AndOneHasARiser()
    {
        var t3 = Make(Cue(FlightSoundEvent.CountdownTick, count: 3));
        var t2 = Make(Cue(FlightSoundEvent.CountdownTick, count: 2));
        var t1 = Make(Cue(FlightSoundEvent.CountdownTick, count: 1));
        Assert.True(t2.Peak() > t3.Peak() && t1.Peak() > t2.Peak());
        Assert.True(HighShare(t2.Mono()[..(Sr / 10)], 2000) > HighShare(t3.Mono()[..(Sr / 10)], 2000));
        Assert.True(HighShare(t1.Mono()[..(Sr / 10)], 2000) > HighShare(t2.Mono()[..(Sr / 10)], 2000));
        // "1" swells toward Go: much more energy between 0.4 and 0.9 s.
        Assert.True(t1.Energy(0, Sr * 4 / 10, Sr * 9 / 10) > 20 * t3.Energy(0, Sr * 4 / 10, Sr * 9 / 10));
        // A sub hit under each beep: a real share of the first 100 ms is below 150 Hz.
        Assert.True(1 - HighShare(t3.Mono()[..(Sr / 10)], 150) > 0.15, $"{1 - HighShare(t3.Mono()[..(Sr / 10)], 150):F3}");
    }

    [Fact]
    public void Go_IsAnOctaveAboveTheTick_AndHitsHarder()
    {
        int tick = LoudestSemitone(Make(Cue(FlightSoundEvent.CountdownTick, count: 3)).Mono(), 0.005f, 0.08f, 64, 100);
        var go = Make(Cue(FlightSoundEvent.Go));
        Assert.Equal(tick + 12, LoudestSemitone(go.Mono(), 0.05f, 0.35f, 64, 100));
        Assert.True(go.Energy(0) > 1.5 * Make(Cue(FlightSoundEvent.CountdownTick, count: 3)).Energy(0));
    }

    [Fact]
    public void Finish_ScalesWithTheMedal_AndARecordAddsMore()
    {
        var none = Make(Cue(FlightSoundEvent.Finish, medal: 0));
        var bronze = Make(Cue(FlightSoundEvent.Finish, medal: 1));
        var silver = Make(Cue(FlightSoundEvent.Finish, medal: 2));
        var gold = Make(Cue(FlightSoundEvent.Finish, medal: 3));
        var goldRecord = Make(Cue(FlightSoundEvent.Finish, medal: 3, record: true));
        Assert.True(bronze.Energy(0) > none.Energy(0));
        Assert.True(silver.Energy(0) > bronze.Energy(0));
        Assert.True(gold.Energy(0) > 1.5 * silver.Energy(0), $"{gold.Energy(0)} vs {silver.Energy(0)}");
        Assert.True(gold.Frames > none.Frames);
        Assert.True(gold.PeakDb() > none.PeakDb() + 2f);
        Assert.True(HighShare(goldRecord.Mono(), 3000) > HighShare(gold.Mono(), 3000));
        // Gold rings on: two seconds in, the tail is within 20 dB of the first second.
        double tail = gold.Energy(0, 2 * Sr, 3 * Sr) / gold.Energy(0, 0, Sr);
        Assert.True(tail > 0.01, $"tail {tail:F4}");
    }

    [Fact]
    public void GateMissed_FallsAndBuzzes()
    {
        float[] m = Make(Cue(FlightSoundEvent.GateMissed)).Mono();
        // Power loss: the buzz's pitch falls by more than an octave within half a second.
        double early = PeakHz(m, Sr * 8 / 100, Sr * 18 / 100, 120, 600), late = PeakHz(m, Sr * 40 / 100, Sr * 55 / 100, 40, 600);
        Assert.True(late < 0.5 * early, $"{early:F0} Hz -> {late:F0} Hz");
        // Glitch: strong energy fluctuation (chopping) in the first 250 ms.
        Assert.True(EnvelopeFlutter(m, 0, Sr / 4) > 0.15, $"{EnvelopeFlutter(m, 0, Sr / 4):F3}");
    }

    [Fact]
    public void RockPass_IsShortQuietFallingAndEndsOnItsSide()
    {
        foreach (float side in (float[])[-0.8f, 0.8f])
        {
            var r = Make(Cue(FlightSoundEvent.RockPass, pan: side, strength: 1f, speed: 0.5f));
            Assert.True(r.Frames < 0.5f * Sr, $"{r.Frames / (float)Sr:F2} s");
            Assert.True(r.PeakDb() < Make(Gate(5, 1)).PeakDb() - 4f);
            // Starts near the front, ends on its side.
            int ch = side < 0 ? 0 : 1, other = 1 - ch;
            int half = r.Frames / 2;
            double startRatio = r.Energy(ch, 0, Sr / 20) / Math.Max(r.Energy(other, 0, Sr / 20), 1e-12);
            double endRatio = r.Energy(ch, half) / Math.Max(r.Energy(other, half), 1e-12);
            Assert.True(endRatio > 3 && endRatio > 2 * startRatio, $"start {startRatio:F2} end {endRatio:F2}");
            // The noise band falls as it passes.
            float[] m = r.Mono();
            Assert.True(Centroid(m, 0, half) > 1.4 * Centroid(m, half, r.Frames));
        }
        // Closer = louder.
        Assert.True(Make(Cue(FlightSoundEvent.RockPass, 0.7f, 1f)).Peak() > 1.5f * Make(Cue(FlightSoundEvent.RockPass, 0.7f, 0.1f)).Peak());
    }

    [Fact]
    public void RockHit_StrongerIsLouderLongerAndCrunchier()
    {
        var soft = Make(Cue(FlightSoundEvent.RockHit, strength: 0.1f));
        var mid = Make(Cue(FlightSoundEvent.RockHit, strength: 0.5f));
        var hard = Make(Cue(FlightSoundEvent.RockHit, strength: 1f));
        Assert.True(mid.Peak() > soft.Peak() && hard.Peak() > mid.Peak());
        Assert.True(hard.Energy(0) > 3 * soft.Energy(0));
        Assert.True(hard.Frames > soft.Frames);
    }

    [Fact]
    public void BoostIgnite_HasASubPunchAndARisingWhine()
    {
        float[] m = Make(Cue(FlightSoundEvent.BoostIgnite)).Mono();
        Assert.True(Goertzel(m, 0, Sr / 10, 60) > 0.1 * Goertzel(m, 0, Sr / 10, 1000));
        // The turbine whine spools up by about two octaves.
        double early = PeakHz(m, Sr * 5 / 100, Sr * 15 / 100, 500, 3000), late = PeakHz(m, Sr * 40 / 100, Sr * 52 / 100, 500, 3000);
        Assert.True(late > 1.8 * early, $"{early:F0} Hz -> {late:F0} Hz");
    }

    [Fact]
    public void SameCue_GivesIdenticalSamples()
    {
        foreach (FlightSoundEvent ev in Enum.GetValues<FlightSoundEvent>())
        {
            var cue = new FlightSoundCue
            {
                Event = ev, Pan = 0.3f, Strength = 0.7f, Speed01 = 0.6f, GateIndex = 9, GateCount = 25, Combo = 11,
                Split = -0.5f, HasBest = true, Medal = 2, NewRecord = true, Count = 2,
            };
            var a = Make(cue);
            var b = Make(cue);
            Assert.Equal(a.Samples, b.Samples);
        }
    }

    [Fact]
    public void Overlapping_GatesHitsAndPassesStayFiniteAndUnderFullScale()
    {
        var sources = new List<IAudioSource>
        {
            FlightSfx.Create(Gate(23, 23, -2f, true, 1f, -0.3f))!,
            FlightSfx.Create(Gate(19, 19, -2f, true, 1f, 0.3f))!,
            FlightSfx.Create(Cue(FlightSoundEvent.RockHit, strength: 1f))!,
            FlightSfx.Create(Cue(FlightSoundEvent.RockPass, 0.9f, 1f, 1f))!,
            FlightSfx.Create(Cue(FlightSoundEvent.RockPass, -0.9f, 1f, 1f))!,
            FlightSfx.Create(Cue(FlightSoundEvent.BoostIgnite))!,
        };
        // Started 30 ms apart, as they would be in a busy moment of the race (sample-exact
        // pile-ups are the mixer's limiter's job).
        var buf = new float[Block * 2];
        float peak = 0f;
        var playing = new List<IAudioSource>();
        for (int b = 0; b < 600 && (sources.Count > 0 || playing.Count > 0); b++)
        {
            if (b % 3 == 0 && sources.Count > 0) { playing.Add(sources[0]); sources.RemoveAt(0); }
            Array.Clear(buf);
            playing.RemoveAll(s => !s.Mix(buf, Block));
            foreach (float x in buf) { Assert.True(float.IsFinite(x)); peak = MathF.Max(peak, MathF.Abs(x)); }
        }
        Assert.Empty(playing);
        Assert.True(peak < 1f, $"peak {peak}");
    }

    [Fact]
    public void Garbage_FieldsStillGiveACleanSound()
    {
        var bad = new FlightSoundCue
        {
            Event = FlightSoundEvent.RockHit, Strength = float.NaN, Pan = float.NaN, Speed01 = float.PositiveInfinity,
        };
        var r = Make(bad);
        Assert.True(r.Ended);
        Assert.All(r.Samples, x => Assert.True(float.IsFinite(x)));
        bad.Event = FlightSoundEvent.RockPass;
        Assert.True(Make(bad).Ended);
        Assert.True(Make(new FlightSoundCue
        {
            Event = FlightSoundEvent.GatePassed, GateIndex = 99, GateCount = -4, Combo = -7, Split = float.NaN, HasBest = true, Pan = 5f,
        }).Ended);
        Assert.True(Make(Cue(FlightSoundEvent.Finish, medal: -3)).Ended);
        Assert.True(Make(Cue(FlightSoundEvent.Finish, medal: 9, record: true)).Ended);
        Assert.True(Make(Cue(FlightSoundEvent.CountdownTick, count: 0)).Ended);
        Assert.Null(FlightSfx.Create(new FlightSoundCue { Event = (FlightSoundEvent)99 }));
    }

    // ---- analysis helpers ---------------------------------------------------------------

    /// <summary>The MIDI note (<paramref name="lo"/> .. <paramref name="hi"/>) with the most
    /// Goertzel energy in the window.</summary>
    private static int LoudestSemitone(float[] mono, float fromSec, float toSec, int lo, int hi)
    {
        int a = (int)(fromSec * Sr), b = Math.Min(mono.Length, (int)(toSec * Sr));
        int best = 0;
        double bestPower = -1;
        for (int note = lo; note <= hi; note++)
        {
            double p = Goertzel(mono, a, b, AudioMath.MidiToHz(note));
            if (p > bestPower) { bestPower = p; best = note; }
        }
        return best;
    }

    /// <summary>The <paramref name="count"/> A minor pentatonic notes above <paramref name="midi"/>.</summary>
    private static IEnumerable<int> PentaAbove(int midi, int count)
    {
        int[] pentatonic = [0, 3, 5, 7, 10];
        int m = midi;
        for (int i = 0; i < count; i++)
        {
            do m++; while (!pentatonic.Contains((m - 69 + 120) % 12));
            yield return m;
        }
    }

    /// <summary>The frequency (5 Hz steps) with the most energy in the window.</summary>
    private static double PeakHz(float[] mono, int a, int b, double lo, double hi)
    {
        double best = lo, bestP = -1;
        for (double hz = lo; hz <= hi; hz += 5)
        {
            double p = Goertzel(mono, a, b, hz);
            if (p > bestP) { bestP = p; best = hz; }
        }
        return best;
    }

    /// <summary>Semitones 45 .. 108 whose energy in the window is within 30 dB of the
    /// loudest one and above both neighbours (distinct tonal partials).</summary>
    private static int CountTonalPeaks(float[] mono, float fromSec, float toSec)
    {
        int a = (int)(fromSec * Sr), b = Math.Min(mono.Length, (int)(toSec * Sr));
        var p = new double[110];
        for (int n = 44; n <= 109; n++) p[n] = Goertzel(mono, a, b, AudioMath.MidiToHz(n));
        double max = p.Max();
        int count = 0;
        for (int n = 45; n <= 108; n++)
            if (p[n] > max * 1e-3 && p[n] > p[n - 1] && p[n] > p[n + 1]) count++;
        return count;
    }

    private static double Goertzel(float[] x, int a, int b, double hz)
    {
        double w = 2 * Math.PI * hz / Sr, c = 2 * Math.Cos(w);
        double s1 = 0, s2 = 0;
        int n = b - a;
        for (int i = 0; i < n; i++)
        {
            double win = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n);   // Hann
            double s0 = x[a + i] * win + c * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        return s1 * s1 + s2 * s2 - c * s1 * s2;
    }

    /// <summary>Share of the note's first eight harmonics' energy above the fundamental.</summary>
    private static double HarmonicShare(float[] mono, int midi)
    {
        double f = AudioMath.MidiToHz(midi), fund = Goertzel(mono, 0, mono.Length, f), up = 0;
        for (int k = 2; k <= 8; k++) up += Goertzel(mono, 0, mono.Length, f * k);
        return up / (up + fund);
    }

    /// <summary>Share of the energy above about <paramref name="cutHz"/> (one-pole split).</summary>
    private static double HighShare(float[] mono, double cutHz)
    {
        double k = Math.Exp(-2 * Math.PI * cutHz / Sr);
        double lp = 0, hiE = 0, allE = 0;
        foreach (float x in mono)
        {
            lp = (1 - k) * x + k * lp;
            double hi = x - lp;
            hiE += hi * hi;
            allE += x * (double)x;
        }
        return hiE / Math.Max(allE, 1e-12);
    }

    /// <summary>Side (L − R) energy over mid (L + R) energy in the first frames.</summary>
    private static double SideShare(Rendered r, int frames)
    {
        double side = 0, mid = 0;
        for (int i = 0; i < Math.Min(frames, r.Frames); i++)
        {
            double l = r.Samples[2 * i], rr = r.Samples[2 * i + 1];
            side += (l - rr) * (l - rr);
            mid += (l + rr) * (l + rr);
        }
        return side / Math.Max(mid, 1e-12);
    }

    /// <summary>Spectral centroid (Hz) of the window, from 24 log-spaced Goertzel probes.</summary>
    private static double Centroid(float[] mono, int a, int b)
    {
        double num = 0, den = 0;
        for (int i = 0; i < 24; i++)
        {
            double hz = 60 * Math.Pow(2, i / 3.0);
            double p = Goertzel(mono, a, b, hz) * hz;           // per-octave weighting
            num += p * hz;
            den += p;
        }
        return num / Math.Max(den, 1e-12);
    }

    /// <summary>Standard deviation over mean of the 5 ms RMS envelope in the window.</summary>
    private static double EnvelopeFlutter(float[] mono, int a, int b)
    {
        int fr = Sr / 200;
        var env = new List<double>();
        for (int i = a; i + fr <= b; i += fr)
        {
            double e = 0;
            for (int k = 0; k < fr; k++) e += mono[i + k] * (double)mono[i + k];
            env.Add(Math.Sqrt(e / fr));
        }
        // Detrend with a 40 ms moving average so the overall decay does not count.
        double sum = 0, n = 0;
        for (int i = 4; i < env.Count - 4; i++)
        {
            double avg = 0;
            for (int k = -4; k <= 4; k++) avg += env[i + k];
            avg /= 9;
            sum += Math.Abs(env[i] - avg) / Math.Max(avg, 1e-9);
            n++;
        }
        return sum / Math.Max(n, 1);
    }

    /// <summary>Correlation of the third-octave band levels (dB) of two sounds' first
    /// <paramref name="seconds"/>.</summary>
    private static double BandCorrelation(float[] a, float[] b, float seconds)
    {
        int n = Math.Min((int)(seconds * Sr), Math.Min(a.Length, b.Length));
        double[] la = Bands(a, n), lb = Bands(b, n);
        double ma = la.Average(), mb = lb.Average(), sab = 0, saa = 0, sbb = 0;
        for (int i = 0; i < la.Length; i++)
        {
            sab += (la[i] - ma) * (lb[i] - mb);
            saa += (la[i] - ma) * (la[i] - ma);
            sbb += (lb[i] - mb) * (lb[i] - mb);
        }
        return sab / Math.Sqrt(saa * sbb);

        static double[] Bands(float[] x, int n)
        {
            var r = new double[27];
            for (int i = 0; i < r.Length; i++) r[i] = 10 * Math.Log10(Goertzel(x, 0, n, 63 * Math.Pow(2, i / 3.0)) + 1e-12);
            return r;
        }
    }

    // ---- listening material (only when SFX_WAV_DIR is set) ------------------------------

    /// <summary>Writes every case and a "race reel" as WAV files into the folder named by
    /// the SFX_WAV_DIR environment variable; does nothing without it.</summary>
    [Fact]
    public void WriteListeningWavs_WhenRequested()
    {
        string? dir = Environment.GetEnvironmentVariable("SFX_WAV_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var lines = new List<string>();
        foreach (var row in Cases)
        {
            string name = (string)row[0];
            var r = Make(CaseCue(name));
            WavFile.Write(Path.Combine(dir, name + ".wav"), r.Samples);
            float hard = MathF.Max(Make(CaseCue(name, -1f)).PeakDb(), Make(CaseCue(name, 1f)).PeakDb());
            lines.Add($"{name}\t{r.PeakDb():F2} dBFS\thard-panned {hard:F2}\t{r.Frames / (float)Sr:F3} s");
        }
        File.WriteAllLines(Path.Combine(dir, "levels.txt"), lines);

        // Race reel: enter, countdown, go, 25 gates 1.4 s apart (gate 12 missed: the combo
        // resets), near misses on both sides, two rock hits, boosts, a brake, gold + record, exit.
        var events = new List<(float T, FlightSoundCue Cue)>
        {
            (0.2f, Cue(FlightSoundEvent.EnterFlight)),
            (2.0f, Cue(FlightSoundEvent.CountdownTick, count: 3)),
            (3.0f, Cue(FlightSoundEvent.CountdownTick, count: 2)),
            (4.0f, Cue(FlightSoundEvent.CountdownTick, count: 1)),
            (5.0f, Cue(FlightSoundEvent.Go)),
            (5.6f, Cue(FlightSoundEvent.BoostIgnite)),
            (9.3f, Cue(FlightSoundEvent.BoostEmpty)),
            (10.2f, Cue(FlightSoundEvent.BrakeOn, speed: 0.5f)),
        };
        int combo = 0;
        for (int g = 0; g < 25; g++)
        {
            float t = 6.4f + 1.4f * g;
            float pan = MathF.Sin(g * 1.3f) * 0.4f;
            float speed = g is >= 14 and <= 18 ? 0.95f : 0.3f;
            if (g == 12) { combo = 0; events.Add((t, new FlightSoundCue { Event = FlightSoundEvent.GateMissed, GateIndex = g, GateCount = 25, Pan = 0.4f })); }
            else events.Add((t, Gate(g, ++combo, g < 12 ? -0.4f : 0.6f, true, speed, pan)));
        }
        float[] passes = [8.1f, 12.3f, 16.7f, 19.0f, 22.4f, 27.9f, 29.2f, 33.6f, 36.9f];
        for (int i = 0; i < passes.Length; i++)
            events.Add((passes[i], Cue(FlightSoundEvent.RockPass, i % 2 == 0 ? -0.8f : 0.7f, 0.4f + 0.07f * i, 0.3f + 0.07f * i)));
        events.Add((14.1f, Cue(FlightSoundEvent.RockHit, -0.6f, 0.15f)));
        events.Add((23.8f, Cue(FlightSoundEvent.RockHit, 0.5f, 1f)));
        events.Add((25.9f, Cue(FlightSoundEvent.BoostIgnite)));
        events.Add((31.5f, Cue(FlightSoundEvent.RockHit, 0f, 0.5f)));
        events.Add((41.2f, Cue(FlightSoundEvent.Finish, medal: 3, record: true)));
        events.Add((46.5f, Cue(FlightSoundEvent.ExitFlight)));

        int total = 49 * Sr;
        var reel = new float[total * 2];
        var active = new List<IAudioSource>();
        events.Sort((x, y) => x.T.CompareTo(y.T));
        int next = 0;
        for (int pos = 0; pos + Block <= total; pos += Block)
        {
            while (next < events.Count && events[next].T * Sr <= pos)
                active.Add(FlightSfx.Create(events[next++].Cue)!);
            int at = pos * 2;
            active.RemoveAll(s => !s.Mix(reel.AsSpan(at, Block * 2), Block));
        }
        WavFile.Write(Path.Combine(dir, "race_reel.wav"), reel);
    }
}
