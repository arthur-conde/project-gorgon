using Microsoft.Extensions.Logging;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;
using Silmarillion.ViewModels;
using Silmarillion.Views;

namespace Silmarillion.Navigation;

/// <summary>
/// <see cref="IReferenceKindTarget"/> adapter for the PlayerTitles tab. Resolves
/// selection against the tab VM's bound <see cref="PlayerTitlesTabViewModel.AllTitles"/>
/// collection rather than <see cref="IReferenceDataService"/> directly — same
/// instance-identity concern as the other Silmarillion tabs (cookbook
/// *Pattern walkthrough → ItemsKindTarget*).
/// <para>
/// The <c>internalName</c> argument is the player-title
/// <see cref="PlayerTitleListRow.EnvelopeKey"/> (e.g. <c>"Title_5018"</c>). Unlike
/// Lorebook/Recipe there is no bare-PascalCase form: the PlayerTitle POCO carries
/// no InternalName, so the envelope key <i>is</i> the only identifier and matches
/// the existing <see cref="EntityRef.PlayerTitle(string)"/> factory.
/// </para>
/// </summary>
public sealed class PlayerTitlesKindTarget : IReferenceKindTarget
{
    private readonly PlayerTitlesTabViewModel _vm;
    private readonly ILogger? _logger;

    public PlayerTitlesKindTarget(PlayerTitlesTabViewModel vm, ILogger? logger = null)
    {
        _vm = vm;
        _logger = logger;
    }

    public EntityKind Kind => EntityKind.PlayerTitle;

    public int TabIndex => 8;

    public bool TrySelectByInternalName(string internalName)
    {
        var row = _vm.AllTitles.FirstOrDefault(t => t.EnvelopeKey == internalName);
        if (row is null)
        {
            _logger?.LogDiagnosticInfo("Silmarillion.Nav", $"PlayerTitles.TrySelect '{internalName}' → not found (AllTitles={_vm.AllTitles.Count}).");
            return false;
        }
        _logger?.LogDiagnosticInfo("Silmarillion.Nav", $"PlayerTitles.TrySelect '{internalName}' → found, selecting.");
        // Clear any residual filter so the target row isn't filtered out of the visible list.
        _vm.QueryText = "";
        _vm.SelectedTitle = row;
        return true;
    }

    public bool TryOpenInWindow()
    {
        if (_vm.DetailViewModel is null) return false;
        new PlayerTitleDetailWindow { DataContext = _vm.DetailViewModel }.Show();
        return true;
    }
}
