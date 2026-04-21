using System.ComponentModel;

namespace Legolas.Domain;

public sealed class LegolasSettings : INotifyPropertyChanged
{
    private double _surveyDedupRadiusMetres = 5.0;
    public double SurveyDedupRadiusMetres
    {
        get => _surveyDedupRadiusMetres;
        set
        {
            var clamped = Math.Max(0, value);
            if (Math.Abs(_surveyDedupRadiusMetres - clamped) < 1e-6) return;
            _surveyDedupRadiusMetres = clamped;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SurveyDedupRadiusMetres)));
        }
    }

    private double _surveyPinRadiusMetres = 8.0;
    public double SurveyPinRadiusMetres
    {
        get => _surveyPinRadiusMetres;
        set
        {
            var clamped = Math.Clamp(value, 0.5, 100.0);
            if (Math.Abs(_surveyPinRadiusMetres - clamped) < 1e-6) return;
            _surveyPinRadiusMetres = clamped;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SurveyPinRadiusMetres)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public InventoryGridSettings InventoryGrid { get; set; } = new();
    public WindowLayout MapOverlay { get; set; } = new() { Width = 800, Height = 600 };
    public WindowLayout InventoryOverlay { get; set; } = new() { Width = 540, Height = 440 };
    public double MapOpacity { get; set; } = 1.0;
    public double InventoryOpacity { get; set; } = 1.0;
    public bool InvertDirections { get; set; }

    private bool _clickThroughInventory;
    public bool ClickThroughInventory
    {
        get => _clickThroughInventory;
        set
        {
            if (_clickThroughInventory == value) return;
            _clickThroughInventory = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ClickThroughInventory)));
        }
    }

    private bool _clickThroughMap;
    public bool ClickThroughMap
    {
        get => _clickThroughMap;
        set
        {
            if (_clickThroughMap == value) return;
            _clickThroughMap = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ClickThroughMap)));
        }
    }
    public bool ShowBearingWedges { get; set; } = true;
    public bool ShowRouteLabels { get; set; } = true;
    public int SurveyCount { get; set; } = 6;

    private bool _autoResetWhenAllCollected = true;
    public bool AutoResetWhenAllCollected
    {
        get => _autoResetWhenAllCollected;
        set
        {
            if (_autoResetWhenAllCollected == value) return;
            _autoResetWhenAllCollected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoResetWhenAllCollected)));
        }
    }
}

public sealed class WindowLayout
{
    public double Left { get; set; } = 100;
    public double Top { get; set; } = 100;
    public double Width { get; set; } = 400;
    public double Height { get; set; } = 300;
}
