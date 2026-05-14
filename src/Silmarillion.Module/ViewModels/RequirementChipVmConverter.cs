using System.Globalization;
using System.Windows.Data;
using Mithril.Shared.Wpf;

namespace Silmarillion.ViewModels;

/// <summary>
/// Projects a chip-eligible <see cref="QuestRequirementDisplay"/> (one with
/// <see cref="QuestRequirementDisplay.ChipName"/> populated) into an <see cref="EntityChipVm"/>
/// suitable for the shared <c>EntityChip</c> control. Lets the requirement DataTemplate render
/// labelled prerequisite rows ("Completed: Wolf: Hunt Deer") with a real navigable chip for the
/// entity portion, without forcing the projector to carry a second VM shape per row.
/// </summary>
public sealed class RequirementChipVmConverter : IValueConverter
{
    public object? Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not QuestRequirementDisplay req) return null;
        if (string.IsNullOrEmpty(req.ChipName) || req.Reference is null) return null;
        return new EntityChipVm(
            DisplayName: req.ChipName!,
            IconId: 0,
            Reference: req.Reference,
            IsNavigable: req.IsNavigable);
    }

    public object ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture) =>
        throw new System.NotSupportedException();
}
