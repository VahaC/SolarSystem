using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SolarSystem;

/// <summary>
/// Static cloud of N asteroids on individual Keplerian orbits between Mars and Jupiter.
/// Each asteroid's perifocal-to-world basis is precomputed once at construction; per-frame
/// work is just a Kepler solve + a 2-vector linear combination per asteroid, then the
/// positions are uploaded to a single VBO and drawn as additively-blended GL_POINTS.
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

    private int _vao, _vbo;
    private ShaderProgram _shader = null!;

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

            // Same ecliptic-to-GL mapping used in OrbitalMechanics.HeliocentricPosition:
            //   gl.x =  ecl.x,  gl.y = ecl.z,  gl.z = -ecl.y.
            // Perifocal basis vectors P (line of nodes rotated by ω) and Q (perpendicular
            // in the orbital plane) collapsed with the ecliptic→GL swap:
            var Pgl = new Vector3((float)cosOm, 0f, (float)-sinOm);
            var Qgl = new Vector3((float)(-sinOm * cosI), (float)sinI, (float)(-cosOm * cosI));

            // Fold ω into the basis so per-frame we only need (xp, yp) perifocal coords.
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
        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, Count * 4 * sizeof(float),
            IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 1, VertexAttribPointerType.Float, false, 4 * sizeof(float), 3 * sizeof(float));
        GL.BindVertexArray(0);

        _shader = new ShaderProgram(Vs, Fs);
    }

    /// <summary>Advance every asteroid's mean anomaly to <paramref name="simDays"/> and
    /// repack the world positions into the VBO. Same OrbitWorldScale applied uniformly
    /// so the cloud follows the global compressed/real-scale toggle.</summary>
    public void Update(double simDays)
    {
        for (int i = 0; i < _asteroids.Length; i++)
        {
            ref var a = ref _asteroids[i];
            double M = a.M0 + a.N * simDays;
            // Inline Newton-Raphson Kepler solver (fewer iterations than the planet
            // path because eccentricities are small here).
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

            // World scale uses the asteroid's own semi-major axis so it's consistent
            // with planet orbits in compressed mode (uniform a^p mapping).
            float s = OrbitalMechanics.OrbitWorldScale(a.A);
            Vector3 pos = (xp * a.Ax + yp * a.Bx) * s;

            _packed[i * 4 + 0] = pos.X;
            _packed[i * 4 + 1] = pos.Y;
            _packed[i * 4 + 2] = pos.Z;
            // alpha (brightness) preserved
        }

        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, Count * 4 * sizeof(float), _packed);
    }

    public void Draw(Camera cam)
    {
        if (!Enabled || Count == 0) return;

        _shader.Use();
        _shader.SetMatrix4("uView", cam.ViewMatrix);
        _shader.SetMatrix4("uProj", cam.ProjectionMatrix);
        _shader.SetFloat("uFcoef", 2.0f / MathF.Log2(cam.Far + 1.0f));

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.DepthMask(false);
        GL.Enable(EnableCap.ProgramPointSize);
        GL.BindVertexArray(_vao);
        GL.DrawArrays(PrimitiveType.Points, 0, Count);
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
layout(location=1) in float aBright;
uniform mat4 uView; uniform mat4 uProj;
uniform float uFcoef;
out float vBright;
void main(){
    vec4 vp = uView * vec4(aPos, 1.0);
    gl_Position = uProj * vp;
    gl_Position.z = (log2(max(1e-6, 1.0 + gl_Position.w)) * uFcoef - 1.0) * gl_Position.w;
    float dist = max(1.0, -vp.z);
    gl_PointSize = clamp(160.0 / dist, 1.0, 3.5);
    vBright = aBright;
}";
    private const string Fs = @"#version 330 core
in float vBright; out vec4 fragColor;
void main(){
    vec2 d = gl_PointCoord - vec2(0.5);
    if (length(d) > 0.5) discard;
    // Warm rocky grey-tan, modulated per-asteroid brightness.
    fragColor = vec4(vec3(0.78, 0.72, 0.62) * vBright, vBright);
}";
}
