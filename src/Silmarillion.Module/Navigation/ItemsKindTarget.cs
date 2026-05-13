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
    private readonly IReferenceDataService _refData;

    public ItemsKindTarget(ItemsTabViewModel vm, IReferenceDataService refData)
    {
        _vm = vm;
        _refData = refData;
    }

    public EntityKind Kind => EntityKind.Item;

    public int TabIndex => 0;

    public bool TrySelectByInternalName(string internalName)
    {
        if (!_refData.ItemsByInternalName.TryGetValue(internalName, out var item))
            return false;
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
