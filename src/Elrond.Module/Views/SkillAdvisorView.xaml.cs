using System.Windows;
using System.Windows.Controls;

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
}
