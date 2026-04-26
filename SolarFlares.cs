using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SolarSystem;

/// <summary>
/// Periodic eruptions on the Sun's surface. Every few seconds a random spot ignites,
/// throwing a burst of bright particles outward in a cone. A radial "gravity" pulls
/// them back, producing arcing prominences that rise, peak, and fall over their
/// lifetime. Particles are rendered as additively-blended GL points (large near birth,
/// fading from white-hot to deep orange).
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

    private readonly Particle[] _particles;
    private readonly float[] _packed; // {x,y,z,life01}
    private readonly Random _rng = new(7);
    private float _nextBurstIn;

    private int _vao, _vbo;
    private ShaderProgram _shader = null!;

    public SolarFlares(int maxParticles = 4000)
    {
        MaxParticles = maxParticles;
        _particles = new Particle[maxParticles];
        _packed = new float[maxParticles * 4];
        _nextBurstIn = 0.5f;
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
    }

    public void Update(float dt, Vector3 sunPos, float sunRadius)
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
            // Particles that crash back below the surface die immediately.
            if ((p.Pos - sunPos).LengthSquared < sunRadius * sunRadius * 0.95f * 0.95f) p.Life = 0f;
        }

        // Trigger bursts.
        if (Enabled)
        {
            _nextBurstIn -= dt;
            while (_nextBurstIn <= 0f)
            {
                EmitBurst(sunPos, sunRadius);
                // Exponential-ish jitter so the rhythm feels organic.
                float u = (float)_rng.NextDouble();
                _nextBurstIn += BurstIntervalMean * (0.4f + u * 1.6f);
            }
        }
        else
        {
            // Reset timer so re-enabling doesn't dump a backlog of bursts.
            if (_nextBurstIn < 0.2f) _nextBurstIn = 0.2f;
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

    private void EmitBurst(Vector3 sunPos, float sunRadius)
    {
        // Random surface point (uniform on sphere).
        double u = _rng.NextDouble() * 2.0 - 1.0;
        double t = _rng.NextDouble() * Math.PI * 2.0;
        double s = Math.Sqrt(1.0 - u * u);
        var normal = new Vector3((float)(s * Math.Cos(t)), (float)u, (float)(s * Math.Sin(t)));

        // Build an orthonormal tangent basis for the cone.
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

            // Direction = normal tilted within a cone.
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

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One); // additive
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
    }

    private const string Vs = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in float aLife01;
uniform mat4 uView; uniform mat4 uProj;
uniform float uFcoef;
out float vLife;
void main() {
    vec4 vp = uView * vec4(aPos, 1.0);
    gl_Position = uProj * vp;
    gl_Position.z = (log2(max(1e-6, 1.0 + gl_Position.w)) * uFcoef - 1.0) * gl_Position.w;
    float dist = max(1.0, -vp.z);
    // Larger than solar wind, and bigger near birth (life01 close to 1).
    gl_PointSize = clamp(420.0 / dist, 2.0, 14.0) * mix(0.5, 1.6, aLife01);
    vLife = aLife01;
}";
    private const string Fs = @"#version 330 core
in float vLife;
out vec4 fragColor;
void main() {
    vec2 d = gl_PointCoord - vec2(0.5);
    float r = length(d);
    if (r > 0.5) discard;
    float falloff = 1.0 - r * 2.0;
    falloff = falloff * falloff;
    // White-hot core when newborn -> bright orange -> deep red as it cools.
    vec3 hot   = vec3(1.0, 0.95, 0.75);
    vec3 mid   = vec3(1.0, 0.55, 0.18);
    vec3 cool  = vec3(0.65, 0.10, 0.04);
    vec3 c = mix(cool, mix(mid, hot, smoothstep(0.5, 1.0, vLife)),
                       smoothstep(0.0, 0.6, vLife));
    float a = falloff * (0.35 + 0.65 * vLife);
    fragColor = vec4(c, a);
}";
}
