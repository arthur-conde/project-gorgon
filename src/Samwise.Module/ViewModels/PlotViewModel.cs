using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Samwise.Config;
using Samwise.State;

namespace Samwise.ViewModels;

public sealed partial class PlotViewModel : ObservableObject
{
    private readonly Plot _plot;
    private readonly ICropConfigStore _config;

    public PlotViewModel(Plot plot, ICropConfigStore config)
    {
        _plot = plot;
        _config = config;
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
