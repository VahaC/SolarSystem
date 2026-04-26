using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Mathematics;

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
        };

        using var window = new SolarSystemWindow(GameWindowSettings.Default, nativeSettings);
        window.Run();
    }
}
