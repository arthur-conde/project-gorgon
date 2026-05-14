using CommunityToolkit.Mvvm.ComponentModel;
using Mithril.Shared.Wpf;

namespace Gandalf.ViewModels;

/// <summary>
/// Composes Gandalf's tabs as VM data so the View can bind
/// <c>TabControl.ItemsSource</c> rather than hard-coding TabItems in XAML.
/// Tab views are picked by <c>DataTemplate</c>s keyed on each child VM's type.
/// <para>
/// <see cref="SelectedTabIndex"/> is two-way bound by the View; the dashboard's
/// <c>NavigationRequested</c> event drives it via <see cref="OnNavigationRequested"/>
/// so child VMs can ask the shell to switch tabs.
/// </para>
/// </summary>
public sealed partial class GandalfShellViewModel : ObservableObject
{
    [ObservableProperty] private int _selectedTabIndex;

    public IReadOnlyList<ModuleTab> Tabs { get; }

    public GandalfShellViewModel(
        DashboardViewModel dashboardTab,
        TimerListViewModel userTab,
        QuestTimersViewModel questsTab,
        LootTimersViewModel lootTab)
    {
        Tabs = new[]
        {
            new ModuleTab("Dashboard", dashboardTab),
            new ModuleTab("User",      userTab),
            new ModuleTab("Quests",    questsTab),
            new ModuleTab("Loot",      lootTab),
        };

        dashboardTab.NavigationRequested += (_, n) => OnNavigationRequested(n.SourceId);
    }

    private void OnNavigationRequested(string sourceId) =>
        SelectedTabIndex = sourceId switch
        {
            "gandalf.user" => 1,
            "gandalf.quest" => 2,
            "gandalf.loot" => 3,
            _ => SelectedTabIndex,
        };
}
