using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using Mithril.Overlay;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// <see cref="IMapCaptureRegionProvider"/> backed by the <b>live overlay window
/// bounds</b> — there is no separately-persisted rect (#940 one-rect model, spec
/// §7). The overlay window's desktop rect IS the capture region AND the
/// calibration frame; persistence is handled entirely by the overlay's existing
/// <c>WindowLayoutBinder.Bind</c> wiring (Left/Top on LocationChanged,
/// Width/Height on SizeChanged → <c>LegolasSettings.MapOverlay</c>), so this
/// provider neither reads nor writes settings.
///
/// <para><see cref="Current"/> reads <see cref="Window.Left"/>/<see cref="Window.Top"/>/
/// <see cref="FrameworkElement.ActualWidth"/>/<see cref="FrameworkElement.ActualHeight"/>
/// and converts them to <b>physical desktop pixels</b> via the window's
/// per-monitor DPI (<see cref="PresentationSource.FromVisual"/> →
/// <see cref="System.Windows.Media.CompositionTarget.TransformToDevice"/>), so the
/// returned rect matches what <c>BitBltScreenCapture</c> blits from
/// <c>GetDC(NULL)</c> exactly. The pixel math lives in the pure, unit-tested
/// <see cref="CaptureRectMath.DiuToPhysical"/> helper.</para>
///
/// <para><b>Fail-soft.</b> When the overlay surface isn't available yet (no
/// <see cref="PresentationSource"/>, window not shown, zero size) <see cref="Current"/>
/// returns <see langword="null"/> — never throws. The trigger reads
/// <see cref="Current"/> from a thread-pool thread, so the WPF property access is
/// marshalled to the window's dispatcher.</para>
/// </summary>
public sealed class MapCaptureRegionProvider : IMapCaptureRegionProvider
{
    private readonly IOverlayWindow _overlay;
    private readonly ILogger? _logger;

    public MapCaptureRegionProvider(IOverlayWindow overlay, ILogger? logger = null)
    {
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        _logger = logger;
    }

    public CaptureRect? Current
    {
        get
        {
            var window = _overlay.Window;
            var dispatcher = window.Dispatcher;
            try
            {
                // Touching WPF window properties must happen on the UI thread; the
                // trigger reads Current from a thread-pool thread. Invoke (not
                // InvokeAsync) so the caller gets the value synchronously, matching
                // the property contract.
                if (dispatcher.CheckAccess())
                    return ResolveOnDispatcher(window);
                return dispatcher.Invoke(() => ResolveOnDispatcher(window));
            }
            catch (Exception ex) when (ex is OperationCanceledException
                                       or TaskCanceledException
                                       or InvalidOperationException)
            {
                // Expected dispatcher-teardown race: the overlay window / dispatcher
                // is shutting down mid-read (Invoke throws once the dispatcher has
                // begun shutdown). Fail soft (the trigger/engine treats null as
                // "no region framed yet") but don't swallow silently — log at Trace
                // since this is an expected, noisy-during-shutdown condition.
                _logger?.LogTrace(ex, "Overlay dispatcher unavailable while reading capture region; treating region as unset.");
                return null;
            }
        }
    }

    /// <summary>
    /// Resolve the live overlay rect on the dispatcher thread. Returns
    /// <see langword="null"/> (fail-soft) when the surface isn't ready.
    /// </summary>
    private static CaptureRect? ResolveOnDispatcher(Window window)
    {
        // No HwndSource yet (window never shown / already closed) → no rect.
        if (PresentationSource.FromVisual(window) is not HwndSource source
            || source.CompositionTarget is null)
        {
            return null;
        }

        double left = window.Left;
        double top = window.Top;
        double width = window.ActualWidth;
        double height = window.ActualHeight;

        if (double.IsNaN(left) || double.IsNaN(top) || width <= 0 || height <= 0)
            return null;

        Matrix toDevice = source.CompositionTarget.TransformToDevice;
        var rect = CaptureRectMath.DiuToPhysical(left, top, width, height, toDevice.M11, toDevice.M22);
        return rect.IsEmpty ? null : rect;
    }
}
