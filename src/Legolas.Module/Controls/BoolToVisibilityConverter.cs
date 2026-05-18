using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Legolas.Controls;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public static readonly BoolToVisibilityConverter Instance = new(false);

    /// <summary>Visible when the bound bool is <c>false</c> — used to collapse
    /// the turn-order fallback guidance once the existing-pins route is
    /// usable (#468).</summary>
    public static readonly BoolToVisibilityConverter Inverse = new(true);

    private readonly bool _invert;

    private BoolToVisibilityConverter(bool invert) => _invert = invert;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is true) != _invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
