using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
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

    public MapOverlayViewModel(SessionState session, ICoordinateProjector projector, IRouteOptimizer optimizer, SurveyFlowController surveyFlow, LegolasBrushes brushes)
        : this(session, projector, optimizer, surveyFlow, brushes, settings: null) { }

    public MapOverlayViewModel(SessionState session, ICoordinateProjector projector, IRouteOptimizer optimizer, SurveyFlowController surveyFlow, LegolasBrushes brushes, LegolasSettings? settings)
    {
        _session = session;
        _projector = projector;
        _optimizer = optimizer;
        _surveyFlow = surveyFlow;
        _brushes = brushes;
        _settings = settings;
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
            else if (e.PropertyName is nameof(SessionState.IsAnchorEditable)
                  or nameof(SessionState.HasPlayerPosition))
            {
                // Anchor-editable state gates the "drag to refine" hint.
                OnPropertyChanged(nameof(OverlayHint));
                OnPropertyChanged(nameof(IsOverlayHintVisible));
            }
        };

        // Forward FSM state changes so the pin DataTemplate can gate the
        // active-pin halo on Listening (the only phase where SelectedSurvey
        // is meaningful — Gathering uses IsActiveTarget + marching ants).
        // RebuildAllWedges on transition catches the post-Optimize case
        // where surveys that arrived after the route was computed have
        // RouteOrder=null and therefore aren't cleared by Optimize itself.
        _surveyFlow.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SurveyFlowController.CurrentState))
            {
                OnPropertyChanged(nameof(IsListening));
                OnPropertyChanged(nameof(OverlayHint));
                OnPropertyChanged(nameof(IsOverlayHintVisible));
                RebuildAllWedges();
            }
        };
    }

    /// <summary>True iff the survey FSM is in <c>Listening</c>.</summary>
    public bool IsListening => _surveyFlow.CurrentState == SurveyFlowState.Listening;

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
            return;
        }

        if (_session.IsAnchorEditable)
        {
            var p = _session.PlayerPosition;
            MoveAnchor(new PixelPoint(p.X + dx * step, p.Y + dy * step));
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
    public string OverlayHint
    {
        get
        {
            return _surveyFlow.CurrentState switch
            {
                SurveyFlowState.AwaitingPosition => "Click anywhere on this map to mark your player position.",
                SurveyFlowState.Listening when _session.IsAnchorEditable
                    => "Drag the map to fine-tune your position. Use the Surveying skill in-game — pins place automatically.",
                _ => string.Empty,
            };
        }
    }

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
    private PointCollection _routePoints = new();

    /// <summary>
    /// Two-point polyline drawn on top of the static route line: from the
    /// most-recently-collected pin (or the player anchor if nothing's been
    /// collected yet) to the current <see cref="SurveyItemViewModel.IsActiveTarget"/>
    /// pin. We can't read the player's live position — we only know the anchor —
    /// so the last-collected pin is the closest available proxy for "where the
    /// player physically is right now". Empty when there's no active target.
    /// </summary>
    [ObservableProperty]
    private PointCollection _activeSegmentPoints = new();

    [RelayCommand]
    public void SetPlayerPosition(PixelPoint where)
    {
        _session.PlayerPosition = where;
        _session.HasPlayerPosition = true;
        _projector.SetOrigin(where);
        ReprojectUncorrected();
        _surveyFlow.ConfirmPlayerPosition();
    }

    /// <summary>
    /// Reposition the anchor without crossing FSM states. Only valid while
    /// <see cref="SessionState.IsAnchorEditable"/>; intended for drag/nudge
    /// fine-tuning between <see cref="SetPlayerPosition"/> and the first survey.
    /// No reproject is needed (no surveys exist yet); wedges and route geometry
    /// rebuild via the existing <c>PlayerPosition</c> change handler in the ctor.
    /// </summary>
    public void MoveAnchor(PixelPoint where)
    {
        if (!_session.IsAnchorEditable) return;
        _session.PlayerPosition = where;
        _projector.SetOrigin(where);
    }

    [RelayCommand]
    public void HandleMapClick(PixelPoint where)
    {
        // The only state where a map click means "do something" is AwaitingPosition,
        // where the click sets the projector anchor. Surveys auto-place; user
        // corrections are exclusively drag/nudge gestures on existing pins.
        if (_surveyFlow.CurrentState == SurveyFlowState.AwaitingPosition)
            SetPlayerPosition(where);
    }

    /// <summary>
    /// Recomputes the projector from any pins with <see cref="Survey.ManualOverride"/>
    /// set, propagates the refitted origin back to <see cref="SessionState.PlayerPosition"/>
    /// so the on-screen anchor follows the projector's belief, and reprojects every
    /// uncorrected pin. No-op below the 2-correction threshold.
    /// </summary>
    private void TryRefitFromCorrections()
    {
        var corrections = Surveys
            .Where(s => s.Model.ManualOverride.HasValue)
            .Select(s => (s.Offset, s.Model.ManualOverride!.Value))
            .ToArray();
        if (corrections.Length < 2) return;

        _projector.Refit(corrections);
        // Projector origin may have moved during the 4-DOF refit. Sync the visible
        // anchor so the player marker keeps representing "where the projector
        // thinks the player is", not the user's stale initial click.
        _session.PlayerPosition = _projector.Origin;
        ReprojectUncorrected();
        OnPropertyChanged(nameof(Scale));
        OnPropertyChanged(nameof(RotationDegrees));
        OnPropertyChanged(nameof(PinRadius));
        OnPropertyChanged(nameof(PinDiameter));
    }

    public double Scale => _projector.Scale;
    public double RotationDegrees => _projector.RotationRadians * 180.0 / Math.PI;

    // The slider value is treated as pixel radius for predictable on-screen sizing —
    // multiplying by projector.Scale caused the pin to visibly shrink/grow after
    // every refit, which felt like a bug to the user.
    public double PinRadius => _settings?.SurveyPinRadiusMetres ?? 8.0;
    public double PinDiameter => PinRadius * 2;

    [RelayCommand]
    public void CorrectSurvey(CorrectionArgs args)
    {
        var vm = args.Survey;
        var updated = vm.Model with { ManualOverride = args.NewPixel };
        vm.UpdateModel(updated);

        TryRefitFromCorrections();
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

        var order = _optimizer.Optimize(PlayerPosition, points);
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

    private void ReprojectUncorrected()
    {
        foreach (var s in Surveys)
        {
            if (s.IsCorrected) continue;
            var pixel = _projector.Project(s.Offset);
            s.UpdateModel(s.Model with { PixelPos = pixel });
        }
    }

    private const double WedgeHalfAngleRadians = Math.PI / 8; // 22.5 degrees

    private void RebuildAllWedges()
    {
        foreach (var s in Surveys) RebuildWedgeFor(s);
    }

    private void RebuildWedgeFor(SurveyItemViewModel s)
    {
        // Wedges visualize bearing uncertainty before placement is confirmed.
        // After PR #130's auto-place flow, IsCorrected stays false even for
        // confidently-placed pins, so we also hide wedges:
        //   * once the pin is routed (post-Optimize) or collected/skipped,
        //   * any time the FSM has left Listening — Gathering/Done shouldn't
        //     show wedges at all, even for surveys that arrived after the
        //     most recent Optimize and therefore have RouteOrder=null.
        if (!_session.ShowBearingWedges
            || _surveyFlow.CurrentState != SurveyFlowState.Listening
            || s.IsCorrected
            || s.RouteOrder.HasValue
            || s.Collected
            || s.Skipped
            || s.Offset.Magnitude < 1e-6)
        {
            s.WedgeGeometry = null;
            return;
        }

        var distancePx = s.Offset.Magnitude * _projector.Scale;
        if (distancePx < 4)
        {
            s.WedgeGeometry = null;
            return;
        }

        var bearingOffset = Math.Atan2(s.Offset.East, s.Offset.North);
        var bearing = bearingOffset + _projector.RotationRadians;

        var bMin = bearing - WedgeHalfAngleRadians;
        var bMax = bearing + WedgeHalfAngleRadians;
        var origin = new Point(PlayerPosition.X, PlayerPosition.Y);
        var pStart = new Point(
            origin.X + distancePx * Math.Sin(bMin),
            origin.Y - distancePx * Math.Cos(bMin));
        var pEnd = new Point(
            origin.X + distancePx * Math.Sin(bMax),
            origin.Y - distancePx * Math.Cos(bMax));

        var figure = new PathFigure { StartPoint = origin, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment(pStart, isStroked: false));
        figure.Segments.Add(new ArcSegment(
            point: pEnd,
            size: new Size(distancePx, distancePx),
            rotationAngle: 0,
            isLargeArc: false,
            sweepDirection: SweepDirection.Clockwise,
            isStroked: false));
        figure.Segments.Add(new LineSegment(origin, isStroked: false));

        var geom = new PathGeometry();
        geom.Figures.Add(figure);
        geom.Freeze();
        s.WedgeGeometry = geom;
    }

    private void RebuildRouteGeometry()
    {
        if (!_session.ShowRouteLines)
        {
            RoutePoints = new PointCollection();
            ActiveSegmentPoints = new PointCollection();
            return;
        }

        var ordered = Surveys
            .Where(s => s.RouteOrder.HasValue && s.EffectivePixel.HasValue)
            .OrderBy(s => s.RouteOrder!.Value)
            .ToList();

        var points = new PointCollection { new Point(PlayerPosition.X, PlayerPosition.Y) };
        foreach (var s in ordered)
        {
            var p = s.EffectivePixel!.Value;
            points.Add(new Point(p.X, p.Y));
        }
        RoutePoints = points;

        RebuildActiveSegment();
    }

    private void RebuildActiveSegment()
    {
        if (!_session.ShowRouteLines)
        {
            ActiveSegmentPoints = new PointCollection();
            return;
        }

        var active = Surveys.FirstOrDefault(s => s.IsActiveTarget);
        if (active is null || !active.EffectivePixel.HasValue)
        {
            ActiveSegmentPoints = new PointCollection();
            return;
        }

        // We can't read the live player position — only the anchor set via
        // "Set Player Position". Use the most-recently-collected pin (highest
        // RouteOrder among collected) as a proxy for where the player is now;
        // fall back to the anchor before the first collection.
        var lastCollected = Surveys
            .Where(s => s.Collected && s.RouteOrder.HasValue && s.EffectivePixel.HasValue)
            .OrderByDescending(s => s.RouteOrder!.Value)
            .FirstOrDefault();

        var startX = lastCollected?.EffectivePixel?.X ?? PlayerPosition.X;
        var startY = lastCollected?.EffectivePixel?.Y ?? PlayerPosition.Y;
        var endPx = active.EffectivePixel.Value;

        ActiveSegmentPoints = new PointCollection
        {
            new Point(startX, startY),
            new Point(endPx.X, endPx.Y),
        };
    }
}

public sealed record CorrectionArgs(SurveyItemViewModel Survey, PixelPoint NewPixel);
