using Gorgon.Shared.Reference;

namespace Smaug.State;

/// <summary>
/// One (item × vendor) row for the Vendor Catalog view. Built from
/// <see cref="IReferenceDataService.ItemSources"/> × <see cref="IReferenceDataService.Npcs"/>.
/// </summary>
public sealed record VendorCatalogEntry(
    string ItemInternalName,
    string ItemName,
    int ItemIconId,
    decimal ItemBaseValue,
    string NpcKey,
    string NpcName,
    string Area,
    string? MinFavorTier);

/// <summary>
/// Projects the CDN's <c>sources_items.json</c> + <c>npcs.json</c> into a flat list of
/// item→vendor pairs for the catalog view. Rebuilds on reference-data refresh.
/// </summary>
public sealed class VendorCatalogService
{
    private readonly IReferenceDataService _refData;
    private IReadOnlyList<VendorCatalogEntry> _entries = [];

    public IReadOnlyList<VendorCatalogEntry> Entries => _entries;
    public event EventHandler? CatalogChanged;

    public VendorCatalogService(IReferenceDataService refData)
    {
        _refData = refData;
        Rebuild();
        _refData.FileUpdated += (_, key) =>
        {
            if (key is "sources_items" or "items" or "npcs")
                Rebuild();
        };
    }

    private void Rebuild()
    {
        var entries = new List<VendorCatalogEntry>(4096);
        foreach (var (internalName, sources) in _refData.ItemSources)
        {
            if (!_refData.ItemsByInternalName.TryGetValue(internalName, out var item)) continue;

            foreach (var src in sources)
            {
                if (!string.Equals(src.Type, "Vendor", StringComparison.Ordinal)) continue;
                if (string.IsNullOrEmpty(src.Npc)) continue;

                _refData.Npcs.TryGetValue(src.Npc, out var npc);
                var storeService = npc?.Services.FirstOrDefault(s =>
                    string.Equals(s.Type, "Store", StringComparison.Ordinal));

                entries.Add(new VendorCatalogEntry(
                    ItemInternalName: internalName,
                    ItemName: item.Name,
                    ItemIconId: item.IconId,
                    ItemBaseValue: item.Value,
                    NpcKey: src.Npc,
                    NpcName: npc?.Name ?? src.Npc.Replace("NPC_", ""),
                    Area: npc?.Area ?? "",
                    MinFavorTier: storeService?.MinFavorTier));
            }
        }

        _entries = entries;
        CatalogChanged?.Invoke(this, EventArgs.Empty);
    }
}
