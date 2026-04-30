using CommunityToolkit.Mvvm.ComponentModel;

namespace Gandalf.ViewModels;

public sealed partial class GandalfShellViewModel : ObservableObject
{
    public GandalfShellViewModel(
        TimerListViewModel userTab,
        QuestTimersViewModel questsTab,
        LootTimersViewModel lootTab)
    {
        UserTab = userTab;
        QuestsTab = questsTab;
        LootTab = lootTab;
    }

    public TimerListViewModel UserTab { get; }
    public QuestTimersViewModel QuestsTab { get; }
    public LootTimersViewModel LootTab { get; }

    public string DashboardPlaceholder => "Coming soon";
}
