using OpenTK.Windowing.Common;
using OpenTK.Windowing.Common.Input;
using OpenTK.Windowing.Desktop;
using OpenTK.Mathematics;
using StbImageSharp;

namespace SolarSystem;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // A7: --render switches the app into a hidden, deterministic frame
        // exporter. All other CLI knobs are parsed by HeadlessRenderJob.
        HeadlessRenderJob? job;
        try { job = HeadlessRenderJob.FromCli(args); }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            PrintUsage();
            return;
        }

        // --flight-demo: hidden scripted run of the arcade flight mode (development tool).
        FlightDemoJob? demo = null;
        if (job == null)
        {
            try { demo = FlightDemoJob.FromCli(args); }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                PrintUsage();
                return;
            }
        }

        var nativeSettings = new NativeWindowSettings
        {
            ClientSize = demo?.Size ?? new Vector2i(1280, 800),
            Title = "Solar System (J2000)",
            // 4.3 is required by the A8 asteroid-belt compute shader. Every
            // other shader stays at #version 330 core, which is still valid
            // in a 4.3 core profile, so legacy machines that only support
            // GL 3.3 fall through to the CPU Kepler path automatically when
            // the context creation downgrades.
            APIVersion = new Version(4, 3),
            Profile = ContextProfile.Core,
            Flags = ContextFlags.ForwardCompatible,
            StartVisible = job == null && demo == null,
            WindowBorder = WindowBorder.Resizable,
            Icon = TryLoadIcon("solar-system-logo.png"),
        };

        if (demo == null)
        {
            using var window = new SolarSystemWindow(GameWindowSettings.Default, nativeSettings)
            {
                Headless = job,
            };
            window.Run();
            return;
        }

        // The demo has no console and no window: report failures (for example a GLSL
        // compile error) in a file next to the screenshots.
        try
        {
            using var window = new SolarSystemWindow(GameWindowSettings.Default, nativeSettings)
            {
                FlightDemo = demo,
            };
            window.Run();
        }
        catch (Exception ex)
        {
            try
            {
                Directory.CreateDirectory(demo.OutDir);
                File.WriteAllText(Path.Combine(demo.OutDir, "error.txt"), ex.ToString());
            }
            catch { /* nothing more we can do */ }
            Console.Error.WriteLine(ex);
            Environment.ExitCode = 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  SolarSystem                                 (interactive)");
        Console.WriteLine("  SolarSystem --render --from YYYY-MM-DD --to YYYY-MM-DD");
        Console.WriteLine("              [--dt <days/frame>] [--frames N] [--fps N]");
        Console.WriteLine("              [--out <dir>] [--ffmpeg <path>] [--video-out <file>]");
        Console.WriteLine("              [--real-scale] [--physics]");
        Console.WriteLine("  SolarSystem --flight-demo [--seconds N] [--shot-every S]");
        Console.WriteLine("              [--view chase|orbit|side|front] [--out <dir>] [--size WxH]");
    }

    /// <summary>
    /// Loads a PNG/JPG from disk and wraps it as a GLFW window icon (RGBA8, width, height).
    /// Returns null if the file does not exist or cannot be decoded so the app still runs
    /// with the default platform icon.
    /// </summary>
    private static WindowIcon? TryLoadIcon(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            var img = ImageResult.FromStream(fs, ColorComponents.RedGreenBlueAlpha);
            return new WindowIcon(new Image(img.Width, img.Height, img.Data));
        }
        catch
        {
            return null;
        }
    }
}
