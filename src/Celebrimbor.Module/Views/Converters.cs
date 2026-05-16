using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Celebrimbor.Domain;
using Celebrimbor.ViewModels;
using Mithril.Shared.Storage;

namespace Celebrimbor.Views;

public sealed class BoolToCraftReadyBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Ready = (SolidColorBrush)new BrushConverter().ConvertFromString("#FF7FB069")!;
    private static readonly SolidColorBrush Needs = (SolidColorBrush)new BrushConverter().ConvertFromString("#FFD4A847")!;

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool ready && ready ? Ready : Needs;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class LocationsToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IReadOnlyList<IngredientLocation> list || list.Count == 0) return "—";
        return string.Join(", ", list.Select(l => $"{l.Label} ({l.Quantity})"));
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ZeroToGhostBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Normal = (SolidColorBrush)new BrushConverter().ConvertFromString("#FFE8E8E8")!;
    private static readonly SolidColorBrush Ghost = (SolidColorBrush)new BrushConverter().ConvertFromString("#55FFFFFF")!;

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is int n && n <= 0 ? Ghost : Normal;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ViewModeToVisibilityConverter : IValueConverter
{
    public string Target { get; set; } = "";

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not CelebrimborViewMode mode) return Visibility.Collapsed;
        return Enum.TryParse<CelebrimborViewMode>(Target, ignoreCase: true, out var targetMode)
            && mode == targetMode
            ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ViewModeToAccentBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Active = (SolidColorBrush)new BrushConverter().ConvertFromString("#FFD4A847")!;
    private static readonly SolidColorBrush Inactive = (SolidColorBrush)new BrushConverter().ConvertFromString("#55FFFFFF")!;

    public string Target { get; set; } = "";

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not CelebrimborViewMode mode) return Inactive;
        return Enum.TryParse<CelebrimborViewMode>(Target, ignoreCase: true, out var targetMode)
            && mode == targetMode
            ? Active : Inactive;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Header pivot accent: gold when the area is active, dim otherwise.</summary>
public sealed class BoolToAccentBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Active = (SolidColorBrush)new BrushConverter().ConvertFromString("#FFD4A847")!;
    private static readonly SolidColorBrush Inactive = (SolidColorBrush)new BrushConverter().ConvertFromString("#88FFFFFF")!;

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? Active : Inactive;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Manager glyph / pill colour by plan status (#228 PR-B/B1 frame 02).</summary>
public sealed class PlanStatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Run = (SolidColorBrush)new BrushConverter().ConvertFromString("#FF4FB3A5")!;   // teal — progress
    private static readonly SolidColorBrush Warn = (SolidColorBrush)new BrushConverter().ConvertFromString("#FFC9A24A")!;  // stale
    private static readonly SolidColorBrush Ok = (SolidColorBrush)new BrushConverter().ConvertFromString("#FF7BA876")!;    // done
    private static readonly SolidColorBrush Idle = (SolidColorBrush)new BrushConverter().ConvertFromString("#FF7A7A7A")!;

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            SavedPlanStatus.InProgress => Run,
            SavedPlanStatus.Stale => Warn,
            SavedPlanStatus.Done => Ok,
            _ => Idle,
        };

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Walker rail marker / text colour by phase position (#228 PR-B/B1 frame 03).</summary>
public sealed class PhaseRailStateToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Done = (SolidColorBrush)new BrushConverter().ConvertFromString("#FF7BA876")!;
    private static readonly SolidColorBrush Current = (SolidColorBrush)new BrushConverter().ConvertFromString("#FF4FB3A5")!;
    private static readonly SolidColorBrush Upcoming = (SolidColorBrush)new BrushConverter().ConvertFromString("#FF5A5A5A")!;

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            PhaseRailState.Done => Done,
            PhaseRailState.Current => Current,
            _ => Upcoming,
        };

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var positive = value switch
        {
            int n => n > 0,
            bool b => b,
            System.Collections.ICollection c => c.Count > 0,
            _ => false,
        };
        if (Invert) positive = !positive;
        return positive ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
