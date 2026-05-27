using System.Globalization;
using Arda.Contracts.State.Health;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Palantir.ViewModels;

/// <summary>
/// Palantir detail page consuming <see cref="IWorldHealthView"/> — per-driver
/// rows showing mode, frame count, drift, and liveness. The health view fires
/// <see cref="IWorldHealthView.Changed"/> on the Arda dispatch thread, so
/// every handler marshals through <see cref="_dispatch"/>.
/// </summary>
public sealed partial class WorldHealthViewModel : ObservableObject, IDisposable
{
    private readonly IWorldHealthView _health;
    private readonly Action<Action> _dispatch;
    private bool _disposed;

    [ObservableProperty] private string _playerMode = "—";
    [ObservableProperty] private string _playerFrames = "0";
    [ObservableProperty] private string _playerDrift = "—";
    [ObservableProperty] private string _playerTimestamp = "—";
    [ObservableProperty] private bool _playerDegraded;

    [ObservableProperty] private string _chatMode = "—";
    [ObservableProperty] private string _chatFrames = "0";
    [ObservableProperty] private string _chatDrift = "—";
    [ObservableProperty] private string _chatTimestamp = "—";
    [ObservableProperty] private bool _chatDegraded;

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

        PlayerMode = p.Mode.ToString();
        PlayerFrames = p.FrameCount.ToString("N0", CultureInfo.CurrentCulture);
        PlayerDrift = FormatDrift(p);
        PlayerTimestamp = FormatTimestamp(p.LastTimestamp);
        PlayerDegraded = p.Mode == WorldMode.Live && p.Drift > TimeSpan.FromSeconds(5);

        ChatMode = c.Mode.ToString();
        ChatFrames = c.FrameCount.ToString("N0", CultureInfo.CurrentCulture);
        ChatDrift = FormatDrift(c);
        ChatTimestamp = FormatTimestamp(c.LastTimestamp);
        ChatDegraded = c.Mode == WorldMode.Live && c.Drift > TimeSpan.FromSeconds(5);

        AllLive = _health.AllLive;
        OverallStatus = _health.AllLive
            ? (PlayerDegraded || ChatDegraded ? "Live — drift warning" : "Live — healthy")
            : "Replaying log history…";
    }

    private static string FormatDrift(WorldHealth h)
    {
        if (h.LastTimestamp is null) return "—";
        var d = h.Drift;
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
