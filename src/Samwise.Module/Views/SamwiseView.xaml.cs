using System.Windows.Controls;

namespace Samwise.Views;

public partial class SamwiseView : UserControl
{
    public SamwiseView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Adds a tab with its DataContext already set.
    /// Called by SamwiseModule during DI registration so each tab
    /// has its ViewModel before any bindings are evaluated.
    /// </summary>
    public void AddTab(string header, UserControl content)
    {
        Tabs.Items.Add(new TabItem
        {
            Header = header,
            Content = content,
            Margin = new System.Windows.Thickness(0, 8, 0, 0),
        });
    }
}
