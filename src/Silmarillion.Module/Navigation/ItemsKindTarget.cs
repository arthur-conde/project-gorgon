using Microsoft.Extensions.Logging;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;
using Silmarillion.ViewModels;
using Silmarillion.Views;

namespace Silmarillion.Navigation;

/// <summary>
/// <see cref="IReferenceKindTarget"/> adapter for the Items tab. Lives next to
/// the navigator so the registry is owned by the same module that hosts the
/// tabs. Wires the navigator's "select this entity" call into the tab VM's
/// existing master-detail selection, and the "open in window" call into the
/// existing <see cref="ItemDetailWindow"/> popup.
/// </summary>
public sealed class ItemsKindTarget : IReferenceKindTarget
{
    private readonly ItemsTabViewModel _vm;
    private readonly ILogger? _logger;

    public ItemsKindTarget(ItemsTabViewModel vm, ILogger? logger = null)
    {
        _vm = vm;
        _logger = logger;
    }

    public EntityKind Kind => EntityKind.Item;

    public int TabIndex => 0;

    public bool TrySelectByInternalName(string internalName)
    {
        // Resolve against the tab VM's AllItems (the bound ListBox ItemsSource),
        // not against refData directly. If a background reference-data refresh swaps
        // the underlying dictionary, refData hands us a new Item instance while
        // AllItems still holds the old reference — WPF's ListBox can't find a match
        // by Equals and silently clears the selection, leaving the detail empty.
        // Looking up in AllItems guarantees we pick the canonical instance the
        // ListBox already knows.
        var item = _vm.AllItems.FirstOrDefault(i => i.InternalName == internalName);
        if (item is null)
        {
            _logger?.LogDiagnosticInfo("Silmarillion.Nav", $"Items.TrySelect '{internalName}' → not found (AllItems={_vm.AllItems.Count}).");
            return false;
        }
        _logger?.LogDiagnosticInfo("Silmarillion.Nav", $"Items.TrySelect '{internalName}' → found, selecting.");
        // Clear any residual filter so the target item isn't filtered out of the visible
        // ListBox — without this, a deep-link arriving while the user (or a previous
        // navigation) had a query in the box would set SelectedItem to a row that's
        // hidden by the filter, and the selection wouldn't be visible.
        _vm.QueryText = "";
        _vm.SelectedItem = item;
        return true;
    }

    public bool TryOpenInWindow()
    {
        if (_vm.DetailViewModel is null) return false;
        new ItemDetailWindow(_vm.DetailViewModel).Show();
        return true;
    }
}
