using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Gandalf.Domain;
using Mithril.Shared.Audio;
using Mithril.Shared.Game;
using Mithril.Shared.Wpf;

namespace Gandalf.Services;

/// <summary>
/// Fires alarms at Project Gorgon's published in-game-time-of-day shift
/// transitions (Midnight / Dawn / Morning / Afternoon / Dusk / Night;
/// see <see cref="IShiftCatalog"/>). Distinct from the user-curated timer
/// pipeline (<see cref="TimerAlarmService"/>) — shifts are global,
/// character-agnostic, and have no Start/Done lifecycle, so an
/// <see cref="Domain.ITimerSource"/>-style projection is over-scope. Owns
/// one <see cref="DispatcherTimer"/> scheduled at the next transition;
/// reschedules on tick + on settings changes.
/// </summary>
public sealed class ShiftAlarmService : IDisposable
{
    private readonly IGameClock _gameClock;
    private readonly IShiftCatalog _catalog;
    private readonly GandalfSettings _globalSettings;
    private readonly GandalfShiftSettings _shiftSettings;
    private readonly TimeProvider _time;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, IPlaybackHandle> _playback = new(StringComparer.Ordinal);
    private ShiftDefinition? _scheduledFor;
    private bool _disposed;

    public ShiftAlarmService(
        IGameClock gameClock,
        IShiftCatalog catalog,
        GandalfSettings globalSettings,
        GandalfShiftSettings shiftSettings,
        TimeProvider? time = null,
        Dispatcher? dispatcher = null)
    {
        _gameClock = gameClock;
        _catalog = catalog;
        _globalSettings = globalSettings;
        _shiftSettings = shiftSettings;
        _time = time ?? TimeProvider.System;
        _dispatcher = dispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
        _timer.Tick += (_, _) => OnTick();

        // Toggling a shift's Enabled or sound never changes *which* transition
        // is next (that's a function of the in-game clock alone), but it can
        // change whether the next tick should actually play. We don't need
        // to reschedule on settings change — OnTick re-reads config at fire
        // time. Subscriptions kept minimal.
        Reschedule();
    }

    /// <summary>Test hook — drive one tick without waiting on the dispatcher pump.</summary>
    internal void TickForTests() => OnTick();

    /// <summary>The shift the dispatcher timer will fire on next, or <c>null</c> when disposed.</summary>
    public ShiftDefinition? NextScheduledShift => _scheduledFor;

    /// <summary>Wall-clock instant of the next scheduled transition, or <c>null</c> when disposed.</summary>
    public DateTimeOffset? NextScheduledAt =>
        _disposed ? null : _timer.IsEnabled ? _time.GetUtcNow() + _timer.Interval : null;

    /// <summary>
    /// Stop any in-flight alarm playback for this shift (and any others) and
    /// reset state. Symmetric with <see cref="TimerAlarmService.DismissAll"/>.
    /// </summary>
    public void DismissAll()
    {
        foreach (var h in _playback.Values) h.Stop();
        _playback.Clear();
    }

    /// <summary>
    /// Decision logic for "should this shift transition fire an alarm right
    /// now?" — extracted as a static so unit tests can exercise it without
    /// driving the dispatcher pump or hitting <c>AudioPlayer.Play</c>.
    /// </summary>
    internal static bool ShouldAlarm(ShiftAlarmConfig config, GandalfSettings global) =>
        global.AlarmEnabled && config.Enabled;

    /// <summary>
    /// Per-shift <see cref="ShiftAlarmConfig.SoundFilePath"/> wins; null falls
    /// back to the global <see cref="GandalfSettings.SoundFilePath"/>. Mirrors
    /// <see cref="TimerAlarmService.ResolveSoundPath"/> so the per-row /
    /// per-shift override behavior is identical.
    /// </summary>
    internal static string? ResolveSoundPath(ShiftAlarmConfig config, GandalfSettings global) =>
        config.SoundFilePath ?? global.SoundFilePath;

    private void OnTick()
    {
        if (_disposed) return;

        var fired = _scheduledFor;
        if (fired is not null)
        {
            var config = _shiftSettings.GetOrCreate(fired.Slug);
            if (ShouldAlarm(config, _globalSettings))
            {
                var path = ResolveSoundPath(config, _globalSettings);
                Dispatch(() =>
                {
                    if (_playback.Remove(fired.Slug, out var prior)) prior.Stop();
                    _playback[fired.Slug] = AudioPlayer.Play(
                        path, (float)_globalSettings.AlarmVolume, "gandalf");
                    if (_globalSettings.FlashWindow)
                    {
                        var win = Application.Current?.MainWindow;
                        if (win is not null) WindowFlasher.Flash(win);
                    }
                });
            }
        }

        Reschedule();
    }

    private void Reschedule()
    {
        if (_disposed) return;

        var floor = _time.GetUtcNow();
        var (at, shift) = _catalog.NextTransition(_gameClock, floor);
        _scheduledFor = shift;

        var delay = at - floor;
        // Same epsilon rationale as TimerExpirationScheduler.cs:103 — below
        // human perception, above the dispatcher's resolution noise floor.
        if (delay < TimeSpan.FromMilliseconds(50)) delay = TimeSpan.FromMilliseconds(50);

        _timer.Interval = delay;
        if (!_timer.IsEnabled) _timer.Start();
    }

    private void Dispatch(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        DismissAll();
    }
}
