using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace Mithril.Shared.Wpf;

/// <summary>
/// Segmented two-or-more-way toggle for selecting a view mode (e.g. Rows / Cards).
/// Internally a <see cref="ListBox"/> with one item per <see cref="ViewModeOption"/>;
/// consumers bind <see cref="SelectedMode"/> two-way and declare options as inline
/// children. Default style ships in <c>Mithril.Shared.Wpf/Resources.xaml</c>.
/// </summary>
[ContentProperty(nameof(Modes))]
public sealed class ViewModeToggle : Control
{
    public static readonly DependencyProperty SelectedModeProperty = DependencyProperty.Register(
        nameof(SelectedMode), typeof(string), typeof(ViewModeToggle),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty ModesProperty = DependencyProperty.Register(
        nameof(Modes), typeof(ObservableCollection<ViewModeOption>), typeof(ViewModeToggle));

    static ViewModeToggle()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ViewModeToggle),
            new FrameworkPropertyMetadata(typeof(ViewModeToggle)));
    }

    public ViewModeToggle()
    {
        SetValue(ModesProperty, new ObservableCollection<ViewModeOption>());
    }

    public string SelectedMode
    {
        get => (string)GetValue(SelectedModeProperty);
        set => SetValue(SelectedModeProperty, value);
    }

    public ObservableCollection<ViewModeOption> Modes =>
        (ObservableCollection<ViewModeOption>)GetValue(ModesProperty);
}
