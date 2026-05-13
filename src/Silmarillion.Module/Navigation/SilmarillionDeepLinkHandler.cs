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
///
/// Kind dispatch delegates to the navigator's <see cref="IReferenceNavigator.CanOpen"/>
/// so the handler inherits whatever kinds the navigator's
/// <c>IReferenceKindTarget</c> registry covers — new Bucket-B tabs (NPCs, Quests, …)
/// become deep-linkable without touching this file.
/// </summary>
public sealed class SilmarillionDeepLinkHandler : IDeepLinkHandler
{
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

        // Cap at 3 so 'item/foo/bar' yields three parts and gets rejected as
        // extra-segments rather than silently accepted.
        var parts = subPath.Split('/', 3);
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            diag?.Info("DeepLink", $"Rejected: silmarillion payload '{subPath}' must be '<kind>/<name>'.");
            return false;
        }

        var kind = parts[0];
        var name = parts[1];

        if (!DeepLinkPayload.IsValidInternalName(name))
        {
            diag?.Info("DeepLink", $"Rejected: silmarillion name '{name}' failed validation.");
            return false;
        }

        if (!Enum.TryParse<EntityKind>(kind, ignoreCase: true, out var entityKind))
        {
            diag?.Info("DeepLink", $"Rejected: silmarillion kind '{kind}' is not a known EntityKind.");
            return false;
        }

        var entityRef = new EntityRef(entityKind, name);
        if (!_navigator.CanOpen(entityRef))
        {
            diag?.Info("DeepLink", $"Rejected: silmarillion kind '{kind}' has no registered target yet.");
            return false;
        }

        _navigator.Open(entityRef);
        return true;
    }
}
