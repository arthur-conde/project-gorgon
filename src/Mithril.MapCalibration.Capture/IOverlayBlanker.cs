using System;
using System.Threading.Tasks;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Blanks the overlay so a capture under it grabs the clean map, not our own
/// chrome (spec §6). <see cref="BlankAsync"/> hides the overlay and returns a
/// disposable that restores it; the shell implementation (Phase 3) hides
/// <c>IOverlayWindow.Window</c> on the dispatcher and waits one render frame
/// before completing, then shows it again on dispose.
/// </summary>
public interface IOverlayBlanker
{
    /// <summary>Hide the overlay; dispose the result to restore it.</summary>
    Task<IAsyncDisposable> BlankAsync();
}
