using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SolarSystem;

/// <summary>
/// CPU-driven particle system that emits points radially outward from the Sun,
/// fades them over their lifetime, and renders them as additively-blended billboards
/// (A4: instanced quads, replacing the legacy GL_POINTS path) with size attenuated
/// by remaining life. Integration uses fixed sub-steps (A5) so high frame times
/// don't desync emission or push particles past walls in a single step.
/// </summary>
public sealed class SolarWind : IDisposable
{
    private struct Particle
    {
        public Vector3 Pos;
        public Vector3 Vel;
        public float Life;     // remaining seconds
        public float MaxLife;  // initial life (for normalized fade)
    }

    public bool Enabled { get; set; } = true;
    public int MaxParticles { get; }
    public int ActiveCount { get; private set; }

    /// <summary>Particles emitted per second when enabled.</summary>
    public float EmissionRate { get; set; } = 1500f;

    /// <summary>Average outward speed in world units per second.</summary>
    public float Speed { get; set; } = 35f;

    /// <summary>Average particle lifetime (seconds).</summary>
    public float Lifetime { get; set; } = 6f;

    /// <summary>A5: maximum integration step (seconds). Larger frame times are split
    /// into multiple sub-steps so emission and motion stay frame-rate independent.</summary>
    public float MaxSubStep { get; set; } = 1f / 60f;

    private readonly Particle[] _particles;
    private readonly float[] _packed; // {x,y,z,life01} per active particle
    private float _emitAccumulator;
    private readonly Random _rng = new(1);

    private InstancedQuadParticles _mesh = null!;
    private ShaderProgram _shader = null!;
    private Vector2 _viewport = new(1280f, 800f);

    public SolarWind(int maxParticles = 6000)
    {
        MaxParticles = maxParticles;
        _particles = new Particle[maxParticles];
        _packed = new float[maxParticles * 4];
    }

    public void Initialize()
    {
        _mesh = new InstancedQuadParticles(MaxParticles);
        _mesh.Initialize();
        _shader = ShaderSources.CreateProgram("particle.vert", "solarwind.frag");
    }

    /// <summary>Integrates particle motion and emits new particles when enabled.
    /// Existing particles continue to fade out naturally when disabled, so the toggle
    /// is visually graceful instead of cutting them off mid-flight.</summary>
    public void Update(float dt, Vector3 sunPos, float sunRadius)
    {
        if (dt > 0f)
        {
            // A5: split the frame into fixed-size sub-steps.
            int steps = Math.Max(1, (int)Math.Ceiling(dt / MaxSubStep));
            steps = Math.Min(steps, 16);
            float subDt = dt / steps;
            for (int s = 0; s < steps; s++)
                Step(subDt, sunPos, sunRadius);
        }

        // Pack alive particles tightly and upload (once per frame).
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
        // Integrate motion + age.
        for (int i = 0; i < _particles.Length; i++)
        {
            ref var p = ref _particles[i];
            if (p.Life <= 0f) continue;
            p.Pos += p.Vel * dt;
            p.Life -= dt;
        }

        if (Enabled)
        {
            _emitAccumulator += EmissionRate * dt;
            int toEmit = (int)_emitAccumulator;
            _emitAccumulator -= toEmit;
            if (toEmit > 0) EmitBatch(toEmit, sunPos, sunRadius);
        }
    }

    private void EmitBatch(int count, Vector3 sunPos, float sunRadius)
    {
        int idx = 0;
        for (int i = 0; i < count; i++)
        {
            // Find a free slot.
            while (idx < _particles.Length && _particles[idx].Life > 0f) idx++;
            if (idx >= _particles.Length) return;

            // Uniform direction on a sphere.
            double u = _rng.NextDouble() * 2.0 - 1.0;
            double t = _rng.NextDouble() * Math.PI * 2.0;
            double s = Math.Sqrt(1.0 - u * u);
            var dir = new Vector3((float)(s * Math.Cos(t)), (float)u, (float)(s * Math.Sin(t)));

            float speed = Speed * (0.6f + (float)_rng.NextDouble() * 0.8f);
            float life = Lifetime * (0.6f + (float)_rng.NextDouble() * 0.8f);

            _particles[idx] = new Particle
            {
                Pos = sunPos + dir * sunRadius * 1.05f,
                Vel = dir * speed,
                Life = life,
                MaxLife = life,
            };
            idx++;
        }
    }

    /// <summary>Inform the renderer about the current viewport dimensions so the
    /// instanced-quad shader can convert pixel sizes to clip-space offsets.</summary>
    public void SetViewport(Vector2 viewport) => _viewport = viewport;

    public void Draw(Camera cam)
    {
        if (ActiveCount == 0) return;

        _shader.Use();
        _shader.SetMatrix4("uView", cam.ViewMatrix);
        _shader.SetMatrix4("uProj", cam.ProjectionMatrix);
        _shader.SetFloat("uFcoef", 2.0f / MathF.Log2(cam.Far + 1.0f));
        _shader.SetVector2("uViewportSize", _viewport);
        // Replicate legacy gl_PointSize tuning: clamp(220/dist,1,6) * mix(0.4,1.4,life).
        // pxRadius is half of that since the quad corner spans -1..1 (= diameter).
        _shader.SetFloat("uPxBase", 110f);
        _shader.SetFloat("uPxMin", 0.5f);
        _shader.SetFloat("uPxMax", 3.0f);
        _shader.SetFloat("uLifeLo", 0.4f);
        _shader.SetFloat("uLifeHi", 1.4f);

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
