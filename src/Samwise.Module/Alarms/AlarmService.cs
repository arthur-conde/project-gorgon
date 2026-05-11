using System.Windows;
using System.Windows.Threading;
using Mithril.Shared.Audio;
using Mithril.Shared.Wpf;
using Samwise.State;

namespace Samwise.Alarms;

public sealed record ActiveAlarm(string Key, string CharName, string CropType, DateTimeOffset Triggered);

public sealed class AlarmService : IDisposable
{
    private readonly GardenStateMachine _state;
    private readonly SamwiseSettings _settings;
    private readonly IAudioPlaybackSink _audio;
    private readonly Dictionary<string, DateTimeOffset> _firedAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _snoozedUntil = new(StringComparer.Ordinal);

    // AlarmKey identifies which alarm "owns" the handle so StopOwner can stop the
    // exact (plot, stage) alarm rather than whichever sound currently plays on the channel.
    private sealed record ChannelOwner(string AlarmKey, IPlaybackHandle Handle);
    private readonly Dictionary<string, List<ChannelOwner>> _channelPlayback = new(StringComparer.Ordinal);

    public event EventHandler<ActiveAlarm>? AlarmTriggered;

    public AlarmService(GardenStateMachine state, SamwiseSettings settings, IAudioPlaybackSink audio)
    {
        _state = state;
        _settings = settings;
        _audio = audio;
        _state.PlotChanged += OnPlotChanged;
    }

    public IReadOnlyCollection<string> ActiveKeys => _firedAt.Keys.ToArray();

    public void SnoozeAll()
    {
        var until = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(_settings.Alarms.SnoozeMinutes);
        foreach (var key in _firedAt.Keys.ToArray()) _snoozedUntil[key] = until;
        _firedAt.Clear();
        StopAllChannelPlayback();
    }

    public void DismissAll()
    {
        _firedAt.Clear();
        StopAllChannelPlayback();
    }

    private void OnPlotChanged(object? sender, PlotChangedArgs e)
    {
        if (e.OldStage is not null && e.NewStage != e.OldStage)
        {
            var resolvedKey = $"{e.Plot.CharName}|{e.Plot.PlotId}|{e.OldStage}";
            if (_firedAt.Remove(resolvedKey)
                && _settings.Alarms.Rules.TryGetValue(e.OldStage.Value, out var oldRule)
                && oldRule.StopOnInteraction)
            {
                StopOwner(oldRule.ChannelId, resolvedKey);
            }
        }

        if (!_settings.Alarms.Enabled) return;
        if (e.Plot.CropType is null) return;
        if (e.OldStage is null) return;
        if (e.NewStage == e.OldStage) return;
        if (!_settings.Alarms.Rules.TryGetValue(e.NewStage, out var rule) || !rule.Enabled) return;
        if (_state.IsLikelyGarbageCollected(e.Plot)) return;
        if (_settings.Alarms.MutedCrops.Contains(e.Plot.CropType)) return;
        if (_settings.Alarms.MutedCharacters.Contains(e.Plot.CharName)) return;

        // Dedupe per (plot, stage) so a re-trigger for the same stage is ignored,
        // but a later Ripe transition after an earlier Thirsty alarm still fires.
        var key = $"{e.Plot.CharName}|{e.Plot.PlotId}|{e.NewStage}";
        if (_snoozedUntil.TryGetValue(key, out var until) && until > DateTimeOffset.UtcNow) return;
        if (_firedAt.ContainsKey(key)) return;

        _firedAt[key] = DateTimeOffset.UtcNow;
        Fire(new ActiveAlarm(key, e.Plot.CharName, e.Plot.CropType, DateTimeOffset.UtcNow), rule);
    }

    private AlarmChannel ResolveChannel(string id)
        => _settings.Alarms.Channels.FirstOrDefault(c => c.Id == id)
           ?? _settings.Alarms.Channels[0];

    private static void PruneStopped(List<ChannelOwner> owners)
    {
        for (int i = owners.Count - 1; i >= 0; i--)
            if (!owners[i].Handle.IsPlaying) owners.RemoveAt(i);
    }

    private List<ChannelOwner> OwnersOf(string channelId)
    {
        if (!_channelPlayback.TryGetValue(channelId, out var list))
            _channelPlayback[channelId] = list = new();
        return list;
    }

    private void StopOwner(string channelId, string alarmKey)
    {
        var resolved = ResolveChannel(channelId).Id;
        if (!_channelPlayback.TryGetValue(resolved, out var owners)) return;
        var mine = owners.FirstOrDefault(o => o.AlarmKey == alarmKey);
        if (mine is not null)
        {
            mine.Handle.Stop();
            owners.Remove(mine);
        }
    }

    private void StopAllChannelPlayback()
    {
        foreach (var owners in _channelPlayback.Values)
        {
            foreach (var o in owners) o.Handle.Stop();
            owners.Clear();
        }
    }

    private void Fire(ActiveAlarm alarm, StageAlarmRule rule)
    {
        Dispatch(() =>
        {
            var channel = ResolveChannel(rule.ChannelId);
            var owners = OwnersOf(channel.Id);
            PruneStopped(owners);

            bool willPlaySound;
            switch (channel.Collision)
            {
                case AlarmCollisionBehavior.Suppress:
                    willPlaySound = owners.Count == 0;
                    break;
                case AlarmCollisionBehavior.Replace:
                    foreach (var o in owners) o.Handle.Stop();
                    owners.Clear();
                    willPlaySound = true;
                    break;
                case AlarmCollisionBehavior.Mix:
                default:
                    willPlaySound = true;
                    break;
            }

            if (willPlaySound)
            {
                var handle = _audio.Play(rule.SoundFilePath, (float)_settings.Alarms.AlarmVolume, "samwise", loop: rule.Loop);
                owners.Add(new ChannelOwner(alarm.Key, handle));
            }

            if (_settings.Alarms.FlashWindow)
            {
                var win = Application.Current?.MainWindow;
                if (win is not null) WindowFlasher.Flash(win);
            }

            AlarmTriggered?.Invoke(this, alarm);
        });
    }

    private static void Dispatch(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a(); else d.InvokeAsync(a, DispatcherPriority.Normal);
    }

    public void HandleHarvested(Plot plot)
    {
        var prefix = $"{plot.CharName}|{plot.PlotId}|";
        foreach (var k in _firedAt.Keys.Where(k => k.StartsWith(prefix)).ToArray())
        {
            _firedAt.Remove(k);
            var stageName = k[(prefix.Length)..];
            if (Enum.TryParse<PlotStage>(stageName, out var stage)
                && _settings.Alarms.Rules.TryGetValue(stage, out var rule)
                && rule.StopOnInteraction)
            {
                StopOwner(rule.ChannelId, k);
            }
        }
        foreach (var k in _snoozedUntil.Keys.Where(k => k.StartsWith(prefix)).ToArray())
            _snoozedUntil.Remove(k);
    }

    public void Dispose()
    {
        _state.PlotChanged -= OnPlotChanged;
        StopAllChannelPlayback();
    }
}
