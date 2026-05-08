using System.Windows;
using System.Windows.Controls;
using Elrond.ViewModels;

namespace Elrond.Views;

public partial class SkillAdvisorView : UserControl
{
    public SkillAdvisorView()
    {
        InitializeComponent();
    }

    private void OnOpenSortPopup(object sender, RoutedEventArgs e)
    {
        SortPopup.IsOpen = !SortPopup.IsOpen;
    }

    private void OnOpenFilterPopup(object sender, RoutedEventArgs e)
    {
        FilterPopup.IsOpen = !FilterPopup.IsOpen;
    }

    /// <summary>
    /// TreeView.SelectedItem is read-only so we can't two-way bind it to the
    /// view-model. Forward leaf selections into <see cref="SkillAdvisorViewModel.SelectedSkill"/>
    /// and dismiss the popup; header-only picks are ignored (the user is still
    /// expected to expand and pick a leaf).
    /// </summary>
    private void OnSkillTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is not SkillAdvisorViewModel vm) return;
        if (e.NewValue is not SkillNode node) return;
        if (node.IsHeaderOnly) return;

        vm.SelectedSkill = node.Key;
        SkillPickerPopup.IsOpen = false;
    }
}
