using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Legolas.Domain;

namespace Legolas.ViewModels;

public sealed partial class InventoryOverlayViewModel : ObservableObject
{
    private readonly SessionState _session;

    public InventoryOverlayViewModel(InventoryGridSettings grid, SessionState session)
    {
        Grid = grid;
        _session = session;
        Slots = CollectionViewSource.GetDefaultView(_session.Surveys);
        Slots.Filter = item => item is SurveyItemViewModel s && !s.Collected;

        // ICollectionView doesn't auto-refresh when an item's filtered property
        // changes — we have to nudge it whenever Collected flips.
        _session.Surveys.CollectionChanged += OnSurveysChanged;
        foreach (var s in _session.Surveys)
            s.PropertyChanged += OnItemChanged;
    }

    private void OnSurveysChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (SurveyItemViewModel s in e.NewItems)
                s.PropertyChanged += OnItemChanged;
        if (e.OldItems is not null)
            foreach (SurveyItemViewModel s in e.OldItems)
                s.PropertyChanged -= OnItemChanged;
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SurveyItemViewModel.Collected))
            Slots.Refresh();
    }

    public InventoryGridSettings Grid { get; }

    public ICollectionView Slots { get; }

    [ObservableProperty]
    private bool _isVisible = true;

    public SessionState Session => _session;

    private readonly Random _rng = new(Seed: 1);
    private static readonly string[] _sampleNames =
    {
        "Sapphire", "Ruby", "Emerald", "Diamond", "Topaz",
        "Amethyst", "Pearl", "Opal", "Jade", "Garnet"
    };

    [RelayCommand]
    private void AddDebugSlot()
    {
        var index = _session.Surveys.Count;
        var name = _sampleNames[index % _sampleNames.Length];
        var bearing = _rng.NextDouble() * 2 * Math.PI;
        var distance = 30 + _rng.NextDouble() * 120;
        var offset = new MetreOffset(Math.Sin(bearing) * distance, Math.Cos(bearing) * distance);

        // 3 px/m default scale until calibrated
        var pixel = new PixelPoint(
            _session.PlayerPosition.X + offset.East * 3,
            _session.PlayerPosition.Y - offset.North * 3);

        var survey = Survey.Create(name, offset, gridIndex: index) with { PixelPos = pixel };
        _session.Surveys.Add(new SurveyItemViewModel(survey));
    }

    [RelayCommand]
    private void ClearSlots() => _session.ClearSurveys();
}
