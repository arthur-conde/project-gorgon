using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Capture;
using Mithril.Shared.Settings;

namespace Mithril.Shell;

/// <summary>
/// Shell-owned <see cref="IMapCaptureRectStore"/> backing the Capture-defined seam
/// with the persisted <see cref="ShellSettings.MapCaptureBbox"/> (#947). The shell
/// references both Legolas and the Capture project, so backing a Capture-defined
/// store here keeps the <c>Capture ↛ Legolas.Module</c> boundary intact.
///
/// <para><see cref="Set"/> writes the field AND flushes via
/// <see cref="ISettingsStore{T}.Save"/> immediately — <c>ShellSettings</c> has no
/// autosaver (it's saved explicitly), so the rect must persist on write rather than
/// relying on a shutdown-only save. This is the #947 guarantee: the region survives
/// regardless of window state.</para>
/// </summary>
public sealed class ShellMapCaptureRectStore : IMapCaptureRectStore
{
    private readonly ShellSettings _settings;
    private readonly ISettingsStore<ShellSettings> _store;
    private readonly ILogger? _logger;

    public ShellMapCaptureRectStore(
        ShellSettings settings,
        ISettingsStore<ShellSettings> store,
        ILogger? logger = null)
    {
        _settings = settings;
        _store = store;
        _logger = logger;
    }

    public CaptureRect? Get()
    {
        var b = _settings.MapCaptureBbox;
        return b is null ? null : new CaptureRect(b.Left, b.Top, b.Width, b.Height);
    }

    public void Set(CaptureRect rect)
    {
        _settings.MapCaptureBbox = new MapCaptureBbox
        {
            Left = rect.X,
            Top = rect.Y,
            Width = rect.Width,
            Height = rect.Height,
        };
        _store.Save(_settings);
        _logger?.LogInformation(
            "Persisted map capture bbox {Width}x{Height} at ({Left},{Top}) physical px to shell settings.",
            rect.Width, rect.Height, rect.X, rect.Y);
    }
}
