using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SpecWatcher;

/// <summary>
/// Pure parsing of a single spec folder (Agent OS convention) into a <see cref="SpecRow"/>.
/// Every method is static and side-effect free apart from reading the given files,
/// which keeps it straightforward to unit test against fixture folders.
/// </summary>
public static partial class SpecParser
{
    private const int MaxDescriptionLength = 200;

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}-")]
    private static partial Regex DatePrefix();

    // A markdown task checkbox: optional indent, "- [ ]" / "- [x]" / "* [X]".
    [GeneratedRegex(@"^\s*[-*]\s*\[(?<mark>[ xX])\]")]
    private static partial Regex Checkbox();

    /// <summary>Parse the spec folder at <paramref name="folderPath"/>. Never throws for a
    /// well-formed directory; missing sub-files simply yield fallbacks.</summary>
    public static SpecRow Parse(string folderPath)
    {
        var fullPath = Path.GetFullPath(folderPath);
        var folder = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var specText = TryRead(Path.Combine(folderPath, "spec.md"));
        var liteText = TryRead(Path.Combine(folderPath, "spec-lite.md"));
        var tasksText = TryRead(Path.Combine(folderPath, "tasks.md"));

        var specName = FindBlockquoteField(specText, "Spec");
        var name = !string.IsNullOrWhiteSpace(specName) ? specName! : DeslugFolder(folder);

        var statusRaw = FindBlockquoteField(specText, "Status") ?? string.Empty;
        var status = NormaliseStatus(statusRaw);

        var created = ParseDate(FindBlockquoteField(specText, "Created")) ?? FolderDate(folder);

        var description = FirstParagraph(liteText) ?? OverviewParagraph(specText) ?? string.Empty;
        description = Truncate(CollapseWhitespace(description), MaxDescriptionLength);

        var (done, total, hasTasks) = CountCheckboxes(tasksText);

        // A spec with a full tasks.md and every box checked but a stale "Planning"/blank
        // status still reads as effectively complete; surface that.
        if (status == SpecStatus.Unknown && hasTasks && total > 0 && done == total)
            status = SpecStatus.Complete;

        return new SpecRow(name, description, status, statusRaw.Trim(), created, done, total, hasTasks, folder, fullPath);
    }

    /// <summary>Read the full spec.md and tasks.md text for the detail view (fresh from disk).</summary>
    public static SpecDetail LoadDetail(string fullPath)
    {
        var specText = TryRead(Path.Combine(fullPath, "spec.md"));
        var tasksText = TryRead(Path.Combine(fullPath, "tasks.md"));
        return new SpecDetail(specText, tasksText);
    }

    private static string? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Find a <c>&gt; Field: value</c> line in a spec.md blockquote header.</summary>
    internal static string? FindBlockquoteField(string? text, string field)
    {
        if (text is null) return null;
        foreach (var rawLine in EnumerateLines(text))
        {
            var line = rawLine.TrimStart();
            if (!line.StartsWith('>')) continue;
            var content = line[1..].TrimStart();
            var colon = content.IndexOf(':');
            if (colon <= 0) continue;
            var key = content[..colon].Trim();
            if (key.Equals(field, StringComparison.OrdinalIgnoreCase))
                return content[(colon + 1)..].Trim();
        }
        return null;
    }

    internal static SpecStatus NormaliseStatus(string raw)
    {
        var s = raw.TrimStart().ToLowerInvariant();
        if (s.Length == 0) return SpecStatus.Unknown;
        if (s.StartsWith("complete") || s.StartsWith("done") || s.StartsWith("shipped"))
            return SpecStatus.Complete;
        if (s.StartsWith("in progress") || s.StartsWith("in-progress") || s.StartsWith("wip") || s.StartsWith("started"))
            return SpecStatus.InProgress;
        if (s.StartsWith("planning") || s.StartsWith("planned") || s.StartsWith("proposed") || s.StartsWith("draft"))
            return SpecStatus.Planning;
        return SpecStatus.Unknown;
    }

    /// <summary>Count "- [ ]" / "- [x]" checkboxes. Returns (done, total, foundTasksFile).</summary>
    internal static (int Done, int Total, bool HasTasks) CountCheckboxes(string? tasksText)
    {
        if (tasksText is null) return (0, 0, false);
        int done = 0, total = 0;
        foreach (var line in EnumerateLines(tasksText))
        {
            var m = Checkbox().Match(line);
            if (!m.Success) continue;
            total++;
            if (m.Groups["mark"].Value is "x" or "X") done++;
        }
        return (done, total, true);
    }

    /// <summary>First non-heading, non-blank paragraph (consecutive text lines joined).</summary>
    internal static string? FirstParagraph(string? text)
    {
        if (text is null) return null;
        var sb = new StringBuilder();
        foreach (var raw in EnumerateLines(text))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                if (sb.Length > 0) break; // end of the first paragraph
                continue;                 // leading blank lines
            }
            if (line.StartsWith('#')) continue;   // skip the H1 heading
            if (line.StartsWith('>')) continue;    // skip blockquote metadata
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(line);
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    /// <summary>First paragraph under a "## Overview" heading in spec.md.</summary>
    internal static string? OverviewParagraph(string? specText)
    {
        if (specText is null) return null;
        var lines = EnumerateLines(specText).ToArray();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].TrimStart().StartsWith("## ")) continue;
            var heading = lines[i].TrimStart()[3..].Trim();
            if (!heading.Equals("Overview", StringComparison.OrdinalIgnoreCase)) continue;
            var sb = new StringBuilder();
            for (var j = i + 1; j < lines.Length; j++)
            {
                var line = lines[j].Trim();
                if (line.StartsWith("## ")) break;
                if (line.Length == 0)
                {
                    if (sb.Length > 0) break;
                    continue;
                }
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(line);
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }
        return null;
    }

    internal static string DeslugFolder(string folder)
    {
        var name = DatePrefix().Replace(folder, string.Empty);
        var words = name.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0
            ? folder
            : string.Join(' ', words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    private static DateOnly? FolderDate(string folder)
    {
        var m = DatePrefix().Match(folder);
        return m.Success && DateOnly.TryParseExact(
            m.Value.TrimEnd('-'), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;
    }

    private static DateOnly? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Take the leading YYYY-MM-DD if present (annotations may follow).
        var token = raw.Trim().Split(' ', '\t')[0];
        return DateOnly.TryParseExact(token, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;
    }

    private static IEnumerable<string> EnumerateLines(string text)
    {
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
            yield return line;
    }

    private static string CollapseWhitespace(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";
}
