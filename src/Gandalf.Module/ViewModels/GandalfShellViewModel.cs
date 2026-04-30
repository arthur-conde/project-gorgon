using CommunityToolkit.Mvvm.ComponentModel;

namespace Gandalf.ViewModels;

public sealed partial class GandalfShellViewModel : ObservableObject
{
    [ObservableProperty] private int _selectedTabIndex;

    public GandalfShellViewModel(
        DashboardViewModel dashboardTab,
        TimerListViewModel userTab,
        QuestTimersViewModel questsTab,
        LootTimersViewModel lootTab)
    {
        DashboardTab = dashboardTab;
        UserTab = userTab;
        QuestsTab = questsTab;
        LootTab = lootTab;

        DashboardTab.NavigationRequested += (_, n) => OnNavigationRequested(n.SourceId);
    }

    public DashboardViewModel DashboardTab { get; }
    public TimerListViewModel UserTab { get; }
    public QuestTimersViewModel QuestsTab { get; }
    public LootTimersViewModel LootTab { get; }

    private void OnNavigationRequested(string sourceId) =>
        SelectedTabIndex = sourceId switch
        {
            "gandalf.user" => 1,
            "gandalf.quest" => 2,
            "gandalf.loot" => 3,
            _ => SelectedTabIndex,
        };
}
