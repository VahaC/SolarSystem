namespace SolarSystem;

// PROTOTYPE (2026-11-17): arcade flight mode — sound: the game-thread side. Owns the
// mixer, the shared state the continuous sounds read, and plays one-shot effects.

/// <summary>
/// Sound for flight mode. Nothing is created and no audio device is opened until flight
/// mode is entered for the first time; after that the mixer keeps running (silent while
/// <see cref="FlightSoundState.Active"/> is 0) until the app closes.
/// </summary>
public sealed class FlightAudio : IDisposable
{
    public AudioEngine Engine { get; } = new();
    /// <summary>What the flight is doing — the game thread overwrites it every frame.</summary>
    public FlightSoundState State { get; } = new();

    /// <summary>All flight sound on / off.</summary>
    public bool SoundEnabled { get; set; } = true;
    /// <summary>The soundtrack on / off (effects and engines stay).</summary>
    public bool MusicEnabled { get; set; } = true;
    /// <summary>Master volume 0 .. 1.</summary>
    public float Volume { get; set; } = 0.8f;
    /// <summary>Never open an audio device: the mix is only rendered by calling
    /// <see cref="AudioEngine.Render"/> (the hidden demo, tests).</summary>
    public bool Offline { get; set; }

    /// <summary>Mix balance: the level each layer goes into the mixer at. The one-shot
    /// effects are the reference; the two continuous beds sit under them so that a gate
    /// or a hit always reads over the engines and the soundtrack.</summary>
    public const float EngineLevel = 1f, MusicLevel = 1.25f, EffectsLevel = 1f;

    /// <summary>How far the engines dip under a big effect (a gate, the start signal, a
    /// hard hit) and how long they take to come back, so the effect reads clearly.</summary>
    public const float DuckDepth = 0.3f, DuckSeconds = 0.45f;

    /// <summary>Development only (the hidden demo's <c>--solo</c>): render one layer alone —
    /// <c>engine</c>, <c>music</c> or <c>sfx</c>; null for the full mix.</summary>
    public string? Solo { get; set; }

    /// <summary>True once the sources exist (flight mode has been entered at least once).</summary>
    public bool Started { get; private set; }

    private FlightMusic? _music;
    private FlightSoundscape? _engine;
    private float _duck;   // 1 right after a big effect, falling back to 0

    private bool Plays(string layer) => Solo == null || Solo == layer;

    /// <summary>How much of the engine dip an effect asks for, 0 .. 1.</summary>
    public static float DuckFor(in FlightSoundCue cue) => cue.Event switch
    {
        FlightSoundEvent.GatePassed or FlightSoundEvent.Go or FlightSoundEvent.Finish => 1f,
        FlightSoundEvent.GateMissed or FlightSoundEvent.CountdownTick => 0.7f,
        FlightSoundEvent.RockHit => float.IsFinite(cue.Strength) ? Math.Clamp(cue.Strength, 0f, 1f) : 0f,
        _ => 0f,
    };

    /// <summary>The engine layer's gain right now (its level, dipped under a big effect).</summary>
    public float EngineGain => EngineLevel * (1f - DuckDepth * _duck);

    /// <summary>Once per game frame: let the engine dip recover.</summary>
    public void Update(float dt)
    {
        if (!Started || _duck <= 0f || !(dt > 0f)) return;
        _duck = MathF.Max(0f, _duck - dt / DuckSeconds);
        if (_engine != null && Plays("engine")) Engine.SetSourceGain(_engine, EngineGain);
    }

    /// <summary>Create the continuous sources and open the audio device (unless
    /// <see cref="Offline"/>). Safe to call every time flight mode is entered.</summary>
    public void EnsureStarted()
    {
        if (!Started)
        {
            Started = true;
            // Persistent: a burst of one-shot effects must never push these out of the mix.
            _engine = new FlightSoundscape(State);
            Engine.AddSource(_engine, 0f, persistent: true);
            _music = new FlightMusic(State);
            Engine.AddSource(_music, 0f, persistent: true);
        }
        ApplySettings();
        if (!Offline && !Engine.IsRunning) Engine.Start();
    }

    /// <summary>Push the on / off switches and the volume to the mixer.</summary>
    public void ApplySettings()
    {
        Engine.MasterVolume = Math.Clamp(Volume, 0f, 1f);
        Engine.Muted = !SoundEnabled;
        if (_engine != null) Engine.SetSourceGain(_engine, Plays("engine") ? EngineGain : 0f);
        if (_music != null) Engine.SetSourceGain(_music, MusicEnabled && Plays("music") ? MusicLevel : 0f);
    }

    /// <summary>Play a one-shot effect.</summary>
    public void Play(FlightSoundCue cue)
    {
        if (!Started || !SoundEnabled) return;
        // The engines make room for it (also when only the engine layer is rendered, so
        // that stem shows what the mix really gets).
        float duck = DuckFor(in cue);
        if (duck > _duck)
        {
            _duck = duck;
            if (_engine != null && Plays("engine")) Engine.SetSourceGain(_engine, EngineGain);
        }
        if (!Plays("sfx")) return;
        cue.Pan = float.IsFinite(cue.Pan) ? Math.Clamp(cue.Pan, -1f, 1f) : 0f;
        var source = FlightSfx.Create(in cue);
        if (source != null) Engine.AddSource(source, EffectsLevel);
    }

    /// <summary>Play a one-shot effect that needs no details.</summary>
    public void Play(FlightSoundEvent ev) => Play(new FlightSoundCue { Event = ev });

    public void Dispose() => Engine.Dispose();
}
