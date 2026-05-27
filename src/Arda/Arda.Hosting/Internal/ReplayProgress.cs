using System.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Arda.Hosting.Internal;

/// <summary>
/// Tracks replay completion across source families. Each driver signals
/// <see cref="MarkComplete"/> when IsReplay transitions to false.
/// </summary>
internal sealed class ReplayProgress : IReplayProgress
{
    private readonly TaskCompletionSource _replayComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ILogger? _logger;
    private readonly object _lock = new();

    private double _playerProgress;
    private double _chatProgress;
    private bool _playerDone;
    private bool _chatDone;

    public ReplayProgress(ILogger<ReplayProgress>? logger = null) => _logger = logger;

    public double PlayerProgress => _playerProgress;
    public double ChatProgress => _chatProgress;

    public Task ReplayComplete => _replayComplete.Task;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Signal that a source family has completed replay (IsReplay flipped to false).
    /// When all families are complete, <see cref="ReplayComplete"/> resolves.
    /// </summary>
    public void MarkComplete(SourceFamily family)
    {
        bool firePlayer = false;
        bool fireChat = false;
        bool fireComplete = false;

        lock (_lock)
        {
            switch (family)
            {
                case SourceFamily.Player:
                    if (!_playerDone)
                    {
                        _playerDone = true;
                        if (Math.Abs(_playerProgress - 1.0) >= 0.001)
                        {
                            _playerProgress = 1.0;
                            firePlayer = true;
                        }
                    }
                    break;
                case SourceFamily.Chat:
                    if (!_chatDone)
                    {
                        _chatDone = true;
                        if (Math.Abs(_chatProgress - 1.0) >= 0.001)
                        {
                            _chatProgress = 1.0;
                            fireChat = true;
                        }
                    }
                    break;
            }

            if (_playerDone && _chatDone)
                fireComplete = true;
        }

        if (firePlayer)
        {
            _logger?.LogInformation("Player replay complete");
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayerProgress)));
        }

        if (fireChat)
        {
            _logger?.LogInformation("Chat replay complete");
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChatProgress)));
        }

        if (fireComplete)
        {
            _logger?.LogInformation("All source families replay complete");
            _replayComplete.TrySetResult();
        }
    }

    internal enum SourceFamily { Player, Chat }
}
