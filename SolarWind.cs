using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SolarSystem;

/// <summary>
/// CPU-driven particle system that emits points radially outward from the Sun,
/// fades them over their lifetime, and renders them as additively-blended GL points
/// with size attenuated by remaining life. A single VAO/VBO pair is reused; the VBO
/// is updated each frame with only the currently alive particles, packed tightly.
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

    private readonly Particle[] _particles;
    private readonly float[] _packed; // {x,y,z,life01} per active particle
    private float _emitAccumulator;
    private readonly Random _rng = new(1);

    private int _vao, _vbo;
    private ShaderProgram _shader = null!;

    public SolarWind(int maxParticles = 6000)
    {
        MaxParticles = maxParticles;
        _particles = new Particle[maxParticles];
        _packed = new float[maxParticles * 4];
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

    /// <summary>Integrates particle motion and emits new particles when enabled.
    /// Existing particles continue to fade out naturally when disabled, so the toggle
    /// is visually graceful instead of cutting them off mid-flight.</summary>
    public void Update(float dt, Vector3 sunPos, float sunRadius)
    {
        // Integrate motion + age.
        int alive = 0;
        for (int i = 0; i < _particles.Length; i++)
        {
            ref var p = ref _particles[i];
            if (p.Life <= 0f) continue;
            p.Pos += p.Vel * dt;
            p.Life -= dt;
            if (p.Life <= 0f) continue;
            alive++;
        }

        // Emit new particles from a thin shell around the Sun.
        if (Enabled)
        {
            _emitAccumulator += EmissionRate * dt;
            int toEmit = (int)_emitAccumulator;
            _emitAccumulator -= toEmit;
            if (toEmit > 0) EmitBatch(toEmit, sunPos, sunRadius);
        }

        // Pack alive particles tightly and upload.
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

    public void Draw(Camera cam)
    {
        if (ActiveCount == 0) return;

        _shader.Use();
        _shader.SetMatrix4("uView", cam.ViewMatrix);
        _shader.SetMatrix4("uProj", cam.ProjectionMatrix);

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
out float vLife;
void main() {
    vec4 vp = uView * vec4(aPos, 1.0);
    gl_Position = uProj * vp;
    // Size attenuates with distance and grows slightly with remaining life.
    float dist = max(1.0, -vp.z);
    gl_PointSize = clamp(220.0 / dist, 1.0, 6.0) * mix(0.4, 1.4, aLife01);
    vLife = aLife01;
}";
    private const string Fs = @"#version 330 core
in float vLife;
out vec4 fragColor;
void main() {
    // Soft round point.
    vec2 d = gl_PointCoord - vec2(0.5);
    float r = length(d);
    if (r > 0.5) discard;
    float a = (1.0 - r * 2.0);
    a = a * a * vLife;
    // Colour shifts from cool yellow at birth to a faint orange-red as it ages.
    vec3 c = mix(vec3(1.0, 0.45, 0.15), vec3(1.0, 0.95, 0.55), vLife);
    fragColor = vec4(c, a);
}";
}
