namespace SolarSystem;

// PROTOTYPE (2026-11-17): arcade flight mode — sound: the shared contract between the
// mixer (AudioEngine), the continuous flight soundscape, the one-shot effects, the
// music and the window glue (FlightAudio).
//
// How sound works here:
//  * Everything is synthesised; there are no audio files.
//  * The format is fixed: 48 kHz, stereo, 32-bit float, interleaved (L, R, L, R ...).
//    A full-scale sample is ±1; the mixer applies the master volume and a limiter.
//  * A sound is an IAudioSource: the mixer asks it to ADD its next block of samples to
//    a buffer. Continuous sounds (engine, music) live as long as flight mode; one-shot
//    sounds (a gate chime) return false from Mix when they are over and are dropped.
//  * Mix runs on the audio thread (or, for the hidden demo and tests, is called directly
//    in "offline" mode), never on the game thread. It must not allocate, lock for long,
//    throw, or produce NaN / infinity.
//  * The game thread talks to continuous sources only through FlightSoundState: plain
//    volatile floats it overwrites once per frame. Sources smooth them themselves.
//  * Everything must be deterministic for a given sequence of states and events (seeded
//    noise, no clocks), so the hidden demo can render the same audio every time.
//  * Musical agreement: concert pitch A4 = 440 Hz; the melodic material of the effects
//    and the music is in A minor (pentatonic: A C D E G), so gate chimes and the
//    soundtrack harmonise.

/// <summary>The one audio format everything uses.</summary>
public static class AudioFormat
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
}

/// <summary>A sound generator the mixer can play.</summary>
public interface IAudioSource
{
    /// <summary>ADD the next <paramref name="frames"/> stereo frames to
    /// <paramref name="buffer"/> (interleaved L, R; its length is at least
    /// <c>frames * 2</c>; it already holds what other sources mixed — do not clear it).
    /// Returns false when the sound is over; the mixer then drops the source.</summary>
    bool Mix(Span<float> buffer, int frames);
}

/// <summary>
/// What the flight is doing, for the continuous sounds. The game thread overwrites the
/// fields once per frame; audio sources read them on the audio thread and smooth them.
/// </summary>
public sealed class FlightSoundState
{
    /// <summary>1 while flight mode is on, 0 otherwise: continuous sounds fade to silence at 0.</summary>
    public volatile float Active;
    /// <summary>1 while the game is paused: continuous sounds duck almost to silence.</summary>
    public volatile float Paused;

    /// <summary>Engine output 0 .. 1 (about 0.5 at cruise, 1 at full boost, 0.12 when braking).</summary>
    public volatile float Throttle01;
    /// <summary>Smoothed boost amount 0 .. 1.</summary>
    public volatile float Boost01;
    /// <summary>Speed as a fraction of top speed (cruise ≈ 0.25, full boost = 1, standing = 0).</summary>
    public volatile float Speed01;
    /// <summary>Boost reserve 0 .. 1.</summary>
    public volatile float BoostEnergy01 = 1f;
    /// <summary>How hard the ship is turning, 0 .. 1.</summary>
    public volatile float Turn01;
    /// <summary>Which way the ship is turning: -1 hard left .. +1 hard right (the yaw
    /// input, with a little of the roll).</summary>
    public volatile float TurnPan;
    /// <summary>1 when the ship is in the ring plane among the ice and rocks, falling to 0
    /// about 0.3 world units above or below it.</summary>
    public volatile float RingProximity01;
    /// <summary>How fast the speed is changing: +1 accelerating hard (boost kicking in),
    /// -1 decelerating hard (braking, boost cutting out), 0 steady.</summary>
    public volatile float Accel;
    /// <summary>1 while the air brake is held, else 0.</summary>
    public volatile float Brake01;
    /// <summary>How close the nearest big rock is: 0 when none is within about two ship
    /// lengths, rising to 1 when the ship is touching it.</summary>
    public volatile float RockProximity01;
    /// <summary>Which side that rock is on: -1 left .. +1 right.</summary>
    public volatile float RockPan;

    /// <summary>The race phase as an int: 0 idle (free flight), 1 countdown, 2 running, 3 finished.</summary>
    public volatile int RacePhase;
    /// <summary>Seconds left in the countdown (race phase 1), otherwise 0.</summary>
    public volatile float Countdown;
    /// <summary>Gates done / total gates, 0 .. 1 (0 outside a race).</summary>
    public volatile float RaceProgress01;
    /// <summary>Where the next gate is relative to the ship: -1 hard left .. +1 hard right.</summary>
    public volatile float NextGatePan;
    /// <summary>Approach to the next gate while racing: 0 when it is a world unit or more
    /// away, rising to 1 as the ship reaches it; 0 outside a race.</summary>
    public volatile float GateApproach01;
    /// <summary>Gates passed in a row without a miss in the current race (0 outside a race).</summary>
    public volatile int Combo;
    /// <summary>Medal of the race just finished (race phase 3): 0 none, 1 bronze, 2 silver, 3 gold.</summary>
    public volatile int Medal;
}

/// <summary>Things that happen once and make a sound. The details of each one travel in
/// a <see cref="FlightSoundCue"/>.</summary>
public enum FlightSoundEvent
{
    /// <summary>Flight mode switched on (the ship powers up).</summary>
    EnterFlight,
    /// <summary>Flight mode switched off.</summary>
    ExitFlight,
    /// <summary>The boost kicked in.</summary>
    BoostIgnite,
    /// <summary>The boost reserve ran dry and the boost cut out.</summary>
    BoostEmpty,
    /// <summary>The ship hit a rock (<see cref="FlightSoundCue.Strength"/>, <see cref="FlightSoundCue.Pan"/>).</summary>
    RockHit,
    /// <summary>One second of the countdown (<see cref="FlightSoundCue.Count"/> = 3, 2, 1).</summary>
    CountdownTick,
    /// <summary>The start signal.</summary>
    Go,
    /// <summary>A gate was passed (<see cref="FlightSoundCue.GateIndex"/>,
    /// <see cref="FlightSoundCue.GateCount"/>, <see cref="FlightSoundCue.Combo"/>,
    /// <see cref="FlightSoundCue.Split"/>, <see cref="FlightSoundCue.Speed01"/>).</summary>
    GatePassed,
    /// <summary>A gate was missed: time penalty, the combo is broken.</summary>
    GateMissed,
    /// <summary>The race is over (<see cref="FlightSoundCue.Medal"/>, <see cref="FlightSoundCue.NewRecord"/>).</summary>
    Finish,
    /// <summary>A big rock swept past very close without touching — a near miss
    /// (<see cref="FlightSoundCue.Strength"/> = how close, <see cref="FlightSoundCue.Speed01"/>,
    /// <see cref="FlightSoundCue.Pan"/> = the side it passed on).</summary>
    RockPass,
    /// <summary>The air brake was applied.</summary>
    BrakeOn,
}

/// <summary>One sound event with everything known about it.</summary>
public struct FlightSoundCue
{
    public FlightSoundEvent Event;
    /// <summary>Where it happens: -1 left .. +1 right.</summary>
    public float Pan;
    /// <summary>0 .. 1: how hard (RockHit) or how close (RockPass).</summary>
    public float Strength;
    /// <summary>The ship's speed at that moment as a fraction of top speed (cruise ≈ 0.25, boost = 1).</summary>
    public float Speed01;
    /// <summary>GatePassed / GateMissed: which gate (0 = first) out of how many (25 on the current track).</summary>
    public int GateIndex, GateCount;
    /// <summary>GatePassed: gates passed in a row without a miss, including this one (1, 2, 3 ...).</summary>
    public int Combo;
    /// <summary>GatePassed: this run's time at the gate minus the best run's (negative =
    /// ahead). Only meaningful when <see cref="HasBest"/>.</summary>
    public float Split;
    public bool HasBest;
    /// <summary>Finish: 0 none, 1 bronze, 2 silver, 3 gold.</summary>
    public int Medal;
    /// <summary>Finish: the run beat the best time.</summary>
    public bool NewRecord;
    /// <summary>CountdownTick: the number shown (3, 2, 1).</summary>
    public int Count;
}
