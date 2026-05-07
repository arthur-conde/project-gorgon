using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Legolas.Domain;

namespace Legolas.Views.Converters;

/// <summary>
/// <see cref="ActivePinTreatment.Halo"/> → <see cref="Visibility.Visible"/>;
/// every other treatment → <see cref="Visibility.Collapsed"/>. Used by the
/// settings preview to show the halo Path only when the user has Halo
/// selected, distinct from <see cref="HaloVisibilityConverter"/> which also
/// checks IsSelected + IsListening (those don't apply in the preview).
/// </summary>
public sealed class TreatmentToHaloVisibilityConverter : IValueConverter
{
    public static readonly TreatmentToHaloVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is ActivePinTreatment.Halo ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
