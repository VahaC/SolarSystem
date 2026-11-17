// PROTOTYPE (2026-11-17): arcade flight mode — sound: tests for the mixer, the WAV writer and the DSP helpers.
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace SolarSystem.Tests;

/// <summary>
/// The mixer (offline only, no audio device), the WAV writer and the shared DSP building
/// blocks. One test opens the real device, and only when SOLARSYSTEM_AUDIO_DEVICE_TEST=1;
/// it never plays anything but digital silence.
/// </summary>
public class AudioEngineTests(ITestOutputHelper log)
{
    private const int Sr = AudioFormat.SampleRate;

    // ---------- test sources ----------

    private sealed class ConstSource(float value) : IAudioSource
    {
        public int Calls;
        public bool Mix(Span<float> buffer, int frames)
        {
            Calls++;
            for (int i = 0; i < frames * 2; i++) buffer[i] += value;
            return true;
        }
    }

    private sealed class SineSource(float hz, float amplitude) : IAudioSource
    {
        private long _n;
        public bool Mix(Span<float> buffer, int frames)
        {
            for (int i = 0; i < frames; i++, _n++)
            {
                float v = amplitude * (float)Math.Sin(2 * Math.PI * hz * _n / Sr);
                buffer[2 * i] += v;
                buffer[2 * i + 1] += v;
            }
            return true;
        }
    }

    private sealed class OneShot(int lengthFrames, float value = 0.1f) : IAudioSource
    {
        private int _left = lengthFrames;
        public int Calls;
        public bool Mix(Span<float> buffer, int frames)
        {
            Calls++;
            int n = Math.Min(frames, _left);
            for (int i = 0; i < n * 2; i++) buffer[i] += value;
            _left -= n;
            return _left > 0;
        }
    }

    private sealed class ThrowingSource : IAudioSource
    {
        public bool Mix(Span<float> buffer, int frames) => throw new InvalidOperationException("boom");
    }

    private sealed class NaNSource(float bad) : IAudioSource
    {
        public bool Mix(Span<float> buffer, int frames)
        {
            buffer[frames] += bad;
            return true;
        }
    }

    private static float[] Render(AudioEngine e, int frames)
    {
        var buf = new float[frames * 2];
        e.Render(buf, frames);
        return buf;
    }

    private static float MaxAbs(ReadOnlySpan<float> x)
    {
        float m = 0;
        foreach (float v in x) m = MathF.Max(m, MathF.Abs(v));
        return m;
    }

    private static float MaxStep(ReadOnlySpan<float> stereo)
    {
        float m = 0;
        for (int i = 2; i < stereo.Length; i++) m = MathF.Max(m, MathF.Abs(stereo[i] - stereo[i - 2]));
        return m;
    }

    // ---------- mixer ----------

    [Fact]
    public void Silence_In_Silence_Out()
    {
        using var e = new AudioEngine();
        Assert.All(Render(e, 1000), v => Assert.Equal(0f, v));
        e.AddSource(new ConstSource(0f));
        Assert.All(Render(e, 1000), v => Assert.Equal(0f, v));
        Assert.Equal(0f, e.PeakLevel);
        Assert.False(e.IsRunning);
    }

    [Fact]
    public void KnownSource_ComesOut_AtTheExpectedLevel()
    {
        using var e = new AudioEngine();
        e.AddSource(new ConstSource(0.25f), 0.5f);
        var out1 = Render(e, 480);
        Assert.All(out1, v => Assert.Equal(0.125f, v, 6));
        Assert.Equal(0.125f, e.PeakLevel, 6);
    }

    [Fact]
    public void Limiter_IsUnity_ForAMinus12dBSine()
    {
        using var e = new AudioEngine();
        float amp = AudioMath.DbToGain(-12f);
        e.AddSource(new SineSource(440f, amp));
        var y = Render(e, Sr);
        var reference = new SineSource(440f, amp);
        var x = new float[Sr * 2];
        reference.Mix(x, Sr);
        for (int i = 0; i < x.Length; i++) Assert.Equal(x[i], y[i], 6);
        Assert.InRange(e.PeakLevel, amp * 0.999f, amp * 1.001f);
    }

    [Fact]
    public void Limiter_KeepsOutputWithinFullScale_ForA12dBOverload()
    {
        using var e = new AudioEngine();
        e.AddSource(new SineSource(440f, AudioMath.DbToGain(12f)));
        e.AddSource(new SineSource(97f, 3f));
        var y = Render(e, Sr);
        Assert.All(y, v => Assert.True(float.IsFinite(v) && MathF.Abs(v) <= 1f));
        Assert.InRange(MaxAbs(y), 0.9f, 1f);
        Assert.InRange(e.PeakLevel, 0.9f, 1f);

        // And it recovers: after the loud sources go, a quiet one comes back at unity
        // within a few release time constants.
        e.Clear();
        e.AddSource(new ConstSource(0.1f));
        Render(e, Sr);   // 10 release time constants
        Assert.Equal(0.1f, Render(e, 10)[0], 4);
    }

    [Fact]
    public void Limiter_SurvivesAFullMixOfFullScaleSources()
    {
        using var e = new AudioEngine();
        for (int i = 0; i < AudioEngine.MaxSources; i++) e.AddSource(new ConstSource(999f), 16f);
        var y = Render(e, 2000);
        Assert.All(y, v => Assert.True(float.IsFinite(v) && MathF.Abs(v) <= 1f));
    }

    [Fact]
    public void SourceGainChange_IsSmoothed_WithoutAStep()
    {
        using var e = new AudioEngine();
        var s = new ConstSource(0.5f);
        e.AddSource(s, 1f);
        Render(e, 480);
        e.SetSourceGain(s, 0f);
        var y = Render(e, 4800);   // 100 ms
        Assert.True(y[0] > 0.49f, "the first sample after the change is still near the old level");
        Assert.True(MaxStep(y) < 0.5f * 0.01f, $"max step {MaxStep(y)}");
        Assert.True(MathF.Abs(y[2 * 1199]) < 0.5f * 0.01f, "≤1% left after 25 ms");
        Assert.Equal(0f, y[^1], 5);
        Assert.Equal(1, e.SourceCount);   // gain 0 keeps the source
    }

    [Fact]
    public void MasterVolumeAndMute_AreSmoothed()
    {
        using var e = new AudioEngine();
        e.AddSource(new ConstSource(0.5f));
        Render(e, 480);
        e.MasterVolume = 0.5f;
        var y = Render(e, 4800);
        Assert.True(MaxStep(y) < 0.005f);
        Assert.Equal(0.25f, y[^1], 4);

        e.Muted = true;
        y = Render(e, 4800);
        Assert.True(MaxStep(y) < 0.005f);
        Assert.Equal(0f, y[^1], 5);
        Render(e, 480);
        Assert.True(e.PeakLevel < 1e-4f);

        e.Muted = false;
        y = Render(e, 4800);
        Assert.True(MaxStep(y) < 0.005f);
        Assert.Equal(0.25f, y[^1], 4);

        e.MasterVolume = float.NaN;   // garbage in → silence, not NaN
        y = Render(e, 4800);
        Assert.All(y, v => Assert.True(float.IsFinite(v)));
    }

    [Fact]
    public void OneShot_IsDropped_WhenOver()
    {
        using var e = new AudioEngine();
        var shot = new OneShot(1000);
        e.AddSource(shot);
        e.AddSource(new ConstSource(0f));
        var y = Render(e, 800);
        Assert.Equal(2, e.SourceCount);
        y = Render(e, 800);
        Assert.Equal(0.1f, y[2 * 199], 6);
        Assert.Equal(0f, y[2 * 200], 6);
        Assert.Equal(1, e.SourceCount);
        Render(e, 800);
        Assert.Equal(2, shot.Calls);
    }

    [Fact]
    public void SourceCap_DropsTheOldestOneShot_AndKeepsPersistentSources()
    {
        using var e = new AudioEngine();
        var music = new ConstSource(0f);
        e.AddSource(music, 1f, persistent: true);
        var shots = new List<OneShot>();
        for (int i = 0; i < 60; i++)
        {
            var s = new OneShot(int.MaxValue, 0f);
            shots.Add(s);
            e.AddSource(s);
        }
        Assert.Equal(AudioEngine.MaxSources, e.SourceCount);
        Render(e, 100);
        Assert.Equal(1, music.Calls);
        for (int i = 0; i < 13; i++) Assert.Equal(0, shots[i].Calls);    // the oldest 13 were evicted
        for (int i = 13; i < 60; i++) Assert.Equal(1, shots[i].Calls);
        Assert.Equal(13, e.DroppedSources);

        // Adding the same source again does not take a second slot.
        e.AddSource(shots[59], 0.5f);
        Assert.Equal(AudioEngine.MaxSources, e.SourceCount);
        e.RemoveSource(music);
        Assert.Equal(AudioEngine.MaxSources - 1, e.SourceCount);
        e.Clear();
        Assert.Equal(0, e.SourceCount);
    }

    [Fact]
    public void BadSources_AreDropped_AndTheRestKeepsPlaying()
    {
        using var e = new AudioEngine();
        e.AddSource(new ThrowingSource());
        e.AddSource(new NaNSource(float.NaN));
        e.AddSource(new NaNSource(float.PositiveInfinity));
        e.AddSource(new NaNSource(1e9f));
        e.AddSource(new ConstSource(0.2f));
        var y = Render(e, 480);
        Assert.All(y, v => Assert.Equal(0.2f, v, 6));
        Assert.Equal(1, e.SourceCount);
        Assert.Equal(4, e.DroppedSources);
        y = Render(e, 480);
        Assert.All(y, v => Assert.Equal(0.2f, v, 6));
    }

    private static AudioEngine BuildScene(out IAudioSource sine)
    {
        var e = new AudioEngine();
        sine = new SineSource(440f, 2f);   // drives the limiter
        e.AddSource(sine, 0.8f);
        e.AddSource(new SineSource(55f, 0.3f), 0.5f);
        e.AddSource(new OneShot(1234, 0.2f));
        e.MasterVolume = 0.9f;
        return e;
    }

    [Fact]
    public void OfflineRendering_IsBlockSizeIndependent()
    {
        using var a = BuildScene(out var sa);
        using var b = BuildScene(out var sb);
        var whole = new float[1600 * 2];
        var parts = new float[1600 * 2];
        a.Render(whole.AsSpan(0, 1600), 800);
        for (int i = 0; i < 8; i++) b.Render(parts.AsSpan(i * 200, 200), 100);
        a.SetSourceGain(sa, 0.1f); a.MasterVolume = 0.4f;
        b.SetSourceGain(sb, 0.1f); b.MasterVolume = 0.4f;
        a.Render(whole.AsSpan(1600), 800);
        for (int i = 0; i < 8; i++) b.Render(parts.AsSpan(1600 + i * 200, 200), 100);
        Assert.Equal(whole, parts);
        Assert.True(MaxAbs(whole) > 0.5f);
    }

    [Fact]
    public void WavFile_Write_ProducesAValidHeader_AndTheRightSampleCount()
    {
        string path = Path.Combine(Path.GetTempPath(), $"solarsystem-audio-test-{Guid.NewGuid():N}.wav");
        try
        {
            var samples = new float[300];
            samples[0] = 1f; samples[1] = -1f; samples[2] = 0.5f; samples[3] = 2f; samples[4] = float.NaN;
            WavFile.Write(path, samples);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(44 + 600, bytes.Length);
            Assert.Equal("RIFF"u8.ToArray(), bytes[0..4]);
            Assert.Equal(bytes.Length - 8, BitConverter.ToInt32(bytes, 4));
            Assert.Equal("WAVE"u8.ToArray(), bytes[8..12]);
            Assert.Equal("fmt "u8.ToArray(), bytes[12..16]);
            Assert.Equal(16, BitConverter.ToInt32(bytes, 16));
            Assert.Equal(1, BitConverter.ToInt16(bytes, 20));
            Assert.Equal(2, BitConverter.ToInt16(bytes, 22));
            Assert.Equal(Sr, BitConverter.ToInt32(bytes, 24));
            Assert.Equal(Sr * 4, BitConverter.ToInt32(bytes, 28));
            Assert.Equal(4, BitConverter.ToInt16(bytes, 32));
            Assert.Equal(16, BitConverter.ToInt16(bytes, 34));
            Assert.Equal("data"u8.ToArray(), bytes[36..40]);
            Assert.Equal(600, BitConverter.ToInt32(bytes, 40));
            Assert.Equal(32767, BitConverter.ToInt16(bytes, 44));
            Assert.Equal(-32767, BitConverter.ToInt16(bytes, 46));
            Assert.Equal(16384, BitConverter.ToInt16(bytes, 48));
            Assert.Equal(32767, BitConverter.ToInt16(bytes, 50));
            Assert.Equal(0, BitConverter.ToInt16(bytes, 52));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ConcurrentAddRemove_WhileRendering_DoesNotCrash()
    {
        using var e = new AudioEngine();
        e.AddSource(new ConstSource(0.01f), 1f, persistent: true);
        using var stop = new CancellationTokenSource();
        Exception? error = null;
        var game = new Thread(() =>
        {
            try
            {
                var pool = new List<IAudioSource>();
                int i = 0;
                while (!stop.IsCancellationRequested)
                {
                    var s = new OneShot(500 + (i % 7) * 100, 0.001f);
                    e.AddSource(s);
                    pool.Add(s);
                    if (i % 3 == 0) e.SetSourceGain(pool[i / 2], 0.5f);
                    if (i % 2 == 0) e.RemoveSource(pool[i / 2]);
                    if (i % 500 == 499) e.Clear();
                    _ = e.SourceCount;
                    i++;
                }
            }
            catch (Exception ex) { error = ex; }
        });
        game.Start();
        var buf = new float[480 * 2];
        for (int b = 0; b < 3000; b++)
        {
            e.Render(buf, 480);
            foreach (float v in buf) Assert.True(float.IsFinite(v) && MathF.Abs(v) <= 1f);
        }
        stop.Cancel();
        game.Join();
        Assert.Null(error);
        Assert.InRange(e.SourceCount, 0, AudioEngine.MaxSources);
    }

    [Fact]
    public void Start_NeverThrows_AndOfflineRenderingStillWorksWhenItFails()
    {
        // Does not open the device: only checks the non-Windows / disposed paths.
        var e = new AudioEngine();
        e.Dispose();
        Assert.False(e.Start());
        Assert.False(e.IsRunning);
        e.Stop();
        e.AddSource(new ConstSource(0.1f));
        Assert.Equal(0.1f, Render(e, 10)[0], 6);
        if (!OperatingSystem.IsWindows())
        {
            using var f = new AudioEngine();
            Assert.False(f.Start());
        }
    }

    /// <summary>Opens the real device, with NO sources (digital silence) and the master at 0.
    /// Runs only when SOLARSYSTEM_AUDIO_DEVICE_TEST=1 (CI machines have no sound hardware).</summary>
    [Fact]
    public void Device_PumpsSilence_AndStartsAndStopsCleanly()
    {
        if (Environment.GetEnvironmentVariable("SOLARSYSTEM_AUDIO_DEVICE_TEST") != "1") return;
        var e = new AudioEngine { MasterVolume = 0f, Muted = true };
        Assert.Equal(0, e.SourceCount);

        Assert.True(e.Start(), "the default output device could not be opened");
        Assert.True(e.IsRunning);
        Assert.True(e.Start(), "Start while running is a no-op returning true");
        Thread.Sleep(300);
        long written = e.DeviceFramesWritten;
        log.WriteLine($"frames written in ~300 ms: {written}, rendered {e.FramesRendered}, underruns {e.DeviceUnderruns}, peak {e.PeakLevel}");
        // 300 ms = 14400 frames, plus up to the 4 × 480 queued up front.
        Assert.InRange(written, 9000, 14400 + 1920 + 4800);
        Assert.Equal(0f, e.PeakLevel);
        var sw = Stopwatch.StartNew();
        e.Stop();
        log.WriteLine($"stop took {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.ElapsedMilliseconds < 1000);
        Assert.False(e.IsRunning);

        Assert.True(e.Start());
        Thread.Sleep(100);
        Assert.True(e.DeviceFramesWritten > 0);
        sw.Restart();
        e.Dispose();
        log.WriteLine($"dispose took {sw.ElapsedMilliseconds} ms");
        Assert.False(e.IsRunning);
        Assert.False(e.Start());
    }

    // ---------- DSP helpers ----------

    [Theory]
    [InlineData(OscShape.Sine)]
    [InlineData(OscShape.Saw)]
    [InlineData(OscShape.Square)]
    [InlineData(OscShape.Triangle)]
    public void Oscillator_HasTheRightFrequency_AndAmplitude(OscShape shape)
    {
        foreach (float hz in new[] { 110f, 440f, 3000f })
        {
            var osc = new Oscillator();
            float prev = osc.Next(hz, shape), max = MathF.Abs(prev), sum = prev;
            int rising = 0;
            for (int i = 1; i < Sr; i++)
            {
                float v = osc.Next(hz, shape);
                if (prev < 0f && v >= 0f) rising++;
                max = MathF.Max(max, MathF.Abs(v));
                sum += v;
                prev = v;
            }
            Assert.InRange(rising, hz - 2, hz + 2);
            // A PolyBLEP saw's last sample before the wrap is rounded off: at 3 kHz (16
            // samples a cycle) its peak sample is 1 - 2·dt = 0.875.
            Assert.InRange(max, 0.85f, 1.05f);
            Assert.InRange(sum / Sr, -0.01f, 0.01f);   // no DC
        }
    }

    [Fact]
    public void Oscillator_SurvivesANaNFrequency()
    {
        var osc = new Oscillator();
        osc.Next(float.NaN, OscShape.Saw);
        Assert.True(float.IsFinite(osc.Next(440f, OscShape.Saw)));
        Assert.True(float.IsFinite(osc.Phase));
    }

    /// <summary>Power below the fundamental (where a band-limited wave has nothing but
    /// aliases) of one second of the wave, by direct DFT at 1 Hz bins.</summary>
    private static double AliasPower(Func<float> next, int fromHz, int toHz)
    {
        var x = new double[Sr];
        for (int i = 0; i < Sr; i++) x[i] = next();
        double total = 0;
        for (int f = fromHz; f <= toHz; f++)
        {
            double w = 2 * Math.PI * f / Sr, re = 0, im = 0;
            // Goertzel would do; a plain DFT is clear and fast enough here.
            double c = Math.Cos(w), s = Math.Sin(w), cr = 1, ci = 0;
            for (int i = 0; i < Sr; i++)
            {
                re += x[i] * cr; im -= x[i] * ci;
                double nr = cr * c - ci * s; ci = cr * s + ci * c; cr = nr;
            }
            total += re * re + im * im;
        }
        return total;
    }

    [Theory]
    [InlineData(OscShape.Saw)]
    [InlineData(OscShape.Square)]
    public void PolyBlep_HasMuchLessAliasing_ThanTheNaiveWave(OscShape shape)
    {
        const float hz = 2345.6f;
        var osc = new Oscillator();
        double blep = AliasPower(() => osc.Next(hz, shape), 30, 2000);
        float p = 0;
        double naive = AliasPower(() =>
        {
            float v = shape == OscShape.Saw ? 2f * p - 1f : (p < 0.5f ? 1f : -1f);
            p += hz / Sr; if (p >= 1f) p -= 1f;
            return v;
        }, 30, 2000);
        log.WriteLine($"{shape}: alias power below f0 blep {blep:E3} naive {naive:E3} ({10 * Math.Log10(naive / blep):F1} dB better)");
        Assert.True(blep < naive / 4, $"blep {blep} naive {naive}");

        // And no burst near Nyquist: the output stays bounded at the top of the range.
        var hi = new Oscillator();
        for (int i = 0; i < Sr; i++) Assert.InRange(hi.Next(20000f, shape), -1.5f, 1.5f);
    }

    [Fact]
    public void Noise_IsDeterministicPerSeed_AndRoughlyZeroMean()
    {
        var a = new Noise(42); var b = new Noise(42); var c = new Noise(43);
        double sum = 0; int same = 0;
        for (int i = 0; i < 100_000; i++)
        {
            float x = a.White();
            Assert.Equal(x, b.White());
            if (x == c.White()) same++;
            Assert.InRange(x, -1f, 1f);
            sum += x;
        }
        Assert.True(same < 10);
        Assert.InRange(sum / 100_000, -0.01, 0.01);

        var d = new Noise(0);   // seed 0 is remapped, not stuck at zero
        Assert.NotEqual(d.White(), d.White());
        var p = new Noise(7);
        double pinkSq = 0;
        for (int i = 0; i < 100_000; i++)
        {
            float v = p.Pink();
            Assert.InRange(v, -3f, 3f);   // "roughly ±1": peaks of ~1.6 do occur
            pinkSq += v * v;
        }
        Assert.InRange(Math.Sqrt(pinkSq / 100_000), 0.1, 0.6);
        var u = new Noise(9);
        for (int i = 0; i < 10_000; i++) Assert.InRange(u.Unit(), 0f, 1f);
    }

    private static float GainDb(Biquad f, float hz)
    {
        double sumIn = 0, sumOut = 0;
        for (int i = 0; i < Sr; i++)
        {
            float x = MathF.Sin(AudioMath.TwoPi * hz * i / Sr);
            float y = f.Process(x);
            if (i >= Sr / 2) { sumIn += x * x; sumOut += y * y; }
        }
        return (float)(10 * Math.Log10(sumOut / sumIn));
    }

    [Fact]
    public void Biquad_HasTheExpectedGains()
    {
        var lp = new Biquad(); lp.SetLowPass(1000f);
        var hp = new Biquad(); hp.SetHighPass(1000f);
        var bp = new Biquad(); bp.SetBandPass(1000f, 1f);
        Assert.InRange(GainDb(lp, 100f), -0.2f, 0.2f);
        Assert.InRange(GainDb(lp, 1000f), -3.4f, -2.6f);
        Assert.True(GainDb(lp, 10000f) < -38f);
        Assert.InRange(GainDb(hp, 10000f), -0.3f, 0.3f);
        Assert.InRange(GainDb(hp, 1000f), -3.4f, -2.6f);
        Assert.True(GainDb(hp, 100f) < -38f);
        Assert.InRange(GainDb(bp, 1000f), -0.2f, 0.2f);
        Assert.True(GainDb(bp, 100f) < -18f);
        Assert.True(GainDb(bp, 10000f) < -18f);

        // A NaN input resets the state instead of poisoning the filter.
        var f = new Biquad(); f.SetLowPass(500f);
        Assert.Equal(0f, f.Process(float.NaN));
        Assert.True(float.IsFinite(f.Process(0.5f)));
    }

    [Fact]
    public void Envelope_ReachesOne_AndReturnsToIdle()
    {
        var env = new Envelope(0.01f, 0.1f, 0.5f, 0.05f);
        Assert.True(env.IsIdle);
        env.NoteOn();
        Assert.False(env.IsIdle);
        float max = 0; int reachedAt = -1;
        for (int i = 0; i < Sr; i++)
        {
            float v = env.Next();
            Assert.InRange(v, 0f, 1f);
            if (v >= 1f && reachedAt < 0) reachedAt = i;
            max = MathF.Max(max, v);
        }
        Assert.Equal(1f, max);
        Assert.InRange(reachedAt, 470, 490);   // 10 ms
        Assert.Equal(0.5f, env.Level, 3);
        env.NoteOff();
        int idleAt = -1;
        for (int i = 0; i < Sr && idleAt < 0; i++)
        {
            env.Next();
            if (env.IsIdle) idleAt = i;
        }
        Assert.InRange(idleAt, 1, Sr / 2);
        Assert.Equal(0f, env.Level);

        var zeroSustain = new Envelope(0f, 0.01f, 0f, 0.01f);
        zeroSustain.NoteOn();
        for (int i = 0; i < Sr; i++) zeroSustain.Next();
        Assert.Equal(0f, zeroSustain.Level);   // snapped, no denormal creep
    }

    [Fact]
    public void Pan_IsEqualPower()
    {
        for (float p = -1f; p <= 1.0001f; p += 0.05f)
        {
            AudioMath.Pan(p, out float l, out float r);
            Assert.Equal(1f, l * l + r * r, 5);
        }
        AudioMath.Pan(-1f, out float l1, out float r1);
        Assert.Equal(1f, l1, 5); Assert.Equal(0f, r1, 5);
        AudioMath.Pan(1f, out float l2, out float r2);
        Assert.Equal(0f, l2, 5); Assert.Equal(1f, r2, 5);
        AudioMath.Pan(0f, out float l3, out float r3);
        Assert.Equal(l3, r3, 6);
        AudioMath.Pan(5f, out float l4, out _);
        Assert.Equal(0f, l4, 5);
    }

    [Fact]
    public void Reverb_IsFinite_HasAPlausibleTail_AndDecaysToSilence()
    {
        var rev = new Reverb();
        const int seconds = 40, win = Sr / 20;   // 50 ms windows
        var energy = new double[seconds * 20];
        int lastNonZero = -1;
        for (int i = 0; i < seconds * Sr; i++)
        {
            float x = i < 480 ? 0.5f : 0f;   // 10 ms burst
            rev.Process(x, x, out float l, out float r);
            Assert.True(float.IsFinite(l) && float.IsFinite(r));
            energy[i / win] += l * l + r * r;
            if (l != 0f || r != 0f) lastNonZero = i;
        }
        double peak = energy.Max();
        int peakWin = Array.IndexOf(energy, peak);
        int lastLoud = Array.FindLastIndex(energy, en => en > peak * 1e-6);   // -60 dB
        double rt60 = (lastLoud - peakWin) / 20.0;
        log.WriteLine($"reverb: peak window {peakWin}, RT60 ≈ {rt60:F2} s, last non-zero sample at {lastNonZero / (double)Sr:F1} s");
        Assert.True(peak > 1e-6);
        Assert.InRange(rt60, 0.5, 8.0);
        Assert.True(lastNonZero < (seconds - 1) * Sr, "the tail reaches exact zero (no denormal creep)");
    }

    [Fact]
    public void SoftClip_IsBounded_Monotonic_AndLinearForSmallSignals()
    {
        float prev = AudioMath.SoftClip(-100f);
        for (float x = -100f; x <= 100f; x += 0.01f)
        {
            float y = AudioMath.SoftClip(x);
            Assert.InRange(y, -1f, 1f);
            Assert.True(y >= prev - 1e-6f);
            prev = y;
        }
        Assert.Equal(1f, AudioMath.SoftClip(1.5f), 6);
        Assert.Equal(-1f, AudioMath.SoftClip(-1.5f), 6);
        for (float x = -0.1f; x <= 0.1f; x += 0.01f)
            Assert.Equal(x, AudioMath.SoftClip(x), 3);
    }
}
