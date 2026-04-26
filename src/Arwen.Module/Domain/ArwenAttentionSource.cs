using Mithril.Shared.Modules;

namespace Arwen.Domain;

/// <summary>
/// Adapts <see cref="CalibrationService.PendingChanged"/> /
/// <see cref="CalibrationService.PendingObservations"/> to the shell's
/// attention surface. Stackable gifts whose quantity couldn't be inferred
/// land in the pending bucket; the user is expected to confirm the count,
/// so they need to know one is waiting even when Arwen isn't the active tab.
/// </summary>
internal sealed class ArwenAttentionSource : IAttentionSource
{
    private readonly CalibrationService _calibration;

    public ArwenAttentionSource(CalibrationService calibration)
    {
        _calibration = calibration;
        _calibration.PendingChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public string ModuleId => "arwen";
    public string DisplayLabel => "Arwen — gifts awaiting confirmation";
    public int Count => _calibration.PendingObservations.Count;

    public event EventHandler? Changed;
}
