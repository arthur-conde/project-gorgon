using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shared.Game;
using Microsoft.Win32;

namespace Mithril.Shell.ViewModels;

public sealed partial class GameConfigViewModel : ObservableObject
{
    private readonly GameConfig _config;

    public GameConfigViewModel(GameConfig config)
    {
        _config = config;
        _gameRoot = config.GameRoot;
        _pollSeconds = config.PollIntervalSeconds;
    }

    [ObservableProperty] private string _gameRoot;
    [ObservableProperty] private double _pollSeconds;

    public string PlayerLogPath => string.IsNullOrEmpty(GameRoot) ? "(unset)" : System.IO.Path.Combine(GameRoot, "Player.log");

    partial void OnGameRootChanged(string value)
    {
        _config.GameRoot = value;
        OnPropertyChanged(nameof(PlayerLogPath));
    }

    partial void OnPollSecondsChanged(double value) => _config.PollIntervalSeconds = value;

    [RelayCommand]
    private void Browse()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select Project Gorgon root folder (the one containing Player.log)",
        };
        if (dlg.ShowDialog() == true) GameRoot = dlg.FolderName;
    }

    [RelayCommand]
    private void AutoDetect()
    {
        var detected = GameLocator.AutoDetectGameRoot();
        if (detected is not null) GameRoot = detected;
    }
}
