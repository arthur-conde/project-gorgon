using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mithril.Shared.Logging;

/// <summary>
/// Loads <c>Reference/log-patterns.json</c> — the single source of truth for every
/// log-line regex used by parsers in the Mithril modules and downstream tools
/// (e.g. <c>tools/MithrilLogMcp</c>).
///
/// The .NET parsers keep their <c>[GeneratedRegex]</c> attributes for
/// source-generated perf (Roslyn requires literal pattern strings, so it cannot
/// read from JSON at compile time). Parity is enforced by
/// <c>LogPatternCatalogParityTests</c>, which asserts every catalog entry
/// matches the C# attribute character-for-character. The TS port loads the same
/// JSON at runtime.
/// </summary>
public static class LogPatternCatalog
{
    private const string ResourceName = "Mithril.Shared.Reference.log-patterns.json";

    private static readonly Lazy<LogPatternCatalogDocument> s_document = new(LoadFromAssembly);

    public static LogPatternCatalogDocument Current => s_document.Value;

    public static LogPatternEntry GetEntry(string key) =>
        Current.Regexes.TryGetValue(key, out var entry)
            ? entry
            : throw new KeyNotFoundException($"log-patterns.json has no entry for key '{key}'.");

    public static string GetPattern(string key) => GetEntry(key).Pattern;

    private static LogPatternCatalogDocument LoadFromAssembly()
    {
        var asm = typeof(LogPatternCatalog).Assembly;
        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' not found in {asm.FullName}.");
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        return JsonSerializer.Deserialize<LogPatternCatalogDocument>(json, options)
            ?? throw new InvalidOperationException("log-patterns.json deserialised as null.");
    }
}

public sealed class LogPatternCatalogDocument
{
    [JsonPropertyName("version")] public int Version { get; init; }
    [JsonPropertyName("regexes")] public IReadOnlyDictionary<string, LogPatternEntry> Regexes { get; init; } = new Dictionary<string, LogPatternEntry>();
    [JsonPropertyName("shared")] public LogPatternSharedSection? Shared { get; init; }
}

public sealed class LogPatternEntry
{
    [JsonPropertyName("pattern")] public string Pattern { get; init; } = "";
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    [JsonPropertyName("module")] public string Module { get; init; } = "";
    [JsonPropertyName("csharp")] public LogPatternCSharpRef? CSharp { get; init; }
    [JsonPropertyName("eventType")] public string? EventType { get; init; }
    [JsonPropertyName("kind")] public string? Kind { get; init; }
    [JsonPropertyName("flags")] public IReadOnlyList<string>? Flags { get; init; }
    [JsonPropertyName("notes")] public string? Notes { get; init; }
    [JsonPropertyName("fields")] public IReadOnlyList<LogPatternField> Fields { get; init; } = Array.Empty<LogPatternField>();
}

public sealed class LogPatternCSharpRef
{
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("method")] public string Method { get; init; } = "";
    [JsonPropertyName("options")] public IReadOnlyList<string>? Options { get; init; }
}

public sealed class LogPatternField
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("group")] public JsonElement Group { get; init; }
    [JsonPropertyName("type")] public string Type { get; init; } = "string";
}

public sealed class LogPatternSharedSection
{
    [JsonPropertyName("sessionMarker")] public LogPatternSessionMarker? SessionMarker { get; init; }
    [JsonPropertyName("playerLogTimestampPrefix")] public LogPatternTimestampPrefix? PlayerLogTimestampPrefix { get; init; }
}

public sealed class LogPatternSessionMarker
{
    [JsonPropertyName("literal")] public string Literal { get; init; } = "";
    [JsonPropertyName("scanChunkBytes")] public long ScanChunkBytes { get; init; }
    [JsonPropertyName("notes")] public string? Notes { get; init; }
}

public sealed class LogPatternTimestampPrefix
{
    [JsonPropertyName("regex")] public string Regex { get; init; } = "";
    [JsonPropertyName("notes")] public string? Notes { get; init; }
}
