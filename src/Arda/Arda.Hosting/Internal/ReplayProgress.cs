using System.ComponentModel;

namespace Arda.Hosting.Internal;

/// <summary>
/// Tracks replay completion across source families. Each driver signals
/// <see cref="MarkComplete"/> when IsReplay transitions to false.
/// </summary>
internal sealed class ReplayProgress : IReplayProgress
{
    private readonly TaskCompletionSource _replayComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lock = new();

    private double _playerProgress;
    private double _chatProgress;
    private bool _playerDone;
    private bool _chatDone;

    public double PlayerProgress
    {
        get => _playerProgress;
        private set
        {
            if (Math.Abs(_playerProgress - value) < 0.001) return;
            _playerProgress = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayerProgress)));
        }
    }

    public double ChatProgress
    {
        get => _chatProgress;
        private set
        {
            if (Math.Abs(_chatProgress - value) < 0.001) return;
            _chatProgress = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChatProgress)));
        }
    }

    public Task ReplayComplete => _replayComplete.Task;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Signal that a source family has completed replay (IsReplay flipped to false).
    /// When all families are complete, <see cref="ReplayComplete"/> resolves.
    /// </summary>
    public void MarkComplete(SourceFamily family)
    {
        lock (_lock)
        {
            switch (family)
            {
                case SourceFamily.Player:
                    _playerDone = true;
                    PlayerProgress = 1.0;
                    break;
                case SourceFamily.Chat:
                    _chatDone = true;
                    ChatProgress = 1.0;
                    break;
            }

            if (_playerDone && _chatDone)
                _replayComplete.TrySetResult();
        }
    }

    internal enum SourceFamily { Player, Chat }
}
