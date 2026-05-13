using Mithril.Shared.Reference;

namespace Silmarillion.Navigation;

/// <summary>
/// Live implementation of <see cref="IReferenceNavigator"/>. Maintains unbounded
/// back/forward stacks of <see cref="EntityRef"/>. Registered by
/// <see cref="SilmarillionModule"/> and overrides the shell's
/// <c>NoOpReferenceNavigator</c> via last-singleton-wins DI semantics.
///
/// <see cref="CanOpen"/> is registry-driven: a kind is navigable iff an
/// <see cref="IReferenceKindTarget"/> has been registered for it. As Bucket-B
/// tabs ship (NPCs → Quests → …), each one adds a target and chip
/// clickability for that kind flips on automatically.
///
/// Two constructors exist: an eager one (validates at construction; used by tests
/// that build a navigator from a fixed array) and a lazy one (validates on first
/// <see cref="CanOpen"/> call; used by DI to break the construction cycle, since
/// kind targets depend on tab view-models and tab view-models depend on this
/// navigator). The lazy path's factory closure isn't invoked until first
/// <see cref="CanOpen"/>, by which time this navigator is already cached in the
/// container.
/// </summary>
public sealed class SilmarillionReferenceNavigator : IReferenceNavigator
{
    private readonly Func<IEnumerable<IReferenceKindTarget>>? _targetsFactory;
    private IReadOnlyDictionary<EntityKind, IReferenceKindTarget>? _targets;

    private readonly Stack<EntityRef> _back = new();
    private readonly Stack<EntityRef> _forward = new();

    /// <summary>Eager construction: validates immediately. Used in tests.</summary>
    public SilmarillionReferenceNavigator(IEnumerable<IReferenceKindTarget> targets)
    {
        _targets = BuildRegistry(targets);
    }

    /// <summary>
    /// Lazy construction: defers validation and target enumeration to the first
    /// <see cref="CanOpen"/> call. Used by DI to break the cycle between this
    /// navigator and the kind targets' tab-VM dependencies.
    /// </summary>
    public SilmarillionReferenceNavigator(Func<IEnumerable<IReferenceKindTarget>> targetsFactory)
    {
        _targetsFactory = targetsFactory;
    }

    private IReadOnlyDictionary<EntityKind, IReferenceKindTarget> Targets =>
        _targets ??= BuildRegistry(_targetsFactory!());

    private static IReadOnlyDictionary<EntityKind, IReferenceKindTarget> BuildRegistry(
        IEnumerable<IReferenceKindTarget> targets)
    {
        // Fail-loud on duplicate Kind registrations — same shape as DeepLinkRouter.
        var byKind = new Dictionary<EntityKind, IReferenceKindTarget>();
        foreach (var t in targets)
        {
            if (byKind.ContainsKey(t.Kind))
                throw new InvalidOperationException(
                    $"Duplicate IReferenceKindTarget registration for kind '{t.Kind}': " +
                    $"{byKind[t.Kind].GetType().FullName} and {t.GetType().FullName}.");
            byKind[t.Kind] = t;
        }
        return byKind;
    }

    public EntityRef? Current { get; private set; }
    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;
    public event EventHandler<NavigatedEventArgs>? Navigated;

    public bool CanOpen(EntityRef reference) => Targets.ContainsKey(reference.Kind);

    public void Open(EntityRef reference)
    {
        var previous = Current;
        if (previous is not null) _back.Push(previous);
        _forward.Clear();
        Current = reference;
        Navigated?.Invoke(this, new NavigatedEventArgs(previous, Current, NavigationKind.Open));
    }

    public void Back()
    {
        if (_back.Count == 0) return;
        var previous = Current;
        if (previous is not null) _forward.Push(previous);
        Current = _back.Pop();
        Navigated?.Invoke(this, new NavigatedEventArgs(previous, Current, NavigationKind.Back));
    }

    public void Forward()
    {
        if (_forward.Count == 0) return;
        var previous = Current;
        if (previous is not null) _back.Push(previous);
        Current = _forward.Pop();
        Navigated?.Invoke(this, new NavigatedEventArgs(previous, Current, NavigationKind.Forward));
    }
}
