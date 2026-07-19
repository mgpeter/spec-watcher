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
    /// Pick the best available input source: full keyboard+mouse on a real Windows console,
    /// otherwise a keyboard-only fallback (which is also used when input is redirected).
    /// </summary>
    static IInputSource Create()
    {
        if (OperatingSystem.IsWindows() && !Console.IsInputRedirected)
        {
            try
            {
                return new WindowsConsoleInput();
            }
            catch
            {
                // Any interop/console-mode failure → degrade to keyboard-only.
            }
        }

        return new ConsoleKeyInput();
    }
}
