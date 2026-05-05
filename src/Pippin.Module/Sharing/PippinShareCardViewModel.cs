using System.Globalization;
using System.Windows.Media.Imaging;
using Mithril.Shared.Icons;
using Pippin.Domain;

namespace Pippin.Sharing;

/// <summary>
/// View model for the social-card share image. Built from a <see cref="PippinSharePayload"/>
/// joined against the local <see cref="FoodCatalog"/>; bitmap icons are resolved through
/// <see cref="IIconCacheService"/> at construction time so the off-screen render in
/// <see cref="PippinShareCardRenderer"/> doesn't depend on the IconImage Loaded
/// lifecycle (which doesn't fire for unparented visuals).
/// </summary>
public sealed class PippinShareCardViewModel
{
    public const double CardWidth = 1000;
    public const double CardHeight = 400;
    private const double BarMaxPixelWidth = 928; // 1000 - 32 padding × 2 - 4 (border + margin)

    public PippinShareCardViewModel(
        PippinSharePayload payload,
        FoodCatalog catalog,
        int gourmandLevel,
        IIconCacheService iconCache)
    {
        CharacterTitle = string.IsNullOrWhiteSpace(payload.CharacterName) ? "Pippin · Gourmand" : payload.CharacterName!;
        HasIdentity = !string.IsNullOrWhiteSpace(payload.CharacterName);

        var eatenCount = payload.EatenFoodsByInternalName.Count + (payload.UnknownByName?.Count ?? 0);
        var totalCount = catalog.TotalCount > 0 ? catalog.TotalCount : eatenCount;
        var pct = totalCount > 0 ? 100.0 * eatenCount / totalCount : 0.0;

        EatenCount = eatenCount;
        TotalCount = totalCount;
        CompletionText = string.Format(CultureInfo.InvariantCulture,
            "{0} / {1}  ·  {2:0.#}%", eatenCount, totalCount, pct);
        CompletionRatio = totalCount > 0 ? Math.Clamp((double)eatenCount / totalCount, 0, 1) : 0;
        BarFillPixelWidth = CompletionRatio * BarMaxPixelWidth;

        GourmandLevelText = gourmandLevel > 0 ? $"Gourmand Lv {gourmandLevel}" : "Gourmand";

        LastSyncText = payload.LastReportTime is { } t
            ? $"Synced {t.LocalDateTime:g}"
            : "Sync time not available";

        // Top 5 by count, joined against the recipient's catalog so display names
        // reflect what's actually rendered in the local UI.
        TopEaten = payload.EatenFoodsByInternalName
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(5)
            .Select(kv =>
            {
                var name = kv.Key;
                BitmapImage? icon = null;
                if (catalog.TryGetByInternalName(kv.Key, out var entry))
                {
                    name = entry.Name;
                    if (entry.IconId > 0)
                        icon = iconCache.GetOrLoadIcon(entry.IconId);
                }
                return new TopEatenItem(name, kv.Value, icon);
            })
            .ToList();
    }

    public string CharacterTitle { get; }
    public bool HasIdentity { get; }
    public int EatenCount { get; }
    public int TotalCount { get; }
    public string CompletionText { get; }
    public double CompletionRatio { get; }
    public double BarFillPixelWidth { get; }
    public string GourmandLevelText { get; }
    public string LastSyncText { get; }
    public IReadOnlyList<TopEatenItem> TopEaten { get; }
}

public sealed record TopEatenItem(string Name, int Count, BitmapImage? Icon)
{
    public string CountText => $"×{Count}";
    public bool HasIcon => Icon is not null;
}
