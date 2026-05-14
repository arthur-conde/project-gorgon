using System.Windows.Input;
using Mithril.Shared.Reference;
using PocoEffect = Mithril.Reference.Models.Effects.Effect;

namespace Silmarillion.ViewModels;

/// <summary>
/// Read-only projection of an <see cref="PocoEffect"/> for the Silmarillion Effects tab
/// detail pane. Hostable in both the master-detail right pane and the popup
/// <see cref="Silmarillion.Views.EffectDetailWindow"/>.
/// <para>
/// Slice (c) ships the skeleton: header (icon + name + envelope-key footer) and basic
/// metadata pass-through. The richer projected sections — keyword chips, conditional-rule
/// sub-tables (from #288/#296), stacks-with cluster, required-by-abilities cluster with
/// the overflow pill, procs-from-abilities, and SpewText footer — land in slice (d).
/// </para>
/// </summary>
public sealed class EffectDetailViewModel
{
    public EffectDetailViewModel(
        PocoEffect effect,
        string envelopeKey,
        IReferenceDataService refData,
        IReferenceNavigator navigator,
        IEntityNameResolver nameResolver,
        ICommand? openEntityCommand = null)
    {
        Effect = effect;
        EnvelopeKey = envelopeKey;
        DisplayName = string.IsNullOrEmpty(effect.Name) ? envelopeKey : effect.Name!;
        OpenEntityCommand = openEntityCommand;
        _ = refData;
        _ = navigator;
        _ = nameResolver;
    }

    public PocoEffect Effect { get; }
    public string EnvelopeKey { get; }
    public string DisplayName { get; }
    public int IconId => Effect.IconId;
    public string? Description => Effect.Desc;

    public ICommand? OpenEntityCommand { get; }
}
