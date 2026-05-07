using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Legolas.Domain;
using Legolas.ViewModels;

namespace Legolas.Views;

public partial class NudgePadView : UserControl
{
    public NudgePadView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Step-toggle click handler. Sets <see cref="NudgePadViewModel.SelectedStep"/>
    /// to this toggle's tagged tier and re-asserts <c>IsChecked = true</c>.
    /// Re-asserting matters for the "click the already-active toggle" case:
    /// ToggleButton's default click flips IsChecked, but we want it to stay
    /// pinned to the bound enum (no "all toggles off" state). Mode=OneWay on
    /// the IsChecked binding keeps the bound enum the single source of truth.
    /// </summary>
    private void StepToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton btn) return;
        if (btn.Tag is not NudgeStepSize tier) return;
        if (DataContext is not NudgePadViewModel vm) return;

        vm.SelectedStep = tier;
        btn.IsChecked = true;
    }
}
