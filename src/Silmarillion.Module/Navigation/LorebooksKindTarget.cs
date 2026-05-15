using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;
using Silmarillion.ViewModels;
using Silmarillion.Views;

namespace Silmarillion.Navigation;

/// <summary>
/// <see cref="IReferenceKindTarget"/> adapter for the Lorebooks tab. Resolves selection
/// against the tab VM's bound <see cref="LorebooksTabViewModel.AllLorebooks"/> collection
/// rather than <see cref="IReferenceDataService"/> directly — same instance-identity
/// concern as the other Silmarillion tabs (cookbook *Pattern walkthrough → ItemsKindTarget*).
/// <para>
/// The <c>internalName</c> argument is the lorebook <see cref="LorebookListRow.InternalName"/>
/// (e.g. <c>"TheWastedWishes"</c>) — the bare PascalCase form, NOT the <c>"Book_N"</c>
/// envelope key (matches the existing <see cref="EntityRef.Lorebook(string)"/> factory and
/// every existing call site, e.g. <c>QuestDetailProjector</c>'s lorebook reward chip).
/// </para>
/// </summary>
public sealed class LorebooksKindTarget : IReferenceKindTarget
{
    private readonly LorebooksTabViewModel _vm;
    private readonly IDiagnosticsSink? _diag;

    public LorebooksKindTarget(LorebooksTabViewModel vm, IDiagnosticsSink? diag = null)
    {
        _vm = vm;
        _diag = diag;
    }

    public EntityKind Kind => EntityKind.Lorebook;

    public int TabIndex => 7;

    public bool TrySelectByInternalName(string internalName)
    {
        var row = _vm.AllLorebooks.FirstOrDefault(b => b.InternalName == internalName);
        if (row is null)
        {
            _diag?.Info("Silmarillion.Nav", $"Lorebooks.TrySelect '{internalName}' → not found (AllLorebooks={_vm.AllLorebooks.Count}).");
            return false;
        }
        _diag?.Info("Silmarillion.Nav", $"Lorebooks.TrySelect '{internalName}' → found, selecting.");
        // Clear any residual filter so the target row isn't filtered out of the visible list.
        _vm.QueryText = "";
        _vm.SelectedLorebook = row;
        return true;
    }

    public bool TryOpenInWindow()
    {
        if (_vm.DetailViewModel is null) return false;
        new LorebookDetailWindow { DataContext = _vm.DetailViewModel }.Show();
        return true;
    }
}
