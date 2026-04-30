using CommunityToolkit.Mvvm.ComponentModel;
using Gandalf.Domain;

namespace Gandalf.ViewModels;

public sealed partial class TimerItemViewModel : ObservableObject
{
    private TimerRow _row;

    [ObservableProperty] private bool _elapsedWhileAway;

    public TimerItemViewModel(TimerRow row) => _row = row;

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

    public string TimeDisplay => State switch
    {
        TimerState.Idle => FormatDuration(_row.Duration),
        TimerState.Done when _row.CompletedAt is { } completed =>
            (DateTimeOffset.UtcNow - completed).TotalMinutes < 1
                ? "done!"
                : $"done {FormatDuration(DateTimeOffset.UtcNow - completed)} ago",
        TimerState.Done => "done!",
        _ => FormatDuration(_row.Remaining) + " remaining",
    };

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

    /// <summary>Swap in a fresh row (new catalog + progress snapshot) and re-fire bindings.</summary>
    public void UpdateRow(TimerRow row)
    {
        _row = row;
        Refresh();
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(GroupKey));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(Fraction));
        OnPropertyChanged(nameof(TimeDisplay));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(DurationLabel));
        OnPropertyChanged(nameof(ShowStartButton));
        OnPropertyChanged(nameof(ShowRestartButton));
        OnPropertyChanged(nameof(ShowProgressBar));
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1)
            return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }
}
