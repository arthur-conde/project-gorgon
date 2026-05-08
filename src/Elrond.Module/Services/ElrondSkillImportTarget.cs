using System.Windows;
using Elrond.ViewModels;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Modules;

namespace Elrond.Services;

/// <summary>
/// Elrond's implementation of <see cref="IElrondSkillImportTarget"/>. Brings the
/// Elrond tab to the foreground via <see cref="IModuleActivator"/>, then forwards
/// the skill key into <see cref="SkillAdvisorViewModel.SelectSkillFromDeepLink"/>
/// — that method handles the not-yet-ready cases (no active character, reference
/// data still loading) via its internal pending-deep-link stash, so this target
/// stays a thin adapter.
/// </summary>
public sealed class ElrondSkillImportTarget : IElrondSkillImportTarget
{
    private readonly SkillAdvisorViewModel _vm;
    private readonly IModuleActivator? _activator;
    private readonly IDiagnosticsSink? _diag;

    public ElrondSkillImportTarget(
        SkillAdvisorViewModel viewModel,
        IModuleActivator? activator = null,
        IDiagnosticsSink? diag = null)
    {
        _vm = viewModel;
        _activator = activator;
        _diag = diag;
    }

    public void ImportFromLinkPayload(string skillKey)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            Apply(skillKey);
        else
            dispatcher.InvokeAsync(() => Apply(skillKey));
    }

    private void Apply(string skillKey)
    {
        if (_activator is not null && !_activator.Activate("elrond"))
            _diag?.Info("Elrond", "Deep-link import: module activator could not find 'elrond'.");
        _vm.SelectSkillFromDeepLink(skillKey);
    }
}
