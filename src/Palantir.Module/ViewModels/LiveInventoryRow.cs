using CommunityToolkit.Mvvm.ComponentModel;

namespace Palantir.ViewModels;

public sealed partial class LiveInventoryRow : ObservableObject
{
    public required long InstanceId { get; init; }
    public required string InternalName { get; init; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int _iconId;
    [ObservableProperty] private int _stackSize;
    [ObservableProperty] private bool _isDeleted;
    [ObservableProperty] private DateTime _lastUpdated;
}
