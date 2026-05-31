using System.Threading;
using System.Threading.Tasks;
using Mithril.Shared.Hotkeys;

namespace Mithril.MapCalibration.Capture.Hotkeys;

/// <summary>
/// "Capture &amp; calibrate the current map" hotkey (spec §10). Runs one
/// <see cref="IAutoCalibrationRunner.TryCalibrateCurrentAreaAsync"/> attempt.
/// <see cref="RespectsFocusGate"/> is <see langword="true"/>: it must fire only
/// with Project Gorgon focused, so the capture reads the game's framebuffer (not
/// Mithril's or another app's). No default binding (Legolas convention —
/// game-key collision avoidance).
/// </summary>
public sealed class CaptureCalibrateCommand : IHotkeyCommand
{
    private readonly IAutoCalibrationRunner _runner;

    public CaptureCalibrateCommand(IAutoCalibrationRunner runner) => _runner = runner;

    public string Id => "mapcalibration.capture";
    public string DisplayName => "Capture & Calibrate Map";
    public string? Category => "Map Calibration";
    public HotkeyBinding? DefaultBinding => null;
    public bool RespectsFocusGate => true;

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await _runner.TryCalibrateCurrentAreaAsync(cancellationToken).ConfigureAwait(false);
    }
}
