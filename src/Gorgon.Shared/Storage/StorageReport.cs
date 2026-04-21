using System.Text.Json.Serialization;

namespace Gorgon.Shared.Storage;

/// <summary>
/// Top-level JSON model for a character's storage/inventory export.
/// Files follow the naming pattern: {Character}_{Server}_items_{timestamp}.json
/// </summary>
public sealed record StorageReport(
    string Character,
    string ServerName,
    string Timestamp,
    string Report,
    int ReportVersion,
    IReadOnlyList<StorageItem> Items);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(StorageReport))]
public partial class StorageReportJsonContext : JsonSerializerContext;
