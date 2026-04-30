using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gandalf.Domain;
using Gandalf.Services;

namespace Gandalf.ViewModels;

/// <summary>
/// Read-only home view that aggregates ready/cooling rows across every
/// <see cref="ITimerSource"/>. Three sections — Ready Now, Coming Up, and a
/// per-source count chip row. Click-through raises
/// <see cref="NavigationRequested"/> so the shell can flip to the relevant
/// tab and scroll the row into view.
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    private const int ReadyNowMax = 20;
    private const int ComingUpMax = 15;

    private readonly DashboardAggregator _aggregator;
    private readonly DispatcherTimer _refreshTimer;

    public DashboardViewModel(DashboardAggregator aggregator)
    {
        _aggregator = aggregator;
        _aggregator.Updated += (_, _) => Refresh();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        // 1Hz tick so Cooling → Ready transitions show up without a source
        // event — pure time progression doesn't fire ProgressChanged.
        _refreshTimer.Tick += (_, _) => { _aggregator.Recompute(); };
        _refreshTimer.Start();

        Refresh();
    }

    public ObservableCollection<DashboardRow> ReadyNow { get; } = [];
    public ObservableCollection<DashboardRow> ComingUp { get; } = [];
    public ObservableCollection<DashboardSourceChip> SourceChips { get; } = [];

    public event EventHandler<DashboardNavigation>? NavigationRequested;

    [RelayCommand]
    private void Navigate(DashboardRow? row)
    {
        if (row is null) return;
        NavigationRequested?.Invoke(this, new DashboardNavigation(row.SourceId, row.Key));
    }

    private void Refresh()
    {
        var summaries = _aggregator.Summaries;

        ReadyNow.Clear();
        var ready = summaries
            .Where(s => s.State == TimerState.Done && s.ExpiresAt is not null)
            .OrderByDescending(s => s.ExpiresAt)
            .Take(ReadyNowMax);
        foreach (var s in ready) ReadyNow.Add(DashboardRow.From(s));

        ComingUp.Clear();
        var cooling = summaries
            .Where(s => s.State == TimerState.Running && s.ExpiresAt is not null)
            .OrderBy(s => s.ExpiresAt)
            .Take(ComingUpMax);
        foreach (var s in cooling) ComingUp.Add(DashboardRow.From(s));

        SourceChips.Clear();
        var bySource = summaries.GroupBy(s => s.SourceId).OrderBy(g => g.Key);
        foreach (var group in bySource)
        {
            var readyCount = group.Count(s => s.State == TimerState.Done);
            var coolingCount = group.Count(s => s.State == TimerState.Running);
            SourceChips.Add(new DashboardSourceChip(
                SourceId: group.Key,
                Label: FormatSourceLabel(group.Key, readyCount, coolingCount)));
        }
    }

    private static string FormatSourceLabel(string sourceId, int ready, int cooling)
    {
        var name = FriendlyName(sourceId);
        if (ready == 0 && cooling == 0) return $"{name}: idle";
        var parts = new List<string>(2);
        if (ready > 0) parts.Add($"{ready} ready");
        if (cooling > 0) parts.Add($"{cooling} cooling");
        return $"{name}: {string.Join(" · ", parts)}";
    }

    private static string FriendlyName(string sourceId) => sourceId switch
    {
        "gandalf.user" => "User",
        "gandalf.quest" => "Quests",
        "gandalf.loot" => "Loot",
        _ => sourceId,
    };
}

public sealed record DashboardRow(
    string SourceId,
    string Key,
    string DisplayName,
    string? Region,
    DateTimeOffset? ExpiresAt,
    string SourceLabel)
{
    public static DashboardRow From(TimerSummary s) => new(
        SourceId: s.SourceId,
        Key: s.Key,
        DisplayName: s.DisplayName,
        Region: s.Region,
        ExpiresAt: s.ExpiresAt,
        SourceLabel: FriendlySourceLabel(s.SourceId));

    public string TimeDisplay
    {
        get
        {
            if (ExpiresAt is null) return "";
            var delta = ExpiresAt.Value - DateTimeOffset.UtcNow;
            if (delta <= TimeSpan.Zero) return Format(-delta) + " ago";
            return "in " + Format(delta);
        }
    }

    private static string Format(TimeSpan ts)
    {
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m";
        return $"{ts.Seconds}s";
    }

    private static string FriendlySourceLabel(string sourceId) => sourceId switch
    {
        "gandalf.user" => "User",
        "gandalf.quest" => "Quest",
        "gandalf.loot" => "Loot",
        _ => sourceId,
    };
}

public sealed record DashboardSourceChip(string SourceId, string Label);

public sealed record DashboardNavigation(string SourceId, string Key);
