using Mithril.Shared.Reference;
using Mithril.Shared.Storage;

namespace Mithril.Shared.Wpf;

/// <summary>
/// Backing view-model for <see cref="IngredientSourcesWindow"/>. Built once
/// per-show by <see cref="IngredientSourcesPresenter"/> from an
/// <see cref="IngredientSourcesInput"/> + <see cref="IReferenceDataService"/>.
/// </summary>
public sealed record IngredientSourcesViewModel(
    string Title,
    string? KeywordsLabel,
    int OnHandTotal,
    IReadOnlyList<OnHandLocationGroup> OnHand,
    IReadOnlyList<AcquisitionSource> Sources,
    string? SourcesPlaceholder,
    int SelectedTabIndex)
{
    public bool HasOnHand => OnHand.Count > 0;
    public bool HasSources => Sources.Count > 0;
    public bool HasSourcesPlaceholder => !string.IsNullOrEmpty(SourcesPlaceholder);

    public static IngredientSourcesViewModel Build(IngredientSourcesInput input, IReferenceDataService refData)
    {
        var onHand = BuildOnHand(input.OnHand);
        var onHandTotal = onHand.Sum(g => g.TotalQuantity);

        IReadOnlyList<AcquisitionSource> sources;
        string? placeholder;

        if (input.ItemInternalName is { } itemName)
        {
            sources = BuildSources(itemName, refData);
            placeholder = sources.Count == 0
                ? "No vendor, drop, or quest sources are catalogued for this item."
                : null;
        }
        else
        {
            sources = [];
            placeholder = "Sources for items matching this keyword set are not aggregated yet.";
        }

        // Default tab: On hand (0) when stock exists, otherwise Sources (1).
        var selectedTabIndex = onHand.Count > 0 ? 0 : 1;

        return new IngredientSourcesViewModel(input.Title, input.KeywordsLabel, onHandTotal, onHand, sources, placeholder, selectedTabIndex);
    }

    private static IReadOnlyList<OnHandLocationGroup> BuildOnHand(IReadOnlyList<IngredientLocation> entries)
    {
        if (entries.Count == 0) return [];
        return entries
            .GroupBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var byItem = g
                    .GroupBy(e => e.ItemInternalName, StringComparer.Ordinal)
                    .Select(ig => new OnHandItemBreakdown(
                        DisplayName: ig.First().DisplayName,
                        IconId: ig.First().IconId,
                        Quantity: ig.Sum(x => x.Quantity),
                        ItemInternalName: ig.Key))
                    .OrderByDescending(b => b.Quantity)
                    .ThenBy(b => b.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return new OnHandLocationGroup(
                    Label: g.Key,
                    TotalQuantity: byItem.Sum(b => b.Quantity),
                    Items: byItem);
            })
            .OrderByDescending(g => g.TotalQuantity)
            .ThenBy(g => g.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<AcquisitionSource> BuildSources(string itemInternalName, IReferenceDataService refData)
    {
        if (!refData.ItemSources.TryGetValue(itemInternalName, out var raw)) return [];

        var result = new List<AcquisitionSource>(raw.Count);
        foreach (var src in raw)
        {
            if (string.IsNullOrEmpty(src.Type)) continue;

            switch (src.Type)
            {
                case "Vendor":
                case "Barter":
                case "NpcGift":
                    if (string.IsNullOrEmpty(src.Npc)) continue;
                    if (!refData.Npcs.TryGetValue(src.Npc, out var npc)) continue;
                    result.Add(new AcquisitionSource(
                        Kind: src.Type,
                        Label: npc.Name,
                        AreaFriendlyName: string.IsNullOrEmpty(npc.Area) ? null : npc.Area,
                        Detail: null,
                        Requirement: ResolveFavorRequirement(src.Type, npc)));
                    break;

                case "Quest":
                    result.Add(new AcquisitionSource("Quest", "Quest reward", AreaFriendlyName: null, Detail: src.Context));
                    break;

                case "Recipe":
                    result.Add(new AcquisitionSource("Recipe", "Crafted", AreaFriendlyName: null, Detail: src.Context));
                    break;

                case "Monster":
                case "Drop":
                case "Angling":
                case "HangOut":
                {
                    var area = ResolveArea(src.Context, refData);
                    result.Add(new AcquisitionSource(
                        Kind: src.Type,
                        Label: src.Context ?? src.Type,
                        AreaFriendlyName: area,
                        Detail: null));
                    break;
                }

                default:
                    result.Add(new AcquisitionSource(src.Type, src.Context ?? src.Type, AreaFriendlyName: null, Detail: null));
                    break;
            }
        }

        return result
            .OrderBy(s => s.Kind, StringComparer.Ordinal)
            .ThenBy(s => s.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ResolveArea(string? context, IReferenceDataService refData)
    {
        if (string.IsNullOrEmpty(context)) return null;
        return refData.Areas.TryGetValue(context, out var area) ? area.FriendlyName : null;
    }

    /// <summary>
    /// Map a source kind ("Vendor", "Barter", "NpcGift") to the NpcService type that
    /// gates access, then surface its <see cref="NpcService.MinFavorTier"/> as a
    /// human-readable line. Vendor sources gate on the NPC's "Store" service; Barter
    /// sources gate on the matching "Barter" service. NpcGift acceptance is gated
    /// per-preference (<see cref="NpcPreference.RequiredFavorTier"/>) rather than per-NPC,
    /// so we skip the requirement line for that kind.
    /// </summary>
    private static string? ResolveFavorRequirement(string sourceKind, NpcEntry npc)
    {
        var serviceType = sourceKind switch
        {
            "Vendor" => "Store",
            "Barter" => "Barter",
            _ => null,
        };
        if (serviceType is null) return null;

        foreach (var svc in npc.Services)
        {
            if (!string.Equals(svc.Type, serviceType, StringComparison.Ordinal)) continue;
            if (string.IsNullOrEmpty(svc.MinFavorTier)) return null;
            return $"Requires {svc.MinFavorTier} or higher";
        }
        return null;
    }
}

public sealed record OnHandLocationGroup(
    string Label,
    int TotalQuantity,
    IReadOnlyList<OnHandItemBreakdown> Items);

public sealed record OnHandItemBreakdown(
    string DisplayName,
    int IconId,
    int Quantity,
    string ItemInternalName);

public sealed record AcquisitionSource(
    string Kind,
    string Label,
    string? AreaFriendlyName,
    string? Detail,
    string? Requirement = null);
