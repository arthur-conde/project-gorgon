using System.Threading;
using System.Threading.Tasks;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// The "run one auto-calibration attempt for the current area" capability, split
/// from the concrete <see cref="AutoCalibrationEngine"/> so the hotkey + trigger
/// depend on a narrow seam (testable with a spy that needs no capture/solve
/// dependencies).
/// </summary>
public interface IAutoCalibrationRunner
{
    Task<AutoCalibrationOutcome> TryCalibrateCurrentAreaAsync(CancellationToken ct);
}
