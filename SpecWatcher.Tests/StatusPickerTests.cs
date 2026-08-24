using System.Collections.Immutable;
using SpecWatcher;
using Xunit;

namespace SpecWatcher.Tests;

/// <summary>
/// The status dropdown: which choices it offers, where the box lands, and how it is composited over
/// the detail lines. Geometry is shared by the renderer and the hit-tester, so it is tested once.
/// </summary>
public class StatusPickerTests
{
    // ---- choices ---------------------------------------------------------

    [Fact]
    public void Offers_The_Three_Known_Statuses()
    {
        var choices = WatchCommand.StatusChoices("Planning");

        Assert.Equal(new[] { "Planning", "In progress", "Complete" }, choices);
    }

    [Fact]
    public void Adds_The_Current_Freeform_Value_As_A_Keep_As_Is_Entry()
    {
        var choices = WatchCommand.StatusChoices("Blocked on review");

        Assert.Equal(4, choices.Length);
        Assert.Equal("Blocked on review", choices[3]);
    }

    [Fact]
    public void Does_Not_Duplicate_A_Value_That_Normalises_To_A_Known_Status()
    {
        // "Done" and "WIP" already map onto Complete / In progress, so they add nothing.
        Assert.Equal(3, WatchCommand.StatusChoices("Done").Length);
        Assert.Equal(3, WatchCommand.StatusChoices("WIP").Length);
        Assert.Equal(3, WatchCommand.StatusChoices("  planning  ").Length);
    }

    [Fact]
    public void Offers_Only_The_Known_Statuses_When_There_Is_No_Current_Value()
    {
        Assert.Equal(3, WatchCommand.StatusChoices("").Length);
        Assert.Equal(3, WatchCommand.StatusChoices("   ").Length);
    }

    // ---- initial selection -----------------------------------------------

    [Fact]
    public void Opens_On_The_Current_Status()
    {
        var choices = WatchCommand.StatusChoices("In progress");

        Assert.Equal(1, WatchCommand.InitialChoiceIndex(choices, "In progress"));
        Assert.Equal(2, WatchCommand.InitialChoiceIndex(choices, "Complete"));
        Assert.Equal(0, WatchCommand.InitialChoiceIndex(choices, "Planning"));
    }

    [Fact]
    public void Opens_On_A_Freeform_Value_When_That_Is_What_The_Spec_Says()
    {
        var choices = WatchCommand.StatusChoices("Blocked on review");

        Assert.Equal(3, WatchCommand.InitialChoiceIndex(choices, "Blocked on review"));
    }

    [Fact]
    public void Opens_On_The_First_Choice_When_The_Status_Is_Unrecognised_And_Absent()
    {
        Assert.Equal(0, WatchCommand.InitialChoiceIndex(WatchCommand.StatusChoices(""), ""));
    }

    // ---- index movement --------------------------------------------------

    [Fact]
    public void Choice_Index_Wraps_Both_Ways()
    {
        Assert.Equal(1, WatchCommand.NextChoiceIndex(3, 0, +1));
        Assert.Equal(0, WatchCommand.NextChoiceIndex(3, 2, +1));
        Assert.Equal(2, WatchCommand.NextChoiceIndex(3, 0, -1));
        Assert.Equal(0, WatchCommand.NextChoiceIndex(1, 0, +1));
    }

    [Fact]
    public void Choice_Index_Is_Safe_With_No_Choices()
    {
        Assert.Equal(0, WatchCommand.NextChoiceIndex(0, 0, +1));
    }

    // ---- geometry --------------------------------------------------------

    [Fact]
    public void Box_Contains_Only_Its_Own_Cells()
    {
        var box = new OverlayBox(Top: 5, Left: 2, Width: 20, Height: 5);

        Assert.True(box.Contains(5, 2));
        Assert.True(box.Contains(9, 21));
        Assert.False(box.Contains(4, 10));     // above
        Assert.False(box.Contains(10, 10));    // below
        Assert.False(box.Contains(6, 1));      // left
        Assert.False(box.Contains(6, 22));     // right
    }

    [Fact]
    public void Box_Hangs_Below_The_Status_Line()
    {
        var choices = WatchCommand.StatusChoices("Planning");

        var box = WatchCommand.PickerBox(anchorLine: 4, choices, scroll: 0, viewportHeight: 30);

        Assert.Equal(5, box.Top);
        Assert.Equal(choices.Length + 2, box.Height);
    }

    [Fact]
    public void Box_Flips_Above_The_Anchor_When_There_Is_No_Room_Below()
    {
        var choices = WatchCommand.StatusChoices("Planning");

        var box = WatchCommand.PickerBox(anchorLine: 9, choices, scroll: 0, viewportHeight: 11);

        Assert.True(box.Top + box.Height <= 11, "the box must stay inside the viewport");
        Assert.True(box.Top < 9, "with no room below, it should sit above the anchor");
    }

    [Fact]
    public void Box_Never_Starts_Above_The_Scroll_Position()
    {
        var choices = WatchCommand.StatusChoices("Planning");

        var box = WatchCommand.PickerBox(anchorLine: 20, choices, scroll: 20, viewportHeight: 4);

        Assert.True(box.Top >= 20);
    }

    [Fact]
    public void Box_Is_Wide_Enough_For_Its_Widest_Choice()
    {
        var choices = WatchCommand.StatusChoices("A very long freeform status indeed");

        var box = WatchCommand.PickerBox(anchorLine: 0, choices, scroll: 0, viewportHeight: 40);

        Assert.True(box.Width > "A very long freeform status indeed".Length);
    }

    // ---- rows ------------------------------------------------------------

    [Fact]
    public void Rows_Are_A_Bordered_Box_Of_Exactly_The_Box_Width()
    {
        var choices = WatchCommand.StatusChoices("Planning");
        var box = WatchCommand.PickerBox(0, choices, 0, 40);

        var rows = WatchCommand.PickerRows(choices, selected: 1, width: box.Width);

        Assert.Equal(box.Height, rows.Length);
        Assert.All(rows, r => Assert.Equal(box.Width, r.Length));
        Assert.StartsWith("┌", rows[0]);
        Assert.EndsWith("┐", rows[0]);
        Assert.StartsWith("└", rows[^1]);
        Assert.Contains("Status", rows[0]);
    }

    [Fact]
    public void The_Selected_Row_Carries_A_Marker_And_The_Others_Do_Not()
    {
        var choices = WatchCommand.StatusChoices("Planning");
        var rows = WatchCommand.PickerRows(choices, selected: 1, width: WatchCommand.PickerBox(0, choices, 0, 40).Width);

        Assert.Contains("▸", rows[2]);                       // row 0 is the border, so choice 1 is row 2
        Assert.DoesNotContain("▸", rows[1]);
        Assert.Contains("In progress", rows[2]);
    }

    // ---- compositing -----------------------------------------------------

    [Fact]
    public void Splice_Puts_The_Overlay_At_The_Requested_Column()
    {
        var line = WatchCommand.Splice("0123456789", left: 2, overlayPlain: "XX", overlayStyle: "white");

        Assert.Contains("01", line);
        Assert.Contains("[white]XX[/]", line);
        Assert.Contains("456789", line);
    }

    [Fact]
    public void Splice_Pads_A_Line_Too_Short_To_Reach_The_Overlay()
    {
        var line = WatchCommand.Splice("ab", left: 5, overlayPlain: "XX", overlayStyle: "white");

        Assert.Contains("[white]XX[/]", line);
        Assert.StartsWith("ab   ", line);
    }

    [Fact]
    public void Splice_Handles_An_Overlay_Running_Past_The_End_Of_The_Line()
    {
        var line = WatchCommand.Splice("abc", left: 1, overlayPlain: "XXXXX", overlayStyle: "white");

        Assert.Contains("[white]XXXXX[/]", line);
        Assert.StartsWith("a", line);
    }

    [Fact]
    public void Splice_Escapes_The_Covered_Text_So_It_Cannot_Inject_Markup()
    {
        var line = WatchCommand.Splice("[red]hi[/] there", left: 12, overlayPlain: "X", overlayStyle: "white");

        Assert.Contains("[[red]]", line);
    }
}
