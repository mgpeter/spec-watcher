using System.Text.Json;
using SpecWatcher;
using Xunit;

namespace SpecWatcher.Tests;

/// <summary>
/// End-to-end coverage of the headless path over a real temp specs folder: scan (as ExecuteAsync
/// does) then <see cref="HeadlessRunner.Run"/>, asserting the documented exit-code contract.
/// </summary>
public class HeadlessEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sw-headless-" + Guid.NewGuid().ToString("N"));

    public HeadlessEndToEndTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string SpecsDir => Path.Combine(_root, "docs", "specs");

    private void MakeSpec(string folder, string status, string tasks)
    {
        var dir = Path.Combine(SpecsDir, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "spec.md"), $"# Spec\n\n> Spec: {folder}\n> Status: {status}\n");
        File.WriteAllText(Path.Combine(dir, "tasks.md"), tasks);
    }

    private WatchSettings Settings(string format = "table", string? failOn = null, int? minProgress = null, bool once = false) => new()
    {
        RepoPath = _root,
        SpecsPath = Path.Combine("docs", "specs"),
        Format = format,
        FailOn = failOn,
        MinProgress = minProgress,
        Once = once,
    };

    private static async Task<(int Code, string Output)> RunHeadless(WatchSettings settings)
    {
        var result = await SpecScanner.ScanAsync(settings.ResolvedSpecsDir, DateTimeOffset.Now, settings.ToDriftOptions());
        var writer = new StringWriter();
        var code = HeadlessRunner.Run(settings, result, writer);
        return (code, writer.ToString());
    }

    [Fact]
    public async Task Once_Emits_Board_And_Exits_0()
    {
        MakeSpec("2026-07-20-a", "Complete", "- [x] one\n");
        var settings = Settings(once: true);

        Assert.True(settings.WantsHeadless);   // --once routes to the headless path (no TUI)
        var (code, output) = await RunHeadless(settings);

        Assert.Equal(0, code);
        Assert.Contains("2026-07-20-a", output);
    }

    [Fact]
    public async Task Empty_Dir_Exits_0()
    {
        Directory.CreateDirectory(SpecsDir);   // exists, no spec folders
        var (code, _) = await RunHeadless(Settings(once: true));
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task Missing_Dir_Exits_1()
    {
        // SpecsDir was never created.
        var (code, _) = await RunHeadless(Settings(format: "json", once: true));
        Assert.Equal(1, code);
    }

    [Fact]
    public async Task Missing_Dir_Json_Populates_Error()
    {
        var (code, output) = await RunHeadless(Settings(format: "json"));
        Assert.Equal(1, code);
        var root = JsonDocument.Parse(output).RootElement;
        Assert.Equal(JsonValueKind.String, root.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task FailOn_Planning_On_Planning_Spec_Exits_2()
    {
        MakeSpec("2026-07-20-p", "Planning", "- [ ] one\n- [ ] two\n");
        var (code, _) = await RunHeadless(Settings(failOn: "planning"));
        Assert.Equal(2, code);
    }

    [Fact]
    public async Task MinProgress_On_Behind_Spec_Exits_2()
    {
        MakeSpec("2026-07-20-b", "In progress", "- [x] one\n- [ ] two\n- [ ] three\n");   // 33%
        var (code, _) = await RunHeadless(Settings(minProgress: 80));
        Assert.Equal(2, code);
    }

    [Fact]
    public async Task Clean_Board_Exits_0()
    {
        MakeSpec("2026-07-20-done", "Complete", "- [x] one\n- [x] two\n");
        var (code, _) = await RunHeadless(Settings(failOn: "planning,in-progress", minProgress: 80));
        Assert.Equal(0, code);
    }
}
