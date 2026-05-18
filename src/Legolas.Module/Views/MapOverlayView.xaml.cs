using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Mithril.Shared.Settings;
using Legolas.Controls;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Rendering;
using Legolas.ViewModels;

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

    // #454: the editable player anchor is retired for Survey (absolute
    // placement needs none). Viewport interaction now:
    //  * Motherlode mode: a click records the player position (HandleMapClick
    //    → SetPlayerPosition in the VM; Survey mode ignores the click).
    //  * Drag moves SessionState.SelectedSurvey (picked from the wizard
    //    panel's survey list) — a local pixel correction, no recalibration.
    private SurveyItemViewModel? _draggingPinFromViewport;

    private readonly D2DBrushCache _brushCache = new();
    private readonly MarchingAntsClock _antsClock = new();

    public MapOverlayView()
    {
        InitializeComponent();
        // Wire the D2D pin renderer. Stays attached for the life of the
        // window; the surface itself manages CompositionTarget.Rendering
        // subscription based on visibility, so this handler only fires when
        // there's actually a frame to draw.
        MapSurface.Render += OnMapSurfaceRender;
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
        Loaded += (_, _) => ClickThrough.Apply(this, settings.ClickThroughMap);
        // Re-assert TOPMOST on every show + on a low-frequency timer while
        // visible — Loaded/Activated alone miss the Hide()/Show() cycle the
        // OverlayController drives when the game holds the foreground.
        ClickThrough.KeepTopmost(this);
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LegolasSettings.ClickThroughMap))
                ClickThrough.Apply(this, settings.ClickThroughMap);
        };
        Closed += (_, _) =>
        {
            MapSurface.Render -= OnMapSurfaceRender;
            _brushCache.Dispose();
            MapSurface.Dispose();
        };
    }

    private void OnMapSurfaceRender(object? sender, D2DRenderEventArgs e)
    {
        if (DataContext is not MapOverlayViewModel vm) return;

        // Bind the brush cache to the current render target — cheap when
        // unchanged, drops cached brushes when the target rebuilds (resize,
        // device-lost) so the cache never holds dangling COM pointers.
        _brushCache.Bind(e.RenderTarget);

        var wedges = new List<WedgeArc>(vm.Surveys.Count);
        var pins = new List<PixelPoint>(vm.Surveys.Count);
        var selected = vm.Session.SelectedSurvey;
        var listening = vm.IsListening;
        int? activeIndex = null;
        foreach (var s in vm.Surveys)
        {
            if (s.WedgeArc is { } arc) wedges.Add(arc);
            // IsVisible mirrors the WPF data-template's Visibility binding —
            // collected pins drop out, anything without a projected pixel too.
            if (s.IsVisible)
            {
                if (listening && ReferenceEquals(s, selected))
                    activeIndex = pins.Count;
                pins.Add(s.EffectivePixel!.Value);
            }
        }

        ActivePinTreatmentSpec? activeSpec = null;
        if (activeIndex.HasValue)
        {
            var aps = vm.ActivePinStyle;
            activeSpec = new ActivePinTreatmentSpec(
                Treatment: aps.Treatment,
                Color: ParseColor(aps.Color),
                HaloPaddingPx: aps.HaloPaddingPx,
                StrokeThickness: aps.HaloThickness,
                GlowBlurRadius: aps.GlowBlurRadius);
        }

        var pinStyle = vm.PinStyle;
        var outerStyle = new PinLayerStyle(
            Shape: pinStyle.Outer.Shape,
            FillColor: ParseColor(pinStyle.Outer.FillColor),
            StrokeColor: ParseColor(pinStyle.Outer.StrokeColor),
            StrokeStyle: pinStyle.Outer.StrokeStyle,
            StrokeThickness: pinStyle.Outer.StrokeThickness,
            // Outer Size on survey pins is unused (driven by SurveyPinRadiusMetres);
            // see LegolasPinStyle docs.
            Size: 0);
        var centerStyle = new PinLayerStyle(
            Shape: pinStyle.Center.Shape,
            FillColor: ParseColor(pinStyle.Center.FillColor),
            StrokeColor: ParseColor(pinStyle.Center.StrokeColor),
            StrokeStyle: pinStyle.Center.StrokeStyle,
            StrokeThickness: pinStyle.Center.StrokeThickness,
            Size: pinStyle.Center.Size);

        var playerStyle = vm.PlayerPinStyle;
        var playerOuterStyle = new PinLayerStyle(
            Shape: playerStyle.Outer.Shape,
            FillColor: ParseColor(playerStyle.Outer.FillColor),
            StrokeColor: ParseColor(playerStyle.Outer.StrokeColor),
            StrokeStyle: playerStyle.Outer.StrokeStyle,
            StrokeThickness: playerStyle.Outer.StrokeThickness,
            // Player pin's outer Size IS meaningful — drives the visible
            // diameter the way SurveyPinRadiusMetres does for survey pins.
            Size: playerStyle.Outer.Size);
        var playerCenterStyle = new PinLayerStyle(
            Shape: playerStyle.Center.Shape,
            FillColor: ParseColor(playerStyle.Center.FillColor),
            StrokeColor: ParseColor(playerStyle.Center.StrokeColor),
            StrokeStyle: playerStyle.Center.StrokeStyle,
            StrokeThickness: playerStyle.Center.StrokeThickness,
            Size: playerStyle.Center.Size);

        var scene = new PinScene(
            RoutePoints: vm.RoutePoints,
            ActiveSegmentPoints: vm.ActiveSegmentPoints,
            Wedges: wedges,
            SurveyPins: pins,
            ActivePinIndex: activeIndex,
            ActiveTreatment: activeSpec,
            SurveyOuter: outerStyle,
            SurveyCenter: centerStyle,
            SurveyOuterDiameter: vm.PinDiameter,
            PlayerPosition: vm.Session.HasPlayerPosition ? vm.PlayerPosition : null,
            PlayerOuter: playerOuterStyle,
            PlayerCenter: playerCenterStyle,
            RouteLineColor: vm.Brushes.RouteLine.Color,
            WedgeFillColor: vm.Brushes.BearingWedgeFill.Color,
            WedgeStrokeColor: vm.Brushes.BearingWedgeStroke.Color,
            ActiveSegmentDashOffset: _antsClock.Advance());

        PinSceneRenderer.Render(scene, e.RenderTarget, e.Factory, _brushCache);
    }

    private static Color ParseColor(string hex) => LegolasBrushes.Parse(hex);

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

        // #460: while the wizard Calibrating step has armed capture, a
        // viewport click pairs with the next pending ProcessMapPinAdd world
        // coord (turn order). Consumes the click — no placement / mode logic.
        if (vm.IsCalibrationCapturing)
        {
            vm.PairCalibrationClick(clickPoint);
            e.Handled = true;
            return;
        }

        // Motherlode records the player position from the click; Survey
        // ignores it (placement is automatic + absolute). The VM mode-gates.
        vm.HandleMapClickCommand.Execute(clickPoint);

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

        if (_draggingPinFromViewport is not null)
        {
            ApplyDraggedPinPosition(canvasPos);
        }
    }

    private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!Viewport.IsMouseCaptured) return;
        Viewport.ReleaseMouseCapture();

        if (_draggingPinFromViewport is not null && DataContext is MapOverlayViewModel vm)
        {
            // Final commit through CorrectSurveyCommand — a local pixel
            // correction + route rebuild (no projector refit any more, #454).
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
