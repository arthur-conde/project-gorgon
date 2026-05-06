using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Mithril.Shared.Icons;

namespace Mithril.Shared.Wpf;

/// <summary>
/// Lightweight control that renders a game item icon from the icon cache.
/// Binds to an <see cref="IconId"/> dependency property; loads from disk/CDN
/// via the static <see cref="IIconCacheService"/> reference.
/// </summary>
public sealed class IconImage : Image
{
    private static IIconCacheService? _cache;

    /// <summary>Must be called once at app startup after the DI container is built.</summary>
    public static void SetCacheService(IIconCacheService cache) => _cache = cache;

    public static readonly DependencyProperty IconIdProperty = DependencyProperty.Register(
        nameof(IconId), typeof(int), typeof(IconImage),
        new FrameworkPropertyMetadata(0, OnIconIdChanged));

    public int IconId
    {
        get => (int)GetValue(IconIdProperty);
        set => SetValue(IconIdProperty, value);
    }

    static IconImage()
    {
        // DP metadata defaults (24×24) so that consumers can still override Width/Height
        // in XAML without contending with constructor-time local DP writes.
        WidthProperty.OverrideMetadata(typeof(IconImage), new FrameworkPropertyMetadata(24.0));
        HeightProperty.OverrideMetadata(typeof(IconImage), new FrameworkPropertyMetadata(24.0));
    }

    public IconImage()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static void OnIconIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is IconImage self && self.IsLoaded)
            self.Refresh();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_cache is not null)
            _cache.IconReady += OnIconReady;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_cache is not null)
            _cache.IconReady -= OnIconReady;
    }

    private void OnIconReady(object? sender, int readyId)
    {
        if (readyId == IconId)
            Refresh();
    }

    private void Refresh()
    {
        if (_cache is null || IconId <= 0)
        {
            Source = null;
            Visibility = Visibility.Collapsed;
            return;
        }

        Source = _cache.GetOrLoadIcon(IconId);
        Visibility = Visibility.Visible;
    }
}
