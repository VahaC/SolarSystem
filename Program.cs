using OpenTK.Windowing.Common;
using OpenTK.Windowing.Common.Input;
using OpenTK.Windowing.Desktop;
using OpenTK.Mathematics;
using StbImageSharp;

namespace SolarSystem;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var nativeSettings = new NativeWindowSettings
        {
            ClientSize = new Vector2i(1280, 800),
            Title = "Solar System (J2000)",
            APIVersion = new Version(3, 3),
            Profile = ContextProfile.Core,
            Flags = ContextFlags.ForwardCompatible,
            StartVisible = true,
            WindowBorder = WindowBorder.Resizable,
            Icon = TryLoadIcon("solar-system-logo.png"),
        };

        using var window = new SolarSystemWindow(GameWindowSettings.Default, nativeSettings);
        window.Run();
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
