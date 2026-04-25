using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Mithril.Shared.Settings;
using Legolas.Controls;
using Legolas.ViewModels;
using Legolas.Domain;

namespace Legolas.Views;

public partial class MapOverlayView : Window
{
    private const double MinZoom = 0.25;
    private const double MaxZoom = 8.0;

    private Point? _panStartScreen;
    private double _panStartX;
    private double _panStartY;
    private Vector _grabOffset;
    private SurveyItemViewModel? _placementDrag;

    public MapOverlayView()
    {
        InitializeComponent();
    }

    public MapOverlayView(LegolasSettings settings, SettingsAutoSaver<LegolasSettings> saver) : this()
    {
        WindowLayoutBinder.Bind(this, settings.MapOverlay, saver.Touch);
        Loaded += (_, _) =>
        {
            ClickThrough.Apply(this, settings.ClickThroughMap);
            ClickThrough.ForceTopmost(this);
        };
        Activated += (_, _) => ClickThrough.ForceTopmost(this);
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LegolasSettings.ClickThroughMap))
                ClickThrough.Apply(this, settings.ClickThroughMap);
        };
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void SurveyDot_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is not Thumb thumb) return;
        // Grabbing a pin also selects it for arrow-key nudging.
        if (thumb.DataContext is SurveyItemViewModel s
            && DataContext is MapOverlayViewModel vm)
        {
            vm.Session.SelectedSurvey = s;
        }
        // Capture offset from the dot's center to the cursor at grab time, so
        // the released dot ends up where the user expected (no snap to cursor).
        var cursor = Mouse.GetPosition(Viewport);
        if (thumb.DataContext is SurveyItemViewModel sel && sel.EffectivePixel.HasValue)
        {
            _grabOffset = cursor - new Point(sel.EffectivePixel.Value.X, sel.EffectivePixel.Value.Y);
        }
        else
        {
            _grabOffset = new Vector(0, 0);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (DataContext is not MapOverlayViewModel vm) { base.OnPreviewKeyDown(e); return; }
        var selected = vm.Session.SelectedSurvey;
        if (selected is null || !selected.EffectivePixel.HasValue) { base.OnPreviewKeyDown(e); return; }

        var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 5.0
                 : (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? 0.25
                 : 1.0;
        double dx = 0, dy = 0;
        switch (e.Key)
        {
            case Key.Left: dx = -step; break;
            case Key.Right: dx = step; break;
            case Key.Up: dy = -step; break;
            case Key.Down: dy = step; break;
            case Key.Escape: vm.Session.SelectedSurvey = null; e.Handled = true; return;
            default: base.OnPreviewKeyDown(e); return;
        }
        var p = selected.EffectivePixel.Value;
        vm.CorrectSurveyCommand.Execute(new CorrectionArgs(selected, new PixelPoint(p.X + dx, p.Y + dy)));
        e.Handled = true;
    }

    private void SurveyDot_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb thumb) return;
        // Always create/replace with a mutable transform (XAML-declared ones
        // can be auto-frozen in template contexts).
        if (thumb.RenderTransform is not TranslateTransform t || t.IsFrozen)
        {
            t = new TranslateTransform();
            thumb.RenderTransform = t;
        }
        // DragDelta values are in the Thumb's local (pre-transform) coords —
        // adding them to the Thumb's own TranslateTransform makes the visual
        // track the cursor 1:1 regardless of viewport zoom.
        t.X += e.HorizontalChange;
        t.Y += e.VerticalChange;
    }

    private void SurveyDot_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (sender is not Thumb thumb) return;

        // Drop the transient drag transform; binding to ManualOverride/EffectivePixel
        // re-positions the dot.
        thumb.RenderTransform = Transform.Identity;

        if (e.Canceled) return;
        if (thumb.DataContext is not SurveyItemViewModel survey) return;
        if (DataContext is not MapOverlayViewModel vm) return;

        // Mouse.GetPosition(Viewport) is in pre-transform canvas coordinates:
        // WPF automatically applies the inverse of Viewport's RenderTransform.
        var cursor = Mouse.GetPosition(Viewport);
        var newCenter = new PixelPoint(cursor.X - _grabOffset.X, cursor.Y - _grabOffset.Y);
        _grabOffset = new Vector(0, 0);
        vm.CorrectSurveyCommand.Execute(new CorrectionArgs(survey, newCenter));
    }

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MapOverlayViewModel vm) return;
        if (e.OriginalSource is not FrameworkElement fe) return;
        // Only act on clicks that hit the transparent Viewport background, not
        // a survey thumb or any overlay content.
        if (!ReferenceEquals(fe, Viewport)) return;

        var canvasPos = Mouse.GetPosition(Viewport);
        var clickPoint = new PixelPoint(canvasPos.X, canvasPos.Y);

        // AwaitingPin: place the pin and let the user keep dragging without
        // releasing the mouse — same gesture for placement and fine-tuning.
        if (vm.Session.SurveyPhase == SurveyPhase.AwaitingPin
            && vm.Session.PendingSurvey is not null)
        {
            var placed = vm.PlacePendingPinAt(clickPoint);
            if (placed is not null)
            {
                _placementDrag = placed;
                vm.Session.SelectedSurvey = placed;
                ViewportRoot.CaptureMouse();
                e.Handled = true;
                return;
            }
        }

        vm.HandleMapClickCommand.Execute(clickPoint);
        e.Handled = true;
    }

    private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_placementDrag is null) return;
        if (DataContext is not MapOverlayViewModel vm) return;

        var canvasPos = Mouse.GetPosition(Viewport);
        // Final position via CorrectSurvey so the projector refit fires once
        // with the user's final intended location.
        vm.CorrectSurveyCommand.Execute(new CorrectionArgs(_placementDrag,
            new PixelPoint(canvasPos.X, canvasPos.Y)));
        _placementDrag = null;
        ViewportRoot.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is not MapOverlayViewModel vm) return;

        var cursorScreen = e.GetPosition(ViewportRoot);
        var zoomOld = vm.Zoom;
        var factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
        var zoomNew = Math.Clamp(zoomOld * factor, MinZoom, MaxZoom);
        if (zoomNew == zoomOld) return;

        // Preserve the content point under the cursor across the zoom change.
        // content = (cursor - pan) / zoom  →  pan_new = cursor - (cursor - pan_old) * zoom_new / zoom_old
        var panNewX = cursorScreen.X - (cursorScreen.X - vm.PanX) * zoomNew / zoomOld;
        var panNewY = cursorScreen.Y - (cursorScreen.Y - vm.PanY) * zoomNew / zoomOld;

        vm.Zoom = zoomNew;
        vm.PanX = panNewX;
        vm.PanY = panNewY;
        e.Handled = true;
    }

    private void Viewport_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MapOverlayViewModel vm) return;
        _panStartScreen = e.GetPosition(ViewportRoot);
        _panStartX = vm.PanX;
        _panStartY = vm.PanY;
        ViewportRoot.CaptureMouse();
        e.Handled = true;
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        // Live-update the just-placed pin as the user continues to hold the
        // mouse button — feels like one continuous place-and-position gesture.
        if (_placementDrag is not null)
        {
            var pos = e.GetPosition(Viewport);
            _placementDrag.UpdateModel(_placementDrag.Model
                with { ManualOverride = new PixelPoint(pos.X, pos.Y) });
            return;
        }

        if (_panStartScreen is null) return;
        if (DataContext is not MapOverlayViewModel vm) return;

        var current = e.GetPosition(ViewportRoot);
        vm.PanX = _panStartX + (current.X - _panStartScreen.Value.X);
        vm.PanY = _panStartY + (current.Y - _panStartScreen.Value.Y);
    }

    private void Viewport_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_panStartScreen is null) return;
        _panStartScreen = null;
        ViewportRoot.ReleaseMouseCapture();
        e.Handled = true;
    }
}
