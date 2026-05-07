using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Legolas.Domain;

namespace Legolas.Views.Converters;

/// <summary>
/// Visibility for the active-pin halo overlay. Visible iff
/// <c>(IsSelected, IsListening, ActivePinTreatment)</c> all line up: the pin
/// is the SelectedSurvey, the FSM is in Listening (Gathering uses the
/// marching-ants line instead), and the user picked the Halo treatment.
/// </summary>
public sealed class HaloVisibilityConverter : IMultiValueConverter
{
    public static readonly HaloVisibilityConverter Instance = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 3) return Visibility.Collapsed;
        var isSelected = values[0] is bool b1 && b1;
        var isListening = values[1] is bool b2 && b2;
        var treatment = values[2] is ActivePinTreatment t ? t : ActivePinTreatment.Halo;
        return (isSelected && isListening && treatment == ActivePinTreatment.Halo)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
