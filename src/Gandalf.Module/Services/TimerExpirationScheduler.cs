using System.Windows;
using System.Windows.Threading;
using Gandalf.Domain;

namespace Gandalf.Services;

/// <summary>
/// Drives <see cref="TimerProgressService.CheckExpirations"/> on its own
/// schedule, replacing the 1 Hz <c>TimerListViewModel.Tick</c> that
/// previously held that responsibility. Owns a <see cref="DispatcherTimer"/>
/// scheduled at the soonest known expiration moment across all user-timer
/// definitions; on fire, runs <c>CheckExpirations</c> (which stamps
/// <c>CompletedAt</c> and fires <c>TimerExpired</c> → <c>UserTimerSource.TimerReady</c>
/// → <c>TimerAlarmService</c>) and reschedules.
///
/// <para>Lives in <c>Gandalf.Module</c> rather than alongside
/// <see cref="TimerProgressService"/> in the data layer because it owns a
/// WPF <see cref="DispatcherTimer"/> and Arthur explicitly didn't want WPF
/// dependencies leaking into the per-character data services.</para>
///
/// <para>Reschedule triggers:
/// <list type="bullet">
/// <item><see cref="TimerProgressService.ProgressChanged"/> — covers Start /
/// Restart / Reset / Dismiss / character-switch / clear-all-done.</item>
/// <item><see cref="TimerDefinitionsService.DefinitionsChanged"/> — a
/// definition added or removed shifts the compute set.</item>
/// <item>The own tick — recompute after firing, in case
/// <c>CheckExpirations</c> stamped a <c>CompletedAt</c> that removes one
/// pending entry.</item>
/// </list></para>
///
/// <para>When no timer is currently Running, the dispatcher timer is
/// stopped — zero per-second work for a character with no active user
/// timers. The "wake up at the right moment" model means a timer set 8 h
/// from now sits silent for 8 h, then fires alarms, then sleeps again.</para>
/// </summary>
public sealed class TimerExpirationScheduler : IDisposable
{
    private readonly TimerProgressService _progress;
    private readonly TimerDefinitionsService _defs;
    private readonly TimeProvider _clock;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    public TimerExpirationScheduler(
        TimerProgressService progress,
        TimerDefinitionsService defs,
        TimeProvider? clock = null,
        Dispatcher? dispatcher = null)
    {
        _progress = progress;
        _defs = defs;
        _clock = clock ?? TimeProvider.System;
        _dispatcher = dispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
        _timer.Tick += (_, _) => OnTick();

        _progress.ProgressChanged += OnSourceChanged;
        _defs.DefinitionsChanged += OnSourceChanged;

        // Initial scheduling — in case timers were Running from a prior
        // session, we want to wake at the next expiration immediately.
        Reschedule();
    }

    /// <summary>Test hook — drive one expiration cycle without waiting on the dispatcher pump.</summary>
    internal void TickForTests() => OnTick();

    /// <summary>The next scheduled expiration moment, or <c>null</c> when no timer is Running.</summary>
    public DateTimeOffset? NextExpirationAt => ComputeSoonest();

    private void OnSourceChanged(object? sender, EventArgs e) => Dispatch(Reschedule);

    private void OnTick()
    {
        if (_disposed) return;
        _progress.CheckExpirations();
        // CheckExpirations may have fired ProgressChanged via the stamping
        // path → recursive Reschedule. Guarded internally; explicit call
        // here covers the case where the stamp didn't actually mutate any
        // observable state (already-stamped row).
        Reschedule();
    }

    private void Reschedule()
    {
        if (_disposed) return;

        var soonest = ComputeSoonest();
        if (soonest is null)
        {
            if (_timer.IsEnabled) _timer.Stop();
            return;
        }

        var now = _clock.GetUtcNow();
        var delay = soonest.Value - now;
        // Floor at 50 ms — same rationale as TimerDisplayScheduler. Below
        // human perception for the alarm-fire moment but above the
        // dispatcher's resolution noise floor.
        if (delay < TimeSpan.FromMilliseconds(50)) delay = TimeSpan.FromMilliseconds(50);

        _timer.Interval = delay;
        if (!_timer.IsEnabled) _timer.Start();
    }

    private DateTimeOffset? ComputeSoonest()
    {
        DateTimeOffset? soonest = null;
        foreach (var def in _defs.Definitions)
        {
            var p = _progress.GetProgress(def.Id);
            if (p?.StartedAt is null) continue;
            if (p.CompletedAt is not null) continue;  // already stamped — no further expiration to fire
            // FiringAt is the canonical firing instant (StartedAt + Duration for
            // countdowns, or the next in-game-time occurrence for game-clock
            // timers). Falls back to the legacy arithmetic when a TimerProgress
            // wasn't stamped by the service (e.g. mid-rollout test fixtures).
            var expiresAt = p.FiringAt ?? p.StartedAt.Value + def.Duration;
            if (soonest is null || expiresAt < soonest) soonest = expiresAt;
        }
        return soonest;
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
        _progress.ProgressChanged -= OnSourceChanged;
        _defs.DefinitionsChanged -= OnSourceChanged;
    }
}
