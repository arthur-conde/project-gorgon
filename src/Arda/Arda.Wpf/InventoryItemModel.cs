using CommunityToolkit.Mvvm.ComponentModel;

namespace Arda.Wpf;

/// <summary>
/// Observable inventory item with INPC support. Property mutations fire
/// <see cref="ObservableObject.PropertyChanged"/> automatically, enabling
/// WPF data triggers (e.g. dim rows where <see cref="IsRemoved"/> is true).
/// </summary>
public partial class InventoryItemModel : ObservableObject
{
    public long InstanceId { get; }

    [ObservableProperty] private string _internalName = "";
    [ObservableProperty] private string? _displayName;
    [ObservableProperty] private int _stackSize;
    [ObservableProperty] private int? _typeId;
    [ObservableProperty] private bool _isRemoved;
    [ObservableProperty] private DateTimeOffset? _removedAt;

    public DateTimeOffset FirstSeenAt { get; init; }
    public DateTimeOffset LastUpdatedAt { get; set; }

    public InventoryItemModel(long instanceId) => InstanceId = instanceId;
}
