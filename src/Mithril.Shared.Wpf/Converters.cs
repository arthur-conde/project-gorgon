using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MahApps.Metro.IconPacks;

namespace Mithril.Shared.Wpf;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class StringToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s ? (Color)ColorConverter.ConvertFromString(s)! : Colors.Gray;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class PositiveIntToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
}

/// <summary>
/// Splits a PascalCase / camelCase internal name into space-separated words for display.
/// e.g. "MainHand" → "Main Hand", "OffHand" → "Off Hand", "Head" → "Head". Heuristic
/// (no strings_all.json lookup) — works for known equip-slot names which are all
/// PascalCase ASCII identifiers.
/// </summary>
public sealed class CamelCaseSplitConverter : IValueConverter
{
    private static readonly Regex SplitPattern = new("(?<=[a-z0-9])(?=[A-Z])", RegexOptions.Compiled);

    /// <summary>
    /// Splits a PascalCase / camelCase token into space-separated words.
    /// Exposed for non-XAML callers (VMs, services) that want the same heuristic
    /// without going through the converter pipeline.
    /// </summary>
    public static string Split(string value) =>
        string.IsNullOrEmpty(value) ? value : SplitPattern.Replace(value, " ");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s ? Split(s) : (value ?? "");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Maps a <see cref="LinkGlyph"/> to its concrete <see cref="PackIconLucideKind"/> for
/// the <see cref="Link"/> template's lead glyph. Delegates to
/// <see cref="Link.ToLucideKind"/> so the mapping has one home (and is unit-tested
/// there). <see cref="LinkGlyph.None"/> yields <see cref="PackIconLucideKind.None"/> —
/// the template independently collapses the glyph element via
/// <see cref="LinkGlyphToVisibilityConverter"/>.
/// </summary>
public sealed class LinkGlyphToLucideKindConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is LinkGlyph g ? Link.ToLucideKind(g) : PackIconLucideKind.None;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Collapses the <see cref="Link"/> lead-glyph element when the glyph is
/// <see cref="LinkGlyph.None"/> (the name stands alone), Visible otherwise.
/// </summary>
public sealed class LinkGlyphToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is LinkGlyph g && g != LinkGlyph.None ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Two-way converter between an enum value and <c>bool</c> for radio-button bindings.
/// Usage: <c>IsChecked="{Binding Foo, Converter={StaticResource EnumToBoolConverter}, ConverterParameter=SomeEnumValue}"</c>.
/// </summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter is string s && !string.IsNullOrEmpty(s))
            return Enum.Parse(targetType, s);
        return Binding.DoNothing;
    }
}
