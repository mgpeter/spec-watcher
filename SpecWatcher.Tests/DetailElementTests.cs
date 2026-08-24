using SpecWatcher;
using Xunit;

namespace SpecWatcher.Tests;

/// <summary>
/// Tests for the bridge between rendered detail lines and source-file coordinates. The whole write
/// path depends on <see cref="DetailElement.SourceLine"/> pointing at the line the user is actually
/// looking at, so alignment is asserted from both ends: the rendered array and the file on disk.
/// </summary>
public class DetailElementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sw-elem-" + Guid.NewGuid().ToString("N"));

    public DetailElementTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static SpecRow Row(string fullPath = "/specs/2026-08-24-x") =>
        new("X", "desc", SpecStatus.Planning, "Planning", null, 0, 0, true, "2026-08-24-x", fullPath);

    private static (string[] Styled, string[] Plain, IReadOnlyList<DetailElement> Elements) Build(
        string? spec, string? tasks, string fullPath = "/specs/2026-08-24-x")
    {
        var (styled, plain, elements) = WatchCommand.BuildDetailLines(Row(fullPath), new SpecDetail(spec, tasks));
        return (styled, plain, elements);
    }

    // ---- status element --------------------------------------------------

    [Fact]
    public void Finds_The_Status_Line_In_Spec_Md()
    {
        var spec = "# Spec\n\n> Spec: X\n> Created: 2026-08-24\n> Status: Planning\n\n## Overview\n";

        var (_, plain, elements) = Build(spec, null);

        var status = Assert.Single(elements, e => e.Kind == DetailElementKind.Status);
        Assert.Equal(4, status.SourceLine);
        Assert.Equal("> Status: Planning", status.SourceText);
        Assert.EndsWith("spec.md", status.SourceFile);
        Assert.Contains("Status: Planning", plain[status.ContentLine]);
    }

    [Fact]
    public void Status_Element_Matches_The_Field_Name_Case_Insensitively()
    {
        var (_, _, elements) = Build("> status: planning\n", null);

        Assert.Single(elements, e => e.Kind == DetailElementKind.Status);
    }

    [Fact]
    public void Only_The_First_Status_Line_Becomes_An_Element()
    {
        var spec = "> Status: Planning\n\ntext\n\n> Status: Complete\n";

        var (_, _, elements) = Build(spec, null);

        var status = Assert.Single(elements, e => e.Kind == DetailElementKind.Status);
        Assert.Equal(0, status.SourceLine);
    }

    [Fact]
    public void No_Status_Element_When_Spec_Has_No_Status_Line()
    {
        var (_, _, elements) = Build("# Spec\n\n> Spec: X\n\n## Overview\n", null);

        Assert.DoesNotContain(elements, e => e.Kind == DetailElementKind.Status);
    }

    [Fact]
    public void No_Status_Element_When_There_Is_No_Spec_Md()
    {
        var (_, _, elements) = Build(null, "- [ ] one\n");

        Assert.DoesNotContain(elements, e => e.Kind == DetailElementKind.Status);
    }

    // ---- checkbox elements -----------------------------------------------

    [Fact]
    public void One_Checkbox_Element_Per_Task_In_Order()
    {
        var (_, _, elements) = Build(null, "- [ ] one\n- [x] two\n- [ ] three\n");

        var boxes = elements.Where(e => e.Kind == DetailElementKind.Checkbox).ToList();
        Assert.Equal(3, boxes.Count);
        Assert.Equal(new[] { 0, 1, 2 }, boxes.Select(b => b.SourceLine));
        Assert.Equal(new[] { false, true, false }, boxes.Select(b => b.IsChecked));
    }

    [Fact]
    public void Skipped_Heading_Does_Not_Desynchronise_The_Source_Counter()
    {
        // "# Spec Tasks" is dropped from the render but still occupies source line 0.
        var (_, _, elements) = Build(null, "# Spec Tasks\n\n- [ ] one\n- [ ] two\n");

        var boxes = elements.Where(e => e.Kind == DetailElementKind.Checkbox).ToList();
        Assert.Equal(new[] { 2, 3 }, boxes.Select(b => b.SourceLine));
    }

    [Fact]
    public void Checkbox_Elements_Point_At_Tasks_Md()
    {
        var (_, _, elements) = Build("> Status: Planning\n", "- [ ] one\n");

        var box = Assert.Single(elements, e => e.Kind == DetailElementKind.Checkbox);
        Assert.EndsWith("tasks.md", box.SourceFile);
    }

    [Fact]
    public void Preserves_Indentation_And_Raw_Text_In_SourceText()
    {
        var (_, _, elements) = Build(null, "- [ ] 1. parent\n  - [x] 1.1 child\n");

        var child = elements.Last(e => e.Kind == DetailElementKind.Checkbox);
        Assert.Equal("  - [x] 1.1 child", child.SourceText);
        Assert.True(child.IsChecked);
    }

    [Fact]
    public void Recognises_Star_Bullets()
    {
        var (_, _, elements) = Build(null, "*  [ ]  one\n");

        Assert.Single(elements, e => e.Kind == DetailElementKind.Checkbox);
    }

    [Fact]
    public void No_Checkbox_Elements_When_There_Is_No_Tasks_Md()
    {
        var (_, _, elements) = Build("> Status: Planning\n", null);

        Assert.DoesNotContain(elements, e => e.Kind == DetailElementKind.Checkbox);
    }

    [Fact]
    public void Non_Checkbox_Lines_Produce_No_Elements()
    {
        var (_, _, elements) = Build(null, "## Tasks\n\nSome prose about the work.\n\n- a plain bullet\n");

        Assert.Empty(elements);
    }

    // ---- alignment guarantees --------------------------------------------

    [Fact]
    public void ContentLine_Indexes_The_Rendered_Arrays()
    {
        var (styled, plain, elements) = Build("> Status: Planning\n", "# Spec Tasks\n\n- [ ] one\n- [x] two\n");

        foreach (var element in elements)
        {
            Assert.InRange(element.ContentLine, 0, plain.Length - 1);
            Assert.Equal(styled.Length, plain.Length);
        }

        var two = elements.Last(e => e.Kind == DetailElementKind.Checkbox);
        Assert.Contains("two", plain[two.ContentLine]);
    }

    [Fact]
    public void Elements_Are_Sorted_By_ContentLine()
    {
        var (_, _, elements) = Build("> Status: Planning\n", "- [ ] one\n- [ ] two\n- [ ] three\n");

        var lines = elements.Select(e => e.ContentLine).ToList();
        Assert.Equal(lines.OrderBy(x => x), lines);
    }

    [Fact]
    public void Crlf_Source_Keeps_Source_Line_Numbers()
    {
        var (_, _, elements) = Build("> Spec: X\r\n> Status: Planning\r\n", "# Spec Tasks\r\n\r\n- [ ] one\r\n");

        Assert.Equal(1, Assert.Single(elements, e => e.Kind == DetailElementKind.Status).SourceLine);
        Assert.Equal(2, Assert.Single(elements, e => e.Kind == DetailElementKind.Checkbox).SourceLine);
    }

    // ---- the rendered document is unchanged ------------------------------

    [Fact]
    public void Rendered_Lines_Are_Unchanged_By_Element_Extraction()
    {
        var (_, plain, _) = Build("# Spec\n\n> Status: Planning\n", "# Spec Tasks\n\n- [x] done\n- [ ] todo\n");

        Assert.Equal("/specs/2026-08-24-x", plain[0]);
        Assert.Equal(string.Empty, plain[1]);
        Assert.Contains("Spec", plain[2]);                  // H1, hash stripped
        Assert.Contains("Status: Planning", plain[4]);      // blockquote, marker stripped
        Assert.Contains("──────── Tasks ────────", plain);
        Assert.Contains(plain, l => l.Contains("✔ done"));  // ticked glyph
        Assert.Contains(plain, l => l.Contains("○ todo"));  // unticked glyph
        Assert.DoesNotContain(plain, l => l.Contains("Spec Tasks"));
    }

    // ---- the coordinates actually drive a write --------------------------

    [Fact]
    public void Element_Coordinates_Drive_A_Real_SpecEditor_Write()
    {
        var dir = Path.Combine(_root, "2026-08-24-bridge");
        Directory.CreateDirectory(dir);
        var specText = "# Spec\n\n> Spec: Bridge\n> Status: Planning\n";
        var tasksText = "# Spec Tasks\n\n## Tasks\n\n- [ ] one\n  - [ ] 1.1 nested\n";
        File.WriteAllText(Path.Combine(dir, "spec.md"), specText);
        File.WriteAllText(Path.Combine(dir, "tasks.md"), tasksText);

        var (_, _, elements) = Build(specText, tasksText, dir);

        var nested = elements.Last(e => e.Kind == DetailElementKind.Checkbox);
        Assert.Equal(EditResult.Ok,
            SpecEditor.ToggleCheckbox(nested.SourceFile, nested.SourceLine, nested.SourceText, toChecked: true));

        var status = Assert.Single(elements, e => e.Kind == DetailElementKind.Status);
        Assert.Equal(EditResult.Ok, SpecEditor.SetStatus(status.SourceFile, "Complete", status.SourceText));

        Assert.Equal("# Spec Tasks\n\n## Tasks\n\n- [ ] one\n  - [x] 1.1 nested\n",
            File.ReadAllText(Path.Combine(dir, "tasks.md")));
        var row = SpecParser.Parse(dir);
        Assert.Equal(SpecStatus.Complete, row.Status);
        Assert.Equal(1, row.Done);
        Assert.Equal(2, row.Total);
    }
}
