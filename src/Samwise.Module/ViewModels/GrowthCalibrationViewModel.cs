using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gorgon.Shared.Reference;
using Gorgon.Shared.Wpf.Dialogs;
using Samwise.Calibration;
using Samwise.State;

namespace Samwise.ViewModels;

public sealed class CropGrowthRateRow
{
    public required string CropType { get; init; }
    public required double AvgSeconds { get; init; }
    public required int SampleCount { get; init; }
    public required double MinSeconds { get; init; }
    public required double MaxSeconds { get; init; }
    public required int? ConfigSeconds { get; init; }
    public required double? DeltaPercent { get; init; }

    public string DeltaLabel => DeltaPercent switch
    {
        > 0 => $"+{DeltaPercent:F1}% high",
        < 0 => $"{DeltaPercent:F1}% low",
        0 => "exact",
        _ => "—",
    };

    public string AvgFormatted => FormatSeconds(AvgSeconds);
    public string MinFormatted => FormatSeconds(MinSeconds);
    public string MaxFormatted => FormatSeconds(MaxSeconds);
    public string ConfigFormatted => ConfigSeconds is int s ? FormatSeconds(s) : "—";

    private static string FormatSeconds(double s) =>
        s >= 60 ? $"{(int)s / 60}m {(int)s % 60}s" : $"{s:F0}s";
}

public sealed class GrowthObservationRow
{
    public required string CropType { get; init; }
    public required string CharName { get; init; }
    public required double EffectiveSeconds { get; init; }
    public required string PhaseSummary { get; init; }
    public required DateTimeOffset Timestamp { get; init; }

    public string EffectiveFormatted => EffectiveSeconds >= 60
        ? $"{(int)EffectiveSeconds / 60}m {(int)EffectiveSeconds % 60}s"
        : $"{EffectiveSeconds:F0}s";
}

public sealed class PhaseTransitionRow
{
    public required string CropType { get; init; }
    public required string Transition { get; init; }
    public required double AvgSeconds { get; init; }
    public required int SampleCount { get; init; }
    public required double MinSeconds { get; init; }
    public required double MaxSeconds { get; init; }

    public string AvgFormatted => $"{AvgSeconds:F1}s";
    public string MinFormatted => $"{MinSeconds:F1}s";
    public string MaxFormatted => $"{MaxSeconds:F1}s";
}

public sealed class SlotCapRow
{
    public required string Family { get; init; }
    public required int ObservedMax { get; init; }
    public required int SampleCount { get; init; }
    public required int? ConfigMax { get; init; }

    public string ConfigLabel => ConfigMax is int m ? m.ToString() : "—";

    public string MatchLabel => ConfigMax switch
    {
        null => "—",
        int m when m == ObservedMax => "match",
        int m when m > ObservedMax => $"config +{m - ObservedMax}",
        int m => $"config {m - ObservedMax}",
    };
}

public sealed partial class GrowthCalibrationViewModel : ObservableObject
{
    private readonly GrowthCalibrationService _calibration;
    private readonly ICommunityCalibrationService? _community;
    private readonly IDialogService? _dialogService;

    public GrowthCalibrationViewModel(
        GrowthCalibrationService calibration,
        ICommunityCalibrationService? community = null,
        IDialogService? dialogService = null)
    {
        _calibration = calibration;
        _community = community;
        _dialogService = dialogService;
        _calibration.DataChanged += (_, _) => Refresh();
        if (_community is not null) _community.FileUpdated += (_, _) => Refresh();
        Refresh();
    }

    [ObservableProperty]
    private ObservableCollection<CropGrowthRateRow> _rates = [];

    [ObservableProperty]
    private ObservableCollection<GrowthObservationRow> _observations = [];

    [ObservableProperty]
    private ObservableCollection<PhaseTransitionRow> _phaseRates = [];

    [ObservableProperty]
    private ObservableCollection<SlotCapRow> _slotCaps = [];

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private string _communitySummary = "";

    [RelayCommand]
    private void Share()
    {
        if (_dialogService is null) return;
        var vm = new CommunityShareDialogViewModel(
            moduleDisplayName: "Samwise",
            issueTemplateFile: "samwise-contribution.yml",
            exportJson: note => _calibration.ExportCommunityJson(note));
        _dialogService.ShowDialog(vm, new CommunityShareDialog { DataContext = vm });
    }

    [RelayCommand]
    private async Task RefreshCommunityAsync()
    {
        if (_community is null) return;
        CommunitySummary = "Refreshing…";
        try { await _community.RefreshAsync("samwise"); }
        catch { /* swallow — state reflected in Refresh() */ }
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        var data = _calibration.Data;

        var rates = data.Rates.Values
            .OrderByDescending(r => r.SampleCount)
            .ThenBy(r => r.CropType, StringComparer.OrdinalIgnoreCase)
            .Select(r => new CropGrowthRateRow
            {
                CropType = r.CropType,
                AvgSeconds = r.AvgSeconds,
                SampleCount = r.SampleCount,
                MinSeconds = r.MinSeconds,
                MaxSeconds = r.MaxSeconds,
                ConfigSeconds = r.ConfigSeconds,
                DeltaPercent = r.DeltaPercent,
            })
            .ToList();
        Rates = new ObservableCollection<CropGrowthRateRow>(rates);

        var observations = data.Observations
            .OrderByDescending(o => o.Timestamp)
            .Select(o => new GrowthObservationRow
            {
                CropType = o.CropType,
                CharName = o.CharName,
                EffectiveSeconds = o.EffectiveSeconds,
                PhaseSummary = BuildPhaseSummary(o.Phases),
                Timestamp = o.Timestamp,
            })
            .ToList();
        Observations = new ObservableCollection<GrowthObservationRow>(observations);

        var phaseRates = data.PhaseRates.Values
            .OrderBy(r => r.CropType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => (int)r.FromStage)
            .Select(r => new PhaseTransitionRow
            {
                CropType = r.CropType,
                Transition = $"{StageName(r.FromStage)} → {StageName(r.ToStage)}",
                AvgSeconds = r.AvgSeconds,
                SampleCount = r.SampleCount,
                MinSeconds = r.MinSeconds,
                MaxSeconds = r.MaxSeconds,
            })
            .ToList();
        PhaseRates = new ObservableCollection<PhaseTransitionRow>(phaseRates);

        var slotCaps = data.SlotCapRates.Values
            .OrderBy(r => r.Family, StringComparer.OrdinalIgnoreCase)
            .Select(r => new SlotCapRow
            {
                Family = r.Family,
                ObservedMax = r.ObservedMax,
                SampleCount = r.SampleCount,
                ConfigMax = r.ConfigMax,
            })
            .ToList();
        SlotCaps = new ObservableCollection<SlotCapRow>(slotCaps);

        StatusMessage = $"{data.Rates.Count} crop(s) calibrated from {data.Observations.Count} cycle(s), "
            + $"{data.PhaseRates.Count} phase transition(s), {data.SlotCapRates.Count} slot cap(s)";

        UpdateCommunitySummary();
    }

    private void UpdateCommunitySummary()
    {
        if (_community is null)
        {
            CommunitySummary = "";
            return;
        }
        var payload = _community.SamwiseRates;
        if (payload is null)
        {
            CommunitySummary = "No community data yet.";
            return;
        }
        var snap = _community.GetSnapshot("samwise");
        var total = payload.Rates.Count + payload.PhaseRates.Count + payload.SlotCapRates.Count;
        var when = snap.FetchedAtUtc is { } t ? $"refreshed {t.LocalDateTime:yyyy-MM-dd HH:mm}" : "no refresh yet";
        CommunitySummary = $"Using {total} community entries · {when}";
    }

    private static string BuildPhaseSummary(List<PhaseRecord> phases)
    {
        if (phases.Count == 0) return "—";
        return string.Join(" → ", phases
            .Where(p => p.DurationSeconds > 0 || p.Stage == PlotStage.Ripe)
            .Select(p => p.Stage == PlotStage.Ripe
                ? "Ripe"
                : $"{StageName(p.Stage)}({p.DurationSeconds:F0}s)"));
    }

    private static string StageName(PlotStage stage) => stage switch
    {
        PlotStage.Planted => "Plant",
        PlotStage.Growing => "Grow",
        PlotStage.Thirsty => "Thirsty",
        PlotStage.NeedsFertilizer => "Fert",
        PlotStage.Ripe => "Ripe",
        PlotStage.Harvested => "Done",
        _ => stage.ToString(),
    };
}
