using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SolarSystem;

/// <summary>
/// Halley-like comet: a small body on a high-eccentricity Keplerian orbit (e≈0.967,
/// a≈17.83 AU, i=162°) plus a CPU particle "ion + dust" tail whose particles always
/// stream away from the Sun. Reuses the planet shader for the nucleus by exposing a
/// <see cref="Planet"/> instance, but is otherwise self-contained: owns its tail VBO,
/// shader, RNG and orbit-line VBO.
/// </summary>
public sealed class Comet : IDisposable
{
    public bool TailEnabled { get; set; } = true;
    public Planet Body { get; }

    private struct Particle
    {
        public Vector3 Pos;
        public Vector3 Vel;
        public float Life;
        public float MaxLife;
    }

    public int MaxParticles { get; }
    public int ActiveCount { get; private set; }

    /// <summary>Tail length scales with proximity to the Sun (1 AU ⇒ short, perihelion ⇒ long),
    /// roughly mimicking solar-radiation-driven outgassing without simulating physics.</summary>
    public float EmissionRate { get; set; } = 800f;
    public float Lifetime { get; set; } = 4f;
    public float Speed { get; set; } = 18f;

    private readonly Particle[] _particles;
    private readonly float[] _packed;
    private float _emitAcc;
    private readonly Random _rng = new(7);

    private int _vao, _vbo;
    private ShaderProgram _shader = null!;

    private int _orbitVao, _orbitVbo, _orbitCount;

    public Comet(int maxParticles = 4000)
    {
        MaxParticles = maxParticles;
        _particles = new Particle[maxParticles];
        _packed = new float[maxParticles * 4];

        // 1P/Halley J2000 elements (NASA JPL approximations). Retrograde inclination >90°.
        Body = new Planet
        {
            Name = "Halley",
            SemiMajorAxisAU = 17.834,
            Eccentricity = 0.96714,
            InclinationDeg = 162.26,
            LongAscNodeDeg = 58.42,
            ArgPerihelionDeg = 111.33,
            // MeanLongitude at J2000: chosen so the 1986 perihelion lands near simDays = -5066.
            MeanLongitudeDeg = 58.42 + 111.33 + 38.38, // L = Ω + ω + M0
            OrbitalPeriodYears = 75.32,
            VisualRadius = 0.18f,
            RealRadiusKm = 5.5,
            ProceduralColor = new Vector3(0.85f, 0.88f, 0.95f),
            TextureFile = "", // procedural fallback
            AxisTiltDeg = 0f,
            RotationPeriodHours = 52.8,
        };
    }

    public void Initialize()
    {
        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, MaxParticles * 4 * sizeof(float),
            IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 1, VertexAttribPointerType.Float, false, 4 * sizeof(float), 3 * sizeof(float));
        GL.BindVertexArray(0);

        _shader = new ShaderProgram(Vs, Fs);

        // Build the orbit polyline once. The comet's ellipse is highly eccentric so
        // sample it densely to keep aphelion arcs smooth.
        BuildOrbit(512);

        // Procedural fallback texture (small icy-grey nucleus).
        Body.TextureId = TextureManager.LoadOrProcedural(
            Body.TextureFile,
            (byte)(Body.ProceduralColor.X * 255),
            (byte)(Body.ProceduralColor.Y * 255),
            (byte)(Body.ProceduralColor.Z * 255),
            out Body.TextureFromFile);
    }

    private void BuildOrbit(int samples)
    {
        var pts = OrbitalMechanics.SampleOrbit(Body, samples);
        float s = OrbitalMechanics.OrbitWorldScale(Body.SemiMajorAxisAU);
        var data = new float[samples * 3];
        for (int i = 0; i < samples; i++)
        {
            data[i * 3 + 0] = (float)(pts[i].X * s);
            data[i * 3 + 1] = (float)(pts[i].Y * s);
            data[i * 3 + 2] = (float)(pts[i].Z * s);
        }
        _orbitCount = samples;
        if (_orbitVao == 0)
        {
            _orbitVao = GL.GenVertexArray();
            _orbitVbo = GL.GenBuffer();
        }
        GL.BindVertexArray(_orbitVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _orbitVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.StaticDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.BindVertexArray(0);
    }

    /// <summary>Re-upload the orbit polyline after a global scale toggle.</summary>
    public void RebuildOrbit() => BuildOrbit(512);

    public void UpdatePosition(double simDays)
    {
        Body.HelioAU = OrbitalMechanics.HeliocentricPosition(Body, simDays);
        float s = OrbitalMechanics.OrbitWorldScale(Body.SemiMajorAxisAU);
        Body.Position = new Vector3(
            (float)(Body.HelioAU.X * s),
            (float)(Body.HelioAU.Y * s),
            (float)(Body.HelioAU.Z * s));
        if (Body.RotationPeriodHours != 0.0)
        {
            const double TwoPi = Math.PI * 2.0;
            double angle = (simDays * 24.0 / Body.RotationPeriodHours) * TwoPi;
            angle %= TwoPi;
            if (angle < 0) angle += TwoPi;
            Body.RotationAngleRad = (float)angle;
        }
    }

    /// <summary>Advance tail particles and emit new ones from the comet nucleus,
    /// streaming radially outward from the Sun (the iconic anti-solar tail).</summary>
    public void UpdateTail(float dt, Vector3 sunPos)
    {
        // Distance to Sun (AU) drives how active the tail is — far comets are dormant.
        double rAU = Math.Sqrt(
            Body.HelioAU.X * Body.HelioAU.X +
            Body.HelioAU.Y * Body.HelioAU.Y +
            Body.HelioAU.Z * Body.HelioAU.Z);
        float activity = (float)Math.Clamp(2.5 / Math.Max(rAU, 0.5), 0.0, 1.0);

        // Integrate.
        for (int i = 0; i < _particles.Length; i++)
        {
            ref var p = ref _particles[i];
            if (p.Life <= 0f) continue;
            // Slight continued push away from Sun so older particles keep streaming.
            Vector3 awayDir = p.Pos - sunPos;
            float len = awayDir.Length;
            if (len > 1e-3f) p.Vel += (awayDir / len) * (Speed * 0.15f * dt);
            p.Pos += p.Vel * dt;
            p.Life -= dt;
        }

        // Emit.
        if (TailEnabled && activity > 0.01f)
        {
            _emitAcc += EmissionRate * activity * dt;
            int toEmit = (int)_emitAcc;
            _emitAcc -= toEmit;

            Vector3 cometToSun = sunPos - Body.Position;
            float ctsLen = cometToSun.Length;
            Vector3 antiSun = ctsLen > 1e-4f ? -cometToSun / ctsLen : Vector3.UnitX;

            int idx = 0;
            for (int i = 0; i < toEmit; i++)
            {
                while (idx < _particles.Length && _particles[idx].Life > 0f) idx++;
                if (idx >= _particles.Length) break;

                // Cone around the anti-Sun axis: a small random transverse component
                // gives the tail visible thickness without losing its directional look.
                double th = _rng.NextDouble() * Math.PI * 2.0;
                double rr = _rng.NextDouble() * 0.18; // half-cone tan
                Vector3 t1 = Vector3.Cross(antiSun, MathF.Abs(antiSun.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
                t1 = Vector3.Normalize(t1);
                Vector3 t2 = Vector3.Cross(antiSun, t1);
                Vector3 jitter = ((float)(rr * Math.Cos(th))) * t1 + ((float)(rr * Math.Sin(th))) * t2;
                Vector3 dir = Vector3.Normalize(antiSun + jitter);

                float speed = Speed * (0.7f + (float)_rng.NextDouble() * 0.6f) * activity;
                float life = Lifetime * (0.6f + (float)_rng.NextDouble() * 0.8f);

                _particles[idx] = new Particle
                {
                    Pos = Body.Position + dir * (Body.VisualRadius * 1.2f),
                    Vel = dir * speed,
                    Life = life,
                    MaxLife = life,
                };
                idx++;
            }
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

        if (n > 0)
        {
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, n * 4 * sizeof(float), _packed);
        }
    }

    public void DrawOrbit(Camera cam, ShaderProgram orbitShader)
    {
        if (_orbitCount < 2) return;
        orbitShader.Use();
        orbitShader.SetMatrix4("uView", cam.ViewMatrix);
        orbitShader.SetMatrix4("uProj", cam.ProjectionMatrix);
        orbitShader.SetMatrix4("uModel", Matrix4.Identity);
        orbitShader.SetFloat("uFcoef", 2.0f / MathF.Log2(cam.Far + 1.0f));
        orbitShader.SetVector4("uColor", new Vector4(0.6f, 0.8f, 1.0f, 0.45f));

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.BindVertexArray(_orbitVao);
        GL.DrawArrays(PrimitiveType.LineLoop, 0, _orbitCount);
        GL.BindVertexArray(0);
        GL.Disable(EnableCap.Blend);
    }

    public void DrawTail(Camera cam)
    {
        if (ActiveCount == 0) return;

        _shader.Use();
        _shader.SetMatrix4("uView", cam.ViewMatrix);
        _shader.SetMatrix4("uProj", cam.ProjectionMatrix);
        _shader.SetFloat("uFcoef", 2.0f / MathF.Log2(cam.Far + 1.0f));

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
        GL.DepthMask(false);
        GL.Enable(EnableCap.ProgramPointSize);
        GL.BindVertexArray(_vao);
        GL.DrawArrays(PrimitiveType.Points, 0, ActiveCount);
        GL.BindVertexArray(0);
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
    }

    public void Dispose()
    {
        _shader?.Dispose();
        if (_vbo != 0) GL.DeleteBuffer(_vbo);
        if (_vao != 0) GL.DeleteVertexArray(_vao);
        if (_orbitVbo != 0) GL.DeleteBuffer(_orbitVbo);
        if (_orbitVao != 0) GL.DeleteVertexArray(_orbitVao);
    }

    private const string Vs = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in float aLife01;
uniform mat4 uView; uniform mat4 uProj;
uniform float uFcoef;
out float vLife;
void main(){
    vec4 vp = uView * vec4(aPos, 1.0);
    gl_Position = uProj * vp;
    gl_Position.z = (log2(max(1e-6, 1.0 + gl_Position.w)) * uFcoef - 1.0) * gl_Position.w;
    float dist = max(1.0, -vp.z);
    gl_PointSize = clamp(180.0 / dist, 1.0, 5.0) * mix(0.4, 1.4, aLife01);
    vLife = aLife01;
}";
    private const string Fs = @"#version 330 core
in float vLife; out vec4 fragColor;
void main(){
    vec2 d = gl_PointCoord - vec2(0.5);
    float r = length(d);
    if (r > 0.5) discard;
    float a = (1.0 - r * 2.0);
    a = a * a * vLife;
    // Ion-tail blue at birth fading to a cooler dust-grey as particles age.
    vec3 c = mix(vec3(0.6, 0.7, 0.85), vec3(0.55, 0.85, 1.15), vLife);
    fragColor = vec4(c, a);
}";
}
