using System.Threading;
using System.Threading.Tasks;
using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Captures the framed map region under the blanked overlay and validates it,
/// handing a clean <see cref="GrayImage"/> to the solve engine.
/// </summary>
public interface ICaptureService
{
    /// <summary>
    /// Blank the overlay, capture <paramref name="bbox"/>, restore the overlay,
    /// validate the frame, and return it as gray. <see langword="null"/> on any
    /// failure (capture failed, wrong size, black frame).
    /// </summary>
    Task<GrayImage?> CaptureMapAsync(CaptureRect bbox, CancellationToken ct);
}
