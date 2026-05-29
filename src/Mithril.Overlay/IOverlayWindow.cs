using System.ComponentModel;
using System.Windows;

namespace Mithril.Overlay;

/// <summary>
/// Shared overlay window surface. The overlay's <see cref="Window"/>
/// lifetime is owned by the hosting service (consumers do not dispose it
/// directly) but consumers may attach Legolas-specific input handlers (drag
/// survey pin, calibration phase capture, etc.) to it and bind chrome
/// elements to the status surface exposed via <see cref="INotifyPropertyChanged"/>.
///
/// <para>The <see cref="IDisposable"/> contract is here for symmetry with
/// hosted-service shutdown plumbing &#8212; production code resolves the
/// service-owned instance and lets the host tear it down.</para>
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
public interface IOverlayWindow : IDisposable, INotifyPropertyChanged
{
    /// <summary>The overlay's WPF window. Consumers may attach input
    /// handlers and bind chrome to it; lifetime is owned by the overlay
    /// service.</summary>
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
