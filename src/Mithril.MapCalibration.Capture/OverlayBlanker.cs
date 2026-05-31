using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Mithril.Overlay;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Real <see cref="IOverlayBlanker"/> over <see cref="IOverlayWindow.Window"/>:
/// hides the overlay window so a capture under it grabs the clean game map (not
/// our own chrome — spec §6), waits one render frame, and restores it on dispose.
/// All window access is marshalled to the dispatcher. <c>Hide()</c>/<c>Show()</c>
/// are within the interface's allowed surface (only <c>Close</c> / Topmost /
/// WindowStyle / AllowsTransparency mutation / reparenting are forbidden).
///
/// <para>Device-lost / flicker on the D3DImage overlay is real (spec §15) and
/// only observable live (Task 28 manual-verify). WGC is the no-flicker upgrade.</para>
/// </summary>
public sealed class OverlayBlanker : IOverlayBlanker
{
    private readonly IOverlayWindow _overlay;

    public OverlayBlanker(IOverlayWindow overlay) => _overlay = overlay;

    public async Task<IAsyncDisposable> BlankAsync()
    {
        var window = _overlay.Window;
        var dispatcher = window.Dispatcher;

        // Hide on the dispatcher, then wait one render frame so the compositor
        // has actually dropped the overlay before the BitBlt reads the screen.
        await dispatcher.InvokeAsync(() => window.Hide());
        await dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);

        return new Restorer(window);
    }

    private sealed class Restorer : IAsyncDisposable
    {
        private readonly Window _window;
        public Restorer(Window window) => _window = window;

        public async ValueTask DisposeAsync()
        {
            await _window.Dispatcher.InvokeAsync(() => _window.Show());
        }
    }
}
