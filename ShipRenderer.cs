// PROTOTYPE (2026-11-17): arcade flight mode — the ship's 3D model and its shading.
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SolarSystem;

// The hull is generated in code: a lofted, faceted fuselage with a raised canopy,
// cranked-delta wings, twin engine nacelles with recessed glowing exhausts and canted
// tail fins. Every face has its own flat normal (vertices duplicated per face) for the
// hard-surface look; ship.frag.glsl adds panel seams, paint, emissives and lighting.

/// <summary>Draws the player's ship. Opaque, depth-tested, inside the HDR scene pass.</summary>
public sealed class ShipRenderer : IDisposable
{
    /// <summary>Material ids; must match the constants in ship.frag.glsl.</summary>
    private static class Mat
    {
        public const float Fuselage = 0, Wing = 1, Canopy = 3, Engine = 4, Bare = 5,
            NavLeft = 6, NavRight = 7, Core = 9, Nacelle = 10, Fin = 11, Rear = 12;
    }

    private const int FloatsPerVertex = 7; // position, normal, material id

    private int _vao, _vbo;
    private int _vertexCount;
    private ShaderProgram _shader = null!;

    /// <summary>Builds the mesh and compiles the ship shader. Needs a current GL context.</summary>
    public void Initialize()
    {
        _shader = ShaderSources.CreateProgram("ship.vert", "ship.frag");
        float[] data = BuildMesh();
        _vertexCount = data.Length / FloatsPerVertex;

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.StaticDraw);
        int stride = FloatsPerVertex * sizeof(float);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
    }

    /// <summary>Draw the ship at <see cref="FlightContext.ShipModel"/>. Restores every GL
    /// state it changes (culling, depth test, depth mask, blending).</summary>
    public void Draw(Camera cam, FlightContext ctx)
    {
        bool cull = GL.IsEnabled(EnableCap.CullFace);
        bool depth = GL.IsEnabled(EnableCap.DepthTest);
        bool blend = GL.IsEnabled(EnableCap.Blend);
        GL.GetBoolean(GetPName.DepthWritemask, out bool depthMask);

        // The hull has thin surfaces and inward-facing nozzle walls; normals are explicit,
        // so culling is not needed (and the mesh is small).
        GL.Disable(EnableCap.CullFace);
        GL.Enable(EnableCap.DepthTest);
        GL.Disable(EnableCap.Blend);
        GL.DepthMask(true);

        _shader.Use();
        _shader.SetMatrix4("uView", cam.ViewMatrix);
        _shader.SetMatrix4("uProj", cam.ProjectionMatrix);
        _shader.SetMatrix4("uModel", ctx.ShipModel);
        _shader.SetFloat("uFcoef", 2.0f / MathF.Log2(cam.Far + 1.0f));
        _shader.SetVector3("uSunPos", ctx.SunPosition);
        _shader.SetVector3("uViewPos", cam.Eye);
        _shader.SetVector3("uAnchorPos", ctx.AnchorPosition);
        _shader.SetFloat("uAnchorRadius", ctx.AnchorRadius);
        _shader.SetVector3("uAnchorColor", ctx.AnchorColor);
        _shader.SetFloat("uTime", ctx.Time);
        _shader.SetFloat("uThrottle", ctx.Throttle01);
        _shader.SetFloat("uBoost", ctx.Boost01);
        _shader.SetFloat("uSunIntensity", 2.6f);

        GL.BindVertexArray(_vao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, _vertexCount);
        GL.BindVertexArray(0);

        if (cull) GL.Enable(EnableCap.CullFace); else GL.Disable(EnableCap.CullFace);
        if (depth) GL.Enable(EnableCap.DepthTest); else GL.Disable(EnableCap.DepthTest);
        if (blend) GL.Enable(EnableCap.Blend); else GL.Disable(EnableCap.Blend);
        GL.DepthMask(depthMask);
    }

    /// <summary>Frees the GL buffers and the shader.</summary>
    public void Dispose()
    {
        if (_vbo != 0) GL.DeleteBuffer(_vbo);
        if (_vao != 0) GL.DeleteVertexArray(_vao);
        _shader?.Dispose();
        _vao = _vbo = 0;
    }

    // ---- Mesh ------------------------------------------------------------------------

    private static float[] BuildMesh()
    {
        var m = new MeshBuilder();
        BuildFuselage(m);
        BuildCanopy(m);
        foreach (float side in new[] { -1f, 1f })
        {
            BuildWing(m, side);
            BuildFin(m, side);
        }
        foreach (var nozzle in ShipLayout.EngineNozzles) BuildNacelle(m, nozzle);
        for (int i = 0; i < ShipLayout.WingTips.Length; i++)
        {
            var tip = ShipLayout.WingTips[i];
            float side = MathF.Sign(tip.X);
            BuildDiamond(m, tip + new Vector3(side * 0.006f, 0f, 0f), new Vector3(0.012f, 0.009f, 0.02f),
                side < 0 ? Mat.NavLeft : Mat.NavRight);
        }
        return m.ToArray();
    }

    // Fuselage cross-section table: z, half-width, top, bottom.
    private static readonly (float Z, float W, float T, float B)[] FuselageSections =
    [
        (-0.50f, 0.000f, 0.000f, 0.000f),
        (-0.43f, 0.030f, 0.020f, 0.016f),
        (-0.32f, 0.058f, 0.040f, 0.030f),
        (-0.17f, 0.080f, 0.060f, 0.040f),
        (0.00f, 0.095f, 0.072f, 0.048f),
        (0.16f, 0.100f, 0.074f, 0.050f),
        (0.31f, 0.094f, 0.064f, 0.046f),
        (0.44f, 0.078f, 0.048f, 0.034f),
    ];

    private static float FuselageTop(float z)
    {
        var s = FuselageSections;
        for (int i = 0; i < s.Length - 1; i++)
        {
            if (z <= s[i + 1].Z)
            {
                float t = (z - s[i].Z) / (s[i + 1].Z - s[i].Z);
                return MathHelper.Lerp(s[i].T, s[i + 1].T, Math.Clamp(t, 0f, 1f));
            }
        }
        return s[^1].T;
    }

    private static Vector3[] FuselageRing(float z, float w, float t, float b)
    {
        // Nine points: chined, faceted cross-section with a slight dorsal ridge.
        return
        [
            new(0f, t * 1.08f, z),
            new(0.55f * w, t, z),
            new(w, 0.3f * t, z),
            new(w, -0.3f * b, z),
            new(0.6f * w, -b, z),
            new(-0.6f * w, -b, z),
            new(-w, -0.3f * b, z),
            new(-w, 0.3f * t, z),
            new(-0.55f * w, t, z),
        ];
    }

    private static void BuildFuselage(MeshBuilder m)
    {
        var rings = new Vector3[FuselageSections.Length][];
        for (int i = 0; i < rings.Length; i++)
        {
            var s = FuselageSections[i];
            rings[i] = FuselageRing(s.Z, s.W, s.T, s.B);
            if (s.W == 0f) // nose tip: collapse to a point slightly below centre
                for (int j = 0; j < rings[i].Length; j++) rings[i][j] = new Vector3(0f, -0.004f, s.Z);
        }
        m.Loft(rings, Mat.Fuselage);
        m.Cap(rings[^1], Vector3.UnitZ, Mat.Rear);
    }

    private static void BuildCanopy(MeshBuilder m)
    {
        // z, half-width, height above the hull top.
        (float Z, float W, float H)[] sections =
        [
            (-0.31f, 0.000f, 0.000f),
            (-0.24f, 0.030f, 0.024f),
            (-0.14f, 0.044f, 0.046f),
            (-0.03f, 0.042f, 0.044f),
            (0.07f, 0.030f, 0.020f),
            (0.15f, 0.012f, 0.002f),
        ];
        var rings = new Vector3[sections.Length][];
        for (int i = 0; i < sections.Length; i++)
        {
            var s = sections[i];
            float by = FuselageTop(s.Z) * 1.0f;
            float w = s.W, h = s.H;
            rings[i] =
            [
                new(-w, by - 0.004f, s.Z),
                new(-w * 0.85f, by + h * 0.55f, s.Z),
                new(-w * 0.4f, by + h, s.Z),
                new(w * 0.4f, by + h, s.Z),
                new(w * 0.85f, by + h * 0.55f, s.Z),
                new(w, by - 0.004f, s.Z),
            ];
        }
        m.Loft(rings, Mat.Canopy);
    }

    private static Vector3[] Airfoil(Vector3 origin, Vector3 thickDir, float zLe, float zTe, float th)
    {
        float c = zTe - zLe;
        return
        [
            origin + new Vector3(0f, 0f, zLe),
            origin + new Vector3(0f, 0f, zLe + 0.28f * c) + thickDir * th,
            origin + new Vector3(0f, 0f, zLe + 0.75f * c) + thickDir * (th * 0.6f),
            origin + new Vector3(0f, 0f, zTe),
            origin + new Vector3(0f, 0f, zLe + 0.75f * c) - thickDir * (th * 0.45f),
            origin + new Vector3(0f, 0f, zLe + 0.28f * c) - thickDir * (th * 0.7f),
        ];
    }

    private static void BuildWing(MeshBuilder m, float side)
    {
        var tip = ShipLayout.WingTips[side < 0 ? 0 : 1];
        // Cranked delta: long strake at the root, swept outer panel to the tip.
        (float X, float Y, float Le, float Te, float Th)[] sections =
        [
            (0.05f, 0.000f, -0.24f, 0.36f, 0.020f),
            (0.17f, -0.002f, -0.02f, 0.38f, 0.017f),
            (0.27f, -0.005f, 0.11f, 0.38f, 0.012f),
            (MathF.Abs(tip.X), tip.Y, tip.Z - 0.075f, tip.Z + 0.055f, 0.006f),
        ];
        var rings = new Vector3[sections.Length][];
        for (int i = 0; i < sections.Length; i++)
        {
            var s = sections[i];
            rings[i] = Airfoil(new Vector3(side * s.X, s.Y, 0f), Vector3.UnitY, s.Le, s.Te, s.Th);
        }
        m.Loft(rings, Mat.Wing);
        m.Cap(rings[^1], new Vector3(side, 0f, 0f), Mat.Wing);
    }

    private static void BuildFin(MeshBuilder m, float side)
    {
        float cant = MathHelper.DegreesToRadians(16f);
        var span = new Vector3(side * MathF.Sin(cant), MathF.Cos(cant), 0f);
        var thick = new Vector3(MathF.Cos(cant), -side * MathF.Sin(cant), 0f);
        var root = new Vector3(side * 0.17f, 0.085f, 0f);
        (float H, float Le, float Te, float Th)[] sections =
        [
            (0.00f, 0.13f, 0.44f, 0.011f),
            (0.07f, 0.24f, 0.445f, 0.008f),
            (0.15f, 0.34f, 0.45f, 0.004f),
        ];
        var rings = new Vector3[sections.Length][];
        for (int i = 0; i < sections.Length; i++)
        {
            var s = sections[i];
            rings[i] = Airfoil(root + span * s.H, thick, s.Le, s.Te, s.Th);
        }
        m.Loft(rings, Mat.Fin);
        m.Cap(rings[^1], span, Mat.Fin);
    }

    private const int NacelleSides = 18;

    private static Vector3[] Circle(Vector3 centre, float z, float r, int n = NacelleSides)
    {
        var ring = new Vector3[n];
        for (int j = 0; j < n; j++)
        {
            float a = MathHelper.TwoPi * j / n + MathF.PI / n;
            ring[j] = new Vector3(centre.X + r * MathF.Cos(a), centre.Y + r * MathF.Sin(a), z);
        }
        return ring;
    }

    private static void BuildNacelle(MeshBuilder m, Vector3 nozzle)
    {
        float rExit = ShipLayout.NozzleRadius;
        float zExit = nozzle.Z;
        // Outer skin, front intake to the exhaust lip.
        (float Z, float R)[] skin =
        [
            (-0.07f, 0.052f),
            (-0.045f, 0.066f),
            (0.06f, 0.079f),
            (0.30f, 0.080f),
            (0.40f, 0.078f),
            (zExit, 0.075f),
        ];
        var rings = new Vector3[skin.Length][];
        for (int i = 0; i < skin.Length; i++) rings[i] = Circle(nozzle, skin[i].Z, skin[i].R);
        m.Loft(rings, Mat.Nacelle);

        // Exhaust lip: flat annulus from the skin to the exit circle (exactly NozzleRadius at zExit).
        var lipOuter = rings[^1];
        var exit = Circle(nozzle, zExit, rExit);
        m.Strip(lipOuter, exit, _ => Vector3.UnitZ, Mat.Bare);

        // Recessed glowing interior: inward-facing walls, a back plate and a hot centre spike.
        var mid = Circle(nozzle, zExit - 0.05f, rExit * 0.93f);
        var deep = Circle(nozzle, zExit - 0.10f, rExit * 0.78f);
        var axis = new Vector3(nozzle.X, nozzle.Y, 0f);
        Func<Vector3, Vector3> inward = c => new Vector3(axis.X - c.X, axis.Y - c.Y, 0f);
        m.Strip(exit, mid, inward, Mat.Engine);
        m.Strip(mid, deep, inward, Mat.Engine);
        m.Cap(deep, Vector3.UnitZ, Mat.Engine);
        var spikeBase = Circle(nozzle, zExit - 0.099f, rExit * 0.42f, 10);
        var spikeTip = new Vector3(nozzle.X, nozzle.Y, zExit - 0.035f);
        var tipRing = new Vector3[spikeBase.Length];
        Array.Fill(tipRing, spikeTip);
        m.Strip(spikeBase, tipRing, c => new Vector3(c.X - axis.X, c.Y - axis.Y, 0.3f), Mat.Core);

        // Front intake: lip annulus and a dark recessed duct.
        var inletLip = Circle(nozzle, -0.07f, 0.042f);
        m.Strip(rings[0], inletLip, _ => -Vector3.UnitZ, Mat.Bare);
        var inletDeep = Circle(nozzle, -0.02f, 0.038f);
        m.Strip(inletLip, inletDeep, inward, Mat.Bare);
        m.Cap(inletDeep, -Vector3.UnitZ, Mat.Bare);
    }

    private static void BuildDiamond(MeshBuilder m, Vector3 c, Vector3 size, float mat)
    {
        Vector3 px = c + new Vector3(size.X, 0, 0), nx = c - new Vector3(size.X, 0, 0);
        Vector3 py = c + new Vector3(0, size.Y, 0), ny = c - new Vector3(0, size.Y, 0);
        Vector3 pz = c + new Vector3(0, 0, size.Z), nz = c - new Vector3(0, 0, size.Z);
        Vector3[][] faces =
        [
            [px, py, pz], [py, nx, pz], [nx, ny, pz], [ny, px, pz],
            [py, px, nz], [nx, py, nz], [ny, nx, nz], [px, ny, nz],
        ];
        foreach (var f in faces)
        {
            var centroid = (f[0] + f[1] + f[2]) / 3f;
            m.Tri(f[0], f[1], f[2], centroid - c, mat);
        }
    }

    /// <summary>Accumulates flat-shaded triangles (position, normal, material id). Every
    /// triangle is emitted counter-clockwise around its outward normal.</summary>
    private sealed class MeshBuilder
    {
        private readonly List<float> _data = new();

        public float[] ToArray() => _data.ToArray();

        /// <summary>One triangle; its normal is flipped to agree with <paramref name="hint"/>.</summary>
        public void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 hint, float mat)
        {
            var n = Vector3.Cross(b - a, c - a);
            if (n.LengthSquared < 1e-14f) return;
            n.Normalize();
            if (Vector3.Dot(n, hint) < 0f) { n = -n; (b, c) = (c, b); }
            Emit(a, n, mat); Emit(b, n, mat); Emit(c, n, mat);
        }

        /// <summary>A flat quad panel a-b-c-d (one normal for both triangles).</summary>
        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 hint, float mat)
        {
            var n = Vector3.Cross(c - a, d - b);
            if (n.LengthSquared < 1e-14f) n = Vector3.Cross(b - a, c - a);
            if (n.LengthSquared < 1e-14f) return;
            n.Normalize();
            if (Vector3.Dot(n, hint) < 0f) n = -n;
            EmitOriented(a, b, c, n, mat);
            EmitOriented(a, c, d, n, mat);
        }

        private void EmitOriented(Vector3 a, Vector3 b, Vector3 c, Vector3 n, float mat)
        {
            var cr = Vector3.Cross(b - a, c - a);
            if (cr.LengthSquared < 1e-14f) return;
            if (Vector3.Dot(cr, n) < 0f) (b, c) = (c, b);
            Emit(a, n, mat); Emit(b, n, mat); Emit(c, n, mat);
        }

        /// <summary>Skin between consecutive closed rings; normals point away from the ring centres.</summary>
        public void Loft(Vector3[][] rings, float mat)
        {
            for (int i = 0; i < rings.Length - 1; i++)
            {
                var ca = Centroid(rings[i]);
                var cb = Centroid(rings[i + 1]);
                var axis = (ca + cb) * 0.5f;
                Strip(rings[i], rings[i + 1], c => c - axis, mat);
            }
        }

        /// <summary>Quads between two closed rings with the same point count.</summary>
        public void Strip(Vector3[] r0, Vector3[] r1, Func<Vector3, Vector3> hint, float mat)
        {
            int n = r0.Length;
            for (int j = 0; j < n; j++)
            {
                int k = (j + 1) % n;
                var a = r0[j]; var b = r0[k]; var c = r1[k]; var d = r1[j];
                var centre = (a + b + c + d) * 0.25f;
                Quad(a, b, c, d, hint(centre), mat);
            }
        }

        /// <summary>Fan-fills a closed ring, facing <paramref name="hint"/>.</summary>
        public void Cap(Vector3[] ring, Vector3 hint, float mat)
        {
            var c = Centroid(ring);
            for (int j = 0; j < ring.Length; j++)
                Tri(c, ring[j], ring[(j + 1) % ring.Length], hint, mat);
        }

        private static Vector3 Centroid(Vector3[] ring)
        {
            var s = Vector3.Zero;
            foreach (var p in ring) s += p;
            return s / ring.Length;
        }

        private void Emit(Vector3 p, Vector3 n, float mat)
        {
            _data.Add(p.X); _data.Add(p.Y); _data.Add(p.Z);
            _data.Add(n.X); _data.Add(n.Y); _data.Add(n.Z);
            _data.Add(mat);
        }
    }
}
