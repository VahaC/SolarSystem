using OpenTK.Mathematics;
using Xunit;

namespace SolarSystem.Tests;

/// <summary>
/// Arcade flight mode: the hidden demo runner's command line and scripted pilot, and the
/// shared ship layout the model, the effects and the flight code all rely on.
/// </summary>
public class FlightDemoTests
{
    [Fact]
    public void FromCli_ReturnsNull_WithoutTheFlag()
    {
        Assert.Null(FlightDemoJob.FromCli([]));
        Assert.Null(FlightDemoJob.FromCli(["--render", "--frames", "3"]));
    }

    [Fact]
    public void FromCli_ParsesEveryOption()
    {
        var job = FlightDemoJob.FromCli(
            ["--flight-demo", "--seconds", "4.5", "--shot-every", "0.5", "--view", "Orbit", "--out", "shots", "--size", "800x450"]);
        Assert.NotNull(job);
        Assert.Equal(4.5, job!.Seconds);
        Assert.Equal(0.5, job.ShotEverySeconds);
        Assert.Equal("orbit", job.View);
        Assert.Equal("shots", job.OutDir);
        Assert.Equal(new Vector2i(800, 450), job.Size);
        Assert.Equal(270, job.TotalFrames);
        Assert.Equal(30, job.ShotEveryFrames);
    }

    [Fact]
    public void FromCli_HasSensibleDefaults()
    {
        var job = FlightDemoJob.FromCli(["--flight-demo"])!;
        Assert.Equal("chase", job.View);
        Assert.Equal(new Vector2i(1600, 900), job.Size);
        Assert.Equal(12 * FlightDemoJob.Fps, job.TotalFrames);
    }

    [Theory]
    [InlineData("--view", "sideways")]
    [InlineData("--size", "big")]
    [InlineData("--frobnicate", "1")]
    public void FromCli_RejectsBadInput(string option, string value)
    {
        Assert.ThrowsAny<Exception>(() => FlightDemoJob.FromCli(["--flight-demo", option, value]));
    }

    [Fact]
    public void Autopilot_FollowsItsScript_AndLoops()
    {
        Assert.True(FlightDemoJob.Autopilot(1.0).Pitch < 0f);            // dips through the ring plane
        Assert.True(FlightDemoJob.Autopilot(3.0).Yaw > 0f);              // right turn
        Assert.True(FlightDemoJob.Autopilot(5.0).Boost);                 // boost
        var climb = FlightDemoJob.Autopilot(7.0);
        Assert.True(climb.Boost && climb.Yaw < 0f && climb.Pitch > 0f);  // boosting left climb
        Assert.Equal(1f, FlightDemoJob.Autopilot(9.0).Roll);             // barrel roll
        Assert.True(FlightDemoJob.Autopilot(11.0).Brake);                // braking turn
        Assert.Equal(FlightDemoJob.Autopilot(3.0).Yaw, FlightDemoJob.Autopilot(15.0).Yaw);
    }

    [Fact]
    public void FromCli_ParsesHudAndStick()
    {
        var plain = FlightDemoJob.FromCli(["--flight-demo"])!;
        Assert.False(plain.ShowHud);
        Assert.Null(plain.Stick);

        var job = FlightDemoJob.FromCli(["--flight-demo", "--hud", "--stick", "0.8,-0.25"])!;
        Assert.True(job.ShowHud);
        Assert.Equal(new Vector2(0.8f, -0.25f), job.Stick);
        Assert.ThrowsAny<Exception>(() => FlightDemoJob.FromCli(["--flight-demo", "--stick", "0.8"]));
    }

    [Fact]
    public void FromCli_ParsesRaceAndTheGateView()
    {
        Assert.False(FlightDemoJob.FromCli(["--flight-demo"])!.Race);
        var job = FlightDemoJob.FromCli(["--flight-demo", "--race", "--view", "gate"])!;
        Assert.True(job.Race);
        Assert.Equal("gate", job.View);
    }

    [Fact]
    public void FromCli_ParsesTheSoloLayer()
    {
        Assert.Null(FlightDemoJob.FromCli(["--flight-demo"])!.Solo);
        Assert.Equal("music", FlightDemoJob.FromCli(["--flight-demo", "--solo", "Music"])!.Solo);
        Assert.ThrowsAny<Exception>(() => FlightDemoJob.FromCli(["--flight-demo", "--solo", "drums"]));
    }

    // ---- Sound: the mix ---------------------------------------------------------------------

    [Fact]
    public void FlightAudio_EnginesDipUnderABigEffect_AndComeBack()
    {
        using var audio = new FlightAudio { Offline = true };
        audio.EnsureStarted();
        Assert.Equal(FlightAudio.EngineLevel, audio.EngineGain, 5);

        // A near miss is small: the engines stay where they are.
        audio.Play(new FlightSoundCue { Event = FlightSoundEvent.RockPass, Strength = 1f });
        Assert.Equal(FlightAudio.EngineLevel, audio.EngineGain, 5);

        // A gate is the loudest thing in the race: the engines make room at once ...
        audio.Play(new FlightSoundCue { Event = FlightSoundEvent.GatePassed, GateCount = 25, Combo = 1 });
        Assert.Equal(FlightAudio.EngineLevel * (1f - FlightAudio.DuckDepth), audio.EngineGain, 5);

        // ... a smaller effect meanwhile does not cut the dip short ...
        audio.Update(FlightAudio.DuckSeconds * 0.1f);
        float dipped = audio.EngineGain;
        audio.Play(new FlightSoundCue { Event = FlightSoundEvent.RockHit, Strength = 0.2f });
        Assert.Equal(dipped, audio.EngineGain, 5);

        // ... and they are back to full shortly after.
        for (int i = 0; i < 60; i++) audio.Update(1f / 60f);
        Assert.Equal(FlightAudio.EngineLevel, audio.EngineGain, 5);

        // A pause (no time passing) and garbage leave the dip alone.
        audio.Play(new FlightSoundCue { Event = FlightSoundEvent.RockHit, Strength = float.NaN });
        audio.Update(0f);
        audio.Update(float.NaN);
        Assert.Equal(FlightAudio.EngineLevel, audio.EngineGain, 5);
    }

    [Fact]
    public void FlightAudio_Solo_RendersOnlyThatLayer()
    {
        static float Energy(string? solo)
        {
            using var audio = new FlightAudio { Offline = true, Solo = solo };
            audio.EnsureStarted();
            audio.State.Active = 1f;
            audio.State.Throttle01 = 0.5f;
            audio.State.Speed01 = 0.25f;
            var block = new float[2 * 4800];
            double sum = 0;
            for (int i = 0; i < 30; i++)        // 3 s
            {
                if (i == 10) audio.Play(new FlightSoundCue { Event = FlightSoundEvent.Go });
                audio.Engine.Render(block, 4800);
                if (i >= 10) foreach (float s in block) sum += s * s;
            }
            return (float)sum;
        }

        float all = Energy(null), engine = Energy("engine"), music = Energy("music"), sfx = Energy("sfx");
        Assert.True(engine > 0f && music > 0f && sfx > 0f);
        // Each stem is quieter than the whole, and together they carry about its energy.
        Assert.True(engine < all && music < all && sfx < all);
        Assert.InRange((engine + music + sfx) / all, 0.5f, 2f);
    }

    [Fact]
    public void RingFrame_Point_WalksAroundTheRing()
    {
        var frame = new RingFrame
        {
            Radial = Vector3.UnitX, Up = Vector3.UnitY, Tangent = Vector3.Cross(Vector3.UnitY, Vector3.UnitX),
            AnchorRadius = 3.4f, RingInner = 4.59f, RingOuter = 8.16f,
        };
        // The start line is on the radial axis; height is along Up.
        var start = frame.Point(6f, 0f, 0.1f);
        Assert.Equal(6f, start.X, 5);
        Assert.Equal(0.1f, start.Y, 5);
        Assert.Equal(0f, start.Z, 5);
        // A positive angle moves in the direction of travel, staying at the same radius.
        var ahead = frame.Point(6f, 0.3f, 0f);
        Assert.True(Vector3.Dot(ahead - frame.Point(6f, 0f, 0f), frame.Tangent) > 0f);
        Assert.Equal(6f, ahead.Length, 4);
        // A quarter turn lands on the tangent axis.
        var quarter = frame.Point(6f, MathF.PI / 2f, 0f);
        Assert.Equal(6f, Vector3.Dot(quarter, frame.Tangent), 4);
    }

    // ---- Mouse steering -----------------------------------------------------------------

    private static readonly Vector2 Press = new(800f, 450f);
    private const float Radius = 160f;

    [Fact]
    public void MouseStick_IsCentredOnThePressPoint_WithADeadZone()
    {
        Assert.Equal(Vector2.Zero, MouseStick.Deflection(Press, Press, Radius));
        // A click that wobbles a few pixels must not steer.
        Assert.Equal(Vector2.Zero, MouseStick.Deflection(Press, Press + new Vector2(6f, -5f), Radius));
        Assert.NotEqual(Vector2.Zero, MouseStick.Deflection(Press, Press + new Vector2(30f, 0f), Radius));
    }

    [Fact]
    public void MouseStick_DragRightTurnsRight_DragUpRaisesTheNose()
    {
        var right = MouseStick.Deflection(Press, Press + new Vector2(100f, 0f), Radius);
        Assert.True(right.X > 0f);
        Assert.Equal(0f, right.Y, 5);

        // Screen Y grows downwards: dragging up is a negative Y offset and means "nose up".
        var up = MouseStick.Deflection(Press, Press + new Vector2(0f, -100f), Radius);
        Assert.True(up.Y > 0f);
        Assert.Equal(0f, up.X, 5);

        var downLeft = MouseStick.Deflection(Press, Press + new Vector2(-80f, 80f), Radius);
        Assert.True(downLeft.X < 0f && downLeft.Y < 0f);
        Assert.Equal(downLeft.X, downLeft.Y, 5);
    }

    [Fact]
    public void MouseStick_GrowsWithTheDrag_AndSaturatesAtTheRing()
    {
        float last = 0f;
        for (float px = 10f; px <= Radius; px += 10f)
        {
            float x = MouseStick.Deflection(Press, Press + new Vector2(px, 0f), Radius).X;
            Assert.True(x >= last, $"deflection fell at {px} px");
            last = x;
        }
        Assert.Equal(1f, MouseStick.Deflection(Press, Press + new Vector2(Radius, 0f), Radius).X, 4);
        // Far beyond the ring it is still full deflection, never more.
        var far = MouseStick.Deflection(Press, Press + new Vector2(900f, -700f), Radius);
        Assert.Equal(1f, far.Length, 4);
        // Softer near the centre than a straight line: half the travel gives less than half.
        Assert.True(MouseStick.Deflection(Press, Press + new Vector2(Radius * 0.5f, 0f), Radius).X < 0.5f);
    }

    [Fact]
    public void MouseStick_RadiusScalesWithTheWindow_WithAFloor()
    {
        Assert.Equal(900f * 0.18f, MouseStick.RadiusPx(900f), 3);
        Assert.True(MouseStick.RadiusPx(1440f) > MouseStick.RadiusPx(900f));
        Assert.Equal(60f, MouseStick.RadiusPx(200f));
    }

    [Fact]
    public void ShipLayout_IsSymmetric_AndInsideItsBounds()
    {
        Assert.Equal(2, ShipLayout.EngineNozzles.Length);
        Assert.Equal(2, ShipLayout.WingTips.Length);
        Assert.Equal(-ShipLayout.EngineNozzles[0].X, ShipLayout.EngineNozzles[1].X);
        Assert.Equal(-ShipLayout.WingTips[0].X, ShipLayout.WingTips[1].X);
        foreach (var p in ShipLayout.EngineNozzles.Concat(ShipLayout.WingTips))
            Assert.True(p.Length <= ShipLayout.BoundingRadius, $"{p} is outside the bounding radius");
        // Engines are at the back (local +Z), behind the wing tips.
        Assert.All(ShipLayout.EngineNozzles, n => Assert.True(n.Z > 0.4f));
    }

    [Fact]
    public void FlightContext_ShipPoint_UsesScaleAndOrientation()
    {
        var ctx = new FlightContext
        {
            ShipPosition = new Vector3(10f, 20f, 30f),
            ShipOrientation = Quaternion.Identity,
        };
        var nose = ctx.ShipPoint(new Vector3(0f, 0f, -0.5f));
        Assert.Equal(10f, nose.X, 5);
        Assert.Equal(20f, nose.Y, 5);
        Assert.Equal(30f - 0.5f * ShipLayout.Length, nose.Z, 5);
    }
}
