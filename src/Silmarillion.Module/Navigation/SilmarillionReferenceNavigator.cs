using Microsoft.Extensions.Logging;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Modules;
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
    private readonly Func<IModuleActivator?>? _activatorFactory;
    private readonly ILogger? _logger;

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
    /// navigator and the kind targets' tab-VM dependencies. <paramref name="activator"/>
    /// is optional — when wired, <see cref="Open"/> brings the Silmarillion tab to
    /// the foreground first so that <c>SilmarillionViewModel</c> (which subscribes
    /// to <see cref="Navigated"/> in its constructor) exists by the time the event
    /// fires. Critical for deep links that arrive before the lazy module has been
    /// activated by a tab click.
    /// </summary>
    public SilmarillionReferenceNavigator(
        Func<IEnumerable<IReferenceKindTarget>> targetsFactory,
        Func<IModuleActivator?>? activatorFactory = null,
        ILogger? logger = null)
    {
        _targetsFactory = targetsFactory;
        _activatorFactory = activatorFactory;
        _logger = logger;
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
        _logger?.LogDiagnosticInfo("Silmarillion.Nav", $"Open kind={reference.Kind} name='{reference.InternalName}'.");

        // Activate the host module BEFORE firing Navigated so SilmarillionViewModel
        // exists and is subscribed by the time the event reaches it. Without this,
        // a deep link that arrives while the user is on a different tab silently
        // updates Current but no UI responds — the navigator's state is correct
        // but invisible.
        //
        // Activator resolution is deferred via Func<> because eagerly resolving
        // IModuleActivator at navigator-construction time would drag ShellViewModel
        // and its entire dependency closure into the DI chain that builds
        // IDeepLinkRouter → SilmarillionDeepLinkHandler → IReferenceNavigator. That
        // is a startup-time chain; ShellViewModel is built later and depending on
        // it from here either cycles or blocks. The Func keeps the navigator
        // construction-time deps minimal; Activate fires on the user-action path
        // where ShellViewModel is already constructed.
        _activatorFactory?.Invoke()?.Activate("silmarillion");

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
