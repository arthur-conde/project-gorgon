using Celebrimbor.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Celebrimbor.ViewModels;

public sealed partial class CelebrimborSettingsViewModel : ObservableObject
{
    public CelebrimborSettingsViewModel(CelebrimborSettings settings)
    {
        Settings = settings;
    }

    public CelebrimborSettings Settings { get; }
}
