using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;

namespace Legolas.ViewModels;

/// <summary>
/// Motherlode mode (#488): a thin, read-only projection of
/// <see cref="MotherlodeMeasurementCoordinator"/>. All input is log-driven —
/// the ChatLog distance line, the Player.log use gesture, and position feeders
/// — so this VM no longer takes manual distance/position entry. It rebuilds
/// its slot list on each coordinator change and owns only route optimization
/// (calibration-free world-space ordering) and reset.
/// </summary>
public sealed partial class MotherlodeViewModel : ObservableObject, IDisposable
{
    private readonly MotherlodeMeasurementCoordinator _coordinator;
    private readonly IRouteOptimizer _optimizer;
    private readonly MotherlodeFlowController _flow;

    public MotherlodeViewModel(
        MotherlodeMeasurementCoordinator coordinator,
        IRouteOptimizer optimizer,
        MotherlodeFlowController flow)
    {
        _coordinator = coordinator;
        _optimizer = optimizer;
        _flow = flow;
        _coordinator.Changed += OnCoordinatorChanged;
        Rebuild();
    }

    public MotherlodeFlowController Flow => _flow;

    public ObservableCollection<MotherlodeSlotViewModel> Slots { get; } = new();

    [ObservableProperty] private int _locationCount;
    [ObservableProperty] private int _locationsWithFix;
    [ObservableProperty] private string? _guidance;

    /// <summary>Treasures with a confident fix so far — the headline number.</summary>
    public int SolvedCount => Slots.Count(s => s.HasFix);

    private void OnCoordinatorChanged()
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) Rebuild();
        else d.InvokeAsync(Rebuild);
    }

    private void Rebuild()
    {
        var snap = _coordinator.Snapshot();
        Slots.Clear();
        foreach (var s in snap.Surveys)
            Slots.Add(new MotherlodeSlotViewModel(s));
        LocationCount = snap.LocationCount;
        LocationsWithFix = snap.LocationsWithFix;
        Guidance = snap.Guidance;
        OnPropertyChanged(nameof(SolvedCount));
    }

    [RelayCommand]
    private void OptimizeRoute()
    {
        var snap = _coordinator.Snapshot();
        var ids = new List<Guid>();
        var points = new List<PixelPoint>();   // world (X,Z); routing is similarity-invariant
        foreach (var s in snap.Surveys)
        {
            if (s.Collected || s.SolvedWorld is not { } w) continue;
            ids.Add(s.Id);
            points.Add(new PixelPoint(w.X, w.Z));
        }
        if (points.Count == 0) return;

        var start = snap.LastPlayerWorld is { } p
            ? new PixelPoint(p.X, p.Z)
            : points[0];
        var order = _optimizer.Optimize(start, points);
        _coordinator.ApplyRouteOrder(order.Select(i => ids[i]).ToList());
    }

    [RelayCommand]
    private void Reset() => _coordinator.Reset();

    public void Dispose() => _coordinator.Changed -= OnCoordinatorChanged;
}

/// <summary>Read-only per-treasure row for the wizard panel.</summary>
public sealed class MotherlodeSlotViewModel
{
    public MotherlodeSlotViewModel(MotherlodeSurvey m)
    {
        Id = m.Id;
        Collected = m.Collected;
        RouteOrder = m.RouteOrder;
        DistanceCount = m.DistancesByLocation.Count(d => d > 0);
        HasFix = m.SolvedWorld is not null;
        SolvedText = m.SolvedWorld is { } w
            ? $"({w.X:0}, {w.Z:0})"
            : DistanceCount < 3
                ? $"locating… ({DistanceCount}/3 readings)"
                : "locating…";
        GdopText = m.Gdop is { } g ? $"GDOP {g:0.0}" : null;
        ResidualText = m.ResidualRms is { } r ? $"±{r:0.0} m fit" : null;
    }

    public Guid Id { get; }
    public bool Collected { get; }
    public int? RouteOrder { get; }
    public int DistanceCount { get; }
    public bool HasFix { get; }
    public string SolvedText { get; }
    public string? GdopText { get; }
    public string? ResidualText { get; }
}
