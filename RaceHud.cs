// PROTOTYPE (2026-11-17): arcade flight mode — gate race: the on-screen race display.
using System.Globalization;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SolarSystem;

// Layout (all sizes derive from the framebuffer height, 1080 p = 1.0):
//  - top centre: the race clock in slanted seven-segment digits (fixed width, crisp at any
//    size), the gate counter + best time, one pip per gate, penalties, and a short flash
//    after each gate (split delta against the best run, or "MISSED +2 s");
//  - the next gate: corner brackets + distance while on screen, an edge arrow otherwise;
//  - countdown digits and "GO!" in the upper middle, above the ship;
//  - the finish result as a soft banner in the upper third (never over the ship).
// The bottom-left block (speed, boost, key hints) belongs to the flight HUD.

/// <summary>2D overlay for the race: clock, gate count, countdown, gate marker, result.
/// Drawn over the finished frame (not part of the HDR scene). Text goes through
/// <see cref="Renderer.DrawText"/>; shapes and the big digits through a small batched
/// triangle drawer of its own.</summary>
public sealed class RaceHud : IDisposable
{
    private const float KmPerUnit = 17127f;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly Vector4 Cyan = new(0.55f, 0.92f, 1.00f, 1f);
    private static readonly Vector4 White = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 Dim = new(0.78f, 0.86f, 1.00f, 0.62f);
    private static readonly Vector4 Green = new(0.40f, 1.00f, 0.55f, 1f);
    private static readonly Vector4 Red = new(1.00f, 0.32f, 0.28f, 1f);
    private static readonly Vector4 Amber = new(1.00f, 0.72f, 0.25f, 1f);
    private static readonly Vector4 Gold = new(1.00f, 0.82f, 0.30f, 1f);
    private static readonly Vector4 Silver = new(0.82f, 0.88f, 0.96f, 1f);
    private static readonly Vector4 Bronze = new(0.88f, 0.56f, 0.30f, 1f);
    private static readonly Vector4 Shadow = new(0f, 0f, 0f, 0.6f);

    private ShaderProgram? _shader;
    private int _vao, _vbo, _vboCapacity;
    private float[] _buf = new float[6 * 4096];
    private int _floats;

    // Per-frame values.
    private float _w, _h, _s;
    private Renderer _renderer = null!;
    private BitmapFont _font = null!;
    private readonly List<(string text, float x, float y, float size, Vector4 color)> _texts = new();

    /// <summary>Create the shader and buffers. Call once with a current GL context.</summary>
    public void Initialize()
    {
        _shader = ShaderSources.CreateProgram("racehud.vert", "racehud.frag");
        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        _vboCapacity = _buf.Length;
        GL.BufferData(BufferTarget.ArrayBuffer, _vboCapacity * sizeof(float), IntPtr.Zero, BufferUsageHint.StreamDraw);
        const int stride = 6 * sizeof(float);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, stride, 2 * sizeof(float));
        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
    }

    /// <summary>Draw the race display for this frame. <paramref name="boostEnergy01"/> is
    /// the boost reserve (0 .. 1) — the glue already shows it bottom-left; the race display
    /// echoes it only as a small warning under the clock when it is nearly empty.</summary>
    public void Draw(Renderer renderer, BitmapFont font, Camera cam, FlightContext ctx, RaceView race, float boostEnergy01)
    {
        if (race.Gates.Count == 0) return;
        _renderer = renderer;
        _font = font;
        _w = renderer.FramebufferSize.X;
        _h = renderer.FramebufferSize.Y;
        if (_w < 2f || _h < 2f) return;
        _s = _h / 1080f;
        _floats = 0;
        _texts.Clear();

        switch (race.Phase)
        {
            case RacePhase.Idle:
                DrawIdle(race);
                DrawGateMarker(cam, ctx, race, 0.55f);
                break;
            case RacePhase.Countdown:
                DrawGateMarker(cam, ctx, race, 1f);
                DrawTopCluster(race, boostEnergy01, dimClock: true);
                DrawCountdown(race);
                break;
            case RacePhase.Running:
                DrawGateMarker(cam, ctx, race, 1f);
                DrawTopCluster(race, boostEnergy01, dimClock: false);
                DrawGateFlash(race);
                DrawGo(race);
                break;
            case RacePhase.Finished:
                DrawFinished(race);
                break;
        }

        Flush();
        foreach (var t in _texts)
        {
            // Text positions in this class are the top of the capitals; DrawText's y is the baseline.
            float y = t.y + t.size * 0.74f;
            float o = MathF.Max(1f, t.size * 0.07f);
            _renderer.DrawText(_font, t.text, t.x + o, y + o, t.size, Shadow with { W = Shadow.W * t.color.W });
            _renderer.DrawText(_font, t.text, t.x, y, t.size, t.color);
        }
    }

    // ---- Phases ------------------------------------------------------------------------

    private void DrawIdle(RaceView race)
    {
        float y = P(26f);
        float size = Sz(21f);
        string prompt = Localization.T("ui.race.idle", Localization.T(race.TrackNameKey));
        float tw = _font.MeasureWidth(prompt, size);
        Band(_w * 0.5f, y - P(10f), tw * 0.5f + P(40f), P(70f), 0.38f);
        float pulse = 0.78f + 0.22f * MathF.Sin(race.Clock * 3.2f);
        TextC(prompt, _w * 0.5f, y, size, White with { W = pulse });
        string best = float.IsNaN(race.BestTime)
            ? Localization.T("ui.race.best.none")
            : Localization.T("ui.race.best", FormatTime(race.BestTime));
        TextC(best, _w * 0.5f, y + size * 1.45f, Sz(15f), Dim);

        // A flying-start run that was just called off: say why the race went away.
        float off = race.Clock - race.CalledOffClock;
        if (off >= 0f && off < 4f)
        {
            float a = off < 3f ? 1f : 4f - off;
            TextC(Localization.T("ui.race.calledoff"), _w * 0.5f, y + size * 1.45f + Sz(15f) * 1.9f, Sz(17f), Amber with { W = a });
        }
    }

    /// <summary>Clock, gate counter, best time, pips and penalties at the top centre.</summary>
    private void DrawTopCluster(RaceView race, float boost01, bool dimClock)
    {
        float cx = _w * 0.5f;
        float top = P(22f);
        float digitH = P(56f);
        int n = race.Gates.Count;

        Band(cx, 0f, P(330f), top + digitH + P(96f), 0.42f);

        // Clock.
        string clock = FormatTime(MathF.Max(0f, race.TotalTime));
        float cw = SegWidth(clock, digitH);
        var clockCol = dimClock ? White with { W = 0.55f } : White;
        SegString(clock, cx - cw * 0.5f, top, digitH, clockCol);
        Text(Localization.T("ui.race.time"), cx - cw * 0.5f - _font.MeasureWidth(Localization.T("ui.race.time"), Sz(13f)) - P(14f),
             top + digitH * 0.5f - Sz(13f) * 0.6f, Sz(13f), Dim);

        // Gate counter (right-aligned to the centre) and best time (left-aligned).
        float rowY = top + digitH + P(14f);
        float rowSize = Sz(17f);
        int shownGate = Math.Min(race.NextGate + 1, n);
        string gate = Localization.T("ui.race.gate", shownGate.ToString(Inv), n.ToString(Inv));
        float sinceEvent = race.Clock - race.LastEventClock;
        bool gateEvent = race.LastEvent is RaceEvent.GatePassed or RaceEvent.GateMissed;
        var gateCol = Cyan;
        if (gateEvent && sinceEvent < 0.45f) gateCol = Vector4.Lerp(White, Cyan, sinceEvent / 0.45f);
        Text(gate, cx - P(14f) - _font.MeasureWidth(gate, rowSize), rowY, rowSize, gateCol);
        string best = float.IsNaN(race.BestTime)
            ? Localization.T("ui.race.best.none")
            : Localization.T("ui.race.best", FormatTime(race.BestTime));
        Text(best, cx + P(14f), rowY + (rowSize - Sz(15f)) * 0.5f, Sz(15f), Dim);
        FillQuad(cx - P(1f), rowY + P(2f), P(2f), rowSize * 0.9f, Dim with { W = 0.4f });

        // One pip per gate.
        float pipY = rowY + rowSize + P(16f);
        float pip = P(5.5f);
        float step = MathF.Min(P(18f), P(600f) / MathF.Max(1, n));
        float px0 = cx - step * (n - 1) * 0.5f;
        for (int i = 0; i < n; i++)
        {
            var st = i < race.Status.Length ? race.Status[i] : GateStatus.Upcoming;
            var c = st switch
            {
                GateStatus.Passed => Cyan with { W = 0.9f },
                GateStatus.Missed => Red,
                GateStatus.Next => White with { W = 0.75f + 0.25f * MathF.Sin(race.Clock * 7f) },
                _ => Dim with { W = 0.35f },
            };
            float r = pip;
            if (st == GateStatus.Next) r *= 1.3f;
            if (gateEvent && i == race.LastEventGate && sinceEvent < 0.5f) r *= 1f + 0.9f * (1f - sinceEvent / 0.5f);
            if (i < n - 1 || !race.Gates[i].IsFinish) Diamond(new Vector2(px0 + step * i, pipY), r, c);
            else { FillQuad(px0 + step * i - r, pipY - r, r * 2f, r * 2f, c); }
        }

        // Penalties and a nearly-empty boost warning on one line under the pips.
        float lineY = pipY + P(14f);
        if (race.Penalty > 0f)
            TextC(Localization.T("ui.race.penalty", race.Penalty.ToString("0.#", Inv)), cx, lineY, Sz(15f), Red with { W = 0.9f });
        if (boost01 < 0.15f && !dimClock)
        {
            // Thin bar under the band: drains red, a hint that boost is about to run out.
            float bw = P(120f);
            float by = top + digitH + P(96f) - P(4f);
            FillQuad(cx - bw * 0.5f, by, bw, P(3f), Red with { W = 0.25f });
            FillQuad(cx - bw * 0.5f, by, bw * MathHelper.Clamp(boost01 / 0.15f, 0f, 1f), P(3f), Red with { W = 0.85f });
        }
    }

    /// <summary>~1.5 s after each gate: the split against the best run, or the miss.</summary>
    private void DrawGateFlash(RaceView race)
    {
        const float Dur = 1.5f;
        float t = race.Clock - race.LastEventClock;
        if (t < 0f || t > Dur) return;
        float a = t < Dur - 0.4f ? 1f : (Dur - t) / 0.4f;
        float pop = 1f + 0.35f * MathF.Max(0f, 1f - t / 0.18f);
        float y = P(196f);
        float cx = _w * 0.5f;

        if (race.LastEvent == RaceEvent.GateMissed)
        {
            float size = Sz(28f) * pop;
            string msg = Localization.T("ui.race.missed", race.PenaltyPerMiss.ToString("0.#", Inv));
            float tw = _font.MeasureWidth(msg, size);
            Band(cx, y - P(8f), tw * 0.5f + P(50f), size + P(16f), 0.45f * a, new Vector4(0.45f, 0.02f, 0.02f, 1f));
            TextC(msg, cx, y, size, Red with { W = a });
            // A quick red pulse on the screen edges.
            float e = MathF.Max(0f, 1f - t / 0.5f) * 0.35f;
            if (e > 0f) EdgeGlow(Red with { W = e }, P(60f));
        }
        else if (race.LastEvent is RaceEvent.GatePassed or RaceEvent.Finished && !float.IsNaN(race.LastSplitDelta))
        {
            if (race.LastEvent == RaceEvent.Finished) return; // the banner shows the result
            float d = race.LastSplitDelta;
            string txt = (d <= 0f ? "-" : "+") + MathF.Abs(d).ToString("0.00", Inv);
            var c = d <= 0f ? Green : Red;
            float hgt = P(34f) * pop;
            float sw = SegWidth(txt, hgt);
            Band(cx, y - P(8f), sw * 0.5f + P(46f), hgt + P(16f), 0.38f * a);
            SegString(txt, cx - sw * 0.5f, y, hgt, c with { W = a });
        }
    }

    private void DrawCountdown(RaceView race)
    {
        if (race.Countdown <= 0f) return;
        int digit = Math.Max(1, (int)MathF.Ceiling(race.Countdown));
        float local = MathHelper.Clamp(digit - race.Countdown, 0f, 1f); // 0 when the digit appears
        float ease = 1f - MathF.Pow(1f - MathF.Min(local / 0.22f, 1f), 3f);
        float scale = 1f + 0.55f * (1f - ease);
        float alpha = (local < 0.75f ? 1f : 1f - (local - 0.75f) / 0.25f) * MathF.Min(1f, local / 0.06f + 0.3f);
        var col = digit switch { 1 => new Vector4(1f, 0.95f, 0.45f, 1f), 2 => Amber, _ => new Vector4(1f, 0.42f, 0.32f, 1f) };

        var c = new Vector2(_w * 0.5f, _h * 0.33f);
        float hgt = _h * 0.17f * scale;
        string s = digit.ToString(Inv);
        float sw = SegWidth(s, hgt);
        if (digit == 1) sw += 0.45f * hgt; // a seven-segment "1" sits on the right of its cell
        // Soft glow behind the digit, then the digit, then a ring that winds down with the second.
        Disc(c, hgt * 0.62f, new Vector4(0f, 0f, 0f, 0.28f * alpha), new Vector4(0f, 0f, 0f, 0f));
        SegString(s, c.X - sw * 0.5f, c.Y - hgt * 0.5f, hgt, col with { W = alpha }, ghost: false);
        float ring = _h * 0.125f;
        Arc(c, ring, ring + P(5f), -MathHelper.PiOver2, -MathHelper.PiOver2 + MathHelper.TwoPi * (1f - local), col with { W = 0.8f * alpha });
        Arc(c, ring, ring + P(5f), 0f, MathHelper.TwoPi, col with { W = 0.12f * alpha });
    }

    private void DrawGo(RaceView race)
    {
        // From the start signal itself, not the last event: on a flying start the first
        // gate is passed in the same frame and is already the last event.
        float t = race.Clock - race.StartClock;
        const float Dur = 0.9f;
        if (t < 0f || t > Dur) return;
        float a = 1f - t / Dur;
        var c = new Vector2(_w * 0.5f, _h * 0.33f);
        float size = Sz(84f) * (1f + 0.25f * t / Dur);
        string go = Localization.T("ui.race.go");
        TextC(go, c.X, c.Y - size * 0.36f, size, Green with { W = a });
        float r = _h * 0.10f + _h * 0.32f * (t / Dur);
        Arc(c, r, r + P(6f) * a + P(1f), 0f, MathHelper.TwoPi, Green with { W = 0.7f * a });
    }

    private void DrawFinished(RaceView race)
    {
        float t = race.LastEvent == RaceEvent.Finished ? race.Clock - race.LastEventClock : 10f;
        float k = MathHelper.Clamp(t / 0.35f, 0f, 1f);
        float ease = 1f - MathF.Pow(1f - k, 3f);
        float cx = _w * 0.5f;
        float top = _h * 0.10f;
        var medalCol = race.Medal switch { 3 => Gold, 2 => Silver, 1 => Bronze, _ => Dim with { W = 1f } };

        // Banner backing and two accent lines that grow out from the centre.
        float bandH = P(340f);
        float halfW = MathF.Min(_w * 0.46f, P(620f));
        Band(cx, top, halfW, bandH, 0.55f * ease);
        float lw = halfW * 0.8f * ease;
        FillQuad(cx - lw, top, lw * 2f, P(2f), medalCol with { W = 0.85f });
        FillQuad(cx - lw, top + bandH - P(2f), lw * 2f, P(2f), medalCol with { W = 0.5f });
        if (k <= 0f) return;

        float y = top + P(18f);
        TextC(Localization.T("ui.race.finished"), cx, y, Sz(20f), medalCol with { W = ease });
        y += P(36f);

        float timeH = P(76f) * (1f + 0.25f * (1f - ease));
        float time = float.IsNaN(race.FinishTime) ? race.TotalTime : race.FinishTime;
        string ts = FormatTime(time);
        float tw = SegWidth(ts, timeH);
        SegString(ts, cx - tw * 0.5f, y, timeH, White with { W = ease });
        if (race.NewRecord)
        {
            float blink = 0.65f + 0.35f * MathF.Sin(race.Clock * 6f);
            Text(Localization.T("ui.race.newrecord"), cx + tw * 0.5f + P(20f), y + timeH * 0.5f - Sz(18f) * 0.6f, Sz(18f), Gold with { W = blink * ease });
        }
        y += P(76f) + P(20f);

        // Medal: a disc with a star, then the name.
        string medal = Localization.T(race.Medal switch
        {
            3 => "ui.race.medal.gold", 2 => "ui.race.medal.silver", 1 => "ui.race.medal.bronze", _ => "ui.race.medal.none",
        });
        float ms = Sz(26f);
        float mw = _font.MeasureWidth(medal, ms);
        if (race.Medal > 0)
        {
            float r = P(17f) * (1f + 0.4f * MathF.Max(0f, 1f - (t - 0.35f) / 0.25f) * (t > 0.35f ? 1f : 0f));
            float total = r * 2f + P(12f) + mw;
            var dc = new Vector2(cx - total * 0.5f + r, y + ms * 0.38f);
            Disc(dc, r, medalCol, medalCol * new Vector4(0.7f, 0.7f, 0.7f, 1f));
            Arc(dc, r, r + P(2.5f), 0f, MathHelper.TwoPi, White with { W = 0.7f * ease });
            Star(dc, r * 0.62f, new Vector4(0.25f, 0.18f, 0.05f, 0.85f));
            Text(medal, dc.X + r + P(12f), y, ms, medalCol with { W = ease });
        }
        else
        {
            TextC(medal, cx, y, ms, Dim with { W = 0.8f * ease });
        }
        y += P(44f);

        TextC(Localization.T("ui.race.medal.target", FormatTime(race.GoldTime), FormatTime(race.SilverTime), FormatTime(race.BronzeTime)),
              cx, y, Sz(15f), Dim with { W = Dim.W * ease });
        y += P(24f);
        string best = float.IsNaN(race.BestTime)
            ? Localization.T("ui.race.best.none")
            : Localization.T("ui.race.best", FormatTime(race.BestTime));
        TextC(best, cx, y, Sz(15f), Dim with { W = Dim.W * ease });
        y += P(34f);
        float pulse = 0.7f + 0.3f * MathF.Sin(race.Clock * 3.2f);
        TextC(Localization.T("ui.race.restart"), cx, y, Sz(18f), Cyan with { W = pulse * ease });
    }

    // ---- Gate marker -------------------------------------------------------------------

    /// <summary>Brackets + distance around the next gate when it is on screen; an arrow at
    /// the screen edge pointing toward it otherwise. Also a faint diamond on the gate after.</summary>
    private void DrawGateMarker(Camera cam, FlightContext ctx, RaceView race, float alpha)
    {
        int n = race.Gates.Count;
        int i = race.NextGate;
        if (i < 0 || i >= n) return;

        var view = cam.ViewMatrix;
        view.Row3 = new Vector4(0f, 0f, 0f, 1f); // camera-relative: keeps float precision near Saturn
        var proj = cam.ProjectionMatrix;
        var viewProj = view * proj;
        var gate = race.Gates[i];
        var col = (gate.IsFinish ? Gold : Cyan) with { W = alpha };
        float pulse = 0.5f + 0.5f * MathF.Sin(race.Clock * 5f);
        float km = (race.GateWorld(i, ctx) - ctx.ShipPosition).Length * KmPerUnit;
        string dist = Localization.T("ui.race.distance", km.ToString("#,0", Inv).Replace(',', ' '));

        var clip = new Vector4(race.GateWorld(i, ctx) - cam.Eye, 1f) * viewProj;
        float margin = P(70f);
        if (clip.W > 1e-6f)
        {
            var sp = new Vector2((clip.X / clip.W * 0.5f + 0.5f) * _w, (0.5f - clip.Y / clip.W * 0.5f) * _h);
            float pixR = gate.Radius * proj.M22 / clip.W * 0.5f * _h;
            if (sp.X > margin && sp.X < _w - margin && sp.Y > margin && sp.Y < _h - margin)
            {
                float half = MathF.Max(pixR * 1.15f, P(24f)) * (1f + 0.04f * pulse);
                // The gate is about to fill the screen: fade the brackets out of the way.
                float fade = 1f - MathHelper.Clamp((half - _h * 0.20f) / (_h * 0.10f), 0f, 1f);
                if (fade > 0f)
                {
                    var bc = col with { W = col.W * fade * (0.75f + 0.25f * pulse) };
                    // Dark copy underneath so the corners read on the bright ring too.
                    float so = MathF.Max(1.5f, P(2f));
                    Brackets(sp + new Vector2(so, so), half, Shadow with { W = 0.5f * bc.W });
                    Brackets(sp, half, bc);
                    float ds = Sz(15f);
                    // Beside the bracket, not under it: the ship usually sits right below the gate.
                    Text(dist, sp.X + half + P(10f), sp.Y - ds * 0.5f, ds, (gate.IsFinish ? Gold : White) with { W = 0.9f * alpha * fade });
                }
                DrawAfterNext(race, ctx, cam, viewProj, i + 1, alpha);
                return;
            }
        }

        // Off screen or behind: arrow on the screen edge.
        var d = new Vector2(clip.X * _w * 0.5f, -clip.Y * _h * 0.5f);
        if (clip.W > 1e-6f) d = new Vector2((clip.X / clip.W) * _w * 0.5f, -(clip.Y / clip.W) * _h * 0.5f);
        if (d.LengthSquared < 1e-6f) d = new Vector2(0f, 1f);
        d = d.Normalized();
        var c = new Vector2(_w * 0.5f, _h * 0.5f);
        float edge = P(56f);
        float tx = MathF.Abs(d.X) > 1e-5f ? (_w * 0.5f - edge) / MathF.Abs(d.X) : float.MaxValue;
        float ty = MathF.Abs(d.Y) > 1e-5f ? (_h * 0.5f - edge) / MathF.Abs(d.Y) : float.MaxValue;
        var pos = c + d * MathF.Min(tx, ty);
        // Keep out of the flight HUD block (bottom-left, fixed pixels).
        if (pos.X < 740f && pos.Y > _h - 140f) pos.Y = _h - 140f;
        float sz = P(26f) * (1f + 0.12f * pulse);
        var perp = new Vector2(-d.Y, d.X);
        var tip = pos + d * sz;
        var back = pos - d * sz * 0.35f;
        Tri(tip, pos - d * sz * 0.6f + perp * sz * 0.85f, back, col);
        Tri(tip, back, pos - d * sz * 0.6f - perp * sz * 0.85f, col);
        var tip2 = pos - d * sz * 0.55f;
        var back2 = pos - d * sz * 1.25f;
        var c2 = col with { W = col.W * 0.45f };
        Tri(tip2, back2 - d * sz * 0.25f + perp * sz * 0.6f, back2, c2);
        Tri(tip2, back2, back2 - d * sz * 0.25f - perp * sz * 0.6f, c2);
        float ts = Sz(14f);
        var tp = pos - d * sz * 2.3f;
        TextC(dist, tp.X, tp.Y - ts * 0.6f, ts, White with { W = 0.85f * alpha });
    }

    private void DrawAfterNext(RaceView race, FlightContext ctx, Camera cam, Matrix4 viewProj, int j, float alpha)
    {
        if (j >= race.Gates.Count) return;
        var clip = new Vector4(race.GateWorld(j, ctx) - cam.Eye, 1f) * viewProj;
        if (clip.W <= 1e-6f) return;
        var sp = new Vector2((clip.X / clip.W * 0.5f + 0.5f) * _w, (0.5f - clip.Y / clip.W * 0.5f) * _h);
        if (sp.X < 0f || sp.X > _w || sp.Y < 0f || sp.Y > _h) return;
        DiamondOutline(sp, P(9f), P(2f), Dim with { W = 0.45f * alpha });
    }

    // ---- Shapes ------------------------------------------------------------------------

    private float P(float v) => v * _s;
    /// <summary>Font size: scaled, but never below what is readable at 720 p.</summary>
    private float Sz(float v) => MathF.Max(v * _s, MathF.Min(v, 12f));

    private void Text(string text, float x, float y, float size, Vector4 c) => _texts.Add((text, x, y, size, c));
    private void TextC(string text, float cx, float y, float size, Vector4 c)
        => _texts.Add((text, cx - _font.MeasureWidth(text, size) * 0.5f, y, size, c));

    private void Vert(Vector2 p, Vector4 c)
    {
        if (_floats + 6 > _buf.Length) Array.Resize(ref _buf, _buf.Length * 2);
        _buf[_floats++] = p.X; _buf[_floats++] = p.Y;
        _buf[_floats++] = c.X; _buf[_floats++] = c.Y; _buf[_floats++] = c.Z; _buf[_floats++] = c.W;
    }

    private void Tri(Vector2 a, Vector2 b, Vector2 c, Vector4 col) { Vert(a, col); Vert(b, col); Vert(c, col); }

    private void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Vector4 ca, Vector4 cb, Vector4 cc, Vector4 cd)
    {
        Vert(a, ca); Vert(b, cb); Vert(c, cc);
        Vert(a, ca); Vert(c, cc); Vert(d, cd);
    }

    private void FillQuad(float x, float y, float w, float h, Vector4 c)
        => Quad(new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h), c, c, c, c);

    /// <summary>Soft dark band: solid in the middle, fading out to both sides and at the bottom.</summary>
    private void Band(float cx, float y, float halfW, float h, float alpha, Vector4? tint = null)
    {
        if (alpha <= 0f) return;
        var t = tint ?? new Vector4(0.01f, 0.02f, 0.05f, 1f);
        float fade = MathF.Min(h * 0.4f, P(34f));
        BandRow(cx, y, y + h - fade, halfW, t, alpha, alpha);
        BandRow(cx, y + h - fade, y + h, halfW, t, alpha, 0f);
    }

    private void BandRow(float cx, float y0, float y1, float halfW, Vector4 t, float aTop, float aBot)
    {
        var st = t with { W = aTop };
        var sb = t with { W = aBot };
        var clear = t with { W = 0f };
        float inner = halfW * 0.55f;
        float l = cx - halfW, li = cx - inner, ri = cx + inner, r = cx + halfW;
        Quad(new(l, y0), new(li, y0), new(li, y1), new(l, y1), clear, st, sb, clear);
        Quad(new(li, y0), new(ri, y0), new(ri, y1), new(li, y1), st, st, sb, sb);
        Quad(new(ri, y0), new(r, y0), new(r, y1), new(ri, y1), st, clear, clear, sb);
    }

    private void EdgeGlow(Vector4 c, float depth)
    {
        var z = c with { W = 0f };
        Quad(new(0, 0), new(_w, 0), new(_w, depth), new(0, depth), c, c, z, z);
        Quad(new(0, _h - depth), new(_w, _h - depth), new(_w, _h), new(0, _h), z, z, c, c);
        Quad(new(0, 0), new(depth, 0), new(depth, _h), new(0, _h), c, z, z, c);
        Quad(new(_w - depth, 0), new(_w, 0), new(_w, _h), new(_w - depth, _h), z, c, c, z);
    }

    private void Diamond(Vector2 c, float r, Vector4 col)
        => Quad(c + new Vector2(0, -r), c + new Vector2(r, 0), c + new Vector2(0, r), c + new Vector2(-r, 0), col, col, col, col);

    private void DiamondOutline(Vector2 c, float r, float t, Vector4 col)
    {
        Span<Vector2> o = [c + new Vector2(0, -r), c + new Vector2(r, 0), c + new Vector2(0, r), c + new Vector2(-r, 0)];
        float ri = MathF.Max(0f, r - t * 1.41f);
        Span<Vector2> i = [c + new Vector2(0, -ri), c + new Vector2(ri, 0), c + new Vector2(0, ri), c + new Vector2(-ri, 0)];
        for (int k = 0; k < 4; k++)
        {
            int n = (k + 1) % 4;
            Quad(o[k], o[n], i[n], i[k], col, col, col, col);
        }
    }

    /// <summary>Four L-shaped corners around a square of half-size <paramref name="half"/>.</summary>
    private void Brackets(Vector2 c, float half, Vector4 col)
    {
        float len = MathF.Max(P(10f), half * 0.32f);
        float t = MathF.Max(2f, P(3f));
        for (int sx = -1; sx <= 1; sx += 2)
        for (int sy = -1; sy <= 1; sy += 2)
        {
            float x = c.X + sx * half, y = c.Y + sy * half;
            // Horizontal arm and vertical arm, both growing inward from the corner.
            FillQuad(sx < 0 ? x : x - len, sy < 0 ? y : y - t, len, t, col);
            FillQuad(sx < 0 ? x : x - t, sy < 0 ? y + t : y - len, t, len - t, col);
        }
        // Tiny centre tick so the aim point reads even when the gate is far.
        Diamond(c, MathF.Max(2f, P(3f)), col with { W = col.W * 0.6f });
    }

    private void Arc(Vector2 c, float r0, float r1, float a0, float a1, Vector4 col)
    {
        float span = a1 - a0;
        if (MathF.Abs(span) < 1e-4f) return;
        int seg = Math.Clamp((int)(MathF.Abs(span) * r1 / 6f), 8, 160);
        for (int k = 0; k < seg; k++)
        {
            float t0 = a0 + span * k / seg, t1 = a0 + span * (k + 1) / seg;
            var d0 = new Vector2(MathF.Cos(t0), MathF.Sin(t0));
            var d1 = new Vector2(MathF.Cos(t1), MathF.Sin(t1));
            Quad(c + d0 * r0, c + d0 * r1, c + d1 * r1, c + d1 * r0, col, col, col, col);
        }
    }

    /// <summary>Filled disc, <paramref name="centre"/> colour at the middle fading to
    /// <paramref name="edge"/> at the rim.</summary>
    private void Disc(Vector2 c, float r, Vector4 centre, Vector4 edge)
    {
        int seg = Math.Clamp((int)(r / 3f), 16, 96);
        for (int k = 0; k < seg; k++)
        {
            float t0 = MathHelper.TwoPi * k / seg, t1 = MathHelper.TwoPi * (k + 1) / seg;
            Vert(c, centre);
            Vert(c + new Vector2(MathF.Cos(t0), MathF.Sin(t0)) * r, edge);
            Vert(c + new Vector2(MathF.Cos(t1), MathF.Sin(t1)) * r, edge);
        }
    }

    private void Star(Vector2 c, float r, Vector4 col)
    {
        float ri = r * 0.42f;
        for (int k = 0; k < 5; k++)
        {
            float a = -MathHelper.PiOver2 + k * MathHelper.TwoPi / 5f;
            float am = a + MathHelper.Pi / 5f, ap = a - MathHelper.Pi / 5f;
            var tip = c + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
            var m1 = c + new Vector2(MathF.Cos(am), MathF.Sin(am)) * ri;
            var m0 = c + new Vector2(MathF.Cos(ap), MathF.Sin(ap)) * ri;
            Tri(c, m0, tip, col);
            Tri(c, tip, m1, col);
        }
    }

    // ---- Seven-segment digits ------------------------------------------------------------

    // Bits: a=1 top, b=2 top-right, c=4 bottom-right, d=8 bottom, e=16 bottom-left, f=32 top-left, g=64 middle.
    private static readonly int[] Seg = [63, 6, 91, 79, 102, 109, 125, 7, 127, 111];
    private const float Skew = 0.13f;

    private static float CellWidth(char ch, float h) => ch switch
    {
        >= '0' and <= '9' => 0.56f * h,
        ':' or '.' => 0.24f * h,
        _ => 0.50f * h,
    };

    private static float SegWidth(string s, float h)
    {
        float w = 0f;
        foreach (char ch in s) w += CellWidth(ch, h) + 0.09f * h;
        return MathF.Max(0f, w - 0.09f * h) + Skew * h;
    }

    /// <summary>Slanted seven-segment string, each glyph in a fixed-width cell (digits never
    /// jiggle). Draws a drop shadow and faint unlit segments under the lit ones.</summary>
    private void SegString(string s, float x, float y, float h, Vector4 col, bool ghost = true)
    {
        float off = MathF.Max(1.5f, h * 0.045f);
        SegPass(s, x + off, y + off, h, Shadow with { W = Shadow.W * col.W }, unlit: false);
        if (ghost) SegPass(s, x, y, h, col with { W = col.W * 0.07f }, unlit: true);
        SegPass(s, x, y, h, col, unlit: false);
    }

    private void SegPass(string s, float x, float y, float h, Vector4 col, bool unlit)
    {
        float cx = x;
        foreach (char ch in s)
        {
            float cw = CellWidth(ch, h);
            if (ch is >= '0' and <= '9') Digit(cx, y, h, unlit ? 127 & ~Seg[ch - '0'] : Seg[ch - '0'], col);
            else if (!unlit)
            {
                float t = h * 0.12f;
                switch (ch)
                {
                    case ':':
                        SkewQuad(cx + cw * 0.5f - t * 0.5f, y + h * 0.28f - t * 0.5f, t, t, y + h, col);
                        SkewQuad(cx + cw * 0.5f - t * 0.5f, y + h * 0.72f - t * 0.5f, t, t, y + h, col);
                        break;
                    case '.':
                        SkewQuad(cx + cw * 0.5f - t * 0.5f, y + h - t, t, t, y + h, col);
                        break;
                    case '-':
                        HSeg(cx + 0.06f * h, cx + cw - 0.06f * h, y + h * 0.5f, t, y + h, col);
                        break;
                    case '+':
                        HSeg(cx + 0.06f * h, cx + cw - 0.06f * h, y + h * 0.5f, t, y + h, col);
                        VSeg(cx + cw * 0.5f, y + h * 0.5f - cw * 0.5f + 0.06f * h, y + h * 0.5f - t * 0.6f, t, y + h, col);
                        VSeg(cx + cw * 0.5f, y + h * 0.5f + t * 0.6f, y + h * 0.5f + cw * 0.5f - 0.06f * h, t, y + h, col);
                        break;
                }
            }
            cx += cw + 0.09f * h;
        }
    }

    private void Digit(float x, float y, float h, int mask, Vector4 col)
    {
        float w = 0.56f * h, t = 0.12f * h, g = 0.018f * h;
        float l = x + t * 0.5f, r = x + w - t * 0.5f;
        float top = y + t * 0.5f, mid = y + h * 0.5f, bot = y + h - t * 0.5f;
        float baseY = y + h;
        if ((mask & 1) != 0) HSeg(l + g, r - g, top, t, baseY, col);
        if ((mask & 64) != 0) HSeg(l + g, r - g, mid, t, baseY, col);
        if ((mask & 8) != 0) HSeg(l + g, r - g, bot, t, baseY, col);
        if ((mask & 32) != 0) VSeg(l, top + g, mid - g, t, baseY, col);
        if ((mask & 2) != 0) VSeg(r, top + g, mid - g, t, baseY, col);
        if ((mask & 16) != 0) VSeg(l, mid + g, bot - g, t, baseY, col);
        if ((mask & 4) != 0) VSeg(r, mid + g, bot - g, t, baseY, col);
    }

    private static Vector2 Sk(float x, float y, float baseY) => new(x + (baseY - y) * Skew, y);

    /// <summary>Horizontal hexagonal segment from x0 to x1 (pointed ends) centred on y.</summary>
    private void HSeg(float x0, float x1, float y, float t, float baseY, Vector4 col)
    {
        float h = t * 0.5f;
        var a = Sk(x0, y, baseY); var b = Sk(x0 + h, y - h, baseY); var c = Sk(x1 - h, y - h, baseY);
        var d = Sk(x1, y, baseY); var e = Sk(x1 - h, y + h, baseY); var f = Sk(x0 + h, y + h, baseY);
        Tri(a, b, f, col); Quad(b, c, e, f, col, col, col, col); Tri(c, d, e, col);
    }

    private void VSeg(float x, float y0, float y1, float t, float baseY, Vector4 col)
    {
        float h = t * 0.5f;
        var a = Sk(x, y0, baseY); var b = Sk(x + h, y0 + h, baseY); var c = Sk(x + h, y1 - h, baseY);
        var d = Sk(x, y1, baseY); var e = Sk(x - h, y1 - h, baseY); var f = Sk(x - h, y0 + h, baseY);
        Tri(a, b, f, col); Quad(b, c, e, f, col, col, col, col); Tri(c, d, e, col);
    }

    private void SkewQuad(float x, float y, float w, float h, float baseY, Vector4 col)
        => Quad(Sk(x, y, baseY), Sk(x + w, y, baseY), Sk(x + w, y + h, baseY), Sk(x, y + h, baseY), col, col, col, col);

    // ---- GL ------------------------------------------------------------------------------

    private void Flush()
    {
        if (_floats == 0 || _shader is null) return;
        bool blend = GL.IsEnabled(EnableCap.Blend);
        bool depth = GL.IsEnabled(EnableCap.DepthTest);
        bool cull = GL.IsEnabled(EnableCap.CullFace);
        GL.GetInteger(GetPName.BlendSrcRgb, out int srcRgb);
        GL.GetInteger(GetPName.BlendDstRgb, out int dstRgb);
        GL.GetInteger(GetPName.BlendSrcAlpha, out int srcA);
        GL.GetInteger(GetPName.BlendDstAlpha, out int dstA);
        GL.GetInteger(GetPName.CurrentProgram, out int prog);
        GL.GetInteger(GetPName.VertexArrayBinding, out int vao);
        GL.GetInteger(GetPName.ArrayBufferBinding, out int vbo);

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);

        _shader.Use();
        _shader.SetMatrix4("uProj", Matrix4.CreateOrthographicOffCenter(0, _w, _h, 0, -1, 1));
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        if (_floats > _vboCapacity)
        {
            _vboCapacity = _buf.Length;
            GL.BufferData(BufferTarget.ArrayBuffer, _vboCapacity * sizeof(float), IntPtr.Zero, BufferUsageHint.StreamDraw);
        }
        else
        {
            // Orphan the old storage so the driver does not stall on last frame's draw.
            GL.BufferData(BufferTarget.ArrayBuffer, _vboCapacity * sizeof(float), IntPtr.Zero, BufferUsageHint.StreamDraw);
        }
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, _floats * sizeof(float), _buf);
        GL.DrawArrays(PrimitiveType.Triangles, 0, _floats / 6);

        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.UseProgram(prog);
        GL.BlendFuncSeparate((BlendingFactorSrc)srcRgb, (BlendingFactorDest)dstRgb, (BlendingFactorSrc)srcA, (BlendingFactorDest)dstA);
        if (blend) GL.Enable(EnableCap.Blend); else GL.Disable(EnableCap.Blend);
        if (depth) GL.Enable(EnableCap.DepthTest); else GL.Disable(EnableCap.DepthTest);
        if (cull) GL.Enable(EnableCap.CullFace); else GL.Disable(EnableCap.CullFace);
        _floats = 0;
    }

    // ---- Formatting ------------------------------------------------------------------------

    /// <summary>m:ss.cc, invariant culture; "-:--.--" for NaN.</summary>
    internal static string FormatTime(float seconds)
    {
        if (float.IsNaN(seconds) || float.IsInfinity(seconds)) return "-:--.--";
        int cs = (int)MathF.Floor(MathF.Max(0f, seconds) * 100f);
        int m = cs / 6000, s = cs / 100 % 60, c = cs % 100;
        return string.Create(Inv, $"{m}:{s:00}.{c:00}");
    }

    /// <summary>Release the GL objects.</summary>
    public void Dispose()
    {
        _shader?.Dispose();
        _shader = null;
        if (_vbo != 0) GL.DeleteBuffer(_vbo);
        if (_vao != 0) GL.DeleteVertexArray(_vao);
        _vbo = _vao = 0;
    }
}
