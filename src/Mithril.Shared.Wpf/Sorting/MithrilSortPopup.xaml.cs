using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Mithril.Shared.Wpf.Sorting;

/// <summary>
/// Toolbar popup that lets the user manage an ordered, multi-key sort over an
/// <see cref="ISortableViewModel"/>. The popup interior reads as a chip strip:
/// each chip is one available <see cref="SortKey{T}"/> projected through
/// <see cref="ChipState{T}"/>. Active chips show their direction glyph;
/// inactive chips read as "add" affordances. Clicking always routes through
/// <see cref="ISortableViewModel.ToggleChip"/>, which rewrites the query
/// box's <c>ORDER BY</c> clause.
/// </summary>
public partial class MithrilSortPopup : UserControl
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ISortableViewModel), typeof(MithrilSortPopup),
        new FrameworkPropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen), typeof(bool), typeof(MithrilSortPopup),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsOpenChanged));

    public static readonly DependencyProperty PlacementTargetProperty = DependencyProperty.Register(
        nameof(PlacementTarget), typeof(UIElement), typeof(MithrilSortPopup));

    public ISortableViewModel? Source
    {
        get => (ISortableViewModel?)GetValue(SourceProperty);
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

    public MithrilSortPopup()
    {
        InitializeComponent();
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var p = (MithrilSortPopup)d;
        // Bind ItemsSource via direct interface call rather than XAML, because the
        // non-generic facet (ChipsUntyped) is a default-interface member. WPF's
        // reflection-based binding pipeline cannot see DIM-only properties on the
        // runtime class — interface vtable dispatch here works regardless.
        if (e.NewValue is ISortableViewModel src)
            p.TagsList.ItemsSource = src.ChipsUntyped;
    }

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var p = (MithrilSortPopup)d;
        if ((bool)e.NewValue)
        {
            // Re-bind on every open — Chips is a derived view that recomputes on
            // every ORDER BY edit, so a fresh IEnumerable instance may have replaced
            // the one we captured at OnSourceChanged.
            if (p.Source is { } src) p.TagsList.ItemsSource = src.ChipsUntyped;
            // Focus the first chip when the popup pops so keyboard users land
            // somewhere actionable rather than on the popup root.
            p.Dispatcher.BeginInvoke(new Action(p.FocusFirstChip), DispatcherPriority.Input);
        }
    }

    private void FocusFirstChip()
    {
        if (TagsList.Items.Count == 0) return;
        if (TagsList.ItemContainerGenerator.ContainerFromIndex(0) is not FrameworkElement container) return;
        var button = FindChild<Button>(container);
        button?.Focus();
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

    private void OnChipClick(object sender, RoutedEventArgs e)
    {
        if (Source is not { } src) return;
        if (((FrameworkElement)sender).DataContext is not { } dc) return;
        // ChipsUntyped is IEnumerable<ChipState<T>> for some T — but the popup is
        // non-generic. Read the Key.Id via dynamic to bridge to ToggleChip(string).
        var id = (string)((dynamic)dc).Key.Id;
        src.ToggleChip(id);
        // Re-pull ItemsSource so the new derived projection drives the next render.
        TagsList.ItemsSource = src.ChipsUntyped;
        e.Handled = true;
    }

    private void OnPopupKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        IsOpen = false;
        e.Handled = true;
    }
}
