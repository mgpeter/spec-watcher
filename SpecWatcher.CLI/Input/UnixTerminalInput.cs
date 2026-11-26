using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SpecWatcher.Input;

/// <summary>
/// Keyboard + mouse input on macOS and Linux terminals (Ghostty, iTerm2, Terminal.app, kitty, WezTerm,
/// tmux, ...). The tty is put into raw mode with <c>stty</c>, xterm mouse reporting is switched on
/// (1000 press/release, 1002 drag motion, 1006 SGR coordinates), and a background thread decodes stdin
/// with <see cref="AnsiInputParser"/>. Dispose turns mouse reporting off and restores the tty.
///
/// The read timeout (<c>min 0 time 1</c>) makes every read return within 100 ms, which keeps shutdown
/// responsive and tells a lone Esc press apart from the start of an escape sequence.
/// Reads go straight to fd 0 with libc <c>read</c>: <see cref="Console.OpenStandardInput()"/> on a
/// terminal hands back a line-editing reader that only returns after Enter. For the same reason, do
/// NOT also use <see cref="Console.ReadKey(bool)"/> while this is running: .NET would reconfigure the
/// tty and both readers would consume the same bytes.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class UnixTerminalInput : IInputSource
{
    // Press/release, motion while a button is held, SGR encoding. Disabled in reverse order.
    private const string MouseOn = "\x1b[?1000h\x1b[?1002h\x1b[?1006h";
    private const string MouseOff = "\x1b[?1006l\x1b[?1002l\x1b[?1000l";

    private readonly ConcurrentQueue<InputEvent> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private readonly string _savedTty;
    private int _restored;

    public UnixTerminalInput()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            throw new InvalidOperationException("Not attached to a terminal.");

        _savedTty = Stty("-g")?.Trim() ?? throw new InvalidOperationException("stty -g failed.");

        // No line buffering or echo, no XON/XOFF or literal-next, reads time out after 100 ms.
        // ISIG stays on so Ctrl+C still raises Console.CancelKeyPress.
        if (Stty("-icanon", "-echo", "-ixon", "-iexten", "min", "0", "time", "1") is null)
            throw new InvalidOperationException("stty could not switch to raw mode.");

        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        Console.Out.Write(MouseOn);
        Console.Out.Flush();

        _thread = new Thread(ReaderLoop) { IsBackground = true, Name = "unix-terminal-input" };
        _thread.Start();
    }

    public bool SupportsMouse => true;

    public bool TryRead(out InputEvent evt) => _queue.TryDequeue(out evt);

    private void ReaderLoop()
    {
        var parser = new AnsiInputParser();
        var buffer = new byte[256];
        var events = new List<InputEvent>();

        while (!_cts.IsCancellationRequested)
        {
            var read = (int)ReadStdin(buffer, buffer.Length);
            if (read < 0)
            {
                if (Marshal.GetLastPInvokeError() == EINTR) continue;   // a signal, e.g. SIGWINCH
                return;                                                 // tty gone
            }

            if (read > 0) parser.Feed(buffer.AsSpan(0, read), events);
            else parser.Flush(events);   // timed out with nothing new

            foreach (var evt in events) _queue.Enqueue(evt);
            events.Clear();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread.Join(500);
        Restore();
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        _cts.Dispose();
    }

    private void OnProcessExit(object? sender, EventArgs e) => Restore();

    /// <summary>Mouse off and the original tty settings back. Safe to call more than once.</summary>
    private void Restore()
    {
        if (Interlocked.Exchange(ref _restored, 1) != 0) return;
        try
        {
            Console.Out.Write(MouseOff);
            Console.Out.Flush();
        }
        catch { /* best effort */ }
        Stty(_savedTty);
    }

    private const int StdinFd = 0;
    private const int EINTR = 4;   // same value on macOS and Linux

    private static nint ReadStdin(byte[] buffer, int count) => read(StdinFd, buffer, count);

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int fd, [Out] byte[] buf, nint count);

    /// <summary>Run <c>stty</c> on our tty (it inherits stdin). Returns its stdout, or null on failure.</summary>
    private static string? Stty(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("stty")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(2000)) { proc.Kill(); return null; }
            return proc.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
