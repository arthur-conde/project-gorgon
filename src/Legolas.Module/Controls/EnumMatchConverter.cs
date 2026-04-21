using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Legolas.Controls;

public sealed class EnumMatchConverter : IValueConverter
{
    public static readonly EnumMatchConverter ToBool = new(forVisibility: false);
    public static readonly EnumMatchConverter ToVisibility = new(forVisibility: true);

    private readonly bool _forVisibility;

    private EnumMatchConverter(bool forVisibility) => _forVisibility = forVisibility;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var match = value is not null && parameter is not null
                 && value.ToString() == parameter.ToString();
        return _forVisibility
            ? (match ? Visibility.Visible : Visibility.Collapsed)
            : match;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (_forVisibility || value is not true || parameter is null)
            return Binding.DoNothing;
        return Enum.Parse(targetType, parameter.ToString()!);
    }
}
