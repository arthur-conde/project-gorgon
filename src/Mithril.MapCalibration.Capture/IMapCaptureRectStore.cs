namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Persisted store for the single map-capture bbox (#947), expressed as an
/// absolute virtual-desktop rectangle in <b>physical desktop pixels</b> — the
/// exact frame <c>BitBltScreenCapture</c> blits from <c>GetDC(NULL)</c> (origin at
/// the primary monitor, signed coords possible on a multi-monitor layout where a
/// secondary screen sits left/above the primary).
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
/// <para><b>Why physical pixels (not DIUs).</b> The rect is computed once, at
/// snip-confirm time, from the snip window's own live <c>TransformToDevice</c>
/// scale (see <see cref="RegionSnipWindow"/>). The snip is a <i>single</i> WPF
/// window spanning the whole virtual desktop; under PerMonitorV2 a single top-level
/// window has ONE DPI scale and maps its entire logical surface uniformly at that
/// scale (WPF does not per-monitor-rescale <c>GetPosition</c> within one window), so
/// the snipped rect's physical pixels are <c>DIU · S_snip</c> uniformly. Persisting
/// the already-resolved physical rect makes the read path frame-independent: the
/// provider returns it verbatim with zero read-time DPI work — no monitor
/// enumeration, no per-monitor affine map. This is correct for single-monitor and
/// uniform-DPI multi-monitor layouts; a true mixed-DPI multi-monitor layout (the
/// map sitting on a non-primary monitor at a different scale than the snip window's)
/// is owed to #938 manual-verify, and a stored physical rect goes stale if the user
/// later changes DPI/resolution (re-snip to refresh). This is no worse than the
/// pre-#940 read-time behavior, which also keyed off a single window's scale.</para>
///
/// <para><b>Fail-soft.</b> <see cref="Get"/> returns <see langword="null"/> when no
/// rect has ever been snipped — the legitimate "no bbox set" state the engine
/// surfaces as "no map bbox set". Implementations never throw into the
/// engine/host.</para>
/// </summary>
public interface IMapCaptureRectStore
{
    /// <summary>The persisted capture rect in absolute virtual-desktop physical
    /// pixels, or <see langword="null"/> when never snipped (fail-soft).</summary>
    CaptureRect? Get();

    /// <summary>Persist <paramref name="rect"/> (absolute virtual-desktop physical
    /// pixels) and flush to disk immediately, so the region survives regardless of
    /// window state.</summary>
    void Set(CaptureRect rect);
}
