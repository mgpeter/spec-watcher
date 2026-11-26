using Spectre.Console;
using Spectre.Console.Rendering;
using SpecWatcher;
using Xunit;

namespace SpecWatcher.Tests;

public class ThemeTests
{
    [Fact]
    public void Harbour_Is_The_Default()
    {
        Assert.Same(Theme.Harbour, Theme.Default);
    }

    [Fact]
    public void There_Are_Four_Themes_With_Unique_Keys()
    {
        Assert.Equal(["harbour", "paper", "phosphor", "ember"], Theme.All.Select(t => t.Key));
    }

    [Theory]
    [InlineData("paper")]
    [InlineData("PAPER")]
    [InlineData(" Paper ")]
    public void Find_Ignores_Case_And_Whitespace(string name)
    {
        Assert.Same(Theme.Paper, Theme.Find(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("solarized")]
    public void Find_Returns_Null_For_Unknown_Names(string? name)
    {
        Assert.Null(Theme.Find(name));
    }

    [Fact]
    public void Next_Cycles_Through_Every_Theme_And_Wraps()
    {
        var seen = new List<Theme>();
        var t = Theme.Default;
        for (var i = 0; i < Theme.All.Length; i++) { seen.Add(t); t = t.Next(); }

        Assert.Equal(Theme.All, seen);
        Assert.Same(Theme.Default, t);
    }

    [Fact]
    public void Every_Colour_Is_Valid_Hex()
    {
        foreach (var t in Theme.All)
            foreach (var hex in new[] { t.Ground, t.Surface, t.Line, t.Text, t.Muted, t.Faint, t.Accent,
                         t.Complete, t.Progress, t.Planning, t.Warn, t.Error, t.Highlight, t.Selection })
                Assert.Matches("^#[0-9A-F]{6}$", hex);
    }

    [Fact]
    public void Every_Style_Parses_As_Spectre_Markup()
    {
        foreach (var t in Theme.All)
            foreach (var style in new[] { t.FocusStyle, t.DragStyle, t.PickerStyle, t.Accent })
                _ = new Markup($"[{style}]x[/]");
    }

    [Theory]
    [InlineData("ember", "paper", "ember")]     // the flag wins over the saved choice
    [InlineData(null, "paper", "paper")]        // then the saved choice
    [InlineData(null, "nonsense", "harbour")]   // a bad saved value falls back to the default
    [InlineData(null, null, "harbour")]
    public void ResolveTheme_Prefers_Flag_Then_Saved_Then_Default(string? flag, string? saved, string expected)
    {
        Assert.Equal(expected, WatchCommand.ResolveTheme(flag, saved).Key);
    }

    // ---- ThemedSurface ---------------------------------------------------

    private static readonly Color Ground = Color.FromHex("#0B1118");
    private static readonly Color Text = Color.FromHex("#EEF2F7");

    [Fact]
    public void Paint_Fills_Default_Colours_With_The_Theme()
    {
        var painted = ThemedSurface.Paint(new Segment("   "), Ground, Text);

        Assert.Equal(Ground, painted.Style.Background);
        Assert.Equal(Text, painted.Style.Foreground);
    }

    [Fact]
    public void Paint_Keeps_Explicit_Colours_And_Decoration()
    {
        var red = Color.FromHex("#FF0000");
        var blue = Color.FromHex("#0000FF");
        var painted = ThemedSurface.Paint(new Segment("x", new Style(red, blue, Decoration.Bold)), Ground, Text);

        Assert.Equal(red, painted.Style.Foreground);
        Assert.Equal(blue, painted.Style.Background);
        Assert.Equal(Decoration.Bold, painted.Style.Decoration);
    }

    [Fact]
    public void Paint_Leaves_Line_Breaks_Alone()
    {
        Assert.True(ThemedSurface.Paint(Segment.LineBreak, Ground, Text).IsLineBreak);
    }

    // ---- --theme ---------------------------------------------------------

    [Fact]
    public void Theme_Flag_Accepts_A_Known_Theme()
    {
        var settings = new WatchSettings { RepoPath = Directory.GetCurrentDirectory(), Theme = "phosphor" };
        Assert.True(settings.Validate().Successful);
    }

    [Fact]
    public void Theme_Flag_Rejects_An_Unknown_Theme()
    {
        var settings = new WatchSettings { RepoPath = Directory.GetCurrentDirectory(), Theme = "solarized" };
        var result = settings.Validate();

        Assert.False(result.Successful);
        Assert.Contains("harbour", result.Message);
    }
}

public sealed class UserConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sw-config-" + Guid.NewGuid().ToString("N"));
    private string ConfigFile => Path.Combine(_dir, "spec-watcher", "config.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Missing_File_Loads_As_No_Theme()
    {
        Assert.Null(UserConfig.LoadTheme(ConfigFile));
    }

    [Fact]
    public void Save_Creates_The_Folder_And_Round_Trips()
    {
        Assert.True(UserConfig.SaveTheme(ConfigFile, "ember"));
        Assert.Equal("ember", UserConfig.LoadTheme(ConfigFile));
    }

    [Fact]
    public void Save_Keeps_Other_Keys()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile)!);
        File.WriteAllText(ConfigFile, """{ "future": 42, "theme": "paper" }""");

        UserConfig.SaveTheme(ConfigFile, "phosphor");

        var text = File.ReadAllText(ConfigFile);
        Assert.Contains("\"future\": 42", text);
        Assert.Equal("phosphor", UserConfig.LoadTheme(ConfigFile));
    }

    [Fact]
    public void Corrupt_File_Loads_As_No_Theme_And_Is_Replaced_On_Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile)!);
        File.WriteAllText(ConfigFile, "{ not json");

        Assert.Null(UserConfig.LoadTheme(ConfigFile));
        Assert.True(UserConfig.SaveTheme(ConfigFile, "paper"));
        Assert.Equal("paper", UserConfig.LoadTheme(ConfigFile));
    }
}
