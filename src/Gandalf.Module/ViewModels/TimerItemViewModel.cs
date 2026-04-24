using CommunityToolkit.Mvvm.ComponentModel;
using Gandalf.Domain;

namespace Gandalf.ViewModels;

public sealed partial class TimerItemViewModel : ObservableObject
{
    private TimerView _view;

    public TimerItemViewModel(TimerView view) => _view = view;

    public TimerView View => _view;

    public string Id => _view.Def.Id;
    public string Name => _view.Def.Name;
    public string Region => _view.Def.Region;
    public string Map => _view.Def.Map;
    public string GroupKey => _view.GroupKey;

    public TimerState State => _view.State;
    public bool IsIdle => State == TimerState.Idle;
    public bool IsRunning => State == TimerState.Running;
    public bool IsDone => State == TimerState.Done;

    public double Fraction => _view.Fraction;

    public string TimeDisplay => State switch
    {
        TimerState.Idle => FormatDuration(_view.Def.Duration),
        TimerState.Done when _view.Progress.CompletedAt is { } completed =>
            (DateTimeOffset.UtcNow - completed).TotalMinutes < 1
                ? "done!"
                : $"done {FormatDuration(DateTimeOffset.UtcNow - completed)} ago",
        TimerState.Done => "done!",
        _ => FormatDuration(_view.Remaining) + " remaining",
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

    public string DurationLabel => FormatDuration(_view.Def.Duration);

    public bool ShowStartButton => IsIdle;
    public bool ShowRestartButton => IsDone;
    public bool ShowProgressBar => IsRunning || IsDone;

    /// <summary>Swap in a fresh view (new def + progress snapshot) and re-fire bindings.</summary>
    public void UpdateView(TimerView view)
    {
        _view = view;
        Refresh();
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Region));
        OnPropertyChanged(nameof(Map));
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
