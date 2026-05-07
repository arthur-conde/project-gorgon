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
    /// <summary>VM for the optional on-overlay nudge pad. Surfaced as a property
    /// (rather than going through MapOverlayViewModel) to avoid a fake cycle:
    /// NudgePadViewModel depends on MapOverlayViewModel, so MapOverlayViewModel
    /// can't take NudgePadViewModel as a ctor param.</summary>
    public NudgePadViewModel? NudgePad { get; }

    /// <summary>Surfaced for the overlay-local toggle binding (Visibility on the
    /// pad's host border). Same instance as the one MapOverlayViewModel sees.</summary>
    public LegolasSettings? Settings { get; }

    private Vector _grabOffset;
    private Vector _anchorGrabOffset;

    // State for the "drag anywhere on the viewport to move the active pin"
    // gesture (issue #131 follow-up). Routes by FSM state + IsAnchorEditable:
    //  * AwaitingPosition: first MouseDown sets anchor (existing HandleMapClick);
    //    drag continues to refine the anchor.
    //  * Listening + IsAnchorEditable (anchor set, no surveys yet): drag moves
    //    the player anchor — important when the anchor lands off-screen and
    //    can't be grabbed via its Thumb.
    //  * Listening with surveys: drag moves SessionState.SelectedSurvey.
    private bool _draggingAnchorFromViewport;
    private SurveyItemViewModel? _draggingPinFromViewport;

    public MapOverlayView()
    {
        InitializeComponent();
    }

    public MapOverlayView(LegolasSettings settings, SettingsAutoSaver<LegolasSettings> saver, NudgePadViewModel nudgePad) : this()
    {
        Settings = settings;
        NudgePad = nudgePad;
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

        // AwaitingPosition: first click sets the anchor and transitions FSM to
        // Listening. Fall through into the IsAnchorEditable path below so the
        // user can keep dragging to refine the anchor without releasing.
        if (vm.SurveyFlow.CurrentState == SurveyFlowState.AwaitingPosition)
        {
            vm.HandleMapClickCommand.Execute(clickPoint);
        }

        if (vm.Session.IsAnchorEditable)
        {
            _draggingAnchorFromViewport = true;
            vm.MoveAnchor(clickPoint);
            Viewport.CaptureMouse();
            e.Handled = true;
            return;
        }

        var selected = vm.Session.SelectedSurvey;
        if (selected != null && !selected.Collected && !selected.Skipped)
        {
            _draggingPinFromViewport = selected;
            ApplyDraggedPinPosition(canvasPos);
            Viewport.CaptureMouse();
            e.Handled = true;
            return;
        }

        e.Handled = true;
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!Viewport.IsMouseCaptured) return;
        if (DataContext is not MapOverlayViewModel vm) return;
        var canvasPos = Mouse.GetPosition(Viewport);

        if (_draggingAnchorFromViewport)
        {
            vm.MoveAnchor(new PixelPoint(canvasPos.X, canvasPos.Y));
            return;
        }
        if (_draggingPinFromViewport is not null)
        {
            ApplyDraggedPinPosition(canvasPos);
        }
    }

    private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!Viewport.IsMouseCaptured) return;
        Viewport.ReleaseMouseCapture();

        if (_draggingAnchorFromViewport)
        {
            _draggingAnchorFromViewport = false;
            return;
        }

        if (_draggingPinFromViewport is not null && DataContext is MapOverlayViewModel vm)
        {
            // Final commit through CorrectSurveyCommand — this triggers the
            // projector refit and route rebuild that intermediate Move events
            // intentionally skip.
            var canvasPos = Mouse.GetPosition(Viewport);
            var finalPixel = new PixelPoint(canvasPos.X, canvasPos.Y);
            vm.CorrectSurveyCommand.Execute(new CorrectionArgs(_draggingPinFromViewport, finalPixel));
            _draggingPinFromViewport = null;
        }
    }

    private void ApplyDraggedPinPosition(Point cursor)
    {
        if (_draggingPinFromViewport is null) return;
        var newPixel = new PixelPoint(cursor.X, cursor.Y);
        var updated = _draggingPinFromViewport.Model with { ManualOverride = newPixel };
        _draggingPinFromViewport.UpdateModel(updated);
    }
}
