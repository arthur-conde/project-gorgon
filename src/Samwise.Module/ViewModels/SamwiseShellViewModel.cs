using Mithril.Shared.Wpf;

namespace Samwise.ViewModels;

/// <summary>
/// Composes Samwise's tabs as VM data so the View can bind
/// <c>TabControl.ItemsSource</c> rather than constructing TabItems in code.
/// Tab views are picked by <c>DataTemplate</c>s keyed on each child VM's type.
/// </summary>
public sealed class SamwiseShellViewModel
{
    public IReadOnlyList<ModuleTab> Tabs { get; }

    public SamwiseShellViewModel(GardenViewModel garden, GrowthCalibrationViewModel calibration)
    {
        Tabs = new[]
        {
            new ModuleTab("Garden", garden),
            new ModuleTab("Growth Calibration", calibration),
        };
    }
}
