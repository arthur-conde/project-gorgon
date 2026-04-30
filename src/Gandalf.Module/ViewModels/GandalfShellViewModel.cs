using CommunityToolkit.Mvvm.ComponentModel;

namespace Gandalf.ViewModels;

public sealed partial class GandalfShellViewModel : ObservableObject
{
    public GandalfShellViewModel(TimerListViewModel userTab, QuestTimersViewModel questsTab)
    {
        UserTab = userTab;
        QuestsTab = questsTab;
    }

    public TimerListViewModel UserTab { get; }
    public QuestTimersViewModel QuestsTab { get; }

    public string DashboardPlaceholder => "Coming soon";
    public string LootPlaceholder => "Coming soon";
}
