namespace SolarSystem;

// PROTOTYPE (2026-11-17): arcade flight mode — sound: the continuous sounds of flying, v2.
// v1 was a steady low drone (56 Hz on the start line, 64 Hz at cruise, 80% of its energy
// under 150 Hz, the audible bands almost static), so it read as "one unchanging hum".
// v2 is built to answer the pilot all the time:
//  * twin engines with a mid-range voice: driven saws through a body low-pass and two
//    formant band-passes (≈380–1900 Hz), a pitch-synchronous thrum (rate = f0 / 8, so it
//    speeds up with the engine), a growl (noise AM) and a slow random drift;
//  * pitch range A1 (55 Hz, standing) → E2 (cruise) → ≈G3 (full boost); Accel > 0 spools it
//    up ahead of the speed with a turbo whine, Accel < 0 makes it sag and crackle on the
//    overrun; Turn01 dips the pitch, adds growl and pulls the twin engines apart, and
//    TurnPan × Turn01 leans the engine image into the turn;
//  * boost: an afterburner roar that builds, crackles, sweeps down on release and sputters
//    when BoostEnergy01 runs low;
//  * the world: a whoosh on the side of a passing rock (Doppler-ish sweep from its approach
//    speed), glittering ice + grinding rumble in the ring, an air-brake hiss, and a riser in
//    A minor (A3 → A4 with a fifth) that builds towards every race gate and is released
//    when the gate is passed;
//  * on the start line (RacePhase 1) the engine blips nervously, faster and harder as the
//    countdown runs out.
// Controls are smoothed per sample (gains, pitch) or per 32-sample control block (filter
// coefficients), so 60 Hz steps never zipper or click.
//
// Levels on this source's own output (before the mixer): standing about -29 dBFS RMS, start
// line about -27, cruise about -22, full boost about -15; a soft safety ceiling keeps every
// sample under -7 dBFS. FlightSoundscapeTests pins those numbers.

/// <summary>The always-on soundscape of the flight, driven by <see cref="FlightSoundState"/>.
/// Added to the mixer when flight mode is first entered and kept for the app's lifetime;
/// it fades to silence by itself whenever <see cref="FlightSoundState.Active"/> is 0.</summary>
public sealed class FlightSoundscape : IAudioSource
{
    private const int SR = AudioFormat.SampleRate;
    private const int ControlBlock = 32;
    private const int MaxGrains = 12;
    private const int MaxPops = 6;
    /// <summary>Hard ceiling of the output: -7.1 dBFS.</summary>
    private const float Ceiling = 0.44f;
    /// <summary>Overall trim of this source (tuned so cruise sits near -22 dBFS RMS).</summary>
    private const float MasterGain = 0.43f;
    /// <summary>Gain while paused: -34 dB.</summary>
    private const float PausedGain = 0.02f;
    private const float FadeSeconds = 0.4f;
    /// <summary>Engine fundamental when standing: A1.</summary>
    private const float BaseHz = 55f;
    /// <summary>Gate riser root: A3.</summary>
    private const float RiserHz = 220f;
    /// <summary>Saturation level of each engine voice (see Mix).</summary>
    private const float VoiceSat = 0.6f;
    /// <summary>Bus saturation level before the master gain (see Mix).</summary>
    private const float BusSat = 0.5f;

    private readonly FlightSoundState _state;

    // ---- control targets (read once per control block) ----------------------------------
    private float _activeTarget, _pauseTarget = 1f;
    private float _tThrottle, _tBoost, _tSpeed, _tTurn, _tRing, _tAccelUp, _tAccelDown, _tBrake;
    private float _tEnergy = 1f, _tRock, _tRockPan, _tLean, _tGate, _tNerves, _countdown01;
    private int _combo, _ctlCountdown;
    private float _activeRamp;

    // ---- smoothed controls (per sample) ---------------------------------------------------
    private readonly float _kThrottle, _kBoost, _kSpeed, _kTurn, _kRing, _kPause, _kSpoolUp, _kSpoolDown,
        _kSagUp, _kSagDown, _kBrakeUp, _kBrakeDown, _kRoarUp, _kRoarDown, _kEnergy, _kRockUp, _kRockDown,
        _kPan, _kLean, _kGateUp, _kGateDown, _kNerves, _kRevUp, _kRevDown, _kGate5ms;
    private float _throttle, _boost, _speed, _turn, _ring, _pause = 1f, _spool, _sag, _brake, _roar;
    private float _energy = 1f, _rock, _rockPan, _lean, _gateLevel, _gatePitch, _nerves, _rev, _sputterGate = 1f;

    // ---- per-block derived values ----------------------------------------------------------
    private float _speedCurve, _drift, _driftTarget, _rockPrev, _rockVel, _sputter, _sputterTarget = 1f;
    private float _rockGL, _rockGR, _thrumDepth, _growlDepth, _rushGain, _grainChance, _popRate;
    private int _driftTimer, _sputterTimer, _revTimer, _revHold;
    private float _revStrength;

    // ---- engines -------------------------------------------------------------------------------
    private Oscillator _aSaw1, _aSaw2, _bSaw1, _bSaw2, _sub, _whine1, _whine2, _thrum;
    private Biquad _aBody, _bBody, _aF1, _bF1, _aF2, _bF2, _growlLp;

    // ---- noise layers ------------------------------------------------------------------------
    private Noise _nGrowl, _nRoarL, _nRoarR, _nRushL, _nRushR, _nRockL, _nRockR, _nBrakeL, _nBrakeR,
        _nBrakeP, _nRumble, _nSizzle, _nBed, _nGust, _nGrain, _nPop, _nMisc, _nRiser;
    private Biquad _roarL, _roarR, _roarFormL, _roarFormR, _roarLow;
    private Biquad _rushL, _rushR, _rockL, _rockR, _brakeL, _brakeR, _brakeRev, _flutterLp;
    private Biquad _rumbleLp1, _rumbleLp2, _grind, _sizzleHp, _bedLp, _gustLp;
    private Biquad _popL, _popR, _popBody, _riserBp, _riserNoise;
    private Oscillator _formantLfo, _bedHum, _riser1, _riser2, _trem;

    // ---- output DC blockers ------------------------------------------------------------------
    private Biquad _dcL, _dcR, _dcOutL, _dcOutR;

    // ---- ice grains --------------------------------------------------------------------------
    private readonly float[] _gPhase = new float[MaxGrains], _gModPhase = new float[MaxGrains];
    private readonly float[] _gInc = new float[MaxGrains], _gModInc = new float[MaxGrains];
    private readonly float[] _gAmp = new float[MaxGrains], _gDecay = new float[MaxGrains];
    private readonly float[] _gIndex = new float[MaxGrains], _gL = new float[MaxGrains], _gR = new float[MaxGrains];
    private readonly int[] _gAge = new int[MaxGrains];
    private readonly bool[] _gOn = new bool[MaxGrains];

    // ---- crackles / pops (overrun, afterburner, sputter) -----------------------------------
    private readonly float[] _pAmp = new float[MaxPops], _pDecay = new float[MaxPops];
    private readonly float[] _pL = new float[MaxPops], _pR = new float[MaxPops];
    private readonly int[] _pAge = new int[MaxPops];
    private readonly bool[] _pOn = new bool[MaxPops];

    /// <summary>Creates the soundscape; it starts silent and fades in when
    /// <see cref="FlightSoundState.Active"/> becomes 1.</summary>
    public FlightSoundscape(FlightSoundState state)
    {
        _state = state;
        _kThrottle = AudioMath.SmoothCoeff(0.12f);
        _kBoost = AudioMath.SmoothCoeff(0.06f);
        _kSpeed = AudioMath.SmoothCoeff(0.25f);
        _kTurn = AudioMath.SmoothCoeff(0.12f);
        _kRing = AudioMath.SmoothCoeff(0.15f);
        _kPause = AudioMath.SmoothCoeff(0.08f);
        _kSpoolUp = AudioMath.SmoothCoeff(0.12f); _kSpoolDown = AudioMath.SmoothCoeff(0.7f);
        _kSagUp = AudioMath.SmoothCoeff(0.08f); _kSagDown = AudioMath.SmoothCoeff(0.5f);
        _kBrakeUp = AudioMath.SmoothCoeff(0.06f); _kBrakeDown = AudioMath.SmoothCoeff(0.25f);
        _kRoarUp = AudioMath.SmoothCoeff(0.15f); _kRoarDown = AudioMath.SmoothCoeff(0.45f);
        _kEnergy = AudioMath.SmoothCoeff(0.2f);
        _kRockUp = AudioMath.SmoothCoeff(0.03f); _kRockDown = AudioMath.SmoothCoeff(0.12f);
        _kPan = AudioMath.SmoothCoeff(0.06f);
        _kLean = AudioMath.SmoothCoeff(0.15f);
        _kGateUp = AudioMath.SmoothCoeff(0.06f); _kGateDown = AudioMath.SmoothCoeff(0.2f);
        _kNerves = AudioMath.SmoothCoeff(0.3f);
        _kRevUp = AudioMath.SmoothCoeff(0.05f); _kRevDown = AudioMath.SmoothCoeff(0.25f);
        _kGate5ms = AudioMath.SmoothCoeff(0.012f);

        _nGrowl = new Noise(0x1001); _nRoarL = new Noise(0x2002); _nRoarR = new Noise(0x3003);
        _nRushL = new Noise(0x4004); _nRushR = new Noise(0x5005); _nRockL = new Noise(0x6006);
        _nRockR = new Noise(0x7007); _nBrakeL = new Noise(0x8008); _nBrakeR = new Noise(0x9009);
        _nBrakeP = new Noise(0xA00A); _nRumble = new Noise(0xB00B); _nSizzle = new Noise(0xC00C);
        _nBed = new Noise(0xD00D); _nGust = new Noise(0xE00E); _nGrain = new Noise(0xF00F);
        _nPop = new Noise(0x1111); _nMisc = new Noise(0x2222); _nRiser = new Noise(0x3333);

        // Spread the oscillator phases so the twin engines do not start phase-locked.
        _bSaw1.Phase = 0.37f; _bSaw2.Phase = 0.71f; _aSaw2.Phase = 0.53f; _whine2.Phase = 0.5f;
        _riser2.Phase = 0.3f;

        _dcL.SetHighPass(25f, 0.6f); _dcR.SetHighPass(25f, 0.6f);
        _dcOutL.SetHighPass(5f, 0.6f); _dcOutR.SetHighPass(5f, 0.6f);
        _bedLp.SetLowPass(260f, 0.6f);
        _gustLp.SetLowPass(1.2f, 0.7f);
        _growlLp.SetLowPass(38f, 0.8f);
        _flutterLp.SetLowPass(14f, 0.7f);
        _rumbleLp1.SetLowPass(90f, 0.7f); _rumbleLp2.SetLowPass(90f, 0.7f);
        _grind.SetBandPass(260f, 1.2f);
        _sizzleHp.SetHighPass(5500f, 0.7f);
        _brakeRev.SetBandPass(420f, 0.8f);
        _popBody.SetLowPass(380f, 0.8f);
        UpdateControl();
    }

    private static float Sanitize(float v, float max = 1f)
        => float.IsFinite(v) ? Math.Clamp(v, 0f, max) : 0f;

    private static float Follow(float v, float target, float kUp, float kDown)
        => v + (target - v) * (target > v ? kUp : kDown);

    private void ReadState()
    {
        var s = _state;
        _activeTarget = Sanitize(s.Active) >= 0.5f ? 1f : 0f;
        _pauseTarget = Sanitize(s.Paused) >= 0.5f ? PausedGain : 1f;
        _tThrottle = Sanitize(s.Throttle01);
        _tBoost = Sanitize(s.Boost01);
        _tSpeed = Sanitize(s.Speed01);
        _tTurn = Sanitize(s.Turn01);
        _tRing = Sanitize(s.RingProximity01);
        float accel = s.Accel;
        accel = float.IsFinite(accel) ? Math.Clamp(accel, -1f, 1f) : 0f;
        _tAccelUp = MathF.Max(0f, accel);
        _tAccelDown = MathF.Max(0f, -accel);
        _tBrake = Sanitize(s.Brake01);
        float e = s.BoostEnergy01;
        _tEnergy = float.IsFinite(e) ? Math.Clamp(e, 0f, 1f) : 1f;
        _tRock = Sanitize(s.RockProximity01);
        float pan = s.RockPan;
        _tRockPan = float.IsFinite(pan) ? Math.Clamp(pan, -1f, 1f) : 0f;
        float turnPan = s.TurnPan;
        // Lean of the engine image into the turn: which side times how hard.
        _tLean = (float.IsFinite(turnPan) ? Math.Clamp(turnPan, -1f, 1f) : 0f) * _tTurn;
        int phase = s.RacePhase;
        // The riser only covers the last part of the approach (GateApproach01 above 0.55).
        _tGate = phase == 2 ? Math.Clamp((Sanitize(s.GateApproach01) - 0.55f) / 0.45f, 0f, 1f) : 0f;
        _tNerves = phase == 1 ? 1f : 0f;
        _countdown01 = Sanitize(s.Countdown, 3f) / 3f;
        _combo = Math.Clamp(s.Combo, 0, 8);
    }

    /// <summary>Per-control-block work: slow random drift, rev blips, sputter, filter
    /// coefficients and layer gains from the smoothed controls.</summary>
    private void UpdateControl()
    {
        float thr = _throttle, roar = _roar, turn = _turn, ring = _ring, spool = _spool, sag = _sag;
        _speedCurve = MathF.Pow(_speed, 0.62f);
        float sc = _speedCurve;

        // Slow evolution: a new random drift target every 0.6 .. 1.4 s, glided to.
        if ((_driftTimer -= ControlBlock) <= 0)
        {
            _driftTarget = _nMisc.White();
            _driftTimer = (int)(SR * (0.6f + 0.8f * _nMisc.Unit()));
        }
        _drift += (_driftTarget - _drift) * 0.0011f;    // ≈ 0.6 s at 1500 blocks / s

        // Rock approach speed (for the whoosh's sweep).
        float rv = (_rock - _rockPrev) * (SR / (float)ControlBlock);
        _rockPrev = _rock;
        _rockVel += (rv - _rockVel) * 0.15f;

        // Start-line nerves: rev blips, faster and harder as the countdown runs out.
        if (_tNerves > 0.5f)
        {
            if ((_revTimer -= ControlBlock) <= 0)
            {
                _revHold = (int)(SR * (0.07f + 0.07f * _nMisc.Unit()));
                _revTimer = (int)(SR * (0.32f + 0.5f * _countdown01) * (0.8f + 0.4f * _nMisc.Unit()));
                _revStrength = 0.45f + 0.45f * (1f - _countdown01) + 0.1f * _nMisc.Unit();
            }
            _revHold -= ControlBlock;
        }
        else { _revHold = 0; _revTimer = 0; }

        // Sputter when the boost reserve runs low: the roar drops out in random chunks.
        _sputter = roar * Math.Clamp((0.3f - _energy) / 0.25f, 0f, 1f);
        if ((_sputterTimer -= ControlBlock) <= 0)
        {
            _sputterTimer = (int)(SR * (0.04f + 0.05f * _nMisc.Unit()));
            _sputterTarget = _sputter > 0.01f && _nMisc.Unit() < 0.5f ? 1f - 0.9f * _sputter : 1f;
        }

        float load = Math.Clamp(0.2f * thr + 0.85f * sc + 0.35f * roar + 0.25f * spool + 0.2f * _rev, 0f, 1.3f);

        // Engine body and formants ("oo" at idle, "ah" under load, "ee" when straining).
        float thrumSin = MathF.Sin(AudioMath.TwoPi * _thrum.Phase);
        float bodyCut = (170f + 520f * sc + 900f * spool + 1100f * roar + 500f * _rev) * (1f - 0.3f * sag);
        _aBody.SetLowPass(bodyCut, 0.9f); _bBody.SetLowPass(bodyCut * 0.93f, 0.9f);
        float f1 = (360f + 280f * load + 140f * turn + 40f * _drift) * (1f + 0.09f * thrumSin) * (1f - 0.15f * sag);
        _aF1.SetBandPass(f1, 2.4f); _bF1.SetBandPass(f1 * 1.06f, 2.4f);
        float f2 = 900f + 400f * load + 350f * turn + 450f * spool + 50f * _drift;
        _aF2.SetBandPass(f2, 3f); _bF2.SetBandPass(f2 * 0.95f, 3f);
        _thrumDepth = Math.Clamp(0.6f + 0.12f * _drift + 0.2f * _rev - 0.2f * roar, 0f, 0.8f);
        _growlDepth = 0.2f + 0.55f * turn + 0.3f * roar + 0.35f * sag;

        // Afterburner roar: its formant follows the roar envelope, so the release sweeps down.
        float formant = 280f + 750f * roar + 120f * MathF.Sin(AudioMath.TwoPi * _formantLfo.Phase);
        _roarL.SetBandPass(formant, 1.3f); _roarR.SetBandPass(formant * 1.07f, 1.3f);
        _roarFormL.SetBandPass(formant * 1.8f, 2.2f); _roarFormR.SetBandPass(formant * 1.7f, 2.2f);
        _roarLow.SetLowPass(110f, 0.7f);

        // Rush of speed.
        float rushCentre = 500f + 1300f * sc + 300f * roar;
        _rushL.SetBandPass(rushCentre, 1.0f); _rushR.SetBandPass(rushCentre * 1.12f, 1.0f);
        _rushGain = 0.25f * MathF.Pow(sc, 1.3f) * (1f + 0.2f * roar);

        // Passing rock: pitch sweeps up while it approaches and down as it recedes.
        float rockCentre = (350f + 600f * _rock + 300f * sc) * MathF.Pow(2f, Math.Clamp(0.4f * _rockVel, -0.8f, 0.8f));
        _rockL.SetBandPass(rockCentre, 1.1f); _rockR.SetBandPass(rockCentre * 1.05f, 1.1f);
        AudioMath.Pan(_rockPan, out float pl, out float pr);
        float rockGain = 1.6f * MathF.Pow(_rock, 1.4f) * (0.5f + 0.3f * sc);
        _rockGL = rockGain * (0.25f + 1.2f * pl); _rockGR = rockGain * (0.25f + 1.2f * pr);

        // Air brake.
        _brakeL.SetBandPass(2800f, 0.7f); _brakeR.SetBandPass(3100f, 0.7f);

        // Ring.
        _grainChance = ring * (20f + 25f * sc) / SR;

        // Crackles: overrun, afterburner, sputter, and the start-line blips falling back.
        _popRate = 24f * sag + 5f * roar + 14f * _sputter;
        _popL.SetBandPass(1000f + 400f * roar, 0.8f); _popR.SetBandPass(1080f + 400f * roar, 0.8f);

        // Gate riser.
        float rf = RiserHz * MathF.Pow(2f, _gatePitch);
        _riserBp.SetBandPass(rf * 2.5f, 0.9f);
        _riserNoise.SetBandPass(500f * MathF.Pow(2f, 1.8f * _gatePitch), 2f);
    }

    private void SpawnGrain()
    {
        for (int i = 0; i < MaxGrains; i++)
        {
            if (_gOn[i]) continue;
            float f = 2000f * MathF.Pow(2f, 1.3f * _nGrain.Unit());       // 2 .. 4.9 kHz
            _gInc[i] = f / SR;
            _gModInc[i] = f * (1.41f + 0.6f * _nGrain.Unit()) / SR;      // inharmonic: icy
            _gPhase[i] = 0f; _gModPhase[i] = 0f;
            _gIndex[i] = 0.6f + 1.2f * _nGrain.Unit();
            _gAmp[i] = 0.018f + 0.03f * _nGrain.Unit();
            float decaySeconds = 0.02f + 0.07f * _nGrain.Unit();
            _gDecay[i] = MathF.Exp(-1f / (decaySeconds * SR));
            AudioMath.Pan(_nGrain.White() * 0.85f, out _gL[i], out _gR[i]);
            _gAge[i] = 0;
            _gOn[i] = true;
            return;
        }
    }

    private void SpawnPop()
    {
        for (int i = 0; i < MaxPops; i++)
        {
            if (_pOn[i]) continue;
            _pAmp[i] = 0.5f + 0.5f * _nPop.Unit();
            float decaySeconds = 0.006f + 0.016f * _nPop.Unit();
            _pDecay[i] = MathF.Exp(-1f / (decaySeconds * SR));
            AudioMath.Pan(_nPop.White() * 0.7f, out _pL[i], out _pR[i]);
            _pAge[i] = 0;
            _pOn[i] = true;
            return;
        }
    }

    /// <inheritdoc/>
    public bool Mix(Span<float> buffer, int frames)
    {
        frames = Math.Min(frames, buffer.Length / 2);
        float rampStep = 1f / (FadeSeconds * SR);
        for (int n = 0; n < frames; n++)
        {
            if (--_ctlCountdown <= 0)
            {
                _ctlCountdown = ControlBlock;
                ReadState();
                UpdateControl();
            }

            // Fade in / out over FadeSeconds (linear ramp, eased) — exactly 0 when off.
            _activeRamp = _activeTarget > 0.5f
                ? MathF.Min(1f, _activeRamp + rampStep)
                : MathF.Max(0f, _activeRamp - rampStep);
            if (_activeRamp <= 0f) continue;      // fully silent: add nothing, keep state still

            float thr = _throttle += (_tThrottle - _throttle) * _kThrottle;
            float boost = _boost += (_tBoost - _boost) * _kBoost;
            _speed += (_tSpeed - _speed) * _kSpeed;
            float turn = _turn += (_tTurn - _turn) * _kTurn;
            float ring = _ring += (_tRing - _ring) * _kRing;
            float pause = _pause += (_pauseTarget - _pause) * _kPause;
            float spool = _spool = Follow(_spool, _tAccelUp, _kSpoolUp, _kSpoolDown);
            float sag = _sag = Follow(_sag, _tAccelDown, _kSagUp, _kSagDown);
            float brake = _brake = Follow(_brake, _tBrake, _kBrakeUp, _kBrakeDown);
            float roar = _roar = Follow(_roar, boost, _kRoarUp, _kRoarDown);
            _energy += (_tEnergy - _energy) * _kEnergy;
            float rock = _rock = Follow(_rock, _tRock, _kRockUp, _kRockDown);
            _rockPan += (_tRockPan - _rockPan) * _kPan;
            _lean += (_tLean - _lean) * _kLean;
            _nerves += (_tNerves - _nerves) * _kNerves;
            float rev = _rev = Follow(_rev, _revHold > 0 ? _revStrength * _nerves : 0f, _kRevUp, _kRevDown);
            _sputterGate += (_sputterTarget - _sputterGate) * _kGate5ms;
            float sc = _speedCurve;

            // ---- engines ------------------------------------------------------------------
            float semis = 19f * sc + 3f * roar + 2.5f * (thr - 0.5f) + 4f * spool - 2.5f * sag
                        - 1.6f * turn + 10f * rev + 0.35f * _drift;
            float f0 = BaseHz * MathF.Pow(2f, semis * (1f / 12f));
            float det = 0.0045f + 0.012f * turn;
            float fa = f0 * (1f + det), fb = f0 * (1f - det);
            float load = Math.Clamp(0.2f * thr + 0.85f * sc + 0.35f * roar + 0.25f * spool + 0.2f * rev, 0f, 1.3f);
            float drive = 0.55f + 0.3f * load + 0.6f * turn + 0.15f * roar + 0.3f * sag;
            float ea = AudioMath.SoftClip(drive * (_aSaw1.Next(fa, OscShape.Saw) + 0.6f * _aSaw2.Next(fa * 1.0071f, OscShape.Saw)));
            float eb = AudioMath.SoftClip(drive * (_bSaw1.Next(fb, OscShape.Saw) + 0.6f * _bSaw2.Next(fb * 0.9932f, OscShape.Saw)));

            // Thrum: a pulse in the thrust at f0 / 8 (≈7 Hz standing, 10 Hz cruise, 24 Hz boost).
            float th = 0.5f + 0.5f * _thrum.Next(f0 * 0.125f);
            float thrum = 1f - _thrumDepth * th * th;
            // Growl: rough combustion AM, stronger when straining, roaring or on the overrun.
            float growl = Math.Clamp(1f + _growlDepth * 25f * _growlLp.Process(_nGrowl.White()), 0.3f, 1.5f);
            float mid = thrum * growl * (1f - 0.35f * roar);
            float ma = 2.4f * _aF1.Process(ea) + 1.3f * _aF2.Process(ea);
            float mb = 2.4f * _bF1.Process(eb) + 1.3f * _bF2.Process(eb);
            float va = (0.3f + 0.7f * thrum) * 0.4f * _aBody.Process(ea) + mid * ma;
            float vb = (0.3f + 0.7f * thrum) * 0.4f * _bBody.Process(eb) + mid * mb;
            // Tube-like saturation of each engine voice keeps its crest factor near 10 dB, so the
            // output ceiling stays a safety net instead of shaping every cycle.
            va = VoiceSat * AudioMath.SoftClip(va * (1f / VoiceSat));
            vb = VoiceSat * AudioMath.SoftClip(vb * (1f / VoiceSat));
            float sub = _sub.Next(f0) * (0.12f + 0.1f * roar);
            // Surge: the slow drift swells and eases the engine so no two seconds are alike.
            float engGain = (0.2f + 0.95f * MathF.Min(load, 0.5f)) * (1f + 0.3f * _drift)
                          * (1f + 0.9f * rev) * (1f - 0.25f * _nerves);
            float wide = 0.72f + 0.12f * turn;
            // The engine leans into the turn: up to about 5 dB between the sides on the engine, never silent.
            float leanL = 1f - 0.3f * _lean, leanR = 1f + 0.3f * _lean;
            float engL = engGain * leanL * (wide * va + (1f - wide) * vb + sub);
            float engR = engGain * leanR * ((1f - wide) * va + wide * vb + sub);

            // Turbo whine: spools up ahead of the speed.
            float whineF = 600f + 800f * sc + 1100f * spool + 300f * roar;
            float whine = 0.003f + 0.006f * sc + 0.045f * spool * (1f - 0.5f * roar) + 0.005f * roar;
            engL += _whine1.Next(whineF) * whine;
            engR += _whine2.Next(whineF * 1.0035f + 1.3f) * whine;

            // ---- afterburner roar -----------------------------------------------------------
            _formantLfo.Next(0.7f);
            float roarL = 0f, roarR = 0f;
            if (roar > 1e-4f)
            {
                float pl = _nRoarL.Pink(), pr = _nRoarR.Pink();
                float low = _roarLow.Process(pl + pr);
                float g = roar * roar * (3f - 2f * roar) * 0.7f * _sputterGate;
                roarL = g * (_roarL.Process(pl) + 0.6f * _roarFormL.Process(pl) + 0.8f * low);
                roarR = g * (_roarR.Process(pr) + 0.6f * _roarFormR.Process(pr) + 0.8f * low);
                engL *= 0.2f + 0.8f * _sputterGate;
                engR *= 0.2f + 0.8f * _sputterGate;
            }

            // ---- rush of speed (with slow gusts) -------------------------------------------
            float gust = Math.Clamp(0.75f + 2.5f * _gustLp.Process(_nGust.White()), 0.4f, 1.2f);
            float rushL = _rushGain * gust * _rushL.Process(_nRushL.White());
            float rushR = _rushGain * gust * _rushR.Process(_nRushR.White());

            // ---- passing rocks ---------------------------------------------------------------
            float rockL = 0f, rockR = 0f;
            if (rock > 1e-4f)
            {
                rockL = _rockGL * _rockL.Process(_nRockL.White());
                rockR = _rockGR * _rockR.Process(_nRockR.White());
            }

            // ---- air brake: hiss + reverse-thrust rush, fluttering ------------------------
            float brakeL = 0f, brakeR = 0f;
            if (brake > 1e-4f)
            {
                float flutter = 1f + 0.35f * Math.Clamp(30f * _flutterLp.Process(_nBrakeP.White()), -1f, 1f);
                float bg = brake * (0.5f + 0.7f * sc) * flutter;
                float revRush = 0.3f * _brakeRev.Process(_nBrakeP.Pink());
                brakeL = bg * (0.22f * _brakeL.Process(_nBrakeL.White()) + revRush);
                brakeR = bg * (0.22f * _brakeR.Process(_nBrakeR.White()) + revRush);
            }

            // ---- ring: rumble + grind + sizzle + ice grains ----------------------------------
            float iceL = 0f, iceR = 0f;
            if (_nGrain.Unit() < _grainChance) SpawnGrain();
            if (ring > 1e-4f)
            {
                float rumble = 0.4f * _rumbleLp2.Process(_rumbleLp1.Process(_nRumble.Pink() * 2f)) * gust
                             + 0.6f * (0.5f + sc) * _grind.Process(_nRumble.Pink());
                float sizzle = 0.008f * gust * _sizzleHp.Process(_nSizzle.White());
                iceL = ring * (rumble + sizzle);
                iceR = ring * (rumble - sizzle);
            }
            for (int i = 0; i < MaxGrains; i++)
            {
                if (!_gOn[i]) continue;
                float mod = MathF.Sin(AudioMath.TwoPi * _gModPhase[i]) * _gIndex[i];
                float s = MathF.Sin(AudioMath.TwoPi * _gPhase[i] + mod);
                float attack = MathF.Min(1f, _gAge[i] * (1f / 96f));
                float v = s * _gAmp[i] * attack * ring;
                iceL += v * _gL[i]; iceR += v * _gR[i];
                _gPhase[i] += _gInc[i]; if (_gPhase[i] >= 1f) _gPhase[i] -= 1f;
                _gModPhase[i] += _gModInc[i]; if (_gModPhase[i] >= 1f) _gModPhase[i] -= 1f;
                _gAmp[i] *= _gDecay[i];
                _gIndex[i] *= 0.9997f;
                _gAge[i]++;
                if (_gAmp[i] < 1e-5f) _gOn[i] = false;
            }

            // ---- crackles ----------------------------------------------------------------------
            if (_nPop.Unit() < _popRate * (1f / SR)) SpawnPop();
            float popSumL = 0f, popSumR = 0f;
            for (int i = 0; i < MaxPops; i++)
            {
                if (!_pOn[i]) continue;
                float env = _pAmp[i] * MathF.Min(1f, _pAge[i] * (1f / 48f));
                popSumL += env * _pL[i]; popSumR += env * _pR[i];
                _pAmp[i] *= _pDecay[i];
                _pAge[i]++;
                if (_pAmp[i] < 1e-4f) _pOn[i] = false;
            }
            float popL = 0f, popR = 0f;
            if (popSumL + popSumR > 0f)
            {
                float w = _nPop.White();
                float body = _popBody.Process(w * (popSumL + popSumR));
                float pg = 1f - 0.5f * roar;
                popL = pg * (1.5f * _popL.Process(w * popSumL) + 1.8f * body);
                popR = pg * (1.5f * _popR.Process(w * popSumR) + 1.8f * body);
            }

            // ---- gate riser (A minor: A3 → A4, with the fifth) -----------------------------
            _gateLevel = Follow(_gateLevel, _tGate, _kGateUp, _kGateDown);
            if (_tGate >= _gatePitch - 0.15f) _gatePitch += (_tGate - _gatePitch) * _kGateUp;
            else if (_gateLevel < _tGate + 0.04f) _gatePitch = _tGate;   // released and quiet: re-arm
            float riserL = 0f, riserR = 0f;
            if (_gateLevel > 1e-4f)
            {
                float rf = RiserHz * MathF.Pow(2f, _gatePitch);
                float tone = _riser1.Next(rf, OscShape.Saw) + 0.7f * _riser2.Next(rf * 1.4983f, OscShape.Saw);
                float trem = 0.7f + 0.3f * _trem.Next(5f + 13f * _gatePitch);
                float gl = _gateLevel * _gateLevel * 1.4f * (1f + 0.05f * _combo) * trem;
                // Mostly an unpitched noise swell; the pitched part stays about 14 dB under it.
                float voice = 0.12f * _riserBp.Process(tone) + _riserNoise.Process(_nRiser.White());
                riserL = gl * voice;
                riserR = gl * voice;
            }

            // ---- cockpit / space bed --------------------------------------------------------
            float bed = 0.04f * _bedLp.Process(_nBed.Pink()) + 0.004f * _bedHum.Next(55f);

            float l = engL + roarL + rushL + rockL + brakeL + iceL + popL + riserL + bed;
            float r = engR + roarR + rushR + rockR + brakeR + iceR + popR + riserR + bed;

            l = _dcL.Process(l); r = _dcR.Process(r);

            // Bus saturation: rounds off the rare roar / crackle peaks so the layer tops out near
            // -12 dBFS (-10 on full boost); the output ceiling below is only a safety.
            float bus = BusSat + 0.15f * roar;     // -12 dBFS top in ordinary play, -10 on full boost
            l = bus * AudioMath.SoftClip(l / bus);
            r = bus * AudioMath.SoftClip(r / bus);

            float e = _activeRamp * _activeRamp * (3f - 2f * _activeRamp);
            float g2 = MasterGain * e * pause;
            l *= g2; r *= g2;

            // Soft safety ceiling at -7 dBFS (transparent below about -13 dBFS).
            l = Ceiling * AudioMath.SoftClip(l / Ceiling);
            r = Ceiling * AudioMath.SoftClip(r / Ceiling);
            // Saturating asymmetric peaks makes a little DC: block it again, then hold the ceiling.
            float top = MathF.Min(Ceiling, bus * g2);    // the bus top, exactly (absorbs blocker overshoot)
            l = Math.Clamp(_dcOutL.Process(l), -top, top);
            r = Math.Clamp(_dcOutR.Process(r), -top, top);
            if (!float.IsFinite(l)) l = 0f;
            if (!float.IsFinite(r)) r = 0f;

            buffer[2 * n] += l;
            buffer[2 * n + 1] += r;
        }
        return true;
    }
}
