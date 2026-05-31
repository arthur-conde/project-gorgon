using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Transient, full-virtual-screen Snipping-Tool-style region selector (#940,
/// spec §7). A brand-new dimmed <see cref="Window"/> (NOT the overlay — so the
/// overlay's forbidden-mutation invariants don't apply here) spanning the whole
/// virtual desktop. The user drags a rectangle; mouse-up / <c>Enter</c> confirms,
/// <c>Esc</c> / right-click cancels.
///
/// <para>The confirmed selection is exposed in TWO frames: <see cref="SelectedRect"/>
/// in <b>absolute virtual-desktop DIUs</b> (the same frame WPF <see cref="Window.Left"/>/
/// <see cref="Window.Top"/> use — mirrored onto the overlay window with no further
/// transform) and <see cref="SelectedPhysicalRect"/> in <b>absolute virtual-desktop
/// physical pixels</b> (the frame BitBlt reads — persisted to the capture-rect store,
/// #947). The physical rect is resolved here at confirm time from this single
/// window's own <c>TransformToDevice</c> scale, so the read path is frame-independent.
/// Both are <see langword="null"/> on cancel.</para>
///
/// <para><b>Known limitation (#947).</b> This snip is a SINGLE WPF window spanning
/// the whole virtual desktop. Under PerMonitorV2 a single top-level window has ONE
/// DPI scale and maps its entire logical surface uniformly at that scale (WPF does
/// not per-monitor-rescale <c>GetPosition</c> within one window), so the persisted
/// physical rect is <c>DIU · S_snip</c> uniformly — correct for single-monitor and
/// uniform-DPI multi-monitor layouts. A true mixed-DPI multi-monitor layout (the map
/// sitting on a non-primary monitor at a different scale than the snip window's) is
/// owed to #938 manual-verify. A stored physical rect also goes stale if the user
/// later changes DPI/resolution (re-snip to refresh). This is no worse than the
/// pre-#940 read-time behavior, which also keyed off a single window's scale.</para>
///
/// <para><b>Manual-verify (needs running PG; can't run a live drag in CI):</b> the
/// drag visuals, the dim/hole rendering, and that the snipped rect visually
/// coincides with the in-game map under a scaled (≠100% DPI) and a multi-monitor
/// layout. The DIU math is unit-tested via <see cref="SnipRectMath"/>; the
/// DIU→physical resolution via <see cref="CaptureRectMath.DiuToPhysical"/>.</para>
/// </summary>
internal sealed class RegionSnipWindow : Window
{
    private readonly Canvas _canvas;
    private readonly Rectangle _selectionBorder;
    private readonly Path _dimPath;
    private readonly TextBlock _readout;

    private readonly double _virtualLeft;
    private readonly double _virtualTop;
    private readonly double _virtualWidth;
    private readonly double _virtualHeight;

    private Point? _dragStart; // in canvas (= virtual-screen-local) DIUs
    private Point _dragCurrent;

    /// <summary>The confirmed rect in absolute virtual-desktop DIUs, or null on
    /// cancel. The controller mirrors this onto the overlay window for visual
    /// feedback (the overlay is itself a DIU/WPF surface).</summary>
    public Rect? SelectedRect { get; private set; }

    /// <summary>
    /// The confirmed rect in absolute virtual-desktop <b>physical pixels</b> — the
    /// frame <c>BitBltScreenCapture</c> reads — or null on cancel (#947). Computed
    /// once at confirm time from this single snip window's own live
    /// <c>TransformToDevice</c> scale: under PerMonitorV2 a single top-level window
    /// maps its entire logical surface uniformly at one DPI scale, so
    /// <c>physical = DIU · S_snip</c> uniformly across the whole virtual-desktop
    /// selection. This is what gets persisted, so the read path is frame-independent
    /// (no read-time DPI / monitor enumeration). Correct for single-monitor and
    /// uniform-DPI multi-monitor; a mixed-DPI layout is #938 manual-verify.
    /// </summary>
    public CaptureRect? SelectedPhysicalRect { get; private set; }

    public RegionSnipWindow()
    {
        _virtualLeft = SystemParameters.VirtualScreenLeft;
        _virtualTop = SystemParameters.VirtualScreenTop;
        _virtualWidth = SystemParameters.VirtualScreenWidth;
        _virtualHeight = SystemParameters.VirtualScreenHeight;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent;
        Cursor = Cursors.Cross;

        // WindowStartupLocation must be Manual so Left/Top take effect on a
        // multi-monitor layout with a negative virtual origin.
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = _virtualLeft;
        Top = _virtualTop;
        Width = _virtualWidth;
        Height = _virtualHeight;

        _canvas = new Canvas { Background = Brushes.Transparent };

        // Dim layer drawn as a Path so a "hole" can be punched for the selection.
        _dimPath = new Path
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)),
            IsHitTestVisible = false,
        };
        _canvas.Children.Add(_dimPath);

        _selectionBorder = new Rectangle
        {
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 2 },
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _canvas.Children.Add(_selectionBorder);

        _readout = new TextBlock
        {
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)),
            Padding = new Thickness(4, 2, 4, 2),
            FontSize = 12,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _canvas.Children.Add(_readout);

        Content = _canvas;

        Loaded += OnLoaded;
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        MouseRightButtonDown += (_, e) => { e.Handled = true; Cancel(); };
        KeyDown += OnKeyDown;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Full-screen dim with no hole until a drag begins.
        RedrawDim(null);
        Activate();
        Focus();
    }

    private void OnMouseDown(object? sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(_canvas);
        _dragCurrent = _dragStart.Value;
        CaptureMouse();
        UpdateSelectionVisual();
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (_dragStart is null) return;
        _dragCurrent = e.GetPosition(_canvas);
        UpdateSelectionVisual();
    }

    private void OnMouseUp(object? sender, MouseButtonEventArgs e)
    {
        if (_dragStart is null) return;
        ReleaseMouseCapture();
        Confirm();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Cancel(); }
        else if (e.Key == Key.Enter && _dragStart is not null) { e.Handled = true; Confirm(); }
    }

    private void UpdateSelectionVisual()
    {
        if (_dragStart is null) return;
        var local = SnipRectMath.Normalize(_dragStart.Value, _dragCurrent);

        Canvas.SetLeft(_selectionBorder, local.X);
        Canvas.SetTop(_selectionBorder, local.Y);
        _selectionBorder.Width = local.Width;
        _selectionBorder.Height = local.Height;
        _selectionBorder.Visibility = Visibility.Visible;

        RedrawDim(local);

        _readout.Text = string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Round(local.Width)} × {(int)Math.Round(local.Height)}");
        _readout.Visibility = Visibility.Visible;
        // Place the readout just below-right of the cursor, clamped on-screen.
        double rx = Math.Min(_dragCurrent.X + 12, _virtualWidth - 80);
        double ry = Math.Min(_dragCurrent.Y + 12, _virtualHeight - 24);
        Canvas.SetLeft(_readout, Math.Max(0, rx));
        Canvas.SetTop(_readout, Math.Max(0, ry));
    }

    /// <summary>Draw the dim overlay with an optional transparent "hole" for the selection.</summary>
    private void RedrawDim(Rect? hole)
    {
        var full = new RectangleGeometry(new Rect(0, 0, _virtualWidth, _virtualHeight));
        if (hole is { } h && h.Width > 0 && h.Height > 0)
        {
            var holeGeo = new RectangleGeometry(h);
            _dimPath.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, holeGeo);
        }
        else
        {
            _dimPath.Data = full;
        }
    }

    private void Confirm()
    {
        if (_dragStart is null) { Cancel(); return; }
        var local = SnipRectMath.Normalize(_dragStart.Value, _dragCurrent);
        if (local.Width < 1 || local.Height < 1) { Cancel(); return; }

        // Local canvas DIUs → absolute virtual-desktop DIUs (for the overlay mirror).
        var abs = SnipRectMath.ToVirtualDesktop(local, _virtualLeft, _virtualTop);
        SelectedRect = abs;

        // ...and to absolute virtual-desktop PHYSICAL pixels (what gets persisted +
        // what BitBlt reads), using THIS window's single live device scale. The snip
        // is realized (shown via ShowDialog), so PresentationSource is available.
        SelectedPhysicalRect = ToPhysical(abs);
        DialogClose();
    }

    /// <summary>
    /// Convert an absolute virtual-desktop DIU rect to physical pixels using this
    /// snip window's own live <c>TransformToDevice</c> scale. The window spans the
    /// whole virtual desktop and (under PMv2) has a single uniform device scale, so
    /// the pure <see cref="CaptureRectMath.DiuToPhysical"/> identity applies across
    /// the entire selection. Returns null if the transform is unavailable (window
    /// not realized) or the rect is degenerate — the caller fail-softs.
    /// </summary>
    private CaptureRect? ToPhysical(Rect abs)
    {
        var source = System.Windows.PresentationSource.FromVisual(this);
        var m = source?.CompositionTarget?.TransformToDevice;
        if (m is not { } t) return null;

        var physical = CaptureRectMath.DiuToPhysical(
            abs.X, abs.Y, abs.Width, abs.Height, t.M11, t.M22);
        return physical.IsEmpty ? null : physical;
    }

    private void Cancel()
    {
        SelectedRect = null;
        DialogClose();
    }

    private void DialogClose()
    {
        // Close is fine on THIS transient window (it is not the shared overlay).
        Close();
    }
}
