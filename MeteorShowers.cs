using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SolarSystem;

/// <summary>
/// S11: Annual meteor showers. Each shower has a peak day-of-year and a radiant
/// (RA/Dec on the celestial sphere). When sim time falls within
/// <see cref="WindowDays"/> of a peak we emit short additive streaks near Earth
/// travelling away from the radiant &mdash; a stylised version of the real
/// "burning grains in Earth's path" picture, reusing the A4 instanced-quad
/// pipeline + A5 fixed-substep integration.
/// </summary>
public sealed class MeteorShowers : IDisposable
{
    public bool Enabled { get; set; }
    public int MaxParticles { get; }
    public int ActiveCount { get; private set; }

    /// <summary>Half-width of the activity window (days) around each peak.</summary>
    public float WindowDays { get; set; } = 3f;
    /// <summary>Streak lifetime (seconds), randomised 0.6&ndash;1.4×.</summary>
    public float Lifetime { get; set; } = 1.8f;
    /// <summary>Initial streak speed (world units / s).</summary>
    public float Speed { get; set; } = 10f;
    public float MaxSubStep { get; set; } = 1f / 60f;

    public string ActiveShowerName { get; private set; } = "";

    private readonly struct Shower
    {
        public readonly string Name;
        public readonly int PeakMonth;
        public readonly int PeakDay;
        /// <summary>Radiant unit vector (Y-up world frame, same convention as
        /// <see cref="Constellations"/>'s <c>RaDecToUnit</c>).</summary>
        public readonly Vector3 Radiant;
        public readonly float EmissionRate;
        public Shower(string n, int m, int d, Vector3 r, float e)
        { Name = n; PeakMonth = m; PeakDay = d; Radiant = r; EmissionRate = e; }
    }

    private static readonly Shower[] _showers =
    {
        new Shower("Quadrantids",  1,  3,  RaDec(15.4,  49), 250f),
        new Shower("Lyrids",       4, 22,  RaDec(18.1,  34), 120f),
        new Shower("Eta Aquariids",5,  6,  RaDec(22.5,  -1), 200f),
        new Shower("Perseids",     8, 12,  RaDec( 3.2,  58), 400f),
        new Shower("Orionids",    10, 21,  RaDec( 6.3,  16), 180f),
        new Shower("Leonids",     11, 17,  RaDec(10.2,  22), 200f),
        new Shower("Geminids",    12, 14,  RaDec( 7.5,  33), 450f),
    };

    private static Vector3 RaDec(double raHours, double decDeg)
    {
        double ra = raHours * 15.0 * Math.PI / 180.0;
        double dec = decDeg * Math.PI / 180.0;
        double cd = Math.Cos(dec);
        return new Vector3(
            (float)(cd * Math.Cos(ra)),
            (float)Math.Sin(dec),
            (float)(-cd * Math.Sin(ra)));
    }

    private struct Particle
    {
        public Vector3 Pos;
        public Vector3 Vel;
        public float Life;
        public float MaxLife;
    }

    private readonly Particle[] _particles;
    private readonly float[] _packed;
    private readonly Random _rng = new(11);
    private float _emitAcc;

    private InstancedQuadParticles _mesh = null!;
    private ShaderProgram _shader = null!;
    private Vector2 _viewport = new(1280f, 800f);

    public MeteorShowers(int maxParticles = 1500)
    {
        MaxParticles = maxParticles;
        _particles = new Particle[maxParticles];
        _packed = new float[maxParticles * 4];
    }

    public void Initialize()
    {
        _mesh = new InstancedQuadParticles(MaxParticles);
        _mesh.Initialize();
        // Reuse the comet tail's bright additive look — shorter lifetime turns it
        // into a streak rather than a long tail.
        _shader = ShaderSources.CreateProgram("particle.vert", "comettail.frag");
    }

    public void SetViewport(Vector2 viewport) => _viewport = viewport;

    /// <summary>
    /// Returns the soonest upcoming shower peak relative to <paramref name="simDays"/>,
    /// or <c>null</c> if the table is empty. Wraps to next year if needed.
    /// </summary>
    public (string Name, int DaysUntil)? NextPeak(double simDays)
    {
        var date = OrbitalMechanics.J2000.AddDays(simDays);
        int bestDays = int.MaxValue;
        string bestName = "";
        foreach (var s in _showers)
        {
            for (int yearOffset = 0; yearOffset <= 1; yearOffset++)
            {
                DateTime peak;
                try { peak = new DateTime(date.Year + yearOffset, s.PeakMonth, s.PeakDay, 0, 0, 0, DateTimeKind.Utc); }
                catch { continue; }
                int days = (int)Math.Ceiling((peak - date).TotalDays);
                if (days >= 0 && days < bestDays) { bestDays = days; bestName = s.Name; }
            }
        }
        return bestName.Length == 0 ? null : (bestName, bestDays);
    }

    public void Update(float dt, double simDays, Planet[] planets)
    {
        if (planets.Length < 3) { ActiveCount = 0; ActiveShowerName = ""; return; }
        var earth = planets[2];

        // Find the closest shower whose peak is within the activity window.
        float bestRate = 0f;
        Vector3 radiant = Vector3.Zero;
        ActiveShowerName = "";
        if (Enabled)
        {
            var date = OrbitalMechanics.J2000.AddDays(simDays);
            foreach (var s in _showers)
            {
                var peak = new DateTime(date.Year, s.PeakMonth, s.PeakDay, 0, 0, 0, DateTimeKind.Utc);
                double diff = (date - peak).TotalDays;
                if (Math.Abs(diff) > WindowDays) continue;
                float falloff = 1f - (float)(Math.Abs(diff) / WindowDays);
                float rate = s.EmissionRate * falloff;
                if (rate > bestRate)
                {
                    bestRate = rate;
                    radiant = s.Radiant;
                    ActiveShowerName = s.Name;
                }
            }
        }

        if (dt > 0f)
        {
            int steps = Math.Max(1, (int)Math.Ceiling(dt / MaxSubStep));
            steps = Math.Min(steps, 16);
            float subDt = dt / steps;
            for (int s = 0; s < steps; s++) Step(subDt, earth, radiant, bestRate);
        }

        int n = 0;
        for (int i = 0; i < _particles.Length; i++)
        {
            ref var p = ref _particles[i];
            if (p.Life <= 0f) continue;
            _packed[n * 4 + 0] = p.Pos.X;
            _packed[n * 4 + 1] = p.Pos.Y;
            _packed[n * 4 + 2] = p.Pos.Z;
            _packed[n * 4 + 3] = MathF.Max(0f, p.Life / p.MaxLife);
            n++;
        }
        ActiveCount = n;
        _mesh.UploadInstances(_packed, n);
    }

    private void Step(float dt, Planet earth, Vector3 radiant, float rate)
    {
        for (int i = 0; i < _particles.Length; i++)
        {
            ref var p = ref _particles[i];
            if (p.Life <= 0f) continue;
            p.Pos += p.Vel * dt;
            p.Life -= dt;
        }

        if (rate <= 0f) return;

        _emitAcc += rate * dt;
        int toEmit = (int)_emitAcc;
        _emitAcc -= toEmit;

        // Emit on a small disk perpendicular to the radiant, centred near Earth,
        // with velocity pointing away from the radiant (i.e. into Earth's path).
        Vector3 dir = -radiant; // particles travel from radiant toward observer
        Vector3 t1 = Vector3.Cross(radiant, MathF.Abs(radiant.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
        if (t1.LengthSquared < 1e-6f) t1 = Vector3.UnitX;
        t1.Normalize();
        Vector3 t2 = Vector3.Normalize(Vector3.Cross(radiant, t1));

        float ringRadius = MathF.Max(earth.VisualRadius * 4f, 1.0f);

        int idx = 0;
        for (int i = 0; i < toEmit; i++)
        {
            while (idx < _particles.Length && _particles[idx].Life > 0f) idx++;
            if (idx >= _particles.Length) break;

            double ang = _rng.NextDouble() * Math.PI * 2.0;
            double rr = Math.Sqrt(_rng.NextDouble()) * ringRadius;
            Vector3 jitter = (float)(rr * Math.Cos(ang)) * t1 + (float)(rr * Math.Sin(ang)) * t2;
            Vector3 origin = earth.Position + radiant * ringRadius * 1.2f + jitter;

            // Slight cone spread so streaks aren't perfectly parallel.
            Vector3 spread = ((float)_rng.NextDouble() - 0.5f) * 0.15f * t1
                           + ((float)_rng.NextDouble() - 0.5f) * 0.15f * t2;
            Vector3 v = Vector3.Normalize(dir + spread) * Speed * (0.7f + (float)_rng.NextDouble() * 0.6f);

            float life = Lifetime * (0.6f + (float)_rng.NextDouble() * 0.8f);
            _particles[idx] = new Particle { Pos = origin, Vel = v, Life = life, MaxLife = life };
            idx++;
        }
    }

    public void Draw(Camera cam)
    {
        if (ActiveCount == 0) return;
        _shader.Use();
        _shader.SetMatrix4("uView", cam.ViewMatrix);
        _shader.SetMatrix4("uProj", cam.ProjectionMatrix);
        _shader.SetFloat("uFcoef", 2.0f / MathF.Log2(cam.Far + 1.0f));
        _shader.SetVector2("uViewportSize", _viewport);
        _shader.SetFloat("uPxBase", 90f);
        _shader.SetFloat("uPxMin", 0.6f);
        _shader.SetFloat("uPxMax", 3.0f);
        _shader.SetFloat("uLifeLo", 0.3f);
        _shader.SetFloat("uLifeHi", 1.2f);

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
        GL.DepthMask(false);
        _mesh.DrawInstanced(ActiveCount);
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
    }

    public void Dispose()
    {
        _shader?.Dispose();
        _mesh?.Dispose();
    }
}
