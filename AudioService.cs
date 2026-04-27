using System.Diagnostics;

namespace SolarSystem;

/// <summary>
/// Q15: Lightweight audio-cue service. Plays short SFX hooked into UI events
/// (focus transition "whoosh", date jump "tick"). Implementation is
/// intentionally minimal so the project doesn't take on a heavyweight audio
/// dependency: on Windows it dispatches <see cref="Console.Beep(int, int)"/>
/// on a background <see cref="Task"/> so the main loop never blocks; on other
/// platforms it's a graceful no-op. The toggle is wired through
/// <see cref="Enabled"/> so the user-visible behaviour ("audio on / off") is
/// consistent across platforms even when no real sound device is available.
/// </summary>
public sealed class AudioService
{
    public bool Enabled { get; set; } = false;

    public void PlayTick()
    {
        if (!Enabled) return;
        FireAndForgetBeep(880, 40);
    }

    public void PlayWhoosh()
    {
        if (!Enabled) return;
        // A short two-step descending chirp gives a "swoosh" feel using only
        // Console.Beep's square-wave synth.
        Task.Run(() =>
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    Console.Beep(520, 90);
                    Console.Beep(360, 110);
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[audio] whoosh failed: {ex.Message}"); }
        });
    }

    private static void FireAndForgetBeep(int freq, int ms)
    {
        Task.Run(() =>
        {
            try
            {
                if (OperatingSystem.IsWindows()) Console.Beep(freq, ms);
            }
            catch (Exception ex) { Debug.WriteLine($"[audio] beep failed: {ex.Message}"); }
        });
    }
}
