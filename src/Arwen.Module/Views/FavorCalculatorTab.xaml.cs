using System.Windows.Controls;

namespace Arwen.Views;

public partial class FavorCalculatorTab : UserControl
{
    public FavorCalculatorTab()
    {
        Resources.Add("TierToDisplayNameConverter", new TierToDisplayNameConverter());
        InitializeComponent();
    }
}
