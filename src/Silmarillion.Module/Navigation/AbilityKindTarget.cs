using Microsoft.Extensions.Logging;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;
using Silmarillion.ViewModels;
using Silmarillion.Views;

namespace Silmarillion.Navigation;

/// <summary>
/// <see cref="IReferenceKindTarget"/> adapter for the Abilities tab. Resolves selection against
/// the tab VM's bound <see cref="AbilitiesTabViewModel.AllAbilities"/> collection rather than
/// <see cref="IReferenceDataService"/> directly — same instance-identity concern as
/// <see cref="ItemsKindTarget"/>: a background CDN refresh hands out fresh Ability POCOs but the
/// ListBox is still bound to the old collection.
/// </summary>
public sealed class AbilityKindTarget : IReferenceKindTarget
{
    private readonly AbilitiesTabViewModel _vm;
    private readonly ILogger? _logger;

    public AbilityKindTarget(AbilitiesTabViewModel vm, ILogger? logger = null)
    {
        _vm = vm;
        _logger = logger;
    }

    public EntityKind Kind => EntityKind.Ability;

    public int TabIndex => 4;

    public bool TrySelectByInternalName(string internalName)
    {
        var row = _vm.AllAbilities.FirstOrDefault(r => r.InternalName == internalName);
        if (row is null)
        {
            _logger?.LogDiagnosticInfo("Silmarillion.Nav", $"Abilities.TrySelect '{internalName}' → not found (AllAbilities={_vm.AllAbilities.Count}).");
            return false;
        }
        _logger?.LogDiagnosticInfo("Silmarillion.Nav", $"Abilities.TrySelect '{internalName}' → found, selecting.");
        // Clear any residual filter so the target row isn't filtered out of the visible ListBox.
        _vm.QueryText = "";
        _vm.SelectedRow = row;
        return true;
    }

    public bool TryOpenInWindow()
    {
        if (_vm.DetailViewModel is null) return false;
        new AbilityDetailWindow { DataContext = _vm.DetailViewModel }.Show();
        return true;
    }
}
