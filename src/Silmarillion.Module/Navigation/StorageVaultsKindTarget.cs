using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;
using Silmarillion.ViewModels;
using Silmarillion.Views;

namespace Silmarillion.Navigation;

/// <summary>
/// <see cref="IReferenceKindTarget"/> adapter for the StorageVaults tab. Resolves selection
/// against the tab VM's bound <see cref="StorageVaultsTabViewModel.AllVaults"/> collection
/// rather than <see cref="IReferenceDataService"/> directly — same instance-identity
/// concern as every other Silmarillion tab (cookbook *Pattern walkthrough → ItemsKindTarget*).
/// <para>
/// The <c>internalName</c> argument is the StorageVault <see cref="StorageVaultListRow.EnvelopeKey"/>
/// — the operator NPC internal name (e.g. <c>"NPC_CharlesThompson"</c>) or a <c>"*"</c>-
/// prefixed account-wide form (e.g. <c>"*AccountStorage_Serbule"</c>). This matches the
/// existing <see cref="EntityRef.StorageVault(string)"/> factory and the deep-link route.
/// </para>
/// </summary>
public sealed class StorageVaultsKindTarget : IReferenceKindTarget
{
    private readonly StorageVaultsTabViewModel _vm;
    private readonly IDiagnosticsSink? _diag;

    public StorageVaultsKindTarget(StorageVaultsTabViewModel vm, IDiagnosticsSink? diag = null)
    {
        _vm = vm;
        _diag = diag;
    }

    public EntityKind Kind => EntityKind.StorageVault;

    public int TabIndex => 8;

    public bool TrySelectByInternalName(string internalName)
    {
        var row = _vm.AllVaults.FirstOrDefault(v => v.EnvelopeKey == internalName);
        if (row is null)
        {
            _diag?.Info("Silmarillion.Nav", $"StorageVaults.TrySelect '{internalName}' → not found (AllVaults={_vm.AllVaults.Count}).");
            return false;
        }
        _diag?.Info("Silmarillion.Nav", $"StorageVaults.TrySelect '{internalName}' → found, selecting.");
        // Clear any residual filter so the target row isn't filtered out of the visible list.
        _vm.QueryText = "";
        _vm.SelectedVault = row;
        return true;
    }

    public bool TryOpenInWindow()
    {
        if (_vm.DetailViewModel is null) return false;
        new StorageVaultDetailWindow { DataContext = _vm.DetailViewModel }.Show();
        return true;
    }
}
