// PROTOTYPE (2026-11-17): arcade flight mode — gate race: how the gates and the racing
// line look (energy rings, state colours, pass / miss bursts, beacon and guide chevrons).
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SolarSystem;

/// <summary>Draws the track's gates and the guide to the next one. Additive, inside the
/// HDR scene pass after every opaque object.</summary>
/// <remarks>
/// Everything is rebuilt on the CPU every frame into one dynamic vertex buffer and drawn
/// with a single call (<c>gate.vert/frag</c>): additive ONE/ONE blending, depth test on,
/// depth write off. Vertices are stored relative to the camera eye for precision. All
/// animation runs on <see cref="RaceView.Clock"/>, so a paused flight freezes it.
/// </remarks>
public sealed class RaceGates : IDisposable
{
    // ---- Layout ---------------------------------------------------------------------------
    private const int FloatsPerVertex = 12;          // pos3 uv4 col4 mode1
    private const int ModeRim = 0, ModeMembrane = 1, ModeGlow = 2, ModeStar = 3, ModeShock = 4, ModeChevron = 5, ModeBeam = 6;
    private const float PatRim = 0f, PatArcs = 1f, PatChecker = 2f;

    // ---- Look -----------------------------------------------------------------------------
    private static readonly Vector3 ColNext = new(0.55f, 1.0f, 1.0f);
    private static readonly Vector3 ColUpcoming = new(0.20f, 0.45f, 1.0f);
    private static readonly Vector3 ColPassed = new(0.40f, 1.0f, 0.50f);
    private static readonly Vector3 ColMissed = new(1.0f, 0.14f, 0.08f);
    private static readonly Vector3 ColFinish = new(1.0f, 0.70f, 0.20f);
    private static readonly Vector3 ColGuide = new(0.45f, 0.95f, 1.0f);

    private const float BurstDuration = 1.3f;        // seconds a pass / miss burst lasts
    private const float BeamLength = 0.9f;           // beacon pillar length above and below
    private const float ChevronSpacing = 0.055f;     // guide chevrons, world units apart
    private const int CurveSamples = 48;

    // ---- GL ---------------------------------------------------------------------------------
    private ShaderProgram? _shader;
    private int _vao, _vbo, _vboCapacity;
    private float[] _verts = new float[FloatsPerVertex * 6 * 4096];
    private int _vertCount;
    private Vector2 _viewport = new(1600, 900);

    // ---- Per-gate state changes (Clock time of the last status change) -------------------
    private GateStatus[] _prevStatus = [];
    private float[] _changeClock = [];

    // ---- Per-draw scratch ---------------------------------------------------------------
    private Vector3 _eye, _camRight, _camUp;
    private float _pxPerRadian = 900f;
    private float _clock;
    private readonly Vector3[] _curve = new Vector3[CurveSamples + 1];
    private readonly Vector3[] _curveTan = new Vector3[CurveSamples + 1];
    private readonly float[] _curveLen = new float[CurveSamples + 1];

    /// <summary>Creates the shader and the dynamic vertex buffer; needs a current GL context.</summary>
    public void Initialize()
    {
        _shader = ShaderSources.CreateProgram("gate.vert", "gate.frag");
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
        GL.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride, 7 * sizeof(float));
        GL.EnableVertexAttribArray(3);
        GL.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, stride, 11 * sizeof(float));
        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
    }

    /// <summary>Framebuffer size in pixels; called at start-up and on every resize.</summary>
    public void SetViewport(Vector2 viewport)
    {
        if (viewport.X > 0 && viewport.Y > 0) _viewport = viewport;
    }

    /// <summary>Notices gate status changes (for fade-ins, bursts and fade-outs). Everything
    /// else is a pure function of <see cref="RaceView.Clock"/>.</summary>
    public void Update(FlightContext ctx, RaceView race)
    {
        int n = Math.Min(race.Gates.Count, race.Status.Length);
        if (_prevStatus.Length != n)
        {
            _prevStatus = new GateStatus[n];
            _changeClock = new float[n];
            for (int i = 0; i < n; i++)
            {
                _prevStatus[i] = race.Status[i];
                _changeClock[i] = float.NegativeInfinity;
            }
            return;
        }
        for (int i = 0; i < n; i++)
        {
            var s = race.Status[i];
            if (s == _prevStatus[i]) continue;
            _prevStatus[i] = s;
            bool fromEvent = race.LastEventGate == i && race.LastEvent != RaceEvent.None && race.LastEventClock <= race.Clock;
            _changeClock[i] = fromEvent ? race.LastEventClock : race.Clock;
        }
    }

    /// <summary>Draws gates, bursts, the beacon on the next gate and the guide chevrons.
    /// Changes and then restores blend, depth-mask, depth-test and cull-face state.</summary>
    public void Draw(Camera cam, FlightContext ctx, RaceView race)
    {
        if (_shader == null) return;
        int n = Math.Min(race.Gates.Count, race.Status.Length);
        if (n == 0) return;

        _eye = cam.Eye;
        _clock = race.Clock;
        float fovRad = MathHelper.DegreesToRadians(cam.FovDeg);
        _pxPerRadian = _viewport.Y / (2f * MathF.Tan(fovRad * 0.5f));
        Matrix4 view = cam.ViewMatrix;
        _camRight = new Vector3(view.M11, view.M21, view.M31);
        _camUp = new Vector3(view.M12, view.M22, view.M32);

        _vertCount = 0;
        for (int i = 0; i < n; i++) BuildGate(ctx, race, i);
        BuildBeacon(ctx, race, n);
        BuildGuide(ctx, race, n);
        if (_vertCount == 0) return;

        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        int floats = _vertCount * FloatsPerVertex;
        if (floats > _vboCapacity) _vboCapacity = Math.Max(floats, _vboCapacity * 2);
        GL.BufferData(BufferTarget.ArrayBuffer, _vboCapacity * sizeof(float), IntPtr.Zero, BufferUsageHint.StreamDraw);
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, floats * sizeof(float), _verts);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);

        bool blend = GL.IsEnabled(EnableCap.Blend);
        bool depthTest = GL.IsEnabled(EnableCap.DepthTest);
        bool cull = GL.IsEnabled(EnableCap.CullFace);
        GL.GetBoolean(GetPName.DepthWritemask, out bool depthMask);
        GL.GetInteger(GetPName.BlendSrcRgb, out int srcRgb);
        GL.GetInteger(GetPName.BlendDstRgb, out int dstRgb);
        GL.GetInteger(GetPName.BlendSrcAlpha, out int srcA);
        GL.GetInteger(GetPName.BlendDstAlpha, out int dstA);
        GL.GetInteger(GetPName.CurrentProgram, out int prevProgram);

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.One, BlendingFactor.One);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthMask(false);
        GL.Disable(EnableCap.CullFace);

        _shader.Use();
        _shader.SetMatrix4("uView", Matrix4.CreateTranslation(_eye) * view);
        _shader.SetMatrix4("uProj", cam.ProjectionMatrix);
        _shader.SetFloat("uFcoef", 2.0f / MathF.Log2(cam.Far + 1.0f));
        _shader.SetFloat("uTime", _clock);
        GL.BindVertexArray(_vao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, _vertCount);
        GL.BindVertexArray(0);

        GL.UseProgram(prevProgram);
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
    // Gates
    // =====================================================================================

    /// <summary>Seconds since gate <paramref name="i"/> last changed status (large when unknown).</summary>
    private float Age(RaceView race, int i)
    {
        if (race.LastEventGate == i && race.LastEvent is RaceEvent.GatePassed or RaceEvent.GateMissed or RaceEvent.Finished
            && race.Status[i] is GateStatus.Passed or GateStatus.Missed)
            return MathF.Max(0f, _clock - race.LastEventClock);
        if (i < _changeClock.Length && !float.IsNegativeInfinity(_changeClock[i]))
            return MathF.Max(0f, _clock - _changeClock[i]);
        return 1000f;
    }

    private void BuildGate(FlightContext ctx, RaceView race, int i)
    {
        var g = race.Gates[i];
        Vector3 c = race.GateWorld(i, ctx);
        Basis(g, out var fw, out var up, out var right);
        float R = g.Radius;
        var st = race.Status[i];
        float age = Age(race, i);
        float t = _clock;
        float dist = MathF.Max((c - _eye).Length, 1e-4f);
        float pxR = R / dist * _pxPerRadian;
        int segs = Math.Clamp((int)(pxR * 0.45f), 24, 96);
        int k = i - race.NextGate;
        bool idle = race.Phase == RacePhase.Idle;
        bool finish = g.IsFinish;

        Vector3 tint;
        float rim, arcs, mem, flow, emit, minPx, rot, scale = 1f;
        switch (st)
        {
            case GateStatus.Next:
            {
                float ramp = Smooth(0f, 0.4f, age);
                float pulse = idle
                    ? 0.80f + 0.20f * MathF.Sin(t * MathHelper.TwoPi * 0.5f)
                    : 0.78f + 0.22f * MathF.Sin(t * MathHelper.TwoPi * 1.2f);
                tint = finish ? ColFinish : ColNext;
                rim = Lerp(1.6f, idle ? 4.0f : 5.5f, ramp) * pulse;
                arcs = 2.6f * ramp * pulse;
                mem = 1.0f;
                flow = 1f;
                emit = 3.0f * pulse;
                minPx = 2.6f;
                rot = 0.30f;
                // A gate that just became "next" snaps in from slightly larger.
                scale = 1f + 0.25f * (1f - ramp);
                break;
            }
            case GateStatus.Passed:
            {
                float f = MathF.Exp(-age * 3.2f);
                tint = Vector3.Lerp(finish ? ColFinish : ColPassed, Vector3.One, f * 0.35f);
                float remnant = finish ? 0.5f : 0.07f;
                rim = remnant + 4f * f;
                arcs = remnant * 0.5f + 2.5f * f;
                mem = 0.03f + 0.5f * f;
                flow = 0f;
                emit = remnant + 3f * f;
                minPx = 1.0f;
                rot = 0.08f + 0.6f * f;
                scale = 1f + 0.22f * (1f - MathF.Exp(-age * 5f));
                break;
            }
            case GateStatus.Missed:
            {
                float f = MathF.Exp(-age * 3.5f);
                float flick = age < 0.8f ? 0.55f + 0.45f * MathF.Sin(age * 55f) : 1f;
                tint = ColMissed;
                rim = (0.30f + 5f * f) * flick;
                arcs = (0.08f + 1.6f * f) * flick;
                mem = 0.04f + 0.8f * f;
                flow = 0f;
                emit = (0.15f + 2.5f * f) * flick;
                minPx = 1.2f;
                rot = 0.04f;
                scale = 1f - 0.08f * (1f - MathF.Exp(-age * 4f));
                break;
            }
            default:
            {
                // The two or three after the next one are stronger, so the track's line reads.
                float w = k switch { 1 => 1.0f, 2 => 0.82f, 3 => 0.66f, _ => 0.45f };
                if (race.Phase == RacePhase.Finished) w = 0.3f;
                float fadeIn = Smooth(0f, 0.6f, age);
                w *= 0.3f + 0.7f * fadeIn;
                tint = finish ? ColFinish : ColUpcoming;
                if (finish) w = MathF.Max(w, 0.8f);
                rim = 2.0f * w;
                arcs = 1.0f * w;
                mem = 0.35f * w;
                flow = 0f;
                emit = 1.4f * w;
                minPx = finish ? 2.2f : 1.6f;
                rot = 0.10f;
                break;
            }
        }
        if (finish && st is GateStatus.Next or GateStatus.Upcoming)
        {
            rim *= 1.2f; arcs *= 1.3f; emit *= 1.3f;
        }
        if (finish && race.Phase == RacePhase.Finished && st == GateStatus.Passed)
        {
            // Everything settles; the finish gate keeps a calm gold glow.
            rim = MathF.Max(rim, 0.9f); arcs = MathF.Max(arcs, 0.45f); emit = MathF.Max(emit, 0.8f);
        }

        // The chase camera flies through every gate right behind the ship: soften whatever
        // would otherwise fill the screen for a moment.
        float near = Smooth(R * 0.4f, R * 2.2f, dist);
        rim *= 0.35f + 0.65f * near;
        arcs *= 0.25f + 0.75f * near;
        mem *= near;
        emit *= 0.3f + 0.7f * near;

        // Never let a ring that is about to sweep past the lens wash the frame: fade by how
        // much of the screen the opening covers. Passed / missed gates (which the chase
        // camera flies through right after the burst) fade out completely; the next gate
        // keeps a visible outline to fly through.
        float cover = Cover(R * scale * 1.25f, dist);
        if (st is GateStatus.Passed or GateStatus.Missed)
        {
            rim *= cover; arcs *= cover; mem *= cover; emit *= 0.4f + 0.6f * cover;
        }
        else
        {
            rim *= 0.4f + 0.6f * cover; arcs *= 0.3f + 0.7f * cover; mem *= cover;
        }

        float r = R * scale;
        float phase = t * rot;
        RingTube(c, right, up, r, R * 0.12f, minPx, new Vector4(tint, rim), finish ? PatChecker : PatRim, phase, segs);
        if (arcs > 0.02f)
        {
            var arcTint = Vector3.Lerp(tint, Vector3.One, 0.25f);
            RingTube(c, right, up, r * 1.22f, R * 0.06f, minPx * 0.6f, new Vector4(arcTint, arcs), PatArcs, -phase * 1.7f, segs);
            if (finish)
                RingTube(c, right, up, r * 1.40f, R * 0.05f, minPx * 0.5f, new Vector4(arcTint, arcs * 0.8f), PatArcs, phase * 1.3f + 0.17f, segs);
        }
        if (mem > 0.02f)
            Sprite(c, right, up, r, r, new Vector4(tint, mem), ModeMembrane, flow, finish ? 1f : 0f);

        // Emitters riding on the rim.
        if (emit > 0.04f)
        {
            int count = finish ? 8 : 6;
            float wpp = dist / _pxPerRadian;
            float hs = MathF.Max(R * 0.13f, 3.0f * wpp);
            var eTint = Vector3.Lerp(tint, Vector3.One, 0.35f);
            for (int j = 0; j < count; j++)
            {
                float a = j * MathHelper.TwoPi / count + phase * MathHelper.TwoPi * 0.5f;
                Vector3 p = c + (right * MathF.Cos(a) + up * MathF.Sin(a)) * r * 1.08f;
                Sprite(p, _camRight, _camUp, hs, hs, new Vector4(eTint, emit), ModeGlow, 0f, 0f);
            }
        }

        // Next gate: chevrons streaming in from the approach side and converging on the opening.
        if (st == GateStatus.Next)
        {
            var chTint = Vector3.Lerp(tint, Vector3.One, 0.2f);
            for (int d = 0; d < 4; d++)
            {
                float a = d * MathHelper.PiOver2 + MathHelper.PiOver4;
                Vector3 radial = right * MathF.Cos(a) + up * MathF.Sin(a);
                Vector3 tang = Vector3.Cross(fw, radial);
                Vector3 along = Vector3.Normalize(fw * 0.5f - radial);
                for (int m = 0; m < 2; m++)
                {
                    float ph = Fract(t * 0.9f + m * 0.5f);
                    float rr = R * (1.75f - 0.5f * ph);
                    Vector3 p = c + radial * rr - fw * R * 0.5f * (1f - ph);
                    float inten = MathF.Sin(ph * MathF.PI) * 1.5f * Smooth(R * 2.5f, R * 6f, dist);
                    if (inten > 0.02f)
                        Sprite(p, along, tang, R * 0.09f, R * 0.13f, new Vector4(chTint, inten), ModeChevron, 0f, 0f);
                }
            }
        }

        if (st is GateStatus.Passed or GateStatus.Missed && age < BurstDuration)
            BuildBurst(c, fw, up, right, R, age, st == GateStatus.Passed, finish, i, dist);
    }

    private void BuildBurst(Vector3 c, Vector3 fw, Vector3 up, Vector3 right, float R, float age, bool passed, bool finish, int seed, float dist)
    {
        float life = 1f - age / BurstDuration;
        float l2 = life * life;
        float e = 1f - MathF.Exp(-age * 4.5f);
        Vector3 col = passed ? (finish ? ColFinish : ColPassed) : ColMissed;
        Vector3 hot = Vector3.Lerp(col, Vector3.One, 0.6f);
        float big = finish ? 1.8f : 1f;

        if (passed)
        {
            // Two expanding shock rings in the gate plane and a flash at the centre.
            float h1 = R * (1.1f + 3.0f * big * e);
            Sprite(c, right, up, h1, h1, new Vector4(hot, 2.6f * l2 * Cover(h1, dist)), ModeShock, 0.03f, 0f);
            float a2 = MathF.Max(age - 0.12f, 0f);
            float h2 = R * (1.0f + 2.0f * big * (1f - MathF.Exp(-a2 * 3f)));
            Sprite(c, right, up, h2, h2, new Vector4(col, 1.6f * l2 * Smooth(0f, 0.05f, a2) * Cover(h2, dist)), ModeShock, 0.06f, 0f);
            float hf = R * 1.3f * (0.7f + age * 1.5f);
            float fl = 1.6f * MathF.Exp(-age * 9f) * Cover(hf * 1.6f, dist);
            if (fl > 0.02f)
            {
                Sprite(c, _camRight, _camUp, hf, hf, new Vector4(hot, fl), ModeGlow, 0f, 0f);
            }
        }
        else
        {
            // A red flash that collapses inwards.
            float h1 = R * (1.45f - 0.35f * e);
            Sprite(c, right, up, h1, h1, new Vector4(col, 4f * l2 * (0.3f + 0.7f * Cover(h1, dist))), ModeShock, 0.06f, 0f);
            float fl = 2.0f * MathF.Exp(-age * 8f) * Cover(R * 2f, dist);
            if (fl > 0.02f)
                Sprite(c, _camRight, _camUp, R * 1.3f, R * 1.3f, new Vector4(col, fl), ModeGlow, 0f, 0f);
        }

        // Sparks flung outwards from the rim (streaks: bright head, fading tail).
        int sparks = passed ? (finish ? 48 : 28) : 14;
        for (int j = 0; j < sparks; j++)
        {
            float h0 = Hash(seed * 131 + j * 7 + 1), h1 = Hash(seed * 71 + j * 13 + 5), h2 = Hash(seed * 29 + j * 3 + 11);
            float a = (j + h0 * 0.8f) * MathHelper.TwoPi / sparks;
            Vector3 dir = right * MathF.Cos(a) + up * MathF.Sin(a);
            float sp = (passed ? 2.2f : 1.0f) * big * (0.5f + h1);
            Vector3 head = c + dir * R * (1.05f + sp * e) + fw * R * (h2 - 0.3f) * 1.2f * e;
            Vector3 tail = head - dir * R * (0.15f + 0.5f * life * sp * 0.5f);
            float hd = (head - _eye).Length;
            float hw = MathF.Max(R * 0.014f, 1.2f * hd / _pxPerRadian);
            // Sparks right at the lens become wide white bars: fade them out up close.
            float sf = 2.2f * life * Smooth(R * 0.8f, R * 2.5f, hd) * (0.35f + 0.65f * Cover(R * (1.05f + sp * e), dist));
            if (sf > 0.02f) Segment(head, tail, hw, hw, new Vector4(hot, sf), ModeBeam);
        }
    }

    // =====================================================================================
    // Beacon and guide
    // =====================================================================================

    private void BuildBeacon(FlightContext ctx, RaceView race, int n)
    {
        if (race.Phase == RacePhase.Finished) return;
        int i = race.NextGate;
        if (i < 0 || i >= n) return;
        var g = race.Gates[i];
        Vector3 c = race.GateWorld(i, ctx);
        Basis(g, out _, out var up, out _);
        float R = g.Radius;
        float dist = MathF.Max((c - _eye).Length, 1e-4f);
        float wpp = dist / _pxPerRadian;
        Vector3 tint = g.IsFinish ? ColFinish : ColNext;
        float pulse = 0.8f + 0.2f * MathF.Sin(_clock * MathHelper.TwoPi * 1.2f);

        // Light pillar above the gate (the short downward one read as a stray line under the ship): readable from far away, out of the way close up.
        float pillar = 2.2f * Smooth(0.2f, 0.7f, dist);
        if (pillar > 0.02f)
        {
            float hw = MathF.Max(R * 0.05f, 1.6f * wpp);
            float hwFar = MathF.Max(R * 0.02f, 0.8f * wpp);
            Vector3 a0 = c + up * R * 1.35f;
            Segment(a0, a0 + up * BeamLength, hw, hwFar, new Vector4(tint, pillar), ModeBeam);
        }

        // Star with a minimum on-screen size at the centre; fades out as the gate gets close.
        float star = 3.0f * pulse * Smooth(0.35f, 1.4f, dist);
        if (star > 0.02f)
        {
            float hs = MathF.Max(R * 0.7f, 22f * wpp);
            Sprite(c, _camRight, _camUp, hs, hs, new Vector4(tint, star), ModeStar, 0f, 0f);
        }
    }

    private void BuildGuide(FlightContext ctx, RaceView race, int n)
    {
        if (race.Phase == RacePhase.Finished) return;
        int i = race.NextGate;
        if (i < 0 || i >= n) return;
        var g = race.Gates[i];
        Basis(g, out var gFw, out var gUp, out _);
        Vector3 gc = race.GateWorld(i, ctx);

        // Free flight (no race on): lead the pilot to the first gate only when it is near
        // and ahead; otherwise the guide would trail off-screen as a meaningless smear.
        if (race.Phase == RacePhase.Idle)
        {
            Vector3 toGate = gc - ctx.ShipPosition;
            if (toGate.Length > 1.2f || Vector3.Dot(toGate, ctx.ShipForward) < 0f) return;
        }

        Vector3 p0 = ctx.ShipPosition + ctx.ShipForward * (ShipLayout.Length * 0.9f);
        float d = (gc - p0).Length;
        if (d > 0.08f)
        {
            float tl = MathF.Min(d * 0.6f, 1.2f);
            BuildCurve(p0, ctx.ShipForward * tl, gc, gFw * tl);
            // Free flight (no race running) keeps the guide quieter.
            float gi = race.Phase == RacePhase.Idle ? 1.2f : 2.3f;
            EmitChevrons(ctx.ShipUp, gUp, ColGuide, gi, 0.05f, g.Radius * 0.9f, true);
        }

        if (i + 1 < n)
        {
            var g2 = race.Gates[i + 1];
            Basis(g2, out var g2Fw, out var g2Up, out _);
            Vector3 gc2 = race.GateWorld(i + 1, ctx);
            float d2 = (gc2 - gc).Length;
            float tl = MathF.Min(d2 * 0.6f, 1.2f);
            BuildCurve(gc, gFw * tl, gc2, g2Fw * tl);
            var tint = g2.IsFinish ? ColFinish : Vector3.Lerp(ColUpcoming, ColGuide, 0.4f);
            EmitChevrons(gUp, g2Up, tint, 0.55f, g.Radius * 1.2f, g2.Radius * 1.1f, false);
        }
    }

    /// <summary>Samples a cubic Hermite curve into <see cref="_curve"/> with cumulative lengths.</summary>
    private void BuildCurve(Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1)
    {
        float len = 0f;
        for (int s = 0; s <= CurveSamples; s++)
        {
            float u = s / (float)CurveSamples, u2 = u * u, u3 = u2 * u;
            Vector3 p = (2 * u3 - 3 * u2 + 1) * p0 + (u3 - 2 * u2 + u) * t0 + (-2 * u3 + 3 * u2) * p1 + (u3 - u2) * t1;
            Vector3 dp = (6 * u2 - 6 * u) * p0 + (3 * u2 - 4 * u + 1) * t0 + (-6 * u2 + 6 * u) * p1 + (3 * u2 - 2 * u) * t1;
            if (s > 0) len += (p - _curve[s - 1]).Length;
            _curve[s] = p;
            _curveTan[s] = dp.LengthSquared > 1e-12f ? dp.Normalized() : (p1 - p0).Normalized();
            _curveLen[s] = len;
        }
    }

    /// <summary>Lays chevrons along the sampled curve, flowing forward. They lie in the
    /// plane given by the (interpolated) up vectors, like arrows painted on a runway.</summary>
    private void EmitChevrons(Vector3 upStart, Vector3 upEnd, Vector3 tint, float intensity, float skipStart, float skipEnd, bool fadeFar)
    {
        float total = _curveLen[CurveSamples];
        if (total < 1e-4f) return;
        float offset = Fract(_clock * 0.35f / ChevronSpacing) * ChevronSpacing;
        int seg = 0;
        for (float s = offset; s < total; s += ChevronSpacing)
        {
            while (seg < CurveSamples - 1 && _curveLen[seg + 1] < s) seg++;
            float l0 = _curveLen[seg], l1 = _curveLen[seg + 1];
            float f = l1 > l0 ? (s - l0) / (l1 - l0) : 0f;
            Vector3 p = Vector3.Lerp(_curve[seg], _curve[seg + 1], f);
            Vector3 tan = Vector3.Lerp(_curveTan[seg], _curveTan[seg + 1], f).Normalized();
            float u = s / total;
            // Camera-facing: the arrow follows the curve's on-screen direction, so it never
            // flattens into a sliver when the path runs away from the chase camera.
            Vector3 toEye = _eye - p;
            float dEye = toEye.Length;
            if (dEye < 1e-5f) continue;
            toEye /= dEye;
            Vector3 tanS = tan - Vector3.Dot(tan, toEye) * toEye;
            // Where the path runs straight into the screen the arrows would pile up into one
            // blob; dim them there (the gate itself is right ahead then).
            float onScreen = 0.2f + 0.8f * Smooth(0.12f, 0.5f, tanS.Length);
            onScreen *= Smooth(0.08f, 0.2f, dEye);   // a path curling back past the lens
            if (tanS.LengthSquared < 1e-6f)
            {
                Vector3 upv = Vector3.Lerp(upStart, upEnd, u);
                tanS = upv - Vector3.Dot(upv, toEye) * toEye;
                if (tanS.LengthSquared < 1e-8f) continue;
            }
            tanS.Normalize();
            Vector3 across = Vector3.Cross(tanS, toEye).Normalized();

            float a = intensity;
            a *= Smooth(skipStart, skipStart + 0.08f, s);
            a *= Smooth(skipEnd, skipEnd + 0.08f, total - s);
            if (fadeFar) a *= 0.3f + 0.7f * MathF.Exp(-s * 1.6f);
            // Pulses running along the line towards the gate.
            float wave = 0.5f + 0.5f * MathF.Cos(MathHelper.TwoPi * (s / 0.22f - _clock * 1.8f));
            a *= 0.55f + 0.75f * wave * wave * wave;
            if (a < 0.01f) continue;
            // A little bigger near the ship, never smaller than ~9 px.
            float size = fadeFar ? 1.35f - 0.55f * Smooth(0f, 0.6f, s) : 1f;
            float minW = 9f * dEye / _pxPerRadian;
            float hx = MathF.Max(0.011f * size, minW), hy = MathF.Max(0.014f * size, minW * 1.2f);
            Sprite(p, tanS, across, hx, hy, new Vector4(tint, a * onScreen), ModeChevron, 0f, 0f);
        }
    }

    // =====================================================================================
    // Geometry helpers
    // =====================================================================================

    /// <summary>A circle drawn as a camera-facing tube: thick from every angle, never
    /// thinner than <paramref name="minPx"/> pixels.</summary>
    private void RingTube(Vector3 c, Vector3 ax, Vector3 ay, float radius, float halfWorld, float minPx, Vector4 col, float pattern, float phase, int segs)
    {
        Vector3 prevL = default, prevR = default;
        float prevU = 0f;
        for (int k = 0; k <= segs; k++)
        {
            float a = k * MathHelper.TwoPi / segs;
            float cs = MathF.Cos(a), sn = MathF.Sin(a);
            Vector3 radial = ax * cs + ay * sn;
            Vector3 p = c + radial * radius;
            Vector3 tan = ay * cs - ax * sn;
            Vector3 toEye = _eye - p;
            float dist = toEye.Length;
            Vector3 side = Vector3.Cross(tan, toEye);
            side = side.LengthSquared > 1e-12f ? side.Normalized() : radial;
            float hw = MathF.Max(halfWorld, minPx * dist / _pxPerRadian);
            Vector3 l = p - side * hw, r = p + side * hw;
            float u = k / (float)segs;
            if (k > 0)
                Quad(prevL, prevR, l, r, prevU, u, -1f, 1f, pattern, phase, col, ModeRim);
            prevL = l; prevR = r; prevU = u;
        }
    }

    /// <summary>A camera-facing ribbon from <paramref name="a"/> (uv.x = 0) to <paramref name="b"/> (uv.x = 1).</summary>
    private void Segment(Vector3 a, Vector3 b, float hwA, float hwB, Vector4 col, int mode)
    {
        Vector3 dir = b - a;
        Vector3 mid = (a + b) * 0.5f;
        Vector3 side = Vector3.Cross(dir, _eye - mid);
        if (side.LengthSquared < 1e-14f) return;
        side.Normalize();
        Quad(a - side * hwA, a + side * hwA, b - side * hwB, b + side * hwB, 0f, 1f, -1f, 1f, 0f, 0f, col, mode);
    }

    /// <summary>A quad centred on <paramref name="c"/> spanning ±<paramref name="ax"/>·hx and
    /// ±<paramref name="ay"/>·hy, with uv.xy running −1..1.</summary>
    private void Sprite(Vector3 c, Vector3 ax, Vector3 ay, float hx, float hy, Vector4 col, int mode, float z, float w)
    {
        Vector3 x = ax * hx, y = ay * hy;
        Quad(c - x - y, c - x + y, c + x - y, c + x + y, -1f, 1f, -1f, 1f, z, w, col, mode);
    }

    /// <summary>Two triangles: a0/a1 at uv.x = ua, b0/b1 at uv.x = ub; *0 at uv.y = v0, *1 at v1.</summary>
    private void Quad(Vector3 a0, Vector3 a1, Vector3 b0, Vector3 b1, float ua, float ub, float v0, float v1, float z, float w, Vector4 col, int mode)
    {
        Vertex(a0, ua, v0, z, w, col, mode);
        Vertex(a1, ua, v1, z, w, col, mode);
        Vertex(b0, ub, v0, z, w, col, mode);
        Vertex(b0, ub, v0, z, w, col, mode);
        Vertex(a1, ua, v1, z, w, col, mode);
        Vertex(b1, ub, v1, z, w, col, mode);
    }

    private void Vertex(Vector3 world, float u, float v, float z, float w, Vector4 c, int mode)
    {
        int o = _vertCount * FloatsPerVertex;
        if (o + FloatsPerVertex > _verts.Length) Array.Resize(ref _verts, _verts.Length * 2);
        Vector3 p = world - _eye;
        _verts[o] = p.X; _verts[o + 1] = p.Y; _verts[o + 2] = p.Z;
        _verts[o + 3] = u; _verts[o + 4] = v; _verts[o + 5] = z; _verts[o + 6] = w;
        _verts[o + 7] = c.X; _verts[o + 8] = c.Y; _verts[o + 9] = c.Z; _verts[o + 10] = c.W;
        _verts[o + 11] = mode;
        _vertCount++;
    }

    /// <summary>Orthonormal gate axes, robust to slightly non-unit input.</summary>
    private static void Basis(in RaceGate g, out Vector3 fw, out Vector3 up, out Vector3 right)
    {
        fw = g.Forward.LengthSquared > 1e-12f ? g.Forward.Normalized() : -Vector3.UnitZ;
        up = g.Up - fw * Vector3.Dot(g.Up, fw);
        if (up.LengthSquared < 1e-12f) up = MathF.Abs(fw.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        up.Normalize();
        right = Vector3.Cross(fw, up).Normalized();
    }

    /// <summary>1 while a disc of <paramref name="radius"/> at <paramref name="dist"/> covers
    /// less than ~a third of the screen height, 0 once it would fill most of it.</summary>
    private float Cover(float radius, float dist)
    {
        float pxR = radius / MathF.Max(dist, 1e-5f) * _pxPerRadian;
        return 1f - Smooth(0.28f * _viewport.Y, 0.7f * _viewport.Y, pxR);
    }

    private static float Smooth(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float Fract(float x) => x - MathF.Floor(x);

    private static float Hash(int n)
    {
        uint h = (uint)n * 2654435761u;
        h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
        return (h & 0xFFFFFF) / 16777216f;
    }
}
