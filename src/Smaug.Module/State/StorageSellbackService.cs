using Arda.Composition;
using Mithril.Reference.Models.Items;
using Mithril.Shared.Character;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;
using Mithril.GameReports;
using Smaug.Domain;
using static Mithril.Reference.Models.Npcs.FavorTierExtensions;

namespace Smaug.State;

public sealed record StorageSellbackItem(
    int TypeId,
    string ItemName,
    int IconId,
    int StackSize,
    decimal UnitValue,
    string Location,
    bool? IsAcceptable,
    int? EffectiveMaxGold);

public sealed record StorageSellbackVendor(
    string NpcKey,
    string NpcName,
    string Area,
    string? MinFavorTier,
    string? PlayerFavorTier,
    IReadOnlyList<StorageSellbackItem> Items)
{
    public int DistinctItemCount => Items.Count;
    public int TotalStackCount => Items.Sum(i => i.StackSize);
    public decimal TotalStackValue => Items.Sum(i => i.UnitValue * i.StackSize);
    public decimal AcceptableStackValue =>
        Items.Where(i => i.IsAcceptable != false).Sum(i => i.UnitValue * i.StackSize);
}

/// <summary>
/// Cross-references the active character's storage export against NPCs with Store services:
/// for every vendor, lists the items the player currently owns that match that vendor's
/// <c>CapIncreases</c> keyword filters. Rebuilds on storage-report or reference-data change.
/// </summary>
public sealed class StorageSellbackService
{
    private readonly IReferenceDataService _refData;
    private readonly IActiveCharacterService _activeCharSvc;
    private readonly IPlayerProgressionState _progression;
    private readonly IFavorLookupService? _favorLookup;
    private readonly INpcStateTracker? _npcTracker;
    private readonly IDiagnosticsSink? _diag;

    private IReadOnlyList<StorageSellbackVendor> _vendors = [];

    public IReadOnlyList<StorageSellbackVendor> Vendors => _vendors;
    public string? ActiveCharacter => _activeCharSvc.ActiveCharacterName;
    public string? ActiveServer => _activeCharSvc.ActiveServer;
    public string? ActiveReportPath => _activeCharSvc.ActiveStorageReport?.FilePath;

    public event EventHandler? VendorsChanged;

    public StorageSellbackService(
        IReferenceDataService refData,
        IActiveCharacterService activeCharSvc,
        IPlayerProgressionState progression,
        IFavorLookupService? favorLookup = null,
        INpcStateTracker? npcTracker = null,
        IDiagnosticsSink? diag = null)
    {
        _refData = refData;
        _activeCharSvc = activeCharSvc;
        _progression = progression;
        _favorLookup = favorLookup;
        _npcTracker = npcTracker;
        _diag = diag;

        _activeCharSvc.ActiveCharacterChanged += (_, _) => Rebuild();
        _activeCharSvc.StorageReportsChanged += (_, _) => Rebuild();
        _refData.FileUpdated += (_, key) =>
        {
            if (key is "items" or "npcs") Rebuild();
        };
        if (_favorLookup is not null)
            _favorLookup.FavorChanged += (_, _) => Rebuild();
        if (_npcTracker is not null)
            _npcTracker.StateChanged += () => Rebuild();

        Rebuild();
    }

    private void Rebuild()
    {
        var report = _activeCharSvc.ActiveStorageContents;
        if (report is null)
        {
            _vendors = [];
            VendorsChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Build a keyword set per storage item, indexed by TypeId.
        var itemKeywords = new Dictionary<int, (Item Entry, HashSet<string> Keywords)>(report.Items.Count);
        foreach (var stockItem in report.Items)
        {
            if (itemKeywords.ContainsKey(stockItem.TypeID)) continue;
            if (!_refData.Items.TryGetValue(stockItem.TypeID, out var entry)) continue;
            var kws = entry.Keywords is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(entry.Keywords.Select(k => k.Tag), StringComparer.Ordinal);
            itemKeywords[stockItem.TypeID] = (entry, kws);
        }

        // Walk every NPC with a Store service.
        var matches = new List<StorageSellbackVendor>();
        foreach (var (npcKey, npc) in _refData.Npcs)
        {
            var store = npc.Services.FirstOrDefault(s =>
                string.Equals(s.Type, "Store", StringComparison.Ordinal));
            if (store is null) continue;

            var playerTier = _favorLookup?.GetFavorTier(npcKey);
            var buyableItems = new List<StorageSellbackItem>();
            foreach (var stockItem in report.Items)
            {
                if (!itemKeywords.TryGetValue(stockItem.TypeID, out var ctx)) continue;
                if (!VendorAcceptsItem(store, ctx.Keywords)) continue;

                int? maxGold = null;
                bool? acceptable = null;
                if (playerTier is not null)
                {
                    var civicPride = _progression.Skills.TryGetValue("CivicPride", out var cp) ? cp.Level + cp.BonusLevels : 0;
                    maxGold = VendorCapResolver.ResolveMaxGold(
                        store, playerTier.Value, ctx.Keywords, civicPride);
                    acceptable = maxGold is not null && ctx.Entry.Value <= maxGold.Value;
                }

                buyableItems.Add(new StorageSellbackItem(
                    TypeId: stockItem.TypeID,
                    ItemName: ctx.Entry.Name ?? "",
                    IconId: ctx.Entry.IconId,
                    StackSize: stockItem.StackSize,
                    UnitValue: ctx.Entry.Value,
                    Location: StorageReportLoader.NormalizeLocation(stockItem.StorageVault, stockItem.IsInInventory),
                    IsAcceptable: acceptable,
                    EffectiveMaxGold: maxGold));
            }

            if (buyableItems.Count == 0) continue;

            matches.Add(new StorageSellbackVendor(
                NpcKey: npcKey,
                NpcName: npc.Name,
                Area: string.IsNullOrEmpty(npc.Area) ? "(Unknown Area)" : npc.Area,
                MinFavorTier: store.MinFavorTier?.DisplayName(),
                PlayerFavorTier: playerTier?.DisplayName(),
                Items: buyableItems));
        }

        _vendors = matches;
        VendorsChanged?.Invoke(this, EventArgs.Empty);
        _diag?.Info("Smaug.Sellback",
            $"Rebuilt for {ActiveCharacter}: {matches.Count} vendors matched {report.Items.Count} stocked items.");
    }

    /// <summary>
    /// Accepts the item if the vendor's Store has a cap-increase entry whose keyword list is
    /// either empty (accepts anything) or contains any of the item's keywords.
    /// </summary>
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
