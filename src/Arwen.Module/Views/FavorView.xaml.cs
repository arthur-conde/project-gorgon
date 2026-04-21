using System.Windows.Controls;

namespace Arwen.Views;

public partial class FavorView : UserControl
{
    public FavorView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Adds a tab with its DataContext already set.
    /// Called by ArwenModule during DI registration so each tab
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
