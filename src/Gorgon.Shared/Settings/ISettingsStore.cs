namespace Gorgon.Shared.Settings;

public interface ISettingsStore<T> where T : class, new()
{
    string FilePath { get; }
    Task<T> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(T value, CancellationToken ct = default);
    void Save(T value);
}
