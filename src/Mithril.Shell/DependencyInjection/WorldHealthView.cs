using Arda.Contracts;
using Arda.Contracts.State.Health;
using Arda.Hosting;
using Arda.World.Chat.Events;
using Arda.World.Player.Events;
using Mithril.Shared.Modules;
using Microsoft.Extensions.Hosting;

namespace Mithril.Shell.DependencyInjection;

/// <summary>
/// Observes Arda pipeline health by subscribing to domain events and
/// <see cref="IReplayProgress"/>. Derives per-driver drift (wall-clock
/// minus last-seen log timestamp) and mode (replaying vs live).
///
/// <para>Implements <see cref="IAttentionSource"/> so the shell attention
/// badge surfaces when live-mode drift exceeds a threshold — the Arda
/// replacement for the legacy <c>LogStreamAttentionSource</c>.</para>
/// </summary>
internal sealed class WorldHealthView : IWorldHealthView, IAttentionSource, IHostedService, IDisposable
{
    private static readonly TimeSpan DriftWarningThreshold = TimeSpan.FromSeconds(5);

    private readonly IDomainEventSubscriber _bus;
    private readonly IReplayProgress _replay;
    private readonly TimeProvider _time;

    private readonly object _gate = new();
    private DateTimeOffset? _playerTimestamp;
    private DateTimeOffset? _chatTimestamp;
    private long _playerFrames;
    private long _chatFrames;
    private bool _playerLive;
    private bool _chatLive;

    private readonly List<IDisposable> _subscriptions = [];
    private bool _disposed;

    public WorldHealthView(
        IDomainEventSubscriber bus,
        IReplayProgress replay,
        TimeProvider? time = null)
    {
        _bus = bus;
        _replay = replay;
        _time = time ?? TimeProvider.System;
    }

    public WorldHealth Player
    {
        get
        {
            lock (_gate)
                return Snapshot(_playerTimestamp, _playerFrames, _playerLive);
        }
    }

    public WorldHealth Chat
    {
        get
        {
            lock (_gate)
                return Snapshot(_chatTimestamp, _chatFrames, _chatLive);
        }
    }

    public bool AllLive
    {
        get { lock (_gate) return _playerLive && _chatLive; }
    }

    public event EventHandler? Changed;

    // IAttentionSource
    public string ModuleId => "pipeline-health";
    public string DisplayLabel => "Pipeline health — log tailing drift";

    public int Count
    {
        get
        {
            lock (_gate)
            {
                var count = 0;
                if (_playerLive && DriftExceeds(_playerTimestamp)) count++;
                if (_chatLive && DriftExceeds(_chatTimestamp)) count++;
                return count;
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscriptions.Add(_bus.Subscribe<CalendarTimeAdvanced>(OnCalendarAdvanced));
        _subscriptions.Add(_bus.Subscribe<PlayerChatLine>(OnChatEvent));
        _subscriptions.Add(_bus.Subscribe<ChatInventoryObserved>(OnChatInventory));
        _subscriptions.Add(_bus.Subscribe<ChatSessionIdentified>(OnChatSession));

        _replay.PropertyChanged += OnReplayProgressChanged;

        if (_replay.ReplayComplete.IsCompleted)
        {
            lock (_gate)
            {
                _playerLive = true;
                _chatLive = true;
            }
        }

        return Task.CompletedTask;
    }

    private void OnCalendarAdvanced(CalendarTimeAdvanced evt)
    {
        bool fire;
        lock (_gate)
        {
            _playerTimestamp = evt.Now;
            _playerFrames++;
            fire = true;
        }
        if (fire) RaiseChanged();
    }

    private void OnChatEvent(PlayerChatLine evt)
    {
        AdvanceChat(evt.Metadata);
    }

    private void OnChatInventory(ChatInventoryObserved evt)
    {
        AdvanceChat(evt.Metadata);
    }

    private void OnChatSession(ChatSessionIdentified evt)
    {
        AdvanceChat(evt.Metadata);
    }

    private void AdvanceChat(Arda.Abstractions.Logs.LogLineMetadata metadata)
    {
        var ts = metadata.Timestamp;
        if (ts is null) return;
        lock (_gate)
        {
            _chatTimestamp = ts;
            _chatFrames++;
        }
        RaiseChanged();
    }

    private void OnReplayProgressChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        bool changed = false;
        lock (_gate)
        {
            if (_replay.ReplayComplete.IsCompleted)
            {
                if (!_playerLive || !_chatLive)
                {
                    _playerLive = true;
                    _chatLive = true;
                    changed = true;
                }
            }
            else
            {
                var wasPlayerLive = _playerLive;
                var wasChatLive = _chatLive;
                _playerLive = _replay.PlayerProgress >= 1.0;
                _chatLive = _replay.ChatProgress >= 1.0;
                changed = _playerLive != wasPlayerLive || _chatLive != wasChatLive;
            }
        }
        if (changed) RaiseChanged();
    }

    private WorldHealth Snapshot(DateTimeOffset? ts, long frames, bool live)
    {
        var drift = ts is not null
            ? _time.GetUtcNow() - ts.Value
            : TimeSpan.Zero;
        if (drift < TimeSpan.Zero) drift = TimeSpan.Zero;

        return new WorldHealth(ts, frames, live ? WorldMode.Live : WorldMode.Replaying, drift);
    }

    private bool DriftExceeds(DateTimeOffset? ts)
    {
        if (ts is null) return false;
        var drift = _time.GetUtcNow() - ts.Value;
        return drift > DriftWarningThreshold;
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _replay.PropertyChanged -= OnReplayProgressChanged;
        foreach (var sub in _subscriptions) sub.Dispose();
        _subscriptions.Clear();
    }
}
