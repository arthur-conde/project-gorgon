using Microsoft.Extensions.Logging;
using Mithril.Shared.Reference;

namespace Mithril.Shared.Modules;

/// <summary>
/// Handles <c>mithril://item/&lt;internalName&gt;</c>. Validation lives in
/// <see cref="DeepLinkPayload.IsValidInternalName"/>.
/// </summary>
public sealed class ItemDeepLinkHandler : IDeepLinkHandler
{
    private readonly IReferenceNavigator _navigator;

    public ItemDeepLinkHandler(IReferenceNavigator navigator) => _navigator = navigator;

    public string Action => "item";

    public bool TryHandle(string subPath, ILogger? logger)
    {
        if (!DeepLinkPayload.IsValidInternalName(subPath))
        {
            logger?.LogInformation($"Rejected: item payload '{subPath}' failed validation.");
            return false;
        }
        _navigator.Open(EntityRef.Item(subPath));
        return true;
    }
}
