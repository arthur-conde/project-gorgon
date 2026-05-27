using Microsoft.Extensions.Logging;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;

namespace Mithril.Shared.Modules;

/// <summary>Handles <c>mithril://recipe/&lt;internalName&gt;</c>. See <see cref="ItemDeepLinkHandler"/>.</summary>
public sealed class RecipeDeepLinkHandler : IDeepLinkHandler
{
    private readonly IReferenceNavigator _navigator;

    public RecipeDeepLinkHandler(IReferenceNavigator navigator) => _navigator = navigator;

    public string Action => "recipe";

    public bool TryHandle(string subPath, ILogger? logger)
    {
        if (!DeepLinkPayload.IsValidInternalName(subPath))
        {
            logger?.LogDiagnosticInfo("DeepLink", $"Rejected: recipe payload '{subPath}' failed validation.");
            return false;
        }
        _navigator.Open(EntityRef.Recipe(subPath));
        return true;
    }
}
