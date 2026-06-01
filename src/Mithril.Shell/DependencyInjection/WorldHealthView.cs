using Arda.Abstractions.Diagnostics;
using Arda.Contracts;
using Arda.Contracts.State.Health;
using Arda.Dispatch;
using Arda.Hosting;
using Arda.World.Chat.Events;
using Arda.World.Player.Events;
using Mithril.Shared.Modules;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mithril.Shell.DependencyInjection;

/// <summary>
/// Observes Arda pipeline health.
/// <para>
/// Issue #856 redefined the headline metric: <c>WorldHealth.Drift</c> is the
/// wall-clock age of the tailer's last poll (from <see cref="IIngestPulse"/>),
/// NOT the age of the last in-stream log timestamp. This makes drift a real
/// tailer-liveness signal that's invariant under "the user is AFK / on a
/// quiet chat channel". <see cref="WorldHealth.LastLogTimestamp"/> is kept
/// as an informational field for "last X: 12m ago" UI text.
/// </para>
/// <para>
/// Mode is derived lazily inside <see cref="Snapshot"/>: if grammar raised →
/// <see cref="WorldMode.Halted"/>; else if not yet live →
/// <see cref="WorldMode.Replaying"/>; else if poll age >
/// <see cref="WorldHealth.DriftWarningThreshold"/> →
/// <see cref="WorldMode.Stalled"/>; else <see cref="WorldMode.Live"/>. No
/// timer — pulses from either family naturally re-fire
/// <see cref="Changed"/>, which bounds detection lag to one poll interval.
/// </para>
/// <para>
/// Implements <see cref="IAttentionSource"/> so the shell attention badge
/// surfaces when a driver is <see cref="WorldMode.Stalled"/>. <see cref="WorldMode.Halted"/>
/// has its own banner path so it does not double-count here.
/// </para>
/// </summary>
internal sealed class WorldHealthView : IWorldHealthView, IAttentionSource, IHostedService, IDisposable
{

    private readonly IDomainEventSubscriber _bus;
    private readonly IReplayProgress _replay;
    private readonly IGrammarBreakSignal _grammarSignal;
    private readonly IIngestPulse _pulse;
    private readonly TimeProvider _time;
    private readonly ILogger<WorldHealthView> _logger;

    private readonly object _gate = new();
    private DateTimeOffset? _playerLogTimestamp;
    private DateTimeOffset? _chatLogTimestamp;
    private DateTimeOffset? _playerLastPoll;
    private DateTimeOffset? _chatLastPoll;
    private long _playerFrames;
    private long _chatFrames;
    private bool _playerLive;
    private bool _chatLive;
    private GrammarBreak? _break;

    private readonly List<IDisposable> _subscriptions = [];
    private bool _disposed;

    public WorldHealthView(
        IDomainEventSubscriber bus,
        IReplayProgress replay,
        IGrammarBreakSignal grammarSignal,
        IIngestPulse pulse,
        ILogger<WorldHealthView> logger,
        TimeProvider? time = null)
    {
        _bus = bus;
        _replay = replay;
        _grammarSignal = grammarSignal;
        _pulse = pulse;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public WorldHealth Player
    {
        get
        {
            lock (_gate)
                return Snapshot(_playerLogTimestamp, _playerLastPoll, _playerFrames, _playerLive, _grammarSignal.IsRaised);
        }
    }

    public WorldHealth Chat
    {
        get
        {
            lock (_gate)
                return Snapshot(_chatLogTimestamp, _chatLastPoll, _chatFrames, _chatLive, _grammarSignal.IsRaised);
        }
    }

    public bool AllLive
    {
        get
        {
            // Strict per design lock #9: only Live exactly counts — Stalled
            // does NOT. Inline the Mode derivation rather than calling
            // Snapshot twice.
            lock (_gate)
            {
                if (_grammarSignal.IsRaised) return false;
                if (!_playerLive || !_chatLive) return false;
                return !IsStalled(_playerLastPoll) && !IsStalled(_chatLastPoll);
            }
        }
    }

    public GrammarBreak? Break
    {
        get { lock (_gate) return _break; }
    }

    public bool IsHalted
    {
        get { lock (_gate) return _grammarSignal.IsRaised; }
    }

    public bool IsTolerantBreakActive
    {
        get { lock (_gate) return _grammarSignal.HasObservedBreak && !_grammarSignal.IsRaised; }
    }

    public int ObservedBreakCount => _grammarSignal.ObservedCount;

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
                // Stalled is the attention-worthy state. Halted has its own
                // banner path (design lock #10), Live/Replaying don't count.
                var count = 0;
                if (_playerLive && !_grammarSignal.IsRaised && IsStalled(_playerLastPoll)) count++;
                if (_chatLive && !_grammarSignal.IsRaised && IsStalled(_chatLastPoll)) count++;
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
        _grammarSignal.Raised += OnGrammarBreakRaised;
        _grammarSignal.ObservedBreakChanged += OnGrammarBreakObserved;
        _pulse.Pulsed += OnPulse;

        if (_replay.ReplayComplete.IsCompleted)
        {
            // Seed each family's LastPoll to "now" on the live transition
            // (design lock #5). The clock starts immediately; if no pulse
            // arrives within DriftWarningThreshold the mode flips to Stalled.
            var now = _time.GetUtcNow();
            lock (_gate)
            {
                _playerLive = true;
                _chatLive = true;
                _playerLastPoll ??= _pulse.LastPoll(LogFamily.Player) ?? now;
                _chatLastPoll ??= _pulse.LastPoll(LogFamily.Chat) ?? now;
            }
        }

        // If a break was already raised before subscription, surface it.
        if (_grammarSignal.Current is { } existing)
        {
            lock (_gate) _break = existing;
            RaiseChanged();
        }

        return Task.CompletedTask;
    }

    private void OnPulse(object? sender, IngestPulseEventArgs e)
    {
        lock (_gate)
        {
            switch (e.Family)
            {
                case LogFamily.Player: _playerLastPoll = e.PolledAt; break;
                case LogFamily.Chat: _chatLastPoll = e.PolledAt; break;
            }
        }
        // Re-fire Changed so consumers re-derive mode; this is the only
        // re-evaluation cadence (design lock #6 — no timer).
        RaiseChanged();
    }

    private void OnGrammarBreakRaised(object? sender, EventArgs e)
    {
        var current = _grammarSignal.Current;
        if (current is null) return;
        lock (_gate) _break = current;
        _logger.LogError(
            "Arda pipeline halted on grammar drift: verb {Verb}, hint {Hint}",
            current.Verb, current.ParserHint);
        RaiseChanged();
    }

    private void OnGrammarBreakObserved(object? sender, EventArgs e)
    {
        // Tolerant-mode observations populate Break (first one wins) so the
        // shell banner can render verb/hint context without going through
        // IsRaised. RaiseChanged() then refreshes ObservedBreakCount.
        var current = _grammarSignal.Current;
        if (current is null) return;
        lock (_gate) _break ??= current;
        RaiseChanged();
    }

    private void OnCalendarAdvanced(CalendarTimeAdvanced evt)
    {
        lock (_gate)
        {
            _playerLogTimestamp = evt.Now;
            _playerFrames++;
        }
        RaiseChanged();
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
            _chatLogTimestamp = ts;
            _chatFrames++;
        }
        RaiseChanged();
    }

    private void OnReplayProgressChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        bool changed = false;
        bool playerWentLive = false;
        bool chatWentLive = false;
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_replay.ReplayComplete.IsCompleted)
            {
                if (!_playerLive || !_chatLive)
                {
                    playerWentLive = !_playerLive;
                    chatWentLive = !_chatLive;
                    _playerLive = true;
                    _chatLive = true;
                    // Seed LastPoll on live transition (design lock #5).
                    if (playerWentLive)
                        _playerLastPoll ??= _pulse.LastPoll(LogFamily.Player) ?? now;
                    if (chatWentLive)
                        _chatLastPoll ??= _pulse.LastPoll(LogFamily.Chat) ?? now;
                    changed = true;
                }
            }
            else
            {
                var wasPlayerLive = _playerLive;
                var wasChatLive = _chatLive;
                _playerLive = _replay.PlayerProgress >= 1.0;
                _chatLive = _replay.ChatProgress >= 1.0;
                playerWentLive = _playerLive && !wasPlayerLive;
                chatWentLive = _chatLive && !wasChatLive;
                if (playerWentLive)
                    _playerLastPoll ??= _pulse.LastPoll(LogFamily.Player) ?? now;
                if (chatWentLive)
                    _chatLastPoll ??= _pulse.LastPoll(LogFamily.Chat) ?? now;
                changed = _playerLive != wasPlayerLive || _chatLive != wasChatLive;
            }
        }

        if (playerWentLive)
            _logger.LogInformation("Pipeline health: player family live");
        if (chatWentLive)
            _logger.LogInformation("Pipeline health: chat family live");
        if (changed) RaiseChanged();
    }

    private WorldHealth Snapshot(DateTimeOffset? logTs, DateTimeOffset? lastPoll, long frames, bool live, bool halted)
    {
        // Drift = wall-clock − last poll (#856), NOT wall-clock − last log
        // timestamp. lastPoll being null in Live mode means "we just went
        // live and haven't seen a pulse yet"; drift is 0 in that case.
        var drift = lastPoll is not null
            ? _time.GetUtcNow() - lastPoll.Value
            : TimeSpan.Zero;
        if (drift < TimeSpan.Zero) drift = TimeSpan.Zero;

        WorldMode mode;
        if (halted) mode = WorldMode.Halted;
        else if (!live) mode = WorldMode.Replaying;
        else if (drift > WorldHealth.DriftWarningThreshold) mode = WorldMode.Stalled;
        else mode = WorldMode.Live;

        return new WorldHealth(logTs, frames, mode, drift);
    }

    private bool IsStalled(DateTimeOffset? lastPoll)
    {
        if (lastPoll is null) return false;
        var drift = _time.GetUtcNow() - lastPoll.Value;
        return drift > WorldHealth.DriftWarningThreshold;
    }

    private void RaiseChanged()
    {
        DateTimeOffset? playerLastPoll;
        DateTimeOffset? chatLastPoll;
        bool playerLive;
        bool chatLive;
        lock (_gate)
        {
            playerLastPoll = _playerLastPoll;
            chatLastPoll = _chatLastPoll;
            playerLive = _playerLive;
            chatLive = _chatLive;
        }

        LogStallIfNeeded("Player", playerLive, playerLastPoll);
        LogStallIfNeeded("Chat", chatLive, chatLastPoll);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void LogStallIfNeeded(string family, bool live, DateTimeOffset? lastPoll)
    {
        if (!live || lastPoll is null) return;
        var drift = _time.GetUtcNow() - lastPoll.Value;
        if (drift <= WorldHealth.DriftWarningThreshold) return;
        _logger.LogWarning(
            "Pipeline stall for {Family}: {DriftSeconds:F0}s since last tailer poll {LastPoll}",
            family,
            drift.TotalSeconds,
            lastPoll);
    }

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
        _grammarSignal.Raised -= OnGrammarBreakRaised;
        _grammarSignal.ObservedBreakChanged -= OnGrammarBreakObserved;
        _pulse.Pulsed -= OnPulse;
        foreach (var sub in _subscriptions) sub.Dispose();
        _subscriptions.Clear();
    }
}
