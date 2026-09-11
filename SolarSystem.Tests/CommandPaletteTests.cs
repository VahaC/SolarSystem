using OpenTK.Windowing.GraphicsLibraryFramework;
using Xunit;

namespace SolarSystem.Tests;

/// <summary>Modal lifecycle of the Ctrl+K palette: query editing, cursor
/// movement, Enter semantics (features keep it open, commands close it).</summary>
public class CommandPaletteTests
{
    private static (FeatureRegistry reg, Dictionary<string, bool> state, List<string> ran) Build()
    {
        Localization.SetLanguage("en");
        var state = new Dictionary<string, bool> { ["orbits"] = true, ["labels"] = true };
        var ran = new List<string>();
        var reg = new FeatureRegistry();
        foreach (var id in new[] { "orbits", "labels" })
        {
            string key = id;
            reg.Add(new Feature
            {
                Id = key, Category = FeatureCategory.Bodies, LabelKey = "ui.settings." + key,
                Get = () => state[key], Set = v => state[key] = v,
            });
        }
        reg.Add(new Command
        {
            Id = "screenshot", Category = FeatureCategory.Interface, LabelKey = "ui.cmd.screenshot",
            Run = () => ran.Add("screenshot"),
        });
        return (reg, state, ran);
    }

    [Fact]
    public void Open_SwallowsTheChordCharacter_ThenAcceptsText()
    {
        var (reg, _, _) = Build();
        var p = new CommandPalette();
        p.Open(reg);
        Assert.True(p.Active);
        p.AppendText("k", reg);          // the 'k' from Ctrl+K
        Assert.Equal("", p.Query);
        p.AppendText("lab", reg);
        Assert.Equal("lab", p.Query);
        Assert.Single(p.Matches);
        Assert.Equal("labels", p.Matches[0].Id);
    }

    [Fact]
    public void Open_FromToolbar_DoesNotEatTheFirstLetterTypedLater()
    {
        var (reg, _, _) = Build();
        var p = new CommandPalette();
        p.Open(reg);
        // No chord character arrives right away (opened by a mouse click);
        // the user starts typing a moment later.
        System.Threading.Thread.Sleep(200);
        p.AppendText("l", reg);
        Assert.Equal("l", p.Query);
    }

    [Fact]
    public void Enter_OnFeature_TogglesAndStaysOpen()
    {
        var (reg, state, _) = Build();
        var p = new CommandPalette();
        p.Open(reg);
        p.HandleKey(Keys.Enter, 0, reg, null);
        Assert.False(state["orbits"]);
        Assert.True(p.Active);
    }

    [Fact]
    public void Enter_OnCommand_RunsAndCloses()
    {
        var (reg, _, ran) = Build();
        var p = new CommandPalette();
        p.Open(reg);
        p.AppendText("", reg); // swallow
        p.AppendText("scr", reg);
        p.HandleKey(Keys.Enter, 0, reg, null);
        Assert.Equal(new[] { "screenshot" }, ran);
        Assert.False(p.Active);
    }

    [Fact]
    public void UpDown_WrapAround_AndBackspaceResetsCursor()
    {
        var (reg, _, _) = Build();
        var p = new CommandPalette();
        p.Open(reg);
        Assert.Equal(3, p.Matches.Count);
        p.HandleKey(Keys.Up, 0, reg, null);
        Assert.Equal(2, p.Selected);
        p.HandleKey(Keys.Down, 0, reg, null);
        Assert.Equal(0, p.Selected);
        p.HandleKey(Keys.Down, 0, reg, null);
        p.AppendText("", reg);
        p.AppendText("x", reg);
        p.HandleKey(Keys.Backspace, 0, reg, null);
        Assert.Equal(0, p.Selected);
        Assert.Equal("", p.Query);
    }

    [Fact]
    public void Escape_Closes()
    {
        var (reg, _, _) = Build();
        var p = new CommandPalette();
        p.Open(reg);
        p.HandleKey(Keys.Escape, 0, reg, null);
        Assert.False(p.Active);
    }
}
