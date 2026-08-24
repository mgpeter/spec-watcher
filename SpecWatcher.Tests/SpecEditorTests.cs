using System.Text;
using SpecWatcher;
using Xunit;

namespace SpecWatcher.Tests;

/// <summary>
/// Write-path tests. Every case asserts the whole file, not just the edited line: the point of
/// <see cref="SpecEditor"/> is that everything except one line stays byte-identical.
/// </summary>
public class SpecEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sw-edit-" + Guid.NewGuid().ToString("N"));

    public SpecEditorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch { /* best effort */ }
    }

    /// <summary>Write raw bytes so line endings and BOM are exactly what the test asked for.</summary>
    private string Write(string name, string content, bool bom = false)
    {
        var path = Path.Combine(_root, name);
        var body = Encoding.UTF8.GetBytes(content);
        File.WriteAllBytes(path, bom ? Encoding.UTF8.GetPreamble().Concat(body).ToArray() : body);
        return path;
    }

    private static string ReadRaw(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var pre = Encoding.UTF8.GetPreamble();
        var skip = bytes.Length >= pre.Length && bytes.Take(pre.Length).SequenceEqual(pre) ? pre.Length : 0;
        return Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip);
    }

    private static bool HasBom(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var pre = Encoding.UTF8.GetPreamble();
        return bytes.Length >= pre.Length && bytes.Take(pre.Length).SequenceEqual(pre);
    }

    // ---- ToggleCheckbox: the happy paths -------------------------------

    [Fact]
    public void ToggleCheckbox_Ticks_The_Target_Line()
    {
        var path = Write("tasks.md", "# Spec Tasks\n\n- [ ] one\n- [ ] two\n");

        var result = SpecEditor.ToggleCheckbox(path, 2, "- [ ] one", toChecked: true);

        Assert.Equal(EditResult.Ok, result);
        Assert.Equal("# Spec Tasks\n\n- [x] one\n- [ ] two\n", ReadRaw(path));
    }

    [Fact]
    public void ToggleCheckbox_Unticks_The_Target_Line()
    {
        var path = Write("tasks.md", "- [x] one\n- [x] two\n");

        var result = SpecEditor.ToggleCheckbox(path, 1, "- [x] two", toChecked: false);

        Assert.Equal(EditResult.Ok, result);
        Assert.Equal("- [x] one\n- [ ] two\n", ReadRaw(path));
    }

    [Fact]
    public void ToggleCheckbox_Leaves_Identical_Sibling_Lines_Alone()
    {
        var path = Write("tasks.md", "- [ ] same\n- [ ] same\n- [ ] same\n");

        var result = SpecEditor.ToggleCheckbox(path, 1, "- [ ] same", toChecked: true);

        Assert.Equal(EditResult.Ok, result);
        Assert.Equal("- [ ] same\n- [x] same\n- [ ] same\n", ReadRaw(path));
    }

    [Fact]
    public void ToggleCheckbox_Is_Idempotent_When_Already_In_Target_State()
    {
        var path = Write("tasks.md", "- [x] one\n");
        var before = File.ReadAllBytes(path);

        var result = SpecEditor.ToggleCheckbox(path, 0, "- [x] one", toChecked: true);

        Assert.Equal(EditResult.Ok, result);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // ---- ToggleCheckbox: file shape preservation ------------------------

    [Fact]
    public void ToggleCheckbox_Preserves_Crlf_Everywhere()
    {
        var path = Write("tasks.md", "# Spec Tasks\r\n\r\n- [ ] one\r\n- [ ] two\r\n");

        var result = SpecEditor.ToggleCheckbox(path, 2, "- [ ] one", toChecked: true);

        Assert.Equal(EditResult.Ok, result);
        Assert.Equal("# Spec Tasks\r\n\r\n- [x] one\r\n- [ ] two\r\n", ReadRaw(path));
    }

    [Fact]
    public void ToggleCheckbox_Preserves_Mixed_Line_Endings_Outside_The_Edit()
    {
        var path = Write("tasks.md", "- [ ] one\r\n- [ ] two\n- [ ] three\r\n");

        var result = SpecEditor.ToggleCheckbox(path, 1, "- [ ] two", toChecked: true);

        Assert.Equal(EditResult.Ok, result);
        Assert.Equal("- [ ] one\r\n- [x] two\n- [ ] three\r\n", ReadRaw(path));
    }

    [Fact]
    public void ToggleCheckbox_Preserves_Bom()
    {
        var path = Write("tasks.md", "- [ ] one\n", bom: true);

        Assert.Equal(EditResult.Ok, SpecEditor.ToggleCheckbox(path, 0, "- [ ] one", toChecked: true));
        Assert.True(HasBom(path));
        Assert.Equal("- [x] one\n", ReadRaw(path));
    }

    [Fact]
    public void ToggleCheckbox_Does_Not_Add_A_Bom()
    {
        var path = Write("tasks.md", "- [ ] one\n");

        Assert.Equal(EditResult.Ok, SpecEditor.ToggleCheckbox(path, 0, "- [ ] one", toChecked: true));
        Assert.False(HasBom(path));
    }

    [Fact]
    public void ToggleCheckbox_Preserves_Missing_Trailing_Newline()
    {
        var path = Write("tasks.md", "- [ ] one\n- [ ] last");

        var result = SpecEditor.ToggleCheckbox(path, 1, "- [ ] last", toChecked: true);

        Assert.Equal(EditResult.Ok, result);
        Assert.Equal("- [ ] one\n- [x] last", ReadRaw(path));
    }

    [Fact]
    public void ToggleCheckbox_Preserves_Indentation_And_Trailing_Text()
    {
        var path = Write("tasks.md", "- [ ] 1. parent\n  - [ ] 1.1 child with  spacing\n");

        var result = SpecEditor.ToggleCheckbox(path, 1, "  - [ ] 1.1 child with  spacing", toChecked: true);

        Assert.Equal(EditResult.Ok, result);
        Assert.Equal("- [ ] 1. parent\n  - [x] 1.1 child with  spacing\n", ReadRaw(path));
    }

    [Fact]
    public void ToggleCheckbox_Preserves_Star_Bullets_And_Odd_Spacing()
    {
        var path = Write("tasks.md", "*  [ ]  one\n");

        var result = SpecEditor.ToggleCheckbox(path, 0, "*  [ ]  one", toChecked: true);

        Assert.Equal(EditResult.Ok, result);
        Assert.Equal("*  [x]  one\n", ReadRaw(path));
    }

    [Fact]
    public void ToggleCheckbox_Preserves_An_Uppercase_Mark_When_Unticking()
    {
        var path = Write("tasks.md", "- [X] one\n");

        var result = SpecEditor.ToggleCheckbox(path, 0, "- [X] one", toChecked: false);

        Assert.Equal(EditResult.Ok, result);
        Assert.Equal("- [ ] one\n", ReadRaw(path));
    }

    [Fact]
    public void ToggleCheckbox_Ignores_Trailing_Whitespace_When_Matching()
    {
        var path = Write("tasks.md", "- [ ] one   \n");

        var result = SpecEditor.ToggleCheckbox(path, 0, "- [ ] one", toChecked: true);

        Assert.Equal(EditResult.Ok, result);
        Assert.Equal("- [x] one   \n", ReadRaw(path));
    }

    // ---- ToggleCheckbox: refusals --------------------------------------

    [Fact]
    public void ToggleCheckbox_Conflicts_When_The_Line_Changed_On_Disk()
    {
        var path = Write("tasks.md", "- [ ] renamed on disk\n");
        var before = File.ReadAllBytes(path);

        var result = SpecEditor.ToggleCheckbox(path, 0, "- [ ] what we rendered", toChecked: true);

        Assert.Equal(EditResult.Conflict, result);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void ToggleCheckbox_Conflicts_When_The_Line_Index_Is_Gone()
    {
        var path = Write("tasks.md", "- [ ] one\n");
        var before = File.ReadAllBytes(path);

        Assert.Equal(EditResult.Conflict, SpecEditor.ToggleCheckbox(path, 9, "- [ ] one", toChecked: true));
        Assert.Equal(EditResult.Conflict, SpecEditor.ToggleCheckbox(path, -1, "- [ ] one", toChecked: true));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void ToggleCheckbox_Conflicts_When_The_Line_Is_No_Longer_A_Checkbox()
    {
        var path = Write("tasks.md", "just prose now\n");
        var before = File.ReadAllBytes(path);

        var result = SpecEditor.ToggleCheckbox(path, 0, "just prose now", toChecked: true);

        Assert.Equal(EditResult.Conflict, result);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void ToggleCheckbox_NotFound_For_Missing_File()
    {
        var path = Path.Combine(_root, "nope.md");

        Assert.Equal(EditResult.NotFound, SpecEditor.ToggleCheckbox(path, 0, "- [ ] one", toChecked: true));
    }

    [Fact]
    public void ToggleCheckbox_Unwritable_For_A_Read_Only_File()
    {
        var path = Write("tasks.md", "- [ ] one\n");
        var before = File.ReadAllBytes(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        var result = SpecEditor.ToggleCheckbox(path, 0, "- [ ] one", toChecked: true);

        File.SetAttributes(path, FileAttributes.Normal);
        Assert.Equal(EditResult.Unwritable, result);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // ---- SetStatus: rewriting an existing line --------------------------

    [Fact]
    public void SetStatus_Rewrites_The_Existing_Status_Line()
    {
        var path = Write("spec.md", "# Spec\n\n> Spec: X\n> Created: 2026-08-24\n> Status: Planning\n\n## Overview\n");

        var result = SpecEditor.SetStatus(path, "Complete", "> Status: Planning");

        Assert.Equal(EditResult.Ok, result);
        Assert.Equal("# Spec\n\n> Spec: X\n> Created: 2026-08-24\n> Status: Complete\n\n## Overview\n", ReadRaw(path));
    }

    [Fact]
    public void SetStatus_Preserves_The_Blockquote_Prefix_And_Indentation()
    {
        var path = Write("spec.md", "  >   Status: Planning\n");

        var result = SpecEditor.SetStatus(path, "Complete", null);

        Assert.Equal(EditResult.Ok, result);
        Assert.Equal("  >   Status: Complete\n", ReadRaw(path));
    }

    [Fact]
    public void SetStatus_Handles_A_Blockquote_With_No_Space_After_The_Marker()
    {
        var path = Write("spec.md", ">Status: Planning\n");

        Assert.Equal(EditResult.Ok, SpecEditor.SetStatus(path, "In progress", null));
        Assert.Equal(">Status: In progress\n", ReadRaw(path));
    }

    [Fact]
    public void SetStatus_Writes_A_Freeform_Value_Verbatim()
    {
        var path = Write("spec.md", "> Status: Planning\n");

        Assert.Equal(EditResult.Ok, SpecEditor.SetStatus(path, "Complete (pending review)", null));
        Assert.Equal("> Status: Complete (pending review)\n", ReadRaw(path));
    }

    [Fact]
    public void SetStatus_Matches_The_Field_Name_Case_Insensitively()
    {
        var path = Write("spec.md", "> status: planning\n");

        Assert.Equal(EditResult.Ok, SpecEditor.SetStatus(path, "Complete", null));
        Assert.Equal("> Status: Complete\n", ReadRaw(path));
    }

    [Fact]
    public void SetStatus_Targets_The_First_Status_Line_Only()
    {
        var path = Write("spec.md", "> Status: Planning\n\ntext\n\n> Status: Planning\n");

        Assert.Equal(EditResult.Ok, SpecEditor.SetStatus(path, "Complete", null));
        Assert.Equal("> Status: Complete\n\ntext\n\n> Status: Planning\n", ReadRaw(path));
    }

    [Fact]
    public void SetStatus_Preserves_Crlf_And_Bom()
    {
        var path = Write("spec.md", "> Spec: X\r\n> Status: Planning\r\n", bom: true);

        Assert.Equal(EditResult.Ok, SpecEditor.SetStatus(path, "Complete", "> Status: Planning"));
        Assert.True(HasBom(path));
        Assert.Equal("> Spec: X\r\n> Status: Complete\r\n", ReadRaw(path));
    }

    // ---- SetStatus: inserting a missing line ----------------------------

    [Fact]
    public void SetStatus_Inserts_After_The_Header_Block_When_Absent()
    {
        var path = Write("spec.md", "# Spec\n\n> Spec: X\n> Created: 2026-08-24\n\n## Overview\n");

        var result = SpecEditor.SetStatus(path, "Planning", null);

        Assert.Equal(EditResult.Ok, result);
        Assert.Equal("# Spec\n\n> Spec: X\n> Created: 2026-08-24\n> Status: Planning\n\n## Overview\n", ReadRaw(path));
    }

    [Fact]
    public void SetStatus_Inserted_Line_Uses_The_Files_Newline_And_Prefix()
    {
        var path = Write("spec.md", "# Spec\r\n\r\n  > Spec: X\r\n\r\ntext\r\n");

        Assert.Equal(EditResult.Ok, SpecEditor.SetStatus(path, "Planning", null));
        Assert.Equal("# Spec\r\n\r\n  > Spec: X\r\n  > Status: Planning\r\n\r\ntext\r\n", ReadRaw(path));
    }

    [Fact]
    public void SetStatus_Inserts_After_The_First_Blockquote_Run_Only()
    {
        var path = Write("spec.md", "> Spec: X\n\ntext\n\n> a later quote\n");

        Assert.Equal(EditResult.Ok, SpecEditor.SetStatus(path, "Planning", null));
        Assert.Equal("> Spec: X\n> Status: Planning\n\ntext\n\n> a later quote\n", ReadRaw(path));
    }

    [Fact]
    public void SetStatus_Inserts_When_The_Header_Block_Ends_The_File_Without_A_Newline()
    {
        var path = Write("spec.md", "# Spec\n\n> Spec: X");

        Assert.Equal(EditResult.Ok, SpecEditor.SetStatus(path, "Planning", null));
        Assert.Equal("# Spec\n\n> Spec: X\n> Status: Planning", ReadRaw(path));
    }

    // ---- SetStatus: refusals -------------------------------------------

    [Fact]
    public void SetStatus_NoStatusLine_When_There_Is_No_Blockquote_At_All()
    {
        var path = Write("spec.md", "# Spec\n\n## Overview\n\nSome prose.\n");
        var before = File.ReadAllBytes(path);

        var result = SpecEditor.SetStatus(path, "Complete", null);

        Assert.Equal(EditResult.NoStatusLine, result);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void SetStatus_Conflicts_When_The_Status_Line_Changed_On_Disk()
    {
        var path = Write("spec.md", "> Status: In progress\n");
        var before = File.ReadAllBytes(path);

        var result = SpecEditor.SetStatus(path, "Complete", "> Status: Planning");

        Assert.Equal(EditResult.Conflict, result);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void SetStatus_NotFound_For_Missing_File()
    {
        Assert.Equal(EditResult.NotFound, SpecEditor.SetStatus(Path.Combine(_root, "nope.md"), "Complete", null));
    }

    [Fact]
    public void SetStatus_Unwritable_For_A_Read_Only_File()
    {
        var path = Write("spec.md", "> Status: Planning\n");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        var result = SpecEditor.SetStatus(path, "Complete", null);

        File.SetAttributes(path, FileAttributes.Normal);
        Assert.Equal(EditResult.Unwritable, result);
    }

    // ---- Round trip through the parser ----------------------------------

    [Fact]
    public void Edits_Round_Trip_Through_SpecParser()
    {
        var dir = Path.Combine(_root, "2026-08-24-round-trip");
        Directory.CreateDirectory(dir);
        var spec = Path.Combine(dir, "spec.md");
        var tasks = Path.Combine(dir, "tasks.md");
        File.WriteAllText(spec, "# Spec\n\n> Spec: Round Trip\n> Status: Planning\n");
        File.WriteAllText(tasks, "# Spec Tasks\n\n- [ ] one\n- [ ] two\n");

        Assert.Equal(EditResult.Ok, SpecEditor.ToggleCheckbox(tasks, 2, "- [ ] one", toChecked: true));
        Assert.Equal(EditResult.Ok, SpecEditor.SetStatus(spec, "Complete", "> Status: Planning"));

        var row = SpecParser.Parse(dir);
        Assert.Equal(SpecStatus.Complete, row.Status);
        Assert.Equal(1, row.Done);
        Assert.Equal(2, row.Total);
    }
}
