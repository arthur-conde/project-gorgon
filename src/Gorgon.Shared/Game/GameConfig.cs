using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Gorgon.Shared.Game;

public sealed class GameConfig : INotifyPropertyChanged
{
    private string _gameRoot = "";
    public string GameRoot
    {
        get => _gameRoot;
        set => Set(ref _gameRoot, value);
    }

    private double _pollIntervalSeconds = 1.0;
    public double PollIntervalSeconds
    {
        get => _pollIntervalSeconds;
        set => Set(ref _pollIntervalSeconds, Math.Max(0.1, value));
    }

    public string PlayerLogPath => string.IsNullOrEmpty(GameRoot) ? "" : Path.Combine(GameRoot, "Player.log");
    public string ChatLogDirectory => string.IsNullOrEmpty(GameRoot) ? "" : Path.Combine(GameRoot, "ChatLogs");
    public string ReportsDirectory => string.IsNullOrEmpty(GameRoot) ? "" : Path.Combine(GameRoot, "Reports");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name == nameof(GameRoot))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayerLogPath)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChatLogDirectory)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReportsDirectory)));
        }
    }
}
