using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;

namespace Silmarillion.ViewModels;

/// <summary>
/// Top-level view-model for the Silmarillion reference-data browser. Hosts the
/// per-tab view-models (registered as <see cref="ITabViewModel"/> singletons) and
/// the navigation commands surfaced in the header chrome (Back / Forward /
/// Open-in-window). Subscribes to <see cref="IReferenceNavigator.Navigated"/>
/// to drive automatic tab-switching when an entity of a different kind is opened
/// (e.g. clicking a Recipe cross-link from an Item detail).
///
/// OnNavigated and OpenInWindow dispatch via the <see cref="IReferenceKindTarget"/>
/// registry; per-kind switches were retired in #239. Tabs are composed from
/// <c>IEnumerable&lt;ITabViewModel&gt;</c> ordered by <see cref="ITabViewModel.TabOrder"/>
/// so adding a new tab is a DI registration only — no constructor change rippling
/// into test fixtures (refactor introduced in #243).
/// </summary>
public sealed partial class SilmarillionViewModel : ObservableObject
{
    private readonly IReferenceNavigator _navigator;
    private readonly IReadOnlyDictionary<EntityKind, IReferenceKindTarget> _targets;
    private readonly IDiagnosticsSink? _diag;

    public SilmarillionViewModel(
        IEnumerable<ITabViewModel> tabs,
        IReferenceNavigator navigator,
        IEnumerable<IReferenceKindTarget> targets,
        IDiagnosticsSink? diag = null)
    {
        Tabs = tabs
            .OrderBy(t => t.TabOrder)
            .Select(t => new ModuleTab(t.TabHeader, t))
            .ToArray();
        _navigator = navigator;
        _diag = diag;

        // Same fail-loud-on-duplicate shape as SilmarillionReferenceNavigator —
        // mis-wired DI should crash startup, not silently last-wins.
        var byKind = new Dictionary<EntityKind, IReferenceKindTarget>();
        foreach (var t in targets)
        {
            if (byKind.ContainsKey(t.Kind))
                throw new InvalidOperationException(
                    $"Duplicate IReferenceKindTarget registration for kind '{t.Kind}': " +
                    $"{byKind[t.Kind].GetType().FullName} and {t.GetType().FullName}.");
            byKind[t.Kind] = t;
        }
        _targets = byKind;

        BackCommand = new RelayCommand(() => _navigator.Back(), () => _navigator.CanGoBack);
        ForwardCommand = new RelayCommand(() => _navigator.Forward(), () => _navigator.CanGoForward);

        _navigator.Navigated += OnNavigated;
    }

    /// <summary>The tab descriptors bound to <c>TabControl.ItemsSource</c>.</summary>
    public IReadOnlyList<ModuleTab> Tabs { get; }

    /// <summary>Two-way bound to the TabControl in the view. Index matches <see cref="ITabViewModel.TabOrder"/>.</summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    public IRelayCommand BackCommand { get; }
    public IRelayCommand ForwardCommand { get; }

    [RelayCommand(CanExecute = nameof(CanOpenInWindow))]
    private void OpenInWindow()
    {
        if (_navigator.Current is { } current
            && _targets.TryGetValue(current.Kind, out var target))
        {
            target.TryOpenInWindow();
        }
    }

    private bool CanOpenInWindow() => _navigator.Current is not null;

    private void OnNavigated(object? sender, NavigatedEventArgs e)
    {
        BackCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
        OpenInWindowCommand.NotifyCanExecuteChanged();

        if (e.Current is null)
        {
            _diag?.Info("Silmarillion.Nav", $"OnNavigated kind=null ({e.Kind}) — no-op.");
            return;
        }
        if (!_targets.TryGetValue(e.Current.Kind, out var target))
        {
            _diag?.Info("Silmarillion.Nav", $"OnNavigated kind={e.Current.Kind} name='{e.Current.InternalName}' — no target registered.");
            return;
        }

        _diag?.Info("Silmarillion.Nav", $"OnNavigated kind={e.Current.Kind} name='{e.Current.InternalName}' tabIndex={target.TabIndex}.");
        SelectedTabIndex = target.TabIndex;
        target.TrySelectByInternalName(e.Current.InternalName);
    }
}
