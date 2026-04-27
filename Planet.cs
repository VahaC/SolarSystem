using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTK.Mathematics;

namespace SolarSystem;

/// <summary>
/// Planet data: J2000 Keplerian elements + visual scaling info.
/// </summary>
public sealed class Planet
{
    public string Name { get; init; } = "";
    public double SemiMajorAxisAU { get; init; }
    public double Eccentricity { get; init; }
    public double InclinationDeg { get; init; }
    public double LongAscNodeDeg { get; init; }
    public double ArgPerihelionDeg { get; init; }
    public double MeanLongitudeDeg { get; init; }
    public double OrbitalPeriodYears { get; init; }

    // ---- Secular (per Julian century) rates of change of the Keplerian elements.
    // When non-zero these are added linearly to the J2000 epoch values inside
    // OrbitalMechanics.HeliocentricPosition, so for inner planets we get the proper
    // Standish 1800-2050 mean motion + secular perturbations (Mercury and Venus
    // transit dates land within seconds, instead of the multi-day drift you get
    // from the J2000-only model). Default 0 keeps every body that doesn't supply
    // them on the original simple-Keplerian path.
    public double SemiMajorAxisDot { get; init; }
    public double EccentricityDot { get; init; }
    public double InclinationDotDeg { get; init; }
    public double LongAscNodeDotDeg { get; init; }
    public double ArgPerihelionDotDeg { get; init; }
    public double MeanLongitudeDotDeg { get; init; }

    /// <summary>Visual radius in world units (NOT physical).
    /// between an inflated artistic value and a real-scale (km-derived) value at runtime.</summary>
    public float VisualRadius { get; set; }
    /// <summary>Approximate real radius in km (for UI display).</summary>
    public double RealRadiusKm { get; init; }
    public Vector3 ProceduralColor { get; init; }
    public string TextureFile { get; init; } = "";

    /// <summary>Axial tilt (obliquity) in degrees.</summary>
    public float AxisTiltDeg { get; init; }
    /// <summary>Sidereal rotation period in hours. Negative = retrograde rotation.</summary>
    public double RotationPeriodHours { get; init; }

    // Runtime
    public Vector3 Position;     // world position (units)
    public Vector3d HelioAU;     // heliocentric AU position
    public float RotationAngleRad; // current spin around local Y
    public int TextureId;
    public bool TextureFromFile;

    /// <summary>Optional night-side emissive map (e.g. Earth city lights). 0 = none.</summary>
    public int NightTextureId;
    /// <summary>Optional cloud layer texture (e.g. Earth clouds). 0 = none.</summary>
    public int CloudTextureId;
    /// <summary>V15: optional specular / ocean mask (white = water, black = land).
    /// When non-zero the planet shader gates its specular term by this texture so
    /// only oceans glint. 0 = none → uniform glint across the entire surface.</summary>
    public int OceanMaskTextureId;
    /// <summary>Independent rotation angle for the cloud layer so it can drift relative
    /// to the surface. Updated per frame by <see cref="SolarSystemWindow"/>.</summary>
    public float CloudRotationAngleRad;

    // --- Trail (fading line strip of recent world-space positions) ---
    /// <summary>Maximum number of samples retained for the trail line strip.</summary>
    public const int TrailCapacity = 200;
    /// <summary>Ring buffer of recent world positions. Slot at <see cref="TrailHead"/> is the
    /// next write target; oldest sample (when full) is also at <see cref="TrailHead"/>.</summary>
    public readonly Vector3[] Trail = new Vector3[TrailCapacity];
    /// <summary>Number of valid samples currently in <see cref="Trail"/> (0..TrailCapacity).</summary>
    public int TrailCount;
    /// <summary>Index of the next slot to overwrite in <see cref="Trail"/>.</summary>
    public int TrailHead;

    public void TrailReset()
    {
        TrailCount = 0;
        TrailHead = 0;
    }

    /// <summary>Append <paramref name="pos"/> to the trail if it is far enough from the
    /// most recently stored sample (so the line strip doesn't degenerate when paused).</summary>
    public void TrailPush(Vector3 pos, float minSpacing)
    {
        if (TrailCount > 0)
        {
            int last = (TrailHead - 1 + TrailCapacity) % TrailCapacity;
            if ((pos - Trail[last]).LengthSquared < minSpacing * minSpacing)
                return;
        }
        Trail[TrailHead] = pos;
        TrailHead = (TrailHead + 1) % TrailCapacity;
        if (TrailCount < TrailCapacity) TrailCount++;
    }

    // Built-in J2000 elements (NASA JPL approximations).
    public static Planet[] CreateAll()
    {
        if (TryLoadFromJson(out var majors, out _) && majors.Length > 0)
            return majors;
        return CreateAllBuiltIn();
    }

    private static Planet[] CreateAllBuiltIn()
    {
        return new[]
        {
            new Planet
            {
                Name = "Mercury",
                SemiMajorAxisAU = 0.38709927, Eccentricity = 0.20563593,
                InclinationDeg = 7.00497902, LongAscNodeDeg = 48.33076593,
                ArgPerihelionDeg = 77.45779628 - 48.33076593,
                MeanLongitudeDeg = 252.25032350,
                OrbitalPeriodYears = 0.2408467,
                VisualRadius = 0.7f, RealRadiusKm = 2439.7,
                ProceduralColor = new Vector3(0.65f, 0.62f, 0.58f),
                TextureFile = "8k_mercury.jpg",
                AxisTiltDeg = 0.034f, RotationPeriodHours = 1407.6,
            },
            new Planet
            {
                Name = "Venus",
                SemiMajorAxisAU = 0.72333566, Eccentricity = 0.00677672,
                InclinationDeg = 3.39467605, LongAscNodeDeg = 76.67984255,
                ArgPerihelionDeg = 131.60246718 - 76.67984255,
                MeanLongitudeDeg = 181.97909950,
                OrbitalPeriodYears = 0.61519726,
                VisualRadius = 1.3f, RealRadiusKm = 6051.8,
                ProceduralColor = new Vector3(0.95f, 0.78f, 0.45f),
                TextureFile = "8k_venus_surface.jpg",
                AxisTiltDeg = 177.36f, RotationPeriodHours = -5832.5,
            },
            new Planet
            {
                Name = "Earth",
                SemiMajorAxisAU = 1.00000261, Eccentricity = 0.01671123,
                InclinationDeg = -0.00001531, LongAscNodeDeg = 0.0,
                ArgPerihelionDeg = 102.93768193,
                MeanLongitudeDeg = 100.46457166,
                OrbitalPeriodYears = 1.0000174,
                VisualRadius = 1.5f, RealRadiusKm = 6371.0,
                ProceduralColor = new Vector3(0.25f, 0.55f, 0.95f),
                TextureFile = "8k_earth_daymap.jpg",
                AxisTiltDeg = 23.44f, RotationPeriodHours = 23.9345,
            },
            new Planet
            {
                Name = "Mars",
                SemiMajorAxisAU = 1.52371034, Eccentricity = 0.09339410,
                InclinationDeg = 1.84969142, LongAscNodeDeg = 49.55953891,
                ArgPerihelionDeg = 286.50210865,
                MeanLongitudeDeg = -4.55343205,
                OrbitalPeriodYears = 1.8808476,
                VisualRadius = 1.1f, RealRadiusKm = 3389.5,
                ProceduralColor = new Vector3(0.85f, 0.40f, 0.25f),
                TextureFile = "8k_mars.jpg",
                AxisTiltDeg = 25.19f, RotationPeriodHours = 24.6229,
            },
            new Planet
            {
                Name = "Jupiter",
                SemiMajorAxisAU = 5.20288700, Eccentricity = 0.04838624,
                InclinationDeg = 1.30439695, LongAscNodeDeg = 100.47390909,
                ArgPerihelionDeg = 273.86740658,
                MeanLongitudeDeg = 34.39644051,
                OrbitalPeriodYears = 11.862615,
                VisualRadius = 4.0f, RealRadiusKm = 69911.0,
                ProceduralColor = new Vector3(0.85f, 0.72f, 0.55f),
                TextureFile = "8k_jupiter.jpg",
                AxisTiltDeg = 3.13f, RotationPeriodHours = 9.9250,
            },
            new Planet
            {
                Name = "Saturn",
                SemiMajorAxisAU = 9.53667594, Eccentricity = 0.05386179,
                InclinationDeg = 2.48599187, LongAscNodeDeg = 113.66242448,
                ArgPerihelionDeg = 339.39163095,
                MeanLongitudeDeg = 49.95424423,
                OrbitalPeriodYears = 29.447498,
                VisualRadius = 3.4f, RealRadiusKm = 58232.0,
                ProceduralColor = new Vector3(0.93f, 0.85f, 0.62f),
                TextureFile = "8k_saturn.jpg",
                AxisTiltDeg = 26.73f, RotationPeriodHours = 10.656,
            },
            new Planet
            {
                Name = "Uranus",
                SemiMajorAxisAU = 19.18916464, Eccentricity = 0.04725744,
                InclinationDeg = 0.77263783, LongAscNodeDeg = 74.01692503,
                ArgPerihelionDeg = 96.99893202,
                MeanLongitudeDeg = 313.23810451,
                OrbitalPeriodYears = 84.016846,
                VisualRadius = 2.4f, RealRadiusKm = 25362.0,
                ProceduralColor = new Vector3(0.55f, 0.85f, 0.95f),
                TextureFile = "2k_uranus.jpg",
                AxisTiltDeg = 97.77f, RotationPeriodHours = -17.24,
            },
            new Planet
            {
                Name = "Neptune",
                SemiMajorAxisAU = 30.06992276, Eccentricity = 0.00859048,
                InclinationDeg = 1.77004347, LongAscNodeDeg = 131.78422574,
                ArgPerihelionDeg = 273.18715000,
                MeanLongitudeDeg = -55.12002969,
                OrbitalPeriodYears = 164.79132,
                VisualRadius = 2.3f, RealRadiusKm = 24622.0,
                ProceduralColor = new Vector3(0.30f, 0.50f, 0.95f),
                TextureFile = "2k_neptune.jpg",
                AxisTiltDeg = 28.32f, RotationPeriodHours = 16.11,
            },
        };
    }

    /// <summary>
    /// Five officially-recognised dwarf planets with J2000 Keplerian elements.
    /// They reuse the same Planet record (and therefore the same orbit / trail /
    /// picking / Kepler-solve pipeline) as the major planets — the only thing that
    /// distinguishes them at runtime is that they aren't bound to a numeric focus
    /// shortcut. MeanLongitudeDeg = M0 + ω + Ω so OrbitalMechanics.HeliocentricPosition
    /// recovers the published mean anomaly via M = L − ϖ.
    /// </summary>
    public static Planet[] CreateDwarfPlanets()
    {
        if (TryLoadFromJson(out _, out var dwarfs) && dwarfs.Length > 0)
            return dwarfs;
        return CreateDwarfPlanetsBuiltIn();
    }

    private static Planet[] CreateDwarfPlanetsBuiltIn()
    {
        return new[]
        {
            // Ceres — main-belt, in between Mars and Jupiter.
            new Planet
            {
                Name = "Ceres",
                SemiMajorAxisAU = 2.7691652, Eccentricity = 0.0760091,
                InclinationDeg = 10.5934, LongAscNodeDeg = 80.3293,
                ArgPerihelionDeg = 73.5970,
                MeanLongitudeDeg = 287.5882 + 73.5970 + 80.3293, // M + ω + Ω
                OrbitalPeriodYears = 4.605,
                VisualRadius = 0.3f, RealRadiusKm = 469.7,
                ProceduralColor = new Vector3(0.60f, 0.55f, 0.50f),
                TextureFile = "4k_ceres_fictional.jpg",
                AxisTiltDeg = 4.0f, RotationPeriodHours = 9.074,
            },
            // Pluto — Kuiper belt, mildly eccentric and inclined.
            new Planet
            {
                Name = "Pluto",
                SemiMajorAxisAU = 39.48211675, Eccentricity = 0.24882730,
                InclinationDeg = 17.14001206, LongAscNodeDeg = 110.30393684,
                ArgPerihelionDeg = 224.06891629 - 110.30393684, // ϖ − Ω
                MeanLongitudeDeg = 238.92903833,
                OrbitalPeriodYears = 248.00208,
                VisualRadius = 0.6f, RealRadiusKm = 1188.3,
                ProceduralColor = new Vector3(0.78f, 0.65f, 0.50f),
                TextureFile = "2k_pluto.jpg",
                AxisTiltDeg = 122.53f, RotationPeriodHours = -153.2928, // retrograde
            },
            // Haumea — fast-spinning ellipsoid in the Kuiper belt.
            new Planet
            {
                Name = "Haumea",
                SemiMajorAxisAU = 43.116, Eccentricity = 0.19501,
                InclinationDeg = 28.2137, LongAscNodeDeg = 121.79,
                ArgPerihelionDeg = 239.05,
                MeanLongitudeDeg = 205.66 + 239.05 + 121.79,
                OrbitalPeriodYears = 283.12,
                VisualRadius = 0.5f, RealRadiusKm = 798.0,
                ProceduralColor = new Vector3(0.90f, 0.88f, 0.85f),
                TextureFile = "4k_haumea_fictional.jpg",
                AxisTiltDeg = 0.0f, RotationPeriodHours = 3.9155,
            },
            // Makemake — bright, reddish, classical Kuiper-belt object.
            new Planet
            {
                Name = "Makemake",
                SemiMajorAxisAU = 45.430, Eccentricity = 0.16259,
                InclinationDeg = 29.0058, LongAscNodeDeg = 79.382,
                ArgPerihelionDeg = 295.154,
                MeanLongitudeDeg = 152.146 + 295.154 + 79.382,
                OrbitalPeriodYears = 309.09,
                VisualRadius = 0.5f, RealRadiusKm = 715.0,
                ProceduralColor = new Vector3(0.75f, 0.55f, 0.40f),
                TextureFile = "4k_makemake_fictional.jpg",
                AxisTiltDeg = 0.0f, RotationPeriodHours = 22.83,
            },
            // Eris — scattered-disk; a far enough out and e high enough to noticeably
            // distort its orbit ring relative to the major planets.
            new Planet
            {
                Name = "Eris",
                SemiMajorAxisAU = 68.146, Eccentricity = 0.43607,
                InclinationDeg = 43.7405, LongAscNodeDeg = 36.0387,
                ArgPerihelionDeg = 151.6394,
                MeanLongitudeDeg = 204.7244 + 151.6394 + 36.0387,
                OrbitalPeriodYears = 558.04,
                VisualRadius = 0.6f, RealRadiusKm = 1163.0,
                ProceduralColor = new Vector3(0.85f, 0.85f, 0.82f),
                TextureFile = "4k_eris_fictional.jpg",
                AxisTiltDeg = 0.0f, RotationPeriodHours = 25.9,
            },
        };
    }

    // ---- A3: data-driven loading from data/planets.json ----------------------------
    // Cached so we only hit the disk + parser once even though CreateAll and
    // CreateDwarfPlanets call into the same file.
    private static (Planet[] majors, Planet[] dwarfs)? _jsonCache;
    private static bool _jsonLoadAttempted;

    private static bool TryLoadFromJson(out Planet[] majors, out Planet[] dwarfs)
    {
        if (_jsonCache is { } cached) { majors = cached.majors; dwarfs = cached.dwarfs; return true; }
        if (_jsonLoadAttempted) { majors = Array.Empty<Planet>(); dwarfs = Array.Empty<Planet>(); return false; }
        _jsonLoadAttempted = true;

        string path = Path.Combine(AppContext.BaseDirectory, "data", "planets.json");
        if (!File.Exists(path))
        {
            Debug.WriteLine($"[planets.json] not found at '{path}', using built-in defaults");
            majors = Array.Empty<Planet>(); dwarfs = Array.Empty<Planet>();
            return false;
        }
        try
        {
            using var fs = File.OpenRead(path);
            var doc = JsonSerializer.Deserialize<PlanetsFile>(fs, _jsonOptions)
                      ?? throw new InvalidDataException("planets.json deserialised as null");
            var ms = (doc.Majors ?? Array.Empty<PlanetDto>()).Select(FromDto).ToArray();
            var ds = (doc.Dwarfs ?? Array.Empty<PlanetDto>()).Select(FromDto).ToArray();
            _jsonCache = (ms, ds);
            Debug.WriteLine($"[planets.json] loaded {ms.Length} majors + {ds.Length} dwarfs from '{path}'");
            majors = ms; dwarfs = ds;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[planets.json] parse failed ({ex.GetType().Name}: {ex.Message}) — falling back to built-in defaults");
            majors = Array.Empty<Planet>(); dwarfs = Array.Empty<Planet>();
            return false;
        }
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static Planet FromDto(PlanetDto d)
    {
        var c = d.Color ?? new[] { 1f, 1f, 1f };
        return new Planet
        {
            Name = d.Name ?? "?",
            SemiMajorAxisAU = d.SemiMajorAxisAU,
            Eccentricity = d.Eccentricity,
            InclinationDeg = d.InclinationDeg,
            LongAscNodeDeg = d.LongAscNodeDeg,
            ArgPerihelionDeg = d.ArgPerihelionDeg,
            MeanLongitudeDeg = d.MeanLongitudeDeg,
            OrbitalPeriodYears = d.OrbitalPeriodYears,
            SemiMajorAxisDot = d.SemiMajorAxisDot,
            EccentricityDot = d.EccentricityDot,
            InclinationDotDeg = d.InclinationDotDeg,
            LongAscNodeDotDeg = d.LongAscNodeDotDeg,
            ArgPerihelionDotDeg = d.ArgPerihelionDotDeg,
            MeanLongitudeDotDeg = d.MeanLongitudeDotDeg,
            VisualRadius = d.VisualRadius,
            RealRadiusKm = d.RealRadiusKm,
            ProceduralColor = new Vector3(
                c.Length > 0 ? c[0] : 1f,
                c.Length > 1 ? c[1] : 1f,
                c.Length > 2 ? c[2] : 1f),
            TextureFile = d.TextureFile ?? "",
            AxisTiltDeg = d.AxisTiltDeg,
            RotationPeriodHours = d.RotationPeriodHours,
        };
    }

    private sealed class PlanetsFile
    {
        [JsonPropertyName("majors")] public PlanetDto[]? Majors { get; set; }
        [JsonPropertyName("dwarfs")] public PlanetDto[]? Dwarfs { get; set; }
    }

    private sealed class PlanetDto
    {
        public string? Name { get; set; }
        public double SemiMajorAxisAU { get; set; }
        public double Eccentricity { get; set; }
        public double InclinationDeg { get; set; }
        public double LongAscNodeDeg { get; set; }
        public double ArgPerihelionDeg { get; set; }
        public double MeanLongitudeDeg { get; set; }
        public double OrbitalPeriodYears { get; set; }
        public double SemiMajorAxisDot { get; set; }
        public double EccentricityDot { get; set; }
        public double InclinationDotDeg { get; set; }
        public double LongAscNodeDotDeg { get; set; }
        public double ArgPerihelionDotDeg { get; set; }
        public double MeanLongitudeDotDeg { get; set; }
        public float VisualRadius { get; set; }
        public double RealRadiusKm { get; set; }
        public float[]? Color { get; set; }
        public string? TextureFile { get; set; }
        public float AxisTiltDeg { get; set; }
        public double RotationPeriodHours { get; set; }
    }
}
