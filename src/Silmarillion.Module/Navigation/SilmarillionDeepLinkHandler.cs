using System.Text.RegularExpressions;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;

namespace Silmarillion.Navigation;

/// <summary>
/// Handles <c>mithril://silmarillion/&lt;kind&gt;/&lt;internalName&gt;</c> — the
/// module-scoped form (issue #229). Symmetric with <c>mithril://pippin/...</c>,
/// <c>mithril://legolas/...</c>, etc. The legacy single-kind forms
/// (<c>mithril://item/...</c> / <c>mithril://recipe/...</c>) remain supported
/// via <see cref="ItemDeepLinkHandler"/> / <see cref="RecipeDeepLinkHandler"/>.
/// </summary>
public sealed class SilmarillionDeepLinkHandler : IDeepLinkHandler
{
    private static readonly Regex PayloadPattern = new("^[A-Za-z0-9_]{1,128}$", RegexOptions.Compiled);

    private readonly IReferenceNavigator _navigator;

    public SilmarillionDeepLinkHandler(IReferenceNavigator navigator) => _navigator = navigator;

    public string Action => "silmarillion";

    public bool TryHandle(string subPath, IDiagnosticsSink? diag)
    {
        if (string.IsNullOrEmpty(subPath))
        {
            diag?.Info("DeepLink", "Rejected: silmarillion payload is empty.");
            return false;
        }

        // Strictly two segments: kind/name. Extra segments are rejected so the
        // grammar stays unambiguous when future kinds ship.
        var slash = subPath.IndexOf('/');
        if (slash < 0 || slash == subPath.Length - 1)
        {
            diag?.Info("DeepLink", $"Rejected: silmarillion payload '{subPath}' missing kind or name segment.");
            return false;
        }
        var kind = subPath.AsSpan(0, slash).ToString().ToLowerInvariant();
        var name = subPath[(slash + 1)..];
        if (name.Contains('/'))
        {
            diag?.Info("DeepLink", $"Rejected: silmarillion payload '{subPath}' has extra segments.");
            return false;
        }
        if (!PayloadPattern.IsMatch(name))
        {
            diag?.Info("DeepLink", $"Rejected: silmarillion name '{name}' failed validation.");
            return false;
        }

        switch (kind)
        {
            case "item":
                diag?.Info("DeepLink", $"Silmarillion handler dispatching item '{name}'.");
                _navigator.Open(EntityRef.Item(name));
                return true;
            case "recipe":
                diag?.Info("DeepLink", $"Silmarillion handler dispatching recipe '{name}'.");
                _navigator.Open(EntityRef.Recipe(name));
                return true;
            default:
                diag?.Info("DeepLink", $"Rejected: silmarillion kind '{kind}' is not yet routable.");
                return false;
        }
    }
}
