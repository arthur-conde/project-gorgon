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
/// <para>The confirmed selection is exposed as <see cref="SelectedRect"/> in
/// <b>absolute virtual-desktop DIUs</b> (the same frame WPF <see cref="Window.Left"/>/
/// <see cref="Window.Top"/> use), so it maps directly onto the overlay window's
/// bounds with no further transform. <see cref="SelectedRect"/> is
/// <see langword="null"/> on cancel.</para>
///
/// <para><b>Manual-verify (needs running PG; can't run a live drag in CI):</b> the
/// drag visuals, the dim/hole rendering, and that the snipped rect visually
/// coincides with the in-game map under a scaled (≠100% DPI) and a multi-monitor
/// layout. The DIU math is unit-tested via <see cref="SnipRectMath"/>.</para>
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

    /// <summary>The confirmed rect in absolute virtual-desktop DIUs, or null on cancel.</summary>
    public Rect? SelectedRect { get; private set; }

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

        // Local canvas DIUs → absolute virtual-desktop DIUs.
        SelectedRect = SnipRectMath.ToVirtualDesktop(local, _virtualLeft, _virtualTop);
        DialogClose();
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
