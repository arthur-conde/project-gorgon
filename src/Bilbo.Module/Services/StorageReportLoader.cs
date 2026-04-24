global using Gorgon.Shared.Storage;

using Bilbo.Domain;
using Gorgon.Shared.Reference;

namespace Bilbo.Services;

/// <summary>Bilbo-specific projection helpers that depend on StorageItemRow.</summary>
public static class StorageRowMapper
{
    public static IReadOnlyList<StorageItemRow> ToRows(StorageReport report, IReferenceDataService refData)
    {
        var rows = new List<StorageItemRow>(report.Items.Count);
        foreach (var item in report.Items)
        {
            var location = StorageReportLoader.NormalizeLocation(item.StorageVault, item.IsInInventory);
            var hasRef = refData.Items.TryGetValue(item.TypeID, out var entry);
            var iconId = hasRef ? entry!.IconId : 0;
            var internalName = hasRef ? entry!.InternalName : "";
            rows.Add(new StorageItemRow(
                item.Name,
                location,
                item.StackSize,
                item.Value,
                item.Value * item.StackSize,
                item.Rarity,
                item.Slot,
                item.Level,
                item.TSysPowers?.Count ?? 0,
                item.AttunedTo,
                item.IsCrafted,
                item.TypeID,
                iconId,
                internalName));
        }
        return rows;
    }
}
