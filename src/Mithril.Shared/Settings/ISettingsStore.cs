namespace Mithril.Shared.Settings;

public interface ISettingsStore<T> where T : class, new()
{
    string FilePath { get; }
    T Load();
    Task<T> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(T value, CancellationToken ct = default);
    void Save(T value);
}
