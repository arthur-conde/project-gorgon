using System.Text.Json.Serialization;

namespace Mithril.Shared.Reference;

/// <summary>Per-file metadata sidecar (e.g. <c>items.meta.json</c>).</summary>
public sealed class ReferenceFileMetadata
{
    public string CdnVersion { get; set; } = "";
    public DateTimeOffset? FetchedAtUtc { get; set; }
    public ReferenceFileSource Source { get; set; }
}

/// <summary>
/// System.Text.Json source-generated context for <see cref="ReferenceFileMetadata"/>.
/// Reference data deserialization itself is handled by the
/// <c>Mithril.Reference</c> library (Newtonsoft-based) — only the small per-file
/// metadata sidecars stay on System.Text.Json source-gen.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ReferenceFileMetadata))]
public partial class ReferenceJsonContext : JsonSerializerContext { }
