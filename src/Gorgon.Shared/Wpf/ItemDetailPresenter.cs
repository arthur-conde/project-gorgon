using System.Windows;
using Gorgon.Shared.Diagnostics;
using Gorgon.Shared.Reference;

namespace Gorgon.Shared.Wpf;

public sealed class ItemDetailPresenter : IItemDetailPresenter
{
    private readonly IReferenceDataService _refData;
    private readonly IDiagnosticsSink? _diag;

    public ItemDetailPresenter(IReferenceDataService refData, IDiagnosticsSink? diag = null)
    {
        _refData = refData;
        _diag = diag;
    }

    public void Show(string internalName) => ShowCore(internalName, augments: null);

    public void Show(string internalName, IReadOnlyList<AugmentPreview> augments) =>
        ShowCore(internalName, augments);

    private void ShowCore(string internalName, IReadOnlyList<AugmentPreview>? augments)
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
            Open(item, augments);
        else
            dispatcher.InvokeAsync(() => Open(item, augments));
    }

    private void Open(ItemEntry item, IReadOnlyList<AugmentPreview>? augments)
    {
        var vm = new ItemDetailViewModel(item, _refData, augments);
        var window = new ItemDetailWindow(vm)
        {
            Owner = Application.Current?.MainWindow,
        };
        window.Show();
    }
}
