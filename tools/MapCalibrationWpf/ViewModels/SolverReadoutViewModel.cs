namespace Mithril.Tools.MapCalibrationWpf.ViewModels;

using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Mithril.MapCalibration;

/// <summary>
/// Observable wrapper around the workspace's current <see cref="AreaCalibration"/>.
/// Exposes display-friendly fields + a coloured badge brush keyed on the
/// 3 / 12 px residual thresholds defined in Mithril's LegolasSettings
/// (CalibrationGoodResidualPx = 12.0). The thresholds are inlined here rather
/// than referenced from Legolas — this is a dev-only tool and the constant
/// hasn't churned in months. Reference: src/Legolas.Module/Domain/LegolasSettings.cs.
/// </summary>
public sealed partial class SolverReadoutViewModel : ObservableObject
{
    /// <summary>RMS residual at which the calibration is "ship-quality".</summary>
    public const double GoodResidualPx = 3.0;

    /// <summary>RMS residual ceiling for an "acceptable" fit.</summary>
    public const double AcceptableResidualPx = 12.0;

    [ObservableProperty]
    private AreaCalibration? _calibration;

    partial void OnCalibrationChanged(AreaCalibration? value)
    {
        OnPropertyChanged(nameof(Scale));
        OnPropertyChanged(nameof(RotationDegrees));
        OnPropertyChanged(nameof(MirrorNorth));
        OnPropertyChanged(nameof(OriginX));
        OnPropertyChanged(nameof(OriginY));
        OnPropertyChanged(nameof(ResidualPixels));
        OnPropertyChanged(nameof(ReferenceCount));
        OnPropertyChanged(nameof(ResidualBadgeBrush));
        OnPropertyChanged(nameof(StatusLabel));
    }

    public double? Scale => Calibration?.Scale;
    public double? RotationDegrees => Calibration is { } c ? c.RotationRadians * 180.0 / Math.PI : null;
    public bool MirrorNorth => Calibration?.MirrorNorth ?? false;
    public double? OriginX => Calibration?.OriginX;
    public double? OriginY => Calibration?.OriginY;
    public double? ResidualPixels => Calibration?.ResidualPixels;
    public int ReferenceCount => Calibration?.ReferenceCount ?? 0;

    public Brush ResidualBadgeBrush => Calibration?.ResidualPixels switch
    {
        null => Brushes.Gray,
        <= GoodResidualPx => Brushes.LimeGreen,
        <= AcceptableResidualPx => Brushes.Goldenrod,
        _ => Brushes.IndianRed,
    };

    public string StatusLabel => Calibration?.ResidualPixels switch
    {
        null => "no solve",
        <= GoodResidualPx => "ship-quality",
        <= AcceptableResidualPx => "acceptable",
        _ => "below shipping bar",
    };
}
