namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Captures a desktop-pixel rectangle to a raw BGRA frame. Implementations read
/// the OS framebuffer (the same path a manual screenshot takes — not injection,
/// memory reads, or hooks) and never throw into the caller: a failed capture
/// returns <see langword="null"/>.
/// </summary>
public interface IScreenCapture
{
    /// <summary>
    /// Capture <paramref name="rect"/> from the desktop, or <see langword="null"/>
    /// on any failure (resource allocation, BitBlt, GetDIBits).
    /// </summary>
    CapturedFrame? Capture(CaptureRect rect);
}
