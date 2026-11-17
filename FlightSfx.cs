namespace SolarSystem;

// PROTOTYPE (2026-11-17): arcade flight mode — sound: the one-shot effects (gate hits,
// countdown, start, finish fanfare, near misses, rock hits, boost, air brake, power up /
// down). Second version: punchy, layered and escalating (the first one was a polite bell
// on an 11-step scale).
//
// Every effect is a SfxVoice: a handful of layers (sine / FM tones, PolyBLEP saws and
// squares, filtered noise) with their own pitch glide, filter sweep, envelope and, when a
// layer moves in the stereo field (whooshes, near misses, shimmer), its own pan sweep.
// The layers are summed, DC-blocked, panned with an equal-power law (with a little
// compensation so a hard-panned sound is not 3 dB hotter than a centred one) and
// optionally sent through a private short reverb. Everything is allocated in Create
// (game thread); Mix only does arithmetic.
//
// Levels are tuned by offline measurement (see FlightSfxTests): a plain gate and Go peak
// around -9 dBFS, a gate with a long combo and the gold fanfare around -7 dBFS, ticks,
// boost and hard rock hits around -9 .. -10, near misses and the air brake around
// -15 dBFS. Nothing exceeds -5 dBFS, even hard-panned.
//
// The gate hit (25 times a race) is built from layers: the physical pass (a stereo whoosh
// whose pan sweeps across, an energy "zap" and a low thump), a tonal hit that plays a
// composed melody over the run (GateMelody), harmony that grows with the combo (tiers
// at 3, 5 and 10 gates in a row), a bigger stinger on every fifth gate and a flourish when
// ahead of the best run. Small timbre details are seeded by the gate index so consecutive
// gates never sound identical, and identical cues always give identical samples.

/// <summary>Builds the sound for a <see cref="FlightSoundCue"/>.</summary>
public static class FlightSfx
{
    /// <summary>The gate melody: one MIDI note per gate of a 25-gate race, A minor
    /// pentatonic (A C D E G — every note sits on the soundtrack's Am, F, C and G chords).
    /// Five phrases of five gates; each phrase ends on a checkpoint (gates 5, 10, 15, 20)
    /// with a leap to a new high point, and the last phrase climbs to A6. The one repeated
    /// note (D6 D6, gates 17–18) is on purpose: a held breath before the final climb.</summary>
    private static readonly int[] GateMelody =
    [
        69, 76, 72, 74, 81,     // A4 E5 C5 D5 | A5
        79, 74, 76, 72, 84,     // G5 D5 E5 C5 | C6
        81, 76, 81, 84, 88,     // A5 E5 A5 C6 | E6
        84, 86, 86, 81, 91,     // C6 D6 D6 A5 | G6
        88, 84, 88, 91, 93,     // E6 C6 E6 G6   A6
    ];

    /// <summary>Semitones of A minor pentatonic above A.</summary>
    private static readonly int[] Pentatonic = [0, 3, 5, 7, 10];

    /// <summary>Create the one-shot source for <paramref name="cue"/>, or null when the
    /// event makes no sound. Called on the game thread; the returned source is then mixed
    /// on the audio thread until its Mix returns false. Garbage (NaN, out-of-range) fields
    /// are clamped.</summary>
    public static IAudioSource? Create(in FlightSoundCue cue)
    {
        float pan = Fin(cue.Pan, -1f, 1f);
        float strength = Fin(cue.Strength, 0f, 1f);
        float speed = Fin(cue.Speed01, 0f, 1f);
        return cue.Event switch
        {
            FlightSoundEvent.GatePassed => GatePassed(cue, pan, speed),
            FlightSoundEvent.GateMissed => GateMissed(pan),
            FlightSoundEvent.CountdownTick => CountdownTick(Math.Clamp(cue.Count, 1, 3), pan),
            FlightSoundEvent.Go => Go(pan),
            FlightSoundEvent.Finish => Finish(Math.Clamp(cue.Medal, 0, 3), cue.NewRecord, pan),
            FlightSoundEvent.BoostIgnite => BoostIgnite(pan),
            FlightSoundEvent.BoostEmpty => BoostEmpty(pan),
            FlightSoundEvent.RockHit => RockHit(strength, pan),
            FlightSoundEvent.RockPass => RockPass(strength, speed, pan),
            FlightSoundEvent.BrakeOn => BrakeOn(speed, pan),
            FlightSoundEvent.EnterFlight => EnterFlight(pan),
            FlightSoundEvent.ExitFlight => ExitFlight(pan),
            _ => null,
        };
    }

    /// <summary>The MIDI note of the tonal hit for gate <paramref name="gateIndex"/> of
    /// <paramref name="gateCount"/>: the 25-note <see cref="GateMelody"/>, stretched or
    /// squeezed onto other gate counts (the last gate always plays the top A6).</summary>
    public static int GateNote(int gateIndex, int gateCount)
    {
        int n = GateMelody.Length;
        if (gateCount <= 1) return GateMelody[Math.Clamp(gateIndex, 0, n - 1)];
        int i = Math.Clamp(gateIndex, 0, gateCount - 1);
        if (gateCount == n) return GateMelody[i];
        int step = (int)MathF.Round(i * (n - 1) / (float)(gateCount - 1));
        return GateMelody[Math.Clamp(step, 0, n - 1)];
    }

    /// <summary>How built-up the gate hit is for a streak of <paramref name="combo"/> gates:
    /// 0 a plain note (1–2), 1 with harmony (3–4), 2 brighter with sparkle (5–9), 3 a full
    /// chord stab with shimmer (10+).</summary>
    public static int ComboTier(int combo) => combo >= 10 ? 3 : combo >= 5 ? 2 : combo >= 3 ? 1 : 0;

    /// <summary>True for the checkpoint gates (every fifth gate: index 4, 9, 14, 19 of 25),
    /// never the last gate (the finish fanfare takes that moment).</summary>
    public static bool IsCheckpoint(int gateIndex, int gateCount)
        => gateIndex >= 0 && (gateIndex + 1) % 5 == 0 && (gateCount <= 0 || gateIndex < gateCount - 1);

    // ---- the events --------------------------------------------------------------------

    private static SfxVoice GatePassed(in FlightSoundCue c, float pan, float speed)
    {
        int count = c.GateCount > 0 ? c.GateCount : GateMelody.Length;
        int index = Math.Clamp(c.GateIndex, 0, Math.Max(0, count - 1));
        int note = GateNote(index, count);
        int tier = ComboTier(Math.Max(1, c.Combo));
        bool checkpoint = IsCheckpoint(index, count);
        float split = float.IsFinite(c.Split) ? c.Split : 0f;
        bool ahead = c.HasBest && split < -0.01f, behind = c.HasBest && split > 0.01f;

        // Deterministic per-gate variation.
        uint h = Hash((uint)index * 0x9E3779B1u + 0x1234567u);
        float r0 = Rand(h, 0), r1 = Rand(h, 1), r2 = Rand(h, 2), r3 = Rand(h, 3), r4 = Rand(h, 4);
        float dir = (h & 1u) == 0u ? 1f : -1f;

        float gain = tier switch { 0 => 0.205f, 1 => 0.19f, 2 => 0.2f, _ => 0.215f } * (checkpoint ? 0.8f : 1f);
        var v = new SfxVoice(FlightSoundEvent.GatePassed, pan, gain, reverbRoom: 0.5f + 0.05f * tier, reverbDamping: 0.45f,
            reverbWet: 0.16f + 0.04f * tier + (checkpoint ? 0.06f : 0f), variation: (uint)index * 31u + (uint)tier);

        // 1. The physical pass: a whoosh sweeping down and across, an "air rip" on top,
        //    an energy zap and a low thump. Faster = brighter, wider and shorter.
        float pass = 0.34f - 0.14f * speed;
        float width = 0.35f + 0.55f * speed;
        float hiF = (2600f + 4600f * speed) * (0.85f + 0.3f * r0);
        float loF = 420f + 260f * r1;
        v.Layer(SfxKind.Pink, 0f, 1.5f + 0.7f * speed, 0.012f, 0.02f, pass * 0.45f, pass + 0.18f)
            .Filter(SfxFilter.BandPass, hiF, loF, pass, 1.0f).Spread(-dir * width * 0.4f, dir * width, pass);
        v.Layer(SfxKind.White, 0f, 0.18f + 0.2f * speed, 0.003f, 0f, 0.045f, 0.2f)
            .Filter(SfxFilter.HighPass, 7000f, 3500f, 0.15f, 0.7f).Spread(dir * width * 0.6f, -dir * width * 0.3f, 0.12f);
        v.Layer(SfxKind.Saw, 0f, 0.16f, 0.001f, 0f, 0.025f, 0.12f).Pitch(2600f * (0.8f + 0.4f * r2), 260f, 0.06f)
            .Filter(SfxFilter.LowPass, 7000f, 1200f, 0.08f, 0.8f);
        v.Layer(SfxKind.Sine, 0f, 0.65f + 0.1f * tier, 0.002f, 0.012f, 0.07f, 0.35f).Pitch(125f * (0.9f + 0.2f * r3), 46f, 0.09f);

        // 2. The tonal hit: the melody note; FM ratio and brightness vary per gate.
        float bright = (behind ? 0.55f : ahead ? 1.2f : 0.9f) * (1f + 0.18f * tier) * (0.85f + 0.3f * r4);
        bright *= 1f - 0.3f * Math.Clamp((note - 69) / 24f, 0f, 1f);    // keep the top notes sweet
        float fmRatio = (h >> 3) % 4 switch { 0 => 1f, 1 => 2f, 2 => 1f, _ => 3f };
        Bell(v, 0.004f, note, 1f, bright, decay: 0.26f + 0.04f * tier, length: 1.3f, fmRatio);

        // 3. Combo escalation.
        int below = PentaStep(note, -2);                                   // two scale steps down: a 4th / 3rd
        if (tier >= 1)
        {
            // Strummed a hair after the strike, so the streak adds body, not just peak.
            Bell(v, 0.022f, below, 0.55f, bright * 0.8f, 0.26f, 1.1f, 1f);
            Bell(v, 0.036f, note - 12, 0.36f, bright * 0.6f, 0.24f, 1.0f, 1f);
        }
        if (tier >= 2)
        {
            // Brighter: a detuned saw pair on the note through an opening-then-closing low-pass.
            float f = AudioMath.MidiToHz(note);
            v.Layer(SfxKind.Saw, 0.012f, 0.19f, 0.004f, 0.03f, 0.18f, 0.7f).Pitch(f * 0.997f).Filter(SfxFilter.LowPass, 7000f, 1800f, 0.35f, 0.9f).Spread(-0.25f, -0.25f, 0f);
            v.Layer(SfxKind.Saw, 0.012f, 0.19f, 0.004f, 0.03f, 0.18f, 0.7f).Pitch(f * 1.004f).Filter(SfxFilter.LowPass, 7000f, 1800f, 0.35f, 0.9f).Spread(0.25f, 0.25f, 0f);
            // Sparkle: an octave and an octave + fifth up, a hair after the strike.
            Tinkle(v, 0.035f, note + 12, 0.11f, dir * 0.4f);
            Tinkle(v, 0.07f, PentaStep(note + 12, 3), 0.08f, -dir * 0.4f);
        }
        if (tier >= 3)
        {
            // A full chord stab (note, the harmony, the octave below, the fifth above that) ...
            int[] chord = [note - 12, below, note, PentaStep(note, 3)];
            foreach (int m in chord)
            {
                float f = AudioMath.MidiToHz(m);
                v.Layer(SfxKind.Saw, 0.004f, 0.075f, 0.003f, 0.03f, 0.2f, 0.8f).Pitch(f * 0.996f).Filter(SfxFilter.LowPass, 5500f, 900f, 0.35f, 1.0f);
                v.Layer(SfxKind.Saw, 0.004f, 0.075f, 0.003f, 0.03f, 0.2f, 0.8f).Pitch(f * 1.005f).Filter(SfxFilter.LowPass, 5500f, 900f, 0.35f, 1.0f);
            }
            // ... and a shimmer: a quick pentatonic run up, scattered across the stereo field.
            for (int i = 0; i < 6; i++)
                Tinkle(v, 0.09f + 0.035f * i, PentaStep(note + 12, i), 0.075f - 0.007f * i, (i & 1) == 0 ? 0.6f : -0.6f);
        }

        // 4. Checkpoint stinger, a beat after the pass ("whoosh-thump ... BOOM"): a sub
        //    boom, a wide crash and the octave and fifth above ringing on.
        if (checkpoint)
        {
            int k = (index + 1) / 5;                                     // 1, 2, 3, 4
            const float at = 0.06f;
            v.Layer(SfxKind.Sine, at, 0.4f, 0.006f, 0.05f, 0.3f, 1.1f).Pitch(78f, 34f, 0.35f);
            v.Layer(SfxKind.White, at, 0.2f + 0.03f * k, 0.002f, 0.01f, 0.28f, 1.1f)
                .Filter(SfxFilter.HighPass, 3000f, 5500f, 0.6f, 0.6f).Spread(-0.5f, -0.5f, 0f);
            v.Layer(SfxKind.White, at, 0.2f + 0.03f * k, 0.002f, 0.01f, 0.28f, 1.1f)
                .Filter(SfxFilter.HighPass, 3200f, 6000f, 0.6f, 0.6f).Spread(0.5f, 0.5f, 0f);
            Bell(v, at, note + 12, 0.5f, 1.1f, 0.5f, 1.9f, 2f);
            Bell(v, at, PentaStep(note, 3), 0.4f, 0.9f, 0.45f, 1.8f, 1f);
        }

        // 5. Split against the best run: ahead = a rising flourish; behind = darker (above).
        if (ahead)
        {
            float amount = 0.7f + 0.3f * Math.Clamp(-split / 2f, 0f, 1f);
            for (int i = 1; i <= 3; i++)
                Tinkle(v, 0.06f + 0.04f * i, PentaStep(note + 12, i), 0.1f * amount, dir * (0.2f * i));
        }
        return v;
    }

    private static SfxVoice GateMissed(float pan)
    {
        var v = new SfxVoice(FlightSoundEvent.GateMissed, pan, gain: 0.30f, reverbRoom: 0.3f, reverbDamping: 0.6f, reverbWet: 0.08f);
        // Power loss: a detuned driven saw pair falling more than two octaves, closing down.
        v.Layer(SfxKind.Saw, 0f, 0.42f, 0.004f, 0.08f, 0.2f, 0.7f).Pitch(330f, 52f, 0.55f)
            .Filter(SfxFilter.LowPass, 3800f, 260f, 0.55f, 1.1f).Drive(1.8f).Am(23f, 0.5f);
        v.Layer(SfxKind.Saw, 0f, 0.42f, 0.004f, 0.08f, 0.2f, 0.7f).Pitch(330f * 1.03f, 52f * 1.03f, 0.55f)
            .Filter(SfxFilter.LowPass, 3800f, 260f, 0.55f, 1.1f).Drive(1.8f).Am(23f, 0.5f);
        v.Layer(SfxKind.Square, 0f, 0.25f, 0.004f, 0.08f, 0.18f, 0.6f).Pitch(82.4f, 38f, 0.5f).Filter(SfxFilter.LowPass, 900f, 200f, 0.5f, 0.7f);
        // Impact thud.
        v.Layer(SfxKind.Sine, 0f, 0.5f, 0.002f, 0.01f, 0.07f, 0.35f).Pitch(105f, 36f, 0.1f);
        // Glitch: a stuttering sample-and-hold buzz, chopped hard.
        v.Layer(SfxKind.White, 0.01f, 0.8f, 0.003f, 0.12f, 0.06f, 0.3f).Hold(60)
            .Filter(SfxFilter.BandPass, 1800f, 700f, 0.25f, 1.2f).Am(26f, 1f);
        v.Layer(SfxKind.Square, 0.02f, 0.22f, 0.003f, 0.1f, 0.06f, 0.28f).Pitch(233f).Hold(9)
            .Filter(SfxFilter.LowPass, 2500f, 800f, 0.25f, 0.7f).Am(19f, 1f);
        // The streak breaking: a short glassy crack and a sour falling run.
        v.Layer(SfxKind.White, 0f, 0.3f, 0.001f, 0f, 0.035f, 0.15f).Filter(SfxFilter.HighPass, 5000f, 3000f, 0.1f, 0.7f);
        float[] run = [92.4f, 87.6f, 83.4f, 78.6f];
        for (int i = 0; i < run.Length; i++)
            Tinkle(v, 0.02f + 0.05f * i, run[i], 0.08f - 0.012f * i, i % 2 == 0 ? -0.4f : 0.4f);
        return v;
    }

    private static SfxVoice CountdownTick(int count, float pan)
    {
        // 3, 2, 1: the same E5 beep, each a little louder, brighter and heavier; "1" ends in
        // a riser into Go.
        float tense = (3 - count) / 2f;                                  // 0, 0.5, 1
        var v = new SfxVoice(FlightSoundEvent.CountdownTick, pan, gain: 0.2f + 0.025f * tense, reverbRoom: 0.35f, reverbDamping: 0.5f,
            reverbWet: 0.1f, variation: (uint)count);
        float f = AudioMath.MidiToHz(76f);
        v.Layer(SfxKind.Sine, 0f, 1f, 0.003f, 0.07f, 0.05f, 0.32f).Pitch(f).Fm(2f, 0.9f + 1.1f * tense, 0.05f);
        v.Layer(SfxKind.Square, 0f, 0.07f + 0.1f * tense, 0.003f, 0.07f, 0.05f, 0.3f).Pitch(f).Filter(SfxFilter.LowPass, 2500f + 3500f * tense, 1500f, 0.2f, 0.7f);
        v.Layer(SfxKind.Sine, 0f, 0.25f, 0.003f, 0.07f, 0.05f, 0.3f).Pitch(f * 0.5f);
        // Sub hit and a click.
        v.Layer(SfxKind.Sine, 0f, 0.75f + 0.25f * tense, 0.002f, 0.01f, 0.09f + 0.05f * tense, 0.45f).Pitch(95f, 38f, 0.12f);
        v.Layer(SfxKind.White, 0f, 0.35f, 0.001f, 0f, 0.006f, 0.04f).Filter(SfxFilter.BandPass, 3200f, 3200f, 0f, 1.2f);
        if (count == 1)
        {
            // Riser: noise and a saw sweeping up, swelling, gone just before Go.
            v.Layer(SfxKind.Pink, 0.12f, 0.6f, 0.62f, 0.08f, 0.03f, 0.84f).Filter(SfxFilter.BandPass, 350f, 6000f, 0.75f, 1.4f).Spread(-0.4f, 0.4f, 0.8f);
            v.Layer(SfxKind.Saw, 0.12f, 0.1f, 0.62f, 0.08f, 0.03f, 0.84f).Pitch(AudioMath.MidiToHz(52f), AudioMath.MidiToHz(76f), 0.75f)
                .Filter(SfxFilter.LowPass, 600f, 4000f, 0.75f, 1f);
        }
        return v;
    }

    private static SfxVoice Go(float pan)
    {
        var v = new SfxVoice(FlightSoundEvent.Go, pan, gain: 0.19f, reverbRoom: 0.55f, reverbDamping: 0.45f, reverbWet: 0.16f);
        // A bright A minor chord with E6 (the tick an octave up) on top ...
        float e6 = AudioMath.MidiToHz(88f);
        v.Layer(SfxKind.Sine, 0f, 0.6f, 0.004f, 0.3f, 0.25f, 1.4f).Pitch(e6).Fm(2f, 1.1f, 0.08f);
        v.Layer(SfxKind.Sine, 0f, 0.26f, 0.006f, 0.3f, 0.25f, 1.3f).Pitch(AudioMath.MidiToHz(84f)).Fm(1f, 0.6f, 0.1f);
        v.Layer(SfxKind.Sine, 0f, 0.26f, 0.006f, 0.3f, 0.25f, 1.3f).Pitch(AudioMath.MidiToHz(81f)).Fm(1f, 0.6f, 0.1f);
        foreach (int m in (ReadOnlySpan<int>)[57, 64, 69, 72])
        {
            float f = AudioMath.MidiToHz(m);
            v.Layer(SfxKind.Saw, 0f, 0.07f, 0.004f, 0.15f, 0.3f, 1.2f).Pitch(f * 0.996f).Filter(SfxFilter.LowPass, 6000f, 900f, 0.6f, 0.9f).Spread(-0.35f, -0.35f, 0f);
            v.Layer(SfxKind.Saw, 0f, 0.07f, 0.004f, 0.15f, 0.3f, 1.2f).Pitch(f * 1.005f).Filter(SfxFilter.LowPass, 6000f, 900f, 0.6f, 0.9f).Spread(0.35f, 0.35f, 0f);
        }
        // ... an explosive whoosh tearing outward on both sides ...
        v.Layer(SfxKind.Pink, 0f, 1.4f, 0.006f, 0.04f, 0.25f, 1.1f).Filter(SfxFilter.BandPass, 5500f, 350f, 0.7f, 0.8f).Spread(0f, -0.9f, 0.5f);
        v.Layer(SfxKind.Pink, 0f, 1.4f, 0.006f, 0.04f, 0.25f, 1.1f).Filter(SfxFilter.BandPass, 5000f, 380f, 0.7f, 0.8f).Spread(0f, 0.9f, 0.5f);
        // ... and a kick into a sub drop.
        v.Layer(SfxKind.Sine, 0f, 0.85f, 0.002f, 0.04f, 0.3f, 1.2f).Pitch(120f, 30f, 0.55f);
        v.Layer(SfxKind.White, 0f, 0.4f, 0.001f, 0f, 0.01f, 0.06f).Filter(SfxFilter.BandPass, 2500f, 2500f, 0f, 1f);
        return v;
    }

    private static SfxVoice Finish(int medal, bool record, float pan)
    {
        float gain = medal switch { 3 => 0.17f, 2 => 0.17f, 1 => 0.18f, _ => 0.18f };
        var v = new SfxVoice(FlightSoundEvent.Finish, pan, gain, reverbRoom: 0.62f + 0.04f * medal, reverbDamping: 0.4f,
            reverbWet: 0.22f + 0.04f * medal, variation: (uint)(medal * 2 + (record ? 1 : 0)));
        float end;
        switch (medal)
        {
            case 0:
                // A modest resolve: G4 -> A4 over a soft A minor pad.
                Bell(v, 0f, 67f, 0.8f, 0.6f, 0.3f, 1.2f, 1f);
                Bell(v, 0.22f, 69f, 0.9f, 0.6f, 0.45f, 1.8f, 1f);
                Pad(v, 0.22f, [57, 64, 72], 0.06f, 0.5f, 1.6f);
                end = 0.22f;
                break;
            case 1:
                Impact(v, 0f, 0.5f);
                end = Arpeggio(v, [69f, 72f, 76f], 0.12f, 0.85f, 0.5f);
                Pad(v, end, [57, 64, 69], 0.07f, 0.5f, 1.6f);
                Shimmer(v, end + 0.1f, 4, 0.08f, 0.075f);
                break;
            case 2:
                Impact(v, 0f, 0.75f);
                end = Arpeggio(v, [69f, 72f, 76f, 81f], 0.1f, 0.9f, 0.55f);
                Bell(v, end, 76f, 0.4f, 0.6f, 0.5f, 1.9f, 1f);
                Stab(v, end, [57, 64, 69, 72], 0.06f, 1.8f);
                Shimmer(v, end + 0.1f, 8, 0.09f, 0.075f);
                break;
            default:
                // Gold: an impact, a fast rising C major arpeggio into a big C chord stab with
                // bells, then a long shimmering tail of high tinkles and air.
                Impact(v, 0f, 0.7f);
                end = Arpeggio(v, [72f, 76f, 79f, 84f, 88f], 0.08f, 1f, 0.6f);
                Impact(v, end, 0.5f);
                Bell(v, end, 84f, 0.5f, 0.8f, 0.85f, 3.0f, 2f);
                Bell(v, end, 79f, 0.42f, 0.7f, 0.85f, 3.0f, 1f);
                Bell(v, end, 72f, 0.38f, 0.6f, 0.9f, 3.0f, 1f);
                Stab(v, end, [48, 60, 67, 72, 76], 0.085f, 2.4f);
                Shimmer(v, end + 0.1f, 22, 0.12f, 0.1f);
                Pad(v, end + 0.1f, [64, 67, 72, 76, 84], 0.045f, 1.3f, 3.2f);
                v.Layer(SfxKind.Pink, end, 0.5f, 0.15f, 0.2f, 0.7f, 2.4f).Filter(SfxFilter.HighPass, 7000f, 9000f, 1.5f, 0.7f)
                    .Am(7f, 0.6f).Spread(-0.6f, 0.6f, 2f);
                break;
        }
        if (record)
        {
            // New record: a whoosh up into a fast sparkle run and a top A6 / C7 ring.
            float t0 = end + (medal == 3 ? 0.55f : 0.25f);
            v.Layer(SfxKind.Pink, t0 - 0.3f, 1.1f, 0.28f, 0.02f, 0.06f, 0.5f).Filter(SfxFilter.BandPass, 700f, 8000f, 0.32f, 1.3f).Spread(-0.6f, 0.6f, 0.35f);
            int[] run = [88, 91, 93, 96, 98, 100, 103, 105];
            for (int i = 0; i < run.Length; i++)
                Tinkle(v, t0 + 0.04f * i, run[i], 0.12f, i % 2 == 0 ? -0.5f : 0.5f);
            Bell(v, t0 + 0.34f, 93f, 0.35f, 0.9f, 0.45f, 1.6f, 2f);
            Bell(v, t0 + 0.34f, 96f, 0.25f, 0.9f, 0.45f, 1.6f, 1f);
        }
        return v;
    }

    private static SfxVoice BoostIgnite(float pan)
    {
        var v = new SfxVoice(FlightSoundEvent.BoostIgnite, pan, gain: 0.29f);
        // A sub punch you feel ...
        v.Layer(SfxKind.Sine, 0f, 0.9f, 0.002f, 0.03f, 0.12f, 0.55f).Pitch(105f, 36f, 0.16f);
        // ... a burst of driven noise opening up ...
        v.Layer(SfxKind.Pink, 0f, 2.2f, 0.008f, 0.05f, 0.14f, 0.7f).Filter(SfxFilter.BandPass, 300f, 3800f, 0.14f, 0.9f).Drive(1.6f);
        v.Layer(SfxKind.White, 0f, 0.3f, 0.002f, 0.03f, 0.08f, 0.4f).Filter(SfxFilter.BandPass, 5200f, 5200f, 0f, 1.5f).Am(33f, 0.9f);
        // ... and a turbine whine spooling up.
        v.Layer(SfxKind.Sine, 0.02f, 0.26f, 0.06f, 0.4f, 0.18f, 0.85f).Pitch(650f, 2600f, 0.5f).Fm(0.5f, 0.6f, 0.4f);
        v.Layer(SfxKind.Saw, 0.02f, 0.07f, 0.06f, 0.4f, 0.18f, 0.85f).Pitch(325f, 1300f, 0.5f).Filter(SfxFilter.LowPass, 1200f, 5000f, 0.5f, 1.2f);
        return v;
    }

    private static SfxVoice BoostEmpty(float pan)
    {
        var v = new SfxVoice(FlightSoundEvent.BoostEmpty, pan, gain: 0.55f);
        // A sputtering power-down: a falling saw chopped at two clashing rates, puffs of
        // noise and a few backfire pops, ending in a dull thunk.
        v.Layer(SfxKind.Saw, 0f, 0.5f, 0.006f, 0.15f, 0.14f, 0.6f).Pitch(220f, 55f, 0.5f)
            .Filter(SfxFilter.LowPass, 1800f, 250f, 0.45f, 0.8f).Am(13f, 0.85f).Drive(1.4f);
        v.Layer(SfxKind.Pink, 0f, 0.9f, 0.006f, 0.08f, 0.14f, 0.55f).Filter(SfxFilter.LowPass, 2800f, 350f, 0.4f, 0.7f).Am(7.3f, 0.9f);
        float[] pops = [0.07f, 0.19f, 0.27f, 0.41f];
        for (int i = 0; i < pops.Length; i++)
            v.Layer(SfxKind.White, pops[i], 0.55f - 0.08f * i, 0.001f, 0.004f, 0.015f, 0.07f)
                .Filter(SfxFilter.BandPass, 900f + 500f * i, 600f, 0.05f, 1.2f).Spread(i % 2 == 0 ? -0.3f : 0.3f, 0f, 0.05f);
        v.Layer(SfxKind.Sine, 0.42f, 0.5f, 0.003f, 0.01f, 0.07f, 0.3f).Pitch(80f, 40f, 0.1f);
        return v;
    }

    private static SfxVoice RockHit(float s, float pan)
    {
        var v = new SfxVoice(FlightSoundEvent.RockHit, pan, gain: 0.13f + 0.14f * s, variation: BitsOf(s) ^ BitsOf(pan) * 3u);
        uint h = Hash(BitsOf(s) * 0x2545F491u ^ BitsOf(pan));
        // Thump: a sine pitch drop, lower and longer with strength.
        v.Layer(SfxKind.Sine, 0f, 0.25f + 0.75f * s, 0.002f, 0.01f, 0.05f + 0.13f * s, 0.25f + 0.5f * s)
            .Pitch(140f - 40f * s, 55f - 20f * s, 0.07f + 0.1f * s);
        // Crunch: driven band-passed noise falling in pitch.
        v.Layer(SfxKind.White, 0f, 0.6f + 0.6f * s, 0.002f, 0.005f, 0.03f + 0.08f * s, 0.2f + 0.35f * s)
            .Filter(SfxFilter.BandPass, 2600f - 1200f * s, 600f, 0.7f, 0.8f).Drive(1f + 1.5f * s);
        // Metal / ice ring: inharmonic FM partials.
        float fr = 360f + 260f * Rand(h, 0);
        v.Layer(SfxKind.Sine, 0.002f, 0.12f + 0.2f * s, 0.002f, 0f, 0.08f + 0.18f * s, 0.25f + 0.55f * s).Pitch(fr, fr * 0.94f, 0.3f).Fm(1.414f, 2.2f, 0.06f);
        v.Layer(SfxKind.Sine, 0.002f, 0.06f + 0.1f * s, 0.002f, 0f, 0.06f + 0.1f * s, 0.2f + 0.4f * s).Pitch(fr * 2.76f);
        // Debris: a scatter of small grains around the hit.
        int grains = 2 + (int)(6f * s);
        for (int i = 0; i < grains; i++)
        {
            float t = 0.02f + (0.08f + 0.3f * s) * Rand(h, 10 + i);
            float a = (0.15f + 0.25f * Rand(h, 30 + i)) * (0.5f + 0.5f * s);
            v.Layer(SfxKind.White, t, a, 0.001f, 0.002f, 0.012f, 0.05f)
                .Filter(SfxFilter.BandPass, 2500f + 4500f * Rand(h, 50 + i), 2000f, 0.04f, 2f).Spread((Rand(h, 70 + i) - 0.5f) * 1.2f, 0f, 0f);
        }
        // Graze: a light high scrape that matters only for soft hits.
        v.Layer(SfxKind.White, 0f, 0.5f * (1f - s), 0.004f, 0.03f, 0.05f, 0.25f)
            .Filter(SfxFilter.BandPass, 3000f, 1800f, 0.2f, 0.9f).Am(37f, 0.5f);
        return v;
    }

    private static SfxVoice RockPass(float s, float speed, float pan)
    {
        // A near miss: a band of noise whose pitch falls as the rock sweeps past (doppler),
        // panning from in front out to its side, with a low "vwom" of mass.
        float side = MathF.Abs(pan) < 0.5f ? (pan < 0f ? -0.5f : 0.5f) : pan;
        if (pan == 0f) side = 0.5f;
        float dur = 0.4f - 0.14f * speed;
        uint h = Hash(BitsOf(s) * 0x9E3779B1u ^ BitsOf(pan) ^ BitsOf(speed) * 7u);
        var v = new SfxVoice(FlightSoundEvent.RockPass, 0f, gain: 0.09f + 0.08f * s, variation: h);
        float hi = (2000f + 2800f * speed) * (0.85f + 0.3f * Rand(h, 0));
        v.Layer(SfxKind.Pink, 0f, 2.2f, dur * 0.35f, 0.01f, dur * 0.2f, dur)
            .Filter(SfxFilter.BandPass, hi, hi * 0.22f, dur * 0.85f, 1.6f).Spread(side * 0.15f, side, dur * 0.8f);
        v.Layer(SfxKind.White, 0f, 0.25f, dur * 0.3f, 0f, dur * 0.12f, dur * 0.8f)
            .Filter(SfxFilter.HighPass, 6000f, 3500f, dur * 0.6f, 0.7f).Spread(side * 0.3f, side, dur * 0.6f);
        float f = 170f + 60f * Rand(h, 1);
        v.Layer(SfxKind.Sine, 0f, 0.35f * (0.5f + 0.5f * s), dur * 0.35f, 0f, dur * 0.2f, dur).Pitch(f, f * 0.55f, dur * 0.9f).Spread(side * 0.15f, side * 0.7f, dur * 0.8f);
        return v;
    }

    private static SfxVoice BrakeOn(float speed, float pan)
    {
        // Air brake: a mechanical thunk and a pneumatic hiss, louder at speed.
        var v = new SfxVoice(FlightSoundEvent.BrakeOn, pan, gain: 0.2f + 0.1f * speed);
        v.Layer(SfxKind.Sine, 0f, 0.6f, 0.002f, 0.01f, 0.05f, 0.22f).Pitch(130f, 62f, 0.06f);
        v.Layer(SfxKind.White, 0f, 0.4f, 0.001f, 0f, 0.008f, 0.05f).Filter(SfxFilter.BandPass, 1600f, 1600f, 0f, 1.5f);
        v.Layer(SfxKind.White, 0.015f, 0.55f, 0.012f, 0.06f, 0.14f, 0.5f).Filter(SfxFilter.BandPass, 5200f, 2600f, 0.35f, 0.9f).Spread(-0.45f, -0.45f, 0f);
        v.Layer(SfxKind.White, 0.02f, 0.55f, 0.012f, 0.06f, 0.14f, 0.5f).Filter(SfxFilter.BandPass, 4800f, 2400f, 0.35f, 0.9f).Spread(0.45f, 0.45f, 0f);
        return v;
    }

    private static SfxVoice EnterFlight(float pan)
    {
        var v = new SfxVoice(FlightSoundEvent.EnterFlight, pan, gain: 0.37f, reverbRoom: 0.45f, reverbDamping: 0.5f, reverbWet: 0.35f);
        // Power-up: a rising A1 -> A3 sweep (saw + a fifth above, opening low-pass) with a
        // noise riser, then a soft impact: thump, an A5 / E6 ping and a breath of air.
        v.Layer(SfxKind.Saw, 0f, 0.42f, 0.35f, 0.55f, 0.08f, 1.0f).Pitch(55f, 220f, 0.95f).Filter(SfxFilter.LowPass, 250f, 3200f, 0.95f, 0.9f);
        v.Layer(SfxKind.Saw, 0f, 0.28f, 0.35f, 0.55f, 0.08f, 1.0f).Pitch(82.4f, 329.6f, 0.95f).Filter(SfxFilter.LowPass, 250f, 3200f, 0.95f, 0.9f);
        v.Layer(SfxKind.Sine, 0f, 0.4f, 0.35f, 0.55f, 0.08f, 1.0f).Pitch(27.5f, 110f, 0.95f);
        v.Layer(SfxKind.Pink, 0.2f, 0.9f, 0.7f, 0.02f, 0.04f, 0.8f).Filter(SfxFilter.BandPass, 400f, 5000f, 0.75f, 1.2f).Spread(-0.5f, 0.5f, 0.75f);
        Impact(v, 0.95f, 0.45f);
        v.Layer(SfxKind.Sine, 0.95f, 0.45f, 0.002f, 0.01f, 0.12f, 0.6f).Pitch(AudioMath.MidiToHz(81f)).Fm(2f, 0.8f, 0.03f);
        Tinkle(v, 0.99f, 88f, 0.14f, 0.3f);
        return v;
    }

    private static SfxVoice ExitFlight(float pan)
    {
        var v = new SfxVoice(FlightSoundEvent.ExitFlight, pan, gain: 0.36f, reverbRoom: 0.45f, reverbDamping: 0.5f, reverbWet: 0.35f);
        // Power-down: a click-out, the sweep in reverse (A3 -> A1, closing low-pass) with a
        // falling breath of noise, settling into a soft thud.
        v.Layer(SfxKind.Sine, 0f, 0.35f, 0.002f, 0.01f, 0.05f, 0.25f).Pitch(AudioMath.MidiToHz(76f)).Fm(2f, 0.6f, 0.02f);
        v.Layer(SfxKind.White, 0f, 0.3f, 0.001f, 0f, 0.006f, 0.04f).Filter(SfxFilter.BandPass, 3000f, 3000f, 0f, 1.5f);
        v.Layer(SfxKind.Saw, 0.03f, 0.42f, 0.02f, 0.3f, 0.12f, 0.8f).Pitch(220f, 55f, 0.7f).Filter(SfxFilter.LowPass, 2800f, 200f, 0.7f, 0.9f);
        v.Layer(SfxKind.Saw, 0.03f, 0.28f, 0.02f, 0.3f, 0.12f, 0.8f).Pitch(329.6f, 82.4f, 0.7f).Filter(SfxFilter.LowPass, 2800f, 200f, 0.7f, 0.9f);
        v.Layer(SfxKind.Sine, 0.03f, 0.4f, 0.02f, 0.3f, 0.12f, 0.8f).Pitch(110f, 27.5f, 0.7f);
        v.Layer(SfxKind.Pink, 0.03f, 0.8f, 0.03f, 0.15f, 0.2f, 0.7f).Filter(SfxFilter.BandPass, 4000f, 300f, 0.6f, 1.1f).Spread(0.5f, -0.5f, 0.6f);
        v.Layer(SfxKind.Sine, 0.62f, 0.4f, 0.004f, 0.01f, 0.1f, 0.4f).Pitch(70f, 35f, 0.12f);
        return v;
    }

    // ---- instruments -------------------------------------------------------------------

    /// <summary>An FM bell: the fundamental with a decaying FM brightness (modulator at
    /// <paramref name="fmRatio"/> × the pitch), a slightly detuned octave partial, a short
    /// 3rd partial and a quiet sub-octave body.</summary>
    private static void Bell(SfxVoice v, float start, float midi, float amp, float bright, float decay, float length, float fmRatio)
    {
        float f = AudioMath.MidiToHz(midi);
        v.Layer(SfxKind.Sine, start, amp, 0.003f, 0f, decay, length).Pitch(f).Fm(fmRatio, 1.3f * bright, 0.1f);
        v.Layer(SfxKind.Sine, start, (0.12f + 0.1f * bright) * amp, 0.003f, 0f, decay * 0.55f, length * 0.7f).Pitch(f * 2.003f);
        v.Layer(SfxKind.Sine, start, 0.07f * amp * bright, 0.002f, 0f, 0.1f, 0.5f).Pitch(f * 3f);
        v.Layer(SfxKind.Sine, start, 0.16f * amp, 0.01f, 0f, decay * 0.9f, length).Pitch(f * 0.5f);
    }

    /// <summary>Plays notes as bells <paramref name="step"/> seconds apart; the last one
    /// rings longer. Returns the start time of the last note.</summary>
    private static float Arpeggio(SfxVoice v, ReadOnlySpan<float> notes, float step, float amp, float lastDecay)
    {
        for (int i = 0; i < notes.Length; i++)
        {
            bool last = i == notes.Length - 1;
            Bell(v, step * i, notes[i], amp * (last ? 1f : 0.8f), 0.9f, last ? lastDecay : 0.2f, last ? 2.0f : 0.8f, 1f);
        }
        return step * (notes.Length - 1);
    }

    /// <summary>A tiny high glassy ping for shimmer and sparkle runs, placed at
    /// <paramref name="pan"/> relative to the voice.</summary>
    private static void Tinkle(SfxVoice v, float start, float midi, float amp, float pan = 0f)
    {
        var l = v.Layer(SfxKind.Sine, start, amp, 0.002f, 0f, 0.09f, 0.45f).Pitch(AudioMath.MidiToHz(midi)).Fm(2f, 0.5f, 0.04f);
        if (pan != 0f) l.Spread(pan, pan, 0f);
    }

    /// <summary>A big impact: a sub boom, a kick click and a short wide crash.</summary>
    private static void Impact(SfxVoice v, float start, float size)
    {
        v.Layer(SfxKind.Sine, start, 0.8f * size, 0.002f, 0.03f, 0.18f + 0.15f * size, 0.5f + 0.6f * size).Pitch(95f, 32f, 0.25f);
        v.Layer(SfxKind.White, start, 0.35f * size, 0.001f, 0f, 0.008f, 0.05f).Filter(SfxFilter.BandPass, 2200f, 2200f, 0f, 1f);
        v.Layer(SfxKind.White, start, 0.3f * size, 0.002f, 0.01f, 0.2f + 0.2f * size, 0.6f + 0.6f * size)
            .Filter(SfxFilter.HighPass, 3000f, 6000f, 0.5f, 0.6f).Spread(-0.55f, -0.55f, 0f);
        v.Layer(SfxKind.White, start, 0.3f * size, 0.002f, 0.01f, 0.2f + 0.2f * size, 0.6f + 0.6f * size)
            .Filter(SfxFilter.HighPass, 3200f, 6500f, 0.5f, 0.6f).Spread(0.55f, 0.55f, 0f);
    }

    /// <summary>A brassy chord stab: detuned saw pairs (spread left / right) through a
    /// closing low-pass.</summary>
    private static void Stab(SfxVoice v, float start, ReadOnlySpan<int> notes, float amp, float length)
    {
        foreach (int m in notes)
        {
            float f = AudioMath.MidiToHz(m);
            v.Layer(SfxKind.Saw, start, amp, 0.004f, 0.08f, 0.45f, length).Pitch(f * 0.996f).Filter(SfxFilter.LowPass, 7000f, 800f, 0.9f, 0.9f).Spread(-0.4f, -0.4f, 0f);
            v.Layer(SfxKind.Saw, start, amp, 0.004f, 0.08f, 0.45f, length).Pitch(f * 1.005f).Filter(SfxFilter.LowPass, 7000f, 800f, 0.9f, 0.9f).Spread(0.4f, 0.4f, 0f);
        }
    }

    /// <summary>A soft pad: detuned saws through a fixed low-pass, slow attack.</summary>
    private static void Pad(SfxVoice v, float start, ReadOnlySpan<int> notes, float amp, float decay, float length)
    {
        foreach (int m in notes)
        {
            float f = AudioMath.MidiToHz(m);
            v.Layer(SfxKind.Saw, start, amp, 0.15f, 0.3f, decay, length).Pitch(f * 0.997f).Filter(SfxFilter.LowPass, 1400f, 900f, 1f, 0.7f).Spread(-0.3f, -0.3f, 0f);
            v.Layer(SfxKind.Saw, start, amp, 0.15f, 0.3f, decay, length).Pitch(f * 1.004f).Filter(SfxFilter.LowPass, 1400f, 900f, 1f, 0.7f).Spread(0.3f, 0.3f, 0f);
        }
    }

    /// <summary>A shimmering tail: <paramref name="count"/> high pentatonic tinkles
    /// <paramref name="step"/> seconds apart, scattered left and right, fading.</summary>
    private static void Shimmer(SfxVoice v, float start, int count, float amp, float step)
    {
        int[] notes = [93, 96, 100, 98, 103, 100, 105, 103];
        for (int i = 0; i < count; i++)
            Tinkle(v, start + step * i, notes[i % notes.Length], amp * (1f - 0.6f * i / count), (i & 1) == 0 ? -0.55f : 0.55f);
    }

    // ---- helpers -----------------------------------------------------------------------

    /// <summary>The A minor pentatonic note <paramref name="steps"/> scale steps above
    /// (or, negative, below) <paramref name="midi"/> (snapped down to the scale first).</summary>
    internal static int PentaStep(int midi, int steps)
    {
        int rel = midi - 57;                                             // A3 = 57
        int oct = (int)MathF.Floor(rel / 12f);
        int pc = rel - 12 * oct;
        int deg = 0;
        for (int i = 0; i < Pentatonic.Length; i++) if (Pentatonic[i] <= pc) deg = i;
        int d = oct * 5 + deg + steps;
        int o = (int)MathF.Floor(d / 5f);
        return 57 + 12 * o + Pentatonic[d - 5 * o];
    }

    private static float Fin(float x, float lo, float hi) => float.IsFinite(x) ? Math.Clamp(x, lo, hi) : 0f;

    private static uint BitsOf(float x) => BitConverter.SingleToUInt32Bits(float.IsFinite(x) ? x : 0f);

    private static uint Hash(uint x)
    {
        x ^= x >> 16; x *= 0x7FEB352Du;
        x ^= x >> 15; x *= 0x846CA68Bu;
        x ^= x >> 16;
        return x;
    }

    /// <summary>A deterministic number 0 .. 1 from <paramref name="h"/> and a slot.</summary>
    private static float Rand(uint h, int slot) => (Hash(h + (uint)slot * 0x9E3779B9u) >> 8) / 16777216f;
}

/// <summary>The waveform of an <see cref="SfxLayer"/>.</summary>
internal enum SfxKind { Sine, Saw, Square, Triangle, White, Pink }

/// <summary>The filter of an <see cref="SfxLayer"/>.</summary>
internal enum SfxFilter { None, LowPass, HighPass, BandPass }

/// <summary>One element of a one-shot sound: an oscillator or a noise source with a pitch
/// glide, an optional filter sweep, FM / amplitude modulation, sample-and-hold, drive, an
/// envelope (raised-cosine attack, hold, exponential decay, raised-cosine fade at the end)
/// and an optional pan sweep of its own.</summary>
internal sealed class SfxLayer
{
    private const float Sr = AudioFormat.SampleRate;

    private readonly SfxKind _kind;
    private readonly float _amp;
    internal readonly int Start, Length;
    private readonly int _attack, _hold, _fade;
    private readonly float _decayMul;
    private readonly float _voicePan;

    private float _f0 = 440f, _f1 = 440f, _glideMul = 1f;
    private int _glide;
    private float _fmRatio, _fmIndex, _fmMul = 1f;
    private SfxFilter _filter;
    private float _ff0, _ff1, _fq = 0.707f;
    private int _fglide;
    private float _amRate, _amDepth;
    private float _drive;
    private int _holdN;
    private float _sp0, _sp1;
    private int _spGlide;

    /// <summary>True when the layer has its own pan (see <see cref="Spread"/>).</summary>
    internal bool Stereo;
    /// <summary>The layer's current left / right gains (when <see cref="Stereo"/>).</summary>
    internal float GainL, GainR;

    private float _freq, _phase, _modPhase, _index, _decayEnv = 1f, _amPhase, _held;
    private Oscillator _osc;
    private Noise _noise;
    private Biquad _biquad;

    internal SfxLayer(SfxKind kind, float start, float amp, float attack, float hold, float decay, float length, uint seed, float voicePan)
    {
        _kind = kind;
        _amp = amp;
        Start = (int)(start * Sr);
        Length = Math.Max(64, (int)(length * Sr));
        _attack = Math.Max(1, (int)(attack * Sr));
        _hold = (int)(hold * Sr);
        _fade = Math.Min((int)(0.06f * Sr), Length / 4);
        _decayMul = MathF.Exp(-1f / (MathF.Max(decay, 0.001f) * Sr));
        _noise = new Noise(seed);
        _freq = _f0;
        _voicePan = voicePan;
    }

    /// <summary>Pitch gliding exponentially from <paramref name="from"/> to
    /// <paramref name="to"/> over <paramref name="glide"/> seconds.</summary>
    internal SfxLayer Pitch(float from, float to = -1f, float glide = 0f)
    {
        _f0 = from;
        _f1 = to > 0f ? to : from;
        _glide = (int)(glide * Sr);
        _glideMul = _glide > 0 ? MathF.Pow(_f1 / _f0, 1f / _glide) : 1f;
        _freq = _f0;
        return this;
    }

    /// <summary>Phase modulation by a sine at <paramref name="ratio"/> × the pitch; the
    /// index decays with time constant <paramref name="decay"/> seconds.</summary>
    internal SfxLayer Fm(float ratio, float index, float decay)
    {
        _fmRatio = ratio;
        _fmIndex = _index = index;
        _fmMul = MathF.Exp(-1f / (MathF.Max(decay, 0.001f) * Sr));
        return this;
    }

    /// <summary>A filter whose frequency sweeps exponentially from <paramref name="from"/>
    /// to <paramref name="to"/> over <paramref name="glide"/> seconds.</summary>
    internal SfxLayer Filter(SfxFilter kind, float from, float to, float glide, float q)
    {
        _filter = kind;
        _ff0 = from;
        _ff1 = to;
        _fglide = Math.Max(1, (int)(glide * Sr));
        _fq = q;
        return this;
    }

    /// <summary>Tremolo / chopping: the gain dips by <paramref name="depth"/> at
    /// <paramref name="rate"/> Hz.</summary>
    internal SfxLayer Am(float rate, float depth)
    {
        _amRate = rate;
        _amDepth = Math.Clamp(depth, 0f, 1f);
        return this;
    }

    /// <summary>Soft saturation after the filter: the signal is driven by
    /// <paramref name="drive"/> into <see cref="AudioMath.SoftClip"/> and scaled back.</summary>
    internal SfxLayer Drive(float drive)
    {
        _drive = MathF.Max(drive, 0f);
        return this;
    }

    /// <summary>Sample-and-hold of the raw source every <paramref name="samples"/> samples
    /// (a crude, steppy "glitch" decimation).</summary>
    internal SfxLayer Hold(int samples)
    {
        _holdN = Math.Max(1, samples);
        return this;
    }

    /// <summary>Gives the layer its own pan: an offset from the voice's pan sweeping
    /// linearly from <paramref name="from"/> to <paramref name="to"/> over
    /// <paramref name="glide"/> seconds (narrowed when the voice itself is panned hard).</summary>
    internal SfxLayer Spread(float from, float to, float glide)
    {
        Stereo = true;
        float narrow = 1f - 0.5f * MathF.Abs(_voicePan);
        _sp0 = from * narrow;
        _sp1 = to * narrow;
        _spGlide = Math.Max(1, (int)(glide * Sr));
        AudioMath.Pan(_voicePan + _sp0, out GainL, out GainR);
        return this;
    }

    /// <summary>The layer's sample at voice sample <paramref name="n"/> (called once per
    /// sample, in order).</summary>
    internal float Next(int n)
    {
        int t = n - Start;
        if ((uint)t >= (uint)Length) return 0f;

        float env;
        if (t < _attack) env = 0.5f - 0.5f * MathF.Cos(MathF.PI * t / _attack);
        else if (t < _attack + _hold) env = 1f;
        else env = _decayEnv *= _decayMul;
        int fadeFrom = Length - _fade;
        if (t > fadeFrom) env *= 0.5f + 0.5f * MathF.Cos(MathF.PI * (t - fadeFrom) / _fade);

        if (t < _glide) _freq *= _glideMul;
        else _freq = _f1;

        float x;
        switch (_kind)
        {
            case SfxKind.Sine:
            {
                float mod = 0f;
                if (_fmIndex > 0f)
                {
                    mod = _index * MathF.Sin(AudioMath.TwoPi * _modPhase);
                    _modPhase += _freq * _fmRatio / Sr;
                    if (_modPhase >= 1f) _modPhase -= MathF.Floor(_modPhase);
                    _index *= _fmMul;
                }
                x = MathF.Sin(AudioMath.TwoPi * _phase + mod);
                _phase += _freq / Sr;
                if (_phase >= 1f) _phase -= MathF.Floor(_phase);
                break;
            }
            case SfxKind.Saw: x = _osc.Next(_freq, OscShape.Saw); break;
            case SfxKind.Square: x = _osc.Next(_freq, OscShape.Square); break;
            case SfxKind.Triangle: x = _osc.Next(_freq, OscShape.Triangle); break;
            case SfxKind.White: x = _noise.White(); break;
            default: x = _noise.Pink(); break;
        }

        if (_holdN > 1)
        {
            if (t % _holdN == 0) _held = x;
            x = _held;
        }

        if ((t & 31) == 0)
        {
            if (_filter != SfxFilter.None)
            {
                float u = MathF.Min(1f, (float)t / _fglide);
                float ff = _ff0 * MathF.Pow(_ff1 / _ff0, u);
                switch (_filter)
                {
                    case SfxFilter.LowPass: _biquad.SetLowPass(ff, _fq); break;
                    case SfxFilter.HighPass: _biquad.SetHighPass(ff, _fq); break;
                    default: _biquad.SetBandPass(ff, _fq); break;
                }
            }
            if (Stereo)
            {
                float u = MathF.Min(1f, (float)t / _spGlide);
                AudioMath.Pan(_voicePan + _sp0 + (_sp1 - _sp0) * u, out GainL, out GainR);
            }
        }
        if (_filter != SfxFilter.None) x = _biquad.Process(x);

        if (_drive > 0f) x = AudioMath.SoftClip(_drive * x) / _drive;

        if (_amDepth > 0f)
        {
            x *= 1f - _amDepth * (0.5f - 0.5f * MathF.Cos(AudioMath.TwoPi * _amPhase));
            _amPhase += _amRate / Sr;
            if (_amPhase >= 1f) _amPhase -= 1f;
        }

        return x * env * _amp;
    }
}

/// <summary>A one-shot effect: a set of <see cref="SfxLayer"/>s — centred ones summed and
/// panned with the voice, stereo ones placed by their own pan — DC-blocked per channel,
/// with an optional private reverb tail. Mix returns false once every layer is over and
/// the output has stayed below about -70 dBFS for a short while (or at the 5 s cap).</summary>
internal sealed class SfxVoice : IAudioSource
{
    private const int MaxLayers = 128;
    private const float SilenceLevel = 3.0e-4f;              // about -70 dBFS
    private const int SilenceRun = 1024;                     // ~21 ms of silence ends it
    private const int CapSamples = (int)(4.8f * AudioFormat.SampleRate);
    private const int CapFade = (int)(0.15f * AudioFormat.SampleRate);
    private const int FadeIn = 48;                           // 1 ms master fade-in
    private const float DcR = 0.9985f;                       // ~11 Hz DC blocker

    private readonly SfxLayer[] _layers = new SfxLayer[MaxLayers];
    private int _count;
    private readonly uint _seed;
    private readonly float _pan, _gain, _panL, _panR, _wetL, _wetR;
    private readonly Reverb? _reverb;
    private int _dryEnd;
    private int _n, _quiet;
    private float _dcXL, _dcYL, _dcXR, _dcYR;
    private bool _finished;

    internal SfxVoice(FlightSoundEvent ev, float pan, float gain, float reverbRoom = 0f, float reverbDamping = 0.5f, float reverbWet = 0f, uint variation = 0u)
    {
        _seed = 0x51F15EEDu ^ ((uint)ev * 0x9E3779B1u) ^ (variation * 0x85EBCA6Bu);
        _pan = Math.Clamp(float.IsFinite(pan) ? pan : 0f, -1f, 1f);
        // Equal-power panning makes a hard-panned sound 3 dB hotter on its side than a
        // centred one; take most of that back so the level windows hold at any pan.
        _gain = gain * (1f - 0.25f * MathF.Abs(_pan));
        AudioMath.Pan(_pan, out _panL, out _panR);
        if (reverbWet > 0f)
        {
            _reverb = new Reverb { RoomSize = reverbRoom, Damping = reverbDamping };
            // The tail leans toward the source side too, so a panned sound stays placed.
            _wetL = reverbWet * (0.3f + 0.7f * MathF.Sqrt(2f) * _panL);
            _wetR = reverbWet * (0.3f + 0.7f * MathF.Sqrt(2f) * _panR);
        }
    }

    /// <summary>Adds a layer (times in seconds; <paramref name="decay"/> is the exponential
    /// time constant after the hold).</summary>
    internal SfxLayer Layer(SfxKind kind, float start, float amp, float attack, float hold, float decay, float length)
    {
        var layer = new SfxLayer(kind, start, amp, attack, hold, decay, length, _seed + (uint)_count * 0x2545F491u + 1u, _pan);
        if (_count < MaxLayers)
        {
            _layers[_count++] = layer;
            _dryEnd = Math.Max(_dryEnd, layer.Start + layer.Length);
        }
        return layer;
    }

    public bool Mix(Span<float> buffer, int frames)
    {
        if (_finished) return false;
        frames = Math.Min(frames, buffer.Length / 2);
        for (int i = 0; i < frames; i++)
        {
            float m = 0f, sl = 0f, sr = 0f;
            if (_n < _dryEnd)
                for (int k = 0; k < _count; k++)
                {
                    var layer = _layers[k];
                    float x = layer.Next(_n);
                    if (layer.Stereo) { sl += x * layer.GainL; sr += x * layer.GainR; }
                    else m += x;
                }
            float inL = m * _panL + sl, inR = m * _panR + sr;

            float l = inL - _dcXL + DcR * _dcYL;
            _dcXL = inL;
            _dcYL = MathF.Abs(l) < 1e-20f ? 0f : l;
            float r = inR - _dcXR + DcR * _dcYR;
            _dcXR = inR;
            _dcYR = MathF.Abs(r) < 1e-20f ? 0f : r;

            float g = _gain;
            if (_n < FadeIn) g *= 0.5f - 0.5f * MathF.Cos(MathF.PI * _n / FadeIn);
            int capFrom = CapSamples - CapFade;
            float capG = 1f;
            if (_n > capFrom) capG = 0.5f + 0.5f * MathF.Cos(MathF.PI * Math.Min(1f, (float)(_n - capFrom) / CapFade));
            l *= g * capG;
            r *= g * capG;

            if (_reverb is not null)
            {
                float mono = (l + r) * 0.7071f;
                _reverb.Process(mono, mono, out float wl, out float wr);
                l += wl * capG * _wetL;
                r += wr * capG * _wetR;
            }
            if (!float.IsFinite(l)) l = 0f;
            if (!float.IsFinite(r)) r = 0f;
            buffer[2 * i] += l;
            buffer[2 * i + 1] += r;

            _n++;
            if (_n >= _dryEnd && MathF.Abs(l) < SilenceLevel && MathF.Abs(r) < SilenceLevel) _quiet++;
            else _quiet = 0;
            if (_quiet >= SilenceRun || _n >= CapSamples) _finished = true;
            if (_finished) break;
        }
        return !_finished;
    }
}
