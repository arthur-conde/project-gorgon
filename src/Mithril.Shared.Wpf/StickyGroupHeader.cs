using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mithril.Shared.Wpf;

/// <summary>Attached behavior that pins a <see cref="GroupItem"/>'s header via a <see cref="TranslateTransform"/> while its group is being scrolled through.</summary>
public static class StickyGroupHeader
{
    public static readonly DependencyProperty IsStickyProperty = DependencyProperty.RegisterAttached(
        "IsSticky", typeof(bool), typeof(StickyGroupHeader),
        new PropertyMetadata(false, OnIsStickyChanged));

    public static bool GetIsSticky(DependencyObject d) => (bool)d.GetValue(IsStickyProperty);
    public static void SetIsSticky(DependencyObject d, bool v) => d.SetValue(IsStickyProperty, v);

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(StickyState), typeof(StickyGroupHeader),
        new PropertyMetadata(null));

    private static void OnIsStickyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement header) return;
        if ((bool)e.NewValue)
        {
            var state = new StickyState(header);
            header.SetValue(StateProperty, state);
            header.Loaded += state.OnLoaded;
            header.Unloaded += state.OnUnloaded;
            if (header.IsLoaded) state.OnLoaded(header, null!);
        }
        else if (header.GetValue(StateProperty) is StickyState state)
        {
            header.Loaded -= state.OnLoaded;
            header.Unloaded -= state.OnUnloaded;
            state.Detach();
            header.ClearValue(StateProperty);
        }
    }

    private sealed class StickyState
    {
        private readonly FrameworkElement _header;
        private GroupItem? _group;
        private ScrollViewer? _scroller;
        private TranslateTransform? _transform;

        public StickyState(FrameworkElement header) => _header = header;

        public void OnLoaded(object sender, RoutedEventArgs e)
        {
            _group = FindAncestor<GroupItem>(_header);
            _scroller = FindAncestor<ScrollViewer>(_header);
            if (_group == null || _scroller == null) return;

            _transform = _header.RenderTransform as TranslateTransform;
            if (_transform == null)
            {
                _transform = new TranslateTransform();
                _header.RenderTransform = _transform;
            }

            _scroller.ScrollChanged += OnScrollChanged;
            Update();
        }

        public void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

        public void Detach()
        {
            if (_scroller != null) _scroller.ScrollChanged -= OnScrollChanged;
            if (_transform != null) _transform.Y = 0;
            _scroller = null;
            _group = null;
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e) => Update();

        private void Update()
        {
            if (_group == null || _scroller == null || _transform == null) return;
            if (!_group.IsVisible || !_header.IsVisible) return;

            var groupTop = _group.TranslatePoint(new Point(0, 0), _scroller).Y;
            var groupBottom = groupTop + _group.ActualHeight;
            var headerHeight = _header.ActualHeight;

            if (groupTop >= 0)
            {
                _transform.Y = 0;
            }
            else if (groupBottom <= headerHeight)
            {
                _transform.Y = _group.ActualHeight - headerHeight;
            }
            else
            {
                _transform.Y = -groupTop;
            }
        }

        private static T? FindAncestor<T>(DependencyObject from) where T : DependencyObject
        {
            var current = VisualTreeHelper.GetParent(from);
            while (current != null)
            {
                if (current is T match) return match;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }
    }
}
