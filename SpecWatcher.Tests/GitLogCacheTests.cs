using System.Collections.Immutable;
using SpecWatcher;
using Xunit;

namespace SpecWatcher.Tests;

/// <summary>Tests the HEAD-sha cache decision in isolation with a counting loader (no real git).</summary>
public class GitLogCacheTests
{
    private static ImmutableDictionary<string, ImmutableArray<GitCommit>> Buckets(string folder) =>
        ImmutableDictionary.CreateRange(new[]
        {
            new KeyValuePair<string, ImmutableArray<GitCommit>>(
                folder, ImmutableArray.Create(new GitCommit("s", "s", DateTimeOffset.UtcNow, "a", "x"))),
        });

    [Fact]
    public void SameKey_ReusesCache_LoaderRunsOnce()
    {
        var cache = new GitLogCache();
        var calls = 0;
        Func<ImmutableDictionary<string, ImmutableArray<GitCommit>>?> load = () => { calls++; return Buckets("a"); };

        var first = cache.GetOrLoad("root", "sha1", "main", load, out var loaded1);
        var second = cache.GetOrLoad("root", "sha1", "main", load, out var loaded2);

        Assert.Equal(1, calls);
        Assert.True(loaded1);
        Assert.False(loaded2);   // cache hit
        Assert.Same(first, second);
    }

    [Fact]
    public void HeadShaChange_ReloadsCache()
    {
        var cache = new GitLogCache();
        var calls = 0;
        Func<ImmutableDictionary<string, ImmutableArray<GitCommit>>?> load = () => { calls++; return Buckets("a"); };

        cache.GetOrLoad("root", "sha1", "main", load, out _);
        cache.GetOrLoad("root", "sha2", "main", load, out _);   // HEAD moved

        Assert.Equal(2, calls);
    }

    [Fact]
    public void BranchChange_ReloadsCache_EvenAtSameHead()
    {
        var cache = new GitLogCache();
        var calls = 0;
        Func<ImmutableDictionary<string, ImmutableArray<GitCommit>>?> load = () => { calls++; return Buckets("a"); };

        cache.GetOrLoad("root", "sha1", "main", load, out _);
        cache.GetOrLoad("root", "sha1", "feature", load, out _);   // switched branch, same commit

        Assert.Equal(2, calls);
    }

    [Fact]
    public void LoaderFailure_KeepsStaleBuckets_AndRetriesNextScan()
    {
        var cache = new GitLogCache();
        var good = cache.GetOrLoad("root", "sha1", "main", () => Buckets("a"), out _);
        Assert.NotNull(good);

        // HEAD moves but the log times out (loader returns null): stale buckets are kept.
        var stale = cache.GetOrLoad("root", "sha2", "main", () => null, out var loaded);
        Assert.False(loaded);
        Assert.Same(good, stale);

        // A subsequent successful load for the new key updates the cache.
        var fresh = cache.GetOrLoad("root", "sha2", "main", () => Buckets("b"), out var loaded2);
        Assert.True(loaded2);
        Assert.NotSame(good, fresh);
    }

    [Fact]
    public void LoaderFailure_WithNothingCached_ReturnsNull()
    {
        var cache = new GitLogCache();
        var result = cache.GetOrLoad("root", "sha1", "main", () => null, out var loaded);
        Assert.Null(result);
        Assert.False(loaded);
    }
}
