using CommunityToolkit.Mvvm.ComponentModel;

namespace Mithril.Shared.Wpf;

/// <summary>
/// A single tab descriptor for binding to <c>TabControl.ItemsSource</c>.
/// <para>
/// Use this in preference to constructing TabItems in code-behind:
/// WPF's <c>ItemContainerStyle</c> is reliably applied to generated containers
/// (the TabItems WPF creates from each item in <c>ItemsSource</c>) but is
/// inconsistently applied to TabItems added directly to <c>TabControl.Items</c>.
/// </para>
/// <para>
/// In the parent View, declare <c>DataTemplate</c>s keyed by each child VM type so
/// the TabControl's <c>ContentTemplate</c> resolves <see cref="Content"/> to the
/// right view. Use the TabControl's <c>ItemTemplate</c> to render <see cref="Header"/>.
/// To attach a notification badge, set
/// <c>c:TabBadge.Count="{Binding BadgeCount}"</c> on the <c>ItemContainerStyle</c>;
/// the value is observable so a parent VM can update it as child-VM state changes.
/// </para>
/// </summary>
public sealed partial class ModuleTab : ObservableObject
{
    public string Header { get; }
    public object Content { get; }

    [ObservableProperty]
    private int badgeCount;

    public ModuleTab(string header, object content, int badgeCount = 0)
    {
        Header = header;
        Content = content;
        this.badgeCount = badgeCount;
    }
}
