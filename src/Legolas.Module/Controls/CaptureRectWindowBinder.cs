using System;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration;

namespace Legolas.Controls;

/// <summary>
/// #957 one-rect binder. Positions a WPF window from the shell-persisted map-capture
/// rect (<see cref="IMapCaptureRectStore"/>, physical px) and writes the window's
/// drags/resizes back to the same store, so the overlay frame and the BitBlt capture
/// frame are a single source of truth (spec §7 / #940). Replaces
/// <see cref="WindowLayoutBinder"/> for the survey map overlay; the latter stays for
/// the inventory/calibration overlays, which keep their Legolas-owned
/// <c>WindowLayout</c>.
///
/// <para><b>DIU ↔ physical.</b> The store holds <i>physical</i> desktop pixels;
/// <c>Window.Left/Top/Width/Height</c> are <i>DIUs</i>. Conversion uses the window's
/// own live <c>TransformToDevice</c> at apply/write time (<see cref="CaptureRectMath"/>),
/// the same single-window-scale model the snip uses — exact for single-/uniform-DPI,
/// #938 best-effort for mixed-DPI multi-monitor.</para>
///
/// <para><b>No feedback loop, no write storms.</b> Pushing the store value into the
/// window is fenced by <see cref="BinderState._applying"/> so it doesn't bounce back
/// as a write; write-back is debounced (a drag fires <c>LocationChanged</c> per tick,
/// and the store flushes to disk synchronously) and additionally skipped when the
/// computed rect already equals the stored one — which also absorbs WPF's deferred
/// <c>SizeChanged</c> after a programmatic resize.</para>
/// </summary>
public static class CaptureRectWindowBinder
{
    // ShellMapCaptureRectStore.Set flushes to disk synchronously; coalesce a drag's
    // burst of LocationChanged/SizeChanged into one write after the gesture settles.
    private static readonly TimeSpan WritebackDebounce = TimeSpan.FromMilliseconds(400);

    public static void Bind(Window window, IMapCaptureRectStore store, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(store);

        var state = new BinderState(window, store, logger);

        // Conversion needs the window's device transform, which exists only once the
        // HwndSource is up. Apply now if it already is; otherwise defer to realize.
        if (System.Windows.PresentationSource.FromVisual(window) is not null)
            state.ApplyFromStore();
        else
            window.SourceInitialized += (_, _) => state.ApplyFromStore();

        window.LocationChanged += (_, _) => state.OnWindowChanged();
        window.SizeChanged += (_, _) => state.OnWindowChanged();
    }

    private sealed class BinderState
    {
        private readonly Window _window;
        private readonly IMapCaptureRectStore _store;
        private readonly ILogger? _logger;
        private readonly DispatcherTimer _writebackTimer;
        // True while we push the stored rect INTO the window, so the synchronous
        // LocationChanged/SizeChanged don't immediately schedule a write-back.
        private bool _applying;

        public BinderState(Window window, IMapCaptureRectStore store, ILogger? logger)
        {
            _window = window;
            _store = store;
            _logger = logger;
            _writebackTimer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
            {
                Interval = WritebackDebounce,
            };
            _writebackTimer.Tick += (_, _) => { _writebackTimer.Stop(); WriteBack(); };
        }

        /// <summary>Position the window from the stored physical rect. No-op when the
        /// store is unset (window keeps its XAML default) or the transform isn't live.</summary>
        public void ApplyFromStore()
        {
            CaptureRect? stored;
            try { stored = _store.Get(); }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Reading the capture rect to position the overlay failed; leaving the window at its current place.");
                return;
            }
            if (stored is not { } rect || rect.IsEmpty) return;

            if (!TryGetScale(out var sx, out var sy)) return;
            var (left, top, width, height) = CaptureRectMath.PhysicalToDiu(rect, sx, sy);
            if (width <= 0 || height <= 0) return;

            _applying = true;
            try
            {
                _window.Left = left;
                _window.Top = top;
                _window.Width = width;
                _window.Height = height;
            }
            finally
            {
                _applying = false;
            }
        }

        public void OnWindowChanged()
        {
            if (_applying) return;                                   // our own ApplyFromStore
            if (_window.WindowState != WindowState.Normal) return;   // min/maximised are ephemeral
            // Debounce: the last change of a drag/resize gesture wins.
            _writebackTimer.Stop();
            _writebackTimer.Start();
        }

        /// <summary>Persist the window's current frame as physical px — skipping the
        /// disk write when nothing actually changed (also absorbs the deferred
        /// SizeChanged WPF raises after <see cref="ApplyFromStore"/>'s programmatic
        /// resize, since that recomputes the already-stored rect).</summary>
        private void WriteBack()
        {
            if (_window.WindowState != WindowState.Normal) return;
            if (!TryGetScale(out var sx, out var sy)) return;

            var physical = CaptureRectMath.DiuToPhysical(
                _window.Left, _window.Top, _window.ActualWidth, _window.ActualHeight, sx, sy);
            if (physical.IsEmpty) return;

            try
            {
                if (_store.Get() == physical) return; // unchanged — no redundant flush
                _store.Set(physical);
            }
            catch (Exception ex)
            {
                // Persisting the overlay position is best-effort: this runs on the UI
                // dispatcher (a window event), so a settings-IO failure must not throw
                // into the message loop and crash the overlay (#957 fail-soft). The
                // in-memory window position is unaffected; the next gesture retries.
                _logger?.LogWarning(ex, "Persisting the overlay capture rect failed; the position applies to this session only.");
            }
        }

        private bool TryGetScale(out double scaleX, out double scaleY)
        {
            scaleX = scaleY = 0;
            var source = System.Windows.PresentationSource.FromVisual(_window);
            var m = source?.CompositionTarget?.TransformToDevice;
            if (m is not { } t || t.M11 <= 0 || t.M22 <= 0) return false;
            scaleX = t.M11;
            scaleY = t.M22;
            return true;
        }
    }
}
