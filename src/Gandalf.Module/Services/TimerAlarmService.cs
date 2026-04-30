using System.Windows;
using System.Windows.Threading;
using Gandalf.Domain;
using Mithril.Shared.Audio;
using Mithril.Shared.Wpf;

namespace Gandalf.Services;

/// <summary>
/// Plays the alarm sound + flashes the window when a timer transitions to ready.
/// Subscribes to <see cref="ITimerSource.TimerReady"/> rather than
/// <c>TimerProgressService.TimerExpired</c> directly — the source-level event is
/// the cross-source surface a future shell-level inbox will share. Today only the
/// user feed is wired in; quest/loot sources will register the same way when those
/// phases land.
/// </summary>
public sealed class TimerAlarmService : IDisposable
{
    private readonly ITimerSource _source;
    private readonly GandalfSettings _settings;
    private readonly HashSet<string> _firedKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _snoozedUntil = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IPlaybackHandle> _playback = new(StringComparer.Ordinal);

    public TimerAlarmService(ITimerSource source, GandalfSettings settings)
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
