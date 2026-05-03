using System.Windows;
using System.Windows.Controls;

namespace Mithril.Shared.Wpf;

/// <summary>
/// Attached properties that paint a notification badge on a <see cref="TabItem"/>
/// styled with <c>MithrilTabItemStyle</c>. The pill is hidden when
/// <see cref="GetCount"/> &lt;= 0.
/// </summary>
public static class TabBadge
{
    public static readonly DependencyProperty CountProperty = DependencyProperty.RegisterAttached(
        "Count", typeof(int), typeof(TabBadge),
        new FrameworkPropertyMetadata(0));

    public static int GetCount(DependencyObject obj) => (int)obj.GetValue(CountProperty);
    public static void SetCount(DependencyObject obj, int value) => obj.SetValue(CountProperty, value);
}
