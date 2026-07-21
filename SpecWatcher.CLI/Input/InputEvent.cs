namespace SpecWatcher.Input;

public enum InputKind
{
    Key,
    MouseWheel,
    MouseDown,
    MouseDrag,
    MouseUp,
    Resize,
}

/// <summary>
/// A unified input event drained by the render loop. Mouse coordinates are 0-based,
/// viewport-relative cell coordinates; <see cref="WheelNotches"/> is positive for wheel-up.
///
/// A left-button gesture arrives as <see cref="InputKind.MouseDown"/>, zero or more
/// <see cref="InputKind.MouseDrag"/> (only while the button is held), then
/// <see cref="InputKind.MouseUp"/>. The consumer decides click-vs-drag: an up on the same cell as
/// the down (no intervening drag) is a click; any move to another cell makes it a text selection.
/// </summary>
public readonly record struct InputEvent(
    InputKind Kind,
    ConsoleKey Key = default,
    char KeyChar = '\0',
    int Column = 0,
    int Row = 0,
    int WheelNotches = 0)
{
    public static InputEvent FromKey(ConsoleKey key, char ch = '\0') => new(InputKind.Key, key, ch);
    public static InputEvent Wheel(int notches, int col, int row) => new(InputKind.MouseWheel, Column: col, Row: row, WheelNotches: notches);
    public static InputEvent Down(int col, int row) => new(InputKind.MouseDown, Column: col, Row: row);
    public static InputEvent Drag(int col, int row) => new(InputKind.MouseDrag, Column: col, Row: row);
    public static InputEvent Up(int col, int row) => new(InputKind.MouseUp, Column: col, Row: row);
    public static InputEvent Resize(int cols, int rows) => new(InputKind.Resize, Column: cols, Row: rows);
}
