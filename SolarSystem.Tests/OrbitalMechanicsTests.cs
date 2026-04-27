using OpenTK.Mathematics;
using Xunit;

namespace SolarSystem.Tests;

/// <summary>
/// A9: Validate <see cref="OrbitalMechanics.SolveKepler"/>,
/// <see cref="OrbitalMechanics.HeliocentricPosition"/> and
/// <see cref="OrbitalMechanics.OrbitWorldScale"/> against canonical J2000
/// ephemerides and analytic edge cases. The numbers here do not need to
/// match JPL Horizons to the millisecond — the J2000 mean elements baked
/// into <see cref="Planet"/> are the simple Standish two-body approximation,
/// not the full DE440. We pin known-good results so future refactors of
/// the Kepler solver or the ecliptic→GL frame swap don't silently break
/// the simulation.
/// </summary>
public class OrbitalMechanicsTests
{
    private const double Tau = 2.0 * Math.PI;

    // ---- SolveKepler ---------------------------------------------------

    [Fact]
    public void SolveKepler_ZeroEccentricity_ReturnsMeanAnomaly()
    {
        // Circular orbit ⇒ E ≡ M (modulo 2π).
        for (double M = -Math.PI; M <= Math.PI; M += 0.25)
        {
            double E = OrbitalMechanics.SolveKepler(M, 0.0);
            // SolveKepler normalises to [0, 2π).
            double expected = M; while (expected < 0) expected += Tau; expected %= Tau;
            Assert.Equal(expected, E, 9);
        }
    }

    [Fact]
    public void SolveKepler_RoundTrip_SatisfiesKeplersEquation()
    {
        // For every (M, e) the solver must produce E s.t. M ≈ E - e·sin(E).
        double[] eccs = { 0.0, 0.05, 0.2, 0.5, 0.7, 0.9, 0.95 };
        foreach (var e in eccs)
        {
            for (double M = 0.0; M < Tau; M += 0.31)
            {
                double E = OrbitalMechanics.SolveKepler(M, e);
                double residual = (E - e * Math.Sin(E)) - M;
                // Wrap into [-π, π].
                while (residual >  Math.PI) residual -= Tau;
                while (residual < -Math.PI) residual += Tau;
                Assert.True(Math.Abs(residual) < 1e-9,
                    $"SolveKepler residual too large for e={e}, M={M}: {residual}");
            }
        }
    }

    [Fact]
    public void SolveKepler_NormalisesNegativeMeanAnomaly()
    {
        double E = OrbitalMechanics.SolveKepler(-0.5, 0.0);
        Assert.InRange(E, 0.0, Tau);
    }

    // ---- HeliocentricPosition ------------------------------------------

    [Fact]
    public void HeliocentricPosition_AtJ2000_PlacesEarthAtOneAU()
    {
        // Earth's J2000 heliocentric distance is ≈ 0.9833 AU (perihelion is
        // early January). Distance must be within a few percent of 1 AU and
        // strictly inside the [a(1-e), a(1+e)] band.
        var earth = FindPlanet("Earth");
        var pos = OrbitalMechanics.HeliocentricPosition(earth, 0.0);
        double r = pos.Length;
        double rMin = earth.SemiMajorAxisAU * (1.0 - earth.Eccentricity);
        double rMax = earth.SemiMajorAxisAU * (1.0 + earth.Eccentricity);
        Assert.InRange(r, rMin - 1e-6, rMax + 1e-6);
        Assert.InRange(r, 0.95, 1.05);
        // Out-of-plane component must be tiny — Earth's inclination is
        // essentially zero relative to the ecliptic.
        Assert.True(Math.Abs(pos.Y) < 0.01,
            $"Earth's |y| at J2000 should be ~0, got {pos.Y}");
    }

    [Fact]
    public void HeliocentricPosition_OneFullPeriod_ReturnsToStart()
    {
        // For Mars: heliocentric position after one orbital period must be
        // (essentially) identical to the J2000 position — pure two-body
        // dynamics. Also a regression test for the secular-rate path
        // (Mars ships non-zero *_Dot fields).
        var mars = FindPlanet("Mars");
        var p0 = OrbitalMechanics.HeliocentricPosition(mars, 0.0);
        double period = mars.OrbitalPeriodYears * 365.25;
        var p1 = OrbitalMechanics.HeliocentricPosition(mars, period);
        // Secular drift over 1 period is small but non-zero, so allow ~0.01 AU.
        Assert.True((p1 - p0).Length < 0.02,
            $"Mars one-period drift too large: {(p1 - p0).Length} AU");
    }

    [Fact]
    public void HeliocentricPosition_RespectsZeroInclination_ForEarth()
    {
        // Sample Earth's position at quarter-year intervals: every sample
        // must lie within ±0.001 AU of the ecliptic plane (y=0 in GL frame).
        var earth = FindPlanet("Earth");
        for (double d = 0; d < 365.25; d += 30.0)
        {
            var p = OrbitalMechanics.HeliocentricPosition(earth, d);
            Assert.True(Math.Abs(p.Y) < 0.001,
                $"Earth out-of-plane component at day {d}: {p.Y}");
        }
    }

    // ---- OrbitWorldScale ------------------------------------------------

    [Fact]
    public void OrbitWorldScale_Compressed_IsMonotonicallyDecreasing()
    {
        // p<1 ⇒ scale × a = K · a^p shrinks per-AU as a grows. We check
        // that the per-orbit scale factor is strictly decreasing with a.
        bool prevReal = OrbitalMechanics.RealScale;
        OrbitalMechanics.RealScale = false;
        try
        {
            float prev = OrbitalMechanics.OrbitWorldScale(0.39); // Mercury
            foreach (double a in new[] { 0.72, 1.0, 1.52, 5.20, 9.54, 19.19, 30.07 })
            {
                float s = OrbitalMechanics.OrbitWorldScale(a);
                Assert.True(s < prev, $"Scale not decreasing at a={a}: {s} >= {prev}");
                prev = s;
            }
        }
        finally
        {
            OrbitalMechanics.RealScale = prevReal;
        }
    }

    [Fact]
    public void OrbitWorldScale_RealScale_IsConstantAuToWorld()
    {
        bool prevReal = OrbitalMechanics.RealScale;
        OrbitalMechanics.RealScale = true;
        try
        {
            float expected = (float)OrbitalMechanics.AuToWorldRealScale;
            Assert.Equal(expected, OrbitalMechanics.OrbitWorldScale(0.39), 5);
            Assert.Equal(expected, OrbitalMechanics.OrbitWorldScale(30.07), 5);
        }
        finally
        {
            OrbitalMechanics.RealScale = prevReal;
        }
    }

    [Fact]
    public void OrbitWorldScale_Compressed_PutsNeptuneNearTwoHundredUnits()
    {
        // The K constant is calibrated so Neptune (a ≈ 30.07 AU) lands at
        // ~200 world units in compressed scale. Pin that contract.
        bool prevReal = OrbitalMechanics.RealScale;
        OrbitalMechanics.RealScale = false;
        try
        {
            float scale = OrbitalMechanics.OrbitWorldScale(30.07);
            float r = scale * 30.07f;
            Assert.InRange(r, 195f, 205f);
        }
        finally
        {
            OrbitalMechanics.RealScale = prevReal;
        }
    }

    // ---- helpers --------------------------------------------------------

    private static Planet FindPlanet(string name)
    {
        foreach (var p in Planet.CreateAll())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p;
        throw new InvalidOperationException($"Planet '{name}' not found in built-in catalogue");
    }
}
