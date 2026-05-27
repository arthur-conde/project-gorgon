using Microsoft.Extensions.Logging;
using System.Linq;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;
using Silmarillion.ViewModels;
using Silmarillion.Views;

namespace Silmarillion.Navigation;

/// <summary>
/// <see cref="IReferenceKindTarget"/> adapter for <see cref="EntityKind.Profile"/> →
/// the unified Treasure tab. Also the #214 "pool-query" deep-link landing point: a
/// Profile selection opens the pool's <see cref="ProfileDetailViewModel"/> (the
/// filterable-by-<c>Power.Skill</c> pool list). Shares <see cref="TabIndex"/> with
/// <see cref="PowerKindTarget"/>.
/// </summary>
public sealed class ProfileKindTarget : IReferenceKindTarget
{
    private readonly TreasureTabViewModel _vm;
    private readonly ILogger? _logger;

    public ProfileKindTarget(TreasureTabViewModel vm, ILogger? logger = null)
    {
        _vm = vm;
        _logger = logger;
    }

    public EntityKind Kind => EntityKind.Profile;

    public int TabIndex => 10;

    public bool TrySelectByInternalName(string internalName)
    {
        var row = _vm.AllRows.FirstOrDefault(
            r => r.Kind == TreasureRowKind.Profile && r.InternalName == internalName);
        if (row is null)
        {
            _logger?.LogDiagnosticInfo("Silmarillion.Nav", $"Profile.TrySelect '{internalName}' → not found (AllRows={_vm.AllRows.Count}).");
            return false;
        }
        _logger?.LogDiagnosticInfo("Silmarillion.Nav", $"Profile.TrySelect '{internalName}' → found, selecting.");
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
