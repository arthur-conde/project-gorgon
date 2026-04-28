using System.Windows.Controls;

namespace Palantir.Views;

public partial class PalantirView : UserControl
{
    public PalantirView()
    {
        InitializeComponent();
    }

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
