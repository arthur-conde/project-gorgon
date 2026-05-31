namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Resolves the foreground game window's client rect in desktop pixels, so the
/// capture layer knows where the game is drawing. Returns <see langword="null"/>
/// when the foreground window does not belong to the configured game process
/// (<c>GameConfig.GameProcessName</c>).
/// </summary>
public interface IGameWindowLocator
{
    /// <summary>
    /// Resolve the foreground game window's client rect in DESKTOP pixels, or
    /// <see langword="null"/> when the foreground window doesn't belong to the
    /// configured game process. Mirrors <c>ForegroundFocusGate</c>'s
    /// case-insensitive substring match (#919) — but returns the rect, which
    /// <c>ForegroundFocusGate</c> (<c>IsInApp</c> only) does not expose, so this
    /// is new code.
    /// </summary>
    GameWindow? Locate();
}

/// <summary>The located game window handle + its client rect in desktop pixels.</summary>
public readonly record struct GameWindow(nint Hwnd, CaptureRect ClientRectDesktop);
