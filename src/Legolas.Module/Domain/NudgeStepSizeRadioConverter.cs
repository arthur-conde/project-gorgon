using System.Globalization;
using System.Windows.Data;

namespace Legolas.Domain;

/// <summary>
/// Two-way binding glue for "RadioButton IsChecked ↔ enum-valued property":
/// returns true when the bound enum equals the converter parameter (so the
/// matching RadioButton is checked); on the way back, only commits the
/// parameter value when the radio is being checked (ignoring the redundant
/// uncheck-side notification — RadioButton group toggling fires both).
/// </summary>
public sealed class NudgeStepSizeRadioConverter : IValueConverter
{
    public static readonly NudgeStepSizeRadioConverter Instance = new();

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not NudgeStepSize current) return false;
        if (parameter is not NudgeStepSize target) return false;
        return current == target;
    }

    public object? ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter is NudgeStepSize target) return target;
        return Binding.DoNothing;
    }
}
