using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Mithril.Shared.Wpf.Sorting;

/// <summary>
/// Toolbar popup that lets the user manage an ordered, multi-key sort over an
/// <see cref="ISortableViewModel"/>. The popup interior reads as a TextBox-like
/// surface holding sort-key chips: each chip is <c>[ ▼/▲ DisplayName ✕ ]</c>;
/// clicking the body flips the direction, ✕ removes. A "+ Add sort key"
/// button opens a themed sub-popup (not the system ContextMenu) listing the
/// keys not currently active.
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
        // non-generic facets (AvailableSortKeysUntyped / ActiveSortKeysUntyped) are
        // default-interface members. WPF's reflection-based binding pipeline cannot
        // see DIM-only properties on the runtime class — interface vtable dispatch
        // here works regardless.
        if (e.NewValue is ISortableViewModel src)
            p.TagsList.ItemsSource = src.ActiveSortKeysUntyped;
        p.RefreshEmptyHint();
    }

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var p = (MithrilSortPopup)d;
        if ((bool)e.NewValue)
        {
            // Re-bind on every open in case the consumer assigned Source after the
            // initial OnSourceChanged callback fired (or DIM resolution missed it).
            if (p.Source is { } src) p.TagsList.ItemsSource = src.ActiveSortKeysUntyped;
            p.RefreshEmptyHint();
            // Focus the first chip when the popup pops, so keyboard users land
            // somewhere actionable rather than on the popup root.
            p.Dispatcher.BeginInvoke(new Action(p.FocusFirstChip), DispatcherPriority.Input);
        }
        else
        {
            p.AddPopup.IsOpen = false;
        }
    }

    private void FocusFirstChip()
    {
        if (TagsList.Items.Count == 0) { AddBtn.Focus(); return; }
        if (TagsList.ItemContainerGenerator.ContainerFromIndex(0) is not FrameworkElement container) return;
        // Walk to the inner Button (the chip body) so Space/Enter flips immediately.
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

    private void RefreshEmptyHint()
    {
        if (!IsLoaded) return;
        var hasItems = Source?.ActiveSortKeysUntyped.Count > 0;
        EmptyPlaceholder.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnTagBodyClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is { } dc)
            ((dynamic)dc).FlipDirection();
        // Stop the click bubbling — Popup.StaysOpen=False can interpret a bubbling
        // Click whose ancestor chain reaches outside the popup as "click outside".
        e.Handled = true;
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (Source is not { } src) return;
        if (((FrameworkElement)sender).DataContext is not { } dc) return;
        var index = src.ActiveSortKeysUntyped.IndexOf(dc);
        if (index >= 0) src.RemoveSortKeyAt(index);
        RefreshEmptyHint();
        e.Handled = true;
    }

    private void OnTagKeyDown(object sender, KeyEventArgs e)
    {
        if (Source is not { } src) return;
        if (((FrameworkElement)sender).DataContext is not { } dc) return;
        var index = src.ActiveSortKeysUntyped.IndexOf(dc);
        if (index < 0) return;

        switch (e.Key)
        {
            case Key.Up:
                if (index > 0)
                {
                    src.MoveSortKey(index, index - 1);
                    Dispatcher.BeginInvoke(() => FocusChipAt(index - 1), DispatcherPriority.Input);
                }
                e.Handled = true;
                break;
            case Key.Down:
                if (index < src.ActiveSortKeysUntyped.Count - 1)
                {
                    src.MoveSortKey(index, index + 1);
                    Dispatcher.BeginInvoke(() => FocusChipAt(index + 1), DispatcherPriority.Input);
                }
                e.Handled = true;
                break;
            case Key.Delete:
                src.RemoveSortKeyAt(index);
                RefreshEmptyHint();
                Dispatcher.BeginInvoke(FocusFirstChip, DispatcherPriority.Input);
                e.Handled = true;
                break;
        }
    }

    private void FocusChipAt(int index)
    {
        if ((uint)index >= (uint)TagsList.Items.Count) return;
        if (TagsList.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container) return;
        FindChild<Button>(container)?.Focus();
    }

    private void OnPopupKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (AddPopup.IsOpen) AddPopup.IsOpen = false;
        else IsOpen = false;
        e.Handled = true;
    }

    private void OnAddSortKeyClick(object sender, RoutedEventArgs e)
    {
        if (Source is not { } src) return;

        var activeIds = new HashSet<string>();
        foreach (var active in src.ActiveSortKeysUntyped)
        {
            if (active is null) continue;
            activeIds.Add((string)((dynamic)active).Id);
        }

        var unused = new List<object>();
        foreach (var key in src.AvailableSortKeysUntyped)
        {
            if (key is null) continue;
            var id = (string)((dynamic)key).Id;
            if (!activeIds.Contains(id)) unused.Add(key);
        }

        UnusedKeysList.ItemsSource = unused;
        UnusedKeysList.SelectedIndex = -1;
        AddPopup.IsOpen = unused.Count > 0;
    }

    private void OnUnusedKeysListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Source is not { } src) return;
        if (UnusedKeysList.SelectedItem is not { } selected) return;

        var id = (string)((dynamic)selected).Id;
        src.AddSortKeyById(id);

        AddPopup.IsOpen = false;
        UnusedKeysList.SelectedIndex = -1;
        RefreshEmptyHint();
    }
}
