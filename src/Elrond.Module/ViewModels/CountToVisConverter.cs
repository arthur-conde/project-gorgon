using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Elrond.ViewModels;

/// <summary>Converts a non-zero integer count to Visible, zero to Collapsed.</summary>
public sealed class CountToVisConverter : IValueConverter
{
    public static readonly CountToVisConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
