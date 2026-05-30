namespace Mithril.Tools.MapCalibration.Harness;

/// <summary>
/// Which calibration method produced a <see cref="CalibrationRef"/> /
/// <see cref="CandidateRef"/>. Lets the correction UI badge refs by origin and
/// lets the user reason about a suspect ref's provenance when toggling it out
/// of the solve. Manual clicks carry <see cref="Manual"/> at confidence 1.0.
/// </summary>
public enum CalibrationRefSource
{
    Manual,
    GreenPixel,
    Ncc,
    Bootstrap,
}
