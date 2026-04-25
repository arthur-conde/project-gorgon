using System.Windows;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;

namespace Mithril.Shared.Wpf;

public sealed class ItemDetailPresenter : IItemDetailPresenter
{
    private readonly IReferenceDataService _refData;
    private readonly IAugmentPoolPresenter? _poolPresenter;
    private readonly IDiagnosticsSink? _diag;

    public ItemDetailPresenter(IReferenceDataService refData, IAugmentPoolPresenter? poolPresenter = null, IDiagnosticsSink? diag = null)
    {
        _refData = refData;
        _poolPresenter = poolPresenter;
        _diag = diag;
    }

    public void Show(string internalName) => ShowCore(internalName, ItemDetailContext.Empty);

    public void Show(string internalName, IReadOnlyList<AugmentPreview> augments) =>
        ShowCore(internalName, new ItemDetailContext(Augments: augments));

    public void Show(string internalName, ItemDetailContext context) =>
        ShowCore(internalName, context);

    private void ShowCore(string internalName, ItemDetailContext context)
    {
        if (string.IsNullOrWhiteSpace(internalName))
        {
            _diag?.Warn("ItemDetail", "Show called with empty internal name.");
            return;
        }

        if (!_refData.ItemsByInternalName.TryGetValue(internalName, out var item))
        {
            _diag?.Info("ItemDetail", $"Item '{internalName}' not found in reference data; nothing to show.");
            return;
        }

        // Detail is a UI concern: always dispatch onto the UI thread. Callers on the UI
        // thread (double-click handlers, command invocations) hit the fast path; log-driven
        // callers from worker threads get marshalled for free.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            Open(item, context);
        else
            dispatcher.InvokeAsync(() => Open(item, context));
    }

    private void Open(ItemEntry item, ItemDetailContext context)
    {
        var vm = new ItemDetailViewModel(item, _refData, context, _poolPresenter);
        var window = new ItemDetailWindow(vm)
        {
            Owner = Application.Current?.MainWindow,
        };
        window.Show();
    }
}
