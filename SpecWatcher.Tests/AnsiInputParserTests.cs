using System.Text;
using SpecWatcher.Input;
using Xunit;

namespace SpecWatcher.Tests;

/// <summary>
/// The byte-stream decoder behind mouse and key input on macOS and Linux terminals: plain keys,
/// cursor-key sequences in both CSI and SS3 form, SGR mouse reports, and sequences split across reads.
/// </summary>
public class AnsiInputParserTests
{
    private static List<InputEvent> Parse(string input, bool flush = false)
    {
        var parser = new AnsiInputParser();
        var events = new List<InputEvent>();
        parser.Feed(Encoding.UTF8.GetBytes(input), events);
        if (flush) parser.Flush(events);
        return events;
    }

    private static InputEvent Single(string input, bool flush = false) => Assert.Single(Parse(input, flush));

    // ---- plain keys --------------------------------------------------------

    [Theory]
    [InlineData("q", ConsoleKey.Q, 'q', false)]
    [InlineData("R", ConsoleKey.R, 'R', true)]
    [InlineData("f", ConsoleKey.F, 'f', false)]
    [InlineData("7", ConsoleKey.D7, '7', false)]
    [InlineData(" ", ConsoleKey.Spacebar, ' ', false)]
    [InlineData("\r", ConsoleKey.Enter, '\r', false)]
    [InlineData("\n", ConsoleKey.Enter, '\r', false)]
    [InlineData("\t", ConsoleKey.Tab, '\t', false)]
    [InlineData("\x7f", ConsoleKey.Backspace, '\b', false)]
    [InlineData("\b", ConsoleKey.Backspace, '\b', false)]
    public void Decodes_Plain_Keys(string input, ConsoleKey key, char ch, bool shift)
    {
        var evt = Single(input);

        Assert.Equal(InputKind.Key, evt.Kind);
        Assert.Equal(key, evt.Key);
        Assert.Equal(ch, evt.KeyChar);
        Assert.Equal(shift, evt.Shift);
    }

    [Fact]
    public void Punctuation_Carries_Its_Char()
    {
        var evt = Single("!");

        Assert.Equal(InputKind.Key, evt.Kind);
        Assert.Equal('!', evt.KeyChar);
    }

    [Fact]
    public void Decodes_Multibyte_Utf8_As_One_Char()
    {
        Assert.Equal('ł', Single("ł").KeyChar);
    }

    [Fact]
    public void A_Run_Of_Keys_Yields_One_Event_Each_In_Order()
    {
        var keys = Parse("qR\r").Select(e => e.Key);

        Assert.Equal(new[] { ConsoleKey.Q, ConsoleKey.R, ConsoleKey.Enter }, keys);
    }

    // ---- escape sequences --------------------------------------------------

    [Theory]
    [InlineData("\x1b[A", ConsoleKey.UpArrow)]
    [InlineData("\x1b[B", ConsoleKey.DownArrow)]
    [InlineData("\x1b[C", ConsoleKey.RightArrow)]
    [InlineData("\x1b[D", ConsoleKey.LeftArrow)]
    [InlineData("\x1bOA", ConsoleKey.UpArrow)]      // application cursor mode (keypad_xmit)
    [InlineData("\x1bOB", ConsoleKey.DownArrow)]
    [InlineData("\x1bOD", ConsoleKey.LeftArrow)]
    [InlineData("\x1b[H", ConsoleKey.Home)]
    [InlineData("\x1bOH", ConsoleKey.Home)]
    [InlineData("\x1b[1~", ConsoleKey.Home)]
    [InlineData("\x1b[F", ConsoleKey.End)]
    [InlineData("\x1bOF", ConsoleKey.End)]
    [InlineData("\x1b[4~", ConsoleKey.End)]
    [InlineData("\x1b[5~", ConsoleKey.PageUp)]
    [InlineData("\x1b[6~", ConsoleKey.PageDown)]
    [InlineData("\x1b[3~", ConsoleKey.Delete)]
    public void Decodes_Navigation_Sequences(string input, ConsoleKey key)
    {
        var evt = Single(input);

        Assert.Equal(InputKind.Key, evt.Kind);
        Assert.Equal(key, evt.Key);
    }

    [Fact]
    public void Shift_Tab_Is_Tab_With_Shift()
    {
        var evt = Single("\x1b[Z");

        Assert.Equal(ConsoleKey.Tab, evt.Key);
        Assert.True(evt.Shift);
    }

    [Fact]
    public void A_Modifier_Parameter_Carries_Shift()
    {
        var evt = Single("\x1b[1;2A");

        Assert.Equal(ConsoleKey.UpArrow, evt.Key);
        Assert.True(evt.Shift);
    }

    [Fact]
    public void Unknown_Sequences_Are_Dropped_Without_Leaking_Keys()
    {
        // Focus-in, a function key and an unsupported CSI must not surface as stray letters.
        Assert.Empty(Parse("\x1b[I\x1b[15~\x1b[?1;2c"));
    }

    // ---- the lone Escape key ----------------------------------------------

    [Fact]
    public void A_Trailing_Escape_Waits_For_More_Bytes()
    {
        Assert.Empty(Parse("\x1b"));
    }

    [Fact]
    public void A_Trailing_Escape_Becomes_The_Escape_Key_On_Flush()
    {
        var evt = Single("\x1b", flush: true);

        Assert.Equal(ConsoleKey.Escape, evt.Key);
    }

    [Fact]
    public void Two_Escapes_In_A_Row_Are_Two_Escape_Keys()
    {
        var keys = Parse("\x1b\x1b", flush: true).Select(e => e.Key);

        Assert.Equal(new[] { ConsoleKey.Escape, ConsoleKey.Escape }, keys);
    }

    [Fact]
    public void Alt_Letter_Is_The_Letter()
    {
        Assert.Equal(ConsoleKey.R, Single("\x1br").Key);
    }

    // ---- split reads -------------------------------------------------------

    [Fact]
    public void A_Sequence_Split_Across_Reads_Is_Reassembled()
    {
        var parser = new AnsiInputParser();
        var events = new List<InputEvent>();

        parser.Feed("\x1b["u8, events);
        Assert.Empty(events);
        parser.Feed("<0;10;5M"u8, events);

        var evt = Assert.Single(events);
        Assert.Equal(InputKind.MouseDown, evt.Kind);
    }

    [Fact]
    public void A_Utf8_Char_Split_Across_Reads_Is_Reassembled()
    {
        var parser = new AnsiInputParser();
        var events = new List<InputEvent>();
        var bytes = Encoding.UTF8.GetBytes("ł");

        parser.Feed(bytes.AsSpan(0, 1), events);
        Assert.Empty(events);
        parser.Feed(bytes.AsSpan(1), events);

        Assert.Equal('ł', Assert.Single(events).KeyChar);
    }

    [Fact]
    public void An_Incomplete_Csi_On_Flush_Becomes_Escape_Then_Its_Chars()
    {
        var keys = Parse("\x1b[", flush: true).Select(e => (e.Key, e.KeyChar));

        Assert.Equal(new[] { (ConsoleKey.Escape, '\x1b'), (default(ConsoleKey), '[') }, keys);
    }

    // ---- SGR mouse (1006) ---------------------------------------------------

    [Fact]
    public void Left_Press_Is_MouseDown_With_Zero_Based_Cell()
    {
        var evt = Single("\x1b[<0;10;5M");

        Assert.Equal(InputKind.MouseDown, evt.Kind);
        Assert.Equal(9, evt.Column);
        Assert.Equal(4, evt.Row);
    }

    [Fact]
    public void Left_Release_Is_MouseUp()
    {
        var evt = Single("\x1b[<0;10;5m");

        Assert.Equal(InputKind.MouseUp, evt.Kind);
        Assert.Equal(9, evt.Column);
        Assert.Equal(4, evt.Row);
    }

    [Fact]
    public void Motion_With_Left_Held_Is_A_Drag()
    {
        var evt = Single("\x1b[<32;12;5M");

        Assert.Equal(InputKind.MouseDrag, evt.Kind);
        Assert.Equal(11, evt.Column);
    }

    [Theory]
    [InlineData("\x1b[<64;3;7M", 1)]    // wheel up: positive notches, as on Windows
    [InlineData("\x1b[<65;3;7M", -1)]   // wheel down
    public void Wheel_Reports_Notches(string input, int notches)
    {
        var evt = Single(input);

        Assert.Equal(InputKind.MouseWheel, evt.Kind);
        Assert.Equal(notches, evt.WheelNotches);
        Assert.Equal(2, evt.Column);
        Assert.Equal(6, evt.Row);
    }

    [Fact]
    public void Modifier_Bits_Do_Not_Change_The_Button()
    {
        // Shift (4) and Ctrl (16) on a left press are still a left press.
        Assert.Equal(InputKind.MouseDown, Single("\x1b[<20;1;1M").Kind);
    }

    [Fact]
    public void Other_Buttons_And_Bare_Motion_Are_Ignored()
    {
        // Right press/release, middle press, motion with no button, horizontal wheel.
        Assert.Empty(Parse("\x1b[<2;1;1M\x1b[<2;1;1m\x1b[<1;1;1M\x1b[<35;4;4M\x1b[<66;1;1M"));
    }

    [Fact]
    public void A_Full_Click_Gesture_Arrives_In_Order()
    {
        var kinds = Parse("\x1b[<0;5;5M\x1b[<32;6;5M\x1b[<0;6;5m").Select(e => e.Kind);

        Assert.Equal(new[] { InputKind.MouseDown, InputKind.MouseDrag, InputKind.MouseUp }, kinds);
    }

    // ---- legacy X10 mouse (terminals without 1006) ----------------------------

    [Fact]
    public void Legacy_Mouse_Press_And_Release_Are_Decoded()
    {
        // ESC [ M then button+32, column+33, row+33 as raw bytes.
        var press = "\x1b[M" + (char)(0 + 32) + (char)(9 + 33) + (char)(4 + 33);
        var release = "\x1b[M" + (char)(3 + 32) + (char)(9 + 33) + (char)(4 + 33);

        var events = Parse(press + release);

        Assert.Equal(new[] { InputKind.MouseDown, InputKind.MouseUp }, events.Select(e => e.Kind));
        Assert.All(events, e => Assert.Equal((9, 4), (e.Column, e.Row)));
    }
}
