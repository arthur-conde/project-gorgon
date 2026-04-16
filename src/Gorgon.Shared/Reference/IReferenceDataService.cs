namespace Gorgon.Shared.Reference;

/// <summary>
/// Manages dev-published JSON reference data from cdn.projectgorgon.com.
/// Loaded eagerly at construction from disk cache (or bundled fallback) so
/// consumers see a populated dictionary synchronously. CDN refresh runs in
/// the background and raises <see cref="FileUpdated"/> when new data lands.
/// </summary>
public interface IReferenceDataService
{
    IReadOnlyList<string> Keys { get; }

    IReadOnlyDictionary<long, ItemEntry> Items { get; }

    /// <summary>InternalName → ItemEntry lookup. Useful when the log gives an InternalName but no item id.</summary>
    IReadOnlyDictionary<string, ItemEntry> ItemsByInternalName { get; }

    ReferenceFileSnapshot GetSnapshot(string key);

    Task RefreshAsync(string key, CancellationToken ct = default);

    Task RefreshAllAsync(CancellationToken ct = default);

    /// <summary>Fire-and-forget refresh of every known file. Intended for app start.</summary>
    void BeginBackgroundRefresh();

    event EventHandler<string>? FileUpdated;
}
