using System.Diagnostics;
using OpenTK.Mathematics;

namespace SolarSystem;

/// <summary>How bodies are moved each frame.</summary>
public enum SimulationMode
{
    /// <summary>Analytic ephemerides (Kepler elements, ELP-2000, Meeus). The historical
    /// default; eclipse bookmarks land to the minute. Physical constants are locked.</summary>
    Ephemeris = 0,
    /// <summary>Everything massive is integrated by one N-body leapfrog; asteroids and
    /// comets ride the same field as test particles. Constants are live.</summary>
    Physics = 1,
    /// <summary>Physics drives the bodies while the ephemeris position of every body is
    /// drawn as a translucent "ghost" linked to the physical body by a dashed line.</summary>
    Compare = 2,
}

/// <summary>
/// User-tunable physical constants. Every acceleration in <see cref="PhysicsWorld"/>
/// (and in the GPU asteroid kernel that replays its step records) goes through
/// these values, so changing one mid-flight immediately bends every orbit —
/// bodies keep their current position and velocity, nothing is re-seeded.
/// </summary>
public sealed class PhysicsConstants
{
    public const double GMin = 0.01, GMax = 100.0;
    public const double SunMassMin = 0.01, SunMassMax = 100.0;
    public const double ExponentMin = 1.5, ExponentMax = 3.0;
    public const double LightSpeedMin = 0.01, LightSpeedMax = 100.0;
    public const double BodyMassMin = 0.01, BodyMassMax = 100.0;

    /// <summary>Multiplier on the gravitational constant (1 = real).</summary>
    public double G { get; set; } = 1.0;
    /// <summary>Multiplier on the Sun's mass (1 = real).</summary>
    public double SunMassScale { get; set; } = 1.0;
    /// <summary>Exponent n in <c>a = G·M / r^n</c>. 2 = inverse square (real).</summary>
    public double GravityExponent { get; set; } = 2.0;
    /// <summary>Multiplier on the speed of light used by the light-time delay (R4).</summary>
    public double SpeedOfLightScale { get; set; } = 1.0;
    /// <summary>Per-body mass multipliers keyed by body name (1 = real). Bodies missing
    /// from the dictionary use 1.</summary>
    public Dictionary<string, double> BodyMassScale { get; } = new(StringComparer.OrdinalIgnoreCase);

    public double GetBodyMassScale(string name)
        => BodyMassScale.TryGetValue(name, out var s) ? s : 1.0;

    public void SetBodyMassScale(string name, double scale)
    {
        scale = Math.Clamp(scale, BodyMassMin, BodyMassMax);
        if (Math.Abs(scale - 1.0) < 1e-12) BodyMassScale.Remove(name);
        else BodyMassScale[name] = scale;
    }

    public bool IsDefault
        => G == 1.0 && SunMassScale == 1.0 && GravityExponent == 2.0 && SpeedOfLightScale == 1.0
           && BodyMassScale.Count == 0;

    /// <summary>Back to the real universe (all multipliers 1, inverse-square law).</summary>
    public void Reset()
    {
        G = 1.0;
        SunMassScale = 1.0;
        GravityExponent = 2.0;
        SpeedOfLightScale = 1.0;
        BodyMassScale.Clear();
    }

    public void ResetMasses()
    {
        SunMassScale = 1.0;
        BodyMassScale.Clear();
    }

    /// <summary>Clamp every value into its legal range (used after loading state.json).</summary>
    public void Sanitize()
    {
        if (!double.IsFinite(G)) G = 1.0;
        if (!double.IsFinite(SunMassScale)) SunMassScale = 1.0;
        if (!double.IsFinite(GravityExponent)) GravityExponent = 2.0;
        if (!double.IsFinite(SpeedOfLightScale)) SpeedOfLightScale = 1.0;
        G = Math.Clamp(G, GMin, GMax);
        SunMassScale = Math.Clamp(SunMassScale, SunMassMin, SunMassMax);
        GravityExponent = Math.Clamp(GravityExponent, ExponentMin, ExponentMax);
        SpeedOfLightScale = Math.Clamp(SpeedOfLightScale, LightSpeedMin, LightSpeedMax);
        foreach (var k in BodyMassScale.Keys.ToArray())
        {
            double v = BodyMassScale[k];
            if (!double.IsFinite(v)) BodyMassScale.Remove(k);
            else BodyMassScale[k] = Math.Clamp(v, BodyMassMin, BodyMassMax);
        }
    }

    /// <summary>On-disk layout for the <c>Physics</c> section of <c>state.json</c>.</summary>
    internal sealed class Dto
    {
        public double G { get; set; } = 1.0;
        public double SunMassScale { get; set; } = 1.0;
        public double GravityExponent { get; set; } = 2.0;
        public double SpeedOfLightScale { get; set; } = 1.0;
        public Dictionary<string, double>? BodyMassScale { get; set; }
    }

    internal Dto ToDto() => new()
    {
        G = G,
        SunMassScale = SunMassScale,
        GravityExponent = GravityExponent,
        SpeedOfLightScale = SpeedOfLightScale,
        BodyMassScale = BodyMassScale.Count == 0 ? null : new Dictionary<string, double>(BodyMassScale),
    };

    internal void LoadDto(Dto d)
    {
        G = d.G;
        SunMassScale = d.SunMassScale;
        GravityExponent = d.GravityExponent;
        SpeedOfLightScale = d.SpeedOfLightScale;
        BodyMassScale.Clear();
        if (d.BodyMassScale != null)
            foreach (var (k, v) in d.BodyMassScale) BodyMassScale[k] = v;
        Sanitize();
    }
}

public enum PhysicsBodyKind { Star, Planet, Dwarf, Satellite, TestParticle }

/// <summary>One integrated body. Heliocentric-level bodies store barycentric
/// state (AU, AU/day); satellites store their offset relative to the host
/// planet (planetocentric AU, AU/day). A planet that hosts satellites keeps the
/// state of its <em>subsystem barycentre</em> in <see cref="Pos"/>/<see cref="Vel"/> —
/// use <see cref="PhysicsWorld.AbsolutePosition"/> for the planet itself.</summary>
public sealed class PhysicsBody
{
    public required string Name { get; init; }
    public required PhysicsBodyKind Kind { get; init; }
    /// <summary>Mass in solar masses before user scaling. Starts at the real value
    /// (<see cref="OriginalBaseMass"/>) and grows when the body absorbs another one in a
    /// collision; <see cref="PhysicsWorld.Initialize"/> / <see cref="PhysicsWorld.Reset"/>
    /// put it back.</summary>
    public double BaseMass { get; internal set; }
    /// <summary>Real mass in solar masses as built (never changes).</summary>
    public double OriginalBaseMass { get; internal set; }
    /// <summary>Effective mass after <see cref="PhysicsConstants"/> scaling (refreshed each step).</summary>
    public double Mass { get; internal set; }
    public bool IsMassive => Kind != PhysicsBodyKind.TestParticle;
    /// <summary>Index of the host planet for satellites, -1 for heliocentric-level bodies.
    /// Collisions can re-parent a satellite (its host was absorbed) or promote it; a body
    /// that has been absorbed points at its absorber so its position keeps tracking it.</summary>
    public int Parent { get; internal set; } = -1;
    /// <summary>Parent as built (see <see cref="Parent"/>).</summary>
    public int OriginalParent { get; internal set; } = -1;
    /// <summary>Physical radius (km) used for contact detection. Grows on a merge as if
    /// the two bodies had the same density: R^3 = R1^3 + R2^3.</summary>
    public double RadiusKm { get; internal set; }
    /// <summary>Radius as built.</summary>
    public double OriginalRadiusKm { get; internal set; }
    /// <summary>False once the body has been absorbed in a collision. Dead bodies are
    /// skipped by the integrator, the energy sums and the renderer; their position
    /// follows the body that absorbed them (see <see cref="AbsorbedBy"/>).</summary>
    public bool Alive { get; internal set; } = true;
    /// <summary>Index of the body that absorbed this one, -1 while alive.</summary>
    public int AbsorbedBy { get; internal set; } = -1;
    /// <summary>Sim time (days) of the collision that removed this body.</summary>
    public double AbsorbedAtDays { get; internal set; }
    /// <summary>Render object this body drives (null for the Sun).</summary>
    public Planet? Source { get; init; }
    /// <summary>Hard cap on the local sub-step for satellites (days).</summary>
    public double MaxLocalStepDays { get; init; } = 0.05;
    /// <summary>Real orbit radius around the host (km), satellites only — used by the
    /// renderer to scale the artistic orbit in compressed mode.</summary>
    public double OrbitRadiusKm { get; init; }

    public Vector3d Pos;
    public Vector3d Vel;
    public Vector3d Acc;

    /// <summary>Ephemeris position at a given sim time: heliocentric AU for
    /// heliocentric-level bodies, planetocentric AU for satellites (world-frame
    /// orientation, i.e. the same (x, z, -y) swap <see cref="OrbitalMechanics"/> uses).</summary>
    public required Func<double, Vector3d> Ephemeris { get; init; }
    /// <summary>Optional analytic velocity (AU/day) matching <see cref="Ephemeris"/>. When
    /// null the seed velocity comes from a 4th-order finite-difference stencil.</summary>
    public Func<double, Vector3d>? EphemerisVelocity { get; init; }
    /// <summary>Satellites only: when &gt; 0, the seed state is least-squares fitted to the
    /// ephemeris over ±this many days (see <see cref="PhysicsWorld.FitSatelliteSeed"/>) so
    /// short-period terms missing from a truncated theory don't bias the mean motion.</summary>
    public double SeedFitWindowDays { get; init; }
}

/// <summary>
/// Single N-body integrator for the whole scene: the Sun (free, barycentric
/// frame), 8 planets, 5 dwarfs, the Moon, the Galileans and Titan as massive
/// bodies; comets as test particles. Units are AU / days / solar masses with
/// <c>GM☉ = k² ≈ 2.959e-4 AU³/d²</c>.
///
/// Integration is a symplectic kick-drift-kick leapfrog with a <b>hierarchical
/// step</b>: one global step (≤ 0.5 d, adaptive on the closest r/v in the
/// system) advances every heliocentric-level body, then each "planet +
/// satellites" subsystem is advanced over the same interval with its own
/// smaller sub-steps (Moon ≤ 0.05 d, Galileans ≤ 0.01 d, adaptive) in the
/// planetocentric frame, feeling the host, its sibling moons and the tidal
/// field of the Sun and the other planets (interpolated across the global
/// step). The planet's own global-level state is the barycentre of its
/// subsystem, so momentum stays consistent.
///
/// Every force goes through <see cref="Constants"/> — G multiplier, Sun mass,
/// per-body mass scale and the exponent of the force law — so the user can
/// bend the universe live. Changing a constant never re-seeds anything.
///
/// Bodies whose surfaces touch merge (see <see cref="CollisionsEnabled"/>): every
/// pair is swept along its last step, satellites per sub-step in their host's frame,
/// and the survivor carries the combined mass, momentum and volume.
/// </summary>
public sealed class PhysicsWorld
{
    /// <summary>GM☉ in AU³/day² (Gaussian gravitational constant squared).</summary>
    public const double GM_SUN = 2.959122082855911e-4;
    /// <summary>1 AU in km.</summary>
    public const double AuKm = 1.495978707e8;
    /// <summary>Speed of light in AU/day.</summary>
    public const double LightAuPerDay = 173.1446326742403;
    /// <summary>Yoshida (1990) 4th-order composition coefficients: each step is three
    /// kick-drift-kick leapfrogs of length w1·h, w0·h, w1·h (w0 &lt; 0). Symplectic and
    /// time-reversible like plain leapfrog, but the spurious perihelion drift of the
    /// inner planets falls from O(h²) to O(h⁴), so 0.2-day global steps keep Mercury's
    /// numerical precession far below the real planetary perturbations.</summary>
    public const double YoshidaW1 = 1.3512071919596578;
    public const double YoshidaW0 = -1.7024143839193153;

    /// <summary>Softening floor: pair separations below this (in AU, ~1500 km) are
    /// clamped so a body flung into another can't produce NaNs. With collisions on,
    /// every real pair touches (and merges) well before it gets this close.</summary>
    public const double MinSeparationAU = 1e-5;
    /// <summary>Real solar radius (km), the Sun's contact radius.</summary>
    public const double SunRadiusKm = 695700.0;

    /// <summary>Collisions: when two bodies' surfaces touch (swept along the last step so
    /// a fast pass can't tunnel through) they merge — perfectly inelastic: the heavier
    /// one survives at the pair's centre of mass with the combined momentum, its mass and
    /// volume are summed, the other is removed and re-parents any satellites it had. Test
    /// particles (comets) hitting a massive body simply vanish. Off = bodies pass through
    /// each other (softened at <see cref="MinSeparationAU"/>), the pre-collision behaviour.</summary>
    public bool CollisionsEnabled { get; set; } = true;

    /// <summary>One merge, oldest first. <c>ImpactEnergy</c> is the kinetic energy of the
    /// relative motion, ½·μ·v² with μ the reduced mass (M☉·AU²/d², see
    /// <see cref="EnergyUnitJoules"/>) — what the merge turns into heat; <c>Position</c> is
    /// barycentric.</summary>
    public readonly record struct CollisionEvent(double TimeDays, int Survivor, int Absorbed,
        string SurvivorName, string AbsorbedName, double AbsorbedMass, double SurvivorMassAfter,
        double RelativeSpeedAUPerDay, double ImpactEnergy, Vector3d Position);
    /// <summary>Joules per M☉·AU²/d² (1.989e30 kg · (1.496e11 m)² / (86400 s)²).</summary>
    public const double EnergyUnitJoules = 5.963e42;
    public IReadOnlyList<CollisionEvent> Collisions => _collisions;

    public PhysicsConstants Constants { get; }
    public IReadOnlyList<PhysicsBody> Bodies => _bodies;
    /// <summary>Sim time (days since J2000) the state corresponds to.</summary>
    public double TimeDays { get; private set; }
    /// <summary>Sim time <see cref="Initialize"/> was last called with.</summary>
    public double StartDays { get; private set; }
    public bool Ready { get; private set; }

    /// <summary>Largest global step (days). 0.5 d keeps Mercury at ~180 steps/orbit.</summary>
    public double MaxGlobalStepDays { get; set; } = 0.5;
    /// <summary>Global step is also capped at this fraction of the smallest
    /// (distance / relative speed) between any body and the nearest massive body —
    /// 2π/500 means ≥ 500 steps per orbit for the fastest body (Mercury: ~0.2 d), which
    /// keeps its numerical perihelion drift near 5″/century.</summary>
    public double AdaptiveFraction { get; set; } = 2.0 * Math.PI / 500.0;
    /// <summary>Same rule for satellite sub-steps (≥ 200 steps per orbit), on top of the
    /// per-body caps (Moon 0.05 d, Galileans 0.01 d).</summary>
    public double SatelliteAdaptiveFraction { get; set; } = 2.0 * Math.PI / 200.0;
    /// <summary>Absolute floor for any step (days) so a plunging body can't stall the app.</summary>
    public double MinStepDays { get; set; } = 1e-4;

    // ---- Diagnostics for the HUD ---------------------------------------------------
    public int LastGlobalSteps { get; private set; }
    public int LastSatelliteSteps { get; private set; }
    public double LastGlobalStepDays { get; private set; }
    public double InitialEnergy { get; private set; }
    public Vector3d InitialAngularMomentum { get; private set; }

    /// <summary>Barycentric positions of every massive heliocentric-level body at the
    /// end of each global step performed by the last <see cref="AdvanceTo"/> call
    /// (plus the state at its start as element 0). Replayed by the asteroid belt so
    /// test particles see exactly the field the planets felt.</summary>
    public IReadOnlyList<StepRecord> Records => _records;
    /// <summary>Same as <see cref="Records"/> but only at global-step boundaries (one
    /// entry per <see cref="Step"/>, plus the start), i.e. monotone in time — the belt
    /// merges these into ≤ 1-day leapfrog steps of its own.</summary>
    public IReadOnlyList<StepRecord> GlobalRecords => _globalRecords;
    public readonly record struct StepRecord(double StepDays, Vector3d[] Positions);
    /// <summary>Indices (into <see cref="Bodies"/>) of the bodies listed in each
    /// <see cref="StepRecord"/>, in order. Frozen for the duration of one
    /// <see cref="AdvanceTo"/> call: when a collision removes a body mid-call its slot
    /// keeps reporting the absorber's position (with the victim's G*M), so the belt's
    /// replay stays consistent; the list is refreshed at the next call.</summary>
    public IReadOnlyList<int> RecordBodies => _recordBodies;
    /// <summary>Effective GM (already multiplied by G / mass scales) for each entry of
    /// <see cref="RecordBodies"/>, refreshed at the start of every advance.</summary>
    public IReadOnlyList<double> RecordGM => _recordGM;

    private readonly List<PhysicsBody> _bodies = new();
    private readonly List<int> _helio = new();          // Parent == -1 (massive + test)
    private readonly List<int> _massiveHelio = new();   // Parent == -1 && massive
    private readonly List<Subsystem> _subsystems = new();
    private readonly List<StepRecord> _records = new();
    private readonly List<StepRecord> _globalRecords = new();
    private readonly List<double> _recordGM = new();
    private readonly List<int> _recordBodies = new();
    private readonly List<CollisionEvent> _collisions = new();
    private readonly List<(int A, int B)> _pendingHits = new();
    private Vector3d[] _absStart = Array.Empty<Vector3d>();
    private Vector3d[] _helioPosStart = Array.Empty<Vector3d>();
    private Vector3d[] _snapPos = Array.Empty<Vector3d>();
    private Vector3d[] _snapVel = Array.Empty<Vector3d>();
    private double _minRoverV = double.MaxValue;
    private readonly Stopwatch _clock = new();

    private sealed class Subsystem
    {
        public int Parent;
        public int[] Sats = Array.Empty<int>();
        public double TotalMass;      // parent + satellites (effective)
        public double MaxLocalStep;
        public Vector3d[] Ext0 = Array.Empty<Vector3d>(); // external body positions at step start
        public Vector3d[] Ext1 = Array.Empty<Vector3d>(); // ... and at step end
        public int[] ExtIdx = Array.Empty<int>();         // indices into _bodies
        public int HelioSlot;                             // index of Parent inside _helio
    }

    public PhysicsWorld(PhysicsConstants? constants = null)
    {
        Constants = constants ?? new PhysicsConstants();
    }

    // ---- Building ----------------------------------------------------------------------

    /// <summary>Real masses in solar masses, keyed by name. Planet values are the
    /// JPL system masses; satellites are listed separately so the Earth–Moon and
    /// Jupiter systems get their moons as distinct bodies.</summary>
    public static readonly Dictionary<string, double> Masses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sun"]      = 1.0,
        ["Mercury"]  = 1.6601367952719e-7,
        ["Venus"]    = 2.4478383396645e-6,
        ["Earth"]    = 3.0034896149157e-6,
        ["Mars"]     = 3.2271514350444e-7,
        ["Jupiter"]  = 9.5479194122589e-4,
        ["Saturn"]   = 2.8588598066345e-4,
        ["Uranus"]   = 4.3662440433515e-5,
        ["Neptune"]  = 5.1513890244706e-5,
        ["Ceres"]    = 4.72e-10,
        ["Pluto"]    = 6.55e-9,
        ["Haumea"]   = 2.01e-9,
        ["Makemake"] = 1.56e-9,
        ["Eris"]     = 8.35e-9,
        ["Moon"]     = 3.694e-8,
        ["Io"]       = 4.49e-8,
        ["Europa"]   = 2.41e-8,
        ["Ganymede"] = 7.45e-8,
        ["Callisto"] = 5.41e-8,
        ["Titan"]    = 6.76e-8,
    };

    public static double MassOf(string name) => Masses.TryGetValue(name, out var m) ? m : 0.0;

    /// <summary>Earth's Moon orbital data as used by the ephemeris path (real radius only —
    /// the ELP-2000 theory supplies the actual position).</summary>
    public const double MoonOrbitRadiusKm = 384400.0;

    /// <summary>The Galileans + Titan with the same orbital numbers <c>SolarSystemWindow.OnLoad</c>
    /// uses to build its render moons (radius km, period d, inclination °, phase °). Used by
    /// the tests, and as the fallback body table when a caller has no render moons.</summary>
    public static Moon[] DefaultSatellites() =>
    [
        new Moon(new Planet { Name = "Io",       RealRadiusKm = 1821.6, OrbitalPeriodYears = 1.769138 / 365.25, SemiMajorAxisAU = 421800.0 / AuKm },  4, 421800.0,  5.5f,  1.769138,  0.05f,    0),
        new Moon(new Planet { Name = "Europa",   RealRadiusKm = 1560.8, OrbitalPeriodYears = 3.551181 / 365.25, SemiMajorAxisAU = 671100.0 / AuKm },  4, 671100.0,  7.0f,  3.551181,  0.47f,    90),
        new Moon(new Planet { Name = "Ganymede", RealRadiusKm = 2634.1, OrbitalPeriodYears = 7.154553 / 365.25, SemiMajorAxisAU = 1070400.0 / AuKm }, 4, 1070400.0, 9.0f,  7.154553,  0.20f,    180),
        new Moon(new Planet { Name = "Callisto", RealRadiusKm = 2410.3, OrbitalPeriodYears = 16.689017 / 365.25, SemiMajorAxisAU = 1882700.0 / AuKm }, 4, 1882700.0, 12.0f, 16.689017, 0.20f,    270),
        new Moon(new Planet { Name = "Titan",    RealRadiusKm = 2574.7, OrbitalPeriodYears = 15.945421 / 365.25, SemiMajorAxisAU = 1221870.0 / AuKm }, 5, 1221870.0, 9.0f,  15.945421, 0.34875f, 0),
    ];

    /// <summary>Geocentric offset of the Moon in AU (world-frame orientation) from the
    /// truncated ELP-2000 theory — identical to the vector the ephemeris renderer uses.</summary>
    public static Vector3d MoonOffsetAU(double simDays)
    {
        var lunar = LunarEphemeris.Compute(simDays);
        double lonRad = lunar.LongitudeDeg * OrbitalMechanics.DegToRad;
        double latRad = lunar.LatitudeDeg * OrbitalMechanics.DegToRad;
        double cosB = Math.Cos(latRad);
        double mx = lunar.DistanceKm * cosB * Math.Cos(lonRad);
        double my = lunar.DistanceKm * cosB * Math.Sin(lonRad);
        double mz = lunar.DistanceKm * Math.Sin(latRad);
        return new Vector3d(mx, mz, -my) / AuKm;
    }

    /// <summary>Planetocentric offset (AU, world frame) of a circular-orbit satellite at
    /// <paramref name="angleRad"/> — the same (cos, -sin, inclination) construction the
    /// ephemeris renderer applies to the Galileans and Titan.</summary>
    public static Vector3d CircularOffsetAU(double radiusKm, double angleRad, double inclDeg)
    {
        double r = radiusKm / AuKm;
        double cx = Math.Cos(angleRad) * r;
        double cz = -Math.Sin(angleRad) * r;
        double incl = inclDeg * OrbitalMechanics.DegToRad;
        double cy = cz * Math.Sin(incl);
        cz *= Math.Cos(incl);
        return new Vector3d(cx, cy, cz);
    }

    /// <summary>Velocity (AU/day, world frame) of a circular satellite at <paramref name="angleRad"/>
    /// with sidereal period <paramref name="periodDays"/> — the time derivative of
    /// <see cref="CircularOffsetAU"/> for a uniformly increasing angle.</summary>
    public static Vector3d CircularVelocityAU(double radiusKm, double angleRad, double inclDeg, double periodDays)
    {
        double r = radiusKm / AuKm;
        double rate = 2.0 * Math.PI / periodDays;
        double dcx = -Math.Sin(angleRad) * r * rate;
        double dcz = -Math.Cos(angleRad) * r * rate;
        double incl = inclDeg * OrbitalMechanics.DegToRad;
        double dcy = dcz * Math.Sin(incl);
        dcz *= Math.Cos(incl);
        return new Vector3d(dcx, dcy, dcz);
    }

    /// <summary>4th-order central-difference derivative (five-point stencil) of an
    /// ephemeris. Truncation error ∝ (n·h)⁴ so even Mercury (n ≈ 0.07 rad/d) with
    /// h = 0.1 d is exact to ~1e-10 — a plain ±0.5 d difference would under-estimate
    /// its speed by 2e-4 and drift ~1°/year from the ephemeris.</summary>
    public static Vector3d StencilVelocity(Func<double, Vector3d> f, double t, double h)
        => (-f(t + 2 * h) + f(t + h) * 8.0 - f(t - h) * 8.0 + f(t - 2 * h)) / (12.0 * h);

    /// <summary>Angle (radians) the ephemeris renderer uses for a satellite: Meeus mean
    /// longitude for the Galileans, uniform circular motion for everything else.</summary>
    public static double SatelliteAngleRad(Moon m, double simDays)
    {
        switch (m.Body.Name)
        {
            case "Io":       return GalileanEphemeris.MeanLongitudes(simDays).Io       * OrbitalMechanics.DegToRad;
            case "Europa":   return GalileanEphemeris.MeanLongitudes(simDays).Europa   * OrbitalMechanics.DegToRad;
            case "Ganymede": return GalileanEphemeris.MeanLongitudes(simDays).Ganymede * OrbitalMechanics.DegToRad;
            case "Callisto": return GalileanEphemeris.MeanLongitudes(simDays).Callisto * OrbitalMechanics.DegToRad;
            default:
                return (simDays / m.OrbitalPeriodDays) * (2.0 * Math.PI) + m.PhaseDeg * OrbitalMechanics.DegToRad;
        }
    }

    /// <summary>Build the standard body set. <paramref name="planets"/> holds the eight
    /// majors followed by the dwarfs (<paramref name="dwarfStart"/> marks the split),
    /// <paramref name="earthMoon"/> is the render body of the Moon (may be null),
    /// <paramref name="satellites"/> the Galileans / Titan, <paramref name="comets"/> the
    /// comet nuclei (test particles). Call <see cref="Initialize"/> afterwards.</summary>
    public static PhysicsWorld Create(Planet[] planets, int dwarfStart, Planet? earthMoon,
                                      Moon[] satellites, Planet[] comets,
                                      PhysicsConstants? constants = null)
    {
        var w = new PhysicsWorld(constants);
        w.AddBody(new PhysicsBody
        {
            Name = "Sun", Kind = PhysicsBodyKind.Star, BaseMass = 1.0, RadiusKm = SunRadiusKm,
            Ephemeris = _ => Vector3d.Zero,
        });
        var planetIndex = new Dictionary<int, int>(); // planets[] index -> body index
        for (int i = 0; i < planets.Length; i++)
        {
            var p = planets[i];
            int bi = w.AddBody(new PhysicsBody
            {
                Name = p.Name,
                Kind = i < dwarfStart ? PhysicsBodyKind.Planet : PhysicsBodyKind.Dwarf,
                BaseMass = MassOf(p.Name), RadiusKm = p.RealRadiusKm,
                Source = p,
                Ephemeris = t => OrbitalMechanics.HeliocentricPosition(p, t),
            });
            planetIndex[i] = bi;
        }
        if (earthMoon != null && planetIndex.TryGetValue(2, out int earthBody))
        {
            w.AddBody(new PhysicsBody
            {
                Name = earthMoon.Name, Kind = PhysicsBodyKind.Satellite,
                BaseMass = MassOf("Moon"), Parent = earthBody, Source = earthMoon,
                RadiusKm = earthMoon.RealRadiusKm,
                MaxLocalStepDays = 0.05, OrbitRadiusKm = MoonOrbitRadiusKm,
                Ephemeris = MoonOffsetAU,
                // The truncated ELP series (~26 terms) leaves ~10-30″ of short-period noise;
                // a finite-difference velocity would inherit it as a ~3e-4 mean-motion bias
                // (tens of degrees per decade). Fitting the seed over two anomalistic months
                // averages that out.
                SeedFitWindowDays = 30.0,
            });
        }
        foreach (var m in satellites)
        {
            if (!planetIndex.TryGetValue(m.HostPlanetIndex, out int host)) continue;
            var mm = m;
            bool galilean = mm.Body.Name is "Io" or "Europa" or "Ganymede" or "Callisto";
            w.AddBody(new PhysicsBody
            {
                Name = mm.Body.Name, Kind = PhysicsBodyKind.Satellite,
                BaseMass = MassOf(mm.Body.Name), Parent = host, Source = mm.Body,
                RadiusKm = mm.Body.RealRadiusKm,
                MaxLocalStepDays = galilean ? 0.01 : 0.05,
                OrbitRadiusKm = mm.RealOrbitRadiusKm,
                Ephemeris = t => CircularOffsetAU(mm.RealOrbitRadiusKm, SatelliteAngleRad(mm, t), mm.OrbitInclinationDeg),
                // The Meeus u-angles are measured from the Jupiter→Earth line, so their rate
                // is not a sidereal mean motion; seed the physical circular velocity instead.
                EphemerisVelocity = t => CircularVelocityAU(mm.RealOrbitRadiusKm, SatelliteAngleRad(mm, t), mm.OrbitInclinationDeg, mm.OrbitalPeriodDays),
            });
        }
        foreach (var c in comets)
        {
            var cc = c;
            w.AddBody(new PhysicsBody
            {
                Name = cc.Name, Kind = PhysicsBodyKind.TestParticle, BaseMass = 0.0, Source = cc,
                RadiusKm = cc.RealRadiusKm,
                Ephemeris = t => OrbitalMechanics.HeliocentricPosition(cc, t),
            });
        }
        w.Build();
        return w;
    }

    public int AddBody(PhysicsBody b)
    {
        if (Ready) throw new InvalidOperationException("Cannot add bodies after Initialize()");
        b.OriginalBaseMass = b.BaseMass;
        b.OriginalParent = b.Parent;
        b.OriginalRadiusKm = b.RadiusKm;
        _bodies.Add(b);
        return _bodies.Count - 1;
    }

    /// <summary>Put every body back to the topology it was built with: original mass,
    /// radius and parent, all alive, collision log cleared. Called by
    /// <see cref="Initialize"/> and <see cref="Reset"/> so a merged system can be re-seeded.</summary>
    private void RestoreTopology()
    {
        foreach (var b in _bodies)
        {
            b.BaseMass = b.OriginalBaseMass;
            b.Parent = b.OriginalParent;
            b.RadiusKm = b.OriginalRadiusKm;
            b.Alive = true;
            b.AbsorbedBy = -1;
            b.AbsorbedAtDays = 0.0;
        }
        _collisions.Clear();
        _pendingHits.Clear();
        Build();
        _recordBodies.Clear();
        _recordBodies.AddRange(_massiveHelio);
    }

    public int IndexOf(string name)
    {
        for (int i = 0; i < _bodies.Count; i++)
            if (string.Equals(_bodies[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    public PhysicsBody? Find(string name)
    {
        int i = IndexOf(name);
        return i < 0 ? null : _bodies[i];
    }

    private void Build()
    {
        _helio.Clear();
        _massiveHelio.Clear();
        _subsystems.Clear();
        var satsByParent = new Dictionary<int, List<int>>();
        for (int i = 0; i < _bodies.Count; i++)
        {
            var b = _bodies[i];
            if (!b.Alive) continue;
            if (b.Parent < 0)
            {
                _helio.Add(i);
                if (b.IsMassive) _massiveHelio.Add(i);
            }
            else
            {
                if (!satsByParent.TryGetValue(b.Parent, out var list)) satsByParent[b.Parent] = list = new List<int>();
                list.Add(i);
            }
        }
        foreach (var (parent, sats) in satsByParent)
        {
            var sub = new Subsystem { Parent = parent, Sats = sats.ToArray(), HelioSlot = _helio.IndexOf(parent) };
            double cap = double.MaxValue;
            foreach (var s in sub.Sats) cap = Math.Min(cap, _bodies[s].MaxLocalStepDays);
            sub.MaxLocalStep = cap;
            var ext = new List<int>();
            foreach (var h in _massiveHelio) if (h != parent) ext.Add(h);
            sub.ExtIdx = ext.ToArray();
            sub.Ext0 = new Vector3d[ext.Count];
            sub.Ext1 = new Vector3d[ext.Count];
            _subsystems.Add(sub);
        }
        _helioPosStart = new Vector3d[_helio.Count];
        int maxSats = 0;
        foreach (var sub in _subsystems) maxSats = Math.Max(maxSats, sub.Sats.Length);
        if (_satPrev.Length < maxSats) _satPrev = new Vector3d[maxSats];
        if (_absStart.Length != _bodies.Count) _absStart = new Vector3d[_bodies.Count];
        if (_snapPos.Length != _bodies.Count) { _snapPos = new Vector3d[_bodies.Count]; _snapVel = new Vector3d[_bodies.Count]; }
    }

    // ---- Seeding ----------------------------------------------------------------------

    /// <summary>Seed every body from its ephemeris at <paramref name="simDays"/>:
    /// position straight from the analytic model, velocity by central difference
    /// (±0.5 d for heliocentric bodies, ±0.01 d for satellites). The Sun is placed
    /// so the whole system's barycentre sits at the origin with zero net momentum.</summary>
    public void Initialize(double simDays)
    {
        // A previous run may have merged bodies: rebuild the original hierarchy first.
        RestoreTopology();
        RefreshMasses();

        // Heliocentric-level bodies: heliocentric state first, then shift to barycentric.
        foreach (int i in _helio)
        {
            var b = _bodies[i];
            if (b.Kind == PhysicsBodyKind.Star) { b.Pos = Vector3d.Zero; b.Vel = Vector3d.Zero; continue; }
            b.Pos = b.Ephemeris(simDays);
            b.Vel = b.EphemerisVelocity?.Invoke(simDays) ?? StencilVelocity(b.Ephemeris, simDays, 0.1);
        }
        // Satellites: planetocentric state.
        foreach (var sub in _subsystems)
        {
            foreach (int s in sub.Sats)
            {
                var b = _bodies[s];
                if (b.SeedFitWindowDays > 0)
                {
                    (b.Pos, b.Vel) = FitSatelliteSeed(b, _bodies[sub.Parent], simDays, b.SeedFitWindowDays);
                }
                else
                {
                    b.Pos = b.Ephemeris(simDays);
                    b.Vel = b.EphemerisVelocity?.Invoke(simDays) ?? StencilVelocity(b.Ephemeris, simDays, 0.01);
                }
            }
            // The planet's global-level state is the subsystem barycentre.
            var parent = _bodies[sub.Parent];
            Vector3d dp = Vector3d.Zero, dv = Vector3d.Zero;
            foreach (int s in sub.Sats)
            {
                dp += _bodies[s].Pos * _bodies[s].Mass;
                dv += _bodies[s].Vel * _bodies[s].Mass;
            }
            parent.Pos += dp / sub.TotalMass;
            parent.Vel += dv / sub.TotalMass;
        }
        // Shift to the barycentric frame: Σ m r = 0, Σ m v = 0 over massive helio bodies.
        Vector3d cm = Vector3d.Zero, cv = Vector3d.Zero;
        double mt = 0.0;
        foreach (int i in _massiveHelio)
        {
            var b = _bodies[i];
            double m = SubsystemMass(i);
            cm += b.Pos * m; cv += b.Vel * m; mt += m;
        }
        if (mt > 0) { cm /= mt; cv /= mt; }
        foreach (int i in _helio)
        {
            _bodies[i].Pos -= cm;
            _bodies[i].Vel -= cv;
        }

        TimeDays = simDays;
        StartDays = simDays;
        Ready = true;
        PrimeAccelerations();
        for (int i = 0; i < _bodies.Count; i++) { _snapPos[i] = _bodies[i].Pos; _snapVel[i] = _bodies[i].Vel; }
        InitialEnergy = Energy();
        InitialAngularMomentum = AngularMomentum();
        LastGlobalSteps = LastSatelliteSteps = 0;
        _records.Clear();
        _globalRecords.Clear();
    }

    /// <summary>Return every body to the state captured by the last <see cref="Initialize"/>
    /// (and the clock to <see cref="StartDays"/>). Constants are left alone.</summary>
    public void Reset()
    {
        if (!Ready) return;
        RestoreTopology();
        for (int i = 0; i < _bodies.Count; i++) { _bodies[i].Pos = _snapPos[i]; _bodies[i].Vel = _snapVel[i]; }
        TimeDays = StartDays;
        RefreshMasses();
        PrimeAccelerations();
        // A merge rebases the drift references; the restored state is the reference again.
        InitialEnergy = Energy();
        InitialAngularMomentum = AngularMomentum();
        LastGlobalSteps = LastSatelliteSteps = 0;
        _records.Clear();
        _globalRecords.Clear();
    }

    /// <summary>Recompute every cached acceleration for the current positions so the
    /// next <see cref="Step"/> starts from a consistent state.</summary>
    private void PrimeAccelerations()
    {
        for (int k = 0; k < _helio.Count; k++) _helioPosStart[k] = _bodies[_helio[k]].Pos;
        ComputeHelioAccelerations();
        foreach (var sub in _subsystems)
        {
            CaptureExternals(sub, sub.Ext0);
            Array.Copy(sub.Ext0, sub.Ext1, sub.Ext0.Length);
            ComputeSatelliteAccelerations(sub, 0.0);
        }
    }

    /// <summary>Apply the current constants to every body's effective mass.</summary>
    public void RefreshMasses()
    {
        foreach (var b in _bodies)
        {
            double scale = b.Kind == PhysicsBodyKind.Star
                ? Constants.SunMassScale
                : Constants.GetBodyMassScale(b.Name);
            b.Mass = b.BaseMass * scale;
        }
        foreach (var sub in _subsystems)
        {
            double m = _bodies[sub.Parent].Mass;
            foreach (int s in sub.Sats) m += _bodies[s].Mass;
            sub.TotalMass = m;
        }
    }

    private double SubsystemMass(int helioIndex)
    {
        foreach (var sub in _subsystems) if (sub.Parent == helioIndex) return sub.TotalMass;
        return _bodies[helioIndex].Mass;
    }

    /// <summary>Effective G·M for a heliocentric-level body (subsystem total), including
    /// the G multiplier.</summary>
    public double EffectiveGM(int helioIndex) => GM_SUN * Constants.G * SubsystemMass(helioIndex);

    // ---- Advancing ----------------------------------------------------------------------

    /// <summary>Integrate from <see cref="TimeDays"/> toward <paramref name="targetDays"/>
    /// (either direction). Stops early when <paramref name="maxGlobalSteps"/> or
    /// <paramref name="wallBudgetMs"/> is exhausted so a huge seek is spread over
    /// several frames; returns true when the target was reached.</summary>
    public bool AdvanceTo(double targetDays, int maxGlobalSteps = int.MaxValue, double wallBudgetMs = double.PositiveInfinity)
    {
        if (!Ready) Initialize(targetDays);
        RefreshMasses();
        _records.Clear();
        _globalRecords.Clear();
        _recordGM.Clear();
        _recordBodies.Clear();
        _recordBodies.AddRange(_massiveHelio);
        foreach (int i in _recordBodies) _recordGM.Add(EffectiveGM(i));
        var start = SnapshotMassivePositions();
        _records.Add(new StepRecord(0.0, start));
        _globalRecords.Add(new StepRecord(0.0, start));
        LastGlobalSteps = 0;
        LastSatelliteSteps = 0;
        if (targetDays == TimeDays) return true;

        _clock.Restart();
        // Accelerations are cached from the end of the previous step; the constants may
        // have changed since, so recompute once before stepping.
        ComputeHelioAccelerations();
        int steps = 0;
        while (TimeDays != targetDays)
        {
            if (steps >= maxGlobalSteps) return false;
            if (steps > 0 && _clock.Elapsed.TotalMilliseconds > wallBudgetMs) return false;
            double remaining = targetDays - TimeDays;
            double h = ChooseGlobalStep();
            if (Math.Abs(remaining) <= h) { Step(remaining); TimeDays = targetDays; }
            else Step(Math.Sign(remaining) * h);
            steps++;
            LastGlobalSteps = steps;
        }
        return true;
    }

    private Vector3d[] SnapshotMassivePositions()
    {
        var arr = new Vector3d[_recordBodies.Count];
        for (int k = 0; k < arr.Length; k++) arr[k] = RecordPosition(_recordBodies[k]);
        return arr;
    }

    /// <summary>Position reported in a <see cref="StepRecord"/> slot: the subsystem
    /// barycentre for a live heliocentric body; for a body absorbed earlier in this
    /// advance, the position of whatever finally absorbed it (so its G*M keeps acting
    /// from the right place until the record layout is refreshed).</summary>
    private Vector3d RecordPosition(int i)
    {
        int guard = 0;
        while (!_bodies[i].Alive && _bodies[i].AbsorbedBy >= 0 && guard++ < 64) i = _bodies[i].AbsorbedBy;
        var b = _bodies[i];
        return b.Parent < 0 ? b.Pos : AbsolutePosition(i);
    }

    /// <summary>The global step the adaptive rule would pick right now (days) — what the
    /// HUD shows; <see cref="LastGlobalStepDays"/> can be a tiny remainder step.</summary>
    public double CurrentGlobalStepDays => Ready ? ChooseGlobalStep() : MaxGlobalStepDays;

    private double ChooseGlobalStep()
    {
        double h = MaxGlobalStepDays;
        if (_minRoverV < double.MaxValue) h = Math.Min(h, AdaptiveFraction * _minRoverV);
        return Math.Max(h, MinStepDays);
    }

    /// <summary>One global kick-drift-kick step of signed length <paramref name="h"/>
    /// days, followed by the satellite subsystems over the same interval.</summary>
    public void Step(double h)
    {
        if (!Ready) throw new InvalidOperationException("Initialize() first");
        LastGlobalStepDays = Math.Abs(h);
        if (CollisionsEnabled)
        {
            _pendingHits.Clear();
            for (int i = 0; i < _bodies.Count; i++) if (_bodies[i].Alive) _absStart[i] = AbsolutePosition(i);
        }
        for (int k = 0; k < _helio.Count; k++) _helioPosStart[k] = _bodies[_helio[k]].Pos;
        foreach (var sub in _subsystems) CaptureExternals(sub, sub.Ext0);

        // 4th-order Yoshida composition of three leapfrog sub-steps. Each sub-step is
        // recorded so the GPU asteroid kernel can replay exactly the same field.
        LeapfrogHelio(YoshidaW1 * h);
        LeapfrogHelio(YoshidaW0 * h);
        LeapfrogHelio(YoshidaW1 * h);
        _globalRecords.Add(new StepRecord(h, SnapshotMassivePositions()));

        foreach (var sub in _subsystems)
        {
            CaptureExternals(sub, sub.Ext1);
            IntegrateSubsystem(sub, h);
        }
        TimeDays += h;
        if (CollisionsEnabled) ResolveCollisions();
    }

    private void LeapfrogHelio(double h)
    {
        double hh = 0.5 * h;
        foreach (int i in _helio) { var b = _bodies[i]; b.Vel += b.Acc * hh; }
        foreach (int i in _helio) { var b = _bodies[i]; b.Pos += b.Vel * h; }
        ComputeHelioAccelerations();
        foreach (int i in _helio) { var b = _bodies[i]; b.Vel += b.Acc * hh; }
        _records.Add(new StepRecord(h, SnapshotMassivePositions()));
    }

    private void CaptureExternals(Subsystem sub, Vector3d[] into)
    {
        for (int k = 0; k < sub.ExtIdx.Length; k++) into[k] = _bodies[sub.ExtIdx[k]].Pos;
    }

    private void IntegrateSubsystem(Subsystem sub, double h)
    {
        double cap = sub.MaxLocalStep;
        // Adaptive: a satellite that has been pulled close to (or flung far from) its host
        // gets a step proportional to its own r/v.
        double minRv = double.MaxValue;
        foreach (int s in sub.Sats)
        {
            var b = _bodies[s];
            double r = b.Pos.Length, v = b.Vel.Length;
            if (v > 0) minRv = Math.Min(minRv, r / v);
        }
        if (minRv < double.MaxValue) cap = Math.Min(cap, SatelliteAdaptiveFraction * minRv);
        cap = Math.Max(cap, MinStepDays);
        int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(h) / cap));
        double hl = h / n;

        // Acceleration at the start of the interval is cached from the previous call
        // (or from Initialize) — but the constants may have changed, so recompute.
        ComputeSatelliteAccelerations(sub, 0.0);
        double tau = 0.0;
        bool sweep = CollisionsEnabled;
        for (int k = 0; k < n; k++)
        {
            if (sweep) for (int q = 0; q < sub.Sats.Length; q++) _satPrev[q] = _bodies[sub.Sats[q]].Pos;
            // Yoshida 4th-order composition; tau tracks the position inside the global
            // step (it briefly runs backwards during the negative middle sub-step).
            tau = LeapfrogSatellites(sub, YoshidaW1 * hl, tau, h);
            tau = LeapfrogSatellites(sub, YoshidaW0 * hl, tau, h);
            tau = LeapfrogSatellites(sub, YoshidaW1 * hl, tau, h);
            // Satellites move whole orbit-fractions per global step, so host/sibling
            // contact is swept per local sub-step; hits are resolved once the step is done.
            if (sweep) CheckSubsystemHits(sub);
        }
        LastSatelliteSteps += n;
    }

    // ---- Collisions ------------------------------------------------------------------------

    private Vector3d[] _satPrev = Array.Empty<Vector3d>();
    private Vector3d[] _absNow = Array.Empty<Vector3d>();

    /// <summary>True when the segment p0 → p1 passes within <paramref name="r"/> of the origin.</summary>
    private static bool SegmentPassesWithin(Vector3d p0, Vector3d p1, double r)
    {
        var d = p1 - p0;
        double dd = d.LengthSquared;
        double t = dd > 0 ? Math.Clamp(-Vector3d.Dot(p0, d) / dd, 0.0, 1.0) : 0.0;
        var c = p0 + d * t;
        return c.LengthSquared < r * r;
    }

    private void AddPendingHit(int a, int b)
    {
        foreach (var p in _pendingHits) if ((p.A == a && p.B == b) || (p.A == b && p.B == a)) return;
        _pendingHits.Add((a, b));
    }

    /// <summary>Planetocentric sweep of every satellite against its host (origin) and its
    /// siblings over the sub-step that just completed (<see cref="_satPrev"/> → now).</summary>
    private void CheckSubsystemHits(Subsystem sub)
    {
        var host = _bodies[sub.Parent];
        for (int a = 0; a < sub.Sats.Length; a++)
        {
            int ia = sub.Sats[a];
            var ba = _bodies[ia];
            if (SegmentPassesWithin(_satPrev[a], ba.Pos, (host.RadiusKm + ba.RadiusKm) / AuKm))
                AddPendingHit(sub.Parent, ia);
            for (int b = a + 1; b < sub.Sats.Length; b++)
            {
                int ib = sub.Sats[b];
                var bb = _bodies[ib];
                if (SegmentPassesWithin(_satPrev[a] - _satPrev[b], ba.Pos - bb.Pos, (ba.RadiusKm + bb.RadiusKm) / AuKm))
                    AddPendingHit(ia, ib);
            }
        }
    }

    /// <summary>Barycentric sweep over the whole global step for every live pair that is
    /// not host + satellite or two siblings (those are handled per sub-step, where the
    /// chord of their curved motion is short enough to be meaningful). At least one of
    /// the pair must be massive.</summary>
    private bool FindGlobalHit(out int a, out int b)
    {
        a = b = -1;
        int n = _bodies.Count;
        if (_absNow.Length != n) _absNow = new Vector3d[n];
        for (int i = 0; i < n; i++) if (_bodies[i].Alive) _absNow[i] = AbsolutePosition(i);
        double bestRatio = double.MaxValue;
        for (int i = 0; i < n; i++)
        {
            var bi = _bodies[i];
            if (!bi.Alive) continue;
            for (int j = i + 1; j < n; j++)
            {
                var bj = _bodies[j];
                if (!bj.Alive) continue;
                if (!bi.IsMassive && !bj.IsMassive) continue;
                if (bi.Parent == j || bj.Parent == i || (bi.Parent >= 0 && bi.Parent == bj.Parent)) continue;
                double r = (bi.RadiusKm + bj.RadiusKm) / AuKm;
                var p0 = _absStart[i] - _absStart[j];
                var p1 = _absNow[i] - _absNow[j];
                if (!SegmentPassesWithin(p0, p1, r)) continue;
                // Prefer the deepest contact when several pairs touch in one step.
                double ratio = Math.Min(p0.Length, p1.Length) / r;
                if (ratio < bestRatio) { bestRatio = ratio; a = i; b = j; }
            }
        }
        return a >= 0;
    }

    /// <summary>Apply every contact found during the step that just finished — first the
    /// satellite sub-step hits, then repeated barycentric sweeps until nothing touches.</summary>
    private void ResolveCollisions()
    {
        for (int guard = 0; guard < 32; guard++)
        {
            int a = -1, b = -1;
            foreach (var p in _pendingHits)
            {
                if (_bodies[p.A].Alive && _bodies[p.B].Alive) { a = p.A; b = p.B; break; }
            }
            if (a < 0 && !FindGlobalHit(out a, out b)) break;
            Merge(a, b);
        }
        _pendingHits.Clear();
    }

    /// <summary>Perfectly inelastic merge of bodies <paramref name="i"/> and <paramref name="j"/>:
    /// the heavier survives (a massive body always beats a test particle) at the common
    /// centre of mass with the summed momentum, mass and volume; the other is marked dead,
    /// parked on the survivor, and its satellites are handed to the survivor (or to the
    /// survivor's host when the survivor is itself a satellite). A satellite that
    /// swallows its own host is promoted to heliocentric level. The hierarchy is rebuilt
    /// and every relative state re-derived from the absolute one, so nothing else moves.</summary>
    private void Merge(int i, int j)
    {
        var bi = _bodies[i];
        var bj = _bodies[j];
        int s, v;
        if (!bi.IsMassive) (s, v) = (j, i);
        else if (!bj.IsMassive) (s, v) = (i, j);
        else if (bj.Mass > bi.Mass) (s, v) = (j, i);
        else (s, v) = (i, j);
        var bs = _bodies[s];
        var bv = _bodies[v];

        int n = _bodies.Count;
        var absPos = new Vector3d[n];
        var absVel = new Vector3d[n];
        for (int k = 0; k < n; k++)
        {
            if (!_bodies[k].Alive) continue;
            absPos[k] = AbsolutePosition(k);
            absVel[k] = AbsoluteVelocity(k);
        }
        double ms = bs.Mass, mv = bv.Mass, mt = ms + mv;
        double vRel = (absVel[v] - absVel[s]).Length;
        double impact = mt > 0 ? 0.5 * (ms * mv / mt) * vRel * vRel : 0.0;
        Vector3d newPos = absPos[s], newVel = absVel[s];
        if (mt > 0)
        {
            newPos = (absPos[s] * ms + absPos[v] * mv) / mt;
            newVel = (absVel[s] * ms + absVel[v] * mv) / mt;
        }

        if (bv.IsMassive)
        {
            double scale = bs.Kind == PhysicsBodyKind.Star ? Constants.SunMassScale : Constants.GetBodyMassScale(bs.Name);
            if (!(scale > 0)) scale = 1.0;
            bs.BaseMass = mt / scale;
            bs.RadiusKm = Math.Cbrt(bs.RadiusKm * bs.RadiusKm * bs.RadiusKm + bv.RadiusKm * bv.RadiusKm * bv.RadiusKm);
        }
        bv.Alive = false;
        bv.AbsorbedBy = s;
        bv.AbsorbedAtDays = TimeDays;
        bv.Parent = s;
        bv.Pos = Vector3d.Zero;
        bv.Vel = Vector3d.Zero;

        // Re-parenting. A satellite that ate its host becomes a heliocentric body.
        if (bs.Parent == v) bs.Parent = -1;
        int orphanHost = bs.Parent >= 0 ? bs.Parent : s;
        for (int k = 0; k < n; k++)
        {
            var b = _bodies[k];
            if (b.Alive && b.Parent == v) b.Parent = orphanHost;
        }

        Build();
        RefreshMasses();
        absPos[s] = newPos;
        absVel[s] = newVel;
        foreach (int k in _helio) { _bodies[k].Pos = absPos[k]; _bodies[k].Vel = absVel[k]; }
        foreach (var sub in _subsystems)
        {
            var parent = _bodies[sub.Parent];
            Vector3d dp = Vector3d.Zero, dv = Vector3d.Zero;
            foreach (int q in sub.Sats)
            {
                var b = _bodies[q];
                b.Pos = absPos[q] - absPos[sub.Parent];
                b.Vel = absVel[q] - absVel[sub.Parent];
                dp += b.Pos * b.Mass;
                dv += b.Vel * b.Mass;
            }
            parent.Pos += dp / sub.TotalMass;
            parent.Vel += dv / sub.TotalMass;
        }
        _absStart[s] = AbsolutePosition(s);
        PrimeAccelerations();

        _collisions.Add(new CollisionEvent(TimeDays, s, v, bs.Name, bv.Name, mv, mt, vRel, impact, newPos));
        // An inelastic merge legitimately changes E and L: rebase the drift references so
        // the HUD keeps reporting integrator error rather than the collision itself.
        InitialEnergy = Energy();
        InitialAngularMomentum = AngularMomentum();
    }

    /// <summary>One planetocentric KDK sub-step of signed length <paramref name="hl"/>;
    /// returns the new fraction across the global step of length <paramref name="hGlobal"/>.</summary>
    private double LeapfrogSatellites(Subsystem sub, double hl, double tau, double hGlobal)
    {
        double hh = 0.5 * hl;
        foreach (int s in sub.Sats) { var b = _bodies[s]; b.Vel += b.Acc * hh; }
        foreach (int s in sub.Sats) { var b = _bodies[s]; b.Pos += b.Vel * hl; }
        tau += hl / hGlobal;
        ComputeSatelliteAccelerations(sub, tau);
        foreach (int s in sub.Sats) { var b = _bodies[s]; b.Vel += b.Acc * hh; }
        return tau;
    }

    // ---- Forces ---------------------------------------------------------------------------

    /// <summary>Returns 1/|d|^(n+1) for the force law a = GM·d/|d|^(n+1), with the
    /// separation clamped at <see cref="MinSeparationAU"/>.</summary>
    private double InvPow(double d2)
    {
        const double min2 = MinSeparationAU * MinSeparationAU;
        if (d2 < min2) d2 = min2;
        double n = Constants.GravityExponent;
        if (n == 2.0) return 1.0 / (d2 * Math.Sqrt(d2));
        return Math.Pow(d2, -0.5 * (n + 1.0));
    }

    private void ComputeHelioAccelerations()
    {
        double G = GM_SUN * Constants.G;
        double minRv = double.MaxValue;
        foreach (int i in _helio)
        {
            var bi = _bodies[i];
            Vector3d a = Vector3d.Zero;
            foreach (int j in _massiveHelio)
            {
                if (j == i) continue;
                var bj = _bodies[j];
                var d = bj.Pos - bi.Pos;
                double d2 = d.LengthSquared;
                double gm = G * SubsystemMass(j);
                a += d * (gm * InvPow(d2));
                // Orbital timescale 1/n = √(d³/GM_j) of i about j: the shortest one in the
                // system sets the global step (Mercury about the Sun in the real setup).
                if (gm > 0)
                {
                    double rv = Math.Sqrt(d2 * Math.Sqrt(d2) / gm);
                    if (rv < minRv) minRv = rv;
                }
            }
            bi.Acc = a;
        }
        _minRoverV = minRv;
    }

    /// <summary>Planetocentric accelerations of a subsystem's satellites at fraction
    /// <paramref name="tau"/> ∈ [0,1] across the current global step (external bodies
    /// are interpolated linearly between <see cref="Subsystem.Ext0"/> and <see cref="Subsystem.Ext1"/>).</summary>
    private void ComputeSatelliteAccelerations(Subsystem sub, double tau)
    {
        double G = GM_SUN * Constants.G;
        var parent = _bodies[sub.Parent];
        double mParent = parent.Mass;
        // Host position along the step (its barycentre; the ≤ 5000 km offset of the
        // planet from the subsystem barycentre is irrelevant for the tidal field).
        // Ext arrays hold external positions; the host's own interpolated position is
        // reconstructed from the helio start array + current state.
        Vector3d hostPos = HostPositionAt(sub, tau);
        foreach (int s in sub.Sats)
        {
            var bs = _bodies[s];
            var r = bs.Pos;
            double r2 = r.LengthSquared;
            // Two-body term with the host (relative motion => M_host + m_sat).
            Vector3d a = -r * (G * (mParent + bs.Mass) * InvPow(r2));
            // Sibling satellites: direct minus indirect (the host is also pulled by them).
            foreach (int o in sub.Sats)
            {
                if (o == s) continue;
                var bo = _bodies[o];
                var d = bo.Pos - r;
                double gm = G * bo.Mass;
                a += d * (gm * InvPow(d.LengthSquared));
                a -= bo.Pos * (gm * InvPow(bo.Pos.LengthSquared));
            }
            // Tidal field of the Sun and the other planets: direct minus indirect.
            for (int k = 0; k < sub.ExtIdx.Length; k++)
            {
                var ext = sub.Ext0[k] + (sub.Ext1[k] - sub.Ext0[k]) * tau;
                var R = ext - hostPos;                 // external body relative to the host
                var d = R - r;                         // external body relative to the satellite
                double gm = G * SubsystemMass(sub.ExtIdx[k]);
                a += d * (gm * InvPow(d.LengthSquared));
                a -= R * (gm * InvPow(R.LengthSquared));
            }
            bs.Acc = a;
        }
    }

    private Vector3d HostPositionAt(Subsystem sub, double tau)
    {
        var parent = _bodies[sub.Parent];
        int k = sub.HelioSlot;
        if (k < 0) return parent.Pos;
        var start = _helioPosStart[k];
        return start + (parent.Pos - start) * tau;
    }

    // ---- Seed fitting ----------------------------------------------------------------------------

    /// <summary>
    /// Least-squares fit of a satellite's planetocentric seed state (r₀, v₀) so that a
    /// two-body + solar-tide integration (real constants, host from its own ephemeris)
    /// matches <see cref="PhysicsBody.Ephemeris"/> sampled every 0.5 d over
    /// ±<paramref name="halfWindowDays"/>. Gauss–Newton with a finite-difference
    /// Jacobian, 4 iterations; costs a few milliseconds. Used for the Moon, whose
    /// truncated ELP series would otherwise hand the integrator a biased mean motion.
    /// </summary>
    public static (Vector3d Pos, Vector3d Vel) FitSatelliteSeed(PhysicsBody sat, PhysicsBody host,
        double t0, double halfWindowDays)
    {
        const double sampleStep = 0.5;
        const double stepDays = 0.05;
        double mu = GM_SUN * (host.BaseMass + sat.BaseMass);
        double gmSun = GM_SUN;
        var hostEph = host.Ephemeris;   // heliocentric host position (AU)

        var times = new List<double>();
        for (double dt = sampleStep; dt <= halfWindowDays + 1e-9; dt += sampleStep) times.Add(dt);
        int n = times.Count;
        var target = new Vector3d[2 * n];
        for (int k = 0; k < n; k++)
        {
            target[k] = sat.Ephemeris(t0 + times[k]);
            target[n + k] = sat.Ephemeris(t0 - times[k]);
        }

        Vector3d Acc(Vector3d r, double t)
        {
            double r2 = r.LengthSquared;
            var a = -r * (mu / (r2 * Math.Sqrt(r2)));
            var S = -hostEph(t);                       // Sun relative to the host
            var d = S - r;
            double d2 = d.LengthSquared, S2 = S.LengthSquared;
            a += d * (gmSun / (d2 * Math.Sqrt(d2)));
            a -= S * (gmSun / (S2 * Math.Sqrt(S2)));
            return a;
        }

        // Integrate one direction (sign = ±1) with the same Yoshida/KDK scheme the world
        // uses, sampling the position at every sample time.
        void Propagate(double[] p, double sign, Vector3d[] outPos, int offset)
        {
            var r = new Vector3d(p[0], p[1], p[2]);
            var v = new Vector3d(p[3], p[4], p[5]);
            double t = t0;
            var a = Acc(r, t);
            int stepsPerSample = (int)Math.Round(sampleStep / stepDays);
            for (int k = 0; k < n; k++)
            {
                for (int s = 0; s < stepsPerSample; s++)
                {
                    foreach (double c in new[] { YoshidaW1, YoshidaW0, YoshidaW1 })
                    {
                        double h = c * stepDays * sign;
                        v += a * (0.5 * h);
                        r += v * h;
                        t += h;
                        a = Acc(r, t);
                        v += a * (0.5 * h);
                    }
                }
                outPos[offset + k] = r;
            }
        }

        double[] Residuals(double[] p)
        {
            var pos = new Vector3d[2 * n];
            Propagate(p, +1.0, pos, 0);
            Propagate(p, -1.0, pos, n);
            var res = new double[6 * n];
            for (int k = 0; k < 2 * n; k++)
            {
                var d = pos[k] - target[k];
                res[3 * k] = d.X; res[3 * k + 1] = d.Y; res[3 * k + 2] = d.Z;
            }
            return res;
        }

        var r0 = sat.Ephemeris(t0);
        var v0 = sat.EphemerisVelocity?.Invoke(t0) ?? StencilVelocity(sat.Ephemeris, t0, 0.01);
        double[] param = [r0.X, r0.Y, r0.Z, v0.X, v0.Y, v0.Z];
        double[] delta = [1e-8, 1e-8, 1e-8, 1e-9, 1e-9, 1e-9];

        for (int iter = 0; iter < 4; iter++)
        {
            var res = Residuals(param);
            int m = res.Length;
            var J = new double[m, 6];
            for (int j = 0; j < 6; j++)
            {
                var p2 = (double[])param.Clone();
                p2[j] += delta[j];
                var res2 = Residuals(p2);
                for (int i = 0; i < m; i++) J[i, j] = (res2[i] - res[i]) / delta[j];
            }
            // Normal equations: (JᵀJ) dp = -Jᵀ r.
            var A = new double[6, 7];
            for (int a = 0; a < 6; a++)
            {
                for (int b = 0; b < 6; b++)
                {
                    double s = 0; for (int i = 0; i < m; i++) s += J[i, a] * J[i, b];
                    A[a, b] = s;
                }
                double rhs = 0; for (int i = 0; i < m; i++) rhs += J[i, a] * res[i];
                A[a, 6] = -rhs;
            }
            if (!SolveLinear(A, out var dp)) break;
            double stepNorm = 0;
            for (int j = 0; j < 6; j++) { param[j] += dp[j]; stepNorm += dp[j] * dp[j]; }
            if (stepNorm < 1e-30) break;
        }
        return (new Vector3d(param[0], param[1], param[2]), new Vector3d(param[3], param[4], param[5]));
    }

    /// <summary>Gaussian elimination with partial pivoting on a 6×7 augmented matrix.</summary>
    private static bool SolveLinear(double[,] A, out double[] x)
    {
        const int N = 6;
        x = new double[N];
        for (int col = 0; col < N; col++)
        {
            int piv = col;
            for (int r = col + 1; r < N; r++) if (Math.Abs(A[r, col]) > Math.Abs(A[piv, col])) piv = r;
            if (Math.Abs(A[piv, col]) < 1e-300) return false;
            if (piv != col)
                for (int c = 0; c <= N; c++) (A[col, c], A[piv, c]) = (A[piv, c], A[col, c]);
            for (int r = col + 1; r < N; r++)
            {
                double f = A[r, col] / A[col, col];
                for (int c = col; c <= N; c++) A[r, c] -= f * A[col, c];
            }
        }
        for (int r = N - 1; r >= 0; r--)
        {
            double s = A[r, N];
            for (int c = r + 1; c < N; c++) s -= A[r, c] * x[c];
            x[r] = s / A[r, r];
        }
        return true;
    }

    // ---- Derived state -----------------------------------------------------------------------

    /// <summary>Barycentric position of the body itself (planets hosting satellites are
    /// offset from their subsystem barycentre; satellites are host + offset).</summary>
    public Vector3d AbsolutePosition(int index)
    {
        var b = _bodies[index];
        if (b.Parent >= 0) return AbsolutePosition(b.Parent) + b.Pos;
        foreach (var sub in _subsystems)
        {
            if (sub.Parent != index) continue;
            Vector3d off = Vector3d.Zero;
            foreach (int s in sub.Sats) off += _bodies[s].Pos * _bodies[s].Mass;
            return b.Pos - off / sub.TotalMass;
        }
        return b.Pos;
    }

    public Vector3d AbsoluteVelocity(int index)
    {
        var b = _bodies[index];
        if (b.Parent >= 0) return AbsoluteVelocity(b.Parent) + b.Vel;
        foreach (var sub in _subsystems)
        {
            if (sub.Parent != index) continue;
            Vector3d off = Vector3d.Zero;
            foreach (int s in sub.Sats) off += _bodies[s].Vel * _bodies[s].Mass;
            return b.Vel - off / sub.TotalMass;
        }
        return b.Vel;
    }

    /// <summary>Position relative to the Sun (AU, world-frame orientation) — what the
    /// renderer consumes as <see cref="Planet.HelioAU"/>.</summary>
    public Vector3d HeliocentricPosition(int index) => AbsolutePosition(index) - AbsolutePosition(0);

    public Vector3d HeliocentricVelocity(int index) => AbsoluteVelocity(index) - AbsoluteVelocity(0);

    /// <summary>Total mechanical energy of the massive bodies (M☉·AU²/d²) under the
    /// current force law: for exponent n the pair potential is −GMm/((n−1)·r^(n−1)).</summary>
    public double Energy()
    {
        double G = GM_SUN * Constants.G;
        double n = Constants.GravityExponent;
        int count = _bodies.Count;
        var pos = new Vector3d[count];
        var vel = new Vector3d[count];
        for (int i = 0; i < count; i++) { pos[i] = AbsolutePosition(i); vel[i] = AbsoluteVelocity(i); }
        double ke = 0, pe = 0;
        for (int i = 0; i < count; i++)
        {
            var bi = _bodies[i];
            if (!bi.IsMassive || !bi.Alive) continue;
            ke += 0.5 * bi.Mass * vel[i].LengthSquared;
            for (int j = i + 1; j < count; j++)
            {
                var bj = _bodies[j];
                if (!bj.IsMassive || !bj.Alive) continue;
                double r = Math.Max((pos[j] - pos[i]).Length, MinSeparationAU);
                double pair = n == 2.0
                    ? -G * bi.Mass * bj.Mass / r
                    : -G * bi.Mass * bj.Mass / ((n - 1.0) * Math.Pow(r, n - 1.0));
                pe += pair;
            }
        }
        return ke + pe;
    }

    /// <summary>Total angular momentum of the massive bodies about the origin.</summary>
    public Vector3d AngularMomentum()
    {
        Vector3d L = Vector3d.Zero;
        for (int i = 0; i < _bodies.Count; i++)
        {
            var b = _bodies[i];
            if (!b.IsMassive || !b.Alive) continue;
            L += Vector3d.Cross(AbsolutePosition(i), AbsoluteVelocity(i)) * b.Mass;
        }
        return L;
    }

    /// <summary>Relative energy drift since <see cref="Initialize"/>.</summary>
    public double EnergyDrift()
    {
        double e0 = InitialEnergy;
        if (e0 == 0) return 0;
        return (Energy() - e0) / Math.Abs(e0);
    }

    public readonly record struct Elements(double SemiMajorAxisAU, double Eccentricity, double PeriodDays, double DistanceAU, double SpeedAUPerDay, bool Bound);

    /// <summary>Osculating Keplerian elements of body <paramref name="index"/> about its
    /// primary (the Sun for heliocentric-level bodies, the host for satellites),
    /// computed from the current state assuming an inverse-square law with the
    /// current G / mass scales. With a non-2 exponent these are only indicative.</summary>
    public Elements OsculatingElements(int index)
    {
        var b = _bodies[index];
        Vector3d r, v;
        double mu;
        double G = GM_SUN * Constants.G;
        if (b.Parent >= 0)
        {
            r = b.Pos; v = b.Vel;
            mu = G * (_bodies[b.Parent].Mass + b.Mass);
        }
        else
        {
            r = HeliocentricPosition(index); v = HeliocentricVelocity(index);
            mu = G * (_bodies[0].Mass + SubsystemMass(index));
        }
        double dist = r.Length;
        double speed = v.Length;
        if (dist <= 0 || mu <= 0) return new Elements(0, 0, 0, dist, speed, false);
        double inv = 2.0 / dist - speed * speed / mu;
        var h = Vector3d.Cross(r, v);
        var evec = Vector3d.Cross(v, h) / mu - r / dist;
        double e = evec.Length;
        if (inv <= 0) return new Elements(double.PositiveInfinity, e, double.PositiveInfinity, dist, speed, false);
        double a = 1.0 / inv;
        double period = 2.0 * Math.PI * Math.Sqrt(a * a * a / mu);
        return new Elements(a, e, period, dist, speed, true);
    }

    /// <summary>Heliocentric ecliptic longitude of perihelion (degrees) of a heliocentric
    /// body — used by tests to measure precession.</summary>
    public double PerihelionLongitudeDeg(int index)
    {
        var r = HeliocentricPosition(index);
        var v = HeliocentricVelocity(index);
        double mu = GM_SUN * Constants.G * (_bodies[0].Mass + SubsystemMass(index));
        var h = Vector3d.Cross(r, v);
        var evec = Vector3d.Cross(v, h) / mu - r / r.Length;
        // World frame is (x, z_up, -y): ecliptic x = X, ecliptic y = -Z.
        return Math.Atan2(-evec.Z, evec.X) / OrbitalMechanics.DegToRad;
    }

    /// <summary>Current speed-of-light delay factor (days per AU) for the light-time effect.</summary>
    public double LightDaysPerAU => 1.0 / (LightAuPerDay * Constants.SpeedOfLightScale);
}
