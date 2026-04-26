using OpenTK.Mathematics;

namespace SolarSystem;

public static class OrbitalMechanics
{
    public const double DegToRad = Math.PI / 180.0;

    /// <summary>Solve Kepler's equation M = E - e*sin(E) for E using Newton-Raphson.</summary>
    public static double SolveKepler(double meanAnomaly, double e)
    {
        double M = meanAnomaly % (2.0 * Math.PI);
        if (M < 0) M += 2.0 * Math.PI;
        double E = e < 0.8 ? M : Math.PI;
        for (int i = 0; i < 30; i++)
        {
            double f = E - e * Math.Sin(E) - M;
            double fp = 1.0 - e * Math.Cos(E);
            double d = f / fp;
            E -= d;
            if (Math.Abs(d) < 1e-10) break;
        }
        return E;
    }

    /// <summary>
    /// Compute heliocentric ecliptic position (in AU) at given Julian centuries since J2000.
    /// </summary>
    public static Vector3d HeliocentricPosition(Planet p, double daysSinceJ2000)
    {
        double a = p.SemiMajorAxisAU;
        double e = p.Eccentricity;
        double i = p.InclinationDeg * DegToRad;
        double Om = p.LongAscNodeDeg * DegToRad;
        double w = p.ArgPerihelionDeg * DegToRad;
        double L0 = p.MeanLongitudeDeg * DegToRad;

        // Mean motion (rad/day): 2π / (period_years * 365.25)
        double n = 2.0 * Math.PI / (p.OrbitalPeriodYears * 365.25);
        double L = L0 + n * daysSinceJ2000;
        double M = L - (Om + w);

        double E = SolveKepler(M, e);
        double xv = a * (Math.Cos(E) - e);
        double yv = a * (Math.Sqrt(1 - e * e) * Math.Sin(E));
        double trueAnom = Math.Atan2(yv, xv);
        double r = Math.Sqrt(xv * xv + yv * yv);

        double u = trueAnom + w; // arg of latitude
        double cosO = Math.Cos(Om), sinO = Math.Sin(Om);
        double cosI = Math.Cos(i), sinI = Math.Sin(i);
        double cosU = Math.Cos(u), sinU = Math.Sin(u);

        double x = r * (cosO * cosU - sinO * sinU * cosI);
        double y = r * (sinO * cosU + cosO * sinU * cosI);
        double z = r * (sinU * sinI);
        // Map ecliptic (x,y,z) to OpenGL (x, z_up, -y) so orbital plane is XZ.
        return new Vector3d(x, z, -y);
    }

    /// <summary>Sample N points along the orbit ellipse (in AU, world-Y up).</summary>
    public static Vector3d[] SampleOrbit(Planet p, int samples)
    {
        var pts = new Vector3d[samples];
        double a = p.SemiMajorAxisAU;
        double e = p.Eccentricity;
        double i = p.InclinationDeg * DegToRad;
        double Om = p.LongAscNodeDeg * DegToRad;
        double w = p.ArgPerihelionDeg * DegToRad;
        for (int k = 0; k < samples; k++)
        {
            double E = 2.0 * Math.PI * k / samples;
            double xv = a * (Math.Cos(E) - e);
            double yv = a * (Math.Sqrt(1 - e * e) * Math.Sin(E));
            double trueAnom = Math.Atan2(yv, xv);
            double r = Math.Sqrt(xv * xv + yv * yv);
            double u = trueAnom + w;
            double cosO = Math.Cos(Om), sinO = Math.Sin(Om);
            double cosI = Math.Cos(i), sinI = Math.Sin(i);
            double cosU = Math.Cos(u), sinU = Math.Sin(u);
            double x = r * (cosO * cosU - sinO * sinU * cosI);
            double y = r * (sinO * cosU + cosO * sinU * cosI);
            double z = r * (sinU * sinI);
            pts[k] = new Vector3d(x, z, -y);
        }
        return pts;
    }

    /// <summary>J2000 epoch = January 1.5, 2000 TT.</summary>
    public static readonly DateTime J2000 = new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    // Non-linear visual mapping from AU to world units: r_world = K * a^p,
    // applied UNIFORMLY per-orbit (using semi-major axis) so eccentric ellipses keep their shape.
    // p<1 spreads the inner planets out so they don't get swallowed by the Sun's halo,
    // while still keeping Neptune on screen.
    private const double Power = 0.45;
    // Chosen so Neptune (a ≈ 30.07 AU) lands at ~200 world units.
    private const double K = 200.0 / 4.6739; // 30.07^0.45 ≈ 4.6739

    /// <summary>When true, distances are mapped LINEARLY (1 AU = AuToWorldRealScale units)
    /// and bodies use radii derived from real kilometres via KmToWorldRealScale. The result
    /// is astronomically truthful — and visually brutal: planets become near-invisible dots
    /// while Neptune sits ~1500 units from the Sun.</summary>
    public static bool RealScale = false;

    /// <summary>1 AU in world units when <see cref="RealScale"/> is on.</summary>
    public const double AuToWorldRealScale = 50.0;
    /// <summary>1 km in world units when <see cref="RealScale"/> is on (uses the same
    /// linear scale as AuToWorldRealScale, so radii and distances share one unit system).</summary>
    public const double KmToWorldRealScale = AuToWorldRealScale / 1.495978707e8;

    /// <summary>Per-orbit uniform scale factor that converts AU vectors to world-unit vectors.</summary>
    public static float OrbitWorldScale(double semiMajorAxisAU)
        => RealScale
            ? (float)AuToWorldRealScale
            : (float)(K * Math.Pow(semiMajorAxisAU, Power - 1.0));
}
