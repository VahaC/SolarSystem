using OpenTK.Mathematics;

namespace SolarSystem;

/// <summary>
/// S16: container for the loaded comet catalogue. Mirrors the public surface of
/// the original single <see cref="Comet"/> so call-sites in
/// <see cref="SolarSystemWindow"/> change minimally — Initialize / SetViewport /
/// UpdatePosition / UpdateTail / DrawTails / RebuildOrbits all fan out to every
/// loaded comet.
/// </summary>
public sealed class Comets : IDisposable
{
    public Comet[] All { get; private set; } = Array.Empty<Comet>();
    public int TotalActive { get; private set; }
    public int TotalMax { get; private set; }

    public IEnumerable<Planet> Bodies
    {
        get
        {
            foreach (var c in All) yield return c.Body;
        }
    }

    public void Initialize()
    {
        var entries = CometCatalog.LoadOrDefault();
        All = new Comet[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            var c = new Comet(entries[i].Body)
            {
                EmissionRate = entries[i].EmissionRate,
                Lifetime = entries[i].TailLifetime,
                Speed = entries[i].TailSpeed,
            };
            c.Initialize();
            All[i] = c;
        }
        TotalMax = 0;
        foreach (var c in All) TotalMax += c.MaxParticles;
    }

    public void SetViewport(Vector2 vp) { foreach (var c in All) c.SetViewport(vp); }

    public void UpdatePosition(double simDays)
    {
        foreach (var c in All) c.UpdatePosition(simDays);
    }

    public void UpdateTail(float dt, Vector3 sunPos)
    {
        TotalActive = 0;
        foreach (var c in All)
        {
            c.UpdateTail(dt, sunPos);
            TotalActive += c.ActiveCount;
        }
    }

    public void DrawTails(Camera cam)
    {
        foreach (var c in All) c.DrawTail(cam);
    }

    public void RebuildOrbits()
    {
        foreach (var c in All) c.RebuildOrbit();
    }

    public void Dispose()
    {
        foreach (var c in All) c.Dispose();
        All = Array.Empty<Comet>();
    }
}
