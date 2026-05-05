using Pippin.Domain;
using Pippin.Parsing;

namespace Pippin.State;

/// <summary>
/// Maintains the set of foods the player has eaten.
/// Authoritative source: the in-game Foods Consumed report parsed from the log.
///
/// Entries are kept by item <c>InternalName</c>; the in-game report only emits display
/// names, so reports are joined against <see cref="FoodCatalog"/> on ingest. Names that
/// fail to resolve are kept in <see cref="UnknownByName"/> so the UI can still show them.
/// </summary>
public sealed class GourmandStateMachine
{
    private readonly FoodCatalog _catalog;
    private readonly Dictionary<string, int> _eatenByInternalName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _unknownByName = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _lastReportTime;

    public event EventHandler? StateChanged;

    public GourmandStateMachine(FoodCatalog catalog)
    {
        _catalog = catalog;
    }

    public IReadOnlyDictionary<string, int> EatenFoodsByInternalName => _eatenByInternalName;
    public IReadOnlyDictionary<string, int> UnknownByName => _unknownByName;
    public DateTimeOffset? LastReportTime => _lastReportTime;

    /// <summary>
    /// Distinct foods eaten — unique <c>InternalName</c> entries plus orphan unknowns.
    /// </summary>
    public int EatenCount => _eatenByInternalName.Count + _unknownByName.Count;
    public int TotalFoodCount => _catalog.TotalCount;
    public bool HasData => EatenCount > 0;

    public void Apply(GourmandEvent evt)
    {
        switch (evt)
        {
            case FoodsConsumedReport report:
                HandleReport(report);
                break;
        }
    }

    /// <summary>
    /// Restore persisted state on startup without raising events.
    /// </summary>
    public void Hydrate(GourmandState persisted)
    {
        _eatenByInternalName.Clear();
        foreach (var (internalName, count) in persisted.EatenFoodsByInternalName)
            _eatenByInternalName[internalName] = count;

        _unknownByName.Clear();
        foreach (var (name, count) in persisted.UnknownByName)
            _unknownByName[name] = count;

        _lastReportTime = persisted.LastReportTime;
    }

    /// <summary>
    /// Re-resolve any unknown-by-name entries against the catalog. Called after the catalog
    /// completes a build so foods the player ate before the CDN snapshot caught up are
    /// promoted out of the unknown bucket without waiting for the next in-game report.
    /// </summary>
    public bool ReconcileUnknowns()
    {
        if (_unknownByName.Count == 0) return false;

        var resolved = new List<string>();
        foreach (var (name, count) in _unknownByName)
        {
            if (!_catalog.TryGetByName(name, out var food)) continue;
            _eatenByInternalName[food.InternalName] = Math.Max(
                count,
                _eatenByInternalName.TryGetValue(food.InternalName, out var existing) ? existing : 0);
            resolved.Add(name);
        }

        if (resolved.Count == 0) return false;

        foreach (var name in resolved) _unknownByName.Remove(name);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Drain a v1 display-name dict into the resolved/unknown buckets. Used by the
    /// schema migration once the catalog is ready. Returns true if any entries were
    /// applied (and therefore the persisted state should be flushed).
    /// </summary>
    public bool ApplyLegacyByName(IReadOnlyDictionary<string, int> legacy)
    {
        if (legacy.Count == 0) return false;

        foreach (var (name, count) in legacy)
        {
            if (_catalog.TryGetByName(name, out var food))
                _eatenByInternalName[food.InternalName] = count;
            else
                _unknownByName[name] = count;
        }

        _lastReportTime ??= DateTimeOffset.UtcNow;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void HandleReport(FoodsConsumedReport report)
    {
        _eatenByInternalName.Clear();
        _unknownByName.Clear();

        foreach (var food in report.Foods)
        {
            if (_catalog.TryGetByName(food.Name, out var entry))
                _eatenByInternalName[entry.InternalName] = food.Count;
            else
                _unknownByName[food.Name] = food.Count;
        }

        _lastReportTime = DateTimeOffset.UtcNow;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
