using System.Threading;
using System.Threading.Tasks;
using Mithril.Overlay;
using Mithril.Shared.Hotkeys;

namespace Mithril.MapCalibration.Capture.Hotkeys;

/// <summary>
/// "Capture &amp; calibrate the current map" hotkey (spec §10). Runs one
/// <see cref="IAutoCalibrationRunner.TryCalibrateCurrentAreaAsync"/> attempt and
/// surfaces the outcome on the overlay status chip — this is the user-initiated
/// path where feedback matters most: on reject the actionable reason
/// (<see cref="CalibrationStatusFormatter"/>); on success the chip is cleared.
/// <see cref="RespectsFocusGate"/> is <see langword="true"/>: it must fire only
/// with Project Gorgon focused, so the capture reads the game's framebuffer (not
/// Mithril's or another app's). No default binding (Legolas convention —
/// game-key collision avoidance).
/// </summary>
public sealed class CaptureCalibrateCommand : IHotkeyCommand
{
    private readonly IAutoCalibrationRunner _runner;
    private readonly IOverlayWindow _overlay;

    public CaptureCalibrateCommand(IAutoCalibrationRunner runner, IOverlayWindow overlay)
    {
        _runner = runner;
        _overlay = overlay;
    }

    public string Id => "mapcalibration.capture";
    public string DisplayName => "Capture & Calibrate Map";
    public string? Category => "Map Calibration";
    public HotkeyBinding? DefaultBinding => null;
    public bool RespectsFocusGate => true;

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var outcome = await _runner.TryCalibrateCurrentAreaAsync(cancellationToken).ConfigureAwait(false);
        // Always surface the outcome on the manual path. ForOutcome maps a
        // persisted success to null (clear the chip) and a reject to the
        // actionable reason string.
        _overlay.SetStatusMessage(CalibrationStatusFormatter.ForOutcome(outcome));
    }
}
