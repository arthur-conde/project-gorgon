using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using Mithril.Shared.Reference;

namespace Palantir.Views;

/// <summary>
/// Renders the display name for an inventory row: prefers the reference-data
/// <see cref="Mithril.Reference.Models.Items.Item.Name"/>, falls back to the
/// raw <c>InternalName</c> when the bundled/CDN catalogue is silent on the
/// key. Resolves at bind time against the <see cref="IReferenceDataService"/>
/// bound from the view-model — see issue #726 / PR for why the migrated VM
/// projects display fields via converters instead of mirroring the view's
/// collection into a parallel UI row type.
/// </summary>
public sealed class InventoryDisplayNameConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return string.Empty;
        var internalName = values[0] as string ?? string.Empty;
        var refData = values[1] as IReferenceDataService;
        if (refData is not null
            && !string.IsNullOrEmpty(internalName)
            && refData.ItemsByInternalName.TryGetValue(internalName, out var item)
            && !string.IsNullOrEmpty(item.Name))
        {
            return item.Name!;
        }
        return internalName;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Renders the icon id for an inventory row by resolving <c>InternalName</c>
/// against reference data — 0 when the catalogue is silent (caller's
/// <c>IconImage</c> hides itself for id 0).
/// </summary>
public sealed class InventoryIconIdConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return 0;
        var internalName = values[0] as string ?? string.Empty;
        var refData = values[1] as IReferenceDataService;
        if (refData is not null
            && !string.IsNullOrEmpty(internalName)
            && refData.ItemsByInternalName.TryGetValue(internalName, out var item))
        {
            return item.IconId;
        }
        return 0;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Inline converter: <c>true</c> → "Deleted", <c>false</c> → "Live".
/// Used as a markup extension so it can be referenced directly in a binding
/// without a resource dictionary entry.
/// </summary>
public sealed class RemovedStateConverter : MarkupExtension, IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Deleted" : "Live";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    public override object ProvideValue(IServiceProvider serviceProvider) => this;
}
