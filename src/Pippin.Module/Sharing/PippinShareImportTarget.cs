using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Windows;
using Mithril.Shared.Modules;
using Mithril.Shared.Sharing;
using Pippin.Domain;

namespace Pippin.Sharing;

/// <summary>
/// Pippin's implementation of <see cref="IPippinShareImportTarget"/>. Decodes the deep
/// link's base64url payload, deserializes a <see cref="PippinSharePayload"/>, brings
/// the Pippin tab to the foreground, and opens a non-modal
/// <see cref="SharedProgressWindow"/> with the sender's data joined against the local
/// catalog.
/// </summary>
public sealed class PippinShareImportTarget : IPippinShareImportTarget
{
    private readonly FoodCatalog _catalog;
    private readonly IModuleActivator? _activator;
    private readonly ILogger? _logger;

    public PippinShareImportTarget(
        FoodCatalog catalog,
        IModuleActivator? activator = null,
        ILogger? logger = null)
    {
        _catalog = catalog;
        _activator = activator;
        _logger = logger;
    }

    public void ImportFromLinkPayload(string base64UrlPayload)
    {
        if (!ShareCodec.TryDecodePayload(base64UrlPayload, out var json, out var error))
        {
            _logger?.LogInformation($"Share link decode failed: {error}");
            return;
        }

        PippinSharePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(json, PippinShareJsonContext.Default.PippinSharePayload);
        }
        catch (JsonException ex)
        {
            _logger?.LogInformation($"Share link payload is not valid PippinSharePayload JSON: {ex.Message}");
            return;
        }
        if (payload is null)
        {
            _logger?.LogInformation("Share link payload deserialized to null.");
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            Show(payload);
        else
            dispatcher.InvokeAsync(() => Show(payload));
    }

    private void Show(PippinSharePayload payload)
    {
        if (_activator is not null && !_activator.Activate("pippin"))
            _logger?.LogInformation("Deep-link import: module activator could not find 'pippin'.");

        var vm = new SharedProgressViewModel(payload, _catalog);
        var window = new SharedProgressWindow(vm)
        {
            Owner = Application.Current?.MainWindow,
        };
        window.Show();
    }
}
