using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Legolas.Domain;

namespace Legolas.ViewModels;

/// <summary>
/// Shared, session-scoped state referenced by both overlays and the control panel.
/// Everything here dies with the session (new Set Position wipes it).
/// </summary>
public sealed partial class SessionState : ObservableObject
{
    public ObservableCollection<SurveyItemViewModel> Surveys { get; } = new();

    public SessionState()
    {
        Surveys.CollectionChanged += OnSurveysChanged;
    }

    private void OnSurveysChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (SurveyItemViewModel s in e.NewItems)
                s.PropertyChanged += OnSurveyPropertyChanged;
        if (e.OldItems is not null)
            foreach (SurveyItemViewModel s in e.OldItems)
                s.PropertyChanged -= OnSurveyPropertyChanged;
        RecalculateActiveTarget();
    }

    private void OnSurveyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SurveyItemViewModel.Collected)
            or nameof(SurveyItemViewModel.Skipped)
            or nameof(SurveyItemViewModel.RouteOrder))
            RecalculateActiveTarget();
    }

    public event Action? AllCollected;

    public void RecalculateActiveTarget()
    {
        SurveyItemViewModel? next = Surveys
            .Where(s => !s.Collected && !s.Skipped)
            .OrderBy(s => s.RouteOrder ?? int.MaxValue)
            .FirstOrDefault();
        foreach (var s in Surveys)
            s.IsActiveTarget = ReferenceEquals(s, next);

        // Fire only on the transition: non-empty collection where nothing is
        // uncollected. After a reset (Surveys becomes empty) this doesn't re-fire.
        if (next is null && Surveys.Count > 0)
            AllCollected?.Invoke();
    }

    [ObservableProperty] private PixelPoint _playerPosition = new(400, 300);
    [ObservableProperty] private bool _hasPlayerPosition;
    [ObservableProperty] private string _lastLogEvent = "(waiting)";
    [ObservableProperty] private bool _showBearingWedges = true;
    [ObservableProperty] private bool _showRouteLines = true;
    [ObservableProperty] private SessionMode _mode = SessionMode.Survey;
    [ObservableProperty] private double _mapOpacity = 1.0;
    [ObservableProperty] private double _inventoryOpacity = 1.0;
    [ObservableProperty] private bool _isMapVisible;
    [ObservableProperty] private bool _isInventoryVisible;

    [ObservableProperty]
    private SurveyDetected? _pendingSurvey;

    [ObservableProperty]
    private SurveyItemViewModel? _selectedSurvey;

    public void ClearSurveys()
    {
        Surveys.Clear();
    }
}

public enum SessionMode
{
    Survey,
    Motherlode
}
