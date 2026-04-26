using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SolarSystem;

/// <summary>
/// S10: Sun&ndash;planet Lagrange points. For each configured pair we compute the
/// five collinear / triangular points L1..L5 from the analytic restricted
/// three-body formulas (CR3BP) every frame and render them as small additive
/// crosshairs sharing the orbit-line shader. Labels piggy-back on the same
/// bitmap-font path the planet labels use.
/// </summary>
public sealed class LagrangePoints : IDisposable
{
    public bool Enabled { get; set; }

    private readonly struct Pair
    {
        public readonly int PlanetIndex;
        /// <summary>m2 / (m1 + m2). Drives the collinear-point offsets via the
        /// Hill-radius approximation (μ/3)^(1/3).</summary>
        public readonly double Mu;
        public readonly Vector4 Color;
        public Pair(int p, double mu, Vector4 c) { PlanetIndex = p; Mu = mu; Color = c; }
    }

    public readonly struct Marker
    {
        public readonly string Label;
        public readonly Vector3 Pos;
        public readonly Vector4 Color;
        public Marker(string l, Vector3 p, Vector4 c) { Label = l; Pos = p; Color = c; }
    }

    private readonly Pair[] _pairs =
    {
        // Planet index, mass ratio μ, label color.
        new Pair(2,  3.0035e-6, new Vector4(0.55f, 0.95f, 1.00f, 0.95f)), // Sun-Earth
        new Pair(4,  9.5388e-4, new Vector4(1.00f, 0.85f, 0.55f, 0.95f)), // Sun-Jupiter
    };

    private int _vao, _vbo;
    private ShaderProgram _shader = null!;
    private readonly List<float> _scratch = new(128);
    private readonly List<Marker> _markers = new(16);
    public IReadOnlyList<Marker> Markers => _markers;

    public void Initialize()
    {
        _shader = ShaderSources.CreateProgram("orbit.vert", "orbit.frag");
        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.BindVertexArray(0);
    }

    /// <summary>Recompute every Lagrange point from the planets' current world
    /// positions. Called once per frame from <see cref="SolarSystemWindow"/>.</summary>
    public void Update(Planet[] planets, Vector3 sunPos)
    {
        _markers.Clear();
        foreach (var pair in _pairs)
        {
            if (pair.PlanetIndex >= planets.Length) continue;
            var planet = planets[pair.PlanetIndex];
            var rel = planet.Position - sunPos;
            float R = rel.Length;
            if (R < 1e-4f) continue;

            Vector3 er = rel / R;
            // Build a perpendicular axis in the planet's orbital plane. Use the
            // global ecliptic up (Y) as the swing axis — accurate within a degree
            // for every planet we ship.
            Vector3 up = Vector3.UnitY;
            Vector3 perp = Vector3.Cross(up, er);
            if (perp.LengthSquared < 1e-6f) perp = Vector3.UnitX;
            else perp.Normalize();

            // Hill radius for collinear points: r_h = R * (μ/3)^(1/3).
            float hill = R * (float)Math.Pow(pair.Mu / 3.0, 1.0 / 3.0);

            var l1 = sunPos + er * (R - hill);
            var l2 = sunPos + er * (R + hill);
            var l3 = sunPos - er * (R * (1.0f + 5.0f * (float)pair.Mu / 12.0f));
            // L4/L5 sit at the tips of equilateral triangles with Sun & planet.
            float c60 = 0.5f, s60 = 0.8660254f;
            var l4 = sunPos + er * (R * c60) + perp * (R * s60);
            var l5 = sunPos + er * (R * c60) - perp * (R * s60);

            string prefix = planet.Name + " ";
            _markers.Add(new Marker(prefix + "L1", l1, pair.Color));
            _markers.Add(new Marker(prefix + "L2", l2, pair.Color));
            _markers.Add(new Marker(prefix + "L3", l3, pair.Color));
            _markers.Add(new Marker(prefix + "L4", l4, pair.Color));
            _markers.Add(new Marker(prefix + "L5", l5, pair.Color));
        }
    }

    public void Draw(Camera cam)
    {
        if (!Enabled || _markers.Count == 0) return;

        _shader.Use();
        _shader.SetMatrix4("uView", cam.ViewMatrix);
        _shader.SetMatrix4("uProj", cam.ProjectionMatrix);
        _shader.SetMatrix4("uModel", Matrix4.Identity);
        _shader.SetFloat("uFcoef", 2.0f / MathF.Log2(cam.Far + 1.0f));

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
        GL.LineWidth(1.5f);
        GL.DepthMask(false);
        GL.BindVertexArray(_vao);

        foreach (var m in _markers)
        {
            float s = MathF.Max(cam.Distance * 0.010f, 0.04f);
            _scratch.Clear();
            // Diamond: 4 short segments forming a tilted square in the screen plane.
            // Cheap and unambiguous against the line-strip orbit clutter.
            Vector3 c = m.Pos;
            void Seg(Vector3 a, Vector3 b)
            {
                _scratch.Add(a.X); _scratch.Add(a.Y); _scratch.Add(a.Z);
                _scratch.Add(b.X); _scratch.Add(b.Y); _scratch.Add(b.Z);
            }
            Seg(c + new Vector3(-s, 0, 0), c + new Vector3(0, s, 0));
            Seg(c + new Vector3(0, s, 0), c + new Vector3(s, 0, 0));
            Seg(c + new Vector3(s, 0, 0), c + new Vector3(0, -s, 0));
            Seg(c + new Vector3(0, -s, 0), c + new Vector3(-s, 0, 0));
            Seg(c + new Vector3(0, 0, -s), c + new Vector3(0, 0, s));

            var arr = _scratch.ToArray();
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, arr.Length * sizeof(float), arr, BufferUsageHint.DynamicDraw);
            _shader.SetVector4("uColor", m.Color);
            GL.DrawArrays(PrimitiveType.Lines, 0, _scratch.Count / 3);
        }

        GL.BindVertexArray(0);
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
    }

    public void Dispose()
    {
        if (_vbo != 0) GL.DeleteBuffer(_vbo);
        if (_vao != 0) GL.DeleteVertexArray(_vao);
        _shader?.Dispose();
        _vao = _vbo = 0;
    }
}
