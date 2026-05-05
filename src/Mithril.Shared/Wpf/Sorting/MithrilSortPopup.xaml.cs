using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

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
        p.TagsList.ItemsSource = (e.NewValue as ISortableViewModel)?.ActiveSortKeysUntyped;
        p.RefreshEmptyHint();
    }

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var p = (MithrilSortPopup)d;
        if ((bool)e.NewValue) p.RefreshEmptyHint();
        else p.AddPopup.IsOpen = false;
    }

    private void RefreshEmptyHint()
    {
        if (!IsLoaded) return;
        var hasItems = Source?.ActiveSortKeysUntyped.Count > 0;
        EmptyHint.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnTagBodyClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is { } dc)
            ((dynamic)dc).FlipDirection();
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (Source is not { } src) return;
        if (((FrameworkElement)sender).DataContext is not { } dc) return;
        var index = src.ActiveSortKeysUntyped.IndexOf(dc);
        if (index >= 0) src.RemoveSortKeyAt(index);
        RefreshEmptyHint();
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
