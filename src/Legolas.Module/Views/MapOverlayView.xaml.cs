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

    // State for the "drag anywhere on the viewport to move the active pin"
    // gesture. Routes by FSM state + IsAnchorEditable:
    //  * AwaitingPosition: first MouseDown sets anchor (existing HandleMapClick);
    //    drag continues to refine the anchor.
    //  * Listening + IsAnchorEditable (anchor set, no surveys yet): drag moves
    //    the player anchor.
    //  * Listening with surveys: drag moves SessionState.SelectedSurvey, which
    //    is set from the wizard panel's survey list (no per-pin Thumb grab).
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
        // Set the overlay pad's DataContext directly. An ElementName=Self binding
        // on a non-notifying CLR property would resolve at indeterminate timing;
        // assigning here guarantees the pad has its VM before any user input
        // arrives, so Command/IsChecked bindings inside the pad always work.
        OverlayNudgePad.DataContext = nudgePad;
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

    // Per-pin Thumb drag handlers were removed when pins became visual-only.
    // All click + drag interaction now flows through the viewport handlers
    // below; selection moved to the wizard panel's survey list.

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
