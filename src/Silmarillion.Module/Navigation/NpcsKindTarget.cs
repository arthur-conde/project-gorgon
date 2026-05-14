using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;
using Silmarillion.ViewModels;
using Silmarillion.Views;

namespace Silmarillion.Navigation;

/// <summary>
/// <see cref="IReferenceKindTarget"/> adapter for the NPCs tab.
/// See <see cref="ItemsKindTarget"/> for the rationale on resolving against
/// the tab VM's bound collection instead of refData directly.
/// </summary>
public sealed class NpcsKindTarget : IReferenceKindTarget
{
    private readonly NpcsTabViewModel _vm;
    private readonly IDiagnosticsSink? _diag;

    public NpcsKindTarget(NpcsTabViewModel vm, IDiagnosticsSink? diag = null)
    {
        _vm = vm;
        _diag = diag;
    }

    public EntityKind Kind => EntityKind.Npc;

    public int TabIndex => 2;

    public bool TrySelectByInternalName(string internalName)
    {
        // Resolve against AllNpcs (canonical, matches WPF's ListBox ItemsSource), not against
        // refData. See ItemsKindTarget for the post-refresh divergence that motivates this.
        var row = _vm.AllNpcs.FirstOrDefault(r => r.InternalName == internalName);
        if (row is null)
        {
            _diag?.Info("Silmarillion.Nav", $"Npcs.TrySelect '{internalName}' → not found (AllNpcs={_vm.AllNpcs.Count}).");
            return false;
        }
        _diag?.Info("Silmarillion.Nav", $"Npcs.TrySelect '{internalName}' → found, selecting.");
        // Clear any residual filter so the target row isn't filtered out of the visible
        // ListBox. See ItemsKindTarget for the symptom.
        _vm.QueryText = "";
        _vm.SelectedRow = row;
        return true;
    }

    public bool TryOpenInWindow()
    {
        if (_vm.DetailViewModel is null) return false;
        new NpcDetailWindow { DataContext = _vm.DetailViewModel }.Show();
        return true;
    }
}
