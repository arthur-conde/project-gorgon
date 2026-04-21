using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Arwen.Domain;

namespace Arwen.Views;

public partial class NpcDashboardTab : UserControl
{
    public NpcDashboardTab()
    {
        Resources.Add("ProgressToWidthConverter", new ProgressToWidthConverter());
        Resources.Add("TierToDisplayNameConverter", new TierToDisplayNameConverter());
        InitializeComponent();
    }
}

internal sealed class ProgressToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double progress && parameter is string maxStr && double.TryParse(maxStr, out var max))
        {
            if (double.IsNaN(progress)) return 0.0;
            return Math.Clamp(progress, 0, 1) * max;
        }
        return 0.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

internal sealed class TierToDisplayNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is FavorTier tier ? FavorTiers.DisplayName(tier) : value?.ToString() ?? "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
