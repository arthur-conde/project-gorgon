using CommunityToolkit.Mvvm.ComponentModel;
using Saruman.State;

namespace Saruman.ViewModels;

/// <summary>
/// VM row for a single Word-of-Power code. Wraps a
/// <see cref="SarumanCodebook.CodebookEntry"/> and composes the module-internal
/// override flag — UI Spent state is
/// <c>codebook.LastSpentAt != null OR override.IsSpent</c>.
/// </summary>
public sealed partial class KnownWordRow : ObservableObject
{
    public KnownWordRow(SarumanCodebook.CodebookEntry e, bool userOverrideSpent)
    {
        Code = e.Code;
        FirstDiscoveredAt = e.DiscoveredAt.DateTime;
        _effectName = e.Effect;
        _description = e.Description ?? "";
        _viewSpent = e.LastSpentAt is not null;
        _spentAt = e.LastSpentAt?.DateTime;
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

    public void UpdateFrom(SarumanCodebook.CodebookEntry e, bool userOverrideSpent)
    {
        EffectName = e.Effect;
        Description = e.Description ?? "";
        ViewSpent = e.LastSpentAt is not null;
        SpentAt = e.LastSpentAt?.DateTime;
        UserOverrideSpent = userOverrideSpent;
    }
}
