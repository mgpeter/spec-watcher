using SpecWatcher;
using Xunit;

namespace SpecWatcher.Tests;

/// <summary>
/// Tests the process seam and repo detection against the real repo and a temp non-git directory.
/// The git-missing path (Win32Exception → not started) can't be forced portably, so degradation is
/// exercised via the not-a-repo path, which shares the same "return, never throw" contract.
/// </summary>
public class GitRunnerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Walk up from the test binary to the repo root (the dir containing <c>.git</c>).</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void Run_RevParse_InRealRepo_ReturnsToplevel()
    {
        var root = RepoRoot();
        var result = GitRunner.Run(new[] { "-C", root, "rev-parse", "--show-toplevel" }, Timeout, default);

        Assert.True(result.Started);
        Assert.True(result.Ok);
        Assert.False(result.TimedOut);
        Assert.False(string.IsNullOrWhiteSpace(result.StdOut));
    }

    [Fact]
    public void Run_InNonGitDir_ReturnsNonZero_WithoutThrowing()
    {
        var temp = Path.Combine(Path.GetTempPath(), "sw-nogit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var result = GitRunner.Run(new[] { "-C", temp, "rev-parse", "--show-toplevel" }, Timeout, default);
            Assert.True(result.Started);   // git launched
            Assert.False(result.Ok);       // ...but reported "not a repository"
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void DetectContext_RealRepo_ResolvesContext()
    {
        var root = RepoRoot();
        var ctx = GitEnricher.DetectContext(root, default);

        Assert.NotNull(ctx);
        Assert.False(string.IsNullOrWhiteSpace(ctx!.Value.Root));
        Assert.False(string.IsNullOrWhiteSpace(ctx.Value.HeadSha));
    }

    [Fact]
    public void DetectContext_NonGitDir_ReturnsNull_NoThrow()
    {
        var temp = Path.Combine(Path.GetTempPath(), "sw-nogit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Assert.Null(GitEnricher.DetectContext(temp, default));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }
}
