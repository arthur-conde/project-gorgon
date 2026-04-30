using CommunityToolkit.Mvvm.ComponentModel;

namespace Gandalf.ViewModels;

public sealed partial class GandalfShellViewModel : ObservableObject
{
    public GandalfShellViewModel(TimerListViewModel userTab)
    {
        UserTab = userTab;
    }

    public TimerListViewModel UserTab { get; }

    public string DashboardPlaceholder => "Coming soon";
    public string QuestsPlaceholder => "Coming soon";
    public string LootPlaceholder => "Coming soon";
}
