using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Orchestrates capture-under-overlay (spec §6): blank the overlay window for
/// one frame, capture the framed bbox, restore the overlay, then validate before
/// handing a clean <see cref="GrayImage"/> to the solve engine. The
/// <c>await using</c> on the blanker guarantees the overlay is restored on every
/// path — including capture failure and validation rejection.
/// </summary>
public sealed class CaptureService : ICaptureService
{
    private readonly IScreenCapture _capture;
    private readonly IOverlayBlanker _blanker;
    private readonly CaptureValidation _validation;
    private readonly ILogger? _logger;

    public CaptureService(
        IScreenCapture capture,
        IOverlayBlanker blanker,
        CaptureValidation validation,
        ILogger? logger)
    {
        _capture = capture;
        _blanker = blanker;
        _validation = validation;
        _logger = logger;
    }

    public async Task<GrayImage?> CaptureMapAsync(CaptureRect bbox, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // The await using restores the overlay no matter how we leave the block.
        await using (await _blanker.BlankAsync().ConfigureAwait(false))
        {
            CapturedFrame? frame = _capture.Capture(bbox);
            if (frame is null)
            {
                _logger?.LogWarning("Map capture produced no frame for bbox {Width}x{Height} at ({X},{Y})",
                    bbox.Width, bbox.Height, bbox.X, bbox.Y);
                return null;
            }

            if (!_validation.Validate(frame, bbox, out var reason))
            {
                _logger?.LogWarning("Map capture rejected: {Reason}", reason);
                return null;
            }

            return frame.ToGray();
        }
    }
}
