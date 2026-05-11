using CommunityToolkit.Mvvm.ComponentModel;

namespace Mithril.Shared.Wpf.Filtering;

/// <summary>
/// One toggleable predicate exposed to the shared filter popup.
/// </summary>
/// <remarks>
/// Predicates are always written positively (they describe "what passes"). When <see cref="Inverted"/>
/// is <c>true</c> the predicate restricts the set in the *off* state, which lets a label like
/// "Show unknown" read naturally — toggling on suppresses the predicate and reveals more rows.
/// Consumers should combine via <c>filters.Where(f =&gt; f.ShouldApply).All(f =&gt; f.Predicate(item))</c>.
/// </remarks>
public sealed partial class FilterPredicate<T> : ObservableObject
{
    public FilterPredicate(string id, string displayName, Func<T, bool> predicate, bool inverted = false, bool isActive = false)
    {
        Id = id;
        DisplayName = displayName;
        Predicate = predicate;
        Inverted = inverted;
        _isActive = isActive;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public Func<T, bool> Predicate { get; }
    public bool Inverted { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShouldApply))]
    private bool _isActive;

    public bool ShouldApply => Inverted ? !IsActive : IsActive;
}
