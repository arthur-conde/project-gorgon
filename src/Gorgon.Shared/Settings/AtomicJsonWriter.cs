using System.Text.Json.Serialization.Metadata;

namespace Gorgon.Shared.Settings;

/// <summary>Write a JSON file atomically: serialize to <c>path.tmp</c>, then rename
/// over the target. Backed by <see cref="AtomicFile"/>, which retries the whole
/// sequence on Windows AV/indexer interference.</summary>
internal static class AtomicJsonWriter
{
    public static void Write<T>(string filePath, T value, JsonTypeInfo<T> typeInfo) =>
        AtomicFile.WriteJsonAtomic(filePath, value, typeInfo);

    public static Task WriteAsync<T>(string filePath, T value, JsonTypeInfo<T> typeInfo, CancellationToken ct = default) =>
        AtomicFile.WriteJsonAtomicAsync(filePath, value, typeInfo, ct);
}
