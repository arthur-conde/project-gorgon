using Legolas.Domain;
using Legolas.Services;
using Mithril.Shared.Reference;

namespace Legolas.Tests;

/// <summary>
/// Minimal <see cref="IAreaCalibrationService"/> double for the #476 Survey
/// player-GPS tests. Only <see cref="CurrentCalibration"/> + <see cref="Changed"/>
/// are exercised (those are the inputs <see cref="Legolas.ViewModels.MapOverlayViewModel"/>
/// reads to project the tracker fix); everything else is an inert stub.
/// </summary>
public sealed class FakeAreaCalibrationService : IAreaCalibrationService
{
    private AreaCalibration? _calibration;

    /// <summary>Set the calibration and raise <see cref="Changed"/> (mimics a
    /// calibrated area being applied on entry).</summary>
    public void SetCalibration(AreaCalibration? calibration)
    {
        _calibration = calibration;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public AreaCalibration? CurrentCalibration => _calibration;
    public bool IsCurrentAreaCalibrated => _calibration is not null;
    public string? CurrentAreaKey => _calibration is null ? null : "AreaTest";
    public string? CurrentAreaFriendlyName => _calibration is null ? null : "Test Area";
    public IReadOnlyList<CalibrationReference> CurrentAreaReferences => Array.Empty<CalibrationReference>();
    public IReadOnlyList<AreaEntry> AllAreas => Array.Empty<AreaEntry>();

    public event EventHandler? Changed;
    public event EventHandler<CalibrationSurveyObservation>? SurveyObserved;

    public void OnAreaEntered(string areaFriendlyName) { }
    public void SelectArea(string areaKey) { }
    public AreaCalibration? CalibrateCurrentArea(
        IReadOnlyList<(WorldCoord World, PixelPoint Pixel)> placements, double calibrationZoom = 1.0) => null;
    public void ClearCurrentAreaCalibration() { }
    public void NoteSurvey(string name, MetreOffset offset) => SurveyObserved?.Invoke(this, new(name, offset));
}
