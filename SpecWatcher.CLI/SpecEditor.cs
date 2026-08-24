using System.Text;
using System.Text.RegularExpressions;

namespace SpecWatcher;

/// <summary>The outcome of an attempted write. Expected failures are returned, never thrown.</summary>
public enum EditResult
{
    /// <summary>The file now holds the requested value (possibly it already did).</summary>
    Ok,

    /// <summary>The file does not exist.</summary>
    NotFound,

    /// <summary>The line on disk is not what the caller rendered, so nothing was written.</summary>
    Conflict,

    /// <summary>The file could not be read or written (locked, read-only, no permission).</summary>
    Unwritable,

    /// <summary>spec.md has no blockquote header to put a <c>&gt; Status:</c> line in.</summary>
    NoStatusLine,
}

/// <summary>
/// The only write path in the app. Every edit replaces exactly one line's character span in the raw
/// file text, so indentation, trailing whitespace, line endings elsewhere, the BOM and a missing
/// final newline all survive untouched.
///
/// Writes are optimistically concurrent: the caller passes the line it believes is on disk, and the
/// write is refused with <see cref="EditResult.Conflict"/> if the file has moved on underneath.
/// Like <see cref="SpecParser"/>, everything here is static and testable against fixture files.
/// </summary>
public static partial class SpecEditor
{
    // Same shape as SpecParser.Checkbox(): optional indent, "- [ ]" / "* [x]".
    [GeneratedRegex(@"^\s*[-*]\s*\[(?<mark>[ xX])\]")]
    private static partial Regex Checkbox();

    /// <summary>
    /// Set the mark of the checkbox on <paramref name="lineIndex"/> of a tasks.md.
    /// </summary>
    /// <param name="tasksPath">Absolute path to tasks.md.</param>
    /// <param name="lineIndex">0-based line index, counted the way <c>Split('\n')</c> counts.</param>
    /// <param name="expectedLine">The line as rendered; compared ignoring trailing whitespace.</param>
    /// <param name="toChecked">Target state. Already being in it is a no-op <see cref="EditResult.Ok"/>.</param>
    public static EditResult ToggleCheckbox(string tasksPath, int lineIndex, string expectedLine, bool toChecked)
    {
        if (Load(tasksPath, out var text, out var hadBom) is var load && load != EditResult.Ok)
            return load;

        var spans = LineSpans(text);
        if (lineIndex < 0 || lineIndex >= spans.Count) return EditResult.Conflict;

        var (start, end) = spans[lineIndex];
        var line = text[start..end];
        if (!Matches(line, expectedLine)) return EditResult.Conflict;

        var match = Checkbox().Match(line);
        if (!match.Success) return EditResult.Conflict;

        var markAt = start + match.Groups["mark"].Index;
        var isChecked = text[markAt] is 'x' or 'X';
        if (isChecked == toChecked) return EditResult.Ok;      // already there; leave the bytes alone

        var edited = string.Concat(text.AsSpan(0, markAt), toChecked ? "x" : " ", text.AsSpan(markAt + 1));
        return Save(tasksPath, edited, hadBom);
    }

    /// <summary>
    /// Set the value of the <c>&gt; Status:</c> line in a spec.md, preserving the blockquote prefix
    /// and indentation. When no such line exists, one is inserted after the first blockquote run.
    /// </summary>
    /// <param name="specPath">Absolute path to spec.md.</param>
    /// <param name="newValue">The status text to write (freeform values are written verbatim).</param>
    /// <param name="expectedLine">The line as rendered, or null to skip the conflict check.</param>
    public static EditResult SetStatus(string specPath, string newValue, string? expectedLine)
    {
        if (Load(specPath, out var text, out var hadBom) is var load && load != EditResult.Ok)
            return load;

        var value = (newValue ?? string.Empty).Trim();
        var field = value.Length == 0 ? "Status:" : $"Status: {value}";

        var spans = LineSpans(text);
        var statusLine = FindField(text, spans, "Status");

        if (statusLine >= 0)
        {
            var (start, end) = spans[statusLine];
            var line = text[start..end];
            if (!Matches(line, expectedLine)) return EditResult.Conflict;

            var edited = string.Concat(text.AsSpan(0, start), BlockquotePrefix(line) + field, text.AsSpan(end));
            return Save(specPath, edited, hadBom);
        }

        // No Status line: append one to the header block, which is the first run of blockquote lines.
        var first = IndexOfBlockquote(text, spans);
        if (first < 0) return EditResult.NoStatusLine;

        var last = first;
        while (last + 1 < spans.Count && IsBlockquote(text, spans[last + 1])) last++;

        var prefix = BlockquotePrefix(text[spans[last].Start..spans[last].End]);
        if (last + 1 < spans.Count)
        {
            // Reuse the terminator that ends the header block's last line, so the inserted line
            // matches its neighbours even in a mixed-ending file.
            var at = spans[last + 1].Start;
            var newline = text[spans[last].End..at];
            return Save(specPath, string.Concat(text.AsSpan(0, at), prefix + field + newline, text.AsSpan(at)), hadBom);
        }

        // The header block ends the file with no trailing newline.
        return Save(specPath, text + DominantNewline(text) + prefix + field, hadBom);
    }

    // ---- file shape helpers ---------------------------------------------

    /// <summary>
    /// Character spans of every line, excluding its terminator. Counts lines the way
    /// <c>Split('\n')</c> does, so a trailing newline yields a final empty span.
    /// </summary>
    private static List<(int Start, int End)> LineSpans(string text)
    {
        var spans = new List<(int, int)>();
        var pos = 0;
        while (true)
        {
            var nl = text.IndexOf('\n', pos);
            var lineEnd = nl < 0 ? text.Length : nl;
            var end = lineEnd > pos && text[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
            spans.Add((pos, end));
            if (nl < 0) break;
            pos = nl + 1;
        }
        return spans;
    }

    /// <summary>Optimistic-concurrency check: trailing whitespace is not part of the identity.</summary>
    private static bool Matches(string line, string? expected) =>
        expected is null || line.TrimEnd().Equals(expected.TrimEnd(), StringComparison.Ordinal);

    private static string DominantNewline(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

    // ---- blockquote helpers (mirror SpecParser.FindBlockquoteField) ------

    private static bool IsBlockquote(string text, (int Start, int End) span) =>
        text[span.Start..span.End].TrimStart().StartsWith('>');

    private static int IndexOfBlockquote(string text, List<(int Start, int End)> spans)
    {
        for (var i = 0; i < spans.Count; i++)
            if (IsBlockquote(text, spans[i])) return i;
        return -1;
    }

    /// <summary>Index of the first <c>&gt; Field:</c> line, or -1.</summary>
    private static int FindField(string text, List<(int Start, int End)> spans, string field)
    {
        for (var i = 0; i < spans.Count; i++)
        {
            var line = text[spans[i].Start..spans[i].End];
            if (SpecParser.BlockquoteKey(line) is { } key && key.Equals(field, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>Everything up to and including the <c>&gt;</c> and the whitespace after it.</summary>
    private static string BlockquotePrefix(string line)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith('>')) return string.Empty;
        var indent = line.Length - trimmed.Length;
        var afterMarker = trimmed[1..];
        var gap = afterMarker.Length - afterMarker.TrimStart().Length;
        return line[..(indent + 1 + gap)];
    }

    // ---- raw I/O ---------------------------------------------------------

    /// <summary>Read the file as text, reporting whether it carried a UTF-8 BOM.</summary>
    private static EditResult Load(string path, out string text, out bool hadBom)
    {
        text = string.Empty;
        hadBom = false;
        try
        {
            if (!File.Exists(path)) return EditResult.NotFound;
            var bytes = File.ReadAllBytes(path);
            hadBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var skip = hadBom ? 3 : 0;
            text = Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip);
            return EditResult.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return EditResult.Unwritable;
        }
    }

    private static EditResult Save(string path, string text, bool hadBom)
    {
        try
        {
            File.WriteAllText(path, text, new UTF8Encoding(hadBom));
            return EditResult.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return EditResult.Unwritable;
        }
    }
}
