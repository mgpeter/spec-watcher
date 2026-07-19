namespace SpecWatcher.Input;

public enum InputKind
{
    Key,
    MouseWheel,
    MouseClick,
    Resize,
}

/// <summary>
/// A unified input event drained by the render loop. Mouse coordinates are 0-based,
/// viewport-relative cell coordinates; <see cref="WheelNotches"/> is positive for wheel-up.
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
    public static InputEvent Click(int col, int row) => new(InputKind.MouseClick, Column: col, Row: row);
    public static InputEvent Resize(int cols, int rows) => new(InputKind.Resize, Column: cols, Row: rows);
}
