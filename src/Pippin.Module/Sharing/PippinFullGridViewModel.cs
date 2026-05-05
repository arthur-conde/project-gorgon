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
    public const double Width = 1200;
    public const double HeaderHeight = 36;
    public const double RowHeight = 32;

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
            rows.Add(new PippinFullGridRow(
                food.Name, food.FoodType, food.FoodLevel, food.GourmandLevelReq,
                hasCount ? count : 0, hasCount, FormatTags(food.DietaryTags), icon));
        }

        if (payload.UnknownByName is { } unknowns)
        {
            foreach (var (name, count) in unknowns)
                rows.Add(new PippinFullGridRow(name, "Unknown", 0, 0, count, true, "", null));
        }

        foreach (var (internalName, count) in eaten)
        {
            if (catalog.TryGetByInternalName(internalName, out _)) continue;
            rows.Add(new PippinFullGridRow(internalName, "Unknown", 0, 0, count, true, "", null));
        }

        rows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Rows = rows;

        TotalHeight = HeaderHeight + Rows.Count * RowHeight + 80; // header strip + footer pad
    }

    public string CharacterTitle { get; }
    public bool HasIdentity { get; }
    public string StatsLine { get; }
    public string LastSyncText { get; }
    public IReadOnlyList<PippinFullGridRow> Rows { get; }

    /// <summary>Computed pixel height needed to fit every row + header + footer.</summary>
    public double TotalHeight { get; }

    private static string FormatTags(IReadOnlyList<string> tags) => string.Join(", ", tags);
}

public sealed record PippinFullGridRow(
    string Name,
    string FoodType,
    int FoodLevel,
    int GourmandLevelReq,
    int EatenCount,
    bool IsEaten,
    string DietaryTags,
    BitmapImage? Icon)
{
    public bool HasIcon => Icon is not null;
}
