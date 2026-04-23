using Gorgon.Shared.Reference;
using Smaug.Domain;

namespace Smaug.State;

/// <summary>
/// One (item × vendor) row for the Vendor Catalog view. Built from
/// <see cref="IReferenceDataService.ItemSources"/> × <see cref="IReferenceDataService.Npcs"/>.
/// <see cref="IsAcceptable"/> combines the vendor's MinFavorTier gate and the
/// resolved MaxGold cap for this item's keywords against the player's current
/// favor + Civic Pride; null means "unknown" (player favor not yet tracked).
/// </summary>
public sealed record VendorCatalogEntry(
    string ItemInternalName,
    string ItemName,
    int ItemIconId,
    decimal ItemBaseValue,
    string NpcKey,
    string NpcName,
    string Area,
    string? MinFavorTier,
    string? PlayerFavorTier,
    int? EffectiveMaxGold,
    bool? IsAcceptable);

/// <summary>
/// Projects the CDN's <c>sources_items.json</c> + <c>npcs.json</c> into a flat list of
/// item→vendor pairs for the catalog view. Rebuilds on reference-data, favor, or
/// Civic Pride changes.
/// </summary>
public sealed class VendorCatalogService
{
    private readonly IReferenceDataService _refData;
    private readonly IFavorLookupService? _favorLookup;
    private readonly VendorSellContext _sellContext;
    private IReadOnlyList<VendorCatalogEntry> _entries = [];

    public IReadOnlyList<VendorCatalogEntry> Entries => _entries;
    public event EventHandler? CatalogChanged;

    public VendorCatalogService(
        IReferenceDataService refData,
        VendorSellContext sellContext,
        IFavorLookupService? favorLookup = null)
    {
        _refData = refData;
        _sellContext = sellContext;
        _favorLookup = favorLookup;

        Rebuild();
        _refData.FileUpdated += (_, key) =>
        {
            if (key is "sources_items" or "items" or "npcs")
                Rebuild();
        };
        if (_favorLookup is not null)
            _favorLookup.FavorChanged += (_, _) => Rebuild();
    }

    public void Refresh() => Rebuild();

    private void Rebuild()
    {
        var entries = new List<VendorCatalogEntry>(4096);
        foreach (var (internalName, sources) in _refData.ItemSources)
        {
            if (!_refData.ItemsByInternalName.TryGetValue(internalName, out var item)) continue;
            var itemKeywords = new HashSet<string>(item.Keywords.Select(k => k.Tag), StringComparer.Ordinal);

            foreach (var src in sources)
            {
                if (!string.Equals(src.Type, "Vendor", StringComparison.Ordinal)) continue;
                if (string.IsNullOrEmpty(src.Npc)) continue;

                _refData.Npcs.TryGetValue(src.Npc, out var npc);
                var storeService = npc?.Services.FirstOrDefault(s =>
                    string.Equals(s.Type, "Store", StringComparison.Ordinal));

                string? playerTier = null;
                int? maxGold = null;
                bool? acceptable = null;
                if (storeService is not null)
                {
                    playerTier = _favorLookup?.GetFavorTier(src.Npc);
                    if (playerTier is not null)
                    {
                        maxGold = VendorCapResolver.ResolveMaxGold(
                            storeService, playerTier, itemKeywords, _sellContext.CivicPrideLevel);
                        acceptable = maxGold is not null && item.Value <= maxGold.Value;
                    }
                }

                entries.Add(new VendorCatalogEntry(
                    ItemInternalName: internalName,
                    ItemName: item.Name,
                    ItemIconId: item.IconId,
                    ItemBaseValue: item.Value,
                    NpcKey: src.Npc,
                    NpcName: npc?.Name ?? src.Npc.Replace("NPC_", ""),
                    Area: npc?.Area ?? "",
                    MinFavorTier: storeService?.MinFavorTier,
                    PlayerFavorTier: playerTier,
                    EffectiveMaxGold: maxGold,
                    IsAcceptable: acceptable));
            }
        }

        _entries = entries;
        CatalogChanged?.Invoke(this, EventArgs.Empty);
    }
}
