using Microsoft.Extensions.Logging;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;
using Silmarillion.ViewModels;
using Silmarillion.Views;

namespace Silmarillion.Navigation;

/// <summary>
/// <see cref="IReferenceKindTarget"/> adapter for the Quests tab. Resolves selection against
/// the tab VM's bound <see cref="QuestsTabViewModel.AllQuests"/> collection rather than
/// <see cref="IReferenceDataService"/> directly — same instance-identity concern as
/// <see cref="ItemsKindTarget"/>: a background CDN refresh hands out fresh Quest POCOs but
/// the ListBox is still bound to the old collection.
/// </summary>
public sealed class QuestsKindTarget : IReferenceKindTarget
{
    private readonly QuestsTabViewModel _vm;
    private readonly ILogger? _logger;

    public QuestsKindTarget(QuestsTabViewModel vm, ILogger? logger = null)
    {
        _vm = vm;
        _logger = logger;
    }

    public EntityKind Kind => EntityKind.Quest;

    public int TabIndex => 3;

    public bool TrySelectByInternalName(string internalName)
    {
        var row = _vm.AllQuests.FirstOrDefault(r => r.InternalName == internalName);
        if (row is null)
        {
            _logger?.LogDiagnosticInfo("Silmarillion.Nav", $"Quests.TrySelect '{internalName}' → not found (AllQuests={_vm.AllQuests.Count}).");
            return false;
        }
        _logger?.LogDiagnosticInfo("Silmarillion.Nav", $"Quests.TrySelect '{internalName}' → found, selecting.");
        // Clear any residual filter so the target row isn't filtered out of the visible
        // ListBox. See ItemsKindTarget for the symptom.
        _vm.QueryText = "";
        _vm.SelectedRow = row;
        return true;
    }

    public bool TryOpenInWindow()
    {
        if (_vm.DetailViewModel is null) return false;
        new QuestDetailWindow { DataContext = _vm.DetailViewModel }.Show();
        return true;
    }
}
