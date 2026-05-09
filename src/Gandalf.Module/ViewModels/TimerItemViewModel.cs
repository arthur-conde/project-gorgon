using CommunityToolkit.Mvvm.ComponentModel;
using Gandalf.Domain;

namespace Gandalf.ViewModels;

public sealed partial class TimerItemViewModel : ObservableObject
{
    private TimerRow _row;

    // Last-fired snapshot. The binder + scheduler call Refresh repeatedly
    // (every state-flip moment, every fast-tick second on Running rows);
    // unconditionally re-firing every PropertyChanged event is the
    // bottleneck the redesign was aimed at. Diff against these to fire
    // only the properties whose value actually moved.
    private TimerCatalogEntry _lastCatalog;
    private TimerState _lastState;
    private double _lastFraction;
    private string _lastTimeDisplay;

    [ObservableProperty] private bool _elapsedWhileAway;

    public TimerItemViewModel(TimerRow row)
    {
        _row = row;
        _lastCatalog = row.Catalog;
        _lastState = row.State;
        _lastFraction = row.Fraction;
        _lastTimeDisplay = ComputeTimeDisplay();
    }

    public TimerRow Row => _row;
    public TimerCatalogEntry Catalog => _row.Catalog;

    public string Key => _row.Key;
    public string Name => _row.Name;
    public string GroupKey => _row.GroupKey;

    public TimerState State => _row.State;
    public bool IsIdle => State == TimerState.Idle;
    public bool IsRunning => State == TimerState.Running;
    public bool IsDone => State == TimerState.Done;

    public double Fraction => _row.Fraction;

    public string TimeDisplay => ComputeTimeDisplay();

    public string StatusColor => State switch
    {
        TimerState.Idle => "#999999",
        TimerState.Running => "#5b9bd5",
        TimerState.Done => "#66bb6a",
        _ => "#999999",
    };

    public string StatusLabel => State switch
    {
        TimerState.Idle => "Ready",
        TimerState.Running => "Running",
        TimerState.Done => "Done",
        _ => "?",
    };

    public string DurationLabel => FormatDuration(_row.Duration);

    public bool ShowStartButton => IsIdle;
    public bool ShowRestartButton => IsDone;
    public bool ShowProgressBar => IsRunning || IsDone;

    /// <summary>
    /// Loot-tab defeat rows whose duration came from the placeholder (no
    /// calibration entry) flag this true so the view can render a small
    /// "approx." or "?" badge. Other sources / calibrated rows return true.
    /// </summary>
    public bool IsDurationVerified =>
        Catalog.SourceMetadata is LootCatalogPayload p ? p.IsDurationVerified : true;

    /// <summary>Swap in a fresh row (new catalog + progress snapshot) and re-fire bindings.</summary>
    public void UpdateRow(TimerRow row)
    {
        _row = row;
        Refresh();
    }

    /// <summary>
    /// Per-property diff against the last-known snapshot. Fires
    /// <see cref="ObservableObject.OnPropertyChanged(string?)"/> only for
    /// properties whose value materially changed. Replaces the previous
    /// "fire all 14 events on every tick" approach that drove the freeze
    /// bug on the Loot tab.
    ///
    /// <para>Called by:</para>
    /// <list type="bullet">
    /// <item><see cref="UpdateRow"/> — after a delta-driven row swap.</item>
    /// <item><see cref="Services.TimerDisplayScheduler"/> slow tick — when a
    /// row's <see cref="TimerRow.NextDisplayChangeAt"/> moment elapses.</item>
    /// <item><see cref="Services.TimerDisplayScheduler"/> fast tick — at 1 Hz
    /// while the row is Running with a visible progress bar (drives
    /// <see cref="Fraction"/>).</item>
    /// </list>
    /// </summary>
    public void Refresh()
    {
        // Catalog cluster — Name / GroupKey / DurationLabel are the only
        // catalog-derived properties that flow through to bindings. Each
        // is fired only when the corresponding catalog field changed.
        if (!ReferenceEquals(_lastCatalog, _row.Catalog))
        {
            if (_lastCatalog.DisplayName != _row.Catalog.DisplayName)
                OnPropertyChanged(nameof(Name));
            if (!StringEquals(_lastCatalog.Region, _row.Catalog.Region))
                OnPropertyChanged(nameof(GroupKey));
            if (_lastCatalog.Duration != _row.Catalog.Duration)
                OnPropertyChanged(nameof(DurationLabel));
            _lastCatalog = _row.Catalog;
        }

        // State cluster — nine properties co-vary on a single State
        // transition. Cache the last-known State and fire the cluster
        // only when it flips.
        var newState = _row.State;
        if (newState != _lastState)
        {
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsDone));
            OnPropertyChanged(nameof(StatusColor));
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(ShowStartButton));
            OnPropertyChanged(nameof(ShowRestartButton));
            OnPropertyChanged(nameof(ShowProgressBar));
            _lastState = newState;
        }

        // Fraction (continuous while Running). Use a small tolerance so we
        // don't fire on imperceptible double-precision drift, but the value
        // moves visibly under any real-time advance.
        var newFraction = _row.Fraction;
        if (Math.Abs(newFraction - _lastFraction) > 0.0001)
        {
            OnPropertyChanged(nameof(Fraction));
            _lastFraction = newFraction;
        }

        // TimeDisplay (text bucket) — changes once per minute on Running /
        // old-Done rows, on the 60 s "done!" → "done 1m ago" flip on
        // just-Done rows. Compute once and string-compare.
        var newTimeDisplay = ComputeTimeDisplay();
        if (newTimeDisplay != _lastTimeDisplay)
        {
            OnPropertyChanged(nameof(TimeDisplay));
            _lastTimeDisplay = newTimeDisplay;
        }
    }

    private string ComputeTimeDisplay()
    {
        // Route every time-derived read through _row.Clock instead of
        // DateTimeOffset.UtcNow — the prior wall-clock read silently broke
        // ManualTime / FakeTimeProvider in tests (the row's State could
        // read as Done at frozen-time T while TimeDisplay's "X ago"
        // computed against real wall-clock would drift continuously) and
        // would also make TimerDisplayScheduler fire "early" or "late"
        // when wakeups are computed from the injected clock.
        return State switch
        {
            TimerState.Idle => FormatDuration(_row.Duration),
            TimerState.Done when _row.CompletedAt is { } completed =>
                (_row.Clock.GetUtcNow() - completed).TotalMinutes < 1
                    ? "done!"
                    : $"done {FormatDuration(_row.Clock.GetUtcNow() - completed)} ago",
            TimerState.Done => "done!",
            _ => FormatDuration(_row.Remaining) + " remaining",
        };
    }

    private static bool StringEquals(string? a, string? b) =>
        ReferenceEquals(a, b) || (a is not null && a.Equals(b, StringComparison.Ordinal));

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1)
            return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }
}
