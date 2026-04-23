using Gorgon.Shared.Character;
using Gorgon.Shared.Reference;
using Gorgon.Shared.Storage;
using Smaug.Domain;

namespace Smaug.State;

/// <summary>
/// One owned item from the active character's storage export, used as the
/// left-pane picker source for the Sell Planner tab.
/// </summary>
public sealed record SellPlannerItem(
    int TypeId,
    string InternalName,
    string DisplayName,
    int IconId,
    decimal UnitValue,
    int StackCount,
    string Location);

/// <summary>
/// One vendor row shown for a selected item: expected sell price plus an
/// accessibility flag based on the player's current favor vs the vendor's MinFavorTier.
/// </summary>
public sealed record SellPlannerVendorRow(
    string NpcKey,
    string NpcName,
    string Area,
    string? MinFavorTier,
    string? PlayerFavorTier,
    bool IsAccessible,
    PriceEstimateResult? Estimate);

/// <summary>
/// Builds a per-item list of vendors who will accept the item, ordered by expected
/// sell price. Vendors the player does not yet have the favor to access are kept
/// but marked <see cref="SellPlannerVendorRow.IsAccessible"/> = false.
/// </summary>
public sealed class SellPlannerService
{
    private readonly IReferenceDataService _refData;
    private readonly IActiveCharacterService _activeChar;
    private readonly PriceCalibrationService _calibration;
    private readonly VendorSellContext _sellContext;
    private readonly IFavorLookupService? _favorLookup;

    private IReadOnlyList<SellPlannerItem> _ownedItems = [];

    public IReadOnlyList<SellPlannerItem> OwnedItems => _ownedItems;
    public string? ActiveCharacterName => _activeChar.ActiveCharacterName;

    public event EventHandler? ItemsChanged;
    public event EventHandler? VendorsChanged;

    public SellPlannerService(
        IReferenceDataService refData,
        IActiveCharacterService activeChar,
        PriceCalibrationService calibration,
        VendorSellContext sellContext,
        IFavorLookupService? favorLookup = null)
    {
        _refData = refData;
        _activeChar = activeChar;
        _calibration = calibration;
        _sellContext = sellContext;
        _favorLookup = favorLookup;

        _activeChar.ActiveCharacterChanged += (_, _) => RebuildOwnedItems();
        _activeChar.StorageReportsChanged += (_, _) => RebuildOwnedItems();
        _refData.FileUpdated += (_, key) =>
        {
            if (key is "items" or "npcs") VendorsChanged?.Invoke(this, EventArgs.Empty);
        };
        _calibration.DataChanged += (_, _) => VendorsChanged?.Invoke(this, EventArgs.Empty);
        if (_favorLookup is not null)
            _favorLookup.FavorChanged += (_, _) => VendorsChanged?.Invoke(this, EventArgs.Empty);

        RebuildOwnedItems();
    }

    public IReadOnlyList<SellPlannerVendorRow> GetVendorsFor(SellPlannerItem item)
    {
        if (!_refData.ItemsByInternalName.TryGetValue(item.InternalName, out var itemEntry))
            return [];

        var itemKeywords = new HashSet<string>(itemEntry.Keywords.Select(k => k.Tag), StringComparer.Ordinal);
        var rows = new List<SellPlannerVendorRow>();

        foreach (var (npcKey, npc) in _refData.Npcs)
        {
            var store = npc.Services.FirstOrDefault(s =>
                string.Equals(s.Type, "Store", StringComparison.Ordinal));
            if (store is null) continue;
            if (!VendorAcceptsItem(store, itemKeywords)) continue;

            var playerTier = _favorLookup?.GetFavorTier(npcKey);
            var isAccessible = store.MinFavorTier is null ||
                               FavorTierName.IsAtLeast(playerTier ?? FavorTierName.Neutral, store.MinFavorTier);

            // Use the player's known tier for the estimate when we have it; otherwise fall back
            // to the vendor's requirement so users see some number for tier-gated vendors.
            var estimateTier = playerTier ?? store.MinFavorTier ?? FavorTierName.Neutral;
            var estimate = _calibration.EstimateSellPrice(
                npcKey,
                item.InternalName,
                estimateTier,
                _sellContext.CivicPrideLevel);

            rows.Add(new SellPlannerVendorRow(
                NpcKey: npcKey,
                NpcName: npc.Name,
                Area: string.IsNullOrEmpty(npc.Area) ? "(Unknown Area)" : npc.Area,
                MinFavorTier: store.MinFavorTier,
                PlayerFavorTier: playerTier,
                IsAccessible: isAccessible,
                Estimate: estimate));
        }

        rows.Sort((a, b) =>
        {
            // Accessible rows first; within each group, sort by expected price descending.
            if (a.IsAccessible != b.IsAccessible) return a.IsAccessible ? -1 : 1;
            var ap = a.Estimate?.Price ?? -1;
            var bp = b.Estimate?.Price ?? -1;
            return bp.CompareTo(ap);
        });

        return rows;
    }

    private void RebuildOwnedItems()
    {
        var report = _activeChar.ActiveStorageContents;
        if (report is null)
        {
            _ownedItems = [];
            ItemsChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Fold stacks across vaults: one row per TypeId with summed stack count,
        // but surface the richest location info we have.
        var byTypeId = new Dictionary<int, (int Count, string Location, ItemEntry? Entry)>(report.Items.Count);
        foreach (var stockItem in report.Items)
        {
            if (!_refData.Items.TryGetValue(stockItem.TypeID, out var entry)) continue;
            var loc = StorageReportLoader.NormalizeLocation(stockItem.StorageVault, stockItem.IsInInventory);
            if (byTypeId.TryGetValue(stockItem.TypeID, out var existing))
            {
                byTypeId[stockItem.TypeID] = (existing.Count + stockItem.StackSize, existing.Location, entry);
            }
            else
            {
                byTypeId[stockItem.TypeID] = (stockItem.StackSize, loc, entry);
            }
        }

        var items = new List<SellPlannerItem>(byTypeId.Count);
        foreach (var (typeId, data) in byTypeId)
        {
            if (data.Entry is null) continue;
            items.Add(new SellPlannerItem(
                TypeId: typeId,
                InternalName: data.Entry.InternalName,
                DisplayName: data.Entry.Name,
                IconId: data.Entry.IconId,
                UnitValue: data.Entry.Value,
                StackCount: data.Count,
                Location: data.Location));
        }

        items.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        _ownedItems = items;
        ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool VendorAcceptsItem(NpcService store, HashSet<string> itemKeywords)
    {
        if (store.CapIncreases.Count == 0) return false;
        foreach (var cap in store.CapIncreases)
        {
            if (cap.Keywords.Count == 0) return true;
            foreach (var k in cap.Keywords)
                if (itemKeywords.Contains(k)) return true;
        }
        return false;
    }
}
