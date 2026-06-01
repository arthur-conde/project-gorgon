using System.Globalization;
using Arda.Contracts.State.Health;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Palantir.ViewModels;

/// <summary>
/// Palantir detail page consuming <see cref="IWorldHealthView"/> — per-driver
/// rows showing mode, frame count, drift, and liveness. The health view fires
/// <see cref="IWorldHealthView.Changed"/> on the Arda dispatch thread, so
/// every handler marshals through <see cref="_dispatch"/>.
/// <para>
/// Issue #856: row colour/icon binds to <see cref="WorldMode"/> directly
/// rather than a derived "degraded" boolean — Stalled is now a first-class
/// mode and Halted has its own state, so a single bool can't represent the
/// row's attention level cleanly.
/// </para>
/// </summary>
public sealed partial class WorldHealthViewModel : ObservableObject, IDisposable
{
    private readonly IWorldHealthView _health;
    private readonly Action<Action> _dispatch;
    private bool _disposed;

    [ObservableProperty] private WorldMode _playerMode;
    [ObservableProperty] private string _playerModeText = "—";
    [ObservableProperty] private string _playerFrames = "0";
    [ObservableProperty] private string _playerDrift = "—";
    [ObservableProperty] private string _playerLastLog = "—";

    [ObservableProperty] private WorldMode _chatMode;
    [ObservableProperty] private string _chatModeText = "—";
    [ObservableProperty] private string _chatFrames = "0";
    [ObservableProperty] private string _chatDrift = "—";
    [ObservableProperty] private string _chatLastLog = "—";

    [ObservableProperty] private bool _allLive;
    [ObservableProperty] private string _overallStatus = "Waiting for data…";

    public WorldHealthViewModel(IWorldHealthView health)
        : this(health, dispatch: null)
    { }

    /// <summary>Test-friendly ctor with injectable dispatcher.</summary>
    public WorldHealthViewModel(IWorldHealthView health, Action<Action>? dispatch)
    {
        _health = health;
        _dispatch = dispatch ?? DefaultDispatch;
        _health.Changed += OnHealthChanged;
        Refresh();
    }

    private void OnHealthChanged(object? sender, EventArgs e) =>
        _dispatch(Refresh);

    private void Refresh()
    {
        var p = _health.Player;
        var c = _health.Chat;

        PlayerMode = p.Mode;
        PlayerModeText = p.Mode.ToString();
        PlayerFrames = p.FrameCount.ToString("N0", CultureInfo.CurrentCulture);
        PlayerDrift = FormatDrift(p);
        PlayerLastLog = FormatTimestamp(p.LastLogTimestamp);

        ChatMode = c.Mode;
        ChatModeText = c.Mode.ToString();
        ChatFrames = c.FrameCount.ToString("N0", CultureInfo.CurrentCulture);
        ChatDrift = FormatDrift(c);
        ChatLastLog = FormatTimestamp(c.LastLogTimestamp);

        AllLive = _health.AllLive;
        OverallStatus = ComputeOverall(p.Mode, c.Mode, _health.AllLive, _health.IsHalted);
    }

    private static string ComputeOverall(WorldMode player, WorldMode chat, bool allLive, bool isHalted)
    {
        if (isHalted) return "Halted — grammar break";
        if (player == WorldMode.Stalled || chat == WorldMode.Stalled)
            return "Tailer stalled";
        if (allLive) return "Live — healthy";
        return "Replaying log history…";
    }

    private static string FormatDrift(WorldHealth h)
    {
        var d = h.Drift;
        // Drift is tailer-poll age — meaningful from the moment a family
        // goes live, regardless of whether a log line has arrived. The old
        // "no timestamp → '—'" guard no longer applies.
        if (d.TotalSeconds < 1) return "<1s";
        if (d.TotalMinutes < 1) return $"{d.TotalSeconds:0.0}s";
        return $"{d.TotalMinutes:0.0}m";
    }

    private static string FormatTimestamp(DateTimeOffset? ts) =>
        ts?.ToString("u", CultureInfo.InvariantCulture) ?? "—";

    private static void DefaultDispatch(Action action)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.InvokeAsync(action);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _health.Changed -= OnHealthChanged;
    }
}
