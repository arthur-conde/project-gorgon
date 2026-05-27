using Microsoft.Extensions.Logging;
using Mithril.Shared.Reference;
using Silmarillion.ViewModels;
using Silmarillion.Views;

namespace Silmarillion.Navigation;

/// <summary>
/// <see cref="IReferenceKindTarget"/> adapter for the Effects tab. Resolves selection
/// against the tab VM's bound <see cref="EffectsTabViewModel.AllEffects"/> collection
/// rather than <see cref="IReferenceDataService"/> directly — same instance-identity
/// concern as <see cref="ItemsKindTarget"/>: a background CDN refresh hands out fresh
/// Effect POCOs but the ListBox is still bound to the old collection.
/// <para>
/// The <c>internalName</c> argument is the lifted envelope key (e.g. <c>"effect_10003"</c>),
/// which equals <c>Effect.InternalName</c> after the deserializer lift.
/// </para>
/// </summary>
public sealed class EffectsKindTarget : IReferenceKindTarget
{
    private readonly EffectsTabViewModel _vm;
    private readonly ILogger? _logger;

    public EffectsKindTarget(EffectsTabViewModel vm, ILogger? logger = null)
    {
        _vm = vm;
        _logger = logger;
    }

    public EntityKind Kind => EntityKind.Effect;

    public int TabIndex => 5;

    public bool TrySelectByInternalName(string internalName)
    {
        var row = _vm.AllEffects.FirstOrDefault(r => r.EnvelopeKey == internalName);
        if (row is null)
        {
            _logger?.LogTrace("Effects.TrySelect '{InternalName}' → not found (AllEffects={AllEffects}).", internalName, _vm.AllEffects.Count);
            return false;
        }
        _logger?.LogTrace("Effects.TrySelect '{InternalName}' → found, selecting.", internalName);
        // Clear any residual filter so the target row isn't filtered out of the visible ListBox.
        _vm.QueryText = "";
        _vm.SelectedRow = row;
        return true;
    }

    public bool TryOpenInWindow()
    {
        if (_vm.DetailViewModel is null) return false;
        new EffectDetailWindow { DataContext = _vm.DetailViewModel }.Show();
        return true;
    }
}
