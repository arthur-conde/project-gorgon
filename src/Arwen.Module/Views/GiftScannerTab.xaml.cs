using System.Windows.Controls;

namespace Arwen.Views;

public partial class GiftScannerTab : UserControl
{
    public GiftScannerTab()
    {
        Resources.Add("ProgressToWidthConverter", new ProgressToWidthConverter());
        Resources.Add("TierToDisplayNameConverter", new TierToDisplayNameConverter());
        InitializeComponent();
    }
}
