using Mithril.Shared.Reference;

namespace Silmarillion.Navigation;

/// <summary>
/// Live implementation of <see cref="IReferenceNavigator"/>. Maintains unbounded back/forward
/// stacks of <see cref="EntityRef"/>. Registered by <see cref="SilmarillionModule"/> and
/// overrides the shell's <c>NoOpReferenceNavigator</c> via last-singleton-wins DI semantics.
/// </summary>
public sealed class SilmarillionReferenceNavigator : IReferenceNavigator
{
    /// <summary>
    /// EntityKinds for which a v1 tab exists. <see cref="CanOpen"/> drives the
    /// clickable-vs-plain-text rendering of cross-link chips: kinds not in this set
    /// degrade to plain text, and the same chip becomes clickable automatically when
    /// a future tab widens this set.
    /// </summary>
    private static readonly HashSet<EntityKind> V1TabbedKinds = new()
    {
        EntityKind.Item,
        EntityKind.Recipe,
    };

    private readonly Stack<EntityRef> _back = new();
    private readonly Stack<EntityRef> _forward = new();

    public EntityRef? Current { get; private set; }

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    public event EventHandler<NavigatedEventArgs>? Navigated;

    public bool CanOpen(EntityRef reference) => V1TabbedKinds.Contains(reference.Kind);

    public void Open(EntityRef reference)
    {
        var previous = Current;
        if (previous is not null)
        {
            _back.Push(previous);
        }
        _forward.Clear();
        Current = reference;
        Navigated?.Invoke(this, new NavigatedEventArgs(previous, Current, NavigationKind.Open));
    }

    public void Back()
    {
        if (_back.Count == 0) return;
        var previous = Current;
        if (previous is not null)
        {
            _forward.Push(previous);
        }
        Current = _back.Pop();
        Navigated?.Invoke(this, new NavigatedEventArgs(previous, Current, NavigationKind.Back));
    }

    public void Forward()
    {
        if (_forward.Count == 0) return;
        var previous = Current;
        if (previous is not null)
        {
            _back.Push(previous);
        }
        Current = _forward.Pop();
        Navigated?.Invoke(this, new NavigatedEventArgs(previous, Current, NavigationKind.Forward));
    }
}
