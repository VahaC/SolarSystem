using System.Diagnostics;
using System.Globalization;

namespace SolarSystem;

/// <summary>
/// A7: Headless render / video export configuration.
///
/// When <see cref="SolarSystemWindow.Headless"/> is non-null the window is
/// hidden, persisted state is skipped, and every frame the simulation time
/// is forced to <c>From + FrameIndex * DaysPerFrame</c>. The post-bloom
/// composite is read back via <see cref="SolarSystemWindow.SaveScreenshotTo"/>
/// to <c>OutDir/frame_NNNNN.png</c>. After <see cref="TotalFrames"/> the
/// window closes and (if <see cref="FfmpegPath"/> is set) ffmpeg is invoked
/// to encode the PNG sequence into <see cref="VideoOutPath"/>.
/// </summary>
public sealed class HeadlessRenderJob
{
    /// <summary>Sim-days since J2000 of the first frame (inclusive).</summary>
    public double FromSimDays { get; init; }
    /// <summary>Sim-days advanced per output frame.</summary>
    public double DaysPerFrame { get; init; } = 1.0;
    /// <summary>Total number of frames to render.</summary>
    public int TotalFrames { get; init; } = 1;
    /// <summary>Frames per second of the encoded video (only used when an
    /// ffmpeg encode is requested).</summary>
    public int Fps { get; init; } = 60;
    /// <summary>Output directory for the PNG sequence.</summary>
    public string OutDir { get; init; } = "render";
    /// <summary>Optional ffmpeg executable path; when set, the PNG sequence
    /// is encoded to <see cref="VideoOutPath"/> after the final frame.</summary>
    public string? FfmpegPath { get; init; }
    /// <summary>Output video file (defaults to <c>OutDir/out.mp4</c>).</summary>
    public string? VideoOutPath { get; set; }
    /// <summary>Force real-scale mode for the entire render.</summary>
    public bool RealScale { get; init; }

    // Mutable progress, advanced by the window each rendered frame.
    public int FrameIndex { get; set; }

    /// <summary>Parse a <c>--render</c> CLI invocation. Returns null when the
    /// args don't request a headless render. Throws <see cref="ArgumentException"/>
    /// on malformed values so <c>Program.Main</c> can print usage and bail.</summary>
    public static HeadlessRenderJob? FromCli(string[] args)
    {
        if (args == null || args.Length == 0) return null;
        bool render = false;
        DateTime? from = null, to = null;
        double dt = 1.0;
        int fps = 60;
        string outDir = "render";
        string? ffmpeg = null;
        string? videoOut = null;
        bool realScale = false;
        int? frames = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => (i + 1 < args.Length) ? args[++i] : throw new ArgumentException($"Missing value for {a}");
            switch (a)
            {
                case "--render": render = true; break;
                case "--from":   from = ParseDate(Next()); break;
                case "--to":     to = ParseDate(Next()); break;
                case "--dt":
                case "--days-per-frame":
                    dt = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--frames":
                    frames = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--fps": fps = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--out": outDir = Next(); break;
                case "--ffmpeg": ffmpeg = Next(); break;
                case "--video-out": videoOut = Next(); break;
                case "--real-scale": realScale = true; break;
                default:
                    if (render) throw new ArgumentException($"Unknown render option: {a}");
                    break;
            }
        }

        if (!render) return null;

        double fromDays = from.HasValue
            ? (from.Value - OrbitalMechanics.J2000).TotalDays
            : 0.0;

        int totalFrames;
        if (frames.HasValue) totalFrames = Math.Max(1, frames.Value);
        else if (to.HasValue)
        {
            double span = (to.Value - OrbitalMechanics.J2000).TotalDays - fromDays;
            totalFrames = Math.Max(1, (int)Math.Round(span / dt));
        }
        else totalFrames = 1;

        return new HeadlessRenderJob
        {
            FromSimDays = fromDays,
            DaysPerFrame = dt,
            TotalFrames = totalFrames,
            Fps = fps,
            OutDir = outDir,
            FfmpegPath = ffmpeg,
            VideoOutPath = videoOut,
            RealScale = realScale,
        };
    }

    private static DateTime ParseDate(string s)
    {
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
            return d;
        throw new ArgumentException($"Invalid date: {s}");
    }

    /// <summary>Invoke ffmpeg to encode <c>OutDir/frame_%05d.png</c> into
    /// <see cref="VideoOutPath"/>. Returns true on success.</summary>
    public bool TryEncodeVideo()
        => TryEncodePngSequence(FfmpegPath, OutDir, Fps,
            VideoOutPath ?? Path.Combine(OutDir, "out.mp4"));

    /// <summary>Run <c>ffmpeg -y -framerate fps -i dir/frame_%05d.png -vf
    /// scale=trunc(iw/2)*2:trunc(ih/2)*2 -c:v libx264 -pix_fmt yuv420p -crf 18
    /// outFile</c>. <paramref name="ffmpegPath"/> can be a full path or just
    /// <c>"ffmpeg"</c> (resolved via <c>PATH</c>). The <c>scale</c> filter trims
    /// odd dimensions to the nearest even number — libx264 + yuv420p refuses
    /// odd width/height and would otherwise produce a zero-byte output file.
    /// Returns true when the process exits with code 0; on failure stderr is
    /// captured into <paramref name="error"/> so the caller can surface it.</summary>
    public static bool TryEncodePngSequence(string? ffmpegPath, string dir, int fps, string outFile, out string error)
    {
        error = "";
        if (string.IsNullOrEmpty(ffmpegPath)) { error = "ffmpeg not configured"; return false; }
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-framerate"); psi.ArgumentList.Add(fps.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(Path.Combine(dir, "frame_%05d.png"));
        // Force even dimensions so libx264 + yuv420p doesn't bail out with
        // "width not divisible by 2" → zero-byte mp4.
        psi.ArgumentList.Add("-vf"); psi.ArgumentList.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2");
        psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("libx264");
        psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("yuv420p");
        psi.ArgumentList.Add("-crf"); psi.ArgumentList.Add("18");
        psi.ArgumentList.Add(outFile);
        try
        {
            using var p = Process.Start(psi)!;
            string stderr = p.StandardError.ReadToEnd();
            string stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            Console.WriteLine($"[render] ffmpeg exit {p.ExitCode}: {outFile}");
            if (p.ExitCode != 0)
            {
                // Surface the last few lines of stderr — that's where ffmpeg
                // prints the actual failure reason.
                error = TailLines(stderr.Length > 0 ? stderr : stdout, 4);
                Console.WriteLine($"[render] ffmpeg stderr:\n{stderr}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Console.WriteLine($"[render] ffmpeg failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Back-compat overload that drops the stderr output.</summary>
    public static bool TryEncodePngSequence(string? ffmpegPath, string dir, int fps, string outFile)
        => TryEncodePngSequence(ffmpegPath, dir, fps, outFile, out _);

    private static string TailLines(string text, int n)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var lines = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        int start = Math.Max(0, lines.Length - n);
        return string.Join(" | ", lines, start, lines.Length - start);
    }
}
