using System.ComponentModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Legolas.Domain;

namespace Legolas.ViewModels;

/// <summary>
/// View-model wrapper around <see cref="LegolasSettings"/> exposing pre-built
/// <see cref="SolidColorBrush"/> instances for XAML bindings. WPF resource
/// dictionaries don't observe POCO settings cleanly, so views bind through
/// this object instead and we re-emit a fresh brush whenever the underlying
/// hex changes. Takes the whole <see cref="LegolasSettings"/> so we don't have
/// to register every sub-state class with DI individually.
/// </summary>
public sealed class LegolasBrushes : ObservableObject
{
    private readonly LegolasSettings _settings;

    public LegolasBrushes(LegolasSettings settings)
    {
        _settings = settings;
        _settings.Colors.PropertyChanged += OnColorsChanged;
        _settings.PinStyle.Outer.PropertyChanged += OnSurveyOuterChanged;
        _settings.PinStyle.Center.PropertyChanged += OnSurveyCenterChanged;
        _settings.PlayerPinStyle.Outer.PropertyChanged += OnPlayerOuterChanged;
        _settings.PlayerPinStyle.Center.PropertyChanged += OnPlayerCenterChanged;
        _settings.ActivePinStyle.PropertyChanged += OnActiveChanged;
    }

    public SolidColorBrush RouteLine => Build(_settings.Colors.RouteLine);
    public SolidColorBrush BearingWedgeFill => Build(_settings.Colors.BearingWedgeFill);
    public SolidColorBrush BearingWedgeStroke => Build(_settings.Colors.BearingWedgeStroke);

    public SolidColorBrush OuterFill => Build(_settings.PinStyle.Outer.FillColor);
    public SolidColorBrush OuterStroke => Build(_settings.PinStyle.Outer.StrokeColor);
    public SolidColorBrush CenterFill => Build(_settings.PinStyle.Center.FillColor);
    public SolidColorBrush CenterStroke => Build(_settings.PinStyle.Center.StrokeColor);

    public SolidColorBrush PlayerOuterFill => Build(_settings.PlayerPinStyle.Outer.FillColor);
    public SolidColorBrush PlayerOuterStroke => Build(_settings.PlayerPinStyle.Outer.StrokeColor);
    public SolidColorBrush PlayerCenterFill => Build(_settings.PlayerPinStyle.Center.FillColor);
    public SolidColorBrush PlayerCenterStroke => Build(_settings.PlayerPinStyle.Center.StrokeColor);

    public SolidColorBrush ActivePin => Build(_settings.ActivePinStyle.Color);

    private static SolidColorBrush Build(string hex)
    {
        var brush = new SolidColorBrush(Parse(hex));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Parse the canonical 8-digit ARGB hex written by <see cref="HexColor.Normalize"/>.
    /// Falls back to opaque magenta on malformed input — the same fail-loud
    /// fallback as the settings layer, so a bad value is visible everywhere.
    /// </summary>
    public static Color Parse(string hex)
    {
        var normalized = HexColor.Normalize(hex);
        var s = normalized.Substring(1); // strip '#'
        var a = byte.Parse(s.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
        var r = byte.Parse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
        var g = byte.Parse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
        var b = byte.Parse(s.Substring(6, 2), System.Globalization.NumberStyles.HexNumber);
        return Color.FromArgb(a, r, g, b);
    }

    private void OnColorsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName))
            OnPropertyChanged(e.PropertyName);
    }

    private void OnSurveyOuterChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LegolasPinShapeStyle.FillColor): OnPropertyChanged(nameof(OuterFill)); break;
            case nameof(LegolasPinShapeStyle.StrokeColor): OnPropertyChanged(nameof(OuterStroke)); break;
        }
    }

    private void OnSurveyCenterChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LegolasPinShapeStyle.FillColor): OnPropertyChanged(nameof(CenterFill)); break;
            case nameof(LegolasPinShapeStyle.StrokeColor): OnPropertyChanged(nameof(CenterStroke)); break;
        }
    }

    private void OnPlayerOuterChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LegolasPinShapeStyle.FillColor): OnPropertyChanged(nameof(PlayerOuterFill)); break;
            case nameof(LegolasPinShapeStyle.StrokeColor): OnPropertyChanged(nameof(PlayerOuterStroke)); break;
        }
    }

    private void OnPlayerCenterChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LegolasPinShapeStyle.FillColor): OnPropertyChanged(nameof(PlayerCenterFill)); break;
            case nameof(LegolasPinShapeStyle.StrokeColor): OnPropertyChanged(nameof(PlayerCenterStroke)); break;
        }
    }

    private void OnActiveChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LegolasActivePinStyle.Color))
            OnPropertyChanged(nameof(ActivePin));
    }
}
