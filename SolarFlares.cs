using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SolarSystem;

/// <summary>
/// Periodic eruptions on the Sun's surface. Every few seconds a random spot ignites,
/// throwing a burst of bright particles outward in a cone. A radial "gravity" pulls
/// them back, producing arcing prominences that rise, peak, and fall over their
/// lifetime. Particles are rendered as additively-blended instanced quads (A4),
/// integrated with fixed sub-steps (A5).
/// </summary>
public sealed class SolarFlares : IDisposable
{
    private struct Particle
    {
        public Vector3 Pos;
        public Vector3 Vel;
        public float Life;
        public float MaxLife;
    }

    public bool Enabled { get; set; } = true;
    public int MaxParticles { get; }
    public int ActiveCount { get; private set; }

    /// <summary>Average seconds between flare bursts.</summary>
    public float BurstIntervalMean { get; set; } = 1.6f;
    /// <summary>Particles per burst (random in [0.7, 1.3] * this).</summary>
    public int ParticlesPerBurst { get; set; } = 70;
    /// <summary>Outward launch speed (world units / second).</summary>
    public float LaunchSpeed { get; set; } = 22f;
    /// <summary>Half-angle of the launch cone, radians.</summary>
    public float ConeHalfAngle { get; set; } = 0.55f;
    /// <summary>Radial "gravity" pulling particles back toward the Sun (units/s^2).</summary>
    public float Gravity { get; set; } = 9f;
    /// <summary>Particle lifetime (seconds), randomized 0.6–1.4×.</summary>
    public float Lifetime { get; set; } = 4.5f;

    /// <summary>A5: maximum integration step (seconds).</summary>
    public float MaxSubStep { get; set; } = 1f / 60f;

    private readonly Particle[] _particles;
    private readonly float[] _packed; // {x,y,z,life01}
    private readonly Random _rng = new(7);
    private float _nextBurstIn;

    private InstancedQuadParticles _mesh = null!;
    private ShaderProgram _shader = null!;
    private Vector2 _viewport = new(1280f, 800f);

    public SolarFlares(int maxParticles = 4000)
    {
        MaxParticles = maxParticles;
        _particles = new Particle[maxParticles];
        _packed = new float[maxParticles * 4];
        _nextBurstIn = 0.5f;
    }

    public void Initialize()
    {
        _mesh = new InstancedQuadParticles(MaxParticles);
        _mesh.Initialize();
        _shader = ShaderSources.CreateProgram("particle.vert", "solarflares.frag");
    }

    public void SetViewport(Vector2 viewport) => _viewport = viewport;

    public void Update(float dt, Vector3 sunPos, float sunRadius)
    {
        if (dt > 0f)
        {
            int steps = Math.Max(1, (int)Math.Ceiling(dt / MaxSubStep));
            steps = Math.Min(steps, 16);
            float subDt = dt / steps;
            for (int s = 0; s < steps; s++)
                Step(subDt, sunPos, sunRadius);
        }

        // Pack alive particles tightly.
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

    private void Step(float dt, Vector3 sunPos, float sunRadius)
    {
        // Integrate motion + radial gravity back toward Sun.
        for (int i = 0; i < _particles.Length; i++)
        {
            ref var p = ref _particles[i];
            if (p.Life <= 0f) continue;
            Vector3 toSun = sunPos - p.Pos;
            float d = toSun.Length;
            if (d > 1e-4f) p.Vel += (toSun / d) * Gravity * dt;
            p.Pos += p.Vel * dt;
            p.Life -= dt;
            if ((p.Pos - sunPos).LengthSquared < sunRadius * sunRadius * 0.95f * 0.95f) p.Life = 0f;
        }

        if (Enabled)
        {
            _nextBurstIn -= dt;
            while (_nextBurstIn <= 0f)
            {
                EmitBurst(sunPos, sunRadius);
                float u = (float)_rng.NextDouble();
                _nextBurstIn += BurstIntervalMean * (0.4f + u * 1.6f);
            }
        }
        else
        {
            if (_nextBurstIn < 0.2f) _nextBurstIn = 0.2f;
        }
    }

    private void EmitBurst(Vector3 sunPos, float sunRadius)
    {
        // Random surface point (uniform on sphere).
        double u = _rng.NextDouble() * 2.0 - 1.0;
        double t = _rng.NextDouble() * Math.PI * 2.0;
        double s = Math.Sqrt(1.0 - u * u);
        var normal = new Vector3((float)(s * Math.Cos(t)), (float)u, (float)(s * Math.Sin(t)));

        Vector3 helper = MathF.Abs(normal.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 tangent = Vector3.Normalize(Vector3.Cross(helper, normal));
        Vector3 bitangent = Vector3.Cross(normal, tangent);

        Vector3 origin = sunPos + normal * sunRadius * 1.02f;

        int count = (int)(ParticlesPerBurst * (0.7f + (float)_rng.NextDouble() * 0.6f));
        int idx = 0;
        for (int i = 0; i < count; i++)
        {
            while (idx < _particles.Length && _particles[idx].Life > 0f) idx++;
            if (idx >= _particles.Length) return;

            float coneAng = ConeHalfAngle * MathF.Sqrt((float)_rng.NextDouble());
            float az = (float)(_rng.NextDouble() * Math.PI * 2.0);
            Vector3 dir = MathF.Cos(coneAng) * normal +
                          MathF.Sin(coneAng) * (MathF.Cos(az) * tangent + MathF.Sin(az) * bitangent);
            dir = Vector3.Normalize(dir);

            float speed = LaunchSpeed * (0.7f + (float)_rng.NextDouble() * 0.7f);
            float life = Lifetime * (0.6f + (float)_rng.NextDouble() * 0.8f);

            _particles[idx] = new Particle
            {
                Pos = origin,
                Vel = dir * speed,
                Life = life,
                MaxLife = life,
            };
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
        // Legacy: clamp(420/dist, 2, 14) * mix(0.5, 1.6, life). Halved -> radius.
        _shader.SetFloat("uPxBase", 210f);
        _shader.SetFloat("uPxMin", 1.0f);
        _shader.SetFloat("uPxMax", 7.0f);
        _shader.SetFloat("uLifeLo", 0.5f);
        _shader.SetFloat("uLifeHi", 1.6f);

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One); // additive
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
