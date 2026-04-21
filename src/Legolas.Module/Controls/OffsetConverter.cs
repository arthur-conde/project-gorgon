using System.Globalization;
using System.Windows.Data;

namespace Legolas.Controls;

public sealed class OffsetConverter : IValueConverter
{
    public static readonly OffsetConverter MinusSeven = new(-7);
    public static readonly OffsetConverter MinusNine = new(-9);

    public OffsetConverter(double offset) => Offset = offset;

    public double Offset { get; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? d + Offset : 0d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
