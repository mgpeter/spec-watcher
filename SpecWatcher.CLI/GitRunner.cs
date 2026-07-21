using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace SpecWatcher;

/// <summary>
/// The outcome of a single <c>git</c> invocation. A struct so callers can branch without allocating,
/// and so failure is data (never an exception): <see cref="Started"/> is false when the binary could
/// not be launched (git not on PATH), <see cref="TimedOut"/> is true when the process was killed for
/// exceeding its budget.
/// </summary>
internal readonly record struct GitResult(
    bool Started, int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    /// <summary>True only when git launched, ran to completion in time, and exited zero.</summary>
    public bool Ok => Started && !TimedOut && ExitCode == 0;

    /// <summary>A result for the "git binary could not be launched" case.</summary>
    public static GitResult NotStarted => new(false, -1, string.Empty, string.Empty, false);
}

/// <summary>
/// The thin process seam over the system <c>git</c> binary. Read-only by construction (callers only
/// ever pass <c>rev-parse</c>/<c>log</c> arguments), non-throwing, timeout-bounded, and deadlock-safe
/// (stdout/err are drained asynchronously). Every call is expected to run off the UI thread.
/// </summary>
internal static class GitRunner
{
    /// <summary>
    /// Run <c>git</c> with the given arguments. Never throws for the expected failure modes
    /// (git missing, non-zero exit, timeout, cancellation); those surface via <see cref="GitResult"/>.
    /// </summary>
    /// <param name="args">The full argument vector, e.g. <c>["-C", root, "rev-parse", "HEAD"]</c>.</param>
    /// <param name="timeout">Wall-clock budget; on expiry the process tree is killed.</param>
    /// <param name="ct">Cancels the wait (and kills the process) when the scan is torn down.</param>
    public static GitResult Run(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process proc;
        try
        {
            proc = new Process { StartInfo = psi };
            if (!proc.Start())
                return GitResult.NotStarted;
        }
        catch (Win32Exception)
        {
            return GitResult.NotStarted;   // git not found on PATH
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            return GitResult.NotStarted;
        }

        using (proc)
        {
            // Drain both pipes concurrently so a large payload on one can never deadlock the other.
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                proc.WaitForExitAsync(linked.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                var timedOut = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
                return new GitResult(true, -1, string.Empty, string.Empty, timedOut);
            }

            var stdout = SafeResult(outTask);
            var stderr = SafeResult(errTask);
            return new GitResult(true, proc.ExitCode, stdout, stderr, false);
        }
    }

    private static string SafeResult(Task<string> read)
    {
        try { return read.GetAwaiter().GetResult(); }
        catch { return string.Empty; }
    }
}
