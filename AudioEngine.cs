// PROTOTYPE (2026-11-17): arcade flight mode — sound: the mixer and the audio device.
using System.IO;

namespace SolarSystem;

// How the mixer works:
//  * The game thread adds / removes sources and changes gains under a short lock on a
//    list of entries. Once per block the render thread copies the entry references into a
//    preallocated snapshot (under the same lock, a few dozen pointer copies), then mixes
//    every source WITHOUT holding the lock. Dead sources (one-shot over, threw, produced
//    NaN / infinity / absurd values) are removed under the lock afterwards.
//  * Each source is mixed into its own scratch buffer, checked, and added to the output
//    with a per-sample smoothed gain (one-pole, 5 ms time constant: ~15 ms to 95%).
//  * Master volume and mute go through the same kind of smoother.
//  * A stereo-linked peak limiter follows: zero attack (the gain drops instantly, so the
//    output can never overshoot), 100 ms release, with a soft-knee static curve that is
//    exactly unity below -3 dBFS and approaches (never reaches) full scale above it.
//  * Everything is per sample, so the result does not depend on the block size: the
//    device thread (480-frame blocks) and offline rendering (any block size) give the
//    same samples for the same inputs.
//  * Nothing allocates per block once the scratch buffer is large enough (the device
//    back-end sizes it up front).

/// <summary>
/// Mixes <see cref="IAudioSource"/>s into one stereo stream and plays it on the default
/// audio device. Also renders "offline" — on the caller's thread, with no device — for
/// the hidden demo and for tests.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    /// <summary>Most sources mixed at once. When the mix is full, adding a source drops the
    /// oldest evictable (non-persistent) source.</summary>
    public const int MaxSources = 48;

    /// <summary>Gain / master smoothing time constant in seconds (~3× this to settle).</summary>
    public const float SmoothingSeconds = 0.005f;

    /// <summary>The limiter is exactly unity below this level (-3 dBFS).</summary>
    public const float LimiterThreshold = 0.70794578f;

    /// <summary>Limiter release time constant in seconds.</summary>
    public const float LimiterReleaseSeconds = 0.1f;

    /// <summary>A source sample louder than this (+60 dBFS) is treated as garbage.</summary>
    private const float GarbageLevel = 1000f;

    private const float MaxGain = 16f;

    private sealed class Entry
    {
        public required IAudioSource Source;
        public volatile float TargetGain;
        public float CurrentGain;        // render thread only
        public bool Persistent;
    }

    private readonly object _gate = new();           // guards _entries
    private readonly object _renderGate = new();     // one Render at a time
    private readonly object _deviceGate = new();     // Start / Stop
    private readonly List<Entry> _entries = new(MaxSources);
    private readonly Entry[] _snapshot = new Entry[MaxSources];
    private readonly Entry[] _dead = new Entry[MaxSources];
    private float[] _scratch = new float[4096 * AudioFormat.Channels];   // ≥ any device block

    private readonly float _smoothCoeff = AudioMath.SmoothCoeff(SmoothingSeconds);
    private readonly float _releaseCoeff = AudioMath.SmoothCoeff(LimiterReleaseSeconds);
    private float _master;            // render thread: smoothed master gain
    private bool _masterPrimed;
    private float _limiterGain = 1f;  // render thread: current gain reduction

    private volatile float _masterVolume = 1f;
    private volatile bool _muted;
    private volatile float _peak;
    private int _droppedSources;
    private long _framesRendered;

    private IAudioOutput? _output;
    private bool _disposed;

    /// <summary>Open the default output device and start playing. Returns false (and the
    /// engine stays silent but usable offline) when there is no usable device or the
    /// platform is not supported. Never throws.</summary>
    public bool Start()
    {
        try
        {
            lock (_deviceGate)
            {
                if (_disposed) return false;
                if (_output is { IsRunning: true }) return true;
                _output?.Dispose();
                _output = null;
                if (!OperatingSystem.IsWindows()) return false;
                var output = new AudioOutputWinMM(this);
                if (!output.Start())
                {
                    output.Dispose();
                    return false;
                }
                _output = output;
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Stop playing and close the device. Sources are kept. Returns within about
    /// a second even if the driver misbehaves. Never throws.</summary>
    public void Stop()
    {
        try
        {
            lock (_deviceGate)
            {
                _output?.Dispose();
                _output = null;
            }
        }
        catch
        {
            // Stopping must never take the app down.
        }
    }

    /// <summary>True while a device is open and the audio thread is running.</summary>
    public bool IsRunning => _output?.IsRunning ?? false;

    /// <summary>Frames the device thread has handed to the device since the last
    /// <see cref="Start"/> (0 when not running).</summary>
    public long DeviceFramesWritten => _output?.FramesWritten ?? 0;

    /// <summary>Times the device queue ran dry since the last <see cref="Start"/>.</summary>
    public int DeviceUnderruns => _output?.Underruns ?? 0;

    /// <summary>Total frames rendered by <see cref="Render"/> (device and offline).</summary>
    public long FramesRendered => Interlocked.Read(ref _framesRendered);

    /// <summary>Sources removed because they threw or produced NaN / infinity / garbage,
    /// or were evicted by the source cap.</summary>
    public int DroppedSources => Volatile.Read(ref _droppedSources);

    /// <summary>Master volume 0 .. 1 (changes are smoothed to avoid clicks).</summary>
    public float MasterVolume
    {
        get => _masterVolume;
        set => _masterVolume = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
    }

    /// <summary>Silence everything (smoothly) without stopping the device.</summary>
    public bool Muted
    {
        get => _muted;
        set => _muted = value;
    }

    /// <summary>Loudest sample of the most recently rendered block, after the limiter (0 .. 1).</summary>
    public float PeakLevel => _peak;

    /// <summary>Number of sources currently in the mix.</summary>
    public int SourceCount { get { lock (_gate) return _entries.Count; } }

    /// <summary>Add a one-shot (evictable) source to the mix (thread-safe). It plays until
    /// its Mix returns false or it is removed. Adding a source that is already in the mix
    /// only changes its gain. The gain applies from the first sample (no fade-in).</summary>
    public void AddSource(IAudioSource source, float gain = 1f) => AddSource(source, gain, persistent: false);

    /// <summary>Add a source to the mix (thread-safe). <paramref name="persistent"/> sources
    /// (the soundscape, the music) are never evicted by the <see cref="MaxSources"/> cap;
    /// when the mix is full the oldest non-persistent source is dropped to make room, and
    /// if every slot is persistent the new source is ignored.</summary>
    public void AddSource(IAudioSource source, float gain, bool persistent)
    {
        ArgumentNullException.ThrowIfNull(source);
        float g = SanitizeGain(gain);
        lock (_gate)
        {
            foreach (var e in _entries)
            {
                if (ReferenceEquals(e.Source, source))
                {
                    e.TargetGain = g;
                    e.Persistent |= persistent;
                    return;
                }
            }
            if (_entries.Count >= MaxSources)
            {
                int victim = _entries.FindIndex(e => !e.Persistent);
                if (victim < 0) return;
                _entries.RemoveAt(victim);
                Interlocked.Increment(ref _droppedSources);
            }
            _entries.Add(new Entry { Source = source, TargetGain = g, CurrentGain = g, Persistent = persistent });
        }
    }

    /// <summary>Change a playing source's gain (smoothed). No-op when it is not in the mix.</summary>
    public void SetSourceGain(IAudioSource source, float gain)
    {
        float g = SanitizeGain(gain);
        lock (_gate)
        {
            foreach (var e in _entries)
                if (ReferenceEquals(e.Source, source)) e.TargetGain = g;
        }
    }

    /// <summary>Take a source out of the mix (thread-safe). It is not mixed again from the
    /// next block on; it stops abruptly, so fade it out first if it is loud.</summary>
    public void RemoveSource(IAudioSource source)
    {
        lock (_gate)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
                if (ReferenceEquals(_entries[i].Source, source)) _entries.RemoveAt(i);
        }
    }

    /// <summary>Remove every source.</summary>
    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }

    private static float SanitizeGain(float gain) => float.IsFinite(gain) ? Math.Clamp(gain, 0f, MaxGain) : 0f;

    /// <summary>Render the next <paramref name="frames"/> stereo frames of the mix into
    /// <paramref name="buffer"/> (interleaved L, R; cleared first), with master volume
    /// and limiter applied. The audio thread calls this; call it yourself only while the
    /// device is not running (offline rendering) — otherwise you steal the device's samples.
    /// The output is always finite and within ±1. Never throws for a bad source.</summary>
    public void Render(Span<float> buffer, int frames)
    {
        if (frames <= 0) return;
        int n = frames * AudioFormat.Channels;
        if (buffer.Length < n) throw new ArgumentException("Buffer too small for the frame count.", nameof(buffer));
        var output = buffer[..n];
        lock (_renderGate)
        {
            output.Clear();
            if (_scratch.Length < n) _scratch = new float[n];   // offline with large blocks only
            var scratch = _scratch.AsSpan(0, n);

            int count;
            lock (_gate)
            {
                count = _entries.Count;
                _entries.CopyTo(0, _snapshot, 0, count);
            }

            int dead = 0;
            for (int i = 0; i < count; i++)
            {
                var e = _snapshot[i];
                _snapshot[i] = null!;
                bool alive;
                scratch.Clear();
                try
                {
                    alive = e.Source.Mix(scratch, frames);
                }
                catch
                {
                    _dead[dead++] = e;
                    Interlocked.Increment(ref _droppedSources);
                    continue;
                }
                if (!IsSane(scratch))
                {
                    _dead[dead++] = e;
                    Interlocked.Increment(ref _droppedSources);
                    continue;
                }
                AddWithGain(output, scratch, e);
                if (!alive) _dead[dead++] = e;
            }

            if (dead > 0)
            {
                lock (_gate)
                {
                    for (int i = 0; i < dead; i++)
                    {
                        _entries.Remove(_dead[i]);
                        _dead[i] = null!;
                    }
                }
            }

            ApplyMasterAndLimiter(output);
            Interlocked.Add(ref _framesRendered, frames);
        }
    }

    private static bool IsSane(ReadOnlySpan<float> samples)
    {
        foreach (float s in samples)
        {
            // Also false for NaN (every comparison with NaN is false).
            if (!(MathF.Abs(s) <= GarbageLevel)) return false;
        }
        return true;
    }

    private void AddWithGain(Span<float> output, ReadOnlySpan<float> src, Entry e)
    {
        float target = e.TargetGain;
        float g = e.CurrentGain;
        if (g == target)
        {
            for (int k = 0; k < output.Length; k++) output[k] += src[k] * g;
            return;
        }
        float c = _smoothCoeff;
        for (int k = 0; k < output.Length; k += 2)
        {
            g += (target - g) * c;
            output[k] += src[k] * g;
            output[k + 1] += src[k + 1] * g;
        }
        if (MathF.Abs(g - target) < 1e-6f) g = target;
        e.CurrentGain = g;
    }

    private void ApplyMasterAndLimiter(Span<float> output)
    {
        float target = _muted ? 0f : _masterVolume;
        if (!_masterPrimed) { _master = target; _masterPrimed = true; }
        float m = _master;
        float lg = _limiterGain;
        if (!float.IsFinite(lg) || lg <= 0f || lg > 1f) lg = 1f;
        float c = _smoothCoeff, rel = _releaseCoeff;
        const float t = LimiterThreshold, span = 1f - LimiterThreshold;
        float peak = 0f;
        for (int k = 0; k < output.Length; k += 2)
        {
            m += (target - m) * c;
            float l = output[k] * m;
            float r = output[k + 1] * m;
            float a = MathF.Max(MathF.Abs(l), MathF.Abs(r));
            // Static curve: unity below the threshold, soft knee up to (never reaching) 1.
            float want = a <= t ? 1f : (t + span * MathF.Tanh((a - t) / span)) / a;
            if (!float.IsFinite(want)) want = 0f;   // only for an absurd sum; mutes, never blows up
            if (want < lg) lg = want;               // instant attack: never overshoots
            else lg += (want - lg) * rel;           // ~100 ms release
            l *= lg;
            r *= lg;
            // Belt and braces: the curve keeps |x| < 1 already; float rounding cannot exceed it.
            l = Math.Clamp(l, -1f, 1f);
            r = Math.Clamp(r, -1f, 1f);
            if (!float.IsFinite(l)) l = 0f;
            if (!float.IsFinite(r)) r = 0f;
            output[k] = l;
            output[k + 1] = r;
            float p = MathF.Max(MathF.Abs(l), MathF.Abs(r));
            if (p > peak) peak = p;
        }
        if (MathF.Abs(m - target) < 1e-6f) m = target;
        _master = m;
        _limiterGain = lg;
        _peak = peak;
    }

    /// <summary>Stop the device and refuse further <see cref="Start"/> calls. Offline
    /// rendering keeps working.</summary>
    public void Dispose()
    {
        lock (_deviceGate) _disposed = true;
        Stop();
    }
}

/// <summary>A device back-end that pulls blocks from an <see cref="AudioEngine"/>.</summary>
internal interface IAudioOutput : IDisposable
{
    bool IsRunning { get; }
    long FramesWritten { get; }
    int Underruns { get; }
}

/// <summary>Writes rendered audio to a 16-bit PCM WAV file.</summary>
public static class WavFile
{
    /// <summary>Write interleaved stereo float samples (±1 full scale) as 16-bit PCM.</summary>
    public static void Write(string path, ReadOnlySpan<float> interleavedStereo, int sampleRate = AudioFormat.SampleRate)
    {
        int dataBytes = interleavedStereo.Length * 2;
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);                               // PCM
        w.Write((short)AudioFormat.Channels);
        w.Write(sampleRate);
        w.Write(sampleRate * AudioFormat.Channels * 2);  // bytes per second
        w.Write((short)(AudioFormat.Channels * 2));      // block align
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(dataBytes);
        foreach (float s in interleavedStereo)
            w.Write(ToPcm16(s));
    }

    /// <summary>Float sample (±1 full scale) to 16-bit PCM, clamped; NaN becomes 0.</summary>
    internal static short ToPcm16(float s)
    {
        if (!float.IsFinite(s)) return float.IsNaN(s) ? (short)0 : (s > 0 ? short.MaxValue : short.MinValue);
        return (short)Math.Clamp((int)MathF.Round(s * 32767f), short.MinValue, short.MaxValue);
    }
}
