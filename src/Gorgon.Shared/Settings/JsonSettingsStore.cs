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

    public async Task SaveAsync(T value, CancellationToken ct = default)
    {
        EnsureDirectory();
        var tmp = FilePath + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, value, _typeInfo, ct);
        }
        File.Move(tmp, FilePath, overwrite: true);
    }

    public void Save(T value)
    {
        EnsureDirectory();
        var tmp = FilePath + ".tmp";
        using (var stream = File.Create(tmp))
        {
            JsonSerializer.Serialize(stream, value, _typeInfo);
        }
        File.Move(tmp, FilePath, overwrite: true);
    }

    private void EnsureDirectory()
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
    }
}
