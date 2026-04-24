using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Gorgon.Shared.Settings;

/// <summary>Write a JSON file atomically: serialize to <c>path.tmp</c>, then <c>File.Move</c> over the target.</summary>
internal static class AtomicJsonWriter
{
    public static void Write<T>(string filePath, T value, JsonTypeInfo<T> typeInfo)
    {
        EnsureDirectory(filePath);
        var tmp = filePath + ".tmp";
        using (var stream = File.Create(tmp))
        {
            JsonSerializer.Serialize(stream, value, typeInfo);
        }
        AtomicFile.MoveOverwriteWithRetry(tmp, filePath);
    }

    public static async Task WriteAsync<T>(string filePath, T value, JsonTypeInfo<T> typeInfo, CancellationToken ct = default)
    {
        EnsureDirectory(filePath);
        var tmp = filePath + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, value, typeInfo, ct).ConfigureAwait(false);
        }
        await AtomicFile.MoveOverwriteWithRetryAsync(tmp, filePath, ct).ConfigureAwait(false);
    }

    private static void EnsureDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
    }
}
