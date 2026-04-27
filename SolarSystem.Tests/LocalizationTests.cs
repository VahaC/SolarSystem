using System.IO;
using System.Text.Json;
using Xunit;

namespace SolarSystem.Tests;

/// <summary>
/// Covers the localisation table, the new <c>ui.help.hint</c> discovery string
/// added for the small-monitor UX redesign, and the resilience of
/// <see cref="Localization.SetLanguage"/> against missing or malformed files
/// (the bug that masked the Ukrainian translation when <c>lang.uk.json</c>
/// was corrupted).
/// </summary>
public class LocalizationTests
{
    [Fact]
    public void T_ReturnsEnglishDefault_ForKnownKey()
    {
        Localization.SetLanguage("en");
        Assert.Equal("Controls", Localization.T("ui.help.title"));
    }

    [Fact]
    public void T_ReturnsKeyItself_ForUnknownKey()
    {
        Localization.SetLanguage("en");
        const string missing = "ui.totally.bogus.key.that.does.not.exist";
        Assert.Equal(missing, Localization.T(missing));
    }

    [Fact]
    public void T_HelpHint_IsPresentInEnglishDefaults()
    {
        Localization.SetLanguage("en");
        var hint = Localization.T("ui.help.hint");
        Assert.NotEqual("ui.help.hint", hint); // not falling back to the key
        Assert.Contains("Tab", hint);
        Assert.Contains("F1", hint);
    }

    [Fact]
    public void SetLanguage_FallsBackToEnglish_OnUnknownCode()
    {
        Localization.SetLanguage("zz-not-a-real-locale");
        Assert.Equal("en", Localization.CurrentLanguage);
        Assert.Equal("Controls", Localization.T("ui.help.title"));
    }

    [Fact]
    public void SetLanguage_FallsBackToEnglish_OnMalformedJson()
    {
        // Drop a deliberately broken file next to the binary and try to load
        // it. The catch in SetLanguage should swallow the exception and leave
        // the active table at English instead of crashing the app.
        string dataDir = Path.Combine(System.AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);
        string path = Path.Combine(dataDir, "lang.zzbroken.json");
        File.WriteAllText(path, "{ this is : not json, ");
        try
        {
            Localization.SetLanguage("zzbroken");
            Assert.Equal("en", Localization.CurrentLanguage);
            Assert.Equal("Controls", Localization.T("ui.help.title"));
        }
        finally
        {
            File.Delete(path);
            Localization.SetLanguage("en");
        }
    }

    [Fact]
    public void UkrainianLanguageFile_IsValidJson_AndContainsHelpHint()
    {
        // The previous regression replaced the "ui.help.body" entry with a
        // half-finished string literal that broke the whole file. This test
        // pins the invariant: lang.uk.json must parse and must carry the new
        // ui.help.hint key introduced with the small-monitor UI pass.
        string path = Path.Combine(System.AppContext.BaseDirectory, "data", "lang.uk.json");
        Assert.True(File.Exists(path), $"lang.uk.json missing at {path}");
        var json = File.ReadAllText(path);
        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        Assert.NotNull(dict);
        Assert.True(dict!.ContainsKey("ui.help.hint"),
            "ui.help.hint must be translated to Ukrainian.");
        Assert.False(string.IsNullOrWhiteSpace(dict["ui.help.hint"]));
    }

    [Fact]
    public void SetLanguage_Uk_LoadsUkrainianTable()
    {
        try
        {
            Localization.SetLanguage("uk");
            // Sanity: at least one well-known Ukrainian translation must be
            // active. If the file fails to parse SetLanguage silently rolls
            // back to English and this assertion fires.
            Assert.Equal("uk", Localization.CurrentLanguage);
            Assert.Equal("Дата", Localization.T("ui.date"));
        }
        finally
        {
            Localization.SetLanguage("en");
        }
    }
}
