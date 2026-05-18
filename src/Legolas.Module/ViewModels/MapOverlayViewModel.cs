using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;

namespace Legolas.ViewModels;

public sealed partial class MapOverlayViewModel : ObservableObject
{
    private readonly SessionState _session;
    private readonly ICoordinateProjector _projector;
    private readonly IRouteOptimizer _optimizer;
    private readonly LegolasSettings? _settings;
    private readonly SurveyFlowController _surveyFlow;
    private readonly LegolasBrushes _brushes;
    private readonly PinCalibrationCoordinator? _pinCal;

    public MapOverlayViewModel(SessionState session, ICoordinateProjector projector, IRouteOptimizer optimizer, SurveyFlowController surveyFlow, LegolasBrushes brushes)
        : this(session, projector, optimizer, surveyFlow, brushes, settings: null) { }

    public MapOverlayViewModel(SessionState session, ICoordinateProjector projector, IRouteOptimizer optimizer, SurveyFlowController surveyFlow, LegolasBrushes brushes, LegolasSettings? settings, PinCalibrationCoordinator? pinCalibration = null)
    {
        _session = session;
        _projector = projector;
        _optimizer = optimizer;
        _surveyFlow = surveyFlow;
        _brushes = brushes;
        _settings = settings;
        _pinCal = pinCalibration;
        if (_pinCal is not null)
            _pinCal.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PinCalibrationCoordinator.IsArmed))
                    OnPropertyChanged(nameof(IsCalibrationCapturing));
            };
        if (_settings is not null)
        {
            _settings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LegolasSettings.SurveyPinRadiusMetres))
                {
                    OnPropertyChanged(nameof(PinRadius));
                    OnPropertyChanged(nameof(PinDiameter));
                }
            };
        }

        _session.Surveys.CollectionChanged += OnSurveysCollectionChanged;
        _session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SessionState.PlayerPosition))
            {
                OnPropertyChanged(nameof(PlayerPosition));
                RebuildRouteGeometry();
                RebuildAllWedges();
            }
            else if (e.PropertyName is nameof(SessionState.ShowRouteLines))
            {
                RebuildRouteGeometry();
                RebuildAllWedges();
            }
            else if (e.PropertyName is nameof(SessionState.ShowBearingWedges))
            {
                RebuildAllWedges();
            }
            else if (e.PropertyName is nameof(SessionState.Mode))
            {
                // Switching between Survey and Motherlode flips wedge
                // visibility wholesale — Survey hides them, Motherlode shows.
                RebuildAllWedges();
            }
        };

        // Forward FSM state changes so the pin DataTemplate can gate the
        // active-pin halo on Listening (the only phase where SelectedSurvey
        // is meaningful — Gathering uses IsActiveTarget + marching ants).
        _surveyFlow.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SurveyFlowController.CurrentState))
            {
                OnPropertyChanged(nameof(IsListening));
                OnPropertyChanged(nameof(OverlayHint));
                OnPropertyChanged(nameof(IsOverlayHintVisible));
            }
        };
    }

    /// <summary>True iff the survey FSM is in <c>Listening</c>.</summary>
    public bool IsListening => _surveyFlow.CurrentState == SurveyFlowState.Listening;

    /// <summary>#460: true while the wizard <c>Calibrating</c> step has armed
    /// pin-capture on this overlay. The view routes viewport clicks to
    /// <see cref="PairCalibrationClick"/> while this holds.</summary>
    public bool IsCalibrationCapturing => _pinCal?.IsArmed == true;

    /// <summary>Pair a calibration overlay-click with the next pending
    /// <c>ProcessMapPinAdd</c> world coord (turn order). No-op when not
    /// capturing.</summary>
    public void PairCalibrationClick(PixelPoint pixel) => _pinCal?.PairClick(pixel);

    /// <summary>Click-paired calibration markers to render on the overlay
    /// (null when no coordinator — e.g. the test ctor).</summary>
    public System.Collections.ObjectModel.ObservableCollection<PixelPoint>? CalibrationMarkers
        => _pinCal?.PlacedMarkers;

    /// <summary>
    /// Move the currently-nudgeable target by <c>(dx, dy) * step</c>. Routes
    /// to <see cref="SessionState.SelectedSurvey"/> when one is selected and
    /// has a pixel position; falls back to the player anchor while it's still
    /// editable. No-op otherwise. Shared between the keyboard hotkey commands
    /// (NudgePinCommandBase) and the on-screen nudge pad — same semantics, same
    /// commit path through CorrectSurveyCommand / MoveAnchor.
    /// </summary>
    public void Nudge(double dx, double dy, double step)
    {
        var selected = _session.SelectedSurvey;
        if (selected is not null && selected.EffectivePixel.HasValue)
        {
            var p = selected.EffectivePixel.Value;
            CorrectSurveyCommand.Execute(
                new CorrectionArgs(selected, new PixelPoint(p.X + dx * step, p.Y + dy * step)));
        }
    }

    private void OnSurveysCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (SurveyItemViewModel s in e.NewItems)
            {
                s.PropertyChanged += OnSurveyPropertyChanged;
            }
        }
        if (e.OldItems is not null)
        {
            foreach (SurveyItemViewModel s in e.OldItems)
            {
                s.PropertyChanged -= OnSurveyPropertyChanged;
            }
        }
        RebuildRouteGeometry();
        RebuildAllWedges();
    }

    private void OnSurveyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SurveyItemViewModel.Model))
        {
            RebuildRouteGeometry();
            if (sender is SurveyItemViewModel s) RebuildWedgeFor(s);
        }
        else if (e.PropertyName is nameof(SurveyItemViewModel.IsActiveTarget))
        {
            // Active-target change rotates the live segment without changing
            // the rest of the route, so don't churn the full polyline.
            RebuildActiveSegment();
        }
    }

    public ObservableCollection<SurveyItemViewModel> Surveys => _session.Surveys;

    public SessionState Session => _session;

    public SurveyFlowController SurveyFlow => _surveyFlow;

    public LegolasBrushes Brushes => _brushes;

    private static readonly LegolasPinStyle _defaultPinStyle = new();
    private static readonly LegolasPinStyle _defaultPlayerPinStyle = LegolasPinStyle.PlayerDefaults();
    private static readonly LegolasActivePinStyle _defaultActivePinStyle = new();

    /// <summary>Survey pin shape configuration for the DataTemplate. Falls
    /// back to defaults when the simpler test constructor is used (no settings).</summary>
    public LegolasPinStyle PinStyle => _settings?.PinStyle ?? _defaultPinStyle;

    /// <summary>Player anchor pin shape configuration. Same shape model as
    /// <see cref="PinStyle"/> but with player-specific defaults; the player
    /// pin's outer Size is meaningful (drives Thumb bounds) while the survey
    /// pin's outer size still comes from <c>SurveyPinRadiusMetres</c>.</summary>
    public LegolasPinStyle PlayerPinStyle => _settings?.PlayerPinStyle ?? _defaultPlayerPinStyle;

    /// <summary>Active-pin highlight configuration. Falls back to defaults
    /// when the simpler test constructor is used (no settings).</summary>
    public LegolasActivePinStyle ActivePinStyle => _settings?.ActivePinStyle ?? _defaultActivePinStyle;

    /// <summary>
    /// Context-aware on-overlay instruction. Empty during normal use; appears
    /// for the two states where a new user can stall:
    ///  * AwaitingPosition: needs a click to set the anchor.
    ///  * Listening with anchor placed but no surveys yet: the anchor's initial
    ///    projection scale can stick the pin off-screen (#131 follow-up). The
    ///    drag-anywhere gesture rescues it; the hint tells the user how.
    /// Hidden in Gathering/Done where the route geometry speaks for itself.
    /// </summary>
    // #454 retired the anchor-bootstrap states this hint coached through.
    // Absolute placement needs no setup, so there's no stall to rescue — kept
    // as an always-empty property so the view binding stays valid.
    public string OverlayHint => string.Empty;

    public bool IsOverlayHintVisible => !string.IsNullOrEmpty(OverlayHint);

    public PixelPoint PlayerPosition
    {
        get => _session.PlayerPosition;
        set => _session.PlayerPosition = value;
    }

    public bool ShowBearingWedges
    {
        get => _session.ShowBearingWedges;
        set => _session.ShowBearingWedges = value;
    }

    [ObservableProperty]
    private IReadOnlyList<PixelPoint> _routePoints = Array.Empty<PixelPoint>();

    /// <summary>
    /// Two-point polyline drawn on top of the static route line: from the
    /// most-recently-collected pin (or the player anchor if nothing's been
    /// collected yet) to the current <see cref="SurveyItemViewModel.IsActiveTarget"/>
    /// pin. We can't read the player's live position — we only know the anchor —
    /// so the last-collected pin is the closest available proxy for "where the
    /// player physically is right now". Empty when there's no active target.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<PixelPoint> _activeSegmentPoints = Array.Empty<PixelPoint>();

    /// <summary>
    /// Record the player's position from a map click. #454 retired this for
    /// Survey (absolute placement needs no anchor); it survives <b>for
    /// Motherlode</b>, whose triangulation reads
    /// <see cref="SessionState.PlayerPosition"/> via
    /// <c>MotherlodeViewModel.RecordPlayerPosition</c>. No Survey FSM
    /// involvement any more.
    /// </summary>
    [RelayCommand]
    public void SetPlayerPosition(PixelPoint where)
    {
        _session.PlayerPosition = where;
        _session.HasPlayerPosition = true;
        _projector.SetOrigin(where);
    }

    [RelayCommand]
    public void HandleMapClick(PixelPoint where)
    {
        // Survey placement is automatic + absolute (ProcessMapFx) — map clicks
        // do nothing in Survey mode. In Motherlode mode the click records the
        // player position for triangulation.
        if (_session.Mode == SessionMode.Motherlode)
            SetPlayerPosition(where);
    }

    public double Scale => _projector.Scale;
    public double RotationDegrees => _projector.RotationRadians * 180.0 / Math.PI;

    // The slider value is treated as pixel radius for predictable on-screen sizing —
    // multiplying by projector.Scale caused the pin to visibly shrink/grow after
    // every refit, which felt like a bug to the user.
    public double PinRadius => _settings?.SurveyPinRadiusMetres ?? 8.0;
    public double PinDiameter => PinRadius * 2;

    /// <summary>
    /// Drag/nudge a pin to a new pixel. #454: pins are absolute, so this is a
    /// purely local correction of where this one marker draws — it no longer
    /// drives a projector Refit (the relative-calibration model is retired).
    /// </summary>
    [RelayCommand]
    public void CorrectSurvey(CorrectionArgs args)
    {
        var vm = args.Survey;
        vm.UpdateModel(vm.Model with { ManualOverride = args.NewPixel });
        RebuildRouteGeometry();
    }

    [RelayCommand]
    private void OptimizeRoute()
    {
        var points = new List<PixelPoint>();
        var indices = new List<int>();
        for (var i = 0; i < Surveys.Count; i++)
        {
            var s = Surveys[i];
            if (s.Collected || s.Skipped) continue;
            if (!s.EffectivePixel.HasValue) continue;
            points.Add(s.EffectivePixel.Value);
            indices.Add(i);
        }
        if (points.Count == 0) return;

        // #454: no player anchor — the tour starts at the first uncollected
        // pin (the optimiser still finds the best order through the rest).
        var order = _optimizer.Optimize(points[0], points);
        for (var i = 0; i < Surveys.Count; i++)
        {
            Surveys[i].UpdateModel(Surveys[i].Model with { RouteOrder = null });
        }
        for (var i = 0; i < order.Count; i++)
        {
            var src = indices[order[i]];
            Surveys[src].UpdateModel(Surveys[src].Model with { RouteOrder = i });
        }
        RebuildRouteGeometry();
        _surveyFlow.OptimizeRoute();
    }

    private const double WedgeHalfAngleRadians = Math.PI / 8; // 22.5 degrees

    private void RebuildAllWedges()
    {
        foreach (var s in Surveys) RebuildWedgeFor(s);
    }

    private void RebuildWedgeFor(SurveyItemViewModel s)
    {
        // Wedges only render in Motherlode mode. Survey mode's 4-DOF refit
        // (PR #130) lands pins essentially pixel-perfect, so the bearing arc
        // adds no information — the optimised route + the placed pins are
        // sufficient and precise. Motherlode triangulation has no comparable
        // refit, so the bearing arc still narrows the search there.
        if (!_session.ShowBearingWedges
            || _session.Mode != SessionMode.Motherlode
            || s.IsCorrected
            || s.Collected
            || s.Skipped
            || s.Offset.Magnitude < 1e-6)
        {
            s.WedgeArc = null;
            return;
        }

        var distancePx = s.Offset.Magnitude * _projector.Scale;
        if (distancePx < 4)
        {
            s.WedgeArc = null;
            return;
        }

        var bearingOffset = Math.Atan2(s.Offset.East, s.Offset.North);
        var bearing = bearingOffset + _projector.RotationRadians;

        // Raw inputs only; the D2D renderer constructs the arc each frame.
        s.WedgeArc = new WedgeArc(
            Origin: PlayerPosition,
            BearingRadians: bearing,
            HalfAngleRadians: WedgeHalfAngleRadians,
            DistancePx: distancePx);
    }

    private void RebuildRouteGeometry()
    {
        if (!_session.ShowRouteLines)
        {
            RoutePoints = Array.Empty<PixelPoint>();
            ActiveSegmentPoints = Array.Empty<PixelPoint>();
            return;
        }

        var ordered = Surveys
            .Where(s => s.RouteOrder.HasValue && s.EffectivePixel.HasValue)
            .OrderBy(s => s.RouteOrder!.Value)
            .ToList();

        // #454: no player anchor — the route is just the ordered pins.
        var points = new List<PixelPoint>(ordered.Count);
        foreach (var s in ordered) points.Add(s.EffectivePixel!.Value);
        RoutePoints = points;

        RebuildActiveSegment();
    }

    private void RebuildActiveSegment()
    {
        if (!_session.ShowRouteLines)
        {
            ActiveSegmentPoints = Array.Empty<PixelPoint>();
            return;
        }

        var active = Surveys.FirstOrDefault(s => s.IsActiveTarget);
        if (active is null || !active.EffectivePixel.HasValue)
        {
            ActiveSegmentPoints = Array.Empty<PixelPoint>();
            return;
        }

        // #454: no player anchor. The live segment runs from the
        // most-recently-collected pin (best available "where the player is
        // now" proxy) to the active target. Before the first collection
        // there's no start point — just highlight the target, no segment.
        var lastCollected = Surveys
            .Where(s => s.Collected && s.RouteOrder.HasValue && s.EffectivePixel.HasValue)
            .OrderByDescending(s => s.RouteOrder!.Value)
            .FirstOrDefault();

        ActiveSegmentPoints = lastCollected?.EffectivePixel is { } start
            ? new[] { start, active.EffectivePixel.Value }
            : Array.Empty<PixelPoint>();
    }
}

public sealed record CorrectionArgs(SurveyItemViewModel Survey, PixelPoint NewPixel);
