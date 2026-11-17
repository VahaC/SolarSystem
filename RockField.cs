// PROTOTYPE (2026-11-17): arcade flight mode — 3D rocks and ice chunks of the ring the ship flies through.
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SolarSystem;

// How the field works: every layer draws a fixed number of instances per mesh variant. An
// instance has a fixed offset inside a square tile (side = Period) in ring-plane
// coordinates relative to the anchor centre; the vertex shader moves it to the copy
// nearest the camera, so the field is endless but only ~Period/2 around the camera exists.
// Size, height, existence, spin and colour are hashed from (instance, tile index), so
// the wrap never shows a repeating pattern. Rocks fade to zero scale before the wrap
// boundary. Collide() evaluates the same hash on the CPU; keep it in sync with
// Resources/Shaders/rock.vert.glsl.

/// <summary>Opaque, lit, instanced rocks around the ship inside the anchor planet's ring
/// band; drawn inside the HDR scene pass before the translucent ring.</summary>
public sealed class RockField : IDisposable
{
    private sealed class Layer
    {
        public float Period, SizeMin, SizeMax, SizeGamma, Density, Thick, MinPx;
        public int PerVariant;
        public int[] Variants = [];
        public bool Collidable;
        public uint SeedBase;
        // Precomputed per (variant slot, instance): instance hash and tile offset.
        public uint[][] IdHash = [];
        public Vector2[][] Offset = [];
        // Clear zones: ZoneMerge > 0 merges runs of neighbouring zones into bounding
        // spheres no bigger than that (big-rock layers reach far from the camera and need
        // fewer, coarser spheres). Zones = this layer's set (anchor-relative, world axes).
        public float ZoneMerge;
        public Vector4[] Zones = [];
        public float[] Keys = [];
        public int[] Idx = [];
        // The zones picked for the current camera (ring coordinates + radius) and their
        // strength 0..1 — shared by Draw (uploaded as uniforms) and Collide.
        public readonly float[] Sel = new float[MaxZones * 4];
        public readonly float[] SelK = new float[MaxZones];
        public int SelCount;
    }

    /// <summary>Size of the clear-zone uniform array in rock.vert.glsl (uZone / uZoneK).</summary>
    private const int MaxZones = 32;
    private Vector4[] _zones = [];
    private bool _selValid;

    private readonly Layer[] _layers =
    [
        // Pebbles and ice grains: dense, only close to the camera.
        new() { Period = 1.1f, SizeMin = 0.002f, SizeMax = 0.010f, SizeGamma = 1.6f, Density = 1.0f, Thick = 1.0f,
                PerVariant = 2600, Variants = [0, 3], SeedBase = 11, MinPx = 1.0f },
        // Gravel / small chunks.
        new() { Period = 2.4f, SizeMin = 0.008f, SizeMax = 0.024f, SizeGamma = 1.8f, Density = 1.0f, Thick = 1.0f, MinPx = 1.0f,
                PerVariant = 700, Variants = [0, 1, 2, 3, 4], SeedBase = 23, Collidable = true },
        // Boulders, about the size of the ship.
        new() { Period = 4.4f, SizeMin = 0.02f, SizeMax = 0.08f, SizeGamma = 2.2f, Density = 0.85f, Thick = 1.0f, MinPx = 0.5f,
                PerVariant = 280, Variants = [1, 2, 4, 0], SeedBase = 37, Collidable = true, ZoneMerge = 0.25f },
        // Rare hero rocks, visible from far away.
        new() { Period = 12f, SizeMin = 0.15f, SizeMax = 0.30f, SizeGamma = 1.3f, Density = 0.7f, Thick = 0.6f, MinPx = 0.5f,
                PerVariant = 14, Variants = [1, 2, 4], SeedBase = 53, Collidable = true, ZoneMerge = 0.5f },
    ];

    /// <summary>Rocks at least this big (radius, world units) take part in <see cref="Collide"/>.</summary>
    public const float MinCollideSize = 0.02f;
    /// <summary>Collision radius as a fraction of the drawn size (meshes are normalised to a
    /// maximum radius of 1; their mean radius is a bit smaller).</summary>
    public const float CollideRadiusFactor = 0.8f;

    private ShaderProgram? _shader;
    private int _vao, _vbo;
    private readonly int[] _variantFirst = new int[5];
    private readonly int[] _variantCount = new int[5];
    private Vector2 _viewport = new(1600, 900);
    private Vector3 _lastCamRing;
    private bool _hasCam;

    /// <summary>Total instances submitted per frame (before shader culling).</summary>
    public int InstanceCount { get; private set; }

    /// <summary>Builds the rock meshes, the vertex buffer and the shader.</summary>
    public void Initialize()
    {
        var verts = new List<float>();
        for (int v = 0; v < 5; v++)
        {
            _variantFirst[v] = verts.Count / 9;
            BuildRock(v, verts);
            _variantCount[v] = verts.Count / 9 - _variantFirst[v];
        }
        var data = verts.ToArray();

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.StaticDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 9 * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 9 * sizeof(float), 3 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, 9 * sizeof(float), 6 * sizeof(float));
        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);

        _shader = ShaderSources.CreateProgram("rock.vert", "rock.frag");

        int total = 0;
        foreach (var l in _layers)
        {
            l.IdHash = new uint[l.Variants.Length][];
            l.Offset = new Vector2[l.Variants.Length][];
            for (int s = 0; s < l.Variants.Length; s++)
            {
                uint seed = SeedOf(l, s);
                l.IdHash[s] = new uint[l.PerVariant];
                l.Offset[s] = new Vector2[l.PerVariant];
                for (int i = 0; i < l.PerVariant; i++)
                {
                    uint idh = Hash(unchecked(seed * 0x9e3779b9u + (uint)i));
                    l.IdHash[s][i] = idh;
                    l.Offset[s][i] = new Vector2(Rnd(idh, 0), Rnd(idh, 1)) * l.Period;
                }
                total += l.PerVariant;
            }
        }
        InstanceCount = total;
    }

    /// <summary>Framebuffer size in pixels; called at start-up and on every resize.</summary>
    public void SetViewport(Vector2 viewport) => _viewport = viewport;

    /// <summary>Spheres (xyz = centre relative to the anchor planet's centre, world axes;
    /// w = radius) that must stay free of rocks — the race gates and the stretches of the
    /// racing line that cross the ring plane. Replaces the previous set; an empty list
    /// clears it. A rock whose body would reach into a zone is neither drawn nor reported
    /// by <see cref="Collide"/>; rocks shrink smoothly to nothing over a margin that grows
    /// with their size, so the corridor looks swept clean. Each frame only the zones near
    /// the camera (at most 32 per layer) are considered, identically in the shader and in
    /// <see cref="Collide"/>.</summary>
    public void SetClearZones(IReadOnlyList<Vector4> zones)
    {
        var list = new List<Vector4>(zones.Count);
        foreach (var z in zones)
            if (z.W > 0f && float.IsFinite(z.X) && float.IsFinite(z.Y) && float.IsFinite(z.Z) && float.IsFinite(z.W))
                list.Add(z);
        _zones = [.. list];
        foreach (var l in _layers)
        {
            l.Zones = l.ZoneMerge > 0f ? MergeZones(_zones, l.ZoneMerge) : _zones;
            l.Keys = new float[l.Zones.Length];
            l.Idx = new int[l.Zones.Length];
            l.SelCount = 0;
        }
        _selValid = false;
    }

    /// <summary>Greedily merges consecutive zones (the track lists them along the line)
    /// into bounding spheres of at most <paramref name="limit"/> radius. Conservative: a
    /// merged sphere always contains its members.</summary>
    private static Vector4[] MergeZones(Vector4[] zones, float limit)
    {
        var res = new List<Vector4>();
        Vector3 c = default;
        float r = -1f;
        foreach (var z in zones)
        {
            if (r < 0f) { c = z.Xyz; r = z.W; continue; }
            Vector3 d = z.Xyz - c;
            float dl = d.Length;
            Vector3 nc; float nr;
            if (dl + z.W <= r) { nc = c; nr = r; }
            else if (dl + r <= z.W) { nc = z.Xyz; nr = z.W; }
            else { nr = 0.5f * (dl + r + z.W); nc = c + d / dl * (nr - r); }
            if (nr <= limit || nr <= r) { c = nc; r = nr; }
            else { res.Add(new Vector4(c, r)); c = z.Xyz; r = z.W; }
        }
        if (r >= 0f) res.Add(new Vector4(c, r));
        return [.. res];
    }

    /// <summary>Picks the zones that can touch this layer's rocks around the camera
    /// (ring-plane distance), nearest first, at most <see cref="MaxZones"/>. When the cap
    /// cuts the list, the strength of the farthest picked zones fades to 0 toward the
    /// first zone left out, so the set changes continuously as the camera moves.</summary>
    private static void SelectZones(Layer l, Vector3 camRing, Vector3 U, Vector3 N, Vector3 W)
    {
        float R = 0.5f * l.Period;
        float dmax = 1.1f * R + 1.9f * l.SizeMax + 0.05f;
        int n = 0;
        var zs = l.Zones;
        for (int i = 0; i < zs.Length; i++)
        {
            Vector3 z = zs[i].Xyz;
            float dx = Vector3.Dot(z, U) - camRing.X, dz = Vector3.Dot(z, W) - camRing.Z;
            float key = MathF.Sqrt(dx * dx + dz * dz) - zs[i].W;
            if (key >= dmax) continue;
            l.Keys[n] = key; l.Idx[n] = i; n++;
        }
        Array.Sort(l.Keys, l.Idx, 0, n);
        float dcut = n > MaxZones ? MathF.Min(dmax, l.Keys[MaxZones]) : dmax;
        int m = Math.Min(n, MaxZones), c = 0;
        for (int j = 0; j < m; j++)
        {
            float k = 1f - SmoothStep(0.75f * dcut, dcut, l.Keys[j]);
            if (k <= 0f) continue;
            var z = zs[l.Idx[j]];
            l.Sel[c * 4 + 0] = Vector3.Dot(z.Xyz, U);
            l.Sel[c * 4 + 1] = Vector3.Dot(z.Xyz, N);
            l.Sel[c * 4 + 2] = Vector3.Dot(z.Xyz, W);
            l.Sel[c * 4 + 3] = z.W;
            l.SelK[c] = k;
            c++;
        }
        l.SelCount = c;
    }

    private void SelectAll(Vector3 camRing, Vector3 U, Vector3 N, Vector3 W)
    {
        foreach (var l in _layers) SelectZones(l, camRing, U, N, W);
        _selValid = true;
    }

    /// <summary>Scale factor 0..1 of a rock (centre in ring coordinates, drawn radius
    /// <paramref name="size"/>) from the selected clear zones. Mirrors clearFactor() in
    /// rock.vert.glsl. The shrink band is at least 1.3 × the size, so the shrunken body
    /// never reaches into a zone.</summary>
    private static float ClearFactor(Layer l, Vector3 c, float size, uint th)
    {
        float f = 1f;
        if (l.SelCount == 0) return f;
        float band = size * (1.3f + 0.6f * Rnd(th, 14)) + 0.03f;
        var s = l.Sel;
        for (int j = 0; j < l.SelCount; j++)
        {
            float dx = c.X - s[j * 4], dy = c.Y - s[j * 4 + 1], dz = c.Z - s[j * 4 + 2];
            float d = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            float t = Math.Clamp((d - s[j * 4 + 3]) / band, 0f, 1f);
            f = MathF.Min(f, Mix(1f, t * t * (3f - 2f * t), l.SelK[j]));
        }
        return f;
    }

    /// <summary>Nothing to integrate: tumbling is driven by <see cref="FlightContext.Time"/> in the shader.</summary>
    public void Update(FlightContext ctx) { }

    /// <summary>Draws the field around the camera (opaque, depth-tested, back faces culled).</summary>
    public void Draw(Camera cam, FlightContext ctx)
    {
        if (_shader == null || !ctx.HasRing || ctx.RingOuter <= ctx.RingInner) return;

        Basis(ctx, out var U, out var N, out var W);
        Vector3 eye = cam.Eye;
        Vector3 d = eye - ctx.AnchorPosition;
        var camRing = new Vector3(Vector3.Dot(d, U), Vector3.Dot(d, N), Vector3.Dot(d, W));
        _lastCamRing = camRing;
        _hasCam = true;
        SelectAll(camRing, U, N, W);

        // Skip entirely when the camera is far from the ring band.
        float rc = new Vector2(camRing.X, camRing.Z).Length;
        if (MathF.Abs(camRing.Y) > 8f || rc < ctx.RingInner - 8f || rc > ctx.RingOuter + 8f) return;

        var viewRot = cam.ViewMatrix;
        viewRot.Row3 = new Vector4(0, 0, 0, 1);
        float pxPerRad = _viewport.Y / (2f * MathF.Tan(MathHelper.DegreesToRadians(cam.FovDeg) * 0.5f));

        bool depth = GL.IsEnabled(EnableCap.DepthTest);
        bool cull = GL.IsEnabled(EnableCap.CullFace);
        bool blend = GL.IsEnabled(EnableCap.Blend);
        GL.GetBoolean(GetPName.DepthWritemask, out bool depthMask);
        GL.GetInteger(GetPName.CullFaceMode, out int cullMode);
        GL.GetInteger(GetPName.FrontFace, out int frontFace);

        GL.Enable(EnableCap.DepthTest);
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(CullFaceMode.Back);
        GL.FrontFace(FrontFaceDirection.Ccw);

        var sh = _shader;
        sh.Use();
        sh.SetMatrix4("uProj", cam.ProjectionMatrix);
        sh.SetMatrix4("uViewRot", viewRot);
        sh.SetFloat("uFcoef", 2.0f / MathF.Log2(cam.Far + 1.0f));
        sh.SetVector3("uCamRing", camRing);
        sh.SetVector3("uBasisU", U);
        sh.SetVector3("uBasisN", N);
        sh.SetVector3("uBasisW", W);
        sh.SetFloat("uRingInner", ctx.RingInner);
        sh.SetFloat("uRingOuter", ctx.RingOuter);
        sh.SetFloat("uTime", ctx.Time);
        sh.SetFloat("uPxPerRad", pxPerRad);
        sh.SetVector3("uSunRel", ctx.SunPosition - eye);
        sh.SetVector3("uAnchorRel", ctx.AnchorPosition - eye);
        sh.SetFloat("uAnchorRadius", ctx.AnchorRadius);
        sh.SetVector3("uAnchorColor", ctx.AnchorColor);
        sh.SetVector3("uSunColor", new Vector3(1.0f, 0.96f, 0.88f) * 1.15f);
        int seedLoc = GL.GetUniformLocation(sh.Handle, "uSeed");
        int zoneKLoc = GL.GetUniformLocation(sh.Handle, "uZoneK");

        GL.BindVertexArray(_vao);
        foreach (var l in _layers)
        {
            sh.SetInt("uZoneCount", l.SelCount);
            if (l.SelCount > 0)
            {
                sh.SetVector4Array("uZone", l.Sel, l.SelCount);
                GL.Uniform1(zoneKLoc, l.SelCount, l.SelK);
            }
            sh.SetFloat("uPeriod", l.Period);
            sh.SetFloat("uSizeMin", l.SizeMin);
            sh.SetFloat("uSizeMax", l.SizeMax);
            sh.SetFloat("uSizeGamma", l.SizeGamma);
            sh.SetFloat("uDensity", l.Density);
            sh.SetFloat("uThick", l.Thick);
            sh.SetFloat("uMinPx", l.MinPx);
            for (int s = 0; s < l.Variants.Length; s++)
            {
                int v = l.Variants[s];
                GL.Uniform1(seedLoc, SeedOf(l, s));
                GL.DrawArraysInstanced(PrimitiveType.Triangles, _variantFirst[v], _variantCount[v], l.PerVariant);
            }
        }
        GL.BindVertexArray(0);

        if (!depth) GL.Disable(EnableCap.DepthTest);
        GL.DepthMask(depthMask);
        if (blend) GL.Enable(EnableCap.Blend);
        if (!cull) GL.Disable(EnableCap.CullFace);
        GL.CullFace((CullFaceMode)cullMode);
        GL.FrontFace((FrontFaceDirection)frontFace);
    }

    /// <summary>Does a sphere at <paramref name="worldPos"/> touch a rock? When it does,
    /// <paramref name="pushOut"/> is the shortest world-space move that frees it (summed
    /// over every rock it overlaps). Only rocks with a radius of at least
    /// <see cref="MinCollideSize"/> are tested, against a sphere of
    /// <see cref="CollideRadiusFactor"/> × their drawn size.</summary>
    public bool Collide(Vector3 worldPos, float radius, FlightContext ctx, out Vector3 pushOut)
    {
        pushOut = Vector3.Zero;
        if (_shader == null || !ctx.HasRing || ctx.RingOuter <= ctx.RingInner) return false;

        Basis(ctx, out var U, out var N, out var W);
        Vector3 d = worldPos - ctx.AnchorPosition;
        var p = new Vector3(Vector3.Dot(d, U), Vector3.Dot(d, N), Vector3.Dot(d, W));
        if (MathF.Abs(p.Y) > 0.5f) return false;
        var cam = _hasCam ? new Vector2(_lastCamRing.X, _lastCamRing.Z) : new Vector2(p.X, p.Z);
        // Same zone selection as the last Draw (what is on screen); only rebuilt here when
        // the zones changed since or nothing was drawn yet.
        if (!_selValid) SelectAll(_hasCam ? _lastCamRing : p, U, N, W);

        Vector3 push = Vector3.Zero;
        bool hit = false;
        foreach (var l in _layers)
        {
            if (!l.Collidable) continue;
            float R = 0.5f * l.Period;
            for (int s = 0; s < l.Variants.Length; s++)
            {
                var ids = l.IdHash[s];
                var offs = l.Offset[s];
                for (int i = 0; i < ids.Length; i++)
                {
                    // The drawn copy is the one nearest the camera; the ship is close to the
                    // camera, so pick by the camera exactly like the shader does.
                    Vector2 o = offs[i];
                    float kx = MathF.Floor((cam.X - o.X) / l.Period + 0.5f);
                    float ky = MathF.Floor((cam.Y - o.Y) / l.Period + 0.5f);
                    float qx = o.X + kx * l.Period, qy = o.Y + ky * l.Period;
                    float dx = p.X - qx, dz = p.Z - qy;
                    if (dx * dx + dz * dz > 0.2f) continue; // > ~0.45 units: cannot touch
                    uint th = Hash(ids[i] ^ Hash(unchecked((uint)(int)kx * 73856093u ^ (uint)(int)ky * 19349663u)));
                    float dens = RingDensity(MathF.Sqrt(qx * qx + qy * qy), ctx.RingInner, ctx.RingOuter) * l.Density;
                    if (!(Rnd(th, 0) < dens)) continue;
                    float size = l.SizeMin * MathF.Pow(l.SizeMax / l.SizeMin, MathF.Pow(Rnd(th, 1), l.SizeGamma));
                    if (size < MinCollideSize) continue;
                    float fadeD = new Vector2(qx - cam.X, qy - cam.Y).Length;
                    float fade = 1f - SmoothStep(0.72f * R, 0.97f * R, fadeD);
                    if (fade <= 0.01f) continue;
                    float hs = Rnd(th, 2) < 0.85f ? 0.04f : 0.12f;
                    float hgt = (Rnd(th, 3) - 0.5f) * hs * l.Thick;
                    float clear = ClearFactor(l, new Vector3(qx, hgt, qy), size, th);
                    if (clear <= 0.01f) continue;
                    var delta = new Vector3(dx, p.Y - hgt, dz);
                    float rr = radius + size * fade * clear * CollideRadiusFactor;
                    float dist2 = delta.LengthSquared;
                    if (dist2 >= rr * rr) continue;
                    float dist = MathF.Sqrt(dist2);
                    var dir = dist > 1e-6f ? delta / dist : Vector3.UnitY;
                    push += dir * (rr - dist);
                    hit = true;
                }
            }
        }
        if (hit) pushOut = U * push.X + N * push.Y + W * push.Z;
        return hit;
    }

    /// <summary>Releases the GL objects.</summary>
    public void Dispose()
    {
        _shader?.Dispose();
        _shader = null;
        if (_vbo != 0) GL.DeleteBuffer(_vbo);
        if (_vao != 0) GL.DeleteVertexArray(_vao);
        _vbo = _vao = 0;
    }

    // ---- Shared field maths (mirrors rock.vert.glsl) -----------------------------------

    private static uint SeedOf(Layer l, int slot) => l.SeedBase * 7919u + (uint)slot * 104729u;

    private static uint Hash(uint x)
    {
        unchecked
        {
            x ^= x >> 16; x *= 0x7feb352du;
            x ^= x >> 15; x *= 0x846ca68bu;
            x ^= x >> 16; return x;
        }
    }

    private static float Rnd(uint h, uint n) => (Hash(unchecked(h + n * 0x68e31da4u)) >> 8) * (1f / 16777216f);

    private static float SmoothStep(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float Mix(float a, float b, float t) => a + (b - a) * t;

    private static float RingDensity(float r, float inner, float outer)
    {
        float t = (r - inner) / (outer - inner);
        if (t <= 0f || t >= 1f) return 0f;
        float d = SmoothStep(0f, 0.06f, t) * (1f - SmoothStep(0.93f, 1f, t));
        d *= Mix(0.35f, 1f, SmoothStep(0.22f, 0.28f, t));
        d *= Mix(1f, 0.04f, SmoothStep(0.62f, 0.64f, t) * (1f - SmoothStep(0.69f, 0.71f, t)));
        d *= Mix(1f, 0.75f, SmoothStep(0.69f, 0.71f, t));
        d *= 1f - (SmoothStep(0.885f, 0.893f, t) * (1f - SmoothStep(0.903f, 0.911f, t)));
        d *= 0.68f + 0.32f * MathF.Sin(t * 157f + MathF.Sin(t * 41f) * 2f);
        return d;
    }

    /// <summary>Stable orthonormal right-handed basis of the ring plane: U, W in the plane, N the normal.</summary>
    private static void Basis(FlightContext ctx, out Vector3 U, out Vector3 N, out Vector3 W)
    {
        N = ctx.RingNormal.LengthSquared > 1e-8f ? ctx.RingNormal.Normalized() : Vector3.UnitY;
        Vector3 refAxis = MathF.Abs(N.X) < 0.9f ? Vector3.UnitX : Vector3.UnitZ;
        U = (refAxis - N * Vector3.Dot(refAxis, N)).Normalized();
        W = Vector3.Cross(U, N);
    }

    // ---- Mesh generation ---------------------------------------------------------------

    /// <summary>Appends one rock variant (CCW outward triangles) as position, flat face
    /// normal and smoothed normal (9 floats per vertex).</summary>
    private static void BuildRock(int variant, List<float> output)
    {
        var rng = new Random(9001 + variant * 131);
        int subdiv = variant is 0 or 3 ? 1 : 2;
        Icosphere(subdiv, out var pos, out var tris);

        // Shape parameters per variant.
        Vector3 stretch = variant switch
        {
            0 => new Vector3(1.0f, 0.75f, 1.35f),   // elongated chunk
            1 => new Vector3(1.0f, 0.9f, 1.05f),    // blocky boulder
            2 => new Vector3(1.25f, 0.55f, 1.0f),   // flat slab
            3 => new Vector3(1.0f, 0.85f, 0.9f),    // jagged pebble
            _ => new Vector3(1.5f, 0.8f, 0.85f),    // two-lobed
        };
        int bumps = variant == 3 ? 10 : 7;
        int cuts = variant switch { 0 => 6, 1 => 9, 2 => 6, 3 => 5, _ => 6 };

        var bumpDir = new Vector3[bumps];
        var bumpAmp = new float[bumps];
        var bumpK = new float[bumps];
        for (int b = 0; b < bumps; b++)
        {
            bumpDir[b] = RandomDir(rng);
            bumpAmp[b] = ((float)rng.NextDouble() - 0.45f) * (variant == 3 ? 0.55f : 0.35f);
            bumpK[b] = 3f + (float)rng.NextDouble() * 9f;
        }
        var cutN = new Vector3[cuts];
        var cutD = new float[cuts];
        for (int c = 0; c < cuts; c++)
        {
            cutN[c] = RandomDir(rng);
            cutD[c] = 0.42f + (float)rng.NextDouble() * 0.33f;
        }

        for (int i = 0; i < pos.Count; i++)
        {
            Vector3 dir = pos[i];
            float r = 1f;
            for (int b = 0; b < bumps; b++)
                r += bumpAmp[b] * MathF.Exp(-(1f - Vector3.Dot(dir, bumpDir[b])) * bumpK[b]);
            if (variant == 4) // two lobes along X with a waist
                r *= 0.8f + 0.25f * MathF.Abs(dir.X) + 0.05f;
            Vector3 p = dir * r * stretch;
            for (int c = 0; c < cuts; c++)
            {
                float s = Vector3.Dot(p, cutN[c]);
                if (s > cutD[c]) p -= cutN[c] * (s - cutD[c]) * 0.97f;
            }
            pos[i] = p;
        }

        float maxR = 0f;
        foreach (var p in pos) maxR = MathF.Max(maxR, p.Length);
        for (int i = 0; i < pos.Count; i++) pos[i] /= maxR;

        // Face normals (outward) and, per vertex, the faces around it.
        var faceN = new Vector3[tris.Count];
        var faceArea = new float[tris.Count];
        var around = new List<int>[pos.Count];
        for (int i = 0; i < pos.Count; i++) around[i] = [];
        for (int f = 0; f < tris.Count; f++)
        {
            var (a0, b0, c0) = tris[f];
            Vector3 a = pos[a0], b = pos[b0], c = pos[c0];
            Vector3 n = Vector3.Cross(b - a, c - a);
            if (Vector3.Dot(n, a + b + c) < 0f) n = -n;
            faceArea[f] = n.Length;
            faceN[f] = faceArea[f] > 1e-12f ? n / faceArea[f] : (a + b + c).Normalized();
            around[a0].Add(f); around[b0].Add(f); around[c0].Add(f);
        }

        // Layout per vertex: position, flat face normal, smoothed normal. The smoothed one
        // averages only the faces within ~50° of this face (smoothing groups), so the
        // flat cut planes keep crisp edges while the rounded parts lose their facets.
        const float crease = 0.64f;
        for (int f = 0; f < tris.Count; f++)
        {
            var (a0, b0, c0) = tris[f];
            int[] idx = [a0, b0, c0];
            Vector3 a = pos[a0], b = pos[b0], c = pos[c0];
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), a + b + c) < 0f) (idx[1], idx[2]) = (idx[2], idx[1]);
            Vector3 n = faceN[f];
            foreach (int vi in idx)
            {
                Vector3 sm = Vector3.Zero;
                foreach (int g in around[vi])
                    if (Vector3.Dot(faceN[g], n) > crease) sm += faceN[g] * faceArea[g];
                sm = sm.LengthSquared > 1e-12f ? sm.Normalized() : n;
                Vector3 v = pos[vi];
                output.Add(v.X); output.Add(v.Y); output.Add(v.Z);
                output.Add(n.X); output.Add(n.Y); output.Add(n.Z);
                output.Add(sm.X); output.Add(sm.Y); output.Add(sm.Z);
            }
        }
    }

    private static Vector3 RandomDir(Random rng)
    {
        float z = (float)rng.NextDouble() * 2f - 1f;
        float a = (float)rng.NextDouble() * MathF.Tau;
        float s = MathF.Sqrt(1f - z * z);
        return new Vector3(s * MathF.Cos(a), z, s * MathF.Sin(a));
    }

    private static void Icosphere(int subdiv, out List<Vector3> pos, out List<(int, int, int)> tris)
    {
        float t = (1f + MathF.Sqrt(5f)) / 2f;
        pos =
        [
            new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0),
            new(0, -1, t), new(0, 1, t), new(0, -1, -t), new(0, 1, -t),
            new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
        ];
        for (int i = 0; i < pos.Count; i++) pos[i] = pos[i].Normalized();
        tris =
        [
            (0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11),
            (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6), (7, 1, 8),
            (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9),
            (4, 9, 5), (2, 4, 11), (6, 2, 10), (8, 6, 7), (9, 8, 1),
        ];
        for (int s = 0; s < subdiv; s++)
        {
            var cache = new Dictionary<long, int>();
            var next = new List<(int, int, int)>(tris.Count * 4);
            var p = pos;
            int Mid(int a, int b)
            {
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (cache.TryGetValue(key, out int m)) return m;
                p.Add(((p[a] + p[b]) * 0.5f).Normalized());
                cache[key] = p.Count - 1;
                return p.Count - 1;
            }
            foreach (var (a, b, c) in tris)
            {
                int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                next.Add((a, ab, ca)); next.Add((b, bc, ab)); next.Add((c, ca, bc)); next.Add((ab, bc, ca));
            }
            tris = next;
        }
    }
}
