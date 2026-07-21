using SpecWatcher;
using Xunit;

namespace SpecWatcher.Tests;

public class SpecScannerDriftTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sw-drift-" + Guid.NewGuid().ToString("N"));

    public SpecScannerDriftTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string MakeSpec(string folder, string status, string tasks)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "spec.md"), $"# Spec\n\n> Spec: {folder}\n> Status: {status}\n");
        File.WriteAllText(Path.Combine(dir, "tasks.md"), tasks);
        return dir;
    }

    [Fact]
    public async Task ScanAsync_Populates_Drift_On_Rows()
    {
        // Planning but with a checked box → Understated.
        MakeSpec("2026-07-20-a", "Planning", "- [x] one\n- [ ] two\n");

        var result = await SpecScanner.ScanAsync(_root, DateTimeOffset.UtcNow, DriftOptions.Default);

        var row = Assert.Single(result.Rows);
        Assert.Equal(DriftState.Understated, row.Drift);
        Assert.False(string.IsNullOrEmpty(row.DriftReason));
    }

    [Fact]
    public async Task ScanAsync_Captures_LastModifiedUtc()
    {
        MakeSpec("2026-07-20-b", "Planning", "- [ ] one\n");

        var result = await SpecScanner.ScanAsync(_root, DateTimeOffset.UtcNow, DriftOptions.Default);

        var row = Assert.Single(result.Rows);
        Assert.NotNull(row.LastModifiedUtc);
    }

    [Fact]
    public async Task ScanAsync_Idle_Off_By_Default_On_Fresh_Files()
    {
        // Fresh files (just written) are recent, so even in-progress specs are never idle by default.
        MakeSpec("2026-07-20-c", "In progress", "- [ ] one\n- [ ] two\n");

        var result = await SpecScanner.ScanAsync(_root, DateTimeOffset.UtcNow, DriftOptions.Default);

        Assert.Equal(DriftState.None, Assert.Single(result.Rows).Drift);
    }
}
