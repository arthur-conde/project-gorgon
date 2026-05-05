using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mithril.Shared.Icons;
using Pippin.Domain;

namespace Pippin.Sharing;

/// <summary>
/// Renders a <see cref="PippinSharePayload"/> to a <see cref="BitmapSource"/> off-screen
/// via <see cref="RenderTargetBitmap"/>. Two outputs:
/// <list type="bullet">
///   <item><see cref="RenderCard"/> — fixed 1000×400 social card.</item>
///   <item><see cref="RenderFullGrid"/> — fixed-width tall grid for archival.</item>
/// </list>
/// Renderer never touches the live <c>GourmandViewModel</c> or the live grid; it
/// constructs a fresh control tree, lays it out off-screen, and snapshots it. Icons
/// must be cached locally before render — see
/// <c>IIconCacheService.PreloadAsync</c> and the share dialog's preload banner.
/// </summary>
public sealed class PippinShareCardRenderer
{
    private readonly FoodCatalog _catalog;
    private readonly IIconCacheService _iconCache;

    public PippinShareCardRenderer(FoodCatalog catalog, IIconCacheService iconCache)
    {
        _catalog = catalog;
        _iconCache = iconCache;
    }

    /// <summary>1000×400 social card.</summary>
    public BitmapSource RenderCard(PippinSharePayload payload, int gourmandLevel)
    {
        var vm = new PippinShareCardViewModel(payload, _catalog, gourmandLevel, _iconCache);
        var view = new PippinShareCardView { DataContext = vm };
        return RenderControl(view, PippinShareCardViewModel.CardWidth, PippinShareCardViewModel.CardHeight);
    }

    /// <summary>Tall PNG with header strip + every food row.</summary>
    public BitmapSource RenderFullGrid(PippinSharePayload payload, int gourmandLevel)
    {
        var vm = new PippinFullGridViewModel(payload, _catalog, gourmandLevel, _iconCache);
        var view = new PippinFullGridView { DataContext = vm };
        return RenderControl(view, PippinFullGridViewModel.Width, vm.TotalHeight);
    }

    private static BitmapSource RenderControl(FrameworkElement control, double width, double height)
    {
        // Force a layout pass so every binding resolves before the render. Without the
        // explicit Measure/Arrange/UpdateLayout dance, a never-parented FrameworkElement
        // has zero size and RenderTargetBitmap captures nothing.
        control.Width = width;
        control.Height = height;
        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));
        control.UpdateLayout();

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width),
            (int)Math.Ceiling(height),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(control);
        bitmap.Freeze();
        return bitmap;
    }
}
