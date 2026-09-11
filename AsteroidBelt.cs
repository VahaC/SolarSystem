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

    // ---- Physics sandbox: test-particle mode -------------------------------------------
    // In Physics / Compare mode every rock stops solving Kepler's equation and instead
    // integrates a = Σ GM_j / r^n in the field of the Sun + planets that PhysicsWorld
    // recorded while it stepped this frame. The state (barycentric pos + vel) lives in
    // an SSBO on the GPU path, or in the arrays below on the CPU fallback.

    /// <summary>True while the belt is being integrated as test particles.</summary>
    public bool PhysicsMode { get; private set; }
    /// <summary>Which path owns the live state; flipping the GPU toggle mid-physics
    /// re-seeds from the current Kepler position (tiny discontinuity, documented).</summary>
    private bool _physicsOnGpu;
    private Vector3d[] _physPos = Array.Empty<Vector3d>();
    private Vector3d[] _physVel = Array.Empty<Vector3d>();
    private int _stateSsbo, _stepsSsbo;
    private float[] _stepScratch = Array.Empty<float>();
    /// <summary>Longest leapfrog step the belt takes (days). Main-belt periods are
    /// 3–6 years, so 1 d keeps the phase error below 1e-5 per orbit.</summary>
    public double MaxBeltStepDays { get; set; } = 1.0;
    /// <summary>CPU fallback: cap on steps per frame. Beyond this the recorded field is
    /// merged into coarser steps (the belt gets less accurate at extreme speeds instead of
    /// stalling the UI). The GPU path has no such cap.</summary>
    public int MaxCpuStepsPerFrame { get; set; } = 16;
    /// <summary>Diagnostics: leapfrog steps the belt replayed in the last update.</summary>
    public int LastPhysicsSteps { get; private set; }

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

    // ---- Physics sandbox ------------------------------------------------------------------

    /// <summary>Seed every asteroid's barycentric state from its Kepler orbit at
    /// <paramref name="world"/>'s current time and switch to test-particle integration.
    /// Call again after the world is re-initialised or reset.</summary>
    public void BeginPhysics(PhysicsWorld world)
    {
        if (!world.Ready) return;
        if (_physPos.Length != Count) { _physPos = new Vector3d[Count]; _physVel = new Vector3d[Count]; }
        double t = world.TimeDays;
        var sunPos = world.AbsolutePosition(0);
        var sunVel = world.AbsoluteVelocity(0);
        for (int i = 0; i < Count; i++)
        {
            ref var a = ref _asteroids[i];
            double M = a.M0 + a.N * t;
            M %= 2.0 * Math.PI;
            if (M < 0) M += 2.0 * Math.PI;
            double E = OrbitalMechanics.SolveKepler(M, a.E);
            double cosE = Math.Cos(E), sinE = Math.Sin(E);
            double xp = a.A * (cosE - a.E);
            double yp = a.A * a.EFactor * sinE;
            // dE/dt = n / (1 - e cos E)  ⇒  velocity in the perifocal plane.
            double Edot = a.N / (1.0 - a.E * cosE);
            double vx = -a.A * sinE * Edot;
            double vy = a.A * a.EFactor * cosE * Edot;
            var Ax = new Vector3d(a.Ax.X, a.Ax.Y, a.Ax.Z);
            var Bx = new Vector3d(a.Bx.X, a.Bx.Y, a.Bx.Z);
            _physPos[i] = Ax * xp + Bx * yp + sunPos;
            _physVel[i] = Ax * vx + Bx * vy + sunVel;
        }
        PhysicsMode = true;
        _physicsOnGpu = UseGpuCompute && GpuComputeAvailable && _compute != null;
        if (_physicsOnGpu) UploadPhysicsState();
        LastPhysicsSteps = 0;
    }

    /// <summary>Back to the analytic Kepler path (state discarded).</summary>
    public void EndPhysics()
    {
        PhysicsMode = false;
        LastPhysicsSteps = 0;
    }

    private void UploadPhysicsState()
    {
        if (_stateSsbo == 0) _stateSsbo = GL.GenBuffer();
        var data = new float[Count * 8];
        for (int i = 0; i < Count; i++)
        {
            int o = i * 8;
            data[o + 0] = (float)_physPos[i].X; data[o + 1] = (float)_physPos[i].Y; data[o + 2] = (float)_physPos[i].Z; data[o + 3] = 0f;
            data[o + 4] = (float)_physVel[i].X; data[o + 5] = (float)_physVel[i].Y; data[o + 6] = (float)_physVel[i].Z; data[o + 7] = 0f;
        }
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _stateSsbo);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, data.Length * sizeof(float), data, BufferUsageHint.DynamicCopy);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
    }

    /// <summary>Replay the field <paramref name="world"/> recorded this frame: merge its
    /// global-step boundaries into ≤ <see cref="MaxBeltStepDays"/> chunks and leapfrog
    /// every asteroid through them (GPU compute when available, CPU otherwise), then
    /// repack heliocentric world positions into the VBO.</summary>
    public void UpdatePhysics(PhysicsWorld world)
    {
        if (!PhysicsMode || !world.Ready) { Update(world.TimeDays); return; }
        bool gpuNow = UseGpuCompute && GpuComputeAvailable && _compute != null;
        if (gpuNow != _physicsOnGpu)
        {
            // The live state sits on the other side; re-seed rather than read back.
            BeginPhysics(world);
        }

        var recs = world.GlobalRecords;
        int bodies = world.RecordBodies.Count;
        if (recs.Count == 0 || bodies == 0) return;

        // Merge consecutive global steps into chunks of at most MaxBeltStepDays.
        double cap = MaxBeltStepDays;
        if (!gpuNow && recs.Count > 1)
        {
            double total = 0; for (int k = 1; k < recs.Count; k++) total += Math.Abs(recs[k].StepDays);
            cap = Math.Max(cap, total / MaxCpuStepsPerFrame);
        }
        var chunkIdx = new List<int> { 0 };
        var chunkDt = new List<double> { 0.0 };
        double acc = 0;
        for (int k = 1; k < recs.Count; k++)
        {
            acc += recs[k].StepDays;
            bool last = k == recs.Count - 1;
            if (Math.Abs(acc) >= cap - 1e-12 || last)
            {
                if (acc != 0.0 || last) { chunkIdx.Add(k); chunkDt.Add(acc); acc = 0; }
            }
        }
        int steps = chunkIdx.Count - 1;
        LastPhysicsSteps = steps;

        if (gpuNow) UpdatePhysicsGpu(world, recs, chunkIdx, chunkDt, bodies);
        else UpdatePhysicsCpu(world, recs, chunkIdx, chunkDt, bodies);
    }

    private void UpdatePhysicsGpu(PhysicsWorld world, IReadOnlyList<PhysicsWorld.StepRecord> recs,
        List<int> chunkIdx, List<double> chunkDt, int bodies)
    {
        int stride = 1 + bodies;
        int records = chunkIdx.Count;
        int floats = records * stride * 4;
        if (_stepScratch.Length < floats) _stepScratch = new float[floats];
        var gm = world.RecordGM;
        for (int c = 0; c < records; c++)
        {
            int o = c * stride * 4;
            _stepScratch[o + 0] = (float)chunkDt[c]; _stepScratch[o + 1] = 0f; _stepScratch[o + 2] = 0f; _stepScratch[o + 3] = 0f;
            var pos = recs[chunkIdx[c]].Positions;
            for (int j = 0; j < bodies; j++)
            {
                int p = o + 4 + j * 4;
                _stepScratch[p + 0] = (float)pos[j].X;
                _stepScratch[p + 1] = (float)pos[j].Y;
                _stepScratch[p + 2] = (float)pos[j].Z;
                _stepScratch[p + 3] = (float)gm[j];
            }
        }
        if (_stepsSsbo == 0) _stepsSsbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _stepsSsbo);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, floats * sizeof(float), _stepScratch, BufferUsageHint.StreamDraw);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);

        _compute!.Use();
        _compute.SetInt("uMode", 1);
        _compute.SetInt("uCount", Count);
        _compute.SetInt("uRealScale", OrbitalMechanics.RealScale ? 1 : 0);
        _compute.SetFloat("uK", K_Compressed);
        _compute.SetFloat("uPower", Power_Compressed);
        _compute.SetFloat("uAuToWorld", AuToWorld_Real);
        _compute.SetInt("uBodyCount", bodies);
        _compute.SetInt("uStride", stride);
        _compute.SetInt("uStepCount", records - 1);
        _compute.SetFloat("uExponent", (float)world.Constants.GravityExponent);
        _compute.SetFloat("uMinSep2", (float)(PhysicsWorld.MinSeparationAU * PhysicsWorld.MinSeparationAU));

        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, _elementsSsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, _mesh.InstanceVbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _stateSsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, _stepsSsbo);
        int groups = (Count + 63) / 64;
        GL.DispatchCompute(groups, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.VertexAttribArrayBarrierBit |
                         MemoryBarrierFlags.ShaderStorageBarrierBit);
        for (int b = 0; b < 4; b++) GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, b, 0);
    }

    private void UpdatePhysicsCpu(PhysicsWorld world, IReadOnlyList<PhysicsWorld.StepRecord> recs,
        List<int> chunkIdx, List<double> chunkDt, int bodies)
    {
        var gm = world.RecordGM;
        double n = world.Constants.GravityExponent;
        double min2 = PhysicsWorld.MinSeparationAU * PhysicsWorld.MinSeparationAU;

        Vector3d Accel(Vector3d r, Vector3d[] pos)
        {
            Vector3d a = Vector3d.Zero;
            for (int j = 0; j < bodies; j++)
            {
                var d = pos[j] - r;
                double d2 = d.LengthSquared;
                if (d2 < min2) d2 = min2;
                double inv = n == 2.0 ? 1.0 / (d2 * Math.Sqrt(d2)) : Math.Pow(d2, -0.5 * (n + 1.0));
                a += d * (gm[j] * inv);
            }
            return a;
        }

        int steps = chunkIdx.Count - 1;
        for (int i = 0; i < Count; i++)
        {
            var p = _physPos[i];
            var v = _physVel[i];
            var acc = Accel(p, recs[chunkIdx[0]].Positions);
            for (int c = 1; c <= steps; c++)
            {
                double dt = chunkDt[c];
                v += acc * (0.5 * dt);
                p += v * dt;
                acc = Accel(p, recs[chunkIdx[c]].Positions);
                v += acc * (0.5 * dt);
            }
            _physPos[i] = p;
            _physVel[i] = v;
        }

        var sun = recs[chunkIdx[steps]].Positions[0];
        for (int i = 0; i < Count; i++)
        {
            float s = OrbitalMechanics.OrbitWorldScale(_asteroids[i].A);
            var h = _physPos[i] - sun;
            _packed[i * 4 + 0] = (float)(h.X * s);
            _packed[i * 4 + 1] = (float)(h.Y * s);
            _packed[i * 4 + 2] = (float)(h.Z * s);
        }
        _mesh.UploadInstances(_packed, Count);
    }

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
        _compute.SetInt("uMode", 0);
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
        if (_stateSsbo != 0) GL.DeleteBuffer(_stateSsbo);
        if (_stepsSsbo != 0) GL.DeleteBuffer(_stepsSsbo);
        _elementsSsbo = _stateSsbo = _stepsSsbo = 0;
    }
}
