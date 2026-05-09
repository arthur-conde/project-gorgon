using System.Windows;
using System.Windows.Threading;
using Gandalf.Domain;
using Gandalf.ViewModels;

namespace Gandalf.Services;

/// <summary>
/// Wakes registered <see cref="TimerItemViewModel"/>s only when their visible
/// display would actually change — replacing the per-tab 1 Hz
/// <see cref="DispatcherTimer"/> that previously refreshed every row every
/// second whether anything had moved or not.
///
/// <para>Two tiers:</para>
/// <list type="bullet">
/// <item><b>Slow tick</b> — wakes at <c>min(TimerRow.NextDisplayChangeAt)</c>
/// across registered rows. Used when the only thing changing is the
/// <c>"Xh Ym remaining"</c> text bucket on a Running row, or
/// <c>"done X ago"</c> on a Done row, which only flip on minute boundaries.
/// Idle rows return <c>null</c> for <c>NextDisplayChangeAt</c> and don't
/// hold the timer awake at all.</item>
/// <item><b>Fast tick</b> — 1 Hz, runs only while at least one registered
/// row is <c>Running</c> with <c>ShowProgressBar = true</c>. Drives sub-minute
/// <c>Fraction</c> animation on visible progress bars. Stops when the last
/// such row finishes.</item>
/// </list>
///
/// <para>When neither condition holds — all rows Idle, or all Done with
/// <c>NextDisplayChangeAt</c> in the past for "done Xm ago" stability —
/// both timers are stopped. Zero per-second work on an all-Idle tab.</para>
///
/// <para>Threading: same dispatcher contract as <see cref="TimerSourceBinder"/>.
/// Production callers run on the UI thread; tests fall back to
/// <see cref="Dispatcher.CurrentDispatcher"/> and drive ticks directly via
/// <see cref="TickSlowForTests"/> / <see cref="TickFastForTests"/>.</para>
/// </summary>
public sealed class TimerDisplayScheduler : IDisposable
{
    private readonly TimeProvider _clock;
    private readonly Dispatcher _dispatcher;
    // Per-row scheduled wake + last-known state. Both are captured at
    // Register / Refresh / tick time because the corresponding TimerRow
    // properties (NextDisplayChangeAt, State) re-read the clock on every
    // access — once the wake instant has passed, NextDisplayChangeAt
    // already points at the next future moment and State already reads as
    // the post-flip value, so we'd miss the transition without bookkeeping.
    private readonly Dictionary<TimerItemViewModel, ScheduledRow> _scheduledFor = [];

    private sealed class ScheduledRow
    {
        public DateTimeOffset? NextChangeAt;
        public TimerState LastKnownState;
    }
    private readonly DispatcherTimer _slowTimer;
    private readonly DispatcherTimer _fastTimer;
    private bool _disposed;

    public TimerDisplayScheduler(TimeProvider clock, Dispatcher? dispatcher = null)
    {
        _clock = clock;
        _dispatcher = dispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        _slowTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
        _slowTimer.Tick += (_, _) => OnSlowTick();

        _fastTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _fastTimer.Tick += (_, _) => OnFastTick();
    }

    /// <summary>
    /// Fires when a tick caused at least one row's <see cref="TimerItemViewModel.State"/>
    /// to transition (e.g. <c>Running → Done</c>) — the host VM should call
    /// <c>ICollectionView.Refresh</c> because <c>IsDone</c> is in the sort
    /// descriptors and the State filter chip needs re-evaluating.
    /// </summary>
    public event EventHandler? RefreshRequired;

    /// <summary>
    /// Soonest <c>NextDisplayChangeAt</c> across registered rows, or
    /// <c>null</c> when no row needs a future wake. Exposed for tests and
    /// diagnostics; not load-bearing for normal operation (the slow timer's
    /// interval reflects this internally).
    /// </summary>
    public DateTimeOffset? NextSlowTickAt
    {
        get
        {
            DateTimeOffset? earliest = null;
            foreach (var row in _scheduledFor.Values)
            {
                if (row.NextChangeAt is not { } when) continue;
                if (earliest is null || when < earliest) earliest = when;
            }
            return earliest;
        }
    }

    /// <summary>
    /// True iff at least one registered row is <c>Running</c> with
    /// <c>ShowProgressBar = true</c>. The fast-tick gate predicate.
    /// </summary>
    public bool AnyRunningWithProgressBar
    {
        get
        {
            foreach (var vm in _scheduledFor.Keys)
                if (vm.IsRunning && vm.ShowProgressBar) return true;
            return false;
        }
    }

    public void Register(TimerItemViewModel vm)
    {
        if (_disposed) return;
        if (_scheduledFor.ContainsKey(vm)) return;
        _scheduledFor[vm] = Snapshot(vm);
        ReevaluateTimers();
    }

    public void Unregister(TimerItemViewModel vm)
    {
        if (_disposed) return;
        if (_scheduledFor.Remove(vm)) ReevaluateTimers();
    }

    /// <summary>
    /// Recompute timer scheduling for a single registered row whose
    /// <c>NextDisplayChangeAt</c> may have shifted (e.g. after the host
    /// applied <c>UpdateRow</c> via the binder, or the user dismissed a row).
    /// No-op if the row isn't registered.
    /// </summary>
    public void Refresh(TimerItemViewModel vm)
    {
        if (_disposed) return;
        if (!_scheduledFor.TryGetValue(vm, out var row)) return;
        row.NextChangeAt = vm.Row.NextDisplayChangeAt;
        row.LastKnownState = vm.State;
        ReevaluateTimers();
    }

    /// <summary>
    /// Re-read <c>NextDisplayChangeAt</c> and current state for every
    /// registered row. Call after a batch of host-side updates (e.g. when
    /// the binder finishes applying a deltas batch and the host VM wants
    /// the scheduler to converge against the new state).
    /// </summary>
    public void RefreshAll()
    {
        if (_disposed) return;
        foreach (var vm in _scheduledFor.Keys.ToArray())
        {
            var row = _scheduledFor[vm];
            row.NextChangeAt = vm.Row.NextDisplayChangeAt;
            row.LastKnownState = vm.State;
        }
        ReevaluateTimers();
    }

    private static ScheduledRow Snapshot(TimerItemViewModel vm) =>
        new()
        {
            NextChangeAt = vm.Row.NextDisplayChangeAt,
            LastKnownState = vm.State,
        };

    /// <summary>Test hook: drive one slow-tick cycle without waiting on the dispatcher pump.</summary>
    internal void TickSlowForTests() => OnSlowTick();

    /// <summary>Test hook: drive one fast-tick cycle without waiting on the dispatcher pump.</summary>
    internal void TickFastForTests() => OnFastTick();

    private void ReevaluateTimers()
    {
        // Fast tick gate.
        if (AnyRunningWithProgressBar)
        {
            if (!_fastTimer.IsEnabled) _fastTimer.Start();
        }
        else
        {
            if (_fastTimer.IsEnabled) _fastTimer.Stop();
        }

        // Slow tick scheduling. When fast tick is running it already wakes
        // every second, so the slow-tick wake is redundant — let it stay
        // stopped while fast is active.
        if (_fastTimer.IsEnabled)
        {
            if (_slowTimer.IsEnabled) _slowTimer.Stop();
            return;
        }

        var earliest = NextSlowTickAt;
        if (earliest is null)
        {
            if (_slowTimer.IsEnabled) _slowTimer.Stop();
            return;
        }

        var delay = earliest.Value - _clock.GetUtcNow();
        // Floor: avoid sub-tick scheduling that just spins. 50 ms is below
        // human perception for a state-flip moment but above the dispatcher's
        // resolution noise floor.
        if (delay < TimeSpan.FromMilliseconds(50)) delay = TimeSpan.FromMilliseconds(50);
        _slowTimer.Interval = delay;
        if (!_slowTimer.IsEnabled) _slowTimer.Start();
    }

    private void OnSlowTick()
    {
        if (_disposed) return;
        var now = _clock.GetUtcNow();
        Tick(refreshPredicate: scheduled =>
            scheduled.NextChangeAt is { } when && when <= now);
    }

    private void OnFastTick()
    {
        if (_disposed) return;
        // Fast-tick refresh condition is the gate predicate evaluated per VM
        // — Running rows with visible progress bars need their Fraction
        // refreshed every second. State-change detection happens uniformly
        // in Tick() regardless.
        Tick(refreshPredicate: _ => false, fastRefresh: true);
    }

    /// <summary>
    /// Shared body for slow + fast ticks. Iterates registered rows, refreshes
    /// those whose tier-specific condition is met, and uniformly fires
    /// <see cref="RefreshRequired"/> when any row's state transitioned since
    /// the last observation. State transitions are detected even on rows we
    /// don't refresh — a Running row that just crossed to Done won't satisfy
    /// the fast-tick gate any more, but its state-change must still propagate.
    /// </summary>
    private void Tick(Func<ScheduledRow, bool> refreshPredicate, bool fastRefresh = false)
    {
        var stateChanged = false;

        foreach (var vm in _scheduledFor.Keys.ToArray())
        {
            var scheduled = _scheduledFor[vm];
            var oldState = scheduled.LastKnownState;

            var shouldSlowRefresh = refreshPredicate(scheduled);
            var shouldFastRefresh = fastRefresh && vm.IsRunning && vm.ShowProgressBar;

            if (shouldSlowRefresh || shouldFastRefresh)
            {
                vm.Refresh();
                scheduled.NextChangeAt = vm.Row.NextDisplayChangeAt;
            }

            var newState = vm.State;
            if (newState != oldState) stateChanged = true;
            scheduled.LastKnownState = newState;
        }

        if (stateChanged) RefreshRequired?.Invoke(this, EventArgs.Empty);
        ReevaluateTimers();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _slowTimer.Stop();
        _fastTimer.Stop();
    }
}
