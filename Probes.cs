using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SolarSystem;

/// <summary>
/// S9: Spacecraft &amp; probes. Each <see cref="Probe"/> exposes a position-update
/// callback that runs once per frame against the current sim time, the planet
/// table and the Sun's world position. The container draws every active probe as
/// a small additive 3-axis cross (uses the existing <c>orbit.vert/frag</c> line
/// shader) plus a label via the bitmap-font path. Trajectories are intentionally
/// approximate &mdash; enough to convey "they're way out there" without dragging
/// in a full ephemeris.
/// </summary>
public sealed class Probes : IDisposable
{
    public bool Enabled { get; set; } = true;
    public IReadOnlyList<Probe> All => _probes;

    private readonly List<Probe> _probes = new();
    private int _vao, _vbo;
    private ShaderProgram _shader = null!;
    private readonly List<float> _scratch = new(64 * 3);

    /// <summary>Reference world-radius of the cross marker. Scaled by camera distance
    /// in <see cref="Update"/> so the cross stays roughly the same on-screen size.</summary>
    private const float MarkerSizePx = 14f;

    public void Initialize()
    {
        _shader = ShaderSources.CreateProgram("orbit.vert", "orbit.frag");
        BuildBuiltIns();

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.BindVertexArray(0);
    }

    /// <summary>Voyager 1 / 2, JWST and ISS. Voyagers use a fixed escape direction
    /// from the Sun &mdash; the real ones are within ~10° of these aim points after
    /// 40+ years of essentially ballistic flight. JWST sits at the Sun&ndash;Earth L2
    /// point, ISS rides a low Earth orbit.</summary>
    private void BuildBuiltIns()
    {
        // Voyager 1: launched 1977-09-05; aimed roughly toward (RA 17.8h, Dec +12°),
        // i.e. into the constellation Ophiuchus. Speed ~3.58 AU/year.
        var v1Launch = new DateTime(1977, 9, 5, 12, 0, 0, DateTimeKind.Utc);
        var v1Dir = EclipticDir(raHours: 17.8, decDeg: 12.0);
        _probes.Add(new Probe
        {
            Name = "Voyager 1",
            Color = new Vector4(0.65f, 0.95f, 1.0f, 0.95f),
            Update = (probe, simDays, planets, sun) =>
            {
                double daysSinceLaunch = simDays - (v1Launch - OrbitalMechanics.J2000).TotalDays;
                if (daysSinceLaunch < 0) { probe.Active = false; return; }
                double rAU = (3.58 / 365.25) * daysSinceLaunch;
                probe.Active = true;
                probe.Position = AuToWorld(v1Dir * (float)rAU);
                probe.HeliocentricAU = rAU;
            },
        });

        // Voyager 2: launched 1977-08-20; aimed toward (RA 19.9h, Dec -55°). Speed ~3.30 AU/yr.
        var v2Launch = new DateTime(1977, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        var v2Dir = EclipticDir(raHours: 19.9, decDeg: -55.0);
        _probes.Add(new Probe
        {
            Name = "Voyager 2",
            Color = new Vector4(0.7f, 1.0f, 0.85f, 0.95f),
            Update = (probe, simDays, planets, sun) =>
            {
                double daysSinceLaunch = simDays - (v2Launch - OrbitalMechanics.J2000).TotalDays;
                if (daysSinceLaunch < 0) { probe.Active = false; return; }
                double rAU = (3.30 / 365.25) * daysSinceLaunch;
                probe.Active = true;
                probe.Position = AuToWorld(v2Dir * (float)rAU);
                probe.HeliocentricAU = rAU;
            },
        });

        // JWST: launched 2021-12-25, parked in halo orbit around Sun-Earth L2
        // (~1.5e6 km from Earth on the anti-Sun side).
        var jwstActive = new DateTime(2022, 1, 24, 12, 0, 0, DateTimeKind.Utc);
        _probes.Add(new Probe
        {
            Name = "JWST",
            Color = new Vector4(1.0f, 0.85f, 0.55f, 0.95f),
            Update = (probe, simDays, planets, sun) =>
            {
                if (simDays < (jwstActive - OrbitalMechanics.J2000).TotalDays)
                { probe.Active = false; return; }
                if (planets.Length < 3) { probe.Active = false; return; }
                var earth = planets[2];
                var antiSun = earth.Position - sun;
                if (antiSun.LengthSquared < 1e-8f) { probe.Active = false; return; }
                antiSun.Normalize();
                float l2DistKm = 1_500_000f;
                float l2DistWorld = OrbitalMechanics.RealScale
                    ? (float)(l2DistKm * OrbitalMechanics.KmToWorldRealScale)
                    : 0.6f; // artistic — sit just outside Earth's silhouette in compressed mode
                probe.Active = true;
                probe.Position = earth.Position + antiSun * l2DistWorld;
                probe.HeliocentricAU = Math.Sqrt(
                    earth.HelioAU.X * earth.HelioAU.X +
                    earth.HelioAU.Y * earth.HelioAU.Y +
                    earth.HelioAU.Z * earth.HelioAU.Z);
            },
        });

        // ISS: launched 1998-11-20; LEO at ~408 km altitude, period ~92.68 min,
        // inclined 51.6° to the equator.
        var issLaunch = new DateTime(1998, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        const double issPeriodDays = 92.68 / (60.0 * 24.0);
        _probes.Add(new Probe
        {
            Name = "ISS",
            Color = new Vector4(1.0f, 0.95f, 0.7f, 0.95f),
            Update = (probe, simDays, planets, sun) =>
            {
                if (simDays < (issLaunch - OrbitalMechanics.J2000).TotalDays)
                { probe.Active = false; return; }
                if (planets.Length < 3) { probe.Active = false; return; }
                var earth = planets[2];
                float r = OrbitalMechanics.RealScale
                    ? (float)(6778.0 * OrbitalMechanics.KmToWorldRealScale)
                    : earth.VisualRadius * 1.3f;
                double ang = (simDays / issPeriodDays) * Math.PI * 2.0;
                float incl = MathHelper.DegreesToRadians(51.6f);
                float cx = (float)Math.Cos(ang) * r;
                float cz = (float)Math.Sin(ang) * r;
                float cy = cz * MathF.Sin(incl);
                cz *= MathF.Cos(incl);
                probe.Active = true;
                probe.Position = earth.Position + new Vector3(cx, cy, cz);
                probe.HeliocentricAU = Math.Sqrt(
                    earth.HelioAU.X * earth.HelioAU.X +
                    earth.HelioAU.Y * earth.HelioAU.Y +
                    earth.HelioAU.Z * earth.HelioAU.Z);
            },
        });
    }

    /// <summary>RA/Dec → unit vector in the same Y-up world frame the camera uses
    /// (matches <c>Constellations.RaDecToUnit</c> exactly).</summary>
    private static Vector3 EclipticDir(double raHours, double decDeg)
    {
        double ra = raHours * 15.0 * Math.PI / 180.0;
        double dec = decDeg * Math.PI / 180.0;
        double cd = Math.Cos(dec);
        return new Vector3(
            (float)(cd * Math.Cos(ra)),
            (float)Math.Sin(dec),
            (float)(-cd * Math.Sin(ra)));
    }

    /// <summary>Convert an AU-space heliocentric vector to world units using the
    /// active scale mode &mdash; mirrors <c>OrbitalMechanics.OrbitWorldScale</c>
    /// but applies a uniform scale across the whole vector (probes don't have a
    /// well-defined &quot;orbit&quot;).</summary>
    private static Vector3 AuToWorld(Vector3 au)
    {
        if (OrbitalMechanics.RealScale) return au * (float)OrbitalMechanics.AuToWorldRealScale;
        // Use the same K * a^p rule with the probe's current heliocentric distance.
        double r = au.Length;
        if (r < 1e-6) return Vector3.Zero;
        float s = OrbitalMechanics.OrbitWorldScale(r);
        return au * s;
    }

    public void Update(double simDays, Planet[] planets, Vector3 sunPos)
    {
        foreach (var p in _probes) p.Update(p, simDays, planets, sunPos);
    }

    public void Draw(Camera cam)
    {
        if (!Enabled) return;

        // Cross size scales with camera distance so the marker stays a roughly
        // constant on-screen size (~MarkerSizePx pixels) without needing a
        // dedicated screen-space pass.
        float ppu = cam.Distance / Math.Max(1, cam.Aspect * 1f);
        _ = ppu;

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

        foreach (var probe in _probes)
        {
            if (!probe.Active) continue;
            float size = MathF.Max(cam.Distance * 0.012f, 0.05f);
            BuildCross(probe.Position, size);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            var arr = _scratch.ToArray();
            GL.BufferData(BufferTarget.ArrayBuffer, arr.Length * sizeof(float), arr, BufferUsageHint.DynamicDraw);
            _shader.SetVector4("uColor", probe.Color);
            GL.DrawArrays(PrimitiveType.Lines, 0, _scratch.Count / 3);
        }

        GL.BindVertexArray(0);
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
    }

    private void BuildCross(Vector3 c, float s)
    {
        _scratch.Clear();
        // Three orthogonal segments forming a 3-axis cross.
        void Seg(Vector3 a, Vector3 b)
        {
            _scratch.Add(a.X); _scratch.Add(a.Y); _scratch.Add(a.Z);
            _scratch.Add(b.X); _scratch.Add(b.Y); _scratch.Add(b.Z);
        }
        Seg(c + new Vector3(-s, 0, 0), c + new Vector3(s, 0, 0));
        Seg(c + new Vector3(0, -s, 0), c + new Vector3(0, s, 0));
        Seg(c + new Vector3(0, 0, -s), c + new Vector3(0, 0, s));
    }

    public void Dispose()
    {
        if (_vbo != 0) GL.DeleteBuffer(_vbo);
        if (_vao != 0) GL.DeleteVertexArray(_vao);
        _shader?.Dispose();
        _vao = _vbo = 0;
    }
}

public sealed class Probe
{
    public string Name { get; init; } = "";
    public Vector4 Color { get; init; } = new Vector4(0.8f, 0.95f, 1f, 1f);
    public Vector3 Position;
    public bool Active;
    /// <summary>Approximate distance from the Sun in AU (for the info tooltip / HUD).</summary>
    public double HeliocentricAU;

    /// <summary>Per-frame position update. Args: probe, simDays, planets, sunPos.</summary>
    public Action<Probe, double, Planet[], Vector3> Update { get; init; } = (_, _, _, _) => { };
}
