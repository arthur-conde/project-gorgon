using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Bilbo.Views;

public sealed class NullDashConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() is { Length: > 0 } s ? s : "\u2014";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class DecimalFormatConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is decimal d ? d.ToString("N0", culture) : "\u2014";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class RarityToBrushConverter : IValueConverter
{
    private static readonly Brush Uncommon = new SolidColorBrush(Color.FromRgb(0x1E, 0xFF, 0x00)); // green
    private static readonly Brush Rare = new SolidColorBrush(Color.FromRgb(0x00, 0x70, 0xDD));     // blue
    private static readonly Brush Exceptional = new SolidColorBrush(Color.FromRgb(0xA3, 0x35, 0xEE)); // purple
    private static readonly Brush Epic = new SolidColorBrush(Color.FromRgb(0xFF, 0x80, 0x00));      // orange
    private static readonly Brush Legendary = new SolidColorBrush(Color.FromRgb(0xFF, 0xD1, 0x00)); // gold
    private static readonly Brush Default = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));   // default text

    static RarityToBrushConverter()
    {
        Uncommon.Freeze(); Rare.Freeze(); Exceptional.Freeze();
        Epic.Freeze(); Legendary.Freeze(); Default.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return (value as string) switch
        {
            "Uncommon" => Uncommon,
            "Rare" => Rare,
            "Exceptional" => Exceptional,
            "Epic" => Epic,
            "Legendary" => Legendary,
            _ => Default,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
