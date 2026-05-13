using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;

namespace Mithril.Shared.Modules;

/// <summary>Handles <c>mithril://recipe/&lt;internalName&gt;</c>. See <see cref="ItemDeepLinkHandler"/>.</summary>
public sealed class RecipeDeepLinkHandler : IDeepLinkHandler
{
    private readonly IReferenceNavigator _navigator;

    public RecipeDeepLinkHandler(IReferenceNavigator navigator) => _navigator = navigator;

    public string Action => "recipe";

    public bool TryHandle(string subPath, IDiagnosticsSink? diag)
    {
        if (!DeepLinkPayload.IsValidInternalName(subPath))
        {
            diag?.Info("DeepLink", $"Rejected: recipe payload '{subPath}' failed validation.");
            return false;
        }
        _navigator.Open(EntityRef.Recipe(subPath));
        return true;
    }
}
