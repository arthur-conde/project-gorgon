using System.ComponentModel;
using System.Windows;

namespace Mithril.Overlay;

/// <summary>
/// Shared overlay window surface. The overlay's <see cref="Window"/>
/// lifetime is owned by the hosting service; consumers may attach
/// Legolas-specific input handlers (drag survey pin, calibration phase
/// capture, etc.) to it and bind chrome elements to the status surface
/// exposed via <see cref="INotifyPropertyChanged"/>.
///
/// <para>The interface deliberately does <b>not</b> extend
/// <see cref="IDisposable"/>: the singleton lifetime is the host's
/// responsibility (<see cref="Microsoft.Extensions.Hosting.IHostedService.StopAsync"/>
/// on the backing service), and a consumer who resolved this interface and
/// disposed it would tear down the shared overlay for every other consumer.
/// The concrete implementation still implements <see cref="IDisposable"/>
/// for the host's benefit; just don't reach for it through this contract.</para>
///
/// <para><b>Notification properties.</b>
/// <list type="bullet">
/// <item><see cref="IsReady"/> &#8212; <see langword="true"/> while the D3D
/// surface is alive and ready to render. Flipping to <see langword="false"/>
/// after a device-lost event lets consumers hide chrome that depends on the
/// surface being live.</item>
/// <item><see cref="StatusMessage"/> &#8212; user-visible status chip text
/// (e.g. <c>"map not calibrated &#8212; use Legolas wizard"</c> for the
/// uncalibrated-area case). Empty / null when the surface is in its happy
/// state.</item>
/// </list></para>
/// </summary>
public interface IOverlayWindow : INotifyPropertyChanged
{
    /// <summary>The overlay's WPF window. Consumers may attach input
    /// handlers and bind chrome to it; lifetime is owned by the overlay
    /// service.
    ///
    /// <para><b>Allowed</b>: attach input handlers (<c>PreviewKeyDown</c>,
    /// <c>MouseLeftButtonDown</c>, etc.); add child elements / DataTemplates
    /// to the window's content tree; data-bind read-only properties of the
    /// window (e.g. <c>ActualWidth</c>); call <see cref="Window.Show"/> the
    /// first time the overlay becomes user-visible.</para>
    ///
    /// <para><b>Forbidden</b>: <see cref="Window.Close"/> (the host owns
    /// teardown); mutation of <see cref="Window.Topmost"/> /
    /// <see cref="Window.WindowStyle"/> /
    /// <see cref="Window.AllowsTransparency"/> (those are the click-through /
    /// composition invariants from <c>docs/legolas-overview.md</c> &#167;Pitfalls);
    /// re-parenting the window or wrapping it in a new
    /// <see cref="System.Windows.Interop.HwndSource"/>.</para>
    ///
    /// <para>A narrower per-consumer attach surface (e.g.
    /// <c>InputSurface</c> + <c>HeaderContent</c>) is queued as a follow-up
    /// once Gwaihir (second consumer) lands and the attach pressure is
    /// real &#8212; for v1 the raw <see cref="Window"/> matches the issue
    /// spec's "consumers may bind chrome" wording.</para>
    /// </summary>
    Window Window { get; }

    /// <summary>True while the underlying D3D surface is alive and ready to
    /// render. Raises <see cref="INotifyPropertyChanged.PropertyChanged"/>
    /// when the state flips so consumers can react to device-lost without
    /// polling.</summary>
    bool IsReady { get; }

    /// <summary>
    /// Consumer-agnostic status surfaced as the overlay's header chip
    /// (e.g. <c>"map not calibrated &#8212; use Legolas wizard"</c>).
    /// Empty / null when the surface is healthy. Raises
    /// <see cref="INotifyPropertyChanged.PropertyChanged"/> when it
    /// changes so the chip can update without polling.
    /// </summary>
    string? StatusMessage { get; }
}
