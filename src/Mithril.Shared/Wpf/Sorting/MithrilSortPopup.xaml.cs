using System.Windows;
using System.Windows.Controls;

namespace Mithril.Shared.Wpf.Sorting;

/// <summary>
/// Toolbar popup that lets the user manage an ordered, multi-key sort over an
/// <see cref="ISortableViewModel"/>. Each active key renders as a "tag" with a
/// flip-direction body, ↑/↓ reorder buttons, and an × remove button. The footer
/// Add button shows a context menu of currently-unused keys.
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

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MithrilSortPopup)d).RefreshEmptyHint();

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var p = (MithrilSortPopup)d;
        if ((bool)e.NewValue) p.RefreshEmptyHint();
    }

    private void RefreshEmptyHint()
    {
        if (!IsLoaded) return;
        var hasItems = Source?.ActiveSortKeysUntyped.Count > 0;
        EmptyHint.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        TagsList.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTagBodyClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is { } dc)
            ((dynamic)dc).FlipDirection();
    }

    private void OnMoveUpClick(object sender, RoutedEventArgs e) => Move(sender, -1);
    private void OnMoveDownClick(object sender, RoutedEventArgs e) => Move(sender, +1);

    private void Move(object sender, int delta)
    {
        if (Source is not { } src) return;
        if (((FrameworkElement)sender).DataContext is not { } dc) return;
        var from = src.ActiveSortKeysUntyped.IndexOf(dc);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= src.ActiveSortKeysUntyped.Count) return;
        src.MoveSortKey(from, to);
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
        AddMenu.Items.Clear();

        var activeIds = new HashSet<string>();
        foreach (var active in src.ActiveSortKeysUntyped)
        {
            if (active is null) continue;
            activeIds.Add((string)((dynamic)active).Id);
        }

        var added = 0;
        foreach (var key in src.AvailableSortKeysUntyped)
        {
            if (key is null) continue;
            var id = (string)((dynamic)key).Id;
            if (activeIds.Contains(id)) continue;
            var displayName = (string)((dynamic)key).DisplayName;
            var item = new MenuItem { Header = displayName, Tag = id };
            item.Click += OnAddMenuItemClick;
            AddMenu.Items.Add(item);
            added++;
        }

        if (added == 0)
        {
            AddMenu.Items.Add(new MenuItem
            {
                Header = "All sort keys are already active",
                IsEnabled = false,
            });
        }

        AddMenu.PlacementTarget = AddBtn;
        AddMenu.IsOpen = true;
    }

    private void OnAddMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (Source is not { } src) return;
        if (((FrameworkElement)sender).Tag is string id)
            src.AddSortKeyById(id);
        RefreshEmptyHint();
    }
}
