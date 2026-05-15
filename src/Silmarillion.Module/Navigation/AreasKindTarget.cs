using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;
using Silmarillion.ViewModels;
using Silmarillion.Views;

namespace Silmarillion.Navigation;

/// <summary>
/// <see cref="IReferenceKindTarget"/> adapter for the Areas tab. Resolves selection
/// against the tab VM's bound <see cref="AreasTabViewModel.AllAreas"/> collection
/// rather than <see cref="IReferenceDataService"/> directly — same instance-identity
/// concern as the other Silmarillion tabs (cookbook *Pattern walkthrough → ItemsKindTarget*).
/// <para>
/// The <c>internalName</c> argument is the area envelope key (e.g. <c>"AreaSerbule"</c>),
/// which equals <see cref="AreaEntry.Key"/>.
/// </para>
/// </summary>
public sealed class AreasKindTarget : IReferenceKindTarget
{
    private readonly AreasTabViewModel _vm;
    private readonly IDiagnosticsSink? _diag;

    public AreasKindTarget(AreasTabViewModel vm, IDiagnosticsSink? diag = null)
    {
        _vm = vm;
        _diag = diag;
    }

    public EntityKind Kind => EntityKind.Area;

    public int TabIndex => 6;

    public bool TrySelectByInternalName(string internalName)
    {
        var row = _vm.AllAreas.FirstOrDefault(a => a.Key == internalName);
        if (row is null)
        {
            _diag?.Info("Silmarillion.Nav", $"Areas.TrySelect '{internalName}' → not found (AllAreas={_vm.AllAreas.Count}).");
            return false;
        }
        _diag?.Info("Silmarillion.Nav", $"Areas.TrySelect '{internalName}' → found, selecting.");
        // Clear any residual filter so the target row isn't filtered out of the visible ListBox.
        _vm.QueryText = "";
        _vm.SelectedArea = row;
        return true;
    }

    public bool TryOpenInWindow()
    {
        if (_vm.DetailViewModel is null) return false;
        new AreaDetailWindow { DataContext = _vm.DetailViewModel }.Show();
        return true;
    }
}
