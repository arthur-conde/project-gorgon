using System.Text.Json.Serialization;

namespace Gorgon.Shared.Reference;

/// <summary>Per-file metadata sidecar (e.g. <c>items.meta.json</c>).</summary>
public sealed class ReferenceFileMetadata
{
    public string CdnVersion { get; set; } = "";
    public DateTimeOffset? FetchedAtUtc { get; set; }
    public ReferenceFileSource Source { get; set; }
}

/// <summary>Raw items.json shape — the fields we actually project into <see cref="ItemEntry"/>.</summary>
public sealed class RawItem
{
    public string? Name { get; set; }
    public string? InternalName { get; set; }
    public int? MaxStackSize { get; set; }
    public int? IconId { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ReferenceFileMetadata))]
[JsonSerializable(typeof(Dictionary<string, RawItem>))]
public partial class ReferenceJsonContext : JsonSerializerContext { }
