using System.Text;

namespace SpecWatcher.Input;

/// <summary>
/// Decodes the raw byte stream a VT/xterm-style terminal sends in raw mode into
/// <see cref="InputEvent"/>s: plain keys (UTF-8), cursor and editing keys in CSI and SS3 form, SGR
/// (mode 1006) mouse reports and, for terminals without 1006, legacy X10 mouse reports.
///
/// Bytes that end mid-sequence are held until the next <see cref="Feed"/>. A read that times out with
/// nothing new should call <see cref="Flush"/>, which turns a held lone ESC into the Escape key.
/// Not thread-safe: one reader thread owns an instance.
/// </summary>
public sealed class AnsiInputParser
{
    private const byte Esc = 0x1b;
    private const int MaxPending = 64;   // anything longer is not a sequence we understand

    private readonly List<byte> _pending = new();
    private bool _legacyLeftDown;        // X10 reports a release without saying which button

    public void Feed(ReadOnlySpan<byte> bytes, List<InputEvent> output)
    {
        foreach (var b in bytes) _pending.Add(b);
        Drain(output, final: false);
    }

    /// <summary>Resolve whatever is held, treating an unfinished escape sequence as typed keys.</summary>
    public void Flush(List<InputEvent> output) => Drain(output, final: true);

    private void Drain(List<InputEvent> output, bool final)
    {
        var span = _pending.ToArray().AsSpan();
        var pos = 0;
        while (pos < span.Length)
        {
            var used = span[pos] == Esc
                ? ParseEscape(span[pos..], output, final)
                : ParseChar(span[pos..], output, final);
            if (used == 0) break;   // incomplete: keep the tail for the next read
            pos += used;
        }

        _pending.RemoveRange(0, pos);
        if (_pending.Count > MaxPending) _pending.Clear();
    }

    // ---- plain characters ----------------------------------------------------

    private static int ParseChar(ReadOnlySpan<byte> s, List<InputEvent> output, bool final)
    {
        var b = s[0];
        if (b < 0x80)
        {
            output.Add(KeyForAscii((char)b));
            return 1;
        }

        var status = Rune.DecodeFromUtf8(s, out var rune, out var consumed);
        if (status == System.Buffers.OperationStatus.NeedMoreData && !final) return 0;
        if (status == System.Buffers.OperationStatus.Done && rune.IsBmp)
            output.Add(InputEvent.FromKey(default, (char)rune.Value));
        return Math.Max(1, consumed);   // invalid or astral: skip it
    }

    private static InputEvent KeyForAscii(char c) => c switch
    {
        '\r' or '\n' => InputEvent.FromKey(ConsoleKey.Enter, '\r'),
        '\t' => InputEvent.FromKey(ConsoleKey.Tab, '\t'),
        '\x7f' or '\b' => InputEvent.FromKey(ConsoleKey.Backspace, '\b'),
        ' ' => InputEvent.FromKey(ConsoleKey.Spacebar, ' '),
        >= 'a' and <= 'z' => InputEvent.FromKey(ConsoleKey.A + (c - 'a'), c),
        >= 'A' and <= 'Z' => InputEvent.FromKey(ConsoleKey.A + (c - 'A'), c, shift: true),
        >= '0' and <= '9' => InputEvent.FromKey(ConsoleKey.D0 + (c - '0'), c),
        // Ctrl+letter, as Console.ReadKey reports it: the letter's key with the control char.
        >= '\x01' and <= '\x1a' => InputEvent.FromKey(ConsoleKey.A + (c - '\x01'), c),
        _ => InputEvent.FromKey(default, c),
    };

    // ---- escape sequences ----------------------------------------------------

    /// <summary>Parse a sequence starting at ESC. Returns bytes consumed, or 0 to wait for more.</summary>
    private int ParseEscape(ReadOnlySpan<byte> s, List<InputEvent> output, bool final)
    {
        if (s.Length == 1)
        {
            if (!final) return 0;
            output.Add(EscapeKey);
            return 1;
        }

        switch (s[1])
        {
            case (byte)'[':
                return ParseCsi(s, output, final);

            case (byte)'O':
                if (s.Length < 3)
                {
                    if (!final) return 0;
                    output.Add(EscapeKey);
                    return 1;
                }
                if (Ss3Key(s[2]) is { } key) output.Add(InputEvent.FromKey(key));
                return 3;

            case Esc:
                output.Add(EscapeKey);
                return 1;

            default:
                // Alt+key arrives as ESC + key. Drop the Alt rather than invent an Escape press.
                return 1;
        }
    }

    private static readonly InputEvent EscapeKey = InputEvent.FromKey(ConsoleKey.Escape, '\x1b');

    private int ParseCsi(ReadOnlySpan<byte> s, List<InputEvent> output, bool final)
    {
        // ESC [ params(0x30-0x3F)* intermediates(0x20-0x2F)* final(0x40-0x7E)
        var i = 2;
        while (i < s.Length && s[i] is >= 0x30 and <= 0x3F) i++;
        while (i < s.Length && s[i] is >= 0x20 and <= 0x2F) i++;
        if (i >= s.Length)
        {
            if (!final && s.Length < MaxPending) return 0;
            output.Add(EscapeKey);   // a real Esc press followed by ordinary typing
            return 1;
        }

        var finalByte = s[i];
        if (finalByte is < 0x40 or > 0x7E) return i;   // malformed: skip what we scanned
        var param = Encoding.ASCII.GetString(s[2..i]);

        // Legacy X10 mouse: "ESC [ M" with no parameters, then three raw bytes.
        if (finalByte == 'M' && param.Length == 0)
        {
            if (s.Length < i + 4) return final ? i + 1 : 0;
            LegacyMouse(s[i + 1] - 32, s[i + 2] - 33, s[i + 3] - 33, output);
            return i + 4;
        }

        if (param.StartsWith('<') && finalByte is (byte)'M' or (byte)'m')
            SgrMouse(param[1..], release: finalByte == 'm', output);
        else if (CsiKey(param, (char)finalByte) is { } evt)
            output.Add(evt);

        return i + 1;
    }

    private static ConsoleKey? Ss3Key(byte b) => b switch
    {
        (byte)'A' => ConsoleKey.UpArrow,
        (byte)'B' => ConsoleKey.DownArrow,
        (byte)'C' => ConsoleKey.RightArrow,
        (byte)'D' => ConsoleKey.LeftArrow,
        (byte)'H' => ConsoleKey.Home,
        (byte)'F' => ConsoleKey.End,
        (byte)'P' => ConsoleKey.F1,
        (byte)'Q' => ConsoleKey.F2,
        (byte)'R' => ConsoleKey.F3,
        (byte)'S' => ConsoleKey.F4,
        _ => null,
    };

    private static InputEvent? CsiKey(string param, char final)
    {
        var parts = param.Split(';');
        // xterm modifier parameter: 1 + (shift 1 | alt 2 | ctrl 4); only Shift matters here.
        var shift = parts.Length > 1 && int.TryParse(parts[1], out var mod) && ((mod - 1) & 1) != 0;

        ConsoleKey? key = final switch
        {
            'A' => ConsoleKey.UpArrow,
            'B' => ConsoleKey.DownArrow,
            'C' => ConsoleKey.RightArrow,
            'D' => ConsoleKey.LeftArrow,
            'H' => ConsoleKey.Home,
            'F' => ConsoleKey.End,
            'Z' => ConsoleKey.Tab,
            '~' => parts[0] switch
            {
                "1" or "7" => ConsoleKey.Home,
                "2" => ConsoleKey.Insert,
                "3" => ConsoleKey.Delete,
                "4" or "8" => ConsoleKey.End,
                "5" => ConsoleKey.PageUp,
                "6" => ConsoleKey.PageDown,
                _ => null,
            },
            _ => null,
        };

        if (key is null) return null;
        if (final == 'Z') shift = true;   // back-tab
        return InputEvent.FromKey(key.Value, key == ConsoleKey.Tab ? '\t' : '\0', shift);
    }

    // ---- mouse -------------------------------------------------------------------

    private const int MotionBit = 32;
    private const int WheelBit = 64;
    private const int ModifierBits = 4 | 8 | 16;   // shift, meta, ctrl

    /// <summary>SGR report "b;x;y" with 1-based cells; 'm' marks a release.</summary>
    private static void SgrMouse(string param, bool release, List<InputEvent> output)
    {
        var parts = param.Split(';');
        if (parts.Length != 3
            || !int.TryParse(parts[0], out var cb)
            || !int.TryParse(parts[1], out var x)
            || !int.TryParse(parts[2], out var y))
            return;

        var col = x - 1;
        var row = y - 1;
        cb &= ~ModifierBits;

        if ((cb & WheelBit) != 0)
        {
            if (release) return;
            switch (cb & 3)
            {
                case 0: output.Add(InputEvent.Wheel(+1, col, row)); break;   // up
                case 1: output.Add(InputEvent.Wheel(-1, col, row)); break;   // down
            }
            return;
        }

        if ((cb & 3) != 0) return;   // middle, right, or motion with no button
        if ((cb & MotionBit) != 0) output.Add(InputEvent.Drag(col, row));
        else output.Add(release ? InputEvent.Up(col, row) : InputEvent.Down(col, row));
    }

    /// <summary>X10 report with 0-based cells already applied; button 3 is "a button was released".</summary>
    private void LegacyMouse(int cb, int col, int row, List<InputEvent> output)
    {
        cb &= ~ModifierBits;

        if ((cb & WheelBit) != 0)
        {
            switch (cb & 3)
            {
                case 0: output.Add(InputEvent.Wheel(+1, col, row)); break;
                case 1: output.Add(InputEvent.Wheel(-1, col, row)); break;
            }
            return;
        }

        var button = cb & 3;
        if ((cb & MotionBit) != 0)
        {
            if (button == 0 && _legacyLeftDown) output.Add(InputEvent.Drag(col, row));
        }
        else if (button == 0)
        {
            _legacyLeftDown = true;
            output.Add(InputEvent.Down(col, row));
        }
        else if (button == 3 && _legacyLeftDown)
        {
            _legacyLeftDown = false;
            output.Add(InputEvent.Up(col, row));
        }
    }
}
