using System.Threading;
using System.Threading.Tasks;
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

    /// <summary>1000×400 social card. Async — renders on a background STA worker thread
    /// so the UI stays responsive while WPF lays out the visual tree.</summary>
    public Task<BitmapSource> RenderCardAsync(PippinSharePayload payload, int gourmandLevel)
        => RenderOnStaWorker(() =>
        {
            var vm = new PippinShareCardViewModel(payload, _catalog, gourmandLevel, _iconCache);
            var view = new PippinShareCardView { DataContext = vm };
            return RenderControl(view, PippinShareCardViewModel.CardWidth, PippinShareCardViewModel.CardHeight);
        });

    /// <summary>PNG sized to the card grid's natural height — width fixed to a column-count
    /// constraint, height read from the laid-out WrapPanel via <see cref="FrameworkElement.DesiredSize"/>.
    /// Async — renders on a background STA worker so the dialog stays responsive while
    /// hundreds of card containers are laid out.</summary>
    public Task<BitmapSource> RenderFullGridAsync(PippinSharePayload payload, int gourmandLevel)
        => RenderOnStaWorker(() =>
        {
            var vm = new PippinFullGridViewModel(payload, _catalog, gourmandLevel, _iconCache);
            var view = new PippinFullGridView { DataContext = vm };
            return RenderControl(view, PippinFullGridViewModel.Width, height: null);
        });

    /// <summary>
    /// Runs <paramref name="render"/> on a fresh STA thread. RenderTargetBitmap requires
    /// the rendering visual to live on an STA-apartment thread with a Dispatcher, but
    /// nothing requires that thread to be the main UI thread; a worker STA does fine.
    /// The returned <see cref="BitmapSource"/> is <c>.Freeze()</c>'d inside
    /// <see cref="RenderControl"/>, so it crosses threads safely.
    /// Catalog / icon-cache reads from the worker are safe: <c>FoodCatalog.ByInternalName</c>
    /// is reassigned atomically; <c>IconCacheService</c> uses ConcurrentDictionary and
    /// freezes every BitmapImage it returns.
    /// </summary>
    private static Task<BitmapSource> RenderOnStaWorker(Func<BitmapSource> render)
    {
        var tcs = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var result = render();
                tcs.SetResult(result);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "PippinShareRender",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    /// <summary>
    /// Lays out <paramref name="control"/> at <paramref name="width"/> and renders to a bitmap.
    /// When <paramref name="height"/> is null, the control's natural height (post-Measure)
    /// is used — which lets card-grid layouts auto-size to their wrapped content instead
    /// of relying on a manually-computed TotalHeight.
    /// </summary>
    private static BitmapSource RenderControl(FrameworkElement control, double width, double? height)
    {
        // Force a layout pass so every binding resolves before the render. Without the
        // explicit Measure/Arrange/UpdateLayout dance, a never-parented FrameworkElement
        // has zero size and RenderTargetBitmap captures nothing.
        control.Width = width;
        if (height is { } fixedHeight)
        {
            control.Height = fixedHeight;
            control.Measure(new Size(width, fixedHeight));
            control.Arrange(new Rect(0, 0, width, fixedHeight));
            control.UpdateLayout();
        }
        else
        {
            // Measure-driven height: WrapPanel + ItemsControl don't fully materialise their
            // children on a single Measure pass when we hand it infinite height. We need a
            // Measure → Arrange → UpdateLayout → Measure round-trip so the second Measure
            // sees fully realized item containers and reports the true DesiredSize.
            control.Measure(new Size(width, double.PositiveInfinity));
            control.Arrange(new Rect(0, 0, width, control.DesiredSize.Height));
            control.UpdateLayout();
            control.Measure(new Size(width, double.PositiveInfinity));
            var natural = control.DesiredSize.Height;
            control.Arrange(new Rect(0, 0, width, natural));
            control.UpdateLayout();
            fixedHeight = natural;
        }

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width),
            (int)Math.Ceiling(fixedHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(control);
        bitmap.Freeze();
        return bitmap;
    }
}
