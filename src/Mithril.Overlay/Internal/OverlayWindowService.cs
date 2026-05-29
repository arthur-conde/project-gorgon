using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Arda.World.Player;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration;
using Mithril.Shared.Diagnostics.Telemetry;

namespace Mithril.Overlay.Internal;

/// <summary>
/// Hosted-service owner of the shared overlay window + per-tick projection
/// driver. Implements <see cref="IOverlayWindow"/> &#8212; one instance,
/// surfaced under both contracts via DI.
///
/// <para><b>Lifetime.</b> Constructed via DI when the host starts.
/// <see cref="StartAsync"/> is intentionally cheap &#8212; it does not
/// <c>Show()</c> the window. The window is materialised lazily on first
/// <see cref="Window"/> access so the scaffold can ship "registered but
/// dormant"; the migration PRs that wire Legolas's drawers will be the ones
/// to actually surface the overlay. The perf-floor commitment ("zero impact
/// when not enabled") matters once consumers attach &#8212; the scaffold
/// pays only the cost of a null lifecycle field.</para>
///
/// <para><b>Threading.</b> Window/surface construction must happen on the
/// dispatcher; the projection callback runs on the dispatcher (it's the
/// <see cref="D2DOverlaySurface.Render"/> handler). All marker registry
/// reads happen there. <see cref="Dispose"/> may run on a non-UI thread
/// (host shutdown) and marshals window + brush-cache disposal back onto the
/// dispatcher (the cache's brushes are dispatcher-affined via the bound
/// render target).</para>
///
/// <para><b>Projection driver (Decision C from closed #832).</b>
/// Per tick:
/// <list type="number">
/// <item>read <c>IAreaState.CurrentArea</c> &#8211; the area key</item>
/// <item>set <see cref="WorldOverlayMarkers.CurrentArea"/> so the snapshot
/// filter matches</item>
/// <item>for each current-area marker, call
/// <see cref="IMapCalibrationService.WorldToWindow"/> with <c>currentZoom = 1.0</c>
/// (TODO(#835 migration steps): the Legolas zoom slider wires through in
/// Migration step 3 when Survey switches over)</item>
/// <item>hand the projected <c>(pixel, style)</c> list to
/// <see cref="MarkerSceneRenderer.Render"/></item>
/// </list>
/// For uncalibrated areas (<c>IsCalibrated(areaKey) == false</c> or
/// <c>WorldToWindow</c> returning null) the renderer is skipped and
/// <see cref="StatusMessage"/> flips to the "not calibrated" chip text.
/// The chip surfaces via <see cref="INotifyPropertyChanged"/> so XAML bindings
/// update without polling.</para>
/// </summary>
internal sealed class OverlayWindowService : IHostedService, IOverlayWindow, IDisposable
{
    private const string UncalibratedMessage = "map not calibrated — use Legolas wizard";

    // Inject the concrete type directly (not IWorldOverlayMarkers): the
    // projection driver needs the internal CurrentArea setter and the
    // concrete type is registered as a singleton ahead of the interface.
    // Avoids a brittle down-cast hazard if a future caller overrides the
    // IWorldOverlayMarkers registration with a fake.
    private readonly WorldOverlayMarkers _markers;
    private readonly MarkerSceneRenderer _renderer;
    private readonly IMapCalibrationService _calibration;
    private readonly IAreaState _areaState;
    private readonly IPositionState _positionState; // reserved for future consumers; ensures the DI shape matches Decision C
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger? _logger;
    private readonly D2DBrushCache _brushCache = new();

    private OverlayWindow? _window;
    private Dispatcher? _dispatcher;
    private bool _isReady;
    private string? _statusMessage;
    private bool _firstFrameLogged;
    private string? _lastSeenUncalibratedArea;
    // ConcurrentDictionary mirrors the MarkerSceneRenderer._missingDrawerLogged
    // pattern: today this field is touched only from the dispatcher (via
    // OnSurfaceRender → ProjectMarkers), but nothing in the type signature
    // encodes that, and a future cross-thread caller would race silently on a
    // plain HashSet. TryAdd is lock-free, so the per-tick cost stays a hashed
    // lookup.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _projectionMissAreasLogged
        = new(StringComparer.Ordinal);
    private bool _disposed;

    public OverlayWindowService(
        WorldOverlayMarkers markers,
        MarkerSceneRenderer renderer,
        IMapCalibrationService calibration,
        IAreaState areaState,
        IPositionState positionState,
        ILoggerFactory? loggerFactory = null)
    {
        _markers = markers;
        _renderer = renderer;
        _calibration = calibration;
        _areaState = areaState;
        _positionState = positionState;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger("Mithril.Overlay");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Window Window
    {
        get
        {
            EnsureWindow();
            return _window!;
        }
    }

    public bool IsReady => _isReady;

    public string? StatusMessage => _statusMessage;

    /// <summary>Convenience for XAML triggers that need a bool instead of
    /// null-vs-non-null. Mirrors the IsPlayerAnchorStatusVisible idiom in
    /// MapOverlayView.</summary>
    public bool HasStatusMessage => !string.IsNullOrEmpty(_statusMessage);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var act = MithrilActivitySources.Overlay.StartActivity("service.start");
        // Resolve the WPF dispatcher off Application.Current rather than
        // Dispatcher.CurrentDispatcher. The latter creates a *new* dispatcher
        // on the calling thread if none exists — which silently passes in a
        // unit test but means an overlay built off the wrong thread can never
        // marshal back to the UI dispatcher. Mithril.Shell calls
        // host.StartAsync() on the UI thread after `new App()`, so
        // Application.Current.Dispatcher is the authoritative reference.
        _dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException(
            "OverlayWindowService.StartAsync requires an active WPF Application — "
            + "Mithril.Shell calls host.StartAsync on the UI thread after `new App()`. "
            + "If you see this from a test or non-shell host, ensure a WPF dispatcher is available "
            + "(or skip the hosted-service registration and exercise the projection helper directly).");
        _logger?.LogInformation("OverlayWindowService starting (window will be created on first Window-access).");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        using var act = MithrilActivitySources.Overlay.StartActivity("service.stop");
        _logger?.LogInformation("OverlayWindowService stopping.");
        DisposeWindow();
        return Task.CompletedTask;
    }

    private void EnsureWindow()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_window is not null) return;
        if (_dispatcher is null)
            throw new InvalidOperationException(
                "OverlayWindowService.StartAsync must run before Window is accessed (it captures the WPF dispatcher).");

        if (_dispatcher.CheckAccess())
        {
            CreateWindowOnDispatcher();
        }
        else
        {
            _dispatcher.Invoke(CreateWindowOnDispatcher);
        }
    }

    private void CreateWindowOnDispatcher()
    {
        if (_window is not null) return;
        using var act = MithrilActivitySources.Overlay.StartActivity("window.create");
        _window = new OverlayWindow();
        // DataContext = the service itself so the XAML chip bindings
        // ({Binding StatusMessage} / {Binding HasStatusMessage}) resolve to
        // *this* service's INPC-backed properties. The previous
        // {Binding ..., RelativeSource=AncestorType=Window} bound to the
        // OverlayWindow class, which has no StatusMessage — a silent
        // never-fires gotcha exactly of the class docs/wpf-gotchas.md warns
        // about.
        _window.DataContext = this;
        _window.OverlaySurface.Render += OnSurfaceRender;
        // Propagate the logger down so a D3D init failure inside the surface
        // surfaces in the trace instead of going dark.
        _window.OverlaySurface.Logger = _loggerFactory?.CreateLogger("Mithril.Overlay.Surface");
        _window.Closed += OnWindowClosed;
        _logger?.LogInformation("OverlayWindow created (not shown — consumer must Show() to surface it).");
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        SetReady(false);

        // External-close defensive cleanup.
        //
        // The internal teardown path (DisposeWindow → Close closure) detaches
        // this handler *before* calling window.Close(), so reaching this method
        // with _window still non-null means a caller violated the
        // IOverlayWindow.Window remarks ("Forbidden: Window.Close()") and
        // closed the window directly. Without this branch the surface +
        // brush cache would leak until IHostedService.StopAsync eventually
        // fires Dispose.
        //
        // Runs on the dispatcher (Closed fires there); safe to call the same
        // cleanup body the internal closure runs.
        if (_window is not null)
        {
            _logger?.LogWarning(
                "OverlayWindow closed externally — IOverlayWindow.Window.Close() is forbidden per the contract; the host owns teardown. Disposing surface + brush cache as a defensive measure.");
            CleanupAfterClose(_window);
        }
        else
        {
            _logger?.LogInformation("OverlayWindow closed.");
        }
    }

    /// <summary>Shared cleanup body for the close path. Runs on the dispatcher.
    /// Symmetric with the closure inside <see cref="DisposeWindow"/> &#8212;
    /// detach the surface Render handler, dispose the surface, dispose the
    /// brush cache, null out <see cref="_window"/>. <see cref="OnWindowClosed"/>
    /// is detached separately (the internal path detaches before calling
    /// Close; the external path needs the handler intact to reach this method
    /// in the first place, and the detach happens implicitly when
    /// <see cref="_window"/> goes out of scope).</summary>
    private void CleanupAfterClose(OverlayWindow window)
    {
        // Render is the only handler we attached besides Closed itself. Surface
        // detach can race a final render tick (the surface unhooks
        // CompositionTarget.Rendering on Unloaded), but the surface dispose
        // below covers either ordering.
        try { window.OverlaySurface.Render -= OnSurfaceRender; } catch (Exception ex)
        {
            _logger?.LogTrace(ex, "OverlayWindow.OverlaySurface.Render detach threw during close cleanup; surface likely already torn down.");
        }
        try { window.OverlaySurface.Dispose(); }
        catch (ObjectDisposedException) { /* idempotent dispose */ }
        _brushCache.Dispose();
        _window = null;
    }

    private void OnSurfaceRender(object? sender, D2DRenderEventArgs e)
    {
        // Render-hook runs on the dispatcher. Inside a BeginDraw/EndDraw pair
        // (the surface manages that).
        SetReady(true);
        _brushCache.Bind(e.RenderTarget);

        var areaKey = _areaState.CurrentArea;
        _markers.CurrentArea = areaKey;

        if (string.IsNullOrEmpty(areaKey))
        {
            SetStatusMessage(null);
            return;
        }

        // Uncalibrated-area guard. Surface the chip and skip projection — the
        // surface still clears + composites a transparent frame, so the
        // window's chrome (header + status) stays interactive.
        if (!_calibration.IsCalibrated(areaKey))
        {
            if (!string.Equals(_lastSeenUncalibratedArea, areaKey, StringComparison.Ordinal))
            {
                _lastSeenUncalibratedArea = areaKey;
                _logger?.LogInformation(
                    "OverlayWindowService: area {AreaKey} is uncalibrated; surfacing 'not calibrated' chip and skipping projection.",
                    areaKey);
            }
            SetStatusMessage(UncalibratedMessage);
            return;
        }

        // Calibrated area — clear the uncalibrated-area dedup state so a
        // re-entry into the same area later re-logs the "uncalibrated"
        // observation if calibration is lost.
        _lastSeenUncalibratedArea = null;
        SetStatusMessage(null);

        var snapshot = _markers.CurrentAreaMarkers;
        // TODO(#835 step 6): plumb the live in-game zoom (Legolas's
        // SessionState.CurrentMapZoom slider) into the projection driver.
        // Calibration markers registered via WindowToWorld(pixel,
        // EffectiveZoom(_session.CurrentMapZoom, cal)) Legolas-side already
        // depend on the zoom factor; passing 1.0 here means the round-trip
        // (register at slider-zoom Z, project at hardcoded 1.0) lands the
        // marker at the wrong pixel whenever the user has the slider off
        // 1.0. Dormant today (the overlay window isn't shown — see PR
        // #863's "Window not shown in production" architectural note),
        // but step 6 must wire a zoom provider through OverlayWindow
        // Service before the window goes live.
        var projected = ProjectMarkers(snapshot, areaKey, _calibration, currentZoom: 1.0,
            onMiss: this, snapshotCount: snapshot.Count);
        if (!_firstFrameLogged)
        {
            _firstFrameLogged = true;
            _logger?.LogInformation(
                "OverlayWindowService: first frame projected for area {AreaKey} ({Count} markers, {DrawerCount} drawers registered).",
                areaKey, projected.Count, _renderer.DrawerCount);
        }

        // Telemetry: per-tick latency histogram + marker count counter. Both
        // are no-ops when no listener is attached (Meter producer semantics).
        var sw = ValueStopwatch.StartNew();
        using (var renderAct = MithrilActivitySources.Overlay.StartActivity("project"))
        {
            renderAct?.SetTag("area", areaKey);
            renderAct?.SetTag("marker_count", projected.Count);
            _renderer.Render(projected, e.RenderTarget, e.Factory, _brushCache);
        }
        var elapsedMs = sw.GetElapsedMilliseconds();
        MithrilMeters.Overlay.ProjectionLatencyMs.Record(elapsedMs,
            new KeyValuePair<string, object?>("area", areaKey));
        MithrilMeters.Overlay.FrameMarkers.Add(projected.Count,
            new KeyValuePair<string, object?>("area", areaKey));
    }

    /// <summary>Pure projection helper &#8212; takes a snapshot + a calibration
    /// service and returns the projected pixel list. Carved out so tests can
    /// exercise the projection without standing up a D3D surface.
    /// Test-friendly overload (no miss callback).</summary>
    internal static IReadOnlyList<(PixelPoint Pixel, IMarkerStyle Style)> ProjectMarkers(
        IReadOnlyList<MarkerSnapshot> markers,
        string areaKey,
        IMapCalibrationService calibration,
        double currentZoom)
        => ProjectMarkers(markers, areaKey, calibration, currentZoom, onMiss: null, snapshotCount: markers.Count);

    /// <summary>Projection helper with the service-side miss hook so the
    /// production path emits the miss counter + Trace log when
    /// <c>WorldToWindow</c> returns null for a calibrated area.
    /// TODO(#835 migration steps): the production-time enhancement
    /// ("all-null snapshot → surface a chip") is deferred until real
    /// markers exist to validate the UX against.</summary>
    private static IReadOnlyList<(PixelPoint Pixel, IMarkerStyle Style)> ProjectMarkers(
        IReadOnlyList<MarkerSnapshot> markers,
        string areaKey,
        IMapCalibrationService calibration,
        double currentZoom,
        OverlayWindowService? onMiss,
        int snapshotCount)
    {
        if (markers.Count == 0)
            return Array.Empty<(PixelPoint, IMarkerStyle)>();

        var result = new List<(PixelPoint, IMarkerStyle)>(markers.Count);
        for (var i = 0; i < markers.Count; i++)
        {
            var snap = markers[i];
            var pixel = calibration.WorldToWindow(areaKey, snap.World, currentZoom);
            if (pixel is null)
            {
                if (onMiss is not null)
                {
                    MithrilMeters.Overlay.ProjectionMisses.Add(1,
                        new KeyValuePair<string, object?>("area", areaKey));
                    if (onMiss._projectionMissAreasLogged.TryAdd(areaKey, 0))
                    {
                        onMiss._logger?.LogTrace(
                            "OverlayWindowService: WorldToWindow returned null for a marker in calibrated area {AreaKey} (style={StyleType}); marker silently skipped.",
                            areaKey, snap.Style.GetType().Name);
                    }
                }
                continue;
            }
            result.Add((pixel.Value, snap.Style));
        }
        return result;
    }

    /// <summary>Surface the renderer to test code &#8212; production
    /// consumers inject <see cref="MarkerSceneRenderer"/> directly via DI
    /// and call <c>RegisterDrawer</c> there.</summary>
    internal MarkerSceneRenderer Renderer => _renderer;

    private void SetReady(bool value)
    {
        if (_isReady == value) return;
        _isReady = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReady)));
    }

    private void SetStatusMessage(string? value)
    {
        if (string.Equals(_statusMessage, value, StringComparison.Ordinal)) return;
        _statusMessage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusMessage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasStatusMessage)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeWindow();
        // _brushCache.Dispose() is folded into DisposeWindow's dispatcher
        // closure (the cache's brushes are dispatcher-affined via the bound
        // render target; disposing them off-thread is a Vortice/D3D liability).
    }

    private void DisposeWindow()
    {
        var window = _window;
        var dispatcher = _dispatcher;
        if (window is null)
        {
            // No window — brush cache is empty / never bound. Still safe to
            // dispose the empty cache off-thread.
            _brushCache.Dispose();
            return;
        }

        void Close()
        {
            // Detach OnWindowClosed FIRST so window.Close() below doesn't
            // re-trigger the external-close defensive branch. Render is
            // detached inside CleanupAfterClose. Event detaches cannot throw
            // under the contracts above; let any genuine bug surface.
            window.Closed -= OnWindowClosed;

            try { window.Close(); }
            catch (InvalidOperationException ex)
            {
                _logger?.LogTrace(ex, "OverlayWindow.Close threw — already closed or in non-closeable state; ignoring.");
            }
            CleanupAfterClose(window);
            SetReady(false);
        }

        // If we have a window, we must have a dispatcher (EnsureWindow throws
        // otherwise). The null-coalesce is just to keep the compiler happy
        // and to fail loudly if a future refactor reorders things.
        if (dispatcher is null) throw new InvalidOperationException(
            "OverlayWindowService.DisposeWindow: dispatcher missing but window exists. This indicates EnsureWindow was bypassed.");

        if (dispatcher.CheckAccess()) Close();
        else dispatcher.Invoke(Close);
    }

    /// <summary>Allocation-free elapsed-ms measurement &#8212; same pattern
    /// as <c>System.Diagnostics.Stopwatch.GetElapsedTime</c> on .NET 7+.
    /// Inlined so the meter producer pays one ticks read on the off path.</summary>
    private readonly struct ValueStopwatch
    {
        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;
        private readonly long _startTicks;
        private ValueStopwatch(long start) { _startTicks = start; }
        public static ValueStopwatch StartNew() => new(Stopwatch.GetTimestamp());
        public double GetElapsedMilliseconds() => (Stopwatch.GetTimestamp() - _startTicks) * TicksToMs;
    }
}
