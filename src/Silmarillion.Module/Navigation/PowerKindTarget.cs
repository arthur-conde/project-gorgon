using System.Linq;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;
using Silmarillion.ViewModels;
using Silmarillion.Views;

namespace Silmarillion.Navigation;

/// <summary>
/// <see cref="IReferenceKindTarget"/> adapter for <see cref="EntityKind.Power"/> →
/// the unified Treasure tab. Resolves selection against the tab VM's bound
/// <see cref="TreasureTabViewModel.AllRows"/> (not <c>IReferenceDataService</c>) for the
/// same post-refresh instance-identity reason as <c>ItemsKindTarget</c>. Shares
/// <see cref="TabIndex"/> with <see cref="ProfileKindTarget"/> — both kinds dispatch to
/// the one Treasure tab (<see cref="TreasureTabViewModel.TabOrder"/>).
/// </summary>
public sealed class PowerKindTarget : IReferenceKindTarget
{
    private readonly TreasureTabViewModel _vm;
    private readonly IDiagnosticsSink? _diag;

    public PowerKindTarget(TreasureTabViewModel vm, IDiagnosticsSink? diag = null)
    {
        _vm = vm;
        _diag = diag;
    }

    public EntityKind Kind => EntityKind.Power;

    public int TabIndex => 10;

    public bool TrySelectByInternalName(string internalName)
    {
        var row = _vm.AllRows.FirstOrDefault(
            r => r.Kind == TreasureRowKind.Power && r.InternalName == internalName);
        if (row is null)
        {
            _diag?.Info("Silmarillion.Nav", $"Power.TrySelect '{internalName}' → not found (AllRows={_vm.AllRows.Count}).");
            return false;
        }
        _diag?.Info("Silmarillion.Nav", $"Power.TrySelect '{internalName}' → found, selecting.");
        _vm.QueryText = "";
        _vm.SelectedRow = row;
        return true;
    }

    public bool TryOpenInWindow()
    {
        if (_vm.DetailViewModel is null) return false;
        new TreasureDetailWindow { DataContext = _vm.DetailViewModel }.Show();
        return true;
    }
}
