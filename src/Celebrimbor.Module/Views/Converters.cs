using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Celebrimbor.Domain;
using Celebrimbor.ViewModels;

namespace Celebrimbor.Views;

public sealed class BoolToCraftReadyBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Ready = (SolidColorBrush)new BrushConverter().ConvertFromString("#FF7FB069")!;
    private static readonly SolidColorBrush Needs = (SolidColorBrush)new BrushConverter().ConvertFromString("#FFD4A847")!;

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool ready && ready ? Ready : Needs;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class LocationsToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IReadOnlyList<IngredientLocation> list || list.Count == 0) return "—";
        return string.Join(", ", list.Select(l => $"{l.Label} ({l.Quantity})"));
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ZeroToGhostBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Normal = (SolidColorBrush)new BrushConverter().ConvertFromString("#FFE8E8E8")!;
    private static readonly SolidColorBrush Ghost = (SolidColorBrush)new BrushConverter().ConvertFromString("#55FFFFFF")!;

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is int n && n <= 0 ? Ghost : Normal;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ViewModeToVisibilityConverter : IValueConverter
{
    public string Target { get; set; } = "";

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not CelebrimborViewMode mode) return Visibility.Collapsed;
        var targetMode = string.Equals(Target, "Shopping", StringComparison.OrdinalIgnoreCase)
            ? CelebrimborViewMode.Shopping
            : CelebrimborViewMode.Picker;
        return mode == targetMode ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ViewModeToAccentBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Active = (SolidColorBrush)new BrushConverter().ConvertFromString("#FFD4A847")!;
    private static readonly SolidColorBrush Inactive = (SolidColorBrush)new BrushConverter().ConvertFromString("#55FFFFFF")!;

    public string Target { get; set; } = "";

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not CelebrimborViewMode mode) return Inactive;
        var targetMode = string.Equals(Target, "Shopping", StringComparison.OrdinalIgnoreCase)
            ? CelebrimborViewMode.Shopping
            : CelebrimborViewMode.Picker;
        return mode == targetMode ? Active : Inactive;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var positive = value switch
        {
            int n => n > 0,
            bool b => b,
            System.Collections.ICollection c => c.Count > 0,
            _ => false,
        };
        if (Invert) positive = !positive;
        return positive ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
