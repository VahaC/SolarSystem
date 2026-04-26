using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SolarSystem;

/// <summary>
/// Static cloud of N asteroids on individual Keplerian orbits between Mars and Jupiter.
/// Each asteroid's perifocal-to-world basis is precomputed once at construction; per-frame
/// work is just a Kepler solve + a 2-vector linear combination per asteroid, then the
/// positions are uploaded to a single VBO and drawn as additively-blended instanced
/// quads (A4, replacing the legacy GL_POINTS path).
/// </summary>
public sealed class AsteroidBelt : IDisposable
{
    public bool Enabled { get; set; } = true;
    public int Count { get; }

    /// <summary>Per-asteroid Keplerian + precomputed orbital-plane basis (world space).</summary>
    private struct Asteroid
    {
        public float A;          // semi-major axis (AU)
        public float E;          // eccentricity
        public float EFactor;    // sqrt(1-e^2)
        public float N;          // mean motion (rad/day)
        public float M0;         // mean anomaly at J2000 (rad)
        public Vector3 Ax;       // perifocal X axis (cos ω·P + sin ω·Q) in GL world
        public Vector3 Bx;       // perifocal Y axis (-sin ω·P + cos ω·Q) in GL world
    }

    private readonly Asteroid[] _asteroids;
    private readonly float[] _packed; // {x,y,z,brightness}

    private InstancedQuadParticles _mesh = null!;
    private ShaderProgram _shader = null!;
    private Vector2 _viewport = new(1280f, 800f);

    public AsteroidBelt(int count = 8000, int seed = 1337)
    {
        Count = count;
        _asteroids = new Asteroid[count];
        _packed = new float[count * 4];

        var rng = new Random(seed);
        for (int i = 0; i < count; i++)
        {
            // Real main-belt range is ~2.06–3.27 AU; we narrow it slightly so the
            // visualization sits cleanly between Mars (1.52) and Jupiter (5.20).
            double a = 2.15 + rng.NextDouble() * 1.15;
            double e = rng.NextDouble() * 0.18;
            double iDeg = (rng.NextDouble() - 0.5) * 30.0; // ±15°
            double Om = rng.NextDouble() * Math.PI * 2.0;
            double w = rng.NextDouble() * Math.PI * 2.0;
            double M0 = rng.NextDouble() * Math.PI * 2.0;

            // Kepler's third law: period_yrs = a^1.5; n = 2π / period_days.
            double periodDays = Math.Pow(a, 1.5) * 365.25;
            double n = 2.0 * Math.PI / periodDays;

            double iRad = iDeg * OrbitalMechanics.DegToRad;
            double cosOm = Math.Cos(Om), sinOm = Math.Sin(Om);
            double cosI = Math.Cos(iRad), sinI = Math.Sin(iRad);
            double cosW = Math.Cos(w), sinW = Math.Sin(w);

            // Ecliptic→GL mapping: gl.x = ecl.x, gl.y = ecl.z, gl.z = -ecl.y.
            var Pgl = new Vector3((float)cosOm, 0f, (float)-sinOm);
            var Qgl = new Vector3((float)(-sinOm * cosI), (float)sinI, (float)(-cosOm * cosI));

            var Ax = (float)cosW * Pgl + (float)sinW * Qgl;
            var Bx = (float)-sinW * Pgl + (float)cosW * Qgl;

            _asteroids[i] = new Asteroid
            {
                A = (float)a,
                E = (float)e,
                EFactor = (float)Math.Sqrt(1.0 - e * e),
                N = (float)n,
                M0 = (float)M0,
                Ax = Ax,
                Bx = Bx,
            };

            // Brightness baked into the alpha channel of the VBO so each asteroid keeps a
            // stable apparent magnitude across frames.
            _packed[i * 4 + 3] = 0.4f + (float)rng.NextDouble() * 0.6f;
        }
    }

    public void Initialize()
    {
        _mesh = new InstancedQuadParticles(Count);
        _mesh.Initialize();
        _shader = ShaderSources.CreateProgram("particle.vert", "asteroidbelt.frag");
    }

    public void SetViewport(Vector2 viewport) => _viewport = viewport;

    /// <summary>Advance every asteroid's mean anomaly to <paramref name="simDays"/> and
    /// repack the world positions into the VBO.</summary>
    public void Update(double simDays)
    {
        for (int i = 0; i < _asteroids.Length; i++)
        {
            ref var a = ref _asteroids[i];
            double M = a.M0 + a.N * simDays;
            M %= 2.0 * Math.PI;
            if (M < 0) M += 2.0 * Math.PI;
            double E = a.E < 0.8 ? M : Math.PI;
            for (int it = 0; it < 6; it++)
            {
                double f = E - a.E * Math.Sin(E) - M;
                double fp = 1.0 - a.E * Math.Cos(E);
                double d = f / fp;
                E -= d;
                if (Math.Abs(d) < 1e-8) break;
            }

            float xp = a.A * ((float)Math.Cos(E) - a.E);
            float yp = a.A * a.EFactor * (float)Math.Sin(E);

            float s = OrbitalMechanics.OrbitWorldScale(a.A);
            Vector3 pos = (xp * a.Ax + yp * a.Bx) * s;

            _packed[i * 4 + 0] = pos.X;
            _packed[i * 4 + 1] = pos.Y;
            _packed[i * 4 + 2] = pos.Z;
            // alpha (brightness) preserved
        }

        _mesh.UploadInstances(_packed, Count);
    }

    public void Draw(Camera cam)
    {
        if (!Enabled || Count == 0) return;

        _shader.Use();
        _shader.SetMatrix4("uView", cam.ViewMatrix);
        _shader.SetMatrix4("uProj", cam.ProjectionMatrix);
        _shader.SetFloat("uFcoef", 2.0f / MathF.Log2(cam.Far + 1.0f));
        _shader.SetVector2("uViewportSize", _viewport);
        // Legacy: clamp(160/dist, 1, 3.5). Halved -> radius. No life-driven scaling.
        _shader.SetFloat("uPxBase", 80f);
        _shader.SetFloat("uPxMin", 0.5f);
        _shader.SetFloat("uPxMax", 1.75f);
        _shader.SetFloat("uLifeLo", 1.0f);
        _shader.SetFloat("uLifeHi", 1.0f);

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.DepthMask(false);
        _mesh.DrawInstanced(Count);
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
    }

    public void Dispose()
    {
        _shader?.Dispose();
        _mesh?.Dispose();
    }
}
