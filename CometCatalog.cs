using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTK.Mathematics;

namespace SolarSystem;

/// <summary>
/// S16: Real comet catalogue. Replaces the single hard-coded Halley nucleus with
/// a JSON-driven list of well-known comets (Halley, Hale–Bopp, NEOWISE, Encke).
/// Each entry produces a <see cref="Planet"/> (the nucleus body shared with the
/// regular planet rendering pipeline) plus a small bundle of tuning parameters
/// for the particle tail (emission rate, particle lifetime, exhaust speed).
/// Missing or malformed <c>data/comets.json</c> falls back to a single built-in
/// Halley entry so the simulation always boots.
/// </summary>
public static class CometCatalog
{
    public readonly struct Entry
    {
        public Planet Body { get; init; }
        public float EmissionRate { get; init; }
        public float TailLifetime { get; init; }
        public float TailSpeed { get; init; }
    }

    private static Entry[]? _cache;

    public static Entry[] LoadOrDefault()
    {
        if (_cache != null) return _cache;

        string path = Path.Combine(AppContext.BaseDirectory, "data", "comets.json");
        if (!File.Exists(path))
            path = Path.Combine("data", "comets.json");

        if (File.Exists(path))
        {
            try
            {
                using var fs = File.OpenRead(path);
                // A11: source-gen typeinfo with the same lenient runtime options.
                var ctx = new SolarSystemJsonContext(_options);
                var doc = JsonSerializer.Deserialize(fs, ctx.CometsFile);
                if (doc?.Comets is { Length: > 0 } list)
                {
                    var arr = new Entry[list.Length];
                    for (int i = 0; i < list.Length; i++) arr[i] = FromDto(list[i]);
                    Debug.WriteLine($"[comets.json] loaded {arr.Length} comet(s) from '{path}'");
                    _cache = arr;
                    return arr;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[comets.json] parse failed ({ex.GetType().Name}: {ex.Message}) — falling back");
            }
        }
        else
        {
            Debug.WriteLine($"[comets.json] not found — using built-in default");
        }

        _cache = new[] { BuiltInHalley() };
        return _cache;
    }

    private static Entry BuiltInHalley() => new()
    {
        Body = new Planet
        {
            Name = "Halley",
            SemiMajorAxisAU = 17.834,
            Eccentricity = 0.96714,
            InclinationDeg = 162.26,
            LongAscNodeDeg = 58.42,
            ArgPerihelionDeg = 111.33,
            MeanLongitudeDeg = 58.42 + 111.33 + 38.38,
            OrbitalPeriodYears = 75.32,
            VisualRadius = 0.18f,
            RealRadiusKm = 5.5,
            ProceduralColor = new Vector3(0.85f, 0.88f, 0.95f),
            TextureFile = "",
            AxisTiltDeg = 0f,
            RotationPeriodHours = 52.8,
        },
        EmissionRate = 800f,
        TailLifetime = 4f,
        TailSpeed = 18f,
    };

    private static Entry FromDto(CometDto d)
    {
        var c = d.Color ?? new[] { 0.9f, 0.9f, 1f };
        var body = new Planet
        {
            Name = d.Name ?? "?",
            SemiMajorAxisAU = d.SemiMajorAxisAU,
            Eccentricity = d.Eccentricity,
            InclinationDeg = d.InclinationDeg,
            LongAscNodeDeg = d.LongAscNodeDeg,
            ArgPerihelionDeg = d.ArgPerihelionDeg,
            MeanLongitudeDeg = d.MeanLongitudeDeg,
            OrbitalPeriodYears = d.OrbitalPeriodYears,
            VisualRadius = d.VisualRadius > 0 ? d.VisualRadius : 0.18f,
            RealRadiusKm = d.RealRadiusKm > 0 ? d.RealRadiusKm : 5.0,
            ProceduralColor = new Vector3(
                c.Length > 0 ? c[0] : 1f,
                c.Length > 1 ? c[1] : 1f,
                c.Length > 2 ? c[2] : 1f),
            TextureFile = d.TextureFile ?? "",
            AxisTiltDeg = d.AxisTiltDeg,
            RotationPeriodHours = d.RotationPeriodHours == 0 ? 24.0 : d.RotationPeriodHours,
        };
        return new Entry
        {
            Body = body,
            EmissionRate = d.EmissionRate > 0 ? d.EmissionRate : 800f,
            TailLifetime = d.TailLifetime > 0 ? d.TailLifetime : 4f,
            TailSpeed = d.TailSpeed > 0 ? d.TailSpeed : 18f,
        };
    }

    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // A11: was private; promoted to internal so SolarSystemJsonContext (the
    // System.Text.Json source-generated context) can reference the type.
    internal sealed class CometsFile
    {
        [JsonPropertyName("comets")] public CometDto[]? Comets { get; set; }
    }

    internal sealed class CometDto
    {
        public string? Name { get; set; }
        public double SemiMajorAxisAU { get; set; }
        public double Eccentricity { get; set; }
        public double InclinationDeg { get; set; }
        public double LongAscNodeDeg { get; set; }
        public double ArgPerihelionDeg { get; set; }
        public double MeanLongitudeDeg { get; set; }
        public double OrbitalPeriodYears { get; set; }
        public float VisualRadius { get; set; }
        public double RealRadiusKm { get; set; }
        public float[]? Color { get; set; }
        public string? TextureFile { get; set; }
        public float AxisTiltDeg { get; set; }
        public double RotationPeriodHours { get; set; }
        public float EmissionRate { get; set; }
        public float TailLifetime { get; set; }
        public float TailSpeed { get; set; }
    }
}
