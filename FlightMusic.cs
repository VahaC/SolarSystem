namespace SolarSystem;

// PROTOTYPE (2026-11-17): arcade flight mode — sound: the generative soundtrack that
// follows the flight (a slow groove in free flight, an energetic track during a race).
//
// Musical plan (v2 — the first version was judged "boring, monotonous"):
//  * 120 BPM, 4/4, counted in samples: a sixteenth is exactly 6000 samples at 48 kHz.
//  * Key of A minor; chords Am F C G (main), F G Em Am, Dm F C G, Am Em F G (free-flight
//    variations), F F G G (the race's break / lift). Every pitched note is in A natural
//    minor; bells and the counter-line use only A minor pentatonic chord tones.
//  * The hook (8 bars over Am F C G Am F C G, written around A4):
//      A C E A' G E | F' E C A | G C E G E D | D B G D E ||
//      E A' G E C D | C A C F' E | E G C'' B' G E | D B D G' E
//  * RACE (one chord per bar, 4-bar blocks, fills at the end of every block):
//      bars 0-3  DROP   kick on every beat, clap/snare on 2 and 4, eighth hats, a driving
//                       bass with octave jumps and passing notes, off-beat chord stabs,
//                       a crash on the start signal; the 16th arpeggio enters at bar 2.
//      bars 4-   MAIN   + the lead hook, sixteenth hats with accents, arpeggio pattern
//                       changes every block.
//      BREAK (the first block boundary with RaceProgress01 >= 0.4): two bars of pads and
//                       a dark arpeggio over F, then two bars of lift over G — snare roll
//                       accelerating 8ths -> 16ths, noise riser, filters opening.
//      FINAL (after the break, or from progress 0.72): all in — crash, rolling 16th bass,
//                       open hats on the off-beats, ghost snares, the hook doubled an
//                       octave up, brightest filters, a crash every 8 bars.
//    Combo layers: >= 3 a 16th shaker, >= 6 the lead gains its octave double, >= 10 a
//    bell counter-line, >= 15 open hats everywhere; every 5th gate in a row crashes on
//    the next downbeat. A miss (combo back to 0) removes all of them on the next step.
//    Boost opens every filter and adds a rising noise sweep. The lead ducks for a moment
//    whenever a gate is passed, so the gate chime stays on top.
//  * COUNTDOWN: the groove stops; Am pad darkens then opens with a noise riser; a snare
//    build on beats -> 8ths -> 16ths -> 32nds lands on the start, where the race restarts
//    the song on its downbeat (the drop).
//  * FINISHED: on the next bar a big Am hit with a crash, then a 4-bar outro scaled by
//    the medal: gold restates the hook and ends on a high A with a cymbal swell, silver
//    a shorter phrase, bronze one bar of it, none just the chords. Then free flight.
//  * FREE FLIGHT: a 64-bar cycle of eight 8-bar sections (two bars per chord): sparse
//    intro, half-time kick and shaker, hook fragments, chord variations, a breakdown with
//    bells, a fuller groove with a fill into the next cycle; slow filter movement.
// Changes happen on step / bar boundaries; every note has an envelope; gains are
// smoothed; the bass and pads are side-chained to the kick for pump.

/// <summary>The adaptive music, driven by <see cref="FlightSoundState"/>. Added to the
/// mixer when flight mode is first entered and kept for the app's lifetime; it fades to
/// silence by itself whenever <see cref="FlightSoundState.Active"/> is 0.</summary>
public sealed class FlightMusic : IAudioSource
{
    // ---- timing ----------------------------------------------------------------------
    /// <summary>Tempo in beats per minute.</summary>
    internal const float Bpm = 120f;
    /// <summary>Samples per sixteenth note (exact at 120 BPM and 48 kHz).</summary>
    internal const int StepSamples = AudioFormat.SampleRate * 60 / 120 / 4;
    internal const int StepsPerBar = 16;
    internal const int BarSamples = StepSamples * StepsPerBar;

    // ---- harmony -----------------------------------------------------------------------
    /// <summary>Chord names, indexed by the chord numbers used everywhere below.</summary>
    internal static readonly string[] ChordNames = ["Am", "F", "C", "G", "Em", "Dm"];
    /// <summary>Pitch classes (0 = C) of each chord.</summary>
    internal static readonly int[][] ChordPitchClasses = [[9, 0, 4], [5, 9, 0], [0, 4, 7], [7, 11, 2], [4, 7, 11], [2, 5, 9]];
    /// <summary>The race's main progression, one chord per bar.</summary>
    internal static readonly int[] RaceMain = [0, 1, 2, 3];
    /// <summary>The race's break and lift, one chord per bar.</summary>
    internal static readonly int[] RaceBreak = [1, 1, 3, 3];
    private static readonly int[][] FreeProgressions = [[0, 1, 2, 3], [1, 3, 4, 0], [5, 1, 2, 3], [0, 4, 1, 3]];
    /// <summary>Which progression each 8-bar free-flight section uses.</summary>
    private static readonly int[] FreeSectionProg = [0, 0, 0, 1, 2, 0, 3, 0];
    private static readonly float[] FreeSectionBright = [0.25f, 0.4f, 0.55f, 0.5f, 0.2f, 0.6f, 0.5f, 0.75f];
    private static readonly int[] FreeSectionArp = [0, 0, 1, 2, 1, 3, 2, 1];

    /// <summary>Pad voicing per chord (MIDI, three close tones around C4).</summary>
    private static readonly int[][] PadVoicing = [[57, 60, 64], [57, 60, 65], [55, 60, 64], [55, 59, 62], [55, 59, 64], [57, 62, 65]];
    /// <summary>Bass roots per chord (MIDI, octave 2).</summary>
    private static readonly int[] BassRoots = [45, 41, 48, 43, 40, 38];
    /// <summary>Arpeggio tones per chord (MIDI, octave 3-4).</summary>
    private static readonly int[][] ArpTones = [[57, 60, 64, 69], [57, 60, 65, 69], [60, 64, 67, 72], [59, 62, 67, 71], [59, 64, 67, 71], [57, 62, 65, 69]];
    private static readonly int[][] ArpPatterns =
    [
        [0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3],
        [0, 1, 2, 3, 2, 1, 0, 1, 2, 3, 2, 1, 0, 1, 2, 3],
        [0, 2, 1, 3, 2, 0, 3, 1, 0, 2, 1, 3, 2, 3, 1, 2],
        [0, 0, 2, 1, 3, 3, 2, 1, 0, 0, 3, 2, 1, 3, 2, 3],
    ];
    /// <summary>Bell / counter-line notes per chord: chord tones in A minor pentatonic.</summary>
    private static readonly int[][] BellTones = [[76, 81, 84], [72, 81, 84], [76, 79, 84], [74, 79, 86], [76, 79, 88], [74, 81, 86]];

    /// <summary>The hook: per bar, (step, MIDI note, length in steps) triples.</summary>
    internal static readonly int[][] Hook =
    [
        [0, 69, 2, 2, 72, 2, 4, 76, 3, 7, 81, 5, 12, 79, 2, 14, 76, 2],
        [0, 77, 6, 6, 76, 2, 8, 72, 4, 12, 69, 4],
        [0, 67, 2, 2, 72, 2, 4, 76, 3, 7, 79, 5, 12, 76, 2, 14, 74, 2],
        [0, 74, 6, 6, 71, 2, 8, 67, 4, 12, 74, 2, 14, 76, 2],
        [0, 76, 2, 2, 81, 4, 6, 79, 2, 8, 76, 4, 12, 72, 2, 14, 74, 2],
        [0, 72, 4, 4, 69, 2, 6, 72, 2, 8, 77, 4, 12, 76, 4],
        [0, 76, 2, 2, 79, 2, 4, 84, 4, 8, 83, 2, 10, 79, 2, 12, 76, 4],
        [0, 74, 4, 4, 71, 2, 6, 74, 2, 8, 79, 6, 14, 76, 2],
    ];
    private static readonly int[] SilverEnding = [0, 81, 12];
    private static readonly int[] GoldEnding = [0, 81, 16];

    // Bass patterns: (step, offset, length) triples; offset 0 root, 7 fifth, 12 octave, Pass = passing note.
    private const int Pass = 100;
    private static readonly int[] BassDrive = [0, 0, 2, 3, 0, 1, 4, 12, 2, 6, 0, 1, 8, 0, 2, 10, 12, 1, 11, 0, 1, 12, 0, 2, 14, Pass, 2];
    private static readonly int[] BassRoll = [0, 0, 1, 2, 12, 1, 3, 0, 1, 4, 0, 1, 6, 12, 1, 7, 0, 1, 8, 0, 1, 10, 12, 1, 11, 0, 1, 12, 7, 1, 13, 0, 1, 14, 12, 1, 15, Pass, 1];
    private static readonly int[] BassEighths = [0, 0, 1, 2, 0, 1, 4, 12, 1, 6, 0, 1, 8, 0, 1, 10, 12, 1, 12, 0, 1, 14, Pass, 1];
    private static readonly int[] BassHalf = [0, 0, 8, 8, 7, 6, 14, 12, 2];
    private static readonly int[] BassHalfB = [0, 0, 8, 8, 7, 4, 12, 12, 4];
    private static readonly int[] BassHalfPass = [0, 0, 8, 8, 7, 4, 12, 12, 2, 14, Pass, 2];
    private static readonly int[] BassWalk = [0, 0, 3, 3, 0, 3, 6, 12, 2, 10, 7, 3, 14, Pass, 2];
    private static readonly int[] BassWalkB = [0, 0, 3, 3, 12, 3, 6, 7, 2, 8, 0, 4, 12, 7, 2, 14, Pass, 2];
    private static readonly int[] BassWhole = [0, 0, 15];
    private static readonly int[] BassLong = [0, 0, 32];
    private static readonly int[] OutroProgMedal = [0, 1, 3, 0];
    private static readonly int[] OutroProgNone = [0, 1, 0, 0];

    private static readonly int[] NaturalMinor = [9, 11, 0, 2, 4, 5, 7];

    /// <summary>What a traced note was played by (tests only).</summary>
    internal enum NoteKind { Chord, Section, Pad, Bell, Pulse, Bass, Arp, Lead, Stab, Counter, Kick, Snare, Hat, OpenHat, Shaker, Tom, Crash }

    /// <summary>Section ids traced with <see cref="NoteKind.Section"/>: 0..7 free-flight
    /// sections, then the race / countdown / outro sections below.</summary>
    internal const int SecDrop = 10, SecMain = 11, SecBreak = 12, SecFinal = 13, SecCountdown = 20, SecOutro = 30;

    /// <summary>Called for every scheduled note: kind, MIDI note (chord index for
    /// <see cref="NoteKind.Chord"/>, section id for <see cref="NoteKind.Section"/>, -1 for
    /// unpitched drums) and the sample (frame) index since this instance was created.
    /// For tests; null in the game.</summary>
    internal Action<NoteKind, int, long>? NoteTrace;

    /// <summary>The lead's current gate-duck gain (1 = no duck). For tests.</summary>
    internal float LeadDuck => _gateDuck.Value;

    // ---- voices -------------------------------------------------------------------------
    private struct PadVoice
    {
        public int Note;
        public bool Held;
        public float Hz, Pan;
        public Oscillator O1, O2;
        public Envelope Env;
    }

    private struct BellVoice
    {
        public float Hz, Level, Attack, Gain, Decay;
        public Oscillator O1, O2, O3;
        public bool On;
    }

    private struct PluckVoice
    {
        public float Hz, Pan, Vel;
        public Oscillator O1, O2;
        public Envelope Env;
    }

    private const int PadVoices = 12;
    private const int BellVoices = 4;
    private const int ArpVoices = 3;
    private const int StabVoices = 3;

    private readonly FlightSoundState _state;
    private readonly PadVoice[] _pad = new PadVoice[PadVoices];
    private readonly BellVoice[] _bells = new BellVoice[BellVoices];
    private readonly PluckVoice[] _arp = new PluckVoice[ArpVoices];
    private readonly PluckVoice[] _stab = new PluckVoice[StabVoices];
    private readonly Reverb _reverb = new() { RoomSize = 0.8f, Damping = 0.55f };
    private readonly DelayLine _echo = new(StepSamples * 3 + 1);
    private Noise _rng = new(0x5EED1234u);
    private Noise _nHat = new(0x0BADC0DEu), _nSnare = new(0x51A2E0F1u), _nCrashL = new(0xC0A5A1u), _nCrashR = new(0xC0A5A2u), _nRiser = new(0x7153E7u), _nShake = new(0x5A4E7u);

    private Biquad _padLpL, _padLpR, _bassLp, _arpLp, _stabLp, _leadLp;
    private Biquad _hatHp, _ohatHp, _shakeBp, _snBp, _snHp, _crashHpL, _crashHpR, _riserBp;

    // Bass (mono, gliding).
    private Oscillator _bassO1, _bassO2;
    private Envelope _bassEnv = new(0.003f, 0.25f, 0.6f, 0.05f);
    private int _bassGate;
    private float _bassHz = 110f, _bassHzNow = 110f, _bassVel = 1f, _bassFEnv;

    // Lead (mono, gliding, with an octave-up double).
    private Oscillator _leadO1, _leadO2, _leadO3, _leadO4;
    private Envelope _leadEnv = new(0.006f, 0.35f, 0.7f, 0.14f);
    private int _leadGate, _leadAge;
    private float _leadHz = 440f, _leadHzNow = 440f, _leadVel = 1f, _leadFEnv, _vibPhase, _vibMul = 1f;

    // Drums.
    private Oscillator _kickOsc, _kickClick, _snOsc, _tomOsc, _pulseO1, _pulseO2;
    private int _kickT = int.MaxValue / 2, _snT = int.MaxValue / 2, _hatT = int.MaxValue / 2, _ohatT = int.MaxValue / 2;
    private int _shakeT = int.MaxValue / 2, _tomT = int.MaxValue / 2, _crashT = int.MaxValue / 2, _pulseT = int.MaxValue / 2;
    private float _kickVel, _snVel, _hatVel, _ohatVel, _ohatChoke = 1f, _shakeVel, _tomVel, _tomHz = 110f, _crashVel, _crashAttack = 48f;
    private int _halfSnare = -1;   // countdown 32nd: a snare due this many samples from now

    private int _arpNext;

    // ---- sequencer ------------------------------------------------------------------------
    private long _sample;          // samples since the song (re)started
    private long _frame;           // samples since construction (trace time base)
    private int _mode;             // musical mode: 0 free, 1 countdown, 2 race, 3 finished (outro)
    private int _lastPhase;
    private bool _finishPending;
    private int _chord = -1;
    private int _section = -1, _sectionStart;
    private bool _breakDone;
    private int _medal;
    private float _cdTotal = 3f, _cdFrac;
    private int _lastCombo;
    private bool _comboCrash;
    private int _gateDuckT;
    private int _lastBellStep = -100;
    private float _bright = 0.3f;  // target filter brightness of the current section (0..1)
    private float _riserTarget, _riserHzTarget = 500f;

    // ---- smoothed controls ---------------------------------------------------------------
    private float _active;         // 0 .. 1 linear fade
    private float _pause = 1f;
    private Smooth _padGain, _brightS, _intensity, _boost, _wet, _riserGain, _riserHz, _gateDuck, _approach, _stabGain, _leadUp, _drumGain;
    private float _lfoPhase;
    private bool _asleep = true;
    private bool _released;
    private int _silentSamples;
    private int _combo;

    private static readonly float ActiveStep = 1f / AudioFormat.SampleRate;          // 1 s fade
    private static readonly float PauseCoeff = AudioMath.SmoothCoeff(0.12f);
    private static readonly float GainCoeff = AudioMath.SmoothCoeff(0.4f);
    private static readonly float FastCoeff = AudioMath.SmoothCoeff(0.04f);
    private static readonly float SlowCoeff = AudioMath.SmoothCoeff(1.5f);
    private static readonly float BoostCoeff = AudioMath.SmoothCoeff(0.25f);
    private static readonly float GlideCoeff = AudioMath.SmoothCoeff(0.018f);
    private const int ControlBlock = 32;
    private const float PausedGain = 0.02f;       // -34 dB

    // Layer levels (tuned by measurement against the targets in the brief).
    private const float PadLevel = 0.046f;
    private const float StabLevel = 0.085f;
    private const float BellLevel = 0.045f;
    private const float CounterLevel = 0.03f;
    private const float PulseLevel = 0.1f;
    private const float KickLevel = 0.25f;
    private const float SnareLevel = 0.12f;
    private const float HatLevel = 0.06f;
    private const float OpenHatLevel = 0.03f;
    private const float ShakerLevel = 0.05f;
    private const float TomLevel = 0.16f;
    private const float CrashLevel = 0.045f;
    private const float RiserLevel = 0.26f;
    private const float BassLevel = 0.17f;
    private const float ArpLevel = 0.075f;
    private const float LeadLevel = 0.056f;
    private const float ReverbReturn = 0.8f;
    private static readonly int PumpSamples = (int)(0.24f * AudioFormat.SampleRate);
    private static readonly int GateDuckSamples = (int)(0.35f * AudioFormat.SampleRate);

    /// <summary>Creates the music for <paramref name="state"/>. Allocates everything it will
    /// ever need here, so <see cref="Mix"/> never allocates.</summary>
    public FlightMusic(FlightSoundState state)
    {
        _state = state;
        for (int i = 0; i < PadVoices; i++) _pad[i].Env = new Envelope(1.2f, 1f, 1f, 2.2f);
        for (int i = 0; i < ArpVoices; i++) _arp[i].Env = new Envelope(0.002f, 0.5f, 0f, 0.08f);
        for (int i = 0; i < StabVoices; i++) _stab[i].Env = new Envelope(0.002f, 0.45f, 0f, 0.06f);
        _padGain.Value = 1f;
        _brightS.Value = 0.3f;
        _wet.Value = 1f;
        _gateDuck.Value = 1f;
        _drumGain.Value = 1f;
        _riserHz.Value = 500f;
        _hatHp.SetHighPass(7500f, 0.7f);
        _ohatHp.SetHighPass(6000f, 0.7f);
        _shakeBp.SetBandPass(6500f, 1.4f);
        _snBp.SetBandPass(1900f, 0.8f);
        _snHp.SetHighPass(5000f, 0.7f);
        _crashHpL.SetHighPass(3500f, 0.7f);
        _crashHpR.SetHighPass(3700f, 0.7f);
        _riserBp.SetBandPass(500f, 2f);
        _lastPhase = 0;
    }

    /// <inheritdoc/>
    public bool Mix(Span<float> buffer, int frames)
    {
        frames = Math.Min(frames, buffer.Length / 2);
        if (frames <= 0) return true;

        float targetActive = Finite(_state.Active) > 0.5f ? 1f : 0f;
        if (_asleep)
        {
            if (targetActive == 0f) { _frame += frames; return true; }
            WakeUp();
        }
        else if (_released && targetActive == 1f) WakeUp();   // back on while the tails drain

        float targetPause = Finite(_state.Paused) > 0.5f ? PausedGain : 1f;
        int phase = _state.RacePhase;
        if (phase < 0 || phase > 3) phase = 0;
        float progress = Math.Clamp(Finite(_state.RaceProgress01), 0f, 1f);
        float boost = Math.Clamp(Finite(_state.Boost01), 0f, 1f);
        float approach = Math.Clamp(Finite(_state.GateApproach01), 0f, 1f);
        // Combo rises by one in the very frame a gate is passed (the finish gate too, so
        // phase 3 counts); a jump of several gates ducks once, a reset to 0 never ducks.
        _combo = phase is 2 or 3 ? Math.Clamp(_state.Combo, 0, 99) : 0;
        if (_combo > _lastCombo)
        {
            _gateDuckT = GateDuckSamples;                       // a gate was just passed
            if (_combo / 5 > _lastCombo / 5) _comboCrash = true;
        }
        _lastCombo = _combo;

        if (phase != _lastPhase) OnPhaseChange(phase);

        for (int start = 0; start < frames; start += ControlBlock)
        {
            int n = Math.Min(ControlBlock, frames - start);
            UpdateControls(progress, boost, approach, n);
            for (int i = 0; i < n; i++)
            {
                if (!_released)
                {
                    int inStep = (int)(_sample % StepSamples);
                    if (inStep == 0) OnStep(progress);
                    if (_halfSnare >= 0 && --_halfSnare < 0) { HitSnare(_snVel); }
                }

                RenderSample(out float l, out float r);

                _active += Math.Clamp(targetActive - _active, -ActiveStep, ActiveStep);
                _pause += (targetPause - _pause) * PauseCoeff;
                float g = _active * _active * _pause;
                l *= g; r *= g;
                if (!float.IsFinite(l)) l = 0f;
                if (!float.IsFinite(r)) r = 0f;
                int k = (start + i) * 2;
                buffer[k] += Ceiling(l);
                buffer[k + 1] += Ceiling(r);
                _sample++;
                _frame++;
            }
        }

        if (targetActive == 0f && _active == 0f)
        {
            if (!_released) { ReleaseAll(); _released = true; }
            _silentSamples += frames;
            if (_silentSamples > 5 * AudioFormat.SampleRate) _asleep = true;   // tails drained
        }
        else _silentSamples = 0;
        return true;
    }

    private static float Finite(float v) => float.IsFinite(v) ? v : 0f;

    internal const float CeilingKnee = 0.3f;    // -10.5 dBFS: below this the signal is untouched
    internal const float CeilingMax = 0.44f;    // -7.1 dBFS: never exceeded

    /// <summary>Safety ceiling: identity below the knee, a smooth tanh shoulder above it
    /// that never exceeds <see cref="CeilingMax"/>. Normal playing only rarely reaches it.</summary>
    private static float Ceiling(float x)
    {
        float a = MathF.Abs(x);
        if (a <= CeilingKnee) return x;
        const float room = CeilingMax - CeilingKnee;
        float y = CeilingKnee + room * MathF.Tanh((a - CeilingKnee) / room);
        return x < 0f ? -y : y;
    }

    private void WakeUp()
    {
        _asleep = false;
        _released = false;
        _silentSamples = 0;
        _active = 0f;
        _chord = -1;
        _section = -1;
        _sample = 0;
        int phase = _state.RacePhase;
        _mode = phase is 1 or 2 ? phase : 0;
        _lastPhase = phase is >= 0 and <= 3 ? phase : 0;
        _finishPending = false;
        _breakDone = false;
        if (_mode == 1) _cdTotal = Math.Clamp(Finite(_state.Countdown), 0.5f, 5f);
    }

    private void OnPhaseChange(int phase)
    {
        switch (phase)
        {
            case 1:
                _mode = 1;                // the countdown restarts the song on Am
                _sample = 0;
                _section = -1;
                _finishPending = false;
                _cdTotal = Math.Clamp(Finite(_state.Countdown), 0.5f, 5f);
                break;
            case 2:
                _mode = 2;                // the start signal is the drop: bar 1 of the race
                _sample = 0;
                _section = -1;
                _breakDone = false;
                _halfSnare = -1;
                _chord = -1;              // re-announce Am (held pad notes stay held)
                _finishPending = false;
                break;
            case 3:
                _finishPending = true;    // the outro starts on the next bar
                break;
            default:
                if (_mode != 0 || _finishPending) { _mode = 0; _sample = 0; _section = -1; }
                _finishPending = false;
                break;
        }
        _lastPhase = phase;
    }

    private void UpdateControls(float progress, float boost, float approach, int n)
    {
        // Smooth over a control block: apply the per-sample coefficient n times.
        float k = n;
        float gc = 1f - MathF.Pow(1f - GainCoeff, k);
        float fc = 1f - MathF.Pow(1f - FastCoeff, k);
        float sc = 1f - MathF.Pow(1f - SlowCoeff, k);
        float bc = 1f - MathF.Pow(1f - BoostCoeff, k);
        bool race = _mode == 2;
        float intensity = _intensity.Next(race ? progress : 0f, sc);
        float b = _boost.Next(race ? boost : boost * 0.3f, bc);
        float appr = _approach.Next(race ? approach : 0f, fc);
        float bright = _brightS.Next(_bright, sc);

        _lfoPhase += k / (29f * AudioFormat.SampleRate);
        if (_lfoPhase >= 1f) _lfoPhase -= 1f;
        float lfo = MathF.Sin(AudioMath.TwoPi * _lfoPhase);

        // Riser: countdown build, the race's lift, boost.
        float riser = _riserTarget, riserHz = _riserHzTarget;
        if (race) { riser = MathF.Max(riser, 0.55f * b); riserHz = MathF.Max(riserHz, 1200f + 5000f * b); }
        float rg = _riserGain.Next(riser, riser > _riserGain.Value ? gc : fc);
        float rh = _riserHz.Next(riserHz, fc);
        _riserBp.SetBandPass(rh, 2.2f);
        _ = rg;

        float padGain = _mode switch
        {
            1 => 0.5f + 0.9f * _cdFrac,
            2 => _section == SecBreak ? 1.2f : 0.55f,
            3 => 1.1f,
            _ => 1f,
        };
        _padGain.Next(padGain, gc);
        float padCut = 500f + 2200f * bright + 1200f * b + (race ? 0f : 250f * lfo);
        _padLpL.SetLowPass(padCut, 0.7f);
        _padLpR.SetLowPass(padCut * 1.05f, 0.7f);
        _stabLp.SetLowPass(900f + 3800f * bright + 1500f * b, 1.1f);
        _stabGain.Next(race ? 1f : 0f, gc);

        _bassFEnv *= MathF.Pow(0.9993f, k);
        _bassLp.SetLowPass(220f + 500f * bright + 1300f * _bassFEnv + 400f * b, 1.6f);

        _arpLp.SetLowPass(700f + 3200f * bright + 1500f * appr * (race ? 1f : 0f) + 1800f * b + 300f * intensity, 1.3f);

        _leadFEnv *= MathF.Pow(0.99985f, k);
        _leadLp.SetLowPass(1100f + 2400f * bright + 2500f * _leadFEnv + 1500f * b, 1.2f);
        _leadAge += n;
        _vibPhase += k * 5.2f / AudioFormat.SampleRate;
        if (_vibPhase >= 1f) _vibPhase -= 1f;
        float vibDepth = Math.Clamp((_leadAge - 0.18f * AudioFormat.SampleRate) / (0.3f * AudioFormat.SampleRate), 0f, 1f) * 0.0045f;
        _vibMul = 1f + vibDepth * MathF.Sin(AudioMath.TwoPi * _vibPhase);

        float upTarget = _mode switch
        {
            2 => _section == SecFinal ? 0.55f : (_combo >= 6 ? 0.35f : 0f),
            3 => _medal >= 3 ? 0.6f : 0f,
            _ => 0f,
        };
        _leadUp.Next(upTarget, fc);

        if (_gateDuckT > 0) _gateDuckT -= n;
        _gateDuck.Next(_gateDuckT > 0 ? 0.5f : 1f, fc);
        _drumGain.Next(_mode == 0 ? 0.7f : 1f, gc);
        _wet.Next(race ? 0.75f : 1f, gc);
    }

    // ---- sequencer ------------------------------------------------------------------------

    private void OnStep(float progress)
    {
        int step = (int)(_sample / StepSamples);
        int inBar = step % StepsPerBar;
        if (inBar == 0 && _finishPending)
        {
            _finishPending = false;
            _mode = 3;
            _medal = Math.Clamp(_state.Medal, 0, 3);
            _sample = 0;
            step = 0;
            _section = -1;
        }
        int bar = step / StepsPerBar;

        switch (_mode)
        {
            case 0: StepFree(bar, inBar, step); break;
            case 1: StepCountdown(bar, inBar); break;
            case 2: StepRace(bar, inBar, progress); break;
            default: StepOutro(bar, inBar); break;
        }
    }

    private void EnterSection(int id, int bar)
    {
        if (_section == id) return;
        _section = id;
        _sectionStart = bar;
        Trace(NoteKind.Section, id);
    }

    // ---- free flight ----------------------------------------------------------------------

    private void StepFree(int bar, int inBar, int step)
    {
        int sec = (bar / 8) % 8;
        int barIn = bar % 8;
        if (inBar == 0)
        {
            if (barIn == 0) { _section = -1; EnterSection(sec, bar); }
            _bright = FreeSectionBright[sec];
            _riserTarget = 0f;
        }
        int chord = FreeProgressions[FreeSectionProg[sec]][barIn / 2];
        if (chord != _chord) SetChord(chord);
        int nextChord = FreeProgressions[FreeSectionProg[(barIn == 7 ? sec + 1 : sec) % 8]][((barIn + 1) % 8) / 2];

        bool groove = sec is not 0 and not 4;
        if (groove)
        {
            if (inBar == 0) HitKick(0.75f);
            if (inBar == 10) HitKick(0.5f);
            if (inBar == 8 && sec is 3 or 5 or 7) HitSnare(0.35f);
            if (inBar % 4 == 2) HitShaker(0.6f);
            if (sec >= 5 && inBar % 2 == 1) HitShaker(0.25f);
            if (sec is 6 or 7 && inBar % 4 == 2) HitHat(0.45f);
            if (sec == 7 && barIn == 7 && inBar >= 12) HitTom(inBar < 14 ? 52 : 45, 0.6f);
            if (sec == 3 && barIn == 7 && inBar >= 14) HitSnare(0.3f);
        }

        // Bass.
        // Bass: the second bar of each chord answers the first.
        bool odd = barIn % 2 == 1;
        int[] bassPat = sec is 0 or 1 or 4
            ? (odd ? (sec == 0 ? BassHalfB : BassHalfPass) : BassHalf)
            : (odd ? BassWalkB : BassWalk);
        PlayBass(bassPat, inBar, nextChord, 0.75f);

        // Arpeggio: eighths, sixteenths in the last section.
        bool sixteenths = sec == 7;
        if (sixteenths || inBar % 2 == 0)
        {
            int[] pat = ArpPatterns[FreeSectionArp[sec]];
            int note = ArpTones[_chord][pat[inBar]] + (odd && inBar >= 8 ? 12 : 0);   // climbs in the answering bar
            PlayArp(note, sec == 4 ? 0.5f : 0.75f, inBar);
        }

        // Hook fragments.
        if (sec == 2 && barIn is 0 or 2) PlayHookBar(barIn / 2, inBar, 0, 0.7f);
        if (sec == 5 && barIn is 0 or 2) PlayHookBar(4 + barIn / 2, inBar, 0, 0.7f);
        if (sec == 7 && barIn is 4 or 6) PlayHookBar(barIn / 2, inBar, 0, 0.75f);

        // Bells: sparse, more in the breakdown.
        if (inBar % 4 == 0 && step - _lastBellStep >= 16)
        {
            float chance = sec is 4 or 6 ? 0.45f : 0.12f;
            if (_rng.Unit() < chance)
            {
                int[] tones = BellTones[_chord];
                TriggerBell(tones[(int)(_rng.Unit() * tones.Length) % tones.Length], 0.8f + 0.2f * _rng.Unit(), BellDecaySlow, NoteKind.Bell);
                _lastBellStep = step;
            }
        }
    }

    // ---- countdown ------------------------------------------------------------------------

    private void StepCountdown(int bar, int inBar)
    {
        if (bar == 0 && inBar == 0) EnterSection(SecCountdown, 0);
        if (_chord != 0) SetChord(0);
        float rem = Math.Clamp(Finite(_state.Countdown), 0f, _cdTotal);
        float frac = 1f - rem / _cdTotal;
        _cdFrac = frac;
        _bright = 0.1f + 0.8f * frac;
        _riserTarget = 0.2f + 0.8f * frac * frac;
        _riserHzTarget = 300f * MathF.Pow(20f, frac);

        if (inBar % 4 == 0 && rem > 1f)
        {
            _pulseT = 0; _pulseO1.Phase = 0f; _pulseO2.Phase = 0f;
            Trace(NoteKind.Pulse, 33);
        }
        bool hit = rem > 2f ? inBar % 4 == 0 : rem > 1f ? inBar % 2 == 0 : true;
        float vel = 0.25f + 0.85f * frac;
        if (hit) HitSnare(vel);
        if (rem <= 0.5f) { _snVel = vel; _halfSnare = StepSamples / 2; }   // 32nds in the last half second
    }

    // ---- race -------------------------------------------------------------------------------

    private void StepRace(int bar, int inBar, float progress)
    {
        if (inBar == 0 && bar % 4 == 0)
        {
            int block = bar / 4;
            int sec;
            if (block == 0) sec = SecDrop;
            else if (_section is SecFinal) sec = SecFinal;
            else if (_section is SecBreak) { sec = SecFinal; _breakDone = true; }
            else if (progress >= 0.72f) sec = SecFinal;
            else if (block >= 2 && progress >= 0.4f && !_breakDone) sec = SecBreak;
            else sec = SecMain;
            if (sec != _section && sec is SecFinal or SecDrop) HitCrash(1f, 48f);
            EnterSection(sec, bar);
        }
        if (inBar == 0)
        {
            if (_comboCrash) { _comboCrash = false; HitCrash(0.7f, 48f); }
            if (_section == SecFinal && bar > _sectionStart && (bar - _sectionStart) % 8 == 0) HitCrash(0.8f, 48f);
        }

        int barIn = bar - _sectionStart;
        bool brk = _section == SecBreak;
        bool lift = brk && barIn >= 2;
        bool final = _section == SecFinal;
        int chord = brk ? RaceBreak[barIn % 4] : RaceMain[bar % 4];
        if (chord != _chord) SetChord(chord);
        int nextChord = brk ? (barIn % 4 == 3 ? 0 : RaceBreak[(barIn + 1) % 4]) : RaceMain[(bar + 1) % 4];

        // Brightness and the lift's riser.
        _bright = _section switch
        {
            SecDrop => 0.55f,
            SecMain => 0.65f + 0.15f * progress,
            SecBreak => lift ? 0.3f + 0.6f * ((barIn - 2) * 16 + inBar) / 32f : 0.2f,
            _ => 1f,
        };
        if (lift)
        {
            float f = ((barIn - 2) * 16 + inBar) / 32f;
            _riserTarget = 0.3f + 0.7f * f;
            _riserHzTarget = 400f * MathF.Pow(20f, f);
        }
        else { _riserTarget = 0f; _riserHzTarget = 500f; }

        // Drums.
        bool fillBar = bar % 4 == 3 && !brk;
        if (!brk)
        {
            if (inBar % 4 == 0) HitKick(1f);
            if (inBar is 4 or 12) HitSnare(1f);
            if (final && inBar == 15 && !fillBar) HitSnare(0.3f);
            if (fillBar)
            {
                if ((bar / 4) % 2 == 0) { if (inBar >= 13) HitSnare(0.45f + 0.15f * (inBar - 12)); }
                else if (inBar is 8 or 10 or 13 or 14 or 15) HitTom(inBar <= 10 ? 52 : inBar == 13 ? 48 : 45, 0.8f);
            }
        }
        else if (lift)
        {
            bool roll = barIn == 2 ? inBar % 2 == 0 : true;
            if (roll) HitSnare(0.3f + 0.6f * ((barIn - 2) * 16 + inBar) / 32f);
            if (barIn == 2 && inBar == 0) HitKick(0.8f);
        }

        bool openOff = final || _combo >= 15;
        if (!brk || lift)
        {
            if (openOff && inBar % 4 == 2) HitOpenHat(0.9f);
            else if (_section == SecDrop || lift) { if (inBar % 2 == 0) HitHat(inBar % 4 == 2 ? 1f : 0.45f); }
            else
            {
                float v = (inBar % 4) switch { 0 => 0.55f, 1 => 0.25f, 2 => 1f, _ => 0.35f };
                HitHat(v);
            }
            if (_combo >= 3 && inBar % 2 == 1) HitShaker(inBar % 4 == 3 ? 0.8f : 0.5f);
        }

        // Bass.
        int[] bassPat = _section switch
        {
            SecBreak => lift ? BassEighths : BassWhole,
            SecFinal => BassRoll,
            _ => BassDrive,
        };
        PlayBass(bassPat, inBar, nextChord, 1f);

        // Off-beat chord stabs.
        if (!brk && inBar % 4 == 2) PlayStab(final ? 1f : 0.8f);

        // Arpeggio.
        if (_section == SecDrop)
        {
            if (bar >= 2) PlayArp(ArpTones[_chord][ArpPatterns[0][inBar]], 0.8f, inBar);
        }
        else if (brk)
        {
            if (lift || inBar % 2 == 0) PlayArp(ArpTones[_chord][ArpPatterns[1][inBar]], lift ? 0.8f : 0.6f, inBar);
        }
        else
        {
            int pat = final ? 3 : 1 + (bar / 4) % 2;
            int note = ArpTones[_chord][ArpPatterns[pat][inBar]];
            if (final && inBar % 2 == 1) note += 12;
            PlayArp(note, 0.9f, inBar);
        }

        // Lead hook.
        if (_section == SecMain) PlayHookBar((bar - 4) % 8, inBar, 0, 1f);
        else if (final) PlayHookBar(barIn % 8, inBar, 0, 1f);

        // Counter-line at high combos.
        if (_combo >= 10 && !brk && (inBar is 6 or 14 || (final && inBar == 10)))
        {
            int[] tones = BellTones[_chord];
            TriggerBell(tones[(bar + inBar / 4) % tones.Length] - 12, 0.9f, BellDecayFast, NoteKind.Counter);
        }
    }

    // ---- outro ------------------------------------------------------------------------------

    private void StepOutro(int bar, int inBar)
    {
        if (bar == 0 && inBar == 0)
        {
            EnterSection(SecOutro, 0);
            HitCrash(_medal >= 1 ? 1f : 0.6f, 48f);
        }
        if (bar >= 6) { _mode = 0; _sample = 0; _section = -1; StepFree(0, 0, 0); return; }   // settle into free flight
        _riserTarget = 0f;
        _bright = bar < 4 ? 0.6f + 0.1f * _medal : 0.35f;
        int[] prog = _medal >= 1 ? OutroProgMedal : OutroProgNone;
        int chord = bar < 4 ? prog[bar] : 0;
        if (chord != _chord) SetChord(chord);

        if (bar < 4)
        {
            if (inBar == 0 && bar < 3) PlayBass(BassWhole, 0, chord, 1f);
            if (bar == 3 && inBar == 0) PlayBass(BassLong, 0, 0, 1f);
            if (inBar == 0 && (bar == 0 || bar == 3)) PlayStab(1f);
            if (_medal >= 2)
            {
                if (bar < 3 && inBar is 0 or 8) HitKick(inBar == 0 ? 1f : 0.7f);
                if (bar < 3 && inBar == 8) HitSnare(0.8f);
                if (bar == 3 && inBar == 0) { HitKick(1f); HitCrash(0.9f, 48f); }
                if (bar == 2 && inBar == 0 && _medal >= 3) HitCrash(0.8f, 1.7f * AudioFormat.SampleRate);   // swell into the last chord
            }
            else if (_medal == 1) { if (inBar == 0 && bar is 0 or 3) HitKick(0.8f); }
            else if (bar == 0 && inBar == 0) HitKick(0.6f);
        }

        if (_medal >= 3)
        {
            if (bar < 2) PlayHookBar(bar, inBar, 0, 1f);
            else if (bar == 2) PlayHookBar(7, inBar, 0, 1f);
            else if (bar == 3) PlayPhrase(GoldEnding, inBar, 1f);
        }
        else if (_medal == 2)
        {
            if (bar == 0) PlayHookBar(0, inBar, 0, 0.9f);
            else if (bar == 1) PlayPhrase(SilverEnding, inBar, 0.9f);
        }
        else if (_medal == 1 && bar == 0) PlayHookBar(0, inBar, 0, 0.8f);
    }

    // ---- note helpers -------------------------------------------------------------------------

    private void PlayHookBar(int hookBar, int inBar, int transpose, float vel) => PlayPhrase(Hook[hookBar], inBar, vel, transpose);

    private void PlayPhrase(int[] phrase, int inBar, float vel, int transpose = 0)
    {
        for (int i = 0; i + 2 < phrase.Length; i += 3)
            if (phrase[i] == inBar) TriggerLead(phrase[i + 1] + transpose, phrase[i + 2], vel);
    }

    private void PlayBass(int[] pattern, int inBar, int nextChord, float vel)
    {
        for (int i = 0; i + 2 < pattern.Length; i += 3)
        {
            if (pattern[i] != inBar) continue;
            int root = BassRoots[_chord];
            int off = pattern[i + 1];
            int note;
            if (off == Pass)
            {
                int target = BassRoots[nextChord];
                if (target > root) note = Neighbour(target, -1);
                else if (target < root) note = Neighbour(target, +1);
                else note = Neighbour(root, +1);
            }
            else note = root + off;
            _bassHz = AudioMath.MidiToHz(note);
            if (_bassEnv.IsIdle) _bassHzNow = _bassHz;
            _bassEnv.NoteOn();
            _bassGate = pattern[i + 2] * StepSamples - StepSamples / 4;
            _bassVel = vel * (off == 12 ? 0.85f : 1f);
            _bassFEnv = 1f;
            Trace(NoteKind.Bass, note);
        }
    }

    /// <summary>The nearest A natural minor note from <paramref name="note"/> in direction
    /// <paramref name="dir"/> (a diatonic neighbour).</summary>
    private static int Neighbour(int note, int dir)
    {
        int n = note + dir;
        while (Array.IndexOf(NaturalMinor, ((n % 12) + 12) % 12) < 0) n += dir;
        return n;
    }

    private void PlayArp(int note, float vel, int inBar)
    {
        ref PluckVoice v = ref _arp[_arpNext];
        _arpNext = (_arpNext + 1) % ArpVoices;
        v.Hz = AudioMath.MidiToHz(note);
        v.Pan = inBar % 2 == 0 ? -0.4f : 0.4f;
        v.Vel = vel;
        v.Env.Level = MathF.Min(v.Env.Level, 0.3f);
        v.Env.NoteOn();
        Trace(NoteKind.Arp, note);
    }

    private void PlayStab(float vel)
    {
        int[] notes = PadVoicing[_chord];
        for (int n = 0; n < StabVoices && n < notes.Length; n++)
        {
            ref PluckVoice v = ref _stab[n];
            v.Hz = AudioMath.MidiToHz(notes[n] + 12);
            v.Pan = (n - 1) * 0.5f;
            v.Vel = vel;
            v.Env.Level = MathF.Min(v.Env.Level, 0.3f);
            v.Env.NoteOn();
            Trace(NoteKind.Stab, notes[n] + 12);
        }
    }

    private void TriggerLead(int note, int lengthSteps, float vel)
    {
        _leadHz = AudioMath.MidiToHz(note);
        if (_leadEnv.IsIdle) _leadHzNow = _leadHz;
        _leadEnv.NoteOn();
        _leadGate = lengthSteps * StepSamples - StepSamples / 5;
        _leadVel = vel;
        _leadFEnv = 1f;
        _leadAge = 0;
        Trace(NoteKind.Lead, note);
    }

    private void HitKick(float vel) { _kickT = 0; _kickOsc.Phase = 0f; _kickClick.Phase = 0f; _kickVel = vel; Trace(NoteKind.Kick, -1); }
    private void HitSnare(float vel) { _snT = 0; _snOsc.Phase = 0f; _snVel = vel; Trace(NoteKind.Snare, -1); }
    private void HitHat(float vel) { _hatT = 0; _hatVel = vel; if (_ohatT < AudioFormat.SampleRate) _ohatChoke = 0.9985f; Trace(NoteKind.Hat, -1); }
    private void HitOpenHat(float vel) { _ohatT = 0; _ohatVel = vel; _ohatChoke = 1f; Trace(NoteKind.OpenHat, -1); }
    private void HitShaker(float vel) { _shakeT = 0; _shakeVel = vel; Trace(NoteKind.Shaker, -1); }
    private void HitTom(int note, float vel) { _tomT = 0; _tomOsc.Phase = 0f; _tomHz = AudioMath.MidiToHz(note); _tomVel = vel; Trace(NoteKind.Tom, -1); }
    private void HitCrash(float vel, float attackSamples) { _crashT = 0; _crashVel = vel; _crashAttack = attackSamples; Trace(NoteKind.Crash, -1); }

    private void SetChord(int chord)
    {
        _chord = chord;
        Trace(NoteKind.Chord, chord);
        int[] notes = PadVoicing[chord];
        // Release the held notes that are not in the new chord; keep the common tones.
        for (int i = 0; i < PadVoices; i++)
        {
            ref PadVoice v = ref _pad[i];
            if (!v.Held) continue;
            if (Array.IndexOf(notes, v.Note) < 0) { v.Held = false; v.Env.NoteOff(); }
        }
        for (int n = 0; n < notes.Length; n++)
        {
            int note = notes[n];
            bool held = false;
            for (int i = 0; i < PadVoices; i++)
                if (_pad[i].Held && _pad[i].Note == note) { held = true; break; }
            if (held) continue;
            int slot = FindPadSlot(note);
            ref PadVoice v = ref _pad[slot];
            if (v.Env.IsIdle || v.Note != note)
            {
                v.O1.Phase = _rng.Unit();
                v.O2.Phase = _rng.Unit();
            }
            v.Note = note;
            v.Held = true;
            v.Hz = AudioMath.MidiToHz(note);
            v.Pan = (n - 1) * 0.45f;
            v.Env.NoteOn();
            Trace(NoteKind.Pad, note);
        }
    }

    private int FindPadSlot(int note)
    {
        // A releasing voice on the same note is picked up again (no new attack from zero).
        for (int i = 0; i < PadVoices; i++)
            if (!_pad[i].Held && !_pad[i].Env.IsIdle && _pad[i].Note == note) return i;
        for (int i = 0; i < PadVoices; i++)
            if (_pad[i].Env.IsIdle) return i;
        int best = 0;
        float lowest = float.MaxValue;
        for (int i = 0; i < PadVoices; i++)
            if (!_pad[i].Held && _pad[i].Env.Level < lowest) { lowest = _pad[i].Env.Level; best = i; }
        return best;
    }

    private const float BellDecaySlow = 0.99996f;   // about 2.5 s to -40 dB
    private const float BellDecayFast = 0.99985f;   // about 0.7 s

    private void TriggerBell(int note, float gain, float decay, NoteKind kind)
    {
        int slot = 0;
        float lowest = float.MaxValue;
        for (int i = 0; i < BellVoices; i++)
        {
            if (!_bells[i].On) { slot = i; lowest = -1f; break; }
            if (_bells[i].Level < lowest) { lowest = _bells[i].Level; slot = i; }
        }
        ref BellVoice b = ref _bells[slot];
        if (!b.On) { b.O1.Phase = b.O2.Phase = b.O3.Phase = 0f; b.Level = 0f; }
        b.Hz = AudioMath.MidiToHz(note);
        b.Attack = 1f;
        b.Gain = gain * (kind == NoteKind.Counter ? CounterLevel / BellLevel : 1f);
        b.Decay = decay;
        b.On = true;
        Trace(kind, note);
    }

    private void ReleaseAll()
    {
        for (int i = 0; i < PadVoices; i++) { _pad[i].Held = false; _pad[i].Env.NoteOff(); }
        _bassEnv.NoteOff();
        _leadEnv.NoteOff();
        _halfSnare = -1;
        _chord = -1;
    }

    private void Trace(NoteKind kind, int note) => NoteTrace?.Invoke(kind, note, _frame);

    // ---- synthesis ------------------------------------------------------------------------

    private static readonly float KickPitchTau = 0.03f * AudioFormat.SampleRate;
    private static readonly float KickAmpTau = 0.17f * AudioFormat.SampleRate;
    private static readonly int KickLen = AudioFormat.SampleRate * 6 / 10;
    private static readonly float PulseTau = 0.16f * AudioFormat.SampleRate;
    private static readonly float SnareNoiseTau = 0.1f * AudioFormat.SampleRate;
    private static readonly float SnareToneTau = 0.045f * AudioFormat.SampleRate;
    private static readonly int SnareLen = AudioFormat.SampleRate * 6 / 10;
    private static readonly float HatTau = 0.024f * AudioFormat.SampleRate;
    private static readonly float OpenHatTau = 0.2f * AudioFormat.SampleRate;
    private static readonly float ShakerTau = 0.035f * AudioFormat.SampleRate;
    private static readonly float TomTau = 0.16f * AudioFormat.SampleRate;
    private static readonly float CrashTau = 1.3f * AudioFormat.SampleRate;
    private const int Never = int.MaxValue / 2;

    private void RenderSample(out float outL, out float outR)
    {
        // Side-chain pump from the last kick.
        float pump = 1f;
        if (_kickT < PumpSamples)
        {
            float x = (float)_kickT / PumpSamples;
            pump = 1f - 0.75f * (1f - x) * (1f - x) * _kickVel;
        }
        float dg = _drumGain.Value;

        // Pad.
        float padL = 0f, padR = 0f;
        for (int i = 0; i < PadVoices; i++)
        {
            ref PadVoice v = ref _pad[i];
            if (v.Env.IsIdle) continue;
            float e = v.Env.Next();
            float a = v.O1.Next(v.Hz * 0.99654f, OscShape.Saw);   // -6 cents
            float b = v.O2.Next(v.Hz * 1.00347f, OscShape.Saw);   // +6 cents
            float pl = 0.5f - 0.5f * v.Pan, pr = 0.5f + 0.5f * v.Pan;
            padL += (a * 0.65f + b * 0.35f) * e * (0.5f + pl);
            padR += (a * 0.35f + b * 0.65f) * e * (0.5f + pr);
        }
        float pg = _padGain.Value * PadLevel * (0.35f + 0.65f * pump);
        padL = _padLpL.Process(padL) * pg;
        padR = _padLpR.Process(padR) * pg;

        // Off-beat stabs.
        float stabL = 0f, stabR = 0f;
        for (int i = 0; i < StabVoices; i++)
        {
            ref PluckVoice v = ref _stab[i];
            if (v.Env.IsIdle) continue;
            float e = v.Env.Next();
            if (e < 1e-4f && v.Env.Level < 1e-4f) { v.Env.NoteOff(); }
            float s = (v.O1.Next(v.Hz * 0.997f, OscShape.Saw) + v.O2.Next(v.Hz * 1.003f, OscShape.Saw)) * 0.5f * e * v.Vel;
            stabL += s * (0.5f - 0.5f * v.Pan) * 1.4f;
            stabR += s * (0.5f + 0.5f * v.Pan) * 1.4f;
        }
        float stabMono = _stabLp.Process((stabL + stabR) * 0.5f) * StabLevel * _stabGain.Value * (0.6f + 0.4f * pump);
        float stabSide = (stabL - stabR) * 0.5f * StabLevel * _stabGain.Value * 0.5f;
        stabL = stabMono + stabSide;
        stabR = stabMono - stabSide;

        // Bells and the counter-line: fundamental, octave and twelfth (all harmonics in key).
        float bell = 0f;
        for (int i = 0; i < BellVoices; i++)
        {
            ref BellVoice b = ref _bells[i];
            if (!b.On) continue;
            if (b.Attack > 0f)
            {
                b.Level += 1f / (0.004f * AudioFormat.SampleRate);
                if (b.Level >= 1f) { b.Level = 1f; b.Attack = 0f; }
            }
            else
            {
                b.Level *= b.Decay;
                if (b.Level < 1e-4f) { b.On = false; b.Level = 0f; continue; }
            }
            float s = b.O1.Next(b.Hz) + 0.22f * b.O2.Next(b.Hz * 2f) * b.Level + 0.06f * b.O3.Next(b.Hz * 3f) * b.Level;
            bell += s * b.Level * b.Gain;
        }
        bell *= BellLevel;

        // Countdown pulse: A1 + A2 sines.
        float pulse = 0f;
        if (_pulseT < AudioFormat.SampleRate)
        {
            float t = _pulseT++;
            float env = MathF.Min(1f, t / 240f) * MathF.Exp(-t / PulseTau);
            pulse = (_pulseO1.Next(55f) + 0.6f * _pulseO2.Next(110f)) * env * PulseLevel;
        }

        // Kick: sine with a falling pitch plus a short click.
        float kick = 0f;
        if (_kickT < KickLen)
        {
            float t = _kickT;
            float hz = 46f + 120f * MathF.Exp(-t / KickPitchTau);
            float env = MathF.Min(1f, t / 24f) * MathF.Exp(-t / KickAmpTau);
            float click = _kickClick.Next(1700f) * MathF.Exp(-t / 90f) * 0.3f;
            kick = (_kickOsc.Next(hz) + click) * env * _kickVel * KickLevel * dg;
        }
        if (_kickT < Never) _kickT++;

        // Snare / clap: a tone plus band-passed noise with three quick clap strikes.
        float snare = 0f;
        float wn = _nSnare.White();
        float snBp = _snBp.Process(wn), snHp = _snHp.Process(wn);
        if (_snT < SnareLen)
        {
            float t = _snT++;
            float tone = _snOsc.Next(180f + 70f * MathF.Exp(-t / 500f)) * MathF.Exp(-t / SnareToneTau);
            float nEnv = MathF.Exp(-t / SnareNoiseTau);
            if (t < 1440f) nEnv = MathF.Max(nEnv * 0.55f, 1f - (t % 480f) / 480f);
            nEnv *= MathF.Min(1f, t / 24f);
            snare = (0.45f * tone * MathF.Min(1f, t / 24f) + (snBp * 1.4f + snHp * 0.5f) * nEnv) * _snVel * SnareLevel * dg;
        }

        // Hats and shaker.
        float hn = _nHat.White();
        float hat = 0f, ohat = 0f, shake = 0f;
        float hhp = _hatHp.Process(hn), ohp = _ohatHp.Process(hn);
        if (_hatT < AudioFormat.SampleRate / 6)
        {
            float t = _hatT++;
            hat = hhp * MathF.Min(1f, t / 16f) * MathF.Exp(-t / HatTau) * _hatVel * HatLevel * (0.8f + 0.4f * _intensity.Value) * dg;
        }
        if (_ohatT < AudioFormat.SampleRate)
        {
            float t = _ohatT++;
            if (_ohatChoke < 1f) _ohatVel *= _ohatChoke;
            ohat = ohp * MathF.Min(1f, t / 24f) * MathF.Exp(-t / OpenHatTau) * _ohatVel * OpenHatLevel * dg;
        }
        float sn = _shakeBp.Process(_nShake.White());
        if (_shakeT < AudioFormat.SampleRate / 5)
        {
            float t = _shakeT++;
            shake = sn * MathF.Min(1f, t / 280f) * MathF.Exp(-t / ShakerTau) * _shakeVel * ShakerLevel * dg;
        }

        // Toms (tuned to A minor notes).
        float tom = 0f;
        if (_tomT < AudioFormat.SampleRate / 2)
        {
            float t = _tomT++;
            float env = MathF.Min(1f, t / 48f) * MathF.Exp(-t / TomTau);
            tom = _tomOsc.Next(_tomHz * (1f + 0.5f * MathF.Exp(-t / 900f))) * env * _tomVel * TomLevel * dg;
        }

        // Crash / swell: two decorrelated high-passed noises.
        float crL = _crashHpL.Process(_nCrashL.White()), crR = _crashHpR.Process(_nCrashR.White());
        float crashL = 0f, crashR = 0f;
        if (_crashT < 4 * AudioFormat.SampleRate)
        {
            float t = _crashT++;
            float env = t < _crashAttack ? (t / _crashAttack) * (t / _crashAttack) : MathF.Exp(-(t - _crashAttack) / CrashTau);
            float g = env * _crashVel * CrashLevel;
            crashL = crL * g; crashR = crR * g;
        }

        // Riser: band-passed noise sweeping up.
        float riser = 0f;
        float rr = _riserBp.Process(_nRiser.White());
        if (_riserGain.Value > 1e-4f) riser = rr * _riserGain.Value * RiserLevel;

        // Bass: saw + sine, filter envelope, glide, pumped by the kick.
        if (_bassGate > 0 && --_bassGate == 0) _bassEnv.NoteOff();
        float bassEnv = _bassEnv.Next();
        float bass = 0f;
        if (!_bassEnv.IsIdle)
        {
            _bassHzNow += (_bassHz - _bassHzNow) * GlideCoeff;
            float raw = 0.55f * _bassO1.Next(_bassHzNow, OscShape.Saw) + 0.8f * _bassO2.Next(_bassHzNow);
            bass = _bassLp.Process(raw * bassEnv) * BassLevel * _bassVel * (0.25f + 0.75f * pump);
        }

        // Arpeggio.
        float arpL = 0f, arpR = 0f;
        for (int i = 0; i < ArpVoices; i++)
        {
            ref PluckVoice v = ref _arp[i];
            if (v.Env.IsIdle) continue;
            float e = v.Env.Next();
            if (e < 1e-4f && v.Env.Level < 1e-4f) { v.Env.NoteOff(); }
            float s = (0.7f * v.O1.Next(v.Hz, OscShape.Square) + 0.45f * v.O2.Next(v.Hz * 1.004f, OscShape.Saw)) * e * v.Vel;
            arpL += s * (0.5f - 0.5f * v.Pan);
            arpR += s * (0.5f + 0.5f * v.Pan);
        }
        float arpGain = ArpLevel * (0.8f + 0.3f * _intensity.Value + 0.2f * _boost.Value) * (0.7f + 0.3f * pump);
        float arpMid = _arpLp.Process((arpL + arpR) * 0.5f) * arpGain;
        float arpSide = (arpL - arpR) * 0.5f * arpGain * 0.6f;
        arpL = arpMid + arpSide;
        arpR = arpMid - arpSide;

        // Lead: two detuned saws + sub square, octave-up double, filter envelope, vibrato.
        if (_leadGate > 0 && --_leadGate == 0) _leadEnv.NoteOff();
        float leadEnv = _leadEnv.Next();
        float lead = 0f;
        if (!_leadEnv.IsIdle)
        {
            _leadHzNow += (_leadHz - _leadHzNow) * GlideCoeff;
            float hz = _leadHzNow * _vibMul;
            float s = 0.5f * _leadO1.Next(hz * 0.9965f, OscShape.Saw) + 0.5f * _leadO2.Next(hz * 1.0035f, OscShape.Saw)
                    + 0.3f * _leadO3.Next(hz * 0.5f, OscShape.Square);
            float up = _leadUp.Value;
            if (up > 1e-3f) s += up * _leadO4.Next(hz * 2.002f, OscShape.Saw);
            lead = _leadLp.Process(s * leadEnv) * LeadLevel * _leadVel * _gateDuck.Value;
        }

        // Echo (dotted eighth) of the arpeggio and lead.
        float echo = _echo.Read(StepSamples * 3);
        _echo.Write((arpMid + lead * 0.6f) + echo * 0.35f);
        float echoL = echo * 0.5f, echoR = echo * 0.42f;

        float drumsL = kick + snare + tom * 0.9f + hat * 0.75f + ohat * 1.1f + shake * 1.25f + crashL;
        float drumsR = kick + snare + tom * 1.1f + hat * 1.25f + ohat * 0.9f + shake * 0.75f + crashR;
        float dryL = padL + stabL + bell * 0.75f + pulse + bass + arpL + lead + echoL + riser + drumsL;
        float dryR = padR + stabR + bell * 0.75f + pulse + bass + arpR + lead + echoR + riser + drumsR;

        float sendL = padL * 0.6f + stabL * 0.5f + bell * 1.3f + arpL * 0.4f + lead * 0.6f + snare * 0.4f + tom * 0.3f + echoL * 0.3f + pulse * 0.2f;
        float sendR = padR * 0.6f + stabR * 0.5f + bell * 1.3f + arpR * 0.4f + lead * 0.6f + snare * 0.4f + tom * 0.3f + echoR * 0.3f + pulse * 0.2f;
        _reverb.Process(sendL, sendR, out float wetL, out float wetR);
        float wet = _wet.Value * ReverbReturn;
        outL = dryL + wetL * wet;
        outR = dryR + wetR * wet;
    }
}
