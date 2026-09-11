using OpenTK.Mathematics;
using Xunit;

namespace SolarSystem.Tests;

/// <summary>
/// Covers the small-monitor UX redesign: viewport clamping, mouse-wheel
/// scrolling, off-screen row hit-test suppression, and the existing toggle /
/// slider click routing. These tests poke <see cref="SettingsPanel"/> through
/// the test-only seeding hooks so no OpenGL context is required.
/// </summary>
public class SettingsPanelTests
{
    private static SettingsPanel BuildPanelWithToggles(int count, bool[] state)
    {
        var p = new SettingsPanel { Visible = true };
        for (int i = 0; i < count; i++)
        {
            int idx = i;
            p.Add(new SettingsPanel.ToggleRow
            {
                Label = $"row.{idx}",
                Get = () => state[idx],
                Toggle = () => state[idx] = !state[idx],
            });
        }
        return p;
    }

    [Fact]
    public void HandleScroll_ReturnsFalse_WhenPanelHidden()
    {
        var p = new SettingsPanel { Visible = false };
        p.SeedForTests(contentH: 1000f,
            viewport: new SettingsPanel.Box(0, 0, 300, 400));
        Assert.False(p.HandleScroll(new Vector2(50, 50), -1f));
        Assert.Equal(0f, p.ScrollOffsetForTests);
    }

    [Fact]
    public void HandleScroll_ReturnsFalse_WhenCursorOutsideViewport()
    {
        var p = new SettingsPanel { Visible = true };
        p.SeedForTests(1000f, new SettingsPanel.Box(100, 100, 200, 200));
        // Far away from the viewport rectangle.
        Assert.False(p.HandleScroll(new Vector2(5, 5), -3f));
        Assert.Equal(0f, p.ScrollOffsetForTests);
    }

    [Fact]
    public void HandleScroll_AdvancesByRowHeightPerNotch()
    {
        var p = new SettingsPanel { Visible = true };
        // Plenty of overflow so the clamp is not the limiting factor.
        p.SeedForTests(1000f, new SettingsPanel.Box(0, 0, 300, 200));
        // Negative offsetY in OpenTK = wheel down = scroll content up.
        Assert.True(p.HandleScroll(new Vector2(50, 50), -1f));
        Assert.Equal(22f, p.ScrollOffsetForTests);
        Assert.True(p.HandleScroll(new Vector2(50, 50), -2f));
        Assert.Equal(22f + 44f, p.ScrollOffsetForTests);
    }

    [Fact]
    public void HandleScroll_ClampsAtZero_WhenScrollingUpFromTop()
    {
        var p = new SettingsPanel { Visible = true };
        p.SeedForTests(1000f, new SettingsPanel.Box(0, 0, 300, 200));
        Assert.True(p.HandleScroll(new Vector2(50, 50), +5f));
        Assert.Equal(0f, p.ScrollOffsetForTests);
    }

    [Fact]
    public void HandleScroll_ClampsAtMaxScroll_WhenContentOverflows()
    {
        var p = new SettingsPanel { Visible = true };
        // contentH=500, viewportH=200 => maxScroll = 300.
        p.SeedForTests(500f, new SettingsPanel.Box(0, 0, 300, 200));
        // 50 wheel notches down would be 50*22 = 1100 px without clamp.
        for (int i = 0; i < 50; i++)
            Assert.True(p.HandleScroll(new Vector2(50, 50), -1f));
        Assert.Equal(300f, p.ScrollOffsetForTests);
    }

    [Fact]
    public void HandleScroll_NoMovement_WhenContentFitsViewport()
    {
        var p = new SettingsPanel { Visible = true };
        // contentH < viewportH => maxScroll < 0 => clamped to 0.
        p.SeedForTests(50f, new SettingsPanel.Box(0, 0, 300, 200));
        Assert.True(p.HandleScroll(new Vector2(50, 50), -10f));
        Assert.Equal(0f, p.ScrollOffsetForTests);
    }

    [Fact]
    public void TryHandleClick_FlipsToggleRow_WhenClickInsideBounds()
    {
        var state = new[] { false, false };
        var p = BuildPanelWithToggles(2, state);
        // Manually place the row bounds (Draw normally does this).
        p.RowsForTests[0].Bounds = new SettingsPanel.Box(10, 10, 200, 22);
        p.RowsForTests[1].Bounds = new SettingsPanel.Box(10, 40, 200, 22);

        Assert.True(p.TryHandleClick(new Vector2(50, 20)));
        Assert.True(state[0]);
        Assert.False(state[1]);

        Assert.True(p.TryHandleClick(new Vector2(50, 50)));
        Assert.True(state[0]);
        Assert.True(state[1]);
    }

    [Fact]
    public void TryHandleClick_ReturnsFalse_WhenPanelHidden()
    {
        var state = new[] { false };
        var p = BuildPanelWithToggles(1, state);
        p.Visible = false;
        p.RowsForTests[0].Bounds = new SettingsPanel.Box(10, 10, 200, 22);
        Assert.False(p.TryHandleClick(new Vector2(50, 20)));
        Assert.False(state[0]);
    }

    [Fact]
    public void TryHandleClick_OffscreenRowWithZeroedBounds_DoesNotToggle()
    {
        // Reproduces the small-monitor invariant: when Draw decides a row is
        // outside the visible viewport it sets Bounds to (0,-1,0,0) so a click
        // at the same screen Y can't accidentally flip the hidden row.
        var state = new[] { false };
        var p = BuildPanelWithToggles(1, state);
        p.RowsForTests[0].Bounds = new SettingsPanel.Box(0, -1, 0, 0);
        // _panelHit is false (we never seeded a panel rect) so the click is
        // not consumed at all.
        Assert.False(p.TryHandleClick(new Vector2(50, 50)));
        Assert.False(state[0]);
    }

    [Fact]
    public void SliderRow_NudgeButtons_StepValueWithinBounds()
    {
        float v = 5f;
        var p = new SettingsPanel { Visible = true };
        var slider = new SettingsPanel.SliderRow
        {
            Label = "speed",
            Get = () => v,
            Set = nv => v = nv,
            Min = 0f,
            Max = 10f,
            Step = 1f,
        };
        p.Add(slider);
        slider.Minus = new SettingsPanel.Box(0, 0, 10, 10);
        slider.Plus  = new SettingsPanel.Box(20, 0, 10, 10);
        slider.Track = new SettingsPanel.Box(40, 0, 100, 10);

        Assert.True(p.TryHandleClick(new Vector2(5, 5)));   // minus
        Assert.Equal(4f, v);
        Assert.True(p.TryHandleClick(new Vector2(25, 5)));  // plus
        Assert.Equal(5f, v);

        // Track click at 50% maps to Min + (Max-Min)*0.5 = 5.
        Assert.True(p.TryHandleClick(new Vector2(40 + 50, 5)));
        Assert.Equal(5f, v);

        // Click at far right snaps to Max.
        Assert.True(p.TryHandleClick(new Vector2(40 + 100, 5)));
        Assert.Equal(10f, v);

        // Below Min: clamp.
        for (int i = 0; i < 50; i++) p.TryHandleClick(new Vector2(5, 5));
        Assert.Equal(0f, v);
    }
    [Fact]
    public void TryHandleClick_OnTabHit_SwitchesActiveTab()
    {
        var p = new SettingsPanel { Visible = true };
        p.SeedTabHitForTests(new SettingsPanel.Box(0, 0, 100, 20), FeatureCategory.Developer);
        Assert.True(p.TryHandleClick(new Vector2(50, 10)));
        Assert.Equal(FeatureCategory.Developer, p.ActiveTab);
    }

    [Fact]
    public void TryHandleClick_OnPresetAndToolButtons_InvokesCallbacks()
    {
        var p = new SettingsPanel { Visible = true, ActiveTab = FeatureCategory.PostFx };
        FeaturePreset? applied = null;
        var resets = new List<FeatureCategory>();
        var setAll = new List<(FeatureCategory, bool)>();
        p.OnPreset = pr => applied = pr;
        p.OnReset = c => resets.Add(c);
        p.OnSetAll = (c, v) => setAll.Add((c, v));
        var preset = new FeaturePreset { Id = "perf", LabelKey = "ui.preset.performance" };
        p.SeedPresetHitForTests(new SettingsPanel.Box(0, 0, 50, 20), preset);
        p.SeedToolButtonsForTests(
            reset: new SettingsPanel.Box(0, 30, 50, 20),
            allOn: new SettingsPanel.Box(60, 30, 50, 20),
            allOff: new SettingsPanel.Box(120, 30, 50, 20));

        Assert.True(p.TryHandleClick(new Vector2(10, 10)));
        Assert.Same(preset, applied);
        Assert.True(p.TryHandleClick(new Vector2(10, 40)));
        Assert.Equal(new[] { FeatureCategory.PostFx }, resets);
        Assert.True(p.TryHandleClick(new Vector2(70, 40)));
        Assert.True(p.TryHandleClick(new Vector2(130, 40)));
        Assert.Equal(new[] { (FeatureCategory.PostFx, true), (FeatureCategory.PostFx, false) }, setAll);
    }

    [Fact]
    public void TryHandleClick_UnavailableToggle_ConsumesClickWithoutFlipping()
    {
        var state = new[] { false };
        var p = BuildPanelWithToggles(1, state);
        var row = (SettingsPanel.ToggleRow)p.RowsForTests[0];
        row.Unavailable = () => "ui.gpubelt.unavailable";
        row.Bounds = new SettingsPanel.Box(10, 10, 200, 22);
        Assert.True(p.TryHandleClick(new Vector2(50, 20)));
        Assert.False(state[0]);
    }

    [Fact]
    public void CycleTab_WrapsAroundBothWays()
    {
        var p = new SettingsPanel { ActiveTab = FeatureCategory.Bodies };
        p.CycleTab(-1);
        Assert.Equal(FeatureCategory.Developer, p.ActiveTab);
        p.CycleTab(+1);
        Assert.Equal(FeatureCategory.Bodies, p.ActiveTab);
    }
}
