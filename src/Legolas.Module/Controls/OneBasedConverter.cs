using System.Globalization;
using System.Windows.Data;

namespace Legolas.Controls;

/// <summary>
/// Display-only converter that adds 1 to int / int? values, so 0-based route
/// indices render as 1-based labels. Returns "" for null. Not back-convertible.
/// </summary>
public sealed class OneBasedConverter : IValueConverter
{
    public static readonly OneBasedConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            null => string.Empty,
            int i => (i + 1).ToString(culture),
            _ => value,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
