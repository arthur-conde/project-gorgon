using System.Windows;
using Celebrimbor.ViewModels;
using Celebrimbor.Views;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;

namespace Celebrimbor;

public sealed class CelebrimborAugmentPoolPresenter : IAugmentPoolPresenter
{
    private readonly IReferenceDataService _refData;
    private readonly IDiagnosticsSink? _diag;

    public CelebrimborAugmentPoolPresenter(IReferenceDataService refData, IDiagnosticsSink? diag = null)
    {
        _refData = refData;
        _diag = diag;
    }

    public void Show(string sourceLabel, string profileName, int? minTier = null, int? maxTier = null, string? recommendedSkill = null, int? craftingTargetLevel = null, int? rolledRarityRank = null)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            _diag?.Warn("AugmentPool", "Show called with empty profile name.");
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            Open(sourceLabel, profileName, minTier, maxTier, recommendedSkill, craftingTargetLevel, rolledRarityRank);
        else
            dispatcher.InvokeAsync(() => Open(sourceLabel, profileName, minTier, maxTier, recommendedSkill, craftingTargetLevel, rolledRarityRank));
    }

    private void Open(string sourceLabel, string profileName, int? minTier, int? maxTier, string? recommendedSkill, int? craftingTargetLevel, int? rolledRarityRank)
    {
        var vm = new AugmentPoolViewModel(sourceLabel, profileName, minTier, maxTier, recommendedSkill, craftingTargetLevel, rolledRarityRank, _refData);
        var window = new AugmentPoolView(vm)
        {
            Owner = Application.Current?.MainWindow,
        };
        window.Show();
    }
}
