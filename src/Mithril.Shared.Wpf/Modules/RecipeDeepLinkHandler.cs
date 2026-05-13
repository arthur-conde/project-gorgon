using System.Text.RegularExpressions;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;

namespace Mithril.Shared.Modules;

/// <summary>Handles <c>mithril://recipe/&lt;internalName&gt;</c>. See <see cref="ItemDeepLinkHandler"/>.</summary>
public sealed class RecipeDeepLinkHandler : IDeepLinkHandler
{
    private static readonly Regex PayloadPattern = new("^[A-Za-z0-9_]{1,128}$", RegexOptions.Compiled);

    private readonly IReferenceNavigator _navigator;

    public RecipeDeepLinkHandler(IReferenceNavigator navigator) => _navigator = navigator;

    public string Action => "recipe";

    public bool TryHandle(string subPath, IDiagnosticsSink? diag)
    {
        if (!PayloadPattern.IsMatch(subPath))
        {
            diag?.Info("DeepLink", $"Rejected: recipe payload '{subPath}' failed validation.");
            return false;
        }
        _navigator.Open(EntityRef.Recipe(subPath));
        return true;
    }
}
