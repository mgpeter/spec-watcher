using System.Collections.Immutable;
using SpecWatcher;
using Xunit;

namespace SpecWatcher.Tests;

public class AttentionFlagsTests
{
    private static SpecRow Row(DriftState drift) =>
        new("N", "d", SpecStatus.Planning, "", null, 0, 3, true, "f", "/f", null, drift,
            drift == DriftState.None ? null : "reason");

    // ---- Task 2: FlagField glyph + width ----

    [Theory]
    [InlineData(DriftState.Overstated, "⚠")]
    [InlineData(DriftState.Understated, "⚠")]
    [InlineData(DriftState.Idle, "◍")]
    public void FlagField_Glyph_Per_State(DriftState state, string glyph)
    {
        var (_, plain) = WatchCommand.FlagField(state);
        Assert.Equal(glyph, plain);
        Assert.Single(plain);   // exactly FlagW = 1 wide
    }

    [Fact]
    public void FlagField_None_Is_Blank_Of_Width_One()
    {
        var (_, plain) = WatchCommand.FlagField(DriftState.None);
        Assert.Equal(" ", plain);
    }

    // ---- Task 2: nameW stays aligned and never wraps below the minimum ----

    [Fact]
    public void NameWidth_Reserves_Flag_Column_When_On()
    {
        Assert.Equal(WatchCommand.NameWidth(120, false) - (1 + 1), WatchCommand.NameWidth(120, true));
    }

    [Fact]
    public void NameWidth_Never_Below_Minimum()
    {
        Assert.True(WatchCommand.NameWidth(10, true) >= 16);
    }

    // ---- Task 3: !-jump ----

    private static ImmutableArray<SpecRow> Rows(params DriftState[] drifts) =>
        drifts.Select(Row).ToImmutableArray();

    [Fact]
    public void NextFlagged_Wraps_Forward()
    {
        // flagged at index 1 and 3
        var rows = Rows(DriftState.None, DriftState.Overstated, DriftState.None, DriftState.Idle);
        Assert.Equal(1, WatchCommand.NextFlaggedIndex(rows, 0));
        Assert.Equal(3, WatchCommand.NextFlaggedIndex(rows, 1));
        Assert.Equal(1, WatchCommand.NextFlaggedIndex(rows, 3));   // wraps past the end
    }

    [Fact]
    public void NextFlagged_No_Flags_Returns_Negative()
    {
        Assert.Equal(-1, WatchCommand.NextFlaggedIndex(Rows(DriftState.None, DriftState.None), 0));
    }

    [Fact]
    public void NextFlagged_Single_Flag_Lands_On_Itself()
    {
        var rows = Rows(DriftState.None, DriftState.Overstated, DriftState.None);
        Assert.Equal(1, WatchCommand.NextFlaggedIndex(rows, 1));   // wraps around to itself
    }

    [Fact]
    public void NextFlagged_Empty_Returns_Negative()
    {
        Assert.Equal(-1, WatchCommand.NextFlaggedIndex(ImmutableArray<SpecRow>.Empty, 0));
    }
}
