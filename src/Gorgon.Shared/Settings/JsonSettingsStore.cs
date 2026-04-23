using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Gorgon.Shared.Settings;

public sealed class JsonSettingsStore<T> : ISettingsStore<T> where T : class, new()
{
    private readonly JsonTypeInfo<T> _typeInfo;

    public JsonSettingsStore(string filePath, JsonTypeInfo<T> typeInfo)
    {
        FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _typeInfo = typeInfo ?? throw new ArgumentNullException(nameof(typeInfo));
    }

    public string FilePath { get; }

    public T Load()
    {
        if (!File.Exists(FilePath)) return new T();
        using var stream = File.OpenRead(FilePath);
        var result = JsonSerializer.Deserialize(stream, _typeInfo);
        return result ?? new T();
    }

    public async Task<T> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(FilePath)) return new T();
        await using var stream = File.OpenRead(FilePath);
        var result = await JsonSerializer.DeserializeAsync(stream, _typeInfo, ct);
        return result ?? new T();
    }

    public Task SaveAsync(T value, CancellationToken ct = default)
        => AtomicJsonWriter.WriteAsync(FilePath, value, _typeInfo, ct);

    public void Save(T value)
        => AtomicJsonWriter.Write(FilePath, value, _typeInfo);
}
