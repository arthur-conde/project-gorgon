using CommunityToolkit.Mvvm.ComponentModel;
using Saruman.Domain;

namespace Saruman.ViewModels;

public sealed partial class KnownWordRow : ObservableObject
{
    public KnownWordRow(KnownWord w)
    {
        Code = w.Code;
        Tier = w.Tier;
        FirstDiscoveredAt = w.FirstDiscoveredAt;
        _effectName = w.EffectName;
        _description = w.Description;
        _state = w.State;
        _spentAt = w.SpentAt;
        _discoveryCount = w.DiscoveryCount;
    }

    public string Code { get; }
    public WordOfPowerTier Tier { get; }
    public DateTime FirstDiscoveredAt { get; }

    public string TierLabel => $"Tier {(int)Tier} · {(int)Tier + 1}-syllable";

    [ObservableProperty] private string _effectName;
    [ObservableProperty] private string _description;
    [ObservableProperty] private int _discoveryCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSpent))]
    [NotifyPropertyChangedFor(nameof(IsKnown))]
    private WordOfPowerState _state;

    [ObservableProperty] private DateTime? _spentAt;

    public bool IsSpent => State == WordOfPowerState.Spent;
    public bool IsKnown => State == WordOfPowerState.Known;

    /// <summary>Used to sort Known above Spent within a tier group.</summary>
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
