using Mithril.MapCalibration.Detection;
using Mithril.Shared.Game;

namespace Mithril.MapCalibration.Capture.DependencyInjection;

/// <summary>
/// DI composition for the map auto-capture pipeline (mithril#914 PR-2). The full
/// <c>AddMithrilMapCalibrationCapture</c> registration lands in Task 28; the
/// <see cref="BuildConfidenceGate"/> factory is shared with the unit test so the
/// GameConfig threshold wiring is CI-locked.
/// </summary>
public static partial class CaptureServiceCollectionExtensions
{
    /// <summary>
    /// Build the auto path's <see cref="ICalibrationConfidenceGate"/> honouring
    /// <see cref="GameConfig.CalibrationGoodResidualPx"/> (the SAME user-tunable
    /// threshold the manual path uses — PR-0 relocated it to GameConfig; spec §9),
    /// with the shipped <see cref="CalibrationConfidenceGate.DefaultInlierFloor"/>.
    /// </summary>
    internal static ICalibrationConfidenceGate BuildConfidenceGate(GameConfig cfg) =>
        new CalibrationConfidenceGate(cfg.CalibrationGoodResidualPx, CalibrationConfidenceGate.DefaultInlierFloor);
}
