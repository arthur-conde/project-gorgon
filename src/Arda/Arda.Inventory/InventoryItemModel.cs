using CommunityToolkit.Mvvm.ComponentModel;

namespace Arda.Inventory;

/// <summary>
/// Observable inventory item with INPC support. Property mutations fire
/// <see cref="ObservableObject.PropertyChanged"/> automatically, enabling
/// WPF data triggers (e.g. dim rows where <see cref="IsRemoved"/> is true).
/// </summary>
public partial class InventoryItemModel : ObservableObject
{
    /// <summary>Per-instance unique ID from <c>Player.log ProcessAddItem</c>.</summary>
    public long InstanceId { get; }

    [ObservableProperty] private string _internalName = "";
    [ObservableProperty] private string? _displayName;
    [ObservableProperty] private int _stackSize;
    [ObservableProperty] private int? _typeId;
    [ObservableProperty] private int? _iconId;
    [ObservableProperty] private bool _isRemoved;
    [ObservableProperty] private DateTimeOffset? _removedAt;
    [ObservableProperty] private InventorySource _sources;

    public DateTimeOffset FirstSeenAt { get; init; }
    public DateTimeOffset LastUpdatedAt { get; set; }

    public InventoryItemModel(long instanceId) => InstanceId = instanceId;
}

[Flags]
public enum InventorySource
{
    None = 0,
    PlayerLog = 1,
    ChatLog = 2,
    StorageReport = 4,
}
