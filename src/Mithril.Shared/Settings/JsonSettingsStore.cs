using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Mithril.Shared.Settings;

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
        if (!File.Exists(FilePath)) return PostInit(new T());
        using var stream = File.OpenRead(FilePath);
        var result = JsonSerializer.Deserialize(stream, _typeInfo);
        return PostInit(result ?? new T());
    }

    public async Task<T> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(FilePath)) return PostInit(new T());
        await using var stream = File.OpenRead(FilePath);
        var result = await JsonSerializer.DeserializeAsync(stream, _typeInfo, ct);
        return PostInit(result ?? new T());
    }

    // STJ source-gen populates property values without running ctors that
    // would have wired SettingsNode.Bubble subscriptions on nested children.
    // IPostLoadInit lets the loaded type re-wire after deserialize. A
    // freshly-constructed `new T()` has no children, so the call is a
    // safe no-op there — the symmetry keeps the load contract simple.
    private static T PostInit(T value)
    {
        (value as IPostLoadInit)?.PostLoadInit();
        return value;
    }

    public Task SaveAsync(T value, CancellationToken ct = default)
        => AtomicJsonWriter.WriteAsync(FilePath, value, _typeInfo, ct);

    public void Save(T value)
        => AtomicJsonWriter.Write(FilePath, value, _typeInfo);
}
