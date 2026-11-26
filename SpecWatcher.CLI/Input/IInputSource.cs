namespace SpecWatcher.Input;

/// <summary>
/// A non-blocking source of <see cref="InputEvent"/>s. Implementations run their own reader
/// thread and enqueue events; the UI loop drains them lock-free via <see cref="TryRead"/>.
/// </summary>
public interface IInputSource : IDisposable
{
    /// <summary>True when an event was dequeued; false when the queue is currently empty.</summary>
    bool TryRead(out InputEvent evt);

    /// <summary>Whether this source delivers mouse events (used only for footer hints).</summary>
    bool SupportsMouse { get; }

    /// <summary>
    /// Pick the best available input source: full keyboard+mouse on a real Windows console or a
    /// macOS/Linux terminal, otherwise a keyboard-only fallback (also used when input is redirected).
    /// </summary>
    static IInputSource Create()
    {
        if (!Console.IsInputRedirected)
        {
            try
            {
                if (OperatingSystem.IsWindows()) return new WindowsConsoleInput();
                return new UnixTerminalInput();
            }
            catch
            {
                // Any interop/console-mode/stty failure → degrade to keyboard-only.
            }
        }

        return new ConsoleKeyInput();
    }
}
