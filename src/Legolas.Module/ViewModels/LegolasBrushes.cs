using System.ComponentModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Legolas.Domain;

namespace Legolas.ViewModels;

/// <summary>
/// View-model wrapper around <see cref="LegolasColors"/> exposing pre-built
/// <see cref="SolidColorBrush"/> instances for XAML bindings. WPF resource
/// dictionaries don't observe POCO settings cleanly, so views bind through
/// this object instead and we re-emit a fresh brush whenever the underlying
/// hex changes.
/// </summary>
public sealed class LegolasBrushes : ObservableObject
{
    private readonly LegolasColors _colors;

    public LegolasBrushes(LegolasColors colors)
    {
        _colors = colors;
        _colors.PropertyChanged += OnColorsPropertyChanged;
    }

    public SolidColorBrush PinPending => Build(_colors.PinPending);
    public SolidColorBrush PinFinalized => Build(_colors.PinFinalized);
    public SolidColorBrush PlayerMarker => Build(_colors.PlayerMarker);
    public SolidColorBrush RouteLine => Build(_colors.RouteLine);
    public SolidColorBrush BearingWedgeFill => Build(_colors.BearingWedgeFill);
    public SolidColorBrush BearingWedgeStroke => Build(_colors.BearingWedgeStroke);

    private static SolidColorBrush Build(string hex)
    {
        var brush = new SolidColorBrush(Parse(hex));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Parse the canonical 8-digit ARGB hex written by <see cref="LegolasColors.Normalize"/>.
    /// Falls back to opaque magenta on malformed input — the same fail-loud
    /// fallback as the settings layer, so a bad value is visible everywhere.
    /// </summary>
    public static Color Parse(string hex)
    {
        var normalized = LegolasColors.Normalize(hex);
        var s = normalized.Substring(1); // strip '#'
        var a = byte.Parse(s.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
        var r = byte.Parse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
        var g = byte.Parse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
        var b = byte.Parse(s.Substring(6, 2), System.Globalization.NumberStyles.HexNumber);
        return Color.FromArgb(a, r, g, b);
    }

    private void OnColorsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Forward each color property → matching brush property notification.
        // Names line up 1:1, so re-emit the same name and bound views re-fetch.
        if (!string.IsNullOrEmpty(e.PropertyName))
            OnPropertyChanged(e.PropertyName);
    }
}
