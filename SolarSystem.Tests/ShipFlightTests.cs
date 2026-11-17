// PROTOTYPE (2026-11-17): arcade flight mode — tests for the flight model and chase camera.
using OpenTK.Mathematics;
using Xunit;

namespace SolarSystem.Tests;

/// <summary>
/// Pins the conventions of <see cref="ShipFlight"/> and <see cref="ChaseCamera"/>:
/// input signs, orientation hygiene, speed response, the planet shell, frame-rate
/// independence and the published context. No OpenGL needed.
/// </summary>
public class ShipFlightTests
{
    private const float Saturn = 3.4f;

    private static FlightContext Ctx(Vector3? anchor = null) => new()
    {
        AnchorPosition = anchor ?? new Vector3(100f, -20f, 50f),
        AnchorRadius = Saturn,
        HasRing = true,
        RingInner = 4.59f,
        RingOuter = 8.16f,
    };

    /// <summary>A ship far from the planet, flying along -Z with +Y up.</summary>
    private static ShipFlight FreeShip()
    {
        var s = new ShipFlight();
        s.Reset(new Vector3(30f, 0f, 0f), -Vector3.UnitZ, Vector3.UnitY);
        return s;
    }

    private static void Run(ShipFlight s, FlightContext ctx, ShipInput input, float seconds, float fps = 60f)
    {
        int n = (int)MathF.Round(seconds * fps);
        for (int i = 0; i < n; i++) s.Update(1f / fps, input, ctx);
    }

    private static ShipInput Script(float t) => t switch
    {
        < 1f => new ShipInput(),
        < 2.5f => new ShipInput { Yaw = 1f },
        < 3.5f => new ShipInput { Boost = true, Pitch = 0.4f },
        < 5f => new ShipInput { Boost = true, Yaw = -0.8f },
        < 6.5f => new ShipInput { Roll = 1f },
        < 8f => new ShipInput { Brake = true, Yaw = 0.6f, Pitch = -0.3f },
        _ => new ShipInput(),
    };

    // ---- Reset / orientation -------------------------------------------------

    [Fact]
    public void Reset_AlignsAxesWithRequestedFrame()
    {
        var s = new ShipFlight();
        var f = new Vector3(1f, 0.2f, -0.4f).Normalized();
        s.Reset(new Vector3(10f, 0f, 0f), f, Vector3.UnitY);
        Assert.True(Vector3.Dot(s.Forward, f) > 0.9999f);
        Assert.True(s.Up.Y > 0.9f);
        Assert.True(Vector3.Dot(Vector3.Cross(s.Forward, s.Up), s.Right) > 0.9999f); // right-handed: F x U = R
        Assert.Equal(ShipFlight.CruiseSpeed, s.Velocity.Length, 4);
    }

    [Fact]
    public void Orientation_StaysNormalised_OverThousandsOfSteps()
    {
        var s = FreeShip();
        var ctx = Ctx();
        for (int i = 0; i < 6000; i++)
        {
            float t = i / 120f;
            var input = new ShipInput
            {
                Pitch = MathF.Sin(t * 0.7f),
                Yaw = MathF.Sin(t * 1.3f + 1f),
                Roll = MathF.Sin(t * 0.37f + 2f),
                Boost = (i / 300) % 3 == 0,
                Brake = (i / 500) % 4 == 1,
            };
            s.Update(1f / 120f, input, ctx);
            Assert.InRange(s.Orientation.Length, 0.999f, 1.001f);
        }
        AssertOrthonormal(s.Forward, s.Up, s.Right);
        Assert.True(float.IsFinite(s.LocalPosition.X));
    }

    // ---- Input signs ---------------------------------------------------------

    [Fact]
    public void Yaw_TurnsTowardRight()
    {
        var s = FreeShip();
        var right0 = s.Right;
        Run(s, Ctx(), new ShipInput { Yaw = 1f }, 0.6f);
        Assert.True(Vector3.Dot(s.Forward, right0) > 0.2f, $"forward·right0 = {Vector3.Dot(s.Forward, right0)}");
    }

    [Fact]
    public void Pitch_RaisesNoseTowardUp()
    {
        var s = FreeShip();
        var up0 = s.Up;
        Run(s, Ctx(), new ShipInput { Pitch = 1f }, 0.6f);
        Assert.True(Vector3.Dot(s.Forward, up0) > 0.2f);
    }

    [Fact]
    public void Roll_IsClockwiseSeenFromBehind()
    {
        // Seen from behind, clockwise means the dorsal side swings toward the right wing.
        var s = FreeShip();
        var right0 = s.Right;
        var fwd0 = s.Forward;
        Run(s, Ctx(), new ShipInput { Roll = 1f }, 0.3f);
        Assert.True(Vector3.Dot(s.Up, right0) > 0.2f);
        Assert.True(Vector3.Dot(s.Forward, fwd0) > 0.999f); // pure roll keeps the nose
    }

    [Fact]
    public void Yaw_BanksIntoTheTurn_AndBankEasesOut()
    {
        var s = FreeShip();
        var ctx = Ctx();
        Run(s, ctx, new ShipInput { Yaw = 1f }, 1.2f);
        float bankDeg = MathHelper.RadiansToDegrees(s.BankRad);
        Assert.InRange(bankDeg, 35f, 55f);
        // Right wing down: the dorsal side leans toward the inside of the turn. The ship
        // started with up = +Y and only yawed, so the unbanked right is horizontal.
        var levelRight = Vector3.Cross(s.Forward, Vector3.UnitY).Normalized();
        Assert.True(Vector3.Dot(s.Up, levelRight) > 0.5f, $"up·levelRight = {Vector3.Dot(s.Up, levelRight)}");

        Run(s, ctx, new ShipInput(), 1.5f);
        Assert.InRange(MathHelper.RadiansToDegrees(s.BankRad), -3f, 3f);
    }

    [Fact]
    public void AngularResponse_EasesIn_NoSnap()
    {
        // One 60 fps frame of full yaw must turn far less than the full rate would.
        var s = FreeShip();
        var f0 = s.Forward;
        s.Update(1f / 60f, new ShipInput { Yaw = 1f }, Ctx());
        float angle = MathF.Acos(Math.Clamp(Vector3.Dot(f0, s.Forward), -1f, 1f));
        Assert.True(angle < ShipFlight.MaxYawRate / 60f * 0.25f, $"angle {angle}");
    }

    // ---- Speed ---------------------------------------------------------------

    [Fact]
    public void Boost_ApproachesBoostSpeed_AndReleaseReturnsToCruise()
    {
        var s = FreeShip();
        var ctx = Ctx();
        Run(s, ctx, new ShipInput { Boost = true }, 0.25f);
        Assert.True(s.Boost01 > 0.95f, "Boost01 rises fast");
        Run(s, ctx, new ShipInput { Boost = true }, 2.75f);
        Assert.InRange(s.Velocity.Length, ShipFlight.BoostSpeed * 0.97f, ShipFlight.BoostSpeed * 1.001f);
        Assert.InRange(s.Throttle01, 0.95f, 1f);

        Run(s, ctx, new ShipInput(), 0.25f);
        Assert.True(s.Boost01 > 0.3f, "Boost01 falls slower than it rises");
        Run(s, ctx, new ShipInput(), 4.75f);
        Assert.InRange(s.Velocity.Length, ShipFlight.CruiseSpeed - 0.01f, ShipFlight.CruiseSpeed + 0.01f);
        Assert.InRange(s.Throttle01, 0.45f, 0.55f);
    }

    [Fact]
    public void Brake_ShedsSpeed_AndLowersThrottle()
    {
        var s = FreeShip();
        Run(s, Ctx(), new ShipInput { Brake = true }, 2f);
        Assert.True(s.Velocity.Length < ShipFlight.CruiseSpeed * 0.5f);
        Assert.True(s.Throttle01 < 0.3f);
    }

    // ---- Planet --------------------------------------------------------------

    [Theory]
    [InlineData(60f, false)]
    [InlineData(60f, true)]
    [InlineData(10f, true)] // 0.1 s spikes
    public void NeverEntersThePlanet_HeadOnAtBoost(float fps, bool aim)
    {
        var s = new ShipFlight();
        // Exactly head-on (degenerate steering axis) or slightly off.
        var start = new Vector3(0f, aim ? 0.3f : 0f, 9f);
        s.Reset(start, -start, Vector3.UnitY);
        var ctx = Ctx();
        float minR = float.MaxValue;
        int n = (int)(12f * fps);
        for (int i = 0; i < n; i++)
        {
            s.Update(1f / fps, new ShipInput { Boost = true }, ctx);
            minR = MathF.Min(minR, s.LocalPosition.Length);
        }
        Assert.True(minR >= Saturn + ShipFlight.ShellMargin - 1e-3f, $"min radius {minR}");
    }

    [Fact]
    public void DivingIntoThePlanet_SlidesAlongTheShell()
    {
        var s = new ShipFlight();
        s.Reset(new Vector3(0f, 0f, 4f), new Vector3(0f, 0.1f, -1f), Vector3.UnitY);
        var ctx = Ctx();
        for (int i = 0; i < 600; i++)
        {
            s.Update(1f / 60f, new ShipInput { Boost = true, Pitch = -1f }, ctx); // keep pushing down
            Assert.True(s.LocalPosition.Length >= Saturn + ShipFlight.ShellMargin - 1e-3f);
        }
        Assert.True(s.Velocity.Length > ShipFlight.CruiseSpeed); // it slides, it does not stop
    }

    // ---- Frame rate / pause --------------------------------------------------

    [Fact]
    public void SixtyAndTwoFortyFps_EndClose()
    {
        ShipFlight Fly(float fps)
        {
            var s = FreeShip();
            var ctx = Ctx();
            int n = (int)MathF.Round(9f * fps);
            for (int i = 0; i < n; i++) s.Update(1f / fps, Script(i / fps), ctx);
            return s;
        }
        var a = Fly(60f);
        var b = Fly(240f);
        Assert.True((a.LocalPosition - b.LocalPosition).Length < 0.01f, $"{(a.LocalPosition - b.LocalPosition).Length}");
        Assert.True(Vector3.Dot(a.Forward, b.Forward) > 0.999f);
        Assert.True(Vector3.Dot(a.Up, b.Up) > 0.999f);
        Assert.Equal(a.Velocity.Length, b.Velocity.Length, 2);
    }

    [Fact]
    public void ZeroDt_IsNoOp()
    {
        var s = FreeShip();
        var ctx = Ctx();
        Run(s, ctx, new ShipInput { Yaw = 0.5f, Boost = true }, 0.7f);
        var p = s.LocalPosition; var q = s.Orientation; var v = s.Velocity;
        float th = s.Throttle01, bo = s.Boost01;
        s.Update(0f, new ShipInput { Yaw = -1f, Pitch = 1f, Roll = 1f, Brake = true }, ctx);
        Assert.Equal(p, s.LocalPosition);
        Assert.Equal(q, s.Orientation);
        Assert.Equal(v, s.Velocity);
        Assert.Equal(th, s.Throttle01);
        Assert.Equal(bo, s.Boost01);
        // And the motion continues unchanged afterwards.
        s.Update(1f / 60f, new ShipInput { Yaw = 0.5f, Boost = true }, ctx);
        Assert.NotEqual(p, s.LocalPosition);
    }

    // ---- Context -------------------------------------------------------------

    [Fact]
    public void WriteTo_IsSelfConsistent()
    {
        var s = FreeShip();
        var ctx = Ctx();
        Run(s, ctx, new ShipInput { Yaw = 0.7f, Pitch = 0.3f, Roll = 0.2f, Boost = true }, 1.3f);
        s.WriteTo(ctx);

        Assert.Equal(ctx.AnchorPosition + s.LocalPosition, ctx.ShipPosition);
        AssertOrthonormal(ctx.ShipForward, ctx.ShipUp, ctx.ShipRight);
        Assert.InRange(ctx.Speed01, 0f, 1f);
        Assert.InRange(ctx.Throttle01, 0f, 1f);
        Assert.InRange(ctx.Boost01, 0f, 1f);
        Assert.Equal(ctx.ShipVelocity.Length, ctx.Speed, 4);

        var nose = Vector3.TransformPosition(new Vector3(0f, 0f, -0.5f), ctx.ShipModel);
        var expected = ctx.ShipPosition + ctx.ShipForward * (0.5f * ShipLayout.Length);
        Assert.True((nose - expected).Length < 1e-5f, $"nose off by {(nose - expected).Length}");
        var wing = Vector3.TransformPosition(new Vector3(1f, 0f, 0f), ctx.ShipModel);
        Assert.True((wing - (ctx.ShipPosition + ctx.ShipRight * ShipLayout.Length)).Length < 1e-5f);
        var top = Vector3.TransformPosition(new Vector3(0f, 1f, 0f), ctx.ShipModel);
        Assert.True((top - (ctx.ShipPosition + ctx.ShipUp * ShipLayout.Length)).Length < 1e-5f);
        Assert.True((ctx.ShipPoint(new Vector3(0f, 0f, -0.5f)) - expected).Length < 1e-5f);
    }

    // ---- Chase camera --------------------------------------------------------

    private static (ShipFlight ship, ChaseCamera cam, FlightContext ctx) Rig(Vector3 start, Vector3 fwd)
    {
        var s = new ShipFlight();
        s.Reset(start, fwd, Vector3.UnitY);
        var ctx = Ctx();
        s.WriteTo(ctx);
        var cam = new ChaseCamera();
        cam.Reset(ctx);
        return (s, cam, ctx);
    }

    private static void Frame(ShipFlight s, ChaseCamera cam, FlightContext ctx, ShipInput input, float dt)
    {
        ctx.Dt = dt;
        ctx.Time += dt;
        s.Update(dt, input, ctx);
        s.WriteTo(ctx);
        cam.Update(ctx);
    }

    [Fact]
    public void Camera_Reset_SnapsDirectlyBehind()
    {
        var (_, cam, ctx) = Rig(new Vector3(30f, 0f, 0f), -Vector3.UnitZ);
        var toShip = ctx.ShipPosition - cam.Eye;
        Assert.True(Vector3.Dot(toShip.Normalized(), ctx.ShipForward) > 0.95f);
        Assert.True(Vector3.Dot(cam.Eye - ctx.ShipPosition, ctx.ShipUp) > 0f); // above
        Assert.Equal(ChaseCamera.BaseFovDeg, cam.FovDeg, 3);
    }

    [Fact]
    public void Camera_StaysBehindShip_AndOutsideThePlanet()
    {
        // Skim the planet so the eye is tested against the shell too.
        var (s, cam, ctx) = Rig(new Vector3(0f, 0f, 3.6f), new Vector3(1f, 0f, -0.2f));
        for (int i = 0; i < 60 * 10; i++)
        {
            float t = i / 60f;
            var input = Script(t % 9f);
            input.Pitch = t < 3f ? -1f : input.Pitch; // dive into the planet first
            Frame(s, cam, ctx, input, 1f / 60f);
            Assert.True(Vector3.Dot(ctx.ShipForward, ctx.ShipPosition - cam.Eye) > 0f, $"t={t}");
            Assert.True((cam.Eye - ctx.AnchorPosition).Length > Saturn, $"t={t}");
        }
    }

    [Fact]
    public void Camera_Boost_PullsBackAndWidensFov_ThenEasesBack()
    {
        var (s, cam, ctx) = Rig(new Vector3(30f, 0f, 0f), -Vector3.UnitZ);
        for (int i = 0; i < 60; i++) Frame(s, cam, ctx, new ShipInput(), 1f / 60f);
        float cruiseDist = (cam.Eye - ctx.ShipPosition).Length;
        for (int i = 0; i < 120; i++) Frame(s, cam, ctx, new ShipInput { Boost = true }, 1f / 60f);
        Assert.True(cam.FovDeg > ChaseCamera.BoostFovDeg - 4f, $"fov {cam.FovDeg}");
        // A modest pull-back: enough to feel the kick, not enough to lose the ship.
        Assert.True((cam.Eye - ctx.ShipPosition).Length > cruiseDist * 1.08f);
        for (int i = 0; i < 300; i++) Frame(s, cam, ctx, new ShipInput(), 1f / 60f);
        Assert.InRange(cam.FovDeg, 59.5f, 61f);
        Assert.InRange((cam.Eye - ctx.ShipPosition).Length, cruiseDist * 0.97f, cruiseDist * 1.03f);
    }

    [Fact]
    public void Camera_ZeroDt_KeepsPose()
    {
        var (s, cam, ctx) = Rig(new Vector3(30f, 0f, 0f), -Vector3.UnitZ);
        for (int i = 0; i < 50; i++) Frame(s, cam, ctx, new ShipInput { Yaw = 1f, Boost = true }, 1f / 60f);
        var eye = cam.Eye; var look = cam.LookAt; var up = cam.UpVector; float fov = cam.FovDeg;
        Frame(s, cam, ctx, new ShipInput { Yaw = -1f }, 0f);
        Assert.Equal(eye, cam.Eye);
        Assert.Equal(look, cam.LookAt);
        Assert.Equal(up, cam.UpVector);
        Assert.Equal(fov, cam.FovDeg);
    }

    [Fact]
    public void Camera_RollLagsTheShip()
    {
        var (s, cam, ctx) = Rig(new Vector3(30f, 0f, 0f), -Vector3.UnitZ);
        for (int i = 0; i < 15; i++) Frame(s, cam, ctx, new ShipInput { Roll = 1f }, 1f / 60f);
        // Ship has rolled noticeably; the camera has rolled less.
        float shipRoll = MathF.Acos(Math.Clamp(Vector3.Dot(ctx.ShipUp, Vector3.UnitY), -1f, 1f));
        float camRoll = MathF.Acos(Math.Clamp(Vector3.Dot(cam.UpVector, Vector3.UnitY), -1f, 1f));
        Assert.True(shipRoll > 0.3f);
        Assert.True(camRoll < shipRoll * 0.8f, $"ship {shipRoll} cam {camRoll}");
    }

    [Fact]
    public void Camera_InASustainedTurn_ShipLooksBankedIntoTheTurn()
    {
        var (s, cam, ctx) = Rig(new Vector3(30f, 0f, 0f), -Vector3.UnitZ);
        for (int i = 0; i < 150; i++) Frame(s, cam, ctx, new ShipInput { Yaw = 1f }, 1f / 60f);
        var view = (cam.LookAt - cam.Eye).Normalized();
        var camRight = Vector3.Cross(view, cam.UpVector).Normalized();
        // On screen the ship's dorsal side leans right (into the turn) by a visible amount,
        // but less than its full bank: the camera follows part of it.
        float screenBank = MathF.Atan2(Vector3.Dot(ctx.ShipUp, camRight), Vector3.Dot(ctx.ShipUp, cam.UpVector));
        float deg = MathHelper.RadiansToDegrees(screenBank);
        Assert.InRange(deg, 10f, MathHelper.RadiansToDegrees(s.BankRad) - 5f);
        // The camera itself rolled the same way as the ship (horizon tilts), not against it.
        Assert.True(Vector3.Dot(cam.UpVector, Vector3.Cross(ctx.ShipForward, Vector3.UnitY).Normalized()) > 0.05f);
    }

    [Fact]
    public void Camera_FramesShipBelowCentre_AboutOneSixthWide()
    {
        var (s, cam, ctx) = Rig(new Vector3(30f, 0f, 0f), -Vector3.UnitZ);
        for (int i = 0; i < 120; i++) Frame(s, cam, ctx, new ShipInput(), 1f / 60f);
        var c = new Camera { Aspect = 16f / 9f };
        cam.ApplyTo(c);
        var vp = c.ViewMatrix * c.ProjectionMatrix;
        Vector2 Ndc(Vector3 p)
        {
            var h = new Vector4(p, 1f) * vp;
            return h.Xy / h.W;
        }
        var centre = Ndc(ctx.ShipPosition);
        Assert.InRange(centre.Y, -0.5f, -0.05f);
        Assert.InRange(centre.X, -0.02f, 0.02f);
        var l = Ndc(ctx.ShipPoint(ShipLayout.WingTips[0]));
        var r = Ndc(ctx.ShipPoint(ShipLayout.WingTips[1]));
        float widthFraction = MathF.Abs(r.X - l.X) / 2f;
        Assert.InRange(widthFraction, 1f / 6f * 0.75f, 1f / 6f * 1.25f);
    }

    [Fact]
    public void ApplyTo_SetsViewConsistentWithEye()
    {
        var (s, cam, ctx) = Rig(new Vector3(30f, 0f, 0f), -Vector3.UnitZ);
        for (int i = 0; i < 90; i++) Frame(s, cam, ctx, new ShipInput { Yaw = 0.6f, Roll = 0.3f, Boost = true }, 1f / 60f);
        var c = new Camera();
        cam.ApplyTo(c);
        Assert.Equal(cam.Eye, c.Eye);
        Assert.Equal(cam.Eye, c.EyeOverride);
        Assert.Equal(cam.LookAt, c.Target);
        Assert.Equal(cam.FovDeg, c.FovDeg);
        Assert.Equal((cam.Eye - cam.LookAt).Length, c.Distance, 4);
        var view = c.ViewMatrix;
        var eyeInView = Vector3.TransformPosition(cam.Eye, view);
        Assert.True(eyeInView.Length < 1e-4f, $"eye in view space {eyeInView}");
        var lookInView = Vector3.TransformPosition(cam.LookAt, view);
        Assert.True(lookInView.Z < 0f && MathF.Abs(lookInView.X) < 1e-4f && MathF.Abs(lookInView.Y) < 1e-4f);
        var upInView = Vector3.TransformVector(cam.UpVector, view);
        Assert.True(upInView.Y > 0.999f);
        Assert.True(Vector3.Dot(ctx.ShipForward, ctx.ShipPosition - cam.Eye) > 0f);
    }

    private static void AssertOrthonormal(Vector3 f, Vector3 u, Vector3 r)
    {
        Assert.InRange(f.Length, 0.999f, 1.001f);
        Assert.InRange(u.Length, 0.999f, 1.001f);
        Assert.InRange(r.Length, 0.999f, 1.001f);
        Assert.InRange(Vector3.Dot(f, u), -1e-3f, 1e-3f);
        Assert.InRange(Vector3.Dot(f, r), -1e-3f, 1e-3f);
        Assert.InRange(Vector3.Dot(u, r), -1e-3f, 1e-3f);
    }
}
