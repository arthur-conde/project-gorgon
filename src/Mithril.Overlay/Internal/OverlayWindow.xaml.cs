using System.Windows;
using System.Windows.Input;

namespace Mithril.Overlay.Internal;

/// <summary>
/// The shared overlay window. Minimal chrome &#8212; backdrop, drag-via-header,
/// status chip, hosts a <see cref="D2DOverlaySurface"/>. Legolas-specific
/// input handling (drag survey pin, calibration phase capture, nudge pad,
/// validate-calibration banner, zoom strip) stays in Legolas and is attached
/// post-construction by the Legolas-side input controller once the migration
/// PRs land.
///
/// <para>The XAML's <c>x:Class</c> requires this hand-authored code-behind
/// calling <see cref="InitializeComponent"/>; per <c>docs/wpf-gotchas.md</c>
/// the absence of a code-behind would yield a silent blank window that
/// builds + tests green.</para>
/// </summary>
public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
    }

    /// <summary>The Direct2D surface inside the window. Internal so the
    /// hosting <see cref="OverlayWindowService"/> can hook <c>Render</c>
    /// without leaking the surface type onto the public
    /// <see cref="IOverlayWindow"/> contract.</summary>
    internal D2DOverlaySurface OverlaySurface => Surface;

    private void HeaderChrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Header drag — matches MapOverlayView's drag-the-window-by-header
        // behaviour so an overlay user can reposition a frameless window.
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { /* not a left-button-down event after all */ }
        }
    }
}
