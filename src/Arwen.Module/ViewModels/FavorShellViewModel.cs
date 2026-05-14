using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Mithril.Shared.Wpf;

namespace Arwen.ViewModels;

/// <summary>
/// Composes Arwen's tabs as VM data for <c>TabControl.ItemsSource</c> binding,
/// and serves as the cross-tab navigator (<see cref="IFavorViewNavigator"/>)
/// so child VMs can ask the shell to switch tabs and hand off context.
/// <para>
/// Calibration and "Edit Observations" share <see cref="CalibrationViewModel"/>
/// state but render through different views; the latter is wrapped in
/// <see cref="ObservationsEditorViewModel"/> so DataTemplate-by-type can
/// distinguish them.
/// </para>
/// <para>
/// The "Edit Observations" tab's badge count mirrors
/// <see cref="CalibrationViewModel.PendingCount"/>; updates flow via a
/// <see cref="INotifyPropertyChanged"/> subscription wired in the constructor.
/// </para>
/// </summary>
public sealed partial class FavorShellViewModel : ObservableObject, IFavorViewNavigator
{
    private readonly GiftScannerViewModel _giftScanner;

    [ObservableProperty]
    private object? selectedTab;

    public IReadOnlyList<ModuleTab> Tabs { get; }

    public FavorShellViewModel(
        FavorDashboardViewModel dashboard,
        FavorCalculatorViewModel calculator,
        GiftScannerViewModel giftScanner,
        ItemLookupViewModel itemLookup,
        StorageGiftsViewModel storageGifts,
        CalibrationViewModel calibration)
    {
        _giftScanner = giftScanner;

        var editor = new ObservationsEditorViewModel(calibration);
        var editorTab = new ModuleTab("Edit Observations", editor, badgeCount: calibration.PendingCount);

        // Pending observations live on CalibrationViewModel; keep the badge
        // count on the editor tab in sync with it.
        calibration.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CalibrationViewModel.PendingCount))
                editorTab.BadgeCount = calibration.PendingCount;
        };

        Tabs = new[]
        {
            new ModuleTab("NPC Dashboard",     dashboard),
            new ModuleTab("Favor Calculator",  calculator),
            new ModuleTab("Gift Scanner",      giftScanner),
            new ModuleTab("Item Lookup",       itemLookup),
            new ModuleTab("Storage Gifts",     storageGifts),
            new ModuleTab("Calibration",       calibration),
            editorTab,
        };

        selectedTab = Tabs[0];
    }

    /// <inheritdoc/>
    public void OpenInGiftScanner(string npcKey)
    {
        var tab = Tabs.FirstOrDefault(t => t.Content is GiftScannerViewModel);
        if (tab is null) return;

        SelectedTab = tab;

        var entry = _giftScanner.NpcList.FirstOrDefault(e => e.NpcKey == npcKey);
        if (entry is not null) _giftScanner.SelectedNpc = entry;
    }
}
