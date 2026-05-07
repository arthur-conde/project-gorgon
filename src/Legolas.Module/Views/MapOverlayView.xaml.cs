using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Mithril.Shared.Settings;
using Legolas.Controls;
using Legolas.Flow;
using Legolas.ViewModels;
using Legolas.Domain;

namespace Legolas.Views;

public partial class MapOverlayView : Window
{
    private Vector _grabOffset;
    private Vector _anchorGrabOffset;

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
        // Arrow-key pin nudging moved to global IHotkeyCommand registrations
        // (Legolas · Pin Nudge category) so it works while the game window has
        // focus. Local handler now only clears the active selection on Escape.
        if (DataContext is MapOverlayViewModel vm && e.Key == Key.Escape)
        {
            vm.Session.SelectedSurvey = null;
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
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
        t.X += e.HorizontalChange;
        t.Y += e.VerticalChange;
    }

    private void PlayerAnchor_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (DataContext is not MapOverlayViewModel vm) return;
        if (!vm.Session.IsAnchorEditable) return;
        // Clear pin selection so subsequent keyboard nudges fall through to the
        // anchor (NudgePinCommandBase routes by SelectedSurvey first).
        vm.Session.SelectedSurvey = null;
        var cursor = Mouse.GetPosition(Viewport);
        _anchorGrabOffset = cursor - new Point(vm.PlayerPosition.X, vm.PlayerPosition.Y);
    }

    private void PlayerAnchor_DragDelta(object sender, DragDeltaEventArgs e)
    {
        // Same transient-transform trick as SurveyDot_DragDelta — the binding
        // re-positions the Thumb on DragCompleted.
        if (sender is not Thumb thumb) return;
        if (thumb.RenderTransform is not TranslateTransform t || t.IsFrozen)
        {
            t = new TranslateTransform();
            thumb.RenderTransform = t;
        }
        t.X += e.HorizontalChange;
        t.Y += e.VerticalChange;
    }

    private void PlayerAnchor_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (sender is not Thumb thumb) return;
        thumb.RenderTransform = Transform.Identity;
        if (e.Canceled) return;
        if (DataContext is not MapOverlayViewModel vm) return;
        // Re-check editability — a survey could have landed mid-drag and sealed
        // the anchor. Silently dropping the move is safer than committing it.
        if (!vm.Session.IsAnchorEditable) return;
        var cursor = Mouse.GetPosition(Viewport);
        var newCenter = new PixelPoint(cursor.X - _anchorGrabOffset.X, cursor.Y - _anchorGrabOffset.Y);
        _anchorGrabOffset = new Vector(0, 0);
        vm.MoveAnchor(newCenter);
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

        // The VM only acts on clicks while the FSM is in AwaitingPosition (anchor
        // setup). Survey placement is automatic; corrections are drag-only. This
        // handler is still here so AwaitingPosition continues to work.
        vm.HandleMapClickCommand.Execute(clickPoint);
        e.Handled = true;
    }
}
