using System.Collections.Immutable;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpecWatcher;

/// <summary>
/// Builds the stable, machine-readable JSON board from a <see cref="ScanResult"/>. This schema is the
/// single source of truth integrations (e.g. a README badge / <c>spec-status.json</c>) consume:
/// field names are stable API, additive changes only. camelCase, two-space indented, no BOM, one
/// trailing newline.
/// </summary>
public static class BoardJson
{
    private const string ToolName = "spec-watcher";
    private const int Schema = 1;

    /// <summary>The app version stamped into the JSON (matches <c>Program.cs</c>'s application version).</summary>
    public const string ToolVersion = "1.0.0";

    /// <summary>Shared serializer options: camelCase, indented, relaxed escaping, kebab statuses.</summary>
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new SpecStatusJsonConverter() },
    };

    /// <summary>
    /// Serialize the board to a JSON string with a trailing newline. Pass a non-null
    /// <paramref name="gate"/> only when a gate flag was given; otherwise the <c>gate</c> block is null.
    /// </summary>
    public static string Serialize(WatchSettings settings, ScanResult result, GateOutcome? gate) =>
        JsonSerializer.Serialize(Build(settings, result, gate), Options) + "\n";

    private static BoardDto Build(WatchSettings settings, ScanResult result, GateOutcome? gate)
    {
        var rows = result.Rows.IsDefaultOrEmpty ? ImmutableArray<SpecRow>.Empty : result.Rows;

        var summary = new SummaryDto(
            rows.Count(r => r.Status == SpecStatus.Planning),
            rows.Count(r => r.Status == SpecStatus.InProgress),
            rows.Count(r => r.Status == SpecStatus.Complete),
            rows.Count(r => r.Status == SpecStatus.Unknown));

        GateDto? gateDto = gate is null
            ? null
            : new GateDto(
                settings.FailOnStatuses.Select(StatusKeyword.ToKebab).ToArray(),
                settings.MinProgress,
                gate.Passed,
                gate.Violations.Select(v => new ViolationDto(v.Folder, v.Reason, v.Detail)).ToArray());

        var specs = rows.Select(r => new SpecDto(
            r.Name,
            r.Folder,
            r.Status,
            r.StatusRaw,
            r.Description,
            r.Created?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            r.Done,
            r.Total,
            r.HasTasks,
            r.Progress,
            Normalize(r.FullPath))).ToArray();

        return new BoardDto(
            ToolName,
            Schema,
            ToolVersion,
            result.CompletedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            Normalize(settings.ResolvedSpecsDir),
            specs.Length,
            summary,
            gateDto,
            specs,
            result.Error);
    }

    /// <summary>JSON paths use forward slashes so the schema is stable across OSes.</summary>
    private static string Normalize(string path) => path.Replace('\\', '/');

    // ---- serializable DTOs (declaration order == JSON field order) -------

    private sealed record BoardDto(
        string Tool,
        int SchemaVersion,
        string Version,
        string GeneratedAt,
        string SpecsDir,
        int SpecCount,
        SummaryDto Summary,
        GateDto? Gate,
        IReadOnlyList<SpecDto> Specs,
        string? Error);

    private sealed record SummaryDto(int Planning, int InProgress, int Complete, int Unknown);

    private sealed record GateDto(
        IReadOnlyList<string> FailOn,
        int? MinProgress,
        bool Passed,
        IReadOnlyList<ViolationDto> Violations);

    private sealed record ViolationDto(string Folder, string Reason, string Detail);

    private sealed record SpecDto(
        string Name,
        string Folder,
        SpecStatus Status,
        string StatusRaw,
        string Description,
        string? Created,
        int Done,
        int Total,
        bool HasTasks,
        double? Progress,
        string Path);
}

/// <summary>Serializes <see cref="SpecStatus"/> as its stable kebab-case keyword.</summary>
internal sealed class SpecStatusJsonConverter : JsonConverter<SpecStatus>
{
    public override SpecStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        WatchSettings.TryMapStatus(reader.GetString() ?? string.Empty, out var status) ? status : SpecStatus.Unknown;

    public override void Write(Utf8JsonWriter writer, SpecStatus value, JsonSerializerOptions options) =>
        writer.WriteStringValue(StatusKeyword.ToKebab(value));
}
