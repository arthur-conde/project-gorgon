using System.Windows;
using System.Windows.Threading;
using Gandalf.Domain;
using Mithril.Shared.Audio;
using Mithril.Shared.Wpf;

namespace Gandalf.Services;

/// <summary>
/// Plays the alarm sound + flashes the window when a user timer transitions to
/// ready. Subscribes to <see cref="UserTimerSource"/> directly — quest and loot
/// cooldowns ship through the cross-source <c>DashboardAggregator</c> instead;
/// dragging derived rows into the user-tab alarm path would surface an audible
/// alarm for every chest re-loot the player observes, which isn't the user's
/// expressed preference. Cross-source notification belongs in a future shell
/// inbox subsystem (see docs/gandalf-roadmap.md non-goals).
/// </summary>
public sealed class TimerAlarmService : IDisposable
{
    private readonly UserTimerSource _source;
    private readonly GandalfSettings _settings;
    private readonly HashSet<string> _firedKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _snoozedUntil = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IPlaybackHandle> _playback = new(StringComparer.Ordinal);

    public TimerAlarmService(UserTimerSource source, GandalfSettings settings)
    {
        _source = source;
        _settings = settings;
        _source.TimerReady += OnTimerReady;
    }

    public void SnoozeAll()
    {
        var until = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(_settings.SnoozeMinutes);
        foreach (var key in _firedKeys.ToArray()) _snoozedUntil[key] = until;
        _firedKeys.Clear();
        StopAllPlayback();
    }

    public void DismissAll()
    {
        _firedKeys.Clear();
        StopAllPlayback();
    }

    public void Dismiss(string key)
    {
        _firedKeys.Remove(key);
        if (_playback.Remove(key, out var handle))
            handle.Stop();
    }

    private void OnTimerReady(object? sender, TimerReadyEventArgs e)
    {
        if (!_settings.AlarmEnabled) return;
        var key = e.Key;
        if (_firedKeys.Contains(key)) return;
        if (_snoozedUntil.TryGetValue(key, out var until) && until > DateTimeOffset.UtcNow) return;

        _firedKeys.Add(key);
        Dispatch(() =>
        {
            var handle = AudioPlayer.Play(_settings.SoundFilePath, (float)_settings.AlarmVolume, "gandalf");
            _playback[key] = handle;
            if (_settings.FlashWindow)
            {
                var win = Application.Current?.MainWindow;
                if (win is not null) WindowFlasher.Flash(win);
            }
        });
    }

    private static void Dispatch(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a(); else d.InvokeAsync(a, DispatcherPriority.Normal);
    }

    public void Dispose()
    {
        _source.TimerReady -= OnTimerReady;
        StopAllPlayback();
    }

    private void StopAllPlayback()
    {
        foreach (var h in _playback.Values) h.Stop();
        _playback.Clear();
    }
}
