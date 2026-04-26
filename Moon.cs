using OpenTK.Mathematics;

namespace SolarSystem;

/// <summary>
/// A natural satellite that orbits a host planet (rather than the Sun directly).
/// Wraps a <see cref="Planet"/> body — so it reuses the same sphere mesh, planet
/// shader, texture pipeline and axial-rotation logic — plus a small bundle of
/// orbital parameters expressed relative to the host. Position is computed each
/// frame as <c>host.Position + R(angle)</c> on a circular orbit inclined to the
/// host's equatorial / ecliptic plane (we use the ecliptic here for simplicity;
/// the visual error against the host's tilt is well below the artistic-radius
/// noise floor for the bodies modelled).
/// </summary>
public sealed class Moon
{
    public Planet Body { get; }
    public int HostPlanetIndex { get; }
    /// <summary>Real semi-major axis around the host, in km. Used in real-scale mode.</summary>
    public double RealOrbitRadiusKm { get; }
    /// <summary>Inflated artistic orbit radius in world units, used in compressed mode
    /// so the moon is clearly visible separated from its host.</summary>
    public float ArtisticOrbitRadius { get; }
    public double OrbitalPeriodDays { get; }
    public float OrbitInclinationDeg { get; }
    /// <summary>Initial phase offset (degrees) so co-orbiting moons don't all start lined up.</summary>
    public double PhaseDeg { get; }

    public Moon(Planet body, int hostPlanetIndex,
                double realOrbitRadiusKm, float artisticOrbitRadius,
                double orbitalPeriodDays, float orbitInclinationDeg,
                double phaseDeg = 0.0)
    {
        Body = body;
        HostPlanetIndex = hostPlanetIndex;
        RealOrbitRadiusKm = realOrbitRadiusKm;
        ArtisticOrbitRadius = artisticOrbitRadius;
        OrbitalPeriodDays = orbitalPeriodDays;
        OrbitInclinationDeg = orbitInclinationDeg;
        PhaseDeg = phaseDeg;
    }
}
