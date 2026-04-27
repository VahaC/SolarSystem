using System.IO;
using System.Text.Json;
using OpenTK.Mathematics;

namespace SolarSystem;

/// <summary>
/// Q10: Cinematic camera path. The user records waypoints with
/// <c>Ctrl+1..9</c> (each slot = one waypoint, late writes overwrite); pressing
/// <c>Shift+P</c> plays them back in slot order using a Catmull–Rom spline so
/// the camera glides smoothly between samples. Playback animates
/// <see cref="Camera.Yaw"/>, <see cref="Camera.Pitch"/>, <see cref="Camera.Distance"/>
/// and <see cref="Camera.Target"/> over a configurable total duration.
/// </summary>
public sealed class CameraPath
{
    public sealed class Waypoint
    {
        public Vector3 Target;
        public float Yaw;
        public float Pitch;
        public float Distance;
    }

    private readonly Waypoint?[] _slots = new Waypoint?[9];
    private List<Waypoint> _playback = new();
    private double _playT;
    private double _playDuration;

    public bool IsPlaying { get; private set; }
    public int Count => _slots.Count(w => w != null);

    /// <summary>Capture the camera state into slot <paramref name="slot"/> (1..9).</summary>
    public void Record(int slot, Camera cam)
    {
        if (slot < 1 || slot > 9) return;
        _slots[slot - 1] = new Waypoint
        {
            Target = cam.Target,
            Yaw = cam.Yaw,
            Pitch = cam.Pitch,
            Distance = cam.Distance,
        };
        TrySaveToDisk();
    }

    public void Clear(int slot)
    {
        if (slot < 1 || slot > 9) return;
        _slots[slot - 1] = null;
        TrySaveToDisk();
    }

    public void ClearAll()
    {
        for (int i = 0; i < _slots.Length; i++) _slots[i] = null;
        TrySaveToDisk();
    }

    /// <summary>Begin a Catmull-Rom playback across all populated slots.
    /// At least 2 waypoints are required; otherwise the call is ignored.</summary>
    public bool Play(double durationSeconds = 6.0)
    {
        var pts = _slots.Where(w => w != null).Select(w => w!).ToList();
        if (pts.Count < 2) return false;
        _playback = pts;
        _playT = 0;
        _playDuration = Math.Max(0.5, durationSeconds);
        IsPlaying = true;
        return true;
    }

    public void Stop() => IsPlaying = false;

    /// <summary>Advance playback by <paramref name="dt"/> seconds and write the
    /// interpolated state back into <paramref name="cam"/>. Caller should keep
    /// the camera's auto-follow disabled while <see cref="IsPlaying"/> is true.</summary>
    public void Update(double dt, Camera cam)
    {
        if (!IsPlaying || _playback.Count < 2) return;
        _playT += dt;
        double tn = Math.Clamp(_playT / _playDuration, 0.0, 1.0);
        // Map tn to segment index + local t.
        double f = tn * (_playback.Count - 1);
        int i = (int)Math.Floor(f);
        if (i >= _playback.Count - 1) i = _playback.Count - 2;
        float u = (float)(f - i);

        var p0 = _playback[Math.Max(i - 1, 0)];
        var p1 = _playback[i];
        var p2 = _playback[i + 1];
        var p3 = _playback[Math.Min(i + 2, _playback.Count - 1)];

        cam.Target = CatmullRom(p0.Target, p1.Target, p2.Target, p3.Target, u);
        cam.Yaw = CatmullRom(p0.Yaw, p1.Yaw, p2.Yaw, p3.Yaw, u);
        cam.Pitch = CatmullRom(p0.Pitch, p1.Pitch, p2.Pitch, p3.Pitch, u);
        cam.Distance = MathF.Max(cam.MinDistance,
            MathF.Min(cam.MaxDistance,
                CatmullRom(p0.Distance, p1.Distance, p2.Distance, p3.Distance, u)));

        if (tn >= 1.0) IsPlaying = false;
    }

    private static float CatmullRom(float p0, float p1, float p2, float p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * ((2f * p1) + (-p0 + p2) * t
            + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
            + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        => new(CatmullRom(p0.X, p1.X, p2.X, p3.X, t),
               CatmullRom(p0.Y, p1.Y, p2.Y, p3.Y, t),
               CatmullRom(p0.Z, p1.Z, p2.Z, p3.Z, t));

    // ---- Persistence -------------------------------------------------------

    private static string PathOnDisk => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SolarSystem", "campath.json");

    public void TryLoadFromDisk()
    {
        try
        {
            if (!File.Exists(PathOnDisk)) return;
            // A11: source-gen typeinfo (AOT-friendly).
            var data = JsonSerializer.Deserialize(
                File.ReadAllText(PathOnDisk),
                SolarSystemJsonContext.Default.WaypointArray);
            if (data == null) return;
            for (int i = 0; i < Math.Min(_slots.Length, data.Length); i++) _slots[i] = data[i];
        }
        catch { /* ignore: best-effort persistence */ }
    }

    private void TrySaveToDisk()
    {
        try
        {
            string? dir = Path.GetDirectoryName(PathOnDisk);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // A11: build a context bound to WriteIndented so the source-gen
            // resolver still produces pretty-printed JSON.
            var ctx = new SolarSystemJsonContext(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(PathOnDisk,
                JsonSerializer.Serialize(_slots, ctx.WaypointArray));
        }
        catch { /* ignore */ }
    }

    public string DebugSummary()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Slots: ");
        for (int i = 0; i < _slots.Length; i++) sb.Append(_slots[i] != null ? (i + 1).ToString() : "·");
        return sb.ToString();
    }
}
