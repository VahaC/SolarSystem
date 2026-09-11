using OpenTK.Mathematics;
using Xunit;

namespace SolarSystem.Tests;

/// <summary>
/// Collisions in the physics sandbox: swept contact detection (global step for
/// heliocentric pairs, per sub-step inside a planet + moons subsystem) and the
/// perfectly inelastic merge that follows — mass, momentum and volume are summed,
/// the lighter body is removed, satellites are re-parented, and both
/// <see cref="PhysicsWorld.Reset"/> and <see cref="PhysicsWorld.Initialize"/> revive
/// everything. No OpenGL involved.
/// </summary>
public class PhysicsCollisionTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public PhysicsCollisionTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private static Planet[] AllPlanets() => [.. Planet.CreateAll(), .. Planet.CreateDwarfPlanets()];

    private static Planet EarthMoon() => new()
    {
        Name = "Moon", RealRadiusKm = 1737.4, SemiMajorAxisAU = 0.00257,
        OrbitalPeriodYears = 27.321661 / 365.25, RotationPeriodHours = 27.321661 * 24.0,
    };

    private static Planet[] Comets() => CometCatalog.LoadOrDefault().Select(e => e.Body).ToArray();

    /// <summary>The app's full body set, seeded at <paramref name="t0"/>.</summary>
    private static PhysicsWorld Full(double t0 = 0.0)
    {
        var w = PhysicsWorld.Create(AllPlanets(), 8, EarthMoon(), PhysicsWorld.DefaultSatellites(), Comets());
        w.Initialize(t0);
        return w;
    }

    /// <summary>Sun + one body at a fixed heliocentric position with an optional
    /// analytic velocity (a constant ephemeris seeds a zero velocity).</summary>
    private static PhysicsWorld SunAnd(string name, PhysicsBodyKind kind, double mass, double radiusKm,
        Vector3d posAU, Vector3d? velAUPerDay = null)
    {
        var w = new PhysicsWorld();
        w.AddBody(new PhysicsBody
        {
            Name = "Sun", Kind = PhysicsBodyKind.Star, BaseMass = 1.0, RadiusKm = PhysicsWorld.SunRadiusKm,
            Ephemeris = _ => Vector3d.Zero,
        });
        w.AddBody(new PhysicsBody
        {
            Name = name, Kind = kind, BaseMass = mass, RadiusKm = radiusKm,
            Ephemeris = _ => posAU,
            EphemerisVelocity = velAUPerDay is { } v ? _ => v : null,
        });
        w.Initialize(0.0);
        return w;
    }

    private static Vector3d TotalMomentum(PhysicsWorld w)
    {
        Vector3d p = Vector3d.Zero;
        for (int i = 0; i < w.Bodies.Count; i++)
        {
            var b = w.Bodies[i];
            if (b.Alive && b.IsMassive) p += w.AbsoluteVelocity(i) * b.Mass;
        }
        return p;
    }

    // ---- Heliocentric pairs ------------------------------------------------------------------

    [Fact]
    public void Planet_FallingIntoTheSun_IsAbsorbed_AndMomentumIsConserved()
    {
        const double m = 3.0e-6, r = 6371.0;
        var w = SunAnd("Rock", PhysicsBodyKind.Planet, m, r, new Vector3d(0.1, 0, 0));
        var sun = w.Bodies[0];
        var rock = w.Bodies[1];
        double sunRadius0 = sun.RadiusKm;
        var p0 = TotalMomentum(w);

        // Free fall from 0.1 AU takes ~2 days; give it ten.
        Assert.True(w.AdvanceTo(10.0));

        Assert.False(rock.Alive);
        Assert.Equal(0, rock.AbsorbedBy);
        Assert.True(sun.Alive);
        Assert.Equal(1.0 + m, sun.Mass, 12);
        Assert.Equal(Math.Cbrt(sunRadius0 * sunRadius0 * sunRadius0 + r * r * r), sun.RadiusKm, 6);
        var ev = Assert.Single(w.Collisions);
        Assert.Equal("Sun", ev.SurvivorName);
        Assert.Equal("Rock", ev.AbsorbedName);
        Assert.Equal(m, ev.AbsorbedMass, 15);
        Assert.InRange(ev.TimeDays, 1.0, 3.0);
        Assert.True(ev.ImpactEnergy > 0, "an inelastic merge must shed energy");
        double mu = 1.0 * m / (1.0 + m);
        Assert.Equal(1.0, ev.ImpactEnergy / (0.5 * mu * ev.RelativeSpeedAUPerDay * ev.RelativeSpeedAUPerDay), 9);
        Assert.True(ev.RelativeSpeedAUPerDay > 0.1, $"impact speed {ev.RelativeSpeedAUPerDay} AU/d");
        // Barycentric frame: the merged Sun carries the (zero) total momentum.
        Assert.True((TotalMomentum(w) - p0).Length < 1e-15);
        Assert.True(w.AbsoluteVelocity(0).Length < 1e-12, $"Sun drifting at {w.AbsoluteVelocity(0).Length}");
        // The dead body rides its absorber.
        Assert.Equal(Vector3d.Zero, w.HeliocentricPosition(1));
        // Energy reference was rebased, so the drift reads as integrator error only.
        Assert.True(Math.Abs(w.EnergyDrift()) < 1e-9);
        // Integration keeps going without the victim.
        Assert.True(w.AdvanceTo(30.0));
        Assert.True(double.IsFinite(w.Energy()));
    }

    [Fact]
    public void CollisionsDisabled_BodyPassesThroughTheSun()
    {
        var w = SunAnd("Rock", PhysicsBodyKind.Planet, 3.0e-6, 6371.0, new Vector3d(0.1, 0, 0));
        w.CollisionsEnabled = false;
        Assert.True(w.AdvanceTo(10.0));
        Assert.True(w.Bodies[1].Alive);
        Assert.Empty(w.Collisions);
        Assert.Equal(1.0, w.Bodies[0].Mass);
        var h = w.HeliocentricPosition(1);
        Assert.True(double.IsFinite(h.X) && h.Length > 0.01, $"rock at {h}");
    }

    [Fact]
    public void FastFlyThrough_IsCaughtBySweep_NotByEndpoints()
    {
        // 1 AU/day straight at the Sun: a single 0.5 d step carries the body from
        // x = +0.3 to x = -0.2 AU, i.e. clean through the 0.0047 AU solar disc.
        var w = SunAnd("Bullet", PhysicsBodyKind.Planet, 1e-9, 1000.0, new Vector3d(0.3, 0, 0), new Vector3d(-1.0, 0, 0));
        w.CollisionsEnabled = false;
        w.Step(0.5);
        Assert.True(w.Bodies[1].Alive);
        Assert.True(w.HeliocentricPosition(1).Length > 0.1, "without collisions the body must end up far past the Sun");

        w.Reset();
        w.CollisionsEnabled = true;
        w.Step(0.5);
        Assert.False(w.Bodies[1].Alive);
        Assert.Single(w.Collisions);
    }

    [Fact]
    public void Comet_HittingTheSun_Vanishes_WithoutChangingItsMass()
    {
        var w = SunAnd("Halley", PhysicsBodyKind.TestParticle, 0.0, 5.5, new Vector3d(0.05, 0, 0));
        Assert.True(w.AdvanceTo(5.0));
        Assert.False(w.Bodies[1].Alive);
        Assert.Equal(1.0, w.Bodies[0].Mass);
        Assert.Equal(PhysicsWorld.SunRadiusKm, w.Bodies[0].RadiusKm);
        var ev = Assert.Single(w.Collisions);
        Assert.Equal(0.0, ev.AbsorbedMass);
        Assert.Equal("Halley", ev.AbsorbedName);
    }

    [Fact]
    public void TwoTestParticles_NeverCollide()
    {
        var w = new PhysicsWorld();
        w.AddBody(new PhysicsBody { Name = "Sun", Kind = PhysicsBodyKind.Star, BaseMass = 1.0, RadiusKm = PhysicsWorld.SunRadiusKm, Ephemeris = _ => Vector3d.Zero });
        var pos = new Vector3d(1, 0, 0);
        var vel = new Vector3d(0, 0, -0.0172);
        w.AddBody(new PhysicsBody { Name = "A", Kind = PhysicsBodyKind.TestParticle, RadiusKm = 5, Ephemeris = _ => pos, EphemerisVelocity = _ => vel });
        w.AddBody(new PhysicsBody { Name = "B", Kind = PhysicsBodyKind.TestParticle, RadiusKm = 5, Ephemeris = _ => pos, EphemerisVelocity = _ => vel });
        w.Initialize(0.0);
        w.AdvanceTo(10.0);
        Assert.True(w.Bodies[1].Alive && w.Bodies[2].Alive);
        Assert.Empty(w.Collisions);
    }

    [Fact]
    public void RealSolarSystem_HasNoSpuriousCollisions_OverADecade()
    {
        var w = Full();
        w.AdvanceTo(10 * 365.25);
        Assert.Empty(w.Collisions);
        Assert.All(w.Bodies, b => Assert.True(b.Alive, b.Name));
    }

    // ---- Inside a subsystem ------------------------------------------------------------------

    [Fact]
    public void Moon_DroppedOntoEarth_MergesInsideTheSubsystem()
    {
        var w = Full();
        int earth = w.IndexOf("Earth"), moon = w.IndexOf("Moon");
        double mE = w.Bodies[earth].Mass, mM = w.Bodies[moon].Mass;
        var p0 = TotalMomentum(w);
        // Kill the Moon's planetocentric velocity: it free-falls onto Earth in ~5 days.
        w.Bodies[moon].Vel = Vector3d.Zero;

        Assert.True(w.AdvanceTo(15.0));

        Assert.False(w.Bodies[moon].Alive);
        Assert.Equal(earth, w.Bodies[moon].AbsorbedBy);
        Assert.Equal(earth, w.Bodies[moon].Parent);
        Assert.True(w.Bodies[earth].Alive);
        Assert.Equal(mE + mM, w.Bodies[earth].Mass, 15);
        var ev = Assert.Single(w.Collisions);
        Assert.Equal(("Earth", "Moon"), (ev.SurvivorName, ev.AbsorbedName));
        Assert.InRange(ev.TimeDays, 3.0, 8.0);
        _out.WriteLine($"Moon hit Earth at day {ev.TimeDays:0.00}, {ev.RelativeSpeedAUPerDay * PhysicsWorld.AuKm / 86400:0.00} km/s, impact {ev.ImpactEnergy * PhysicsWorld.EnergyUnitJoules:0.0e0} J");
        // Momentum is conserved through the merge (barycentric frame stays put).
        Assert.True((TotalMomentum(w) - p0).Length < 1e-14, $"momentum changed by {(TotalMomentum(w) - p0).Length}");
        // Earth keeps a sane heliocentric orbit and the Moon's position follows it.
        var el = w.OsculatingElements(earth);
        Assert.True(el.Bound);
        Assert.InRange(el.SemiMajorAxisAU, 0.98, 1.02);
        Assert.Equal(w.HeliocentricPosition(earth), w.HeliocentricPosition(moon));
        Assert.True(double.IsFinite(w.Energy()));
        Assert.True(w.AdvanceTo(400.0));
        Assert.True(Math.Abs(w.EnergyDrift()) < 1e-7, $"drift {w.EnergyDrift()}");
    }

    [Fact]
    public void SiblingSatellites_Collide_HeavierOneSurvives()
    {
        var w = Full();
        int jupiter = w.IndexOf("Jupiter"), io = w.IndexOf("Io"), europa = w.IndexOf("Europa");
        double mIo = w.Bodies[io].Mass, mEu = w.Bodies[europa].Mass;
        // Park Europa 150 km from Io with Io's velocity: the surfaces already overlap.
        w.Bodies[europa].Pos = w.Bodies[io].Pos + new Vector3d(150.0 / PhysicsWorld.AuKm, 0, 0);
        w.Bodies[europa].Vel = w.Bodies[io].Vel;

        w.Step(0.05);

        Assert.False(w.Bodies[europa].Alive);
        Assert.Equal(io, w.Bodies[europa].AbsorbedBy);
        Assert.True(w.Bodies[io].Alive);
        Assert.Equal(mIo + mEu, w.Bodies[io].Mass, 15);
        Assert.Equal(jupiter, w.Bodies[io].Parent);
        Assert.Equal(3, w.Bodies.Count(b => b.Alive && b.Parent == jupiter));
        var el = w.OsculatingElements(io);
        Assert.True(el.Bound);
        Assert.InRange(el.Eccentricity, 0.0, 0.1);
        Assert.InRange(el.SemiMajorAxisAU * PhysicsWorld.AuKm, 400_000, 445_000);
        Assert.True(w.AdvanceTo(w.TimeDays + 30.0));
        Assert.True(double.IsFinite(w.Energy()));
    }

    [Fact]
    public void SatelliteHeavierThanItsHost_EatsIt_AndIsPromoted()
    {
        var w = Full();
        int earth = w.IndexOf("Earth"), moon = w.IndexOf("Moon");
        w.Constants.SetBodyMassScale("Moon", 100.0);   // 3.7e-6 M☉ > Earth's 3.0e-6
        w.RefreshMasses();
        double mE = w.Bodies[earth].Mass, mM = w.Bodies[moon].Mass;
        Assert.True(mM > mE);
        w.Bodies[moon].Vel = Vector3d.Zero;

        Assert.True(w.AdvanceTo(15.0));

        Assert.False(w.Bodies[earth].Alive);
        Assert.Equal(moon, w.Bodies[earth].AbsorbedBy);
        Assert.True(w.Bodies[moon].Alive);
        Assert.Equal(-1, w.Bodies[moon].Parent);
        Assert.Equal(mE + mM, w.Bodies[moon].Mass, 15);
        var el = w.OsculatingElements(moon);
        Assert.True(el.Bound);
        Assert.InRange(el.SemiMajorAxisAU, 0.98, 1.02);
        Assert.True(w.AdvanceTo(200.0));
        Assert.True(double.IsFinite(w.Energy()));
    }

    // ---- Across subsystems -------------------------------------------------------------------

    [Fact]
    public void PlanetWithMoon_AbsorbsAnotherPlanet_AndKeepsItsMoon()
    {
        var w = Full();
        int earth = w.IndexOf("Earth"), mars = w.IndexOf("Mars"), moon = w.IndexOf("Moon");
        double mE = w.Bodies[earth].Mass, mMars = w.Bodies[mars].Mass;
        double moonA0 = w.OsculatingElements(moon).SemiMajorAxisAU;
        double rSum = (w.Bodies[earth].RadiusKm + w.Bodies[mars].RadiusKm) / PhysicsWorld.AuKm;
        // Mars touching Earth's limb, co-moving: the adaptive step shrinks to ~1e-4 d for the
        // overlapping pair, so the merge happens before any spurious impulse builds up.
        w.Bodies[mars].Pos = w.AbsolutePosition(earth) + new Vector3d(0.9 * rSum, 0, 0);
        w.Bodies[mars].Vel = w.AbsoluteVelocity(earth);

        Assert.True(w.AdvanceTo(w.TimeDays + 0.01));

        Assert.False(w.Bodies[mars].Alive);
        Assert.Equal(earth, w.Bodies[mars].AbsorbedBy);
        Assert.Equal(mE + mMars, w.Bodies[earth].Mass, 15);
        Assert.True(w.Bodies[moon].Alive);
        Assert.Equal(earth, w.Bodies[moon].Parent);
        var el = w.OsculatingElements(moon);
        Assert.True(el.Bound);
        // Same Moon velocity around a 10.7 % heavier primary: vis-viva gives a ≈ 0.91 a₀.
        Assert.InRange(el.SemiMajorAxisAU, moonA0 * 0.85, moonA0 * 1.0);
        Assert.InRange(w.OsculatingElements(earth).SemiMajorAxisAU, 0.98, 1.02);
    }

    [Fact]
    public void Planet_AbsorbedByAGiant_HandsItsMoonOver()
    {
        var w = Full();
        int earth = w.IndexOf("Earth"), jupiter = w.IndexOf("Jupiter"), moon = w.IndexOf("Moon");
        double mE = w.Bodies[earth].Mass, mJ = w.Bodies[jupiter].Mass;
        double rSum = (w.Bodies[earth].RadiusKm + w.Bodies[jupiter].RadiusKm) / PhysicsWorld.AuKm;
        // Earth half-buried in Jupiter's limb, co-moving (Earth's Pos is its
        // Earth–Moon barycentre; the 4 700 km offset is far inside the overlap).
        w.Bodies[earth].Pos = w.AbsolutePosition(jupiter) + new Vector3d(0.5 * rSum, 0, 0);
        w.Bodies[earth].Vel = w.AbsoluteVelocity(jupiter);

        Assert.True(w.AdvanceTo(w.TimeDays + 0.01));

        Assert.False(w.Bodies[earth].Alive);
        Assert.Equal(jupiter, w.Bodies[earth].AbsorbedBy);
        Assert.Equal(mJ + mE, w.Bodies[jupiter].Mass, 15);
        Assert.True(w.Bodies[moon].Alive);
        Assert.Equal(jupiter, w.Bodies[moon].Parent);
        Assert.Equal(5, w.Bodies.Count(b => b.Alive && b.Parent == jupiter));
        // The Moon now orbits Jupiter at ~384 000 km — inside Io's orbit but still bound.
        var el = w.OsculatingElements(moon);
        Assert.True(el.Bound);
        Assert.True(double.IsFinite(w.HeliocentricPosition(moon).X));
        Assert.True(w.AdvanceTo(w.TimeDays + 30.0));
        Assert.True(double.IsFinite(w.Energy()));
    }

    // ---- Re-seeding ---------------------------------------------------------------------------

    [Fact]
    public void Reset_RevivesAbsorbedBodies_AndRestoresTopology()
    {
        var w = Full(100.0);
        int earth = w.IndexOf("Earth"), moon = w.IndexOf("Moon");
        var pos = w.Bodies.Select(b => b.Pos).ToArray();
        var vel = w.Bodies.Select(b => b.Vel).ToArray();
        double mE = w.Bodies[earth].Mass, rE = w.Bodies[earth].RadiusKm;
        w.Bodies[moon].Vel = Vector3d.Zero;
        w.AdvanceTo(115.0);
        Assert.False(w.Bodies[moon].Alive);

        w.Reset();

        Assert.Equal(100.0, w.TimeDays);
        Assert.Empty(w.Collisions);
        Assert.All(w.Bodies, b => Assert.True(b.Alive, b.Name));
        Assert.Equal(-1, w.Bodies[moon].AbsorbedBy);
        Assert.Equal(earth, w.Bodies[moon].Parent);
        Assert.Equal(mE, w.Bodies[earth].Mass);
        Assert.Equal(rE, w.Bodies[earth].RadiusKm);
        for (int i = 0; i < w.Bodies.Count; i++)
        {
            Assert.Equal(pos[i], w.Bodies[i].Pos);
            Assert.Equal(vel[i], w.Bodies[i].Vel);
        }
        Assert.Equal(0.0, w.EnergyDrift(), 12);
        // ...and the Moon is back on its orbit.
        var el = w.OsculatingElements(moon);
        Assert.InRange(el.SemiMajorAxisAU * PhysicsWorld.AuKm, 370_000, 400_000);
        w.AdvanceTo(130.0);
        Assert.Empty(w.Collisions);
    }

    [Fact]
    public void Initialize_AfterAMerge_RestoresTopology()
    {
        var w = Full();
        int earth = w.IndexOf("Earth"), moon = w.IndexOf("Moon");
        double mE = w.Bodies[earth].Mass;
        w.Bodies[moon].Vel = Vector3d.Zero;
        w.AdvanceTo(15.0);
        Assert.False(w.Bodies[moon].Alive);

        w.Initialize(5000.0);

        Assert.All(w.Bodies, b => Assert.True(b.Alive, b.Name));
        Assert.Empty(w.Collisions);
        Assert.Equal(mE, w.Bodies[earth].Mass);
        Assert.Equal(earth, w.Bodies[moon].Parent);
        var expected = w.Bodies[moon].Ephemeris(5000.0);
        var actual = w.AbsolutePosition(moon) - w.AbsolutePosition(earth);
        Assert.True((expected - actual).Length < 5e-6);
    }

    // ---- Belt replay contract ----------------------------------------------------------------

    [Fact]
    public void StepRecords_KeepOneLayout_PerAdvance_EvenAcrossAMerge()
    {
        var w = SunAnd("Rock", PhysicsBodyKind.Planet, 3.0e-6, 6371.0, new Vector3d(0.1, 0, 0));
        Assert.True(w.AdvanceTo(10.0));
        Assert.Single(w.Collisions);
        // The merge happened somewhere inside this call: every record still has the
        // layout the call started with, and the victim's slot follows the Sun.
        Assert.Equal(2, w.RecordBodies.Count);
        Assert.Equal(2, w.RecordGM.Count);
        Assert.All(w.Records, r => Assert.Equal(2, r.Positions.Length));
        Assert.All(w.GlobalRecords, r => Assert.Equal(2, r.Positions.Length));
        var last = w.GlobalRecords[^1].Positions;
        Assert.Equal(last[0], last[1]);
        // The next call drops the dead body from the layout.
        Assert.True(w.AdvanceTo(11.0));
        Assert.Single(w.RecordBodies);
        Assert.Equal(0, w.RecordBodies[0]);
        Assert.All(w.Records, r => Assert.Single(r.Positions));
    }
}
