using System.Collections.Immutable;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace SpecWatcher;

/// <summary>
/// A TUI colour palette. Every colour the dashboard draws comes from one of these roles, so a theme
/// is just a set of hex values. <see cref="Ground"/> and <see cref="Text"/> are painted behind and
/// over every cell by <see cref="ThemedSurface"/>, so a theme looks the same whatever the
/// terminal's own colours are.
/// </summary>
/// <param name="Key">Lower-case name used by <c>--theme</c> and the config file.</param>
/// <param name="Name">Display name.</param>
/// <param name="Ground">Screen background.</param>
/// <param name="Surface">Raised background: the status picker.</param>
/// <param name="Line">Panel borders and the empty part of progress bars.</param>
/// <param name="Text">Default text.</param>
/// <param name="Muted">Secondary text: labels, timestamps, key hints.</param>
/// <param name="Faint">Tertiary text: the folder column.</param>
/// <param name="Accent">Brand colour: the title, caret, headings and focus band.</param>
/// <param name="Complete">Complete status and full progress bars.</param>
/// <param name="Progress">In-progress status and partial progress bars.</param>
/// <param name="Planning">Planning status.</param>
/// <param name="Warn">Drift flags and failed edits.</param>
/// <param name="Error">Scan errors.</param>
/// <param name="Highlight">Git: the branch, the spec for the current branch, commit SHAs.</param>
/// <param name="Selection">Background of the selected list row.</param>
public sealed record Theme(
    string Key,
    string Name,
    string Ground,
    string Surface,
    string Line,
    string Text,
    string Muted,
    string Faint,
    string Accent,
    string Complete,
    string Progress,
    string Planning,
    string Warn,
    string Error,
    string Highlight,
    string Selection)
{
    /// <summary>The usualexpat.com dark theme. The default.</summary>
    public static readonly Theme Harbour = new(
        "harbour", "Harbour",
        Ground: "#0B1118", Surface: "#1A2430", Line: "#2A3542",
        Text: "#EEF2F7", Muted: "#9AA7B6", Faint: "#6B7A8C",
        Accent: "#3A9BFF", Complete: "#5BD38B", Progress: "#F5B544", Planning: "#9AA7B6",
        Warn: "#FF9A4D", Error: "#FF6B6B", Highlight: "#FFD166", Selection: "#16314F");

    /// <summary>The usualexpat.com light theme.</summary>
    public static readonly Theme Paper = new(
        "paper", "Paper",
        Ground: "#F7FAFC", Surface: "#E3EAF1", Line: "#C9D3DE",
        Text: "#001F3F", Muted: "#5A6B7E", Faint: "#8494A6",
        Accent: "#1F6FC5", Complete: "#0F7B4A", Progress: "#9A5B00", Planning: "#5A6B7E",
        Warn: "#B54708", Error: "#C0262D", Highlight: "#8A6100", Selection: "#DCEBFF");

    /// <summary>Green-screen terminal, amber accents.</summary>
    public static readonly Theme Phosphor = new(
        "phosphor", "Phosphor",
        Ground: "#0A0F0B", Surface: "#142219", Line: "#1F3326",
        Text: "#D7F5DF", Muted: "#86A892", Faint: "#5E7A67",
        Accent: "#4ADE80", Complete: "#4ADE80", Progress: "#F5B544", Planning: "#86A892",
        Warn: "#FF9A4D", Error: "#FF6B6B", Highlight: "#F5B544", Selection: "#173A22");

    /// <summary>Brand navy with an orange accent.</summary>
    public static readonly Theme Ember = new(
        "ember", "Ember",
        Ground: "#001F3F", Surface: "#0A3258", Line: "#1D4170",
        Text: "#F7FAFC", Muted: "#A9BCD1", Faint: "#7590AE",
        Accent: "#FF9A4D", Complete: "#6EE7A8", Progress: "#FF9A4D", Planning: "#A9BCD1",
        Warn: "#FFC857", Error: "#FF6B6B", Highlight: "#FFC38A", Selection: "#123E6B");

    /// <summary>Every theme, in the order <c>T</c> cycles through them.</summary>
    public static readonly ImmutableArray<Theme> All = [Harbour, Paper, Phosphor, Ember];

    public static Theme Default => Harbour;

    /// <summary>
    /// The theme the dashboard draws with. Ambient rather than passed around because the row,
    /// badge and detail builders are static and shared with the headless table, which renders
    /// without colour anyway.
    /// </summary>
    public static Theme Current { get; set; } = Default;

    /// <summary>Comma list of theme keys, for help and error text.</summary>
    public static string KeyList => string.Join(", ", All.Select(t => t.Key));

    /// <summary>Find a theme by key or display name, ignoring case.</summary>
    public static Theme? Find(string? name)
    {
        var n = (name ?? string.Empty).Trim();
        return All.FirstOrDefault(t =>
            string.Equals(t.Key, n, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t.Name, n, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The theme after this one, wrapping round.</summary>
    public Theme Next()
    {
        var i = All.IndexOf(this);
        return All[(i + 1) % All.Length];
    }

    // Markup fragments for the styles used in more than one place.

    /// <summary>The focused line in the detail view, and the highlighted picker choice.</summary>
    public string FocusStyle => $"{Ground} on {Accent}";

    /// <summary>Drag-selected text. Differs from <see cref="FocusStyle"/> so both can show at once.</summary>
    public string DragStyle => $"{Ground} on {Muted}";

    /// <summary>The picker's other rows.</summary>
    public string PickerStyle => $"{Text} on {Surface}";

    public Color LineColor => Parse(Line);
    public Color AccentColor => Parse(Accent);

    internal static Color Parse(string hex) => Color.FromHex(hex);
}

/// <summary>
/// Paints the current theme's ground and text colour into every cell the wrapped renderable leaves
/// at the terminal default. Padding, blank lines and plain text all get the theme's colours, so no
/// cell shows through in the terminal's own background.
/// </summary>
internal sealed class ThemedSurface(IRenderable inner) : IRenderable
{
    public Measurement Measure(RenderOptions options, int maxWidth) => inner.Measure(options, maxWidth);

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var theme = Theme.Current;
        var ground = Theme.Parse(theme.Ground);
        var text = Theme.Parse(theme.Text);
        return inner.Render(options, maxWidth).Select(s => Paint(s, ground, text));
    }

    internal static Segment Paint(Segment segment, Color ground, Color text)
    {
        if (segment.IsControlCode || segment.IsLineBreak) return segment;
        var style = segment.Style;
        var fg = style.Foreground == Color.Default ? text : style.Foreground;
        var bg = style.Background == Color.Default ? ground : style.Background;
        if (fg == style.Foreground && bg == style.Background) return segment;
        return new Segment(segment.Text, new Style(fg, bg, style.Decoration, style.Link));
    }
}
