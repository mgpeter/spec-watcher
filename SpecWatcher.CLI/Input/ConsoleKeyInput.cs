using System.Collections.Concurrent;

namespace SpecWatcher.Input;

/// <summary>
/// Keyboard-only input source using blocking <see cref="Console.ReadKey(bool)"/> on a background
/// thread. Used on non-Windows platforms or when the native mouse path is unavailable.
/// </summary>
public sealed class ConsoleKeyInput : IInputSource
{
    private readonly ConcurrentQueue<InputEvent> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;

    public ConsoleKeyInput()
    {
        _thread = new Thread(ReaderLoop) { IsBackground = true, Name = "console-key-input" };
        _thread.Start();
    }

    public bool SupportsMouse => false;

    public bool TryRead(out InputEvent evt) => _queue.TryDequeue(out evt);

    private void ReaderLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                // Avoid blocking forever so cancellation is responsive.
                if (!Console.KeyAvailable)
                {
                    Thread.Sleep(20);
                    continue;
                }

                var key = Console.ReadKey(intercept: true);
                _queue.Enqueue(InputEvent.FromKey(key.Key, key.KeyChar));
            }
            catch (InvalidOperationException)
            {
                // Input was redirected/closed; nothing more to read.
                return;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread.Join(200);
        _cts.Dispose();
    }
}
