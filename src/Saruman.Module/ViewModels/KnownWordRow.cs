using CommunityToolkit.Mvvm.ComponentModel;
using Saruman.Domain;

namespace Saruman.ViewModels;

public sealed partial class KnownWordRow : ObservableObject
{
    public KnownWordRow(KnownWord w)
    {
        Code = w.Code;
        FirstDiscoveredAt = w.FirstDiscoveredAt;
        _effectName = w.EffectName;
        _description = w.Description;
        _state = w.State;
        _spentAt = w.SpentAt;
        _discoveryCount = w.DiscoveryCount;
    }

    public string Code { get; }
    public DateTime FirstDiscoveredAt { get; }

    [ObservableProperty] private string _effectName;
    [ObservableProperty] private string _description;
    [ObservableProperty] private int _discoveryCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSpent))]
    [NotifyPropertyChangedFor(nameof(IsKnown))]
    [NotifyPropertyChangedFor(nameof(StateOrder))]
    private WordOfPowerState _state;

    [ObservableProperty] private DateTime? _spentAt;

    public bool IsSpent => State == WordOfPowerState.Spent;
    public bool IsKnown => State == WordOfPowerState.Known;

    /// <summary>Sorts Known above Spent within an effect group.</summary>
    public int StateOrder => IsKnown ? 0 : 1;

    public void UpdateFrom(KnownWord w)
    {
        EffectName = w.EffectName;
        Description = w.Description;
        DiscoveryCount = w.DiscoveryCount;
        State = w.State;
        SpentAt = w.SpentAt;
    }
}
