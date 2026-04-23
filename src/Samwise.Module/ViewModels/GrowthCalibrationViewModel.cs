using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

public sealed partial class GrowthCalibrationViewModel : ObservableObject
{
    private readonly GrowthCalibrationService _calibration;

    public GrowthCalibrationViewModel(GrowthCalibrationService calibration)
    {
        _calibration = calibration;
        _calibration.DataChanged += (_, _) => Refresh();
        Refresh();
    }

    [ObservableProperty]
    private ObservableCollection<CropGrowthRateRow> _rates = [];

    [ObservableProperty]
    private ObservableCollection<GrowthObservationRow> _observations = [];

    [ObservableProperty]
    private string _statusMessage = "";

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

        StatusMessage = $"{data.Rates.Count} crop(s) calibrated from {data.Observations.Count} observation(s)";
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
