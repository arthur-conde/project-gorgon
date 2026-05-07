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
        };
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
        if (!_session.ShowBearingWedges || s.IsCorrected || s.Offset.Magnitude < 1e-6)
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
