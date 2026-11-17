// PROTOTYPE (2026-11-17): arcade flight mode — the fine ice dust of the ring: a thin,
// sun-lit haze and sparkle hugging the ring plane around the ship.
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SolarSystem;

// How it works (all procedural on the GPU, ringdust.vert/frag; no vertex buffers):
//  * Sparkle: two wrapped grain fields. Every grain has a fixed offset inside a square tile
//    of ring-plane coordinates (relative to the anchor) and is moved to the copy nearest
//    the camera, exactly like RockField. Height (a thin Gaussian slab), size, facet
//    orientation and existence (the ring's density profile) are hashed from
//    (grain, tile), so nothing repeats and nothing depends on frame count: grains glint
//    because the eye moves relative to their facets, and the picture is frozen while
//    paused.
//  * Haze: a few large squares in the ring plane at different heights; each stands for a
//    slice of the slab and adds density * slice thickness / |cos(view, normal)|, so the
//    dust brightens toward the ring's horizon and when the view grazes the plane. Being
//    real geometry it is depth-tested against rocks, ship and planet.
//  * Veil: one camera-facing quad a little beyond the ship; it ray-marches the slab from
//    the eye, giving the short whiteout while the camera punches through the plane (the
//    haze layers close to the eye's own height fade out and hand over to it).
//  * Everything is lit by the Sun with a forward-scattering phase function and is dark in
//    Saturn's shadow (soft ray-vs-sphere test).

/// <summary>Additive dust / sparkle layer in the anchor planet's ring plane; drawn inside
/// the HDR scene pass after every opaque object and after the translucent ring.</summary>
public sealed class RingDust : IDisposable
{
    private readonly record struct GrainLayer(float Period, int Count, float Size, float Gain, uint Seed);

    private static readonly GrainLayer[] Grains =
    [
        new(0.7f, 12000, 0.00025f, 12f, 101u),   // close glitter around the ship
        new(2.2f, 12000, 0.00060f, 7f, 202u),    // farther, coarser sparkle
    ];

    // Haze layer heights (world units from the plane) and the slice each one stands for.
    private static readonly float[] LayerH = [-0.0625f, -0.0375f, -0.0125f, 0.0125f, 0.0375f, 0.0625f];
    private const float LayerSpacing = 0.025f;
    private readonly float[] _layerW = new float[LayerH.Length];

    private const float HazeGain = 0.9f;
    private const float MuMin = 0.06f;
    private const float VeilDist = 0.3f;     // beyond the ship (the camera sits ~0.15 behind it)
    private const float VeilGain = 1.3f;

    private ShaderProgram? _shader;
    private int _vao;
    private Vector2 _viewport = new(1600, 900);

    /// <summary>Creates the shader and an empty vertex array (all geometry is generated in
    /// the vertex shader); needs a current GL context.</summary>
    public void Initialize()
    {
        _shader = ShaderSources.CreateProgram("ringdust.vert", "ringdust.frag");
        _vao = GL.GenVertexArray();
        for (int i = 0; i < LayerH.Length; i++) _layerW[i] = Slab(LayerH[i]) * LayerSpacing;
    }

    /// <summary>Framebuffer size in pixels; called at start-up and on every resize.</summary>
    public void SetViewport(Vector2 viewport)
    {
        if (viewport.X > 0 && viewport.Y > 0) _viewport = viewport;
    }

    /// <summary>Nothing to integrate: the dust is a pure function of the camera position,
    /// the Sun and the ring, so it is stable while paused.</summary>
    public void Update(FlightContext ctx) { }

    /// <summary>Draws sparkle, haze and the crossing veil. Additive, depth test on, depth
    /// write off; changes and then restores blend, depth and cull-face state.</summary>
    public void Draw(Camera cam, FlightContext ctx)
    {
        if (_shader == null || !ctx.HasRing || ctx.RingOuter <= ctx.RingInner) return;

        Basis(ctx, out var U, out var N, out var W);
        Vector3 eye = cam.Eye;
        Vector3 d = eye - ctx.AnchorPosition;
        var camRing = new Vector3(Vector3.Dot(d, U), Vector3.Dot(d, N), Vector3.Dot(d, W));
        float rc = new Vector2(camRing.X, camRing.Z).Length;
        if (MathF.Abs(camRing.Y) > 4f || rc < ctx.RingInner - 4f || rc > ctx.RingOuter + 4f) return;

        Matrix4 view = cam.ViewMatrix;
        var viewRot = view;
        viewRot.Row3 = new Vector4(0, 0, 0, 1);
        var camRight = new Vector3(view.M11, view.M21, view.M31);
        var camUp = new Vector3(view.M12, view.M22, view.M32);
        var camFwd = -new Vector3(view.M13, view.M23, view.M33);
        float fovRad = MathHelper.DegreesToRadians(cam.FovDeg);
        float tanHalf = MathF.Tan(fovRad * 0.5f);
        float pxPerRad = _viewport.Y / (2f * tanHalf);
        float aspect = _viewport.X / _viewport.Y;

        // Colours: icy white with a hint of the ring's tan, in sunlight.
        var sun = new Vector3(1.0f, 0.96f, 0.88f) * 1.15f;
        var ice = sun * new Vector3(0.90f, 0.96f, 1.05f);
        var anchorCol = ctx.AnchorColor.LengthSquared > 1e-6f ? ctx.AnchorColor : new Vector3(0.8f, 0.7f, 0.55f);
        var tan = sun * Vector3.Lerp(anchorCol / MathF.Max(MathF.Max(anchorCol.X, anchorCol.Y), MathF.Max(anchorCol.Z, 1e-3f)),
            new Vector3(0.95f), 0.35f);
        float sunSide = Vector3.Dot(ctx.SunPosition - ctx.AnchorPosition, N);
        float litSide = MathF.Sign(sunSide) == MathF.Sign(camRing.Y) ? 1f : 0f;

        bool blend = GL.IsEnabled(EnableCap.Blend);
        bool depthTest = GL.IsEnabled(EnableCap.DepthTest);
        bool cull = GL.IsEnabled(EnableCap.CullFace);
        GL.GetBoolean(GetPName.DepthWritemask, out bool depthMask);
        GL.GetInteger(GetPName.BlendSrcRgb, out int srcRgb);
        GL.GetInteger(GetPName.BlendDstRgb, out int dstRgb);
        GL.GetInteger(GetPName.BlendSrcAlpha, out int srcA);
        GL.GetInteger(GetPName.BlendDstAlpha, out int dstA);
        GL.GetInteger(GetPName.BlendEquationRgb, out int eqRgb);
        GL.GetInteger(GetPName.BlendEquationAlpha, out int eqA);

        GL.Enable(EnableCap.Blend);
        GL.BlendEquation(BlendEquationMode.FuncAdd);
        GL.BlendFunc(BlendingFactor.One, BlendingFactor.One);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthMask(false);
        GL.Disable(EnableCap.CullFace);

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
        sh.SetFloat("uPxPerRad", pxPerRad);
        sh.SetVector3("uCamRight", camRight);
        sh.SetVector3("uCamUp", camUp);
        sh.SetVector3("uSunRel", ctx.SunPosition - eye);
        sh.SetVector3("uAnchorRel", ctx.AnchorPosition - eye);
        sh.SetFloat("uAnchorRadius", ctx.AnchorRadius);
        sh.SetVector3("uIceColor", ice);
        sh.SetVector3("uTanColor", tan);
        sh.SetFloat("uLitSide", litSide);

        GL.BindVertexArray(_vao);

        // 1) Haze layers (only worth drawing near the slab or when looking across it).
        sh.SetInt("uMode", 1);
        GL.Uniform1(GL.GetUniformLocation(sh.Handle, "uLayerH"), LayerH.Length, LayerH);
        GL.Uniform1(GL.GetUniformLocation(sh.Handle, "uLayerW"), _layerW.Length, _layerW);
        sh.SetFloat("uLayerExtent", ctx.RingOuter * 1.02f);
        sh.SetFloat("uHazeGain", HazeGain);
        sh.SetFloat("uMuMin", MuMin);
        GL.DrawArraysInstanced(PrimitiveType.Triangles, 0, 6, LayerH.Length);

        // 2) Crossing veil: only while the eye is inside the slab.
        if (MathF.Abs(camRing.Y) < 0.16f)
        {
            sh.SetInt("uMode", 2);
            float hy = VeilDist * tanHalf * 1.15f, hx = hy * aspect;
            sh.SetVector3("uVeilCenter", camFwd * VeilDist);
            sh.SetVector3("uVeilRight", camRight * hx);
            sh.SetVector3("uVeilUp", camUp * hy);
            sh.SetFloat("uVeilDist", VeilDist);
            sh.SetFloat("uVeilGain", VeilGain);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
        }

        // 3) Sparkle grains (only when the slab is within reach).
        if (MathF.Abs(camRing.Y) < 1.2f)
        {
            sh.SetInt("uMode", 0);
            int seedLoc = GL.GetUniformLocation(sh.Handle, "uSeed");
            foreach (var g in Grains)
            {
                GL.Uniform1(seedLoc, g.Seed);
                sh.SetFloat("uPeriod", g.Period);
                sh.SetFloat("uGrainSize", g.Size);
                sh.SetFloat("uGrainGain", g.Gain);
                GL.DrawArraysInstanced(PrimitiveType.Triangles, 0, 6, g.Count);
            }
        }

        GL.BindVertexArray(0);

        GL.BlendEquationSeparate((BlendEquationMode)eqRgb, (BlendEquationMode)eqA);
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
        if (_vao != 0) GL.DeleteVertexArray(_vao);
        _vao = 0;
    }

    /// <summary>Vertical density profile of the dust slab (1 at the plane); mirrors slab()
    /// in ringdust.frag.glsl.</summary>
    private static float Slab(float h)
        => 0.82f * MathF.Exp(-h * h / (2f * 0.018f * 0.018f)) + 0.18f * MathF.Exp(-h * h / (2f * 0.05f * 0.05f));

    /// <summary>Same stable ring-plane basis as <see cref="RockField"/>: U, W in the plane, N the normal.</summary>
    private static void Basis(FlightContext ctx, out Vector3 U, out Vector3 N, out Vector3 W)
    {
        N = ctx.RingNormal.LengthSquared > 1e-8f ? ctx.RingNormal.Normalized() : Vector3.UnitY;
        Vector3 refAxis = MathF.Abs(N.X) < 0.9f ? Vector3.UnitX : Vector3.UnitZ;
        U = (refAxis - N * Vector3.Dot(refAxis, N)).Normalized();
        W = Vector3.Cross(U, N);
    }
}
