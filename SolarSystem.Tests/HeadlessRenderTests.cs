using Xunit;

namespace SolarSystem.Tests;

/// <summary>A7 CLI parsing, including the physics-sandbox <c>--physics</c> flag.</summary>
public class HeadlessRenderTests
{
    [Fact]
    public void FromCli_ReturnsNull_WithoutRenderFlag()
    {
        Assert.Null(HeadlessRenderJob.FromCli([]));
        Assert.Null(HeadlessRenderJob.FromCli(["--physics"]));
    }

    [Fact]
    public void FromCli_ParsesPhysicsAndRealScale()
    {
        var job = HeadlessRenderJob.FromCli(
            ["--render", "--from", "2025-01-01", "--frames", "10", "--dt", "2.5", "--physics", "--real-scale"]);
        Assert.NotNull(job);
        Assert.True(job!.Physics);
        Assert.True(job.RealScale);
        Assert.Equal(10, job.TotalFrames);
        Assert.Equal(2.5, job.DaysPerFrame);
        Assert.Equal((new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc) - OrbitalMechanics.J2000).TotalDays, job.FromSimDays);
    }

    [Fact]
    public void FromCli_DefaultsPhysicsOff()
    {
        var job = HeadlessRenderJob.FromCli(["--render", "--frames", "1"]);
        Assert.NotNull(job);
        Assert.False(job!.Physics);
    }

    [Fact]
    public void FromCli_RejectsUnknownRenderOption()
    {
        Assert.Throws<ArgumentException>(() => HeadlessRenderJob.FromCli(["--render", "--phys"]));
    }
}
