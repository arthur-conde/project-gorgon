using System.Globalization;
using System.Windows.Data;
using Mithril.Shared.Wpf;

namespace Silmarillion.ViewModels;

/// <summary>
/// Projects a chip-eligible <see cref="QuestRequirementDisplay"/> or <see cref="QuestRewardDisplay"/>
/// (one with <c>ChipName</c> + <c>Reference</c> populated) into an <see cref="EntityChipVm"/> suitable
/// for the shared <c>EntityChip</c> control. Lets the row DataTemplate render labelled rows
/// ("Completed: Wolf: Hunt Deer" / "Teaches recipe: Bake Bread") with a real navigable chip for the
/// entity portion, without forcing the projector to carry a second VM shape per row.
/// </summary>
public sealed class RequirementChipVmConverter : IValueConverter
{
    public object? Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            QuestRequirementDisplay req when !string.IsNullOrEmpty(req.ChipName) && req.Reference is not null =>
                new EntityChipVm(req.ChipName!, IconId: 0, Reference: req.Reference, IsNavigable: req.IsNavigable),
            QuestRewardDisplay rw when !string.IsNullOrEmpty(rw.ChipName) && rw.Reference is not null =>
                new EntityChipVm(rw.ChipName!, IconId: 0, Reference: rw.Reference, IsNavigable: rw.IsNavigable),
            _ => null,
        };

    public object ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture) =>
        throw new System.NotSupportedException();
}
