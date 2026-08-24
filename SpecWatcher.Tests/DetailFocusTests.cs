using System.Collections.Immutable;
using SpecWatcher;
using SpecWatcher.Input;
using Xunit;

namespace SpecWatcher.Tests;

/// <summary>
/// Focus-cursor movement, hit-testing and viewport follow. All of it is expressed as pure functions
/// of state so it can be exercised without a console, matching how the flag-jump layer is tested.
/// </summary>
public class DetailFocusTests
{
    private static DetailElement Box(int contentLine, int sourceLine = 0, bool isChecked = false) =>
        new(DetailElementKind.Checkbox, contentLine, "/x/tasks.md", sourceLine, "- [ ] t", isChecked);

    private static ImmutableArray<DetailElement> Elements(params int[] contentLines) =>
        contentLines.Select(l => Box(l)).ToImmutableArray();

    // ---- NextFocusIndex --------------------------------------------------

    [Fact]
    public void NextFocusIndex_Returns_Minus_One_When_There_Are_No_Elements()
    {
        var none = ImmutableArray<DetailElement>.Empty;

        Assert.Equal(-1, WatchCommand.NextFocusIndex(none, -1, +1));
        Assert.Equal(-1, WatchCommand.NextFocusIndex(none, -1, -1));
        Assert.Equal(-1, WatchCommand.NextFocusIndex(none, 3, +1));
    }

    [Fact]
    public void NextFocusIndex_Enters_At_The_First_Element_Going_Forward()
    {
        Assert.Equal(0, WatchCommand.NextFocusIndex(Elements(4, 7, 9), -1, +1));
    }

    [Fact]
    public void NextFocusIndex_Enters_At_The_Last_Element_Going_Backward()
    {
        Assert.Equal(2, WatchCommand.NextFocusIndex(Elements(4, 7, 9), -1, -1));
    }

    [Fact]
    public void NextFocusIndex_Steps_Forward_And_Wraps()
    {
        var elements = Elements(4, 7, 9);

        Assert.Equal(1, WatchCommand.NextFocusIndex(elements, 0, +1));
        Assert.Equal(2, WatchCommand.NextFocusIndex(elements, 1, +1));
        Assert.Equal(0, WatchCommand.NextFocusIndex(elements, 2, +1));
    }

    [Fact]
    public void NextFocusIndex_Steps_Backward_And_Wraps()
    {
        var elements = Elements(4, 7, 9);

        Assert.Equal(1, WatchCommand.NextFocusIndex(elements, 2, -1));
        Assert.Equal(0, WatchCommand.NextFocusIndex(elements, 1, -1));
        Assert.Equal(2, WatchCommand.NextFocusIndex(elements, 0, -1));
    }

    [Fact]
    public void NextFocusIndex_Stays_Put_With_A_Single_Element()
    {
        var one = Elements(5);

        Assert.Equal(0, WatchCommand.NextFocusIndex(one, 0, +1));
        Assert.Equal(0, WatchCommand.NextFocusIndex(one, 0, -1));
    }

    [Fact]
    public void NextFocusIndex_Treats_A_Stale_Index_As_Unfocused()
    {
        var elements = Elements(4, 7);

        Assert.Equal(0, WatchCommand.NextFocusIndex(elements, 99, +1));
        Assert.Equal(1, WatchCommand.NextFocusIndex(elements, 99, -1));
    }

    // ---- ElementAtContentLine --------------------------------------------

    [Fact]
    public void ElementAtContentLine_Finds_The_Element_On_That_Line()
    {
        var elements = Elements(4, 7, 9);

        Assert.Equal(0, WatchCommand.ElementAtContentLine(elements, 4));
        Assert.Equal(1, WatchCommand.ElementAtContentLine(elements, 7));
        Assert.Equal(2, WatchCommand.ElementAtContentLine(elements, 9));
    }

    [Fact]
    public void ElementAtContentLine_Returns_Minus_One_Off_An_Element()
    {
        var elements = Elements(4, 7, 9);

        Assert.Equal(-1, WatchCommand.ElementAtContentLine(elements, 5));
        Assert.Equal(-1, WatchCommand.ElementAtContentLine(elements, 0));
        Assert.Equal(-1, WatchCommand.ElementAtContentLine(elements, 100));
        Assert.Equal(-1, WatchCommand.ElementAtContentLine(ImmutableArray<DetailElement>.Empty, 4));
    }

    // ---- ScrollToShow ----------------------------------------------------

    [Fact]
    public void ScrollToShow_Leaves_An_Already_Visible_Line_Alone()
    {
        Assert.Equal(10, WatchCommand.ScrollToShow(contentLine: 12, scroll: 10, viewportHeight: 5, totalLines: 100));
        Assert.Equal(10, WatchCommand.ScrollToShow(contentLine: 10, scroll: 10, viewportHeight: 5, totalLines: 100));
        Assert.Equal(10, WatchCommand.ScrollToShow(contentLine: 14, scroll: 10, viewportHeight: 5, totalLines: 100));
    }

    [Fact]
    public void ScrollToShow_Scrolls_Up_To_A_Line_Above_The_Viewport()
    {
        Assert.Equal(3, WatchCommand.ScrollToShow(contentLine: 3, scroll: 10, viewportHeight: 5, totalLines: 100));
    }

    [Fact]
    public void ScrollToShow_Scrolls_Down_So_The_Line_Is_Last_Visible()
    {
        Assert.Equal(16, WatchCommand.ScrollToShow(contentLine: 20, scroll: 10, viewportHeight: 5, totalLines: 100));
    }

    [Fact]
    public void ScrollToShow_Never_Goes_Negative_Or_Past_The_End()
    {
        Assert.Equal(0, WatchCommand.ScrollToShow(contentLine: 0, scroll: 3, viewportHeight: 5, totalLines: 100));
        Assert.Equal(95, WatchCommand.ScrollToShow(contentLine: 99, scroll: 0, viewportHeight: 5, totalLines: 100));
    }

    [Fact]
    public void ScrollToShow_Is_Zero_When_Everything_Fits()
    {
        Assert.Equal(0, WatchCommand.ScrollToShow(contentLine: 2, scroll: 0, viewportHeight: 20, totalLines: 4));
    }

    // ---- the Escape ladder -----------------------------------------------

    [Fact]
    public void Escape_Closes_The_Picker_First()
    {
        Assert.Equal(EscapeAction.ClosePicker, WatchCommand.NextEscapeAction(pickerOpen: true, hasFocus: true));
        Assert.Equal(EscapeAction.ClosePicker, WatchCommand.NextEscapeAction(pickerOpen: true, hasFocus: false));
    }

    [Fact]
    public void Escape_Clears_Focus_Before_Leaving_The_Detail_View()
    {
        Assert.Equal(EscapeAction.ClearFocus, WatchCommand.NextEscapeAction(pickerOpen: false, hasFocus: true));
    }

    [Fact]
    public void Escape_Leaves_The_Detail_View_When_Nothing_Is_Focused()
    {
        Assert.Equal(EscapeAction.LeaveDetail, WatchCommand.NextEscapeAction(pickerOpen: false, hasFocus: false));
    }

    // ---- focus styling ---------------------------------------------------

    [Fact]
    public void FocusLine_Bands_The_Line_And_Escapes_Markup()
    {
        var styled = WatchCommand.FocusLine("○ a [not] markup");

        Assert.StartsWith("[black on", styled);
        Assert.EndsWith("[/]", styled);
        Assert.Contains("[[not]]", styled);          // Spectre escaping, so the text renders literally
    }

    [Fact]
    public void FocusLine_Uses_A_Different_Band_To_The_Drag_Selection()
    {
        // Selection uses "black on grey62"; focus must be distinguishable when both are on screen.
        Assert.DoesNotContain("grey62", WatchCommand.FocusLine("anything"));
    }

    // ---- shift plumbing ---------------------------------------------------

    [Fact]
    public void InputEvent_Carries_The_Shift_Modifier()
    {
        Assert.True(InputEvent.FromKey(ConsoleKey.Tab, '\t', shift: true).Shift);
        Assert.False(InputEvent.FromKey(ConsoleKey.Tab, '\t').Shift);
    }

    [Fact]
    public void Tab_Direction_Follows_The_Shift_Modifier()
    {
        Assert.Equal(+1, WatchCommand.FocusDelta(shift: false));
        Assert.Equal(-1, WatchCommand.FocusDelta(shift: true));
    }
}
