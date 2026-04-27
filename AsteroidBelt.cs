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

    /// <summary>A8: when true and a compute shader is available, the per-frame
    /// Kepler solve runs on the GPU and writes straight into the instance VBO
    /// via SSBO bindings. Falls back automatically if compute compile / link
    /// fails. Toggle with <c>F8</c>; persisted in <c>state.json</c>.</summary>
    public bool UseGpuCompute { get; set; } = true;

    /// <summary>True once the compute shader has been loaded successfully.
    /// When false the GPU path is unavailable regardless of <see cref="UseGpuCompute"/>.</summary>
    public bool GpuComputeAvailable { get; private set; }

    /// <summary>When <see cref="GpuComputeAvailable"/> is false, holds the
    /// underlying reason (compile error, missing extension, GL version too
    /// low, …) so the F8 banner can surface it instead of just "unavailable".</summary>
    public string? LastInitError { get; private set; }

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

    // A8: GPU compute path.
    private ComputeProgram? _compute;
    private int _elementsSsbo;     // std430 buffer of Asteroid records (3 vec4 each).
    private const int ElementStrideFloats = 12; // 3 * vec4
    private const float K_Compressed = 200.0f / 4.6739f;
    private const float Power_Compressed = 0.45f;
    private const float AuToWorld_Real = 50.0f;

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

        // A8: try the GPU compute path. Any failure (no GL 4.3, driver bug, etc.)
        // is non-fatal — the CPU path keeps working.
        try
        {
            // Sanity-check the live GL context: compute shaders need 4.3+.
            string ver = GL.GetString(StringName.Version) ?? "";
            GL.GetInteger(GetPName.MajorVersion, out int major);
            GL.GetInteger(GetPName.MinorVersion, out int minor);
            System.Diagnostics.Debug.WriteLine($"[asteroidbelt] GL context: {ver} (parsed {major}.{minor})");
            if (major < 4 || (major == 4 && minor < 3))
                throw new Exception($"OpenGL {major}.{minor} context — compute shaders require 4.3+");

            _compute = new ComputeProgram(ShaderSources.Load("asteroidbelt.compute"));
            _elementsSsbo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _elementsSsbo);

            float[] elems = new float[Count * ElementStrideFloats];
            for (int i = 0; i < Count; i++)
            {
                ref var a = ref _asteroids[i];
                int o = i * ElementStrideFloats;
                // ae_n_m0
                elems[o + 0] = a.A;
                elems[o + 1] = a.E;
                elems[o + 2] = a.N;
                elems[o + 3] = a.M0;
                // ax_b: Ax.xyz, brightness
                elems[o + 4] = a.Ax.X;
                elems[o + 5] = a.Ax.Y;
                elems[o + 6] = a.Ax.Z;
                elems[o + 7] = _packed[i * 4 + 3]; // brightness
                // by_e: Bx.xyz, EFactor
                elems[o + 8]  = a.Bx.X;
                elems[o + 9]  = a.Bx.Y;
                elems[o + 10] = a.Bx.Z;
                elems[o + 11] = a.EFactor;
            }
            GL.BufferData(BufferTarget.ShaderStorageBuffer,
                elems.Length * sizeof(float), elems, BufferUsageHint.StaticDraw);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
            GpuComputeAvailable = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[asteroidbelt] GPU compute unavailable: {ex.Message}");
            _compute = null;
            GpuComputeAvailable = false;
            LastInitError = ex.Message;
        }
    }

    public void SetViewport(Vector2 viewport) => _viewport = viewport;

    /// <summary>Advance every asteroid's mean anomaly to <paramref name="simDays"/> and
    /// repack the world positions into the VBO.</summary>
    public void Update(double simDays)
    {
        if (UseGpuCompute && GpuComputeAvailable && _compute != null)
        {
            UpdateGpu(simDays);
            return;
        }

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

    /// <summary>A8: GPU Kepler-solve. The compute shader writes vec4(pos.xyz, brightness)
    /// straight into the instance VBO via SSBO bindings, so the rasteriser sees the
    /// new positions without a CPU round-trip.</summary>
    private void UpdateGpu(double simDays)
    {
        _compute!.Use();
        _compute.SetFloat("uSimDays", (float)simDays);
        _compute.SetInt("uCount", Count);
        _compute.SetInt("uRealScale", OrbitalMechanics.RealScale ? 1 : 0);
        _compute.SetFloat("uK", K_Compressed);
        _compute.SetFloat("uPower", Power_Compressed);
        _compute.SetFloat("uAuToWorld", AuToWorld_Real);

        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, _elementsSsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, _mesh.InstanceVbo);

        int groups = (Count + 63) / 64;
        GL.DispatchCompute(groups, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.VertexAttribArrayBarrierBit |
                         MemoryBarrierFlags.ShaderStorageBarrierBit);

        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, 0);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, 0);
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
        _compute?.Dispose();
        if (_elementsSsbo != 0) GL.DeleteBuffer(_elementsSsbo);
        _elementsSsbo = 0;
    }
}
