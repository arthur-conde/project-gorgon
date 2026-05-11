using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Mithril.Shared.Wpf.Filtering;

/// <summary>
/// Toolbar popup that toggles the active set of filter predicates over an
/// <see cref="IFilterableViewModel"/>. Each predicate renders as a labeled
/// checkbox bound two-way to <c>FilterPredicate&lt;T&gt;.IsActive</c>;
/// authors of inverted filters (e.g. "Show unknown") use the predicate's
/// <c>Inverted</c> flag so the on-state means "reveal more rows" while the
/// underlying predicate stays positively expressed.
/// </summary>
public partial class MithrilFilterPopup : UserControl
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(IFilterableViewModel), typeof(MithrilFilterPopup),
        new FrameworkPropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen), typeof(bool), typeof(MithrilFilterPopup),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsOpenChanged));

    public static readonly DependencyProperty PlacementTargetProperty = DependencyProperty.Register(
        nameof(PlacementTarget), typeof(UIElement), typeof(MithrilFilterPopup));

    public IFilterableViewModel? Source
    {
        get => (IFilterableViewModel?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public UIElement? PlacementTarget
    {
        get => (UIElement?)GetValue(PlacementTargetProperty);
        set => SetValue(PlacementTargetProperty, value);
    }

    public MithrilFilterPopup()
    {
        InitializeComponent();
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var p = (MithrilFilterPopup)d;
        // Bind via interface vtable rather than XAML: AvailableFiltersUntyped is a
        // default-interface member, which WPF's reflection-based binding pipeline
        // can't see on the runtime class.
        if (e.NewValue is IFilterableViewModel src)
            p.FiltersList.ItemsSource = src.AvailableFiltersUntyped;
        p.RefreshEmptyHint();
    }

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var p = (MithrilFilterPopup)d;
        if ((bool)e.NewValue)
        {
            if (p.Source is { } src) p.FiltersList.ItemsSource = src.AvailableFiltersUntyped;
            p.RefreshEmptyHint();
            p.Dispatcher.BeginInvoke(new Action(p.FocusFirstCheckbox), DispatcherPriority.Input);
        }
    }

    private void FocusFirstCheckbox()
    {
        if (FiltersList.Items.Count == 0) return;
        if (FiltersList.ItemContainerGenerator.ContainerFromIndex(0) is not FrameworkElement container) return;
        var checkbox = FindChild<CheckBox>(container);
        checkbox?.Focus();
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var found = FindChild<T>(child);
            if (found is not null) return found;
        }
        return null;
    }

    private void OnPopupKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        IsOpen = false;
        e.Handled = true;
    }

    private void RefreshEmptyHint()
    {
        if (!IsLoaded) return;
        var hasItems = false;
        if (Source is { } src)
            foreach (var _ in src.AvailableFiltersUntyped) { hasItems = true; break; }
        EmptyHint.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnFilterToggleClick(object sender, RoutedEventArgs e)
    {
        // Stop the click bubbling — Popup.StaysOpen=False can dismiss the popup
        // when a routed Click reaches an ancestor outside the popup's tree.
        e.Handled = true;
    }
}
