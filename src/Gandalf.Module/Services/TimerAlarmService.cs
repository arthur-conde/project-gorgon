using System.Windows;
using System.Windows.Threading;
using Gandalf.Domain;
using Mithril.Shared.Audio;
using Mithril.Shared.Wpf;

namespace Gandalf.Services;

public sealed class TimerAlarmService : IDisposable
{
    private readonly TimerProgressService _progress;
    private readonly GandalfSettings _settings;
    private readonly HashSet<string> _firedIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _snoozedUntil = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IPlaybackHandle> _playback = new(StringComparer.Ordinal);

    public TimerAlarmService(TimerProgressService progress, GandalfSettings settings)
    {
        _progress = progress;
        _settings = settings;
        _progress.TimerExpired += OnTimerExpired;
    }

    public void SnoozeAll()
    {
        var until = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(_settings.SnoozeMinutes);
        foreach (var id in _firedIds.ToArray()) _snoozedUntil[id] = until;
        _firedIds.Clear();
        StopAllPlayback();
    }

    public void DismissAll()
    {
        _firedIds.Clear();
        StopAllPlayback();
    }

    public void Dismiss(string id)
    {
        _firedIds.Remove(id);
        if (_playback.Remove(id, out var handle))
            handle.Stop();
    }

    private void OnTimerExpired(object? sender, TimerExpiredEventArgs e)
    {
        if (!_settings.AlarmEnabled) return;
        var id = e.Def.Id;
        if (_firedIds.Contains(id)) return;
        if (_snoozedUntil.TryGetValue(id, out var until) && until > DateTimeOffset.UtcNow) return;

        _firedIds.Add(id);
        Dispatch(() =>
        {
            var handle = AudioPlayer.Play(_settings.SoundFilePath, (float)_settings.AlarmVolume, "gandalf");
            _playback[id] = handle;
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
        _progress.TimerExpired -= OnTimerExpired;
        StopAllPlayback();
    }

    private void StopAllPlayback()
    {
        foreach (var h in _playback.Values) h.Stop();
        _playback.Clear();
    }
}
