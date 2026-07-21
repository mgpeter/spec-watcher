using SpecWatcher;
using Xunit;

namespace SpecWatcher.Tests;

/// <summary>
/// End-to-end scanner enrichment: the real repo enriches (git available, branch + commits), while a
/// temp non-git directory degrades to today's behavior (git fields null/empty, no throw, no hang).
/// </summary>
public class SpecScannerGitTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public async Task ScanAsync_RealRepo_EnrichesWithGit()
    {
        var specsDir = Path.Combine(RepoRoot(), "docs", "specs");
        Assert.True(Directory.Exists(specsDir), "expected the repo's own docs/specs to exist");

        var result = await SpecScanner.ScanAsync(specsDir, DateTimeOffset.Now, DriftOptions.Default);

        Assert.True(result.GitAvailable);
        Assert.False(string.IsNullOrWhiteSpace(result.CurrentBranch));
        Assert.False(result.Rows.IsDefaultOrEmpty);
        Assert.Contains(result.Rows, r => r.LastCommit is not null);   // history touched at least one spec
        Assert.True(result.Rows.Count(r => r.IsCurrent) <= 1);         // at most one current spec
    }

    [Fact]
    public async Task ScanAsync_NonGitDir_DegradesToTodaysBehavior()
    {
        var root = Path.Combine(Path.GetTempPath(), "sw-scan-nogit-" + Guid.NewGuid().ToString("N"));
        var spec = Path.Combine(root, "2026-07-20-alpha");
        Directory.CreateDirectory(spec);
        File.WriteAllText(Path.Combine(spec, "spec.md"), "# Spec\n\n> Spec: alpha\n> Status: Planning\n");
        File.WriteAllText(Path.Combine(spec, "tasks.md"), "- [ ] one\n");
        try
        {
            var result = await SpecScanner.ScanAsync(root, DateTimeOffset.Now, DriftOptions.Default);

            Assert.False(result.GitAvailable);
            Assert.Null(result.CurrentBranch);
            var row = Assert.Single(result.Rows);
            Assert.Null(row.LastCommit);
            Assert.True(row.RecentCommits.IsDefaultOrEmpty);
            Assert.False(row.IsCurrent);
            // Non-git enrichment must not disturb the ordinary parse (name/status still populated).
            Assert.Equal(SpecStatus.Planning, row.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
