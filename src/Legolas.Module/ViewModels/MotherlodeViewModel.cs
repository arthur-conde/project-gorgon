using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Legolas.Domain;
using Legolas.Services;

namespace Legolas.ViewModels;

/// <summary>
/// Motherlode mode: trilateration-driven location of treasures from three
/// distance measurements taken at three different player positions. Uses the
/// existing <see cref="ITrilaterationSolver"/> and <see cref="IRouteOptimizer"/>.
/// </summary>
public sealed partial class MotherlodeViewModel : ObservableObject
{
    private readonly ITrilaterationSolver _trilateration;
    private readonly IRouteOptimizer _optimizer;
    private readonly SessionState _session;
    private readonly MotherlodeSession _state = new();

    public MotherlodeViewModel(ITrilaterationSolver trilateration, IRouteOptimizer optimizer, SessionState session)
    {
        _trilateration = trilateration;
        _optimizer = optimizer;
        _session = session;
    }

    public ObservableCollection<MotherlodeSlotViewModel> Slots { get; } = new();

    public int CurrentRound => _state.PlayerPositions.Count;

    public int RecordedPositions => _state.PlayerPositions.Count;

    [ObservableProperty] private int _distanceInput;

    [RelayCommand]
    private void RecordPlayerPosition()
    {
        _state.PlayerPositions.Add(_session.PlayerPosition);
        OnPropertyChanged(nameof(CurrentRound));
        OnPropertyChanged(nameof(RecordedPositions));
    }

    [RelayCommand]
    private void RecordCurrentDistance() => RecordDistance(DistanceInput);

    [RelayCommand]
    private void RecordDistance(int distanceMetres)
    {
        // Append to current round; a new survey is added when no slots exist for this round
        if (Slots.Count == 0)
        {
            var ms = MotherlodeSurvey.Create();
            _state.Surveys.Add(ms);
            Slots.Add(new MotherlodeSlotViewModel(ms));
        }
        var slot = Slots[Slots.Count - 1];
        slot.AppendDistance(distanceMetres);

        // After 3 rounds with positions recorded, trilaterate
        if (_state.PlayerPositions.Count >= 3 && slot.Distances.Count >= 3)
        {
            var p1 = _state.PlayerPositions[0];
            var p2 = _state.PlayerPositions[1];
            var p3 = _state.PlayerPositions[2];
            var estimate = _trilateration.Solve(
                p1, slot.Distances[0],
                p2, slot.Distances[1],
                p3, slot.Distances[2]);
            slot.EstimatedPosition = estimate;
        }
    }

    [RelayCommand]
    private void OptimizeRoute()
    {
        var indices = new List<int>();
        var points = new List<PixelPoint>();
        for (var i = 0; i < Slots.Count; i++)
        {
            var s = Slots[i];
            if (s.Collected || s.EstimatedPosition is null) continue;
            indices.Add(i);
            points.Add(s.EstimatedPosition.Value);
        }
        if (points.Count == 0) return;

        var order = _optimizer.Optimize(_session.PlayerPosition, points);
        foreach (var slot in Slots) slot.RouteOrder = null;
        for (var i = 0; i < order.Count; i++)
        {
            Slots[indices[order[i]]].RouteOrder = i;
        }
    }

    [RelayCommand]
    private void Reset()
    {
        _state.PlayerPositions.Clear();
        _state.Surveys.Clear();
        _state.CurrentRound = 0;
        Slots.Clear();
        DistanceInput = 0;
        OnPropertyChanged(nameof(CurrentRound));
        OnPropertyChanged(nameof(RecordedPositions));
    }
}

public sealed partial class MotherlodeSlotViewModel : ObservableObject
{
    public MotherlodeSlotViewModel(MotherlodeSurvey model)
    {
        Id = model.Id;
        Distances = new ObservableCollection<int>(model.DistancesByRound);
        EstimatedPosition = model.EstimatedPosition;
        Collected = model.Collected;
        RouteOrder = model.RouteOrder;
    }

    public Guid Id { get; }
    public ObservableCollection<int> Distances { get; }

    [ObservableProperty] private PixelPoint? _estimatedPosition;
    [ObservableProperty] private bool _collected;
    [ObservableProperty] private int? _routeOrder;

    public void AppendDistance(int metres) => Distances.Add(metres);
}
