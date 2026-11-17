namespace SolarSystem;

// PROTOTYPE (2026-11-17): arcade flight mode — sound: small DSP building blocks shared by
// every synthesised sound (oscillators, noise, filters, envelopes, delay, reverb).
// All of them are allocation-free per sample and assume AudioFormat.SampleRate.

/// <summary>Conversions and little helpers.</summary>
public static class AudioMath
{
    public const float TwoPi = MathF.PI * 2f;

    /// <summary>MIDI note number to frequency (69 = A4 = 440 Hz).</summary>
    public static float MidiToHz(float note) => 440f * MathF.Pow(2f, (note - 69f) / 12f);

    public static float DbToGain(float db) => MathF.Pow(10f, db / 20f);

    /// <summary>Per-sample coefficient for a one-pole smoother that covers about 63% of a
    /// step in <paramref name="seconds"/> (use with <see cref="Smooth"/>).</summary>
    public static float SmoothCoeff(float seconds)
        => seconds <= 0f ? 1f : 1f - MathF.Exp(-1f / (seconds * AudioFormat.SampleRate));

    /// <summary>Equal-power stereo gains for <paramref name="pan"/> in -1 (left) .. +1 (right).</summary>
    public static void Pan(float pan, out float left, out float right)
    {
        float a = (Math.Clamp(pan, -1f, 1f) + 1f) * (MathF.PI / 4f);
        left = MathF.Cos(a);
        right = MathF.Sin(a);
    }

    /// <summary>Gentle saturation: linear for small signals, never exceeds ±1.</summary>
    public static float SoftClip(float x)
    {
        if (x > 1.5f) return 1f;
        if (x < -1.5f) return -1f;
        return x - x * x * x * (4f / 27f);
    }

    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>Filter / reverb state below this magnitude (-400 dBFS) is flushed to zero, so
    /// decaying tails never reach the denormal range (very slow on x86 / x64 CPUs).</summary>
    internal const float DenormalFloor = 1e-20f;
}

public enum OscShape { Sine, Saw, Square, Triangle }

/// <summary>Band-limited oscillator (PolyBLEP saw and square). Output is -1 .. +1.</summary>
public struct Oscillator
{
    /// <summary>Phase in cycles, 0 .. 1.</summary>
    public float Phase;

    public float Next(float frequencyHz, OscShape shape = OscShape.Sine)
    {
        // A NaN frequency would otherwise poison the phase forever (Math.Clamp passes NaN through).
        float dt = float.IsNaN(frequencyHz) ? 0f : Math.Clamp(frequencyHz / AudioFormat.SampleRate, 0f, 0.45f);
        float p = Phase;
        float v = shape switch
        {
            OscShape.Saw => 2f * p - 1f - PolyBlep(p, dt),
            OscShape.Square => (p < 0.5f ? 1f : -1f) + PolyBlep(p, dt) - PolyBlep((p + 0.5f) % 1f, dt),
            OscShape.Triangle => 4f * MathF.Abs(p - 0.5f) - 1f,
            _ => MathF.Sin(AudioMath.TwoPi * p),
        };
        p += dt;
        if (p >= 1f) p -= 1f;
        Phase = p;
        return v;
    }

    private static float PolyBlep(float t, float dt)
    {
        if (dt <= 0f) return 0f;
        if (t < dt) { t /= dt; return t + t - t * t - 1f; }
        if (t > 1f - dt) { t = (t - 1f) / dt; return t * t + t + t + 1f; }
        return 0f;
    }
}

/// <summary>Seeded noise: deterministic, so the same seed gives the same sound.</summary>
public struct Noise
{
    private uint _state;
    private float _b0, _b1, _b2;

    public Noise(uint seed)
    {
        _state = seed == 0 ? 0x9E3779B9u : seed;
        _b0 = _b1 = _b2 = 0f;
    }

    /// <summary>White noise, -1 .. +1.</summary>
    public float White()
    {
        if (_state == 0) _state = 0x9E3779B9u;
        uint x = _state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _state = x;
        return (x >> 8) * (2f / 16777216f) - 1f;
    }

    /// <summary>Uniform random number 0 .. 1.</summary>
    public float Unit() => White() * 0.5f + 0.5f;

    /// <summary>Pink (1/f) noise, roughly -1 .. +1.</summary>
    public float Pink()
    {
        float w = White();
        _b0 = 0.99765f * _b0 + w * 0.0990460f;
        _b1 = 0.96300f * _b1 + w * 0.2965164f;
        _b2 = 0.57000f * _b2 + w * 1.0526913f;
        return (_b0 + _b1 + _b2 + w * 0.1848f) * 0.25f;
    }
}

/// <summary>One-pole smoother for control values (see <see cref="AudioMath.SmoothCoeff"/>).</summary>
public struct Smooth
{
    public float Value;
    public float Next(float target, float coeff) => Value += (target - Value) * coeff;
}

/// <summary>Second-order filter (RBJ cookbook), transposed direct form II.</summary>
public struct Biquad
{
    private float _b0, _b1, _b2, _a1, _a2, _z1, _z2;

    public void SetLowPass(float frequencyHz, float q = 0.707f) => Set(0, frequencyHz, q);
    public void SetHighPass(float frequencyHz, float q = 0.707f) => Set(1, frequencyHz, q);
    /// <summary>Band-pass with 0 dB gain at the centre frequency.</summary>
    public void SetBandPass(float frequencyHz, float q = 1f) => Set(2, frequencyHz, q);

    private void Set(int kind, float f, float q)
    {
        f = Math.Clamp(f, 10f, AudioFormat.SampleRate * 0.45f);
        q = MathF.Max(q, 0.05f);
        float w0 = AudioMath.TwoPi * f / AudioFormat.SampleRate;
        float cs = MathF.Cos(w0), sn = MathF.Sin(w0);
        float alpha = sn / (2f * q);
        float b0, b1, b2;
        switch (kind)
        {
            case 0: b0 = (1f - cs) * 0.5f; b1 = 1f - cs; b2 = b0; break;
            case 1: b0 = (1f + cs) * 0.5f; b1 = -(1f + cs); b2 = b0; break;
            default: b0 = alpha; b1 = 0f; b2 = -alpha; break;
        }
        float a0 = 1f + alpha;
        _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0;
        _a1 = -2f * cs / a0; _a2 = (1f - alpha) / a0;
    }

    public float Process(float x)
    {
        float y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        // Flush denormals / recover from a bad input instead of ringing forever.
        if (!float.IsFinite(y)) { _z1 = _z2 = 0f; return 0f; }
        if (MathF.Abs(_z1) < AudioMath.DenormalFloor) _z1 = 0f;
        if (MathF.Abs(_z2) < AudioMath.DenormalFloor) _z2 = 0f;
        return y;
    }

    public void Reset() => _z1 = _z2 = 0f;
}

/// <summary>Attack / decay / sustain / release envelope, 0 .. 1.</summary>
public struct Envelope
{
    public float AttackSeconds, DecaySeconds, SustainLevel, ReleaseSeconds;
    public float Level;
    private int _stage;   // 0 idle, 1 attack, 2 decay / sustain, 3 release

    public Envelope(float attack, float decay, float sustain, float release)
    {
        AttackSeconds = attack; DecaySeconds = decay; SustainLevel = sustain; ReleaseSeconds = release;
        Level = 0f; _stage = 0;
    }

    public readonly bool IsIdle => _stage == 0;

    public void NoteOn() => _stage = 1;
    public void NoteOff() { if (_stage != 0) _stage = 3; }

    public float Next()
    {
        switch (_stage)
        {
            case 1:
                Level += 1f / MathF.Max(1f, AttackSeconds * AudioFormat.SampleRate);
                if (Level >= 1f) { Level = 1f; _stage = 2; }
                break;
            case 2:
                Level += (SustainLevel - Level) * AudioMath.SmoothCoeff(DecaySeconds * 0.33f);
                // Snap once there, so a sustain of 0 does not creep into denormals.
                if (MathF.Abs(SustainLevel - Level) < 1e-6f) Level = SustainLevel;
                break;
            case 3:
                Level -= Level * AudioMath.SmoothCoeff(ReleaseSeconds * 0.33f);
                if (Level < 1e-4f) { Level = 0f; _stage = 0; }
                break;
        }
        return Level;
    }
}

/// <summary>Fixed-size delay line (integer delays).</summary>
public sealed class DelayLine
{
    private readonly float[] _buffer;
    private int _pos;

    public DelayLine(int maxSamples) => _buffer = new float[Math.Max(1, maxSamples)];

    public int Length => _buffer.Length;

    /// <summary>The sample written <paramref name="samplesAgo"/> samples ago (1 .. Length).</summary>
    public float Read(int samplesAgo)
    {
        int i = _pos - Math.Clamp(samplesAgo, 1, _buffer.Length);
        if (i < 0) i += _buffer.Length;
        return _buffer[i];
    }

    public void Write(float x)
    {
        _buffer[_pos] = x;
        if (++_pos >= _buffer.Length) _pos = 0;
    }

    public void Clear() => Array.Clear(_buffer);
}

/// <summary>Stereo reverb (Freeverb-style: 8 damped combs + 4 all-passes per channel).
/// Feed it a dry signal, add the wet output to the mix.</summary>
public sealed class Reverb
{
    private static readonly int[] CombTuning = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];
    private static readonly int[] AllpassTuning = [556, 441, 341, 225];
    private const int StereoSpread = 23;
    private const float InputGain = 0.015f;

    private readonly float[][] _combL, _combR, _apL, _apR;
    private readonly int[] _combPosL, _combPosR, _apPosL, _apPosR;
    private readonly float[] _combStoreL, _combStoreR;

    /// <summary>0 .. 1: how long the tail rings (0.5 small room, 0.85 hall, 0.95 cathedral).</summary>
    public float RoomSize { get; set; } = 0.82f;
    /// <summary>0 .. 1: how quickly the highs die in the tail.</summary>
    public float Damping { get; set; } = 0.35f;

    public Reverb()
    {
        float scale = AudioFormat.SampleRate / 44100f;
        int n = CombTuning.Length, m = AllpassTuning.Length;
        _combL = new float[n][]; _combR = new float[n][];
        _apL = new float[m][]; _apR = new float[m][];
        _combPosL = new int[n]; _combPosR = new int[n];
        _apPosL = new int[m]; _apPosR = new int[m];
        _combStoreL = new float[n]; _combStoreR = new float[n];
        for (int i = 0; i < n; i++)
        {
            _combL[i] = new float[(int)(CombTuning[i] * scale)];
            _combR[i] = new float[(int)((CombTuning[i] + StereoSpread) * scale)];
        }
        for (int i = 0; i < m; i++)
        {
            _apL[i] = new float[(int)(AllpassTuning[i] * scale)];
            _apR[i] = new float[(int)((AllpassTuning[i] + StereoSpread) * scale)];
        }
    }

    /// <summary>Process one stereo sample; the result is the wet signal only.</summary>
    public void Process(float inL, float inR, out float wetL, out float wetR)
    {
        float input = (inL + inR) * InputGain;
        float feedback = 0.7f + 0.28f * Math.Clamp(RoomSize, 0f, 1f);
        float damp = 0.4f * Math.Clamp(Damping, 0f, 1f);
        float l = 0f, r = 0f;
        for (int i = 0; i < _combL.Length; i++)
        {
            l += Comb(_combL[i], ref _combPosL[i], ref _combStoreL[i], input, feedback, damp);
            r += Comb(_combR[i], ref _combPosR[i], ref _combStoreR[i], input, feedback, damp);
        }
        for (int i = 0; i < _apL.Length; i++)
        {
            l = Allpass(_apL[i], ref _apPosL[i], l);
            r = Allpass(_apR[i], ref _apPosR[i], r);
        }
        wetL = float.IsFinite(l) ? l : 0f;
        wetR = float.IsFinite(r) ? r : 0f;
    }

    private static float Comb(float[] buf, ref int pos, ref float store, float input, float feedback, float damp)
    {
        float output = buf[pos];
        store = output * (1f - damp) + store * damp;
        if (MathF.Abs(store) < AudioMath.DenormalFloor) store = 0f;
        float w = input + store * feedback;
        buf[pos] = MathF.Abs(w) < AudioMath.DenormalFloor ? 0f : w;
        if (++pos >= buf.Length) pos = 0;
        return output;
    }

    private static float Allpass(float[] buf, ref int pos, float input)
    {
        float bufOut = buf[pos];
        float output = bufOut - input;
        float w = input + bufOut * 0.5f;
        buf[pos] = MathF.Abs(w) < AudioMath.DenormalFloor ? 0f : w;
        if (++pos >= buf.Length) pos = 0;
        return output;
    }
}
