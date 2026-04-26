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

    /// <summary>Visual radius in world units (NOT physical). Mutable so the host can swap
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
}
