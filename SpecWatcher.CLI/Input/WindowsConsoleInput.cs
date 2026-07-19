using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SpecWatcher.Input;

/// <summary>
/// Keyboard + mouse input on Windows via the Win32 console input buffer (<c>ReadConsoleInput</c>).
/// A dedicated background thread translates raw records into <see cref="InputEvent"/>s.
///
/// Console input mode is reconfigured to deliver mouse events (clearing QuickEdit so the terminal
/// stops hijacking the mouse for text selection) and the original mode is restored on dispose.
/// Do NOT also use <see cref="Console.ReadKey(bool)"/> while this is running — both consume the
/// same input buffer and would steal each other's records.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsConsoleInput : IInputSource
{
    private readonly ConcurrentQueue<InputEvent> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private readonly IntPtr _hIn;
    private readonly uint _originalMode;

    public WindowsConsoleInput()
    {
        _hIn = GetStdHandle(STD_INPUT_HANDLE);
        if (_hIn == IntPtr.Zero || _hIn == INVALID_HANDLE_VALUE)
            throw new InvalidOperationException("No console input handle.");

        if (!GetConsoleMode(_hIn, out _originalMode))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        // Enable mouse + window events; disable QuickEdit (needs EXTENDED_FLAGS to take effect);
        // keep processed input so Ctrl+C still raises CancelKeyPress. Leave VT-input OFF so we get
        // structured records rather than escape sequences.
        var mode = _originalMode;
        mode &= ~ENABLE_QUICK_EDIT_MODE;
        mode |= ENABLE_EXTENDED_FLAGS | ENABLE_MOUSE_INPUT | ENABLE_WINDOW_INPUT | ENABLE_PROCESSED_INPUT;
        mode &= ~ENABLE_VIRTUAL_TERMINAL_INPUT;
        if (!SetConsoleMode(_hIn, mode))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        _thread = new Thread(ReaderLoop) { IsBackground = true, Name = "win-console-input" };
        _thread.Start();
    }

    public bool SupportsMouse => true;

    public bool TryRead(out InputEvent evt) => _queue.TryDequeue(out evt);

    private void ReaderLoop()
    {
        var buffer = new INPUT_RECORD[32];
        while (!_cts.IsCancellationRequested)
        {
            // Cancellable wait so shutdown doesn't hang on a blocking read.
            if (WaitForSingleObject(_hIn, 50) != WAIT_OBJECT_0)
                continue;
            if (!GetNumberOfConsoleInputEvents(_hIn, out var pending) || pending == 0)
                continue;
            if (!ReadConsoleInput(_hIn, buffer, (uint)buffer.Length, out var read))
                continue;

            for (var i = 0; i < read; i++)
                Translate(buffer[i]);
        }
    }

    private void Translate(in INPUT_RECORD rec)
    {
        switch (rec.EventType)
        {
            case KEY_EVENT when rec.KeyEvent.bKeyDown != 0:
            {
                var evt = InputEvent.FromKey((ConsoleKey)rec.KeyEvent.wVirtualKeyCode, rec.KeyEvent.UnicodeChar);
                var repeat = Math.Max((ushort)1, rec.KeyEvent.wRepeatCount);
                for (var i = 0; i < repeat; i++)
                    _queue.Enqueue(evt);
                break;
            }

            case MOUSE_EVENT:
            {
                var m = rec.MouseEvent;
                if ((m.dwEventFlags & MOUSE_WHEELED) != 0)
                {
                    var wheel = (short)(m.dwButtonState >> 16);   // signed high word; + = up
                    var notches = wheel / 120;
                    if (notches != 0)
                        _queue.Enqueue(InputEvent.Wheel(notches, m.dwMousePosition.X, m.dwMousePosition.Y));
                }
                else if ((m.dwEventFlags == 0 || m.dwEventFlags == DOUBLE_CLICK)
                         && (m.dwButtonState & FROM_LEFT_1ST_BUTTON_PRESSED) != 0)
                {
                    _queue.Enqueue(InputEvent.Click(m.dwMousePosition.X, m.dwMousePosition.Y));
                }
                break;
            }

            case WINDOW_BUFFER_SIZE_EVENT:
                _queue.Enqueue(InputEvent.Resize(rec.WindowBufferSizeEvent.dwSize.X, rec.WindowBufferSizeEvent.dwSize.Y));
                break;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread.Join(300);
        try { SetConsoleMode(_hIn, _originalMode); } catch { /* best effort restore */ }
        _cts.Dispose();
    }

    // ---- Win32 interop ---------------------------------------------------

    private const int STD_INPUT_HANDLE = -10;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private const uint WAIT_OBJECT_0 = 0;

    private const uint ENABLE_PROCESSED_INPUT = 0x0001;
    private const uint ENABLE_WINDOW_INPUT = 0x0008;
    private const uint ENABLE_MOUSE_INPUT = 0x0010;
    private const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
    private const uint ENABLE_EXTENDED_FLAGS = 0x0080;
    private const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;

    private const ushort KEY_EVENT = 0x0001;
    private const ushort MOUSE_EVENT = 0x0002;
    private const ushort WINDOW_BUFFER_SIZE_EVENT = 0x0004;

    private const uint FROM_LEFT_1ST_BUTTON_PRESSED = 0x0001;
    private const uint DOUBLE_CLICK = 0x0002;
    private const uint MOUSE_WHEELED = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEY_EVENT_RECORD
    {
        public int bKeyDown;
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public char UnicodeChar;
        public uint dwControlKeyState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSE_EVENT_RECORD
    {
        public COORD dwMousePosition;
        public uint dwButtonState;
        public uint dwControlKeyState;
        public uint dwEventFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOW_BUFFER_SIZE_RECORD { public COORD dwSize; }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_RECORD
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
        [FieldOffset(4)] public MOUSE_EVENT_RECORD MouseEvent;
        [FieldOffset(4)] public WINDOW_BUFFER_SIZE_RECORD WindowBufferSizeEvent;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ReadConsoleInput(IntPtr hConsoleInput, [Out] INPUT_RECORD[] lpBuffer, uint nLength, out uint lpNumberOfEventsRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNumberOfConsoleInputEvents(IntPtr hConsoleInput, out uint lpcNumberOfEvents);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
}
