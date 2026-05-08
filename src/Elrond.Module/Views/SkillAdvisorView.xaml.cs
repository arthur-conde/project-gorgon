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
    /// Forward ListBox selection into the view-model's SelectedSkill (which is the
    /// id-shaped key, not the SkillNode), then dismiss the popup. Forwarding via
    /// code-behind avoids the ListBox-vs-string two-way-binding mismatch.
    /// </summary>
    private void OnSkillListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not SkillAdvisorViewModel vm) return;
        if (e.AddedItems.Count == 0) return;
        if (e.AddedItems[0] is not SkillNode node) return;

        vm.SelectedSkill = node.Key;
        SkillPickerPopup.IsOpen = false;
    }
}
