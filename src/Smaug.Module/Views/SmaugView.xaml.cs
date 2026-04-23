using System.Windows.Controls;

namespace Smaug.Views;

public partial class SmaugView : UserControl
{
    public SmaugView()
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
