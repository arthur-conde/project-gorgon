using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Samwise.State;

namespace Samwise.Views;

public sealed class StageToHarvestVisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is PlotStage s && s == PlotStage.Ripe ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
