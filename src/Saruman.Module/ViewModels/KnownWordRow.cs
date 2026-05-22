using CommunityToolkit.Mvvm.ComponentModel;
using Mithril.GameState.WordsOfPower;

namespace Saruman.ViewModels;

/// <summary>
/// VM row for a single Word-of-Power code (#603 — post-codebook-split). Wraps
/// a <see cref="WordOfPowerEntry"/> from the cross-source view and composes
/// the Saruman module-internal override flag — UI Spent state is
/// <c>view.IsSpent OR override.IsSpent</c>.
/// </summary>
public sealed partial class KnownWordRow : ObservableObject
{
    public KnownWordRow(WordOfPowerEntry e, bool userOverrideSpent)
    {
        Code = e.Code;
        FirstDiscoveredAt = e.DiscoveredAt;
        _effectName = e.EffectName;
        _description = e.Description;
        _viewSpent = e.State == WordOfPowerKnowledge.Spent;
        _spentAt = e.LastSpentAt;
        _userOverrideSpent = userOverrideSpent;
    }

    public string Code { get; }
    public DateTime FirstDiscoveredAt { get; }

    [ObservableProperty] private string _effectName;
    [ObservableProperty] private string _description;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSpent))]
    [NotifyPropertyChangedFor(nameof(IsKnown))]
    [NotifyPropertyChangedFor(nameof(StateOrder))]
    private bool _viewSpent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSpent))]
    [NotifyPropertyChangedFor(nameof(IsKnown))]
    [NotifyPropertyChangedFor(nameof(StateOrder))]
    private bool _userOverrideSpent;

    [ObservableProperty] private DateTime? _spentAt;

    /// <summary>
    /// Composed Spent state: either the view observed a chat burn for this
    /// code, or the user manually marked it Spent.
    /// </summary>
    public bool IsSpent => ViewSpent || UserOverrideSpent;
    public bool IsKnown => !IsSpent;

    /// <summary>Sorts Known above Spent within an effect group.</summary>
    public int StateOrder => IsKnown ? 0 : 1;

    public void UpdateFrom(WordOfPowerEntry e, bool userOverrideSpent)
    {
        EffectName = e.EffectName;
        Description = e.Description;
        ViewSpent = e.State == WordOfPowerKnowledge.Spent;
        SpentAt = e.LastSpentAt;
        UserOverrideSpent = userOverrideSpent;
    }
}
