using System.Text.Json;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Xunit;

namespace SolarSystem.Tests;

/// <summary>
/// Covers the feature registry that every menu / hotkey / save file is derived
/// from: key-chord parsing and display, exact-modifier dispatch, user
/// overrides from keybindings.json, snapshot / restore, legacy state.json
/// migration, presets and palette search. No OpenGL context needed — the
/// registry only holds closures.
/// </summary>
public class FeatureRegistryTests
{
    private static (FeatureRegistry reg, Dictionary<string, bool> state) Build()
    {
        Localization.SetLanguage("en");
        var state = new Dictionary<string, bool> { ["orbits"] = true, ["bloom"] = true, ["fxaa"] = true, ["hud"] = false };
        var reg = new FeatureRegistry();
        Feature F(string id, FeatureCategory cat, bool def, string? legacy = null) => new()
        {
            Id = id, Category = cat, LabelKey = "ui.settings." + id,
            Get = () => state[id], Set = v => state[id] = v, Default = def, LegacyKey = legacy,
        };
        reg.Add(F("orbits", FeatureCategory.Bodies, true, "ShowOrbits")).WithKey(Keys.O);
        reg.Add(F("bloom", FeatureCategory.PostFx, true, "BloomEnabled"));
        reg.Add(F("fxaa", FeatureCategory.PostFx, true, "FxaaEnabled"));
        reg.Add(F("hud", FeatureCategory.Interface, false, "ShowHud")).WithKey(Keys.GraveAccent);
        int runs = 0;
        reg.Add(new Command
        {
            Id = "screenshot", Category = FeatureCategory.Interface, LabelKey = "ui.cmd.screenshot",
            Run = () => runs++,
        }).WithKey(Keys.F12);
        reg.Add(new Command
        {
            Id = "search", Category = FeatureCategory.Interface, LabelKey = "ui.cmd.search",
            Run = () => { },
        }).WithKey(Keys.F, KeyModifiers.Control);
        reg.AddPreset(new FeaturePreset
        {
            Id = "performance", LabelKey = "ui.preset.performance",
            Overrides = new() { ["bloom"] = false, ["fxaa"] = false },
        });
        return (reg, state);
    }

    // ---- KeyBinding -------------------------------------------------------------

    [Theory]
    [InlineData("Ctrl+Shift+P", Keys.P, KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData("alt+enter", Keys.Enter, KeyModifiers.Alt)]
    [InlineData("F12", Keys.F12, (KeyModifiers)0)]
    [InlineData("~", Keys.GraveAccent, (KeyModifiers)0)]
    [InlineData("Ctrl++", Keys.Equal, KeyModifiers.Control)]
    [InlineData("Num1", Keys.KeyPad1, (KeyModifiers)0)]
    [InlineData("Ctrl+1", Keys.D1, KeyModifiers.Control)]
    [InlineData(",", Keys.Comma, (KeyModifiers)0)]
    public void KeyBinding_TryParse_ParsesChords(string text, Keys key, KeyModifiers mods)
    {
        Assert.True(KeyBinding.TryParse(text, out var b));
        Assert.Equal(key, b.Key);
        Assert.Equal(mods, b.Mods);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Bogus")]
    [InlineData("A+B")]
    public void KeyBinding_TryParse_RejectsGarbage(string text)
    {
        Assert.False(KeyBinding.TryParse(text, out _));
    }

    [Fact]
    public void KeyBinding_DisplayString_RoundTrips()
    {
        foreach (var text in new[] { "Ctrl+Shift+P", "Alt+Enter", "F1", "~", "Space", "Ctrl+K", "Num+" })
        {
            Assert.True(KeyBinding.TryParse(text, out var b));
            Assert.Equal(text, b.ToDisplayString());
        }
    }

    [Fact]
    public void KeyBinding_Matches_IgnoresLockKeysButNotChordModifiers()
    {
        var b = new KeyBinding(Keys.F, KeyModifiers.Control);
        Assert.True(b.Matches(Keys.F, KeyModifiers.Control | KeyModifiers.NumLock | KeyModifiers.CapsLock));
        Assert.False(b.Matches(Keys.F, 0));
        Assert.False(b.Matches(Keys.F, KeyModifiers.Control | KeyModifiers.Shift));
    }

    // ---- Dispatch -------------------------------------------------------------------

    [Fact]
    public void Dispatch_TogglesBoundFeature_AndRaisesBanner()
    {
        var (reg, state) = Build();
        string? banner = null;
        var e = reg.Dispatch(Keys.O, 0, b => banner = b);
        Assert.NotNull(e);
        Assert.False(state["orbits"]);
        Assert.Contains("Orbits", banner);
        Assert.Contains("OFF", banner);
    }

    [Fact]
    public void Dispatch_DistinguishesPlainKeyFromCtrlChord()
    {
        var (reg, _) = Build();
        Assert.Equal("search", reg.Resolve(Keys.F, KeyModifiers.Control)!.Id);
        Assert.Null(reg.Resolve(Keys.F, 0));
        Assert.Null(reg.Dispatch(Keys.Z, 0));
    }

    [Fact]
    public void Add_RejectsDuplicateIds()
    {
        var (reg, state) = Build();
        Assert.Throws<InvalidOperationException>(() => reg.Add(new Feature
        {
            Id = "orbits", Category = FeatureCategory.Bodies, LabelKey = "x",
            Get = () => true, Set = _ => { },
        }));
    }

    [Fact]
    public void Feature_Toggle_IsBlockedWhenUnavailable()
    {
        bool value = false;
        var f = new Feature
        {
            Id = "gpu", Category = FeatureCategory.Developer, LabelKey = "ui.settings.gpubelt",
            Get = () => value, Set = v => value = v, Unavailable = () => "ui.gpubelt.unavailable",
        };
        Assert.False(f.Toggle());
        Assert.False(value);
        f.Apply(true);
        Assert.False(value);
    }

    // ---- keybindings.json overrides --------------------------------------------------

    [Fact]
    public void ApplyBindingOverrides_ReplacesChords_AndUnbindsOnEmpty()
    {
        var (reg, _) = Build();
        var warnings = new List<string>();
        int n = reg.ApplyBindingOverrides(new Dictionary<string, string>
        {
            ["orbits"] = "Ctrl+O, Num1",
            ["hud"] = "",
            ["nope"] = "X",
            ["bloom"] = "Ctrl+Bogus",
        }, warnings.Add);

        Assert.Equal(2, n);
        Assert.Null(reg.Resolve(Keys.O, 0));
        Assert.Equal("orbits", reg.Resolve(Keys.O, KeyModifiers.Control)!.Id);
        Assert.Equal("orbits", reg.Resolve(Keys.KeyPad1, 0)!.Id);
        Assert.Null(reg.Resolve(Keys.GraveAccent, 0));
        Assert.Empty(reg.FindFeature("bloom")!.Bindings); // bad chord => untouched (never had one)
        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public void AllBindings_HaveNoCollisions()
    {
        var (reg, _) = Build();
        var seen = new HashSet<KeyBinding>();
        foreach (var (b, _) in reg.AllBindings())
            Assert.True(seen.Add(b), $"chord {b} bound twice");
    }

    // ---- Persistence -----------------------------------------------------------------

    [Fact]
    public void Snapshot_Restore_RoundTrips()
    {
        var (reg, state) = Build();
        state["orbits"] = false;
        state["hud"] = true;
        var snap = reg.Snapshot();
        Assert.Equal(4, snap.Count);

        state["orbits"] = true;
        state["hud"] = false;
        reg.Restore(snap);
        Assert.False(state["orbits"]);
        Assert.True(state["hud"]);
    }

    [Fact]
    public void Restore_IgnoresUnknownIds_AndKeepsMissingOnes()
    {
        var (reg, state) = Build();
        reg.Restore(new Dictionary<string, bool> { ["zzz"] = true, ["bloom"] = false });
        Assert.False(state["bloom"]);
        Assert.True(state["orbits"]);
    }

    [Fact]
    public void MigrateLegacy_ReadsOldPascalCaseProperties()
    {
        var (reg, _) = Build();
        const string legacyJson = """
            { "Yaw": 0.5, "ShowOrbits": false, "BloomEnabled": true, "ShowHud": true, "FxaaEnabled": "nope" }
            """;
        using var doc = JsonDocument.Parse(legacyJson);
        var d = reg.MigrateLegacy(doc.RootElement);
        Assert.Equal(3, d.Count);
        Assert.False(d["orbits"]);
        Assert.True(d["bloom"]);
        Assert.True(d["hud"]);
        Assert.False(d.ContainsKey("fxaa"));
    }

    [Fact]
    public void Snapshot_SkipsNonPersistedFeatures()
    {
        var reg = new FeatureRegistry();
        bool v = true;
        reg.Add(new Feature
        {
            Id = "record", Category = FeatureCategory.Developer, LabelKey = "ui.settings.record",
            Get = () => v, Set = x => v = x, Persist = false,
        });
        Assert.Empty(reg.Snapshot());
    }

    // ---- Presets / reset --------------------------------------------------------------

    [Fact]
    public void ApplyPreset_ResetsSceneCategories_ThenOverrides()
    {
        var (reg, state) = Build();
        state["orbits"] = false;   // scene category, will be reset to default (true)
        state["hud"] = true;       // Interface: must be left alone
        var perf = reg.Presets[0];
        Assert.False(reg.MatchesPreset(perf));
        reg.ApplyPreset(perf);
        Assert.True(state["orbits"]);
        Assert.False(state["bloom"]);
        Assert.False(state["fxaa"]);
        Assert.True(state["hud"]);
        Assert.True(reg.MatchesPreset(perf));
    }

    [Fact]
    public void ResetCategory_And_SetCategory_OnlyTouchThatTab()
    {
        var (reg, state) = Build();
        reg.SetCategory(FeatureCategory.PostFx, false);
        Assert.False(state["bloom"]);
        Assert.False(state["fxaa"]);
        Assert.True(state["orbits"]);
        reg.ResetCategory(FeatureCategory.PostFx);
        Assert.True(state["bloom"]);
    }

    // ---- Palette search ------------------------------------------------------------------

    [Fact]
    public void Search_EmptyQuery_ListsEverythingInOrder()
    {
        var (reg, _) = Build();
        var all = reg.Search("", 100);
        Assert.Equal(new[] { "orbits", "bloom", "fxaa", "hud", "screenshot", "search" }, all.Select(e => e.Id));
    }

    [Fact]
    public void Search_RanksPrefixAboveSubstring_AndFallsBackToId()
    {
        var (reg, _) = Build();
        // "S" prefix: "Screenshot", "Search body…" beat "FPS / particle HUD" (substring).
        var r = reg.Search("s", 10);
        Assert.Equal("screenshot", r[0].Id);
        Assert.Equal("search", r[1].Id);
        // id-only match.
        Assert.Equal("fxaa", reg.Search("fxa", 10).Single().Id);
        Assert.Empty(reg.Search("qqqq", 10));
    }
}
