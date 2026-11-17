// PROTOTYPE (2026-11-17): arcade flight mode — engine exhaust, light trails, boost shock
// ring and the sense of speed (dust / ice streaks around the camera).
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SolarSystem;

/// <summary>Additive effects around the ship; drawn inside the HDR scene pass after
/// every opaque object, so bright parts feed the bloom.</summary>
/// <remarks>
/// Everything is rebuilt on the CPU every frame into one dynamic vertex buffer and drawn
/// with a single call (<c>shipfx.vert/frag</c>): additive ONE/ONE blending, depth test on,
/// depth write off. Vertices are stored relative to the camera eye for precision.
/// Trail history and the boost shock ring live in anchor-relative coordinates, because
/// the anchor planet (and the ship with it) moves every frame.
/// </remarks>
public sealed class ShipFx : IDisposable
{
    // ---- Tuning ----------------------------------------------------------------------
    private const float TrailLife = 1.6f;            // seconds an engine trail persists
    private const float TipTrailLife = 0.9f;         // wing-tip trails are shorter
    private const float SampleInterval = 1f / 90f;   // history spacing (seconds)
    private const int MaxSamples = 200;              // > TrailLife / SampleInterval
    private const int Emitters = 4;                  // 0,1 engines; 2,3 wing tips

    private const int MoteCount = 520;
    private const float MoteBox = 0.7f;              // side of the wrapped cube around the eye

    private const float IgniteDuration = 0.75f;

    private const int FloatsPerVertex = 10;          // pos3 uv2 col4 mode1
    private const int ModeRibbon = 0, ModePlume = 1, ModeGlow = 2, ModeRing = 3, ModeStreak = 4, ModeFlare = 5;

    // ---- GL ----------------------------------------------------------------------------
    private ShaderProgram? _shader;
    private int _vao, _vbo, _vboCapacity;
    private float[] _verts = new float[FloatsPerVertex * 6 * 2048];
    private int _vertCount;
    private Vector2 _viewport = new(1600, 900);

    // ---- Trail history (anchor-relative) ------------------------------------------------
    private readonly Vector3[] _hist = new Vector3[MaxSamples * Emitters];
    private readonly float[] _histTime = new float[MaxSamples];
    private readonly float[] _histEngine = new float[MaxSamples];   // engine trail intensity at emission
    private readonly float[] _histTip = new float[MaxSamples];      // wing-tip trail intensity
    private int _histStart, _histCount;
    private float _lastTime = -1f;

    // ---- Boost ignition ------------------------------------------------------------------
    private float _prevBoost;
    private float _igniteTime = -100f;
    private Vector3 _igniteCenterRel, _igniteRight, _igniteUp;

    // ---- Dust motes (anchor-relative, wrapped around the eye) ---------------------------
    private readonly Vector3[] _motes = new Vector3[MoteCount];
    private readonly float[] _moteBright = new float[MoteCount];

    // ---- Per-draw scratch -----------------------------------------------------------------
    private Vector3 _eye;
    private float _pxPerRadian = 900f;
    private readonly Vector3[] _ribbonPts = new Vector3[MaxSamples + 2];
    private readonly float[] _ribbonAge = new float[MaxSamples + 2];
    private readonly float[] _ribbonInt = new float[MaxSamples + 2];

    /// <summary>Creates the shader and the dynamic vertex buffer; needs a current GL context.</summary>
    public void Initialize()
    {
        _shader = ShaderSources.CreateProgram("shipfx.vert", "shipfx.frag");

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        _vboCapacity = _verts.Length;
        GL.BufferData(BufferTarget.ArrayBuffer, _vboCapacity * sizeof(float), IntPtr.Zero, BufferUsageHint.StreamDraw);
        int stride = FloatsPerVertex * sizeof(float);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride, 5 * sizeof(float));
        GL.EnableVertexAttribArray(3);
        GL.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, stride, 9 * sizeof(float));
        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);

        SeedMotes();
    }

    /// <summary>Framebuffer size in pixels; called at start-up and on every resize.</summary>
    public void SetViewport(Vector2 viewport)
    {
        if (viewport.X > 0 && viewport.Y > 0) _viewport = viewport;
    }

    /// <summary>Flight mode just started (or the ship was re-placed): forget old particles.</summary>
    public void Reset(FlightContext ctx)
    {
        ClearHistory();
        _prevBoost = ctx.Boost01;
        _igniteTime = -100f;
        _lastTime = ctx.Time;
        SeedMotes();
    }

    /// <summary>Advance the effects by <see cref="FlightContext.Dt"/>.</summary>
    public void Update(FlightContext ctx)
    {
        if (ctx.Time < _lastTime - 1e-4f) ClearHistory();   // flight restarted
        _lastTime = ctx.Time;
        if (ctx.Dt <= 0f) return;                            // paused: freeze everything

        // Boost ignition: Boost01 starts rising.
        if (ctx.Boost01 > 0.08f && _prevBoost <= 0.08f && ctx.Time - _igniteTime > 0.6f)
        {
            _igniteTime = ctx.Time;
            Vector3 mid = (ctx.ShipPoint(ShipLayout.EngineNozzles[0]) + ctx.ShipPoint(ShipLayout.EngineNozzles[1])) * 0.5f;
            _igniteCenterRel = mid - ctx.ShipForward * (ShipLayout.Length * 0.15f) - ctx.AnchorPosition;
            _igniteRight = ctx.ShipRight;
            _igniteUp = ctx.ShipUp;
        }
        _prevBoost = ctx.Boost01;

        // Teleport guard: a jump far beyond what the ship can fly clears the trails.
        if (_histCount > 0)
        {
            int newest = (_histStart + _histCount - 1) % MaxSamples;
            Vector3 cur = ctx.ShipPoint(ShipLayout.EngineNozzles[0]) - ctx.AnchorPosition;
            float jump = (cur - _hist[newest * Emitters]).Length;
            float dtSince = MathF.Max(ctx.Time - _histTime[newest], ctx.Dt);
            if (jump > 0.05f + 4f * MathF.Max(ctx.Speed, 1.5f) * dtSince) ClearHistory();
        }

        if (_histCount == 0 || ctx.Time - _histTime[(_histStart + _histCount - 1) % MaxSamples] >= SampleInterval)
            AddSample(ctx);

        // Drop expired samples.
        while (_histCount > 0 && ctx.Time - _histTime[_histStart] > TrailLife)
        {
            _histStart = (_histStart + 1) % MaxSamples;
            _histCount--;
        }
    }

    /// <summary>Draws all ship effects. Changes and then restores blend, depth-mask,
    /// depth-test and cull-face state.</summary>
    public void Draw(Camera cam, FlightContext ctx)
    {
        if (_shader == null) return;

        _eye = cam.Eye;
        float fovRad = MathHelper.DegreesToRadians(cam.FovDeg);
        _pxPerRadian = _viewport.Y / (2f * MathF.Tan(fovRad * 0.5f));
        Matrix4 view = cam.ViewMatrix;
        var camRight = new Vector3(view.M11, view.M21, view.M31);
        var camUp = new Vector3(view.M12, view.M22, view.M32);

        _vertCount = 0;
        BuildMotes(ctx);
        BuildTrails(ctx);
        BuildPlumes(ctx, camRight, camUp);
        BuildIgnition(ctx, camRight, camUp);
        if (_vertCount == 0) return;

        // Upload (orphan + refill).
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        int floats = _vertCount * FloatsPerVertex;
        if (floats > _vboCapacity)
        {
            _vboCapacity = Math.Max(floats, _vboCapacity * 2);
        }
        GL.BufferData(BufferTarget.ArrayBuffer, _vboCapacity * sizeof(float), IntPtr.Zero, BufferUsageHint.StreamDraw);
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, floats * sizeof(float), _verts);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);

        // Save state.
        bool blend = GL.IsEnabled(EnableCap.Blend);
        bool depthTest = GL.IsEnabled(EnableCap.DepthTest);
        bool cull = GL.IsEnabled(EnableCap.CullFace);
        GL.GetBoolean(GetPName.DepthWritemask, out bool depthMask);
        GL.GetInteger(GetPName.BlendSrcRgb, out int srcRgb);
        GL.GetInteger(GetPName.BlendDstRgb, out int dstRgb);
        GL.GetInteger(GetPName.BlendSrcAlpha, out int srcA);
        GL.GetInteger(GetPName.BlendDstAlpha, out int dstA);

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.One, BlendingFactor.One);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthMask(false);
        GL.Disable(EnableCap.CullFace);

        // Eye-relative view: world = rel + eye, so V' = T(eye) * V (row-vector convention).
        _shader.Use();
        _shader.SetMatrix4("uView", Matrix4.CreateTranslation(_eye) * view);
        _shader.SetMatrix4("uProj", cam.ProjectionMatrix);
        _shader.SetFloat("uFcoef", 2.0f / MathF.Log2(cam.Far + 1.0f));
        _shader.SetFloat("uTime", ctx.Time);
        _shader.SetFloat("uBoost", Math.Clamp(ctx.Boost01, 0f, 1f));

        GL.BindVertexArray(_vao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, _vertCount);
        GL.BindVertexArray(0);

        // Restore state.
        GL.BlendFuncSeparate((BlendingFactorSrc)srcRgb, (BlendingFactorDest)dstRgb,
            (BlendingFactorSrc)srcA, (BlendingFactorDest)dstA);
        if (!blend) GL.Disable(EnableCap.Blend);
        if (!depthTest) GL.Disable(EnableCap.DepthTest);
        if (cull) GL.Enable(EnableCap.CullFace);
        GL.DepthMask(depthMask);
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

    // =====================================================================================
    // Simulation helpers
    // =====================================================================================

    private void ClearHistory()
    {
        _histStart = 0;
        _histCount = 0;
    }

    private void AddSample(FlightContext ctx)
    {
        if (_histCount == MaxSamples)
        {
            _histStart = (_histStart + 1) % MaxSamples;
            _histCount--;
        }
        int i = (_histStart + _histCount) % MaxSamples;
        _histCount++;
        _histTime[i] = ctx.Time;
        _histEngine[i] = EngineTrailIntensity(ctx);
        _histTip[i] = TipTrailIntensity(ctx);
        _hist[i * Emitters + 0] = ctx.ShipPoint(ShipLayout.EngineNozzles[0]) - ctx.AnchorPosition;
        _hist[i * Emitters + 1] = ctx.ShipPoint(ShipLayout.EngineNozzles[1]) - ctx.AnchorPosition;
        _hist[i * Emitters + 2] = ctx.ShipPoint(ShipLayout.WingTips[0]) - ctx.AnchorPosition;
        _hist[i * Emitters + 3] = ctx.ShipPoint(ShipLayout.WingTips[1]) - ctx.AnchorPosition;
    }

    private static float EngineTrailIntensity(FlightContext ctx)
        => 0.3f + 0.5f * Math.Clamp(ctx.Throttle01, 0f, 1f) + 0.8f * Math.Clamp(ctx.Boost01, 0f, 1f);

    private static float TipTrailIntensity(FlightContext ctx)
        => Smooth(0.4f, 0.85f, ctx.Speed01);

    private void SeedMotes()
    {
        var rng = new Random(1234);
        for (int i = 0; i < MoteCount; i++)
        {
            _motes[i] = new Vector3(rng.NextSingle(), rng.NextSingle(), rng.NextSingle()) * MoteBox;
            float b = rng.NextSingle();
            _moteBright[i] = 0.35f + 0.65f * b * b;
        }
    }

    // =====================================================================================
    // Geometry builders
    // =====================================================================================

    /// <summary>World size of one pixel at distance <paramref name="dist"/> from the eye.</summary>
    private float PixelWorld(float dist) => MathF.Max(dist, 1e-5f) / _pxPerRadian;

    private void BuildMotes(FlightContext ctx)
    {
        float speed = ctx.Speed;
        if (speed < 1e-4f) return;
        float s01 = Math.Clamp(ctx.Speed01, 0f, 1f);
        float boost = Math.Clamp(ctx.Boost01, 0f, 1f);

        // Streak length (seconds of travel): short at cruise, long at boost.
        float streakTime = 0.03f + 0.05f * s01 + 0.045f * boost;
        Vector3 tailOffset = ctx.ShipVelocity * streakTime;
        float bright = 0.22f + 0.5f * s01 * s01 + 1.0f * boost;
        var tint = new Vector3(0.75f, 0.88f, 1.0f);

        Vector3 eyeRel = _eye - ctx.AnchorPosition;
        float half = MoteBox * 0.5f;
        for (int i = 0; i < MoteCount; i++)
        {
            Vector3 r = _motes[i] - eyeRel;
            r.X -= MoteBox * MathF.Floor(r.X / MoteBox + 0.5f);
            r.Y -= MoteBox * MathF.Floor(r.Y / MoteBox + 0.5f);
            r.Z -= MoteBox * MathF.Floor(r.Z / MoteBox + 0.5f);
            float d = r.Length;
            // Fade at the box edge (hides wrap pops) and right at the lens.
            float fade = (1f - Smooth(half * 0.55f, half * 0.95f, d)) * Smooth(0.008f, 0.04f, d);
            if (fade <= 0.002f) continue;

            Vector3 head = _eye + r;
            Vector3 tail = head + tailOffset;
            float w = PixelWorld(d) * (0.9f + 0.6f * _moteBright[i]);
            float a = bright * _moteBright[i] * fade;
            AddSegment(head, tail, w, w * 0.6f, new Vector4(tint, a), new Vector4(tint, a), ModeStreak, 0f, 1f);
        }
    }

    private void BuildTrails(FlightContext ctx)
    {
        Vector3 anchor = ctx.AnchorPosition;
        float r0 = ShipLayout.NozzleRadius * ShipLayout.Length;
        float boost = Math.Clamp(ctx.Boost01, 0f, 1f);

        for (int e = 0; e < Emitters; e++)
        {
            bool tip = e >= 2;
            float life = tip ? TipTrailLife : TrailLife;
            Vector3 current = ctx.ShipPoint(tip ? ShipLayout.WingTips[e - 2] : ShipLayout.EngineNozzles[e]);

            // Newest first: the live emitter position, then history back in time.
            int n = 0;
            _ribbonPts[n] = current;
            _ribbonAge[n] = 0f;
            _ribbonInt[n] = tip ? TipTrailIntensity(ctx) : EngineTrailIntensity(ctx);
            n++;
            for (int k = _histCount - 1; k >= 0; k--)
            {
                int i = (_histStart + k) % MaxSamples;
                float age = (ctx.Time - _histTime[i]) / life;
                if (age > 1f) break;
                Vector3 p = _hist[i * Emitters + e] + anchor;
                if ((p - _ribbonPts[n - 1]).LengthSquared < 1e-12f) continue;
                _ribbonPts[n] = p;
                _ribbonAge[n] = MathF.Max(age, 0f);
                _ribbonInt[n] = tip ? _histTip[i] : _histEngine[i];
                n++;
            }
            if (n < 2) continue;

            Vector3 tint = tip ? new Vector3(0.85f, 0.92f, 1.0f) : Vector3.Lerp(new Vector3(0.25f, 0.7f, 1.0f), new Vector3(0.45f, 0.45f, 1.0f), boost * 0.5f);
            float baseW = tip ? r0 * 0.3f : r0 * (0.6f + 0.3f * boost);
            float gain = tip ? 1.3f : 1.2f;

            Vector3 prevSide = Vector3.Zero;
            for (int k = 0; k < n; k++)
            {
                Vector3 p = _ribbonPts[k];
                Vector3 tangent = k < n - 1 ? _ribbonPts[k + 1] - p : p - _ribbonPts[k - 1];
                Vector3 toEye = p - _eye;
                float dist = toEye.Length;
                Vector3 side = Vector3.Cross(tangent, toEye);
                float sl = side.Length;
                side = sl > 1e-12f ? side / sl : (prevSide == Vector3.Zero ? Vector3.UnitY : prevSide);
                if (k > 0 && Vector3.Dot(side, prevSide) < 0f) side = -side;   // no twisting
                prevSide = side;

                float age = _ribbonAge[k];
                // Trails widen and dim as they dissipate; never thinner than ~1.2 px.
                float w = MathF.Max(baseW * (1f + 1.0f * age), PixelWorld(dist) * 1.2f);
                float fade = MathF.Pow(1f - age, 1.6f) * Smooth(0f, 0.04f, age + 0.005f);
                float nearFade = Smooth(0.03f, 0.2f, dist);
                float a = _ribbonInt[k] * gain * fade * nearFade;

                _ribbonScratch[k] = new RibbonPoint(p, side * w, new Vector4(tint, a), age);
            }
            for (int k = 0; k < n - 1; k++)
            {
                var a = _ribbonScratch[k];
                var b = _ribbonScratch[k + 1];
                if (a.Color.W <= 1e-4f && b.Color.W <= 1e-4f) continue;
                EmitQuad(a.P - a.Side, a.P + a.Side, b.P - b.Side, b.P + b.Side,
                    a.Age, b.Age, a.Color, b.Color, ModeRibbon);
            }
        }
    }

    private readonly RibbonPoint[] _ribbonScratch = new RibbonPoint[MaxSamples + 2];

    private readonly record struct RibbonPoint(Vector3 P, Vector3 Side, Vector4 Color, float Age);

    private void BuildPlumes(FlightContext ctx, Vector3 camRight, Vector3 camUp)
    {
        float thr = Math.Clamp(ctx.Throttle01, 0f, 1f);
        float boost = Math.Clamp(ctx.Boost01, 0f, 1f);
        float L = ShipLayout.Length;
        float r0 = ShipLayout.NozzleRadius * L;
        Vector3 back = -ctx.ShipForward;   // ship +Z in world
        float t = ctx.Time;

        // Plume length (world units) and overall brightness.
        float len = L * (0.5f + 1.2f * thr + 2.0f * boost);
        float power = 0.5f + 1.0f * thr + 1.0f * boost;

        for (int e = 0; e < 2; e++)
        {
            float flicker = 0.9f + 0.06f * MathF.Sin(t * 53.1f + e * 1.7f) + 0.04f * MathF.Sin(t * 97.3f + e * 3.1f)
                          + 0.03f * MathF.Sin(t * 211f + e * 5.3f);
            Vector3 nozzle = ctx.ShipPoint(ShipLayout.EngineNozzles[e]);
            Vector3 start = nozzle - back * (r0 * 0.4f);   // start just inside the bell
            Vector3 end = nozzle + back * len * flicker;

            // 1) Axis-aligned billboard: the flame seen from the side.
            Vector3 mid = (start + end) * 0.5f;
            Vector3 toEye = mid - _eye;
            float dist = toEye.Length;
            Vector3 viewDir = dist > 1e-9f ? toEye / dist : -ctx.ShipForward;
            Vector3 side = Vector3.Cross(back, viewDir);
            float sinA = side.Length;
            if (sinA > 1e-5f)
            {
                side /= sinA;
                float halfW = MathF.Max(r0 * (1.2f + 0.25f * boost), PixelWorld(dist) * 1.5f);
                float profileFade = Smooth(0.05f, 0.45f, sinA);
                var col = new Vector4(1f, 1f, 1f, power * flicker * profileFade);
                EmitQuad(start - side * halfW, start + side * halfW, end - side * halfW, end + side * halfW,
                    0f, 1f, col, col, ModePlume);
            }

            // 2) Camera-facing cross-section sprites along the axis: from behind they stack
            //    into a hot disc; from the side they read as shock diamonds under boost.
            const int N = 6;
            float diamonds = 4f + 2.5f * boost;
            for (int i = 0; i < N; i++)
            {
                float x = (i + 0.5f) / N * 0.8f;
                // Under boost snap the sprites onto the shock-diamond nodes.
                float xd = MathF.Round(x * diamonds) / diamonds;
                x = x + (xd - x) * boost;
                if (x <= 0.01f) x = 0.03f;
                Vector3 p = nozzle + back * len * flicker * x;
                float size = r0 * (1.5f - 1.1f * x) * (1f + 0.35f * boost);
                // Boost adds length, not blinding glow: these sprites sit right over the hull
                // from the chase view (was 2.6 * boost).
                float inten = MathF.Pow(1f - x, 2.2f) * (0.25f + 0.5f * thr + 0.9f * boost) * flicker;
                Vector3 c = Vector3.Lerp(new Vector3(1f, 0.97f, 0.93f), new Vector3(0.3f, 0.75f, 1f), Smooth(0f, 0.5f + 0.3f * boost, x));
                EmitSprite(p, camRight, camUp, size, size, new Vector4(c, inten), ModeGlow);
            }

            // 3) Nozzle: a white-hot core, a cyan halo, and an anamorphic streak at boost.
            //    Kept tight at boost so the hull around the nozzles stays readable: the
            //    violence of boost lives in the long plume, the streaks and the radial blur.
            float coreSize = r0 * (1.1f + 0.25f * thr + 0.1f * boost);
            EmitSprite(nozzle, camRight, camUp, coreSize, coreSize,
                new Vector4(1f, 0.97f, 0.94f, (2.4f + 2.6f * thr + 0.6f * boost) * flicker), ModeGlow);

            float haloSize = r0 * (1.8f + 1.4f * thr + 0.3f * boost);
            EmitSprite(nozzle, camRight, camUp, haloSize, haloSize,
                new Vector4(0.3f, 0.65f, 1f, (0.3f + 0.45f * thr + 0.2f * boost) * flicker), ModeGlow);

            float flareW = r0 * (4f + 4f * thr + 7f * boost);
            float flareH = r0 * (0.3f + 0.1f * boost);
            float flareDist = (nozzle - _eye).Length;
            flareH = MathF.Max(flareH, PixelWorld(flareDist) * 1.5f);
            EmitSprite(nozzle, camRight, camUp, flareW, flareH,
                new Vector4(0.35f, 0.6f, 1f, (0.2f + 0.25f * thr + 0.45f * boost) * flicker), ModeFlare);
        }
    }

    private void BuildIgnition(FlightContext ctx, Vector3 camRight, Vector3 camUp)
    {
        float age = ctx.Time - _igniteTime;
        if (age < 0f || age > IgniteDuration) return;
        float u = age / IgniteDuration;
        float L = ShipLayout.Length;

        // Expanding shock ring, fixed in space where the boost lit.
        Vector3 c = _igniteCenterRel + ctx.AnchorPosition;
        float radius = L * (0.25f + 3.2f * MathF.Sqrt(u));
        float ringI = 2.2f * MathF.Pow(1f - u, 2f);
        // The chase camera flies into the ring right after it lights: fade it before it
        // can fill the frame and hide the hull.
        float pxR = radius / MathF.Max((c - _eye).Length, 1e-5f) * _pxPerRadian;
        ringI *= 1f - Smooth(0.18f * _viewport.Y, 0.5f * _viewport.Y, pxR);
        EmitSprite(c, _igniteRight, _igniteUp, radius, radius, new Vector4(0.6f, 0.85f, 1f, ringI), ModeRing);

        // Flash at the nozzles (follows the ship), short and very bright.
        float fu = age / 0.3f;
        if (fu < 1f)
        {
            Vector3 mid = (ctx.ShipPoint(ShipLayout.EngineNozzles[0]) + ctx.ShipPoint(ShipLayout.EngineNozzles[1])) * 0.5f;
            float size = L * (0.2f + 0.4f * fu);
            float fi = 2.0f * (1f - fu) * (1f - fu);
            EmitSprite(mid, camRight, camUp, size, size, new Vector4(0.8f, 0.92f, 1f, fi), ModeGlow);
            EmitSprite(mid, camRight, camUp, size * 4f, size * 0.12f, new Vector4(0.45f, 0.7f, 1f, fi * 0.6f), ModeFlare);
        }
    }

    // =====================================================================================
    // Vertex emission
    // =====================================================================================

    /// <summary>A camera-facing segment from <paramref name="a"/> to <paramref name="b"/>
    /// with half-widths <paramref name="wa"/> / <paramref name="wb"/>.</summary>
    private void AddSegment(Vector3 a, Vector3 b, float wa, float wb, Vector4 ca, Vector4 cb, int mode, float ua, float ub)
    {
        Vector3 dir = b - a;
        Vector3 toEye = (a + b) * 0.5f - _eye;
        Vector3 side = Vector3.Cross(dir, toEye);
        float sl = side.Length;
        if (sl < 1e-14f) return;
        side /= sl;
        EmitQuad(a - side * wa, a + side * wa, b - side * wb, b + side * wb, ua, ub, ca, cb, mode);
    }

    /// <summary>Quad whose first edge (a0,a1) has uv.x = <paramref name="ua"/> and second edge
    /// (b0,b1) uv.x = <paramref name="ub"/>; uv.y runs -1 (…0) to +1 (…1).</summary>
    private void EmitQuad(Vector3 a0, Vector3 a1, Vector3 b0, Vector3 b1, float ua, float ub, Vector4 ca, Vector4 cb, int mode)
    {
        EnsureCapacity(6);
        Vertex(a0, ua, -1f, ca, mode);
        Vertex(b0, ub, -1f, cb, mode);
        Vertex(b1, ub, 1f, cb, mode);
        Vertex(a0, ua, -1f, ca, mode);
        Vertex(b1, ub, 1f, cb, mode);
        Vertex(a1, ua, 1f, ca, mode);
    }

    private void EmitSprite(Vector3 center, Vector3 right, Vector3 up, float halfW, float halfH, Vector4 col, int mode)
    {
        if (col.W <= 1e-4f) return;
        EnsureCapacity(6);
        Vector3 r = right * halfW, u = up * halfH;
        Vector3 p00 = center - r - u, p10 = center + r - u, p01 = center - r + u, p11 = center + r + u;
        Vertex(p00, -1f, -1f, col, mode);
        Vertex(p10, 1f, -1f, col, mode);
        Vertex(p11, 1f, 1f, col, mode);
        Vertex(p00, -1f, -1f, col, mode);
        Vertex(p11, 1f, 1f, col, mode);
        Vertex(p01, -1f, 1f, col, mode);
    }

    private void EnsureCapacity(int extraVerts)
    {
        int need = (_vertCount + extraVerts) * FloatsPerVertex;
        if (need > _verts.Length) Array.Resize(ref _verts, Math.Max(need, _verts.Length * 2));
    }

    private void Vertex(Vector3 world, float u, float v, Vector4 c, int mode)
    {
        int o = _vertCount * FloatsPerVertex;
        Vector3 p = world - _eye;
        var f = _verts;
        f[o] = p.X; f[o + 1] = p.Y; f[o + 2] = p.Z;
        f[o + 3] = u; f[o + 4] = v;
        f[o + 5] = c.X; f[o + 6] = c.Y; f[o + 7] = c.Z; f[o + 8] = c.W;
        f[o + 9] = mode;
        _vertCount++;
    }

    private static float Smooth(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
