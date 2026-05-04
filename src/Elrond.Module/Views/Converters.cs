using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Elrond.Views;

public sealed class BoolToCheckConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "\u2713" : "\u2014";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class NullableIntConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int n ? n.ToString("N0", culture) : "\u2014";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Resolves the active-sort indicator for a sort chip. Inputs (in order):
/// current SortKey, current SortDescending, this chip's Key. Returns
/// " ▼" (down arrow) when matching and descending, " ▲" (up
/// arrow) when matching and ascending, empty string otherwise.
/// </summary>
public sealed class SortIndicatorConverter : IMultiValueConverter
{
    public static SortIndicatorConverter Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is [string current, bool desc, string key] && current == key)
            return desc ? " ▼" : " ▲";
        return string.Empty;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Returns true when the current SortKey (first input) equals this chip's
/// Key (second input). Drives the chip's active styling via Tag-based
/// trigger.
/// </summary>
public sealed class IsActiveSortConverter : IMultiValueConverter
{
    public static IsActiveSortConverter Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        => values is [string current, string key] && current == key;

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Returns Visible when the bound value (string) equals the converter
/// parameter (string). Used to show/hide chrome based on view mode.
/// </summary>
public sealed class StringEqualsToVisConverter : IValueConverter
{
    public static StringEqualsToVisConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && parameter is string p && s == p ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
