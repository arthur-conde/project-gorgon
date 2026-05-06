using System.Windows;
using System.Windows.Controls;

namespace Pippin.Views;

public partial class GourmandView : UserControl
{
    public GourmandView()
    {
        InitializeComponent();
        // Virtualizing panels that start out collapsed don't run their initial measure with
        // a real viewport — first show paints blank until the user scrolls. Force a fresh
        // layout pass each time the cards ListBox becomes visible.
        FoodsCards.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                FoodsCards.InvalidateMeasure();
                FoodsCards.UpdateLayout();
            }
        };
    }
}
