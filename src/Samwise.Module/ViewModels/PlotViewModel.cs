using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Samwise.Calibration;
using Samwise.Config;
using Samwise.State;

namespace Samwise.ViewModels;

public sealed record PhaseTickVm(double Fraction, string Color, string Tooltip);

public sealed partial class PlotViewModel : ObservableObject
{
    private readonly Plot _plot;
    private readonly ICropConfigStore _config;
    private readonly GrowthCalibrationService? _calibration;
    private IReadOnlyList<PhaseTickVm>? _ticks;

    public PlotViewModel(Plot plot, ICropConfigStore config, GrowthCalibrationService? calibration = null)
    {
        _plot = plot;
        _config = config;
        _calibration = calibration;
    }

    public string PlotId => _plot.PlotId;
    public string CharName => _plot.CharName;
    public string CropType => _plot.CropType ?? "Unknown";
    public PlotStage Stage => _plot.Stage;
    public string StageLabel => Stage switch
    {
        PlotStage.Planted => "🌱 Planted",
        PlotStage.Growing => "🌿 Growing",
        PlotStage.Thirsty => "💧 Thirsty",
        PlotStage.NeedsFertilizer => "🧪 Needs Fertilizer",
        PlotStage.Ripe => "🌾 Ready to Harvest",
        PlotStage.Harvested => "✅ Harvested",
        _ => "?",
    };

    public string StageColor => Stage switch
    {
        PlotStage.Planted => "#7ec87e",
        PlotStage.Growing => "#4caf50",
        PlotStage.Thirsty => "#5b9bd5",
        PlotStage.NeedsFertilizer => "#e6a817",
        PlotStage.Ripe => "#f0c040",
        PlotStage.Harvested => "#666666",
        _ => "#999999",
    };

    public double GrowthFraction
    {
        get
        {
            if (Stage == PlotStage.Ripe || Stage == PlotStage.Harvested) return 1.0;
            if (EffectiveElapsedSeconds is not double elapsed) return 0.0;
            if (GrowthSeconds is not int secs || secs <= 0) return 0.0;
            return Math.Clamp(elapsed / secs, 0.0, 1.0);
        }
    }

    public string TimeRemaining
    {
        get
        {
            if (Stage == PlotStage.Ripe) return "ready!";
            if (Stage == PlotStage.Harvested) return "—";
            if (Stage == PlotStage.Thirsty) return "needs water";
            if (Stage == PlotStage.NeedsFertilizer) return "needs fertilizer";
            if (EffectiveElapsedSeconds is not double elapsed) return "?";
            if (GrowthSeconds is not int secs) return "?";
            var rem = secs - (int)elapsed;
            if (rem <= 0) return "ready!";
            return rem >= 60 ? $"{rem / 60}m {rem % 60}s" : $"{rem}s";
        }
    }

    /// <summary>
    /// Fractional positions along the progress bar where phase transitions are
    /// expected, derived from calibrated phase durations. Empty when no
    /// calibration data exists for this crop.
    /// </summary>
    public IReadOnlyList<PhaseTickVm> PhaseTicks => _ticks ??= ComputeTicks();

    /// <summary>Call when calibration data updates so next access recomputes.</summary>
    internal void InvalidateTicks()
    {
        _ticks = null;
        OnPropertyChanged(nameof(PhaseTicks));
    }

    private IReadOnlyList<PhaseTickVm> ComputeTicks()
    {
        if (_calibration is null) return [];
        if (_plot.CropType is null) return [];
        if (GrowthSeconds is not int totalSecs || totalSecs <= 0) return [];

        // Walk growth-clock phases in their natural sequence and accumulate
        // durations. Reaction transitions (Thirsty→Growing, NeedsFert→Growing)
        // are already excluded from PhaseRates.
        var rates = _calibration.Data.PhaseRates;
        var ticks = new List<PhaseTickVm>(2);
        double cumulative = 0;

        // First Thirsty event: may arrive from the initial Planted phase or
        // from a post-resolution Growing phase. Take whichever has data.
        var thirstyDur = BestDuration(rates, _plot.CropType,
            (PlotStage.Planted, PlotStage.Thirsty),
            (PlotStage.Growing, PlotStage.Thirsty));
        if (thirstyDur is double thirstyS)
        {
            cumulative += thirstyS;
            var fraction = cumulative / totalSecs;
            if (fraction is > 0 and < 1)
                ticks.Add(new PhaseTickVm(fraction, "#5b9bd5",
                    $"Thirsty ≈ {cumulative:F0}s ({fraction * 100:F0}%)"));
        }

        // Fertilize event: always from Growing.
        var fertDur = BestDuration(rates, _plot.CropType,
            (PlotStage.Growing, PlotStage.NeedsFertilizer));
        if (fertDur is double fertS)
        {
            cumulative += fertS;
            var fraction = cumulative / totalSecs;
            if (fraction is > 0 and < 1)
                ticks.Add(new PhaseTickVm(fraction, "#e6a817",
                    $"Fertilize ≈ {cumulative:F0}s ({fraction * 100:F0}%)"));
        }
        return ticks;
    }

    private static double? BestDuration(
        IReadOnlyDictionary<string, PhaseTransitionRate> rates,
        string crop,
        params (PlotStage From, PlotStage To)[] candidates)
    {
        PhaseTransitionRate? best = null;
        foreach (var (from, to) in candidates)
        {
            if (!rates.TryGetValue($"{crop}|{from}→{to}", out var rate)) continue;
            if (rate.SampleCount < 3) continue;
            if (best is null || rate.SampleCount > best.SampleCount) best = rate;
        }
        return best?.AvgSeconds;
    }

    private int? GrowthSeconds
    {
        get
        {
            if (_plot.CropType is null) return null;
            if (!_config.Current.Crops.TryGetValue(_plot.CropType, out var def)) return null;
            return def.GrowthSeconds;
        }
    }

    private double? EffectiveElapsedSeconds
    {
        get
        {
            if (_plot.CropType is null) return null;
            // Freeze the clock while the plot is paused (Thirsty / NeedsFertilizer).
            var referenceTime = _plot.PausedSince ?? DateTimeOffset.UtcNow;
            return (referenceTime - _plot.PlantedAt - _plot.PausedDuration).TotalSeconds;
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Stage));
        OnPropertyChanged(nameof(StageLabel));
        OnPropertyChanged(nameof(StageColor));
        OnPropertyChanged(nameof(CropType));
        OnPropertyChanged(nameof(GrowthFraction));
        OnPropertyChanged(nameof(TimeRemaining));
    }
}
