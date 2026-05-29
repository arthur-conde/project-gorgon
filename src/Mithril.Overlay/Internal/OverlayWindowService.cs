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
/// (host shutdown) and marshals window disposal back onto the dispatcher.</para>
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
internal sealed class OverlayWindowService : IHostedService, IOverlayWindow
{
    private const string UncalibratedMessage = "map not calibrated — use Legolas wizard";

    private readonly WorldOverlayMarkers _markers;
    private readonly MarkerSceneRenderer _renderer;
    private readonly IMapCalibrationService _calibration;
    private readonly IAreaState _areaState;
    private readonly IPositionState _positionState; // reserved for future consumers; ensures the DI shape matches Decision C
    private readonly ILogger? _logger;
    private readonly D2DBrushCache _brushCache = new();

    private OverlayWindow? _window;
    private Dispatcher? _dispatcher;
    private bool _isReady;
    private string? _statusMessage;
    private bool _firstFrameLogged;
    private string? _lastSeenUncalibratedArea;
    private bool _disposed;

    public OverlayWindowService(
        IWorldOverlayMarkers markers,
        MarkerSceneRenderer renderer,
        IMapCalibrationService calibration,
        IAreaState areaState,
        IPositionState positionState,
        ILoggerFactory? loggerFactory = null)
    {
        // The DI registration always hands us the concrete WorldOverlayMarkers
        // singleton — we need the CurrentArea setter that's not on the public
        // IWorldOverlayMarkers interface. Down-cast is safe and stays internal.
        _markers = (WorldOverlayMarkers)markers;
        _renderer = renderer;
        _calibration = calibration;
        _areaState = areaState;
        _positionState = positionState;
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
        // Capture the dispatcher of whatever thread called StartAsync — in the
        // shell that's the UI thread (Application is up before the host runs).
        // The window itself is materialised lazily on first Window access so
        // the scaffold ships dormant (per the migration plan in #835).
        _dispatcher = Dispatcher.CurrentDispatcher;
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
        _window.OverlaySurface.Render += OnSurfaceRender;
        _window.Closed += OnWindowClosed;
        _logger?.LogInformation("OverlayWindow created (not shown — consumer must Show() to surface it).");
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        SetReady(false);
        _logger?.LogInformation("OverlayWindow closed.");
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

        SetStatusMessage(null);

        var snapshot = _markers.CurrentAreaMarkers;
        var projected = ProjectMarkers(snapshot, areaKey, _calibration, currentZoom: 1.0);
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
    /// exercise the projection without standing up a D3D surface.</summary>
    internal static IReadOnlyList<(PixelPoint Pixel, IMarkerStyle Style)> ProjectMarkers(
        IReadOnlyList<(MarkerHandle Handle, double WorldX, double WorldZ, IMarkerStyle Style)> markers,
        string areaKey,
        IMapCalibrationService calibration,
        double currentZoom)
    {
        if (markers.Count == 0)
            return Array.Empty<(PixelPoint, IMarkerStyle)>();

        var result = new List<(PixelPoint, IMarkerStyle)>(markers.Count);
        for (var i = 0; i < markers.Count; i++)
        {
            var (_, wx, wz, style) = markers[i];
            // WorldCoord constructor is (X, Y, Z) — Y is elevation, not
            // consumed by the 2D map projection. Pass 0 for Y per the type's
            // own remarks; what flows into WorldToWindow is the (X, Z) pair.
            var pixel = calibration.WorldToWindow(areaKey, new WorldCoord(wx, 0, wz), currentZoom);
            if (pixel is null) continue;
            result.Add((pixel.Value, style));
        }
        return result;
    }

    /// <summary>Surface a Legolas-side drawer registration to the renderer.
    /// Internal &#8212; the public path for the migration PRs is to inject
    /// <see cref="MarkerSceneRenderer"/> directly and call
    /// <c>RegisterDrawer</c> there, but tests need the hook through the
    /// service.</summary>
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
        _brushCache.Dispose();
    }

    private void DisposeWindow()
    {
        var window = _window;
        var dispatcher = _dispatcher;
        if (window is null) return;

        void Close()
        {
            try { window.OverlaySurface.Render -= OnSurfaceRender; } catch { /* surface gone */ }
            try { window.Closed -= OnWindowClosed; } catch { /* gone */ }
            try { window.Close(); } catch { /* already closed */ }
            try { window.OverlaySurface.Dispose(); } catch { /* already disposed */ }
            _window = null;
            SetReady(false);
        }

        if (dispatcher is null || dispatcher.CheckAccess()) Close();
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
