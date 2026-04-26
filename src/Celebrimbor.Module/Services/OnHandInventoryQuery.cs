using Celebrimbor.Domain;
using Mithril.Shared.Character;
using Mithril.Shared.Reference;
using Mithril.Shared.Storage;

namespace Celebrimbor.Services;

/// <summary>
/// Projects the active character's latest storage export into
/// per-item-internal-name counts and location chips for the shopping list.
/// Single-character v1; multi-character aggregation is a roadmap item.
/// </summary>
public sealed class OnHandInventoryQuery
{
    private readonly IActiveCharacterService _activeChar;
    private readonly IReferenceDataService _refData;

    public OnHandInventoryQuery(IActiveCharacterService activeChar, IReferenceDataService refData)
    {
        _activeChar = activeChar;
        _refData = refData;
    }

    public OnHandInventory QueryActiveCharacter()
    {
        var report = _activeChar.ActiveStorageContents;
        if (report is null) return OnHandInventory.Empty;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var locations = new Dictionary<string, List<IngredientLocation>>(StringComparer.Ordinal);
        // Per-keyword set of owned InternalNames so the aggregator can resolve
        // keyword-matched ingredient slots in O(1) for the common single-key case.
        var ownedByKeyword = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var item in report.Items)
        {
            if (!_refData.Items.TryGetValue(item.TypeID, out var itemEntry)) continue;
            var key = itemEntry.InternalName;

            counts[key] = counts.TryGetValue(key, out var existing) ? existing + item.StackSize : item.StackSize;

            var label = StorageReportLoader.NormalizeLocation(item.StorageVault, item.IsInInventory);
            if (!locations.TryGetValue(key, out var list))
            {
                list = [];
                locations[key] = list;
            }

            var existingLoc = list.FindIndex(l => l.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
            if (existingLoc >= 0)
                list[existingLoc] = list[existingLoc] with { Quantity = list[existingLoc].Quantity + item.StackSize };
            else
                list.Add(new IngredientLocation(label, item.StackSize, key, itemEntry.Name, itemEntry.IconId));

            foreach (var kw in itemEntry.Keywords)
            {
                if (!ownedByKeyword.TryGetValue(kw.Tag, out var set))
                    ownedByKeyword[kw.Tag] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(key);
            }
        }

        var frozenLocations = locations.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<IngredientLocation>)kv.Value.OrderByDescending(l => l.Quantity).ToList(),
            StringComparer.Ordinal);

        var frozenOwnedByKeyword = ownedByKeyword.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<string>)[.. kv.Value],
            StringComparer.Ordinal);

        return new OnHandInventory(counts, frozenLocations, frozenOwnedByKeyword);
    }
}

public sealed record OnHandInventory(
    IReadOnlyDictionary<string, int> Counts,
    IReadOnlyDictionary<string, IReadOnlyList<IngredientLocation>> Locations,
    IReadOnlyDictionary<string, IReadOnlyList<string>> OwnedInternalNamesByKeyword)
{
    public static readonly OnHandInventory Empty = new(
        new Dictionary<string, int>(StringComparer.Ordinal),
        new Dictionary<string, IReadOnlyList<IngredientLocation>>(StringComparer.Ordinal),
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
}
