using CommunityToolkit.Mvvm.ComponentModel;
using Gandalf.Domain;

namespace Gandalf.ViewModels;

public sealed partial class TimerItemViewModel : ObservableObject
{
    private readonly GandalfTimer _timer;

    public TimerItemViewModel(GandalfTimer timer) => _timer = timer;

    public string Id => _timer.Id;
    public string Name => _timer.Name;
    public string Region => _timer.Region;
    public string Map => _timer.Map;
    public string GroupKey => _timer.GroupKey;
    public GandalfTimer Timer => _timer;

    public TimerState State => _timer.State;
    public bool IsIdle => State == TimerState.Idle;
    public bool IsRunning => State == TimerState.Running;
    public bool IsDone => State == TimerState.Done;

    public double Fraction => _timer.Fraction;

    public string TimeDisplay => State switch
    {
        TimerState.Idle => FormatDuration(_timer.Duration),
        TimerState.Done when _timer.CompletedAt is { } completed =>
            (DateTimeOffset.UtcNow - completed).TotalMinutes < 1
                ? "done!"
                : $"done {FormatDuration(DateTimeOffset.UtcNow - completed)} ago",
        TimerState.Done => "done!",
        _ => FormatDuration(_timer.Remaining) + " remaining",
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

    public string DurationLabel => FormatDuration(_timer.Duration);

    public bool ShowStartButton => IsIdle;
    public bool ShowRestartButton => IsDone;
    public bool ShowProgressBar => IsRunning || IsDone;

    public void Refresh()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(Fraction));
        OnPropertyChanged(nameof(TimeDisplay));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(StatusLabel));
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
