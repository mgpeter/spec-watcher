using Spectre.Console;

namespace SpecWatcher;

/// <summary>
/// The non-interactive (one-shot) path: given a <see cref="WatchSettings"/> and an already-completed
/// <see cref="ScanResult"/>, emit the board in the chosen format to a <see cref="TextWriter"/> and
/// return the process exit code. Kept free of the TUI / Spectre-Live so it is unit-testable with a
/// <see cref="StringWriter"/> and in-memory <see cref="ScanResult"/>s.
/// </summary>
public static class HeadlessRunner
{
    /// <summary>
    /// Emit <paramref name="result"/> in <paramref name="settings"/>'s format to
    /// <paramref name="writer"/>, then return the exit code (0 = OK, 1 = scan error, 2 = gate failure).
    /// The board is always emitted first, so a failing gate still prints the board.
    /// </summary>
    public static int Run(WatchSettings settings, ScanResult result, TextWriter writer)
    {
        // Only evaluate the gate on a trustworthy scan; a scan error skips it (see ExitCode).
        var gate = settings.HasGate && result.Error is null
            ? GateEvaluator.Evaluate(result, settings)
            : null;

        switch (settings.FormatKind)
        {
            case OutputFormat.Json:
                writer.Write(BoardJson.Serialize(settings, result, gate));
                break;
            case OutputFormat.Markdown:
                writer.Write(MarkdownBoard.Render(settings, result, gate));
                break;
            default:
                WriteTable(result, writer);
                break;
        }

        return ExitCode(result, gate);
    }

    /// <summary>
    /// Exit code precedence: a scan error is <c>1</c> even if a gate was requested (partial data is
    /// untrusted); otherwise a failing gate is <c>2</c>; otherwise <c>0</c>.
    /// </summary>
    internal static int ExitCode(ScanResult result, GateOutcome? gate)
    {
        if (result.Error is not null) return 1;
        if (gate is { Passed: false }) return 2;
        return 0;
    }

    /// <summary>Render the existing static table as plain text to the writer (matches piped output).</summary>
    private static void WriteTable(ScanResult result, TextWriter writer)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 120;   // widen so wrapped output stays readable when piped

        if (result.Error is { } err)
            console.MarkupLine($"[red]{Markup.Escape(err)}[/]");
        console.Write(WatchCommand.BuildStaticTable(result));
    }
}
