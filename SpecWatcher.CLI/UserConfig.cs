using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpecWatcher;

/// <summary>
/// Per-user preferences that survive between runs. Only the theme today. Stored as JSON in
/// <c>~/.config/spec-watcher/config.json</c> on macOS and Linux (or under <c>$XDG_CONFIG_HOME</c>)
/// and <c>%APPDATA%\spec-watcher\config.json</c> on Windows. A missing or unreadable file means
/// defaults, and a failed save is ignored: preferences are never worth an error.
/// </summary>
internal static class UserConfig
{
    private const string ThemeKey = "theme";

    /// <summary>The config file path for this user.</summary>
    public static string DefaultPath => Path.Combine(ConfigRoot(), "spec-watcher", "config.json");

    // .NET maps ApplicationData to ~/Library/Application Support on macOS; command-line tools
    // there conventionally use ~/.config, as on Linux.
    private static string ConfigRoot()
    {
        if (OperatingSystem.IsWindows())
            return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return Path.IsPathRooted(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
    }

    /// <summary>The saved theme key, or null when none is saved.</summary>
    public static string? LoadTheme(string path) =>
        Read(path)?[ThemeKey] is JsonValue v && v.TryGetValue(out string? key) ? key : null;

    /// <summary>Save the theme key, keeping any other keys already in the file.</summary>
    public static bool SaveTheme(string path, string key)
    {
        try
        {
            var root = Read(path) ?? new JsonObject();
            root[ThemeKey] = key;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static JsonObject? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
