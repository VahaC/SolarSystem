using OpenTK.Mathematics;
using Xunit;

namespace SolarSystem.Tests;

/// <summary>
/// Physics sandbox core: the hierarchical Yoshida/leapfrog N-body integrator in
/// <see cref="PhysicsWorld"/>. No OpenGL context needed — the world only touches
/// <see cref="Planet"/> / <see cref="Moon"/> records and the analytic ephemerides.
/// </summary>
public class PhysicsWorldTests
{
    private const double Year = 365.25;
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public PhysicsWorldTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private static Planet[] Majors() => Planet.CreateAll();
    private static Planet[] AllPlanets() => [.. Planet.CreateAll(), .. Planet.CreateDwarfPlanets()];

    private static Planet EarthMoon() => new()
    {
        Name = "Moon", RealRadiusKm = 1737.4, SemiMajorAxisAU = 0.00257,
        OrbitalPeriodYears = 27.321661 / 365.25, RotationPeriodHours = 27.321661 * 24.0,
    };

    private static Planet[] Comets() => CometCatalog.LoadOrDefault().Select(e => e.Body).ToArray();

    /// <summary>Sun + the eight majors only (no dwarfs, no satellites, no comets).</summary>
    private static PhysicsWorld PlanetsOnly(double t0 = 0.0)
    {
        var w = PhysicsWorld.Create(Majors(), 8, null, [], []);
        w.Initialize(t0);
        return w;
    }

    /// <summary>Everything the app integrates: 8 majors, 5 dwarfs, Moon, Galileans, Titan, comets.</summary>
    private static PhysicsWorld Full(double t0 = 0.0)
    {
        var w = PhysicsWorld.Create(AllPlanets(), 8, EarthMoon(), PhysicsWorld.DefaultSatellites(), Comets());
        w.Initialize(t0);
        return w;
    }

    private static double AngleBetweenDeg(Vector3d a, Vector3d b)
    {
        double c = Vector3d.Dot(a, b) / (a.Length * b.Length);
        return Math.Acos(Math.Clamp(c, -1.0, 1.0)) / OrbitalMechanics.DegToRad;
    }

    // ---- Seeding ----------------------------------------------------------------------

    [Fact]
    public void Initialize_ReproducesEphemerisPositions_AtStart()
    {
        double t0 = 5000.0;
        var w = Full(t0);
        Assert.True(w.Ready);
        Assert.Equal(t0, w.TimeDays);
        foreach (var body in w.Bodies)
        {
            int i = w.IndexOf(body.Name);
            if (body.Kind == PhysicsBodyKind.Star) continue;
            Vector3d expected, actual;
            if (body.Parent >= 0)
            {
                expected = body.Ephemeris(t0);
                actual = w.AbsolutePosition(i) - w.AbsolutePosition(body.Parent);
            }
            else
            {
                expected = body.Ephemeris(t0);
                actual = w.HeliocentricPosition(i);
            }
            // Fitted seeds (the Moon) may sit a few tens of km off the raw ephemeris point.
            double tol = body.SeedFitWindowDays > 0 ? 5e-6 : 1e-9;
            _out.WriteLine($"{body.Name}: seed offset {(expected - actual).Length * PhysicsWorld.AuKm:0.###} km");
            Assert.True((expected - actual).Length < tol,
                $"{body.Name}: expected {expected}, got {actual}");
        }
        // Barycentric frame: total momentum of the massive bodies is zero.
        Vector3d p = Vector3d.Zero;
        for (int i = 0; i < w.Bodies.Count; i++)
            if (w.Bodies[i].IsMassive) p += w.AbsoluteVelocity(i) * w.Bodies[i].Mass;
        Assert.True(p.Length < 1e-12, $"net momentum {p}");
    }

    [Fact]
    public void Initialize_VelocitiesAreCloseToKeplerian()
    {
        var w = PlanetsOnly();
        for (int i = 1; i < w.Bodies.Count; i++)
        {
            var el = w.OsculatingElements(i);
            var p = w.Bodies[i].Source!;
            Assert.True(el.Bound, $"{p.Name} unbound");
            Assert.InRange(el.SemiMajorAxisAU, p.SemiMajorAxisAU * 0.995, p.SemiMajorAxisAU * 1.005);
            Assert.InRange(el.PeriodDays, p.OrbitalPeriodYears * Year * 0.99, p.OrbitalPeriodYears * Year * 1.01);
        }
    }

    // ---- Conservation ---------------------------------------------------------------------

    [Fact]
    public void Energy_IsConservedTo1e6_Over100Years_ForPlanets()
    {
        var w = PlanetsOnly();
        double e0 = w.Energy();
        var l0 = w.AngularMomentum();
        double worst = 0.0;
        for (int year = 1; year <= 100; year++)
        {
            Assert.True(w.AdvanceTo(year * Year));
            worst = Math.Max(worst, Math.Abs((w.Energy() - e0) / e0));
        }
        _out.WriteLine($"worst |dE/E| over 100 y: {worst:E2}");
        Assert.True(worst < 1e-6, $"relative energy drift {worst:E2}");
        var l1 = w.AngularMomentum();
        Assert.True((l1 - l0).Length / l0.Length < 1e-9, $"angular momentum drift {(l1 - l0).Length / l0.Length:E2}");
    }

    [Fact]
    public void Planets_StayCloseToEphemeris_OverADecade()
    {
        // The Standish elements already contain the secular effect of the mutual
        // perturbations, so a full N-body run and the analytic orbits should agree to
        // a fraction of a degree over ten years (the difference is the whole point of
        // Compare mode, but it must be small).
        // Caveat: the seed is the *mean* Standish orbit, not the osculating state, so the
        // short-period Jupiter–Saturn terms (several arcminutes, ~20-year synodic period)
        // bias Saturn's seeded energy and it drifts ~0.7°/decade; everything else stays
        // well under half a degree.
        var w = PlanetsOnly();
        w.AdvanceTo(10 * Year);
        for (int i = 1; i < w.Bodies.Count; i++)
        {
            var b = w.Bodies[i];
            var eph = b.Ephemeris(w.TimeDays);
            double deg = AngleBetweenDeg(eph, w.HeliocentricPosition(i));
            _out.WriteLine($"{b.Name}: {deg:0.000}° from ephemeris after 10 y");
            Assert.True(deg < 1.5, $"{b.Name} drifted {deg:0.000}° from the ephemeris after 10 years");
        }
    }

    [Fact]
    public void Moon_StaysWithin1Deg_OfElp2000_After10Years()
    {
        var w = Full();
        int moon = w.IndexOf("Moon");
        int earth = w.IndexOf("Earth");
        Assert.True(moon > 0 && earth > 0);
        Assert.True(w.AdvanceTo(10 * Year));
        var physOffset = w.AbsolutePosition(moon) - w.AbsolutePosition(earth);
        var elpOffset = PhysicsWorld.MoonOffsetAU(w.TimeDays);
        double deg = AngleBetweenDeg(physOffset, elpOffset);
        _out.WriteLine($"Moon vs ELP after 10 y: {deg:0.000}°");
        Assert.True(deg < 1.0, $"Moon is {deg:0.000}° from ELP-2000 after 10 years");
        // Distance sanity: still a 356 000–407 000 km orbit.
        double km = physOffset.Length * PhysicsWorld.AuKm;
        Assert.InRange(km, 350_000, 410_000);
    }

    [Fact]
    public void Galileans_KeepTheirPeriods_OverAYear()
    {
        var w = Full();
        w.AdvanceTo(Year);
        foreach (var m in PhysicsWorld.DefaultSatellites())
        {
            int i = w.IndexOf(m.Body.Name);
            var el = w.OsculatingElements(i);
            Assert.True(el.Bound, $"{m.Body.Name} unbound");
            Assert.InRange(el.PeriodDays, m.OrbitalPeriodDays * 0.99, m.OrbitalPeriodDays * 1.01);
            Assert.True(el.Eccentricity < 0.05, $"{m.Body.Name} e = {el.Eccentricity}");
        }
    }

    // ---- Constants ---------------------------------------------------------------------------

    [Fact]
    public void DoublingSunMass_CircularOrbitPeriod_IsShorterBySqrt2()
    {
        // Kepler III under the new constant: a body on a circular 1 AU orbit around a
        // 2 M☉ Sun must complete a revolution in 365.25 / √2 days. Earth is re-seeded
        // onto that circular orbit (velocity × √2) so the test isolates the force law.
        var w = PlanetsOnly();
        w.Constants.SunMassScale = 2.0;
        w.RefreshMasses();
        int earth = w.IndexOf("Earth");
        var b = w.Bodies[earth];
        var sun = w.Bodies[0];
        var rel = b.Vel - sun.Vel;
        b.Vel = sun.Vel + rel * Math.Sqrt(2.0);

        var r0 = w.HeliocentricPosition(earth);
        double swept = 0.0;
        double prevAngle = Math.Atan2(-r0.Z, r0.X);
        double tPrev = w.TimeDays;
        double period = double.NaN;
        while (w.TimeDays < 400.0)
        {
            w.Step(0.25);
            var r = w.HeliocentricPosition(earth);
            double ang = Math.Atan2(-r.Z, r.X);
            double d = ang - prevAngle;
            while (d > Math.PI) d -= 2 * Math.PI;
            while (d < -Math.PI) d += 2 * Math.PI;
            double before = swept;
            swept += d;
            prevAngle = ang;
            if (before < 2 * Math.PI && swept >= 2 * Math.PI)
            {
                double f = (2 * Math.PI - before) / (swept - before);
                period = tPrev + f * (w.TimeDays - tPrev);
                break;
            }
            tPrev = w.TimeDays;
        }
        Assert.False(double.IsNaN(period), "Earth never completed an orbit");
        double expected = Year / Math.Sqrt(2.0);
        Assert.InRange(period, expected - 1.0, expected + 1.0);
    }

    [Fact]
    public void ChangingAConstant_DoesNotReseedBodies()
    {
        var w = PlanetsOnly();
        int earth = w.IndexOf("Earth");
        var pos = w.Bodies[earth].Pos;
        var vel = w.Bodies[earth].Vel;
        w.Constants.SunMassScale = 2.0;
        w.AdvanceTo(w.TimeDays); // zero-length advance still refreshes masses
        Assert.Equal(pos, w.Bodies[earth].Pos);
        Assert.Equal(vel, w.Bodies[earth].Vel);
        // With the real velocity but twice the central mass, Earth drops onto an
        // ellipse with a = 2/3 AU (vis-viva) instead of a new circular orbit.
        var el = w.OsculatingElements(earth);
        Assert.InRange(el.SemiMajorAxisAU, 0.64, 0.69);
        Assert.True(el.Bound);
        // It also stays bound and keeps integrating without blowing up.
        w.AdvanceTo(w.TimeDays + 365.0);
        Assert.True(w.OsculatingElements(earth).Bound);
        Assert.True(double.IsFinite(w.Energy()));
    }

    [Fact]
    public void GravityExponent_ChangesTheForce_ButKeepsKeplerAtTwo()
    {
        var w = PlanetsOnly();
        int jup = w.IndexOf("Jupiter");
        int earth = w.IndexOf("Earth");
        w.Step(1e-6);
        double aJup2 = (w.Bodies[jup].Acc - w.Bodies[0].Acc).Length;
        double aEarth2 = (w.Bodies[earth].Acc - w.Bodies[0].Acc).Length;
        double rJup = w.HeliocentricPosition(jup).Length;
        double rEarth = w.HeliocentricPosition(earth).Length;
        // Inverse-square sanity: GM☉/r² dominates (mutual terms are < 0.1 %).
        Assert.InRange(aJup2 / (PhysicsWorld.GM_SUN / (rJup * rJup)), 0.995, 1.005);

        w.Constants.GravityExponent = 2.5;
        w.Step(1e-6);
        double aJup25 = (w.Bodies[jup].Acc - w.Bodies[0].Acc).Length;
        double aEarth25 = (w.Bodies[earth].Acc - w.Bodies[0].Acc).Length;
        // a ∝ r^-n: at r ≈ 1 AU the laws coincide, at 5.2 AU the steeper law is r^-0.5 weaker.
        Assert.InRange(aJup25 / aJup2, Math.Pow(rJup, -0.5) * 0.99, Math.Pow(rJup, -0.5) * 1.01);
        Assert.InRange(aEarth25 / aEarth2, Math.Pow(rEarth, -0.5) * 0.99, Math.Pow(rEarth, -0.5) * 1.01);
        Assert.True(double.IsFinite(w.Energy()));
    }

    [Fact]
    public void Mercury_DoesNotPrecess_WithoutOtherPlanets_AtExponentTwo()
    {
        // Two-body Kepler problem: with n = 2 the perihelion is fixed, so any drift is
        // integrator error. It must stay well below the real planetary perturbation
        // (~531″/century ≈ 0.15°).
        var mercury = Majors().Where(p => p.Name == "Mercury").ToArray();
        var w = PhysicsWorld.Create(mercury, 1, null, [], []);
        w.Initialize(0.0);
        double w0 = w.PerihelionLongitudeDeg(1);
        w.AdvanceTo(100 * Year);
        double w1 = w.PerihelionLongitudeDeg(1);
        double drift = Math.Abs(w1 - w0);
        if (drift > 180) drift = 360 - drift;
        _out.WriteLine($"two-body Mercury perihelion drift: {drift * 3600:0.0}\" / century");
        Assert.True(drift < 0.02, $"numerical perihelion drift {drift * 3600:0} arcsec / century");
    }

    [Fact]
    public void Mercury_Precession_WithPlanets_MatchesNewtonianOrderOfMagnitude()
    {
        // With every planet present the perihelion advances ~531″/century (Newtonian,
        // no GR). The osculating value wobbles by a few arcminutes, so allow a wide
        // but bounded window: clearly positive, clearly below 0.5°.
        var w = PlanetsOnly();
        double w0 = w.PerihelionLongitudeDeg(1);
        w.AdvanceTo(100 * Year);
        double w1 = w.PerihelionLongitudeDeg(1);
        double drift = w1 - w0;
        while (drift > 180) drift -= 360;
        while (drift < -180) drift += 360;
        _out.WriteLine($"Mercury perihelion advance with planets: {drift * 3600:0}\" / century");
        Assert.InRange(drift, 0.02, 0.5);
    }

    [Fact]
    public void GravityExponent_NotTwo_ProducesPrecession()
    {
        var mercury = Majors().Where(p => p.Name == "Mercury").ToArray();
        var w = PhysicsWorld.Create(mercury, 1, null, [], []);
        w.Initialize(0.0);
        w.Constants.GravityExponent = 2.05;
        double w0 = w.PerihelionLongitudeDeg(1);
        w.AdvanceTo(10 * Year);
        double w1 = w.PerihelionLongitudeDeg(1);
        double drift = Math.Abs(w1 - w0);
        if (drift > 180) drift = 360 - drift;
        Assert.True(drift > 1.0, $"expected a large precession, got {drift:0.00}°");
    }

    // ---- Reset / advance mechanics ----------------------------------------------------------

    [Fact]
    public void Reset_RestoresExactInitialState()
    {
        var w = Full(1234.5);
        var pos = w.Bodies.Select(b => b.Pos).ToArray();
        var vel = w.Bodies.Select(b => b.Vel).ToArray();
        w.AdvanceTo(1234.5 + 400.0);
        Assert.NotEqual(pos[3], w.Bodies[3].Pos);
        w.Reset();
        Assert.Equal(1234.5, w.TimeDays);
        for (int i = 0; i < w.Bodies.Count; i++)
        {
            Assert.Equal(pos[i], w.Bodies[i].Pos);
            Assert.Equal(vel[i], w.Bodies[i].Vel);
        }
        Assert.Equal(0.0, w.EnergyDrift(), 12);
    }

    [Fact]
    public void Step_IsTimeReversible()
    {
        // Leapfrog / Yoshida is symmetric: the same step sequence run backwards lands on
        // the starting state to round-off.
        var w = Full();
        var start = w.Bodies.Select(b => b.Pos).ToArray();
        for (int k = 0; k < 400; k++) w.Step(0.25);
        for (int k = 0; k < 400; k++) w.Step(-0.25);
        for (int i = 0; i < w.Bodies.Count; i++)
        {
            // Satellite sub-step counts are adaptive, so their reverse sequence is not an
            // exact mirror; allow ~150 km there, round-off for everything else.
            double tol = w.Bodies[i].Parent >= 0 ? 1e-6 : 1e-10;
            Assert.True((start[i] - w.Bodies[i].Pos).Length < tol,
                $"{w.Bodies[i].Name} did not return ({(start[i] - w.Bodies[i].Pos).Length:E1} AU)");
        }
        Assert.True(Math.Abs(w.TimeDays) < 1e-9);
    }

    [Fact]
    public void AdvanceTo_Backwards_ReturnsCloseToStart()
    {
        // AdvanceTo picks its own (adaptive) steps and a remainder step, so the reverse
        // sequence is not an exact mirror — but it must still land within metres.
        var w = PlanetsOnly();
        var start = w.Bodies.Select(b => b.Pos).ToArray();
        w.AdvanceTo(200.0);
        w.AdvanceTo(0.0);
        for (int i = 0; i < w.Bodies.Count; i++)
            Assert.True((start[i] - w.Bodies[i].Pos).Length < 1e-7,
                $"{w.Bodies[i].Name} off by {(start[i] - w.Bodies[i].Pos).Length:E1} AU");
    }

    [Fact]
    public void AdvanceTo_HonoursStepBudget_AndReportsProgress()
    {
        var w = PlanetsOnly();
        bool done = w.AdvanceTo(1000.0, maxGlobalSteps: 10);
        Assert.False(done);
        Assert.Equal(10, w.LastGlobalSteps);
        Assert.True(w.TimeDays > 0 && w.TimeDays < 1000.0);
        // 3 recorded leapfrog sub-steps per global step + the initial snapshot.
        Assert.Equal(1 + 3 * 10, w.Records.Count);
        Assert.Equal(0.0, w.Records[0].StepDays);
        Assert.Equal(w.RecordBodies.Count, w.Records[0].Positions.Length);
        Assert.Equal(w.RecordBodies.Count, w.RecordGM.Count);
        done = w.AdvanceTo(1000.0);
        Assert.True(done);
        Assert.Equal(1000.0, w.TimeDays);
    }

    [Fact]
    public void GlobalStep_AdaptsToTheFastestBody()
    {
        var w = PlanetsOnly();
        w.AdvanceTo(10.0);
        // Mercury (88 d) forces ≥ 300 steps per orbit ⇒ ~0.29 d, below the 0.5 d cap.
        _out.WriteLine($"adaptive global step: {w.CurrentGlobalStepDays:0.0000} d");
        Assert.InRange(w.CurrentGlobalStepDays, 0.2, 0.5);
        Assert.True(w.LastGlobalSteps >= 20);
    }

    [Fact]
    public void Comets_AreTestParticles_AndFollowTheirOrbits()
    {
        var w = Full();
        double e0 = w.Energy();
        var halley = w.Bodies.First(b => b.Kind == PhysicsBodyKind.TestParticle);
        int idx = w.IndexOf(halley.Name);
        Assert.Equal(0.0, halley.Mass);
        w.AdvanceTo(Year);
        var eph = halley.Ephemeris(w.TimeDays);
        double deg = AngleBetweenDeg(eph, w.HeliocentricPosition(idx));
        Assert.True(deg < 2.0, $"{halley.Name} drifted {deg:0.00}° from its Kepler orbit in a year");
        // Test particles do not contribute to the energy budget.
        Assert.True(Math.Abs((w.Energy() - e0) / e0) < 1e-6);
    }

    [Fact]
    public void Constants_ClampAndRoundTrip()
    {
        var c = new PhysicsConstants { G = 1e9, GravityExponent = 0.1, SunMassScale = double.NaN };
        c.SetBodyMassScale("Earth", 1e6);
        c.Sanitize();
        Assert.Equal(PhysicsConstants.GMax, c.G);
        Assert.Equal(PhysicsConstants.ExponentMin, c.GravityExponent);
        Assert.Equal(1.0, c.SunMassScale);
        Assert.Equal(PhysicsConstants.BodyMassMax, c.GetBodyMassScale("Earth"));
        var dto = c.ToDto();
        var d = new PhysicsConstants();
        d.LoadDto(dto);
        Assert.Equal(c.G, d.G);
        Assert.Equal(c.GetBodyMassScale("Earth"), d.GetBodyMassScale("Earth"));
        d.Reset();
        Assert.True(d.IsDefault);
        Assert.Equal(1.0, d.GetBodyMassScale("Earth"));
    }
}
