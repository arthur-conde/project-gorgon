using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Legolas.Domain;
using Legolas.Services;

namespace Legolas.ViewModels;

public sealed partial class MapOverlayViewModel : ObservableObject
{
    private readonly SessionState _session;
    private readonly ICoordinateProjector _projector;
    private readonly IRouteOptimizer _optimizer;
    private readonly LegolasSettings? _settings;

    public MapOverlayViewModel(SessionState session, ICoordinateProjector projector, IRouteOptimizer optimizer)
        : this(session, projector, optimizer, settings: null) { }

    public MapOverlayViewModel(SessionState session, ICoordinateProjector projector, IRouteOptimizer optimizer, LegolasSettings? settings)
    {
        _session = session;
        _projector = projector;
        _optimizer = optimizer;
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
    }

    public ObservableCollection<SurveyItemViewModel> Surveys => _session.Surveys;

    public SessionState Session => _session;

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

    [ObservableProperty] private double _zoom = 1.0;
    [ObservableProperty] private double _panX;
    [ObservableProperty] private double _panY;

    [RelayCommand]
    private void ResetView()
    {
        Zoom = 1.0;
        PanX = 0;
        PanY = 0;
    }

    [RelayCommand]
    public void SetPlayerPosition(PixelPoint where)
    {
        _session.PlayerPosition = where;
        _session.HasPlayerPosition = true;
        _projector.SetOrigin(where);
        ReprojectUncorrected();
    }

    [RelayCommand]
    public void HandleMapClick(PixelPoint where)
    {
        switch (_session.SurveyPhase)
        {
            case SurveyPhase.Idle:
                SetPlayerPosition(where);
                _session.SurveyPhase = SurveyPhase.Surveying;
                break;
            case SurveyPhase.Surveying:
                // No-op: prevents accidental player re-set mid-session.
                break;
            case SurveyPhase.AwaitingPin when _session.PendingSurvey is { } pending:
                PlacePendingPinAt(where);
                break;
        }
    }

    /// <summary>
    /// Place the pending survey's pin at <paramref name="where"/> and return
    /// the new VM so the caller can keep dragging it. Returns null if there
    /// is nothing pending.
    /// </summary>
    public SurveyItemViewModel? PlacePendingPinAt(PixelPoint where)
    {
        if (_session.SurveyPhase != SurveyPhase.AwaitingPin) return null;
        if (_session.PendingSurvey is not { } pending) return null;
        var placed = PlacePin(pending, where);
        _session.PendingSurvey = null;
        _session.SurveyPhase = SurveyPhase.Surveying;
        return placed;
    }

    private SurveyItemViewModel PlacePin(SurveyDetected sd, PixelPoint click)
    {
        var index = _session.Surveys.Count;
        var pixel = _projector.Project(sd.Offset);
        var survey = Survey.Create(sd.Name, sd.Offset, gridIndex: index)
            with { PixelPos = pixel, ManualOverride = click };
        var vm = new SurveyItemViewModel(survey);
        _session.Surveys.Add(vm);

        var corrections = Surveys
            .Where(s => s.Model.ManualOverride.HasValue)
            .Select(s => (s.Offset, s.Model.ManualOverride!.Value))
            .ToArray();
        if (corrections.Length >= 2)
        {
            _projector.Refit(corrections);
            ReprojectUncorrected();
            OnPropertyChanged(nameof(Scale));
            OnPropertyChanged(nameof(RotationDegrees));
            OnPropertyChanged(nameof(PinRadius));
            OnPropertyChanged(nameof(PinDiameter));
        }
        RebuildRouteGeometry();
        return vm;
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

        var corrections = Surveys
            .Where(s => s.IsCorrected && s.Model.ManualOverride.HasValue)
            .Select(s => (s.Offset, s.Model.ManualOverride!.Value))
            .ToArray();

        if (corrections.Length >= 2)
        {
            _projector.Refit(corrections);
            ReprojectUncorrected();
            OnPropertyChanged(nameof(Scale));
            OnPropertyChanged(nameof(RotationDegrees));
            OnPropertyChanged(nameof(PinRadius));
            OnPropertyChanged(nameof(PinDiameter));
        }

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
    }
}

public sealed record CorrectionArgs(SurveyItemViewModel Survey, PixelPoint NewPixel);
