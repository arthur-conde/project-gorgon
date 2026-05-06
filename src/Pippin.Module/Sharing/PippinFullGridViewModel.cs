using System.Globalization;
using System.Windows.Media.Imaging;
using Mithril.Shared.Icons;
using Pippin.Domain;

namespace Pippin.Sharing;

/// <summary>
/// View model for the archival full-grid PNG export. Materializes every food row
/// (catalog ∪ orphan-unknowns ∪ sender-only InternalNames) with its bitmap icon
/// pre-resolved through the cache, so the off-screen render in
/// <see cref="PippinShareCardRenderer"/> doesn't depend on the IconImage Loaded
/// lifecycle.
/// </summary>
public sealed class PippinFullGridViewModel
{
    // 960px wide exports cleanly fit 3 × 300px card cells inside the 20px DockPanel
    // margin (920 inner). Larger widths waste right-edge whitespace because the
    // WrapPanel left-aligns and won't stretch cards. Height is measure-driven —
    // the renderer reads <see cref="System.Windows.FrameworkElement.DesiredSize"/>
    // after a layout pass, so the image auto-sizes to wrap content exactly.
    public const double Width = 960;

    public PippinFullGridViewModel(
        PippinSharePayload payload,
        FoodCatalog catalog,
        int gourmandLevel,
        IIconCacheService iconCache)
    {
        CharacterTitle = string.IsNullOrWhiteSpace(payload.CharacterName)
            ? "Pippin · Gourmand"
            : payload.CharacterName!;
        HasIdentity = !string.IsNullOrWhiteSpace(payload.CharacterName);

        var eatenCount = payload.EatenFoodsByInternalName.Count + (payload.UnknownByName?.Count ?? 0);
        var totalCount = catalog.TotalCount > 0 ? catalog.TotalCount : eatenCount;
        var pct = totalCount > 0 ? 100.0 * eatenCount / totalCount : 0.0;

        StatsLine = string.Format(CultureInfo.InvariantCulture,
            "{0} / {1}  ·  {2:0.#}%  ·  {3}",
            eatenCount, totalCount,
            pct,
            gourmandLevel > 0 ? $"Gourmand Lv {gourmandLevel}" : "Gourmand");

        LastSyncText = payload.LastReportTime is { } t
            ? $"Synced {t.LocalDateTime:g}"
            : "";

        // Build rows: catalog + sender-only InternalNames + sender-only display-name
        // unknowns. Mirror the live GourmandViewModel ordering so the image looks like
        // the live grid.
        var rows = new List<PippinFullGridRow>(catalog.TotalCount + (payload.UnknownByName?.Count ?? 0) + 16);

        var eaten = payload.EatenFoodsByInternalName;
        foreach (var food in catalog.ByInternalName.Values)
        {
            var hasCount = eaten.TryGetValue(food.InternalName, out var count);
            var icon = food.IconId > 0 ? iconCache.GetOrLoadIcon(food.IconId) : null;
            // For the sender's own export the gourmandLevel is known, so locked items
            // render with the same lock-badge + dim treatment as the in-app cards.
            var isLocked = !hasCount && food.GourmandLevelReq > 0 && gourmandLevel < food.GourmandLevelReq;
            rows.Add(new PippinFullGridRow(
                food.Name, food.FoodType, food.FoodLevel, food.GourmandLevelReq,
                hasCount ? count : 0, hasCount, isLocked, food.DietaryTags, icon));
        }

        if (payload.UnknownByName is { } unknowns)
        {
            foreach (var (name, count) in unknowns)
                rows.Add(new PippinFullGridRow(name, "Unknown", 0, 0, count, true, false, Array.Empty<string>(), null));
        }

        foreach (var (internalName, count) in eaten)
        {
            if (catalog.TryGetByInternalName(internalName, out _)) continue;
            rows.Add(new PippinFullGridRow(internalName, "Unknown", 0, 0, count, true, false, Array.Empty<string>(), null));
        }

        rows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Rows = rows;
    }

    public string CharacterTitle { get; }
    public bool HasIdentity { get; }
    public string StatsLine { get; }
    public string LastSyncText { get; }
    public IReadOnlyList<PippinFullGridRow> Rows { get; }
}

public sealed record PippinFullGridRow(
    string Name,
    string FoodType,
    int FoodLevel,
    int GourmandLevelReq,
    int EatenCount,
    bool IsEaten,
    bool IsLocked,
    IReadOnlyList<string> DietaryTags,
    BitmapImage? Icon)
{
    public bool HasIcon => Icon is not null;
}
