namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Persisted store for the single map-capture bbox (#947), expressed as an
/// absolute virtual-desktop rectangle in <b>device-independent units (DIUs)</b> —
/// the same frame WPF <see cref="System.Windows.Window.Left"/>/<c>Top</c> and
/// <see cref="SnipRectMath.ToVirtualDesktop"/> use.
///
/// <para><b>Why a SHELL-owned store, not the overlay window.</b> Before #947 the
/// capture region was derived from the live overlay window's realized geometry
/// (<see cref="System.Windows.PresentationSource"/> + <c>Window.Left/Top</c>), so
/// it read <see langword="null"/> whenever the overlay wasn't shown — even though
/// <c>BitBlt</c> capture (with the overlay blanked) never needs the overlay shown.
/// The capture region is a <i>persisted desktop rectangle</i> independent of any
/// window; this seam lifts it into the shell's own settings store so it survives
/// regardless of window state. The shell backs it (it references both Legolas and
/// this Capture project), keeping the <c>Capture ↛ Legolas.Module</c> boundary
/// intact.</para>
///
/// <para><b>Fail-soft.</b> <see cref="Get"/> returns <see langword="null"/> when no
/// rect has ever been snipped — the legitimate "no bbox set" state the engine
/// surfaces as "no map bbox set". Implementations never throw into the
/// engine/host.</para>
/// </summary>
public interface IMapCaptureRectStore
{
    /// <summary>The persisted capture rect in absolute virtual-desktop DIUs, or
    /// <see langword="null"/> when never snipped (fail-soft).</summary>
    MapCaptureRectDiu? Get();

    /// <summary>Persist <paramref name="rect"/> (absolute virtual-desktop DIUs) and
    /// flush to disk immediately, so the region survives regardless of window
    /// state.</summary>
    void Set(MapCaptureRectDiu rect);
}

/// <summary>
/// An absolute virtual-desktop rectangle in device-independent units (DIUs;
/// 1 DIU = 1/96"). Plain BCL doubles so the seam carries no WPF dependency. The
/// origin is signed (negative on a secondary monitor left/above the primary),
/// matching <c>Window.Left/Top</c>.
/// </summary>
public readonly record struct MapCaptureRectDiu(double Left, double Top, double Width, double Height);
