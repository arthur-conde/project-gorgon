using Pippin.Domain;
using Pippin.Parsing;

namespace Pippin.State;

/// <summary>
/// Maintains the set of foods the player has eaten.
/// Authoritative source: the in-game Foods Consumed report parsed from the log.
/// </summary>
public sealed class GourmandStateMachine
{
    private readonly FoodCatalog _catalog;
    private readonly Dictionary<string, int> _eatenFoods = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _lastReportTime;

    public event EventHandler? StateChanged;

    public GourmandStateMachine(FoodCatalog catalog)
    {
        _catalog = catalog;
    }

    public IReadOnlyDictionary<string, int> EatenFoods => _eatenFoods;
    public DateTimeOffset? LastReportTime => _lastReportTime;
    public int EatenCount => _eatenFoods.Count;
    public int TotalFoodCount => _catalog.TotalCount;
    public bool HasData => _eatenFoods.Count > 0;

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
        _eatenFoods.Clear();
        foreach (var (name, count) in persisted.EatenFoods)
            _eatenFoods[name] = count;
        _lastReportTime = persisted.LastReportTime;
    }

    private void HandleReport(FoodsConsumedReport report)
    {
        _eatenFoods.Clear();
        foreach (var food in report.Foods)
            _eatenFoods[food.Name] = food.Count;
        _lastReportTime = DateTimeOffset.UtcNow;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
