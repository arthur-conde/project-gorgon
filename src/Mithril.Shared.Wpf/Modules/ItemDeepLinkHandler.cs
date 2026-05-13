using System.Text.RegularExpressions;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;

namespace Mithril.Shared.Modules;

/// <summary>
/// Handles <c>mithril://item/&lt;internalName&gt;</c>. Internal names in the reference
/// data are ASCII identifiers; the regex refuses anything that could confuse downstream
/// lookups or smuggle separators.
/// </summary>
public sealed class ItemDeepLinkHandler : IDeepLinkHandler
{
    private static readonly Regex PayloadPattern = new("^[A-Za-z0-9_]{1,128}$", RegexOptions.Compiled);

    private readonly IReferenceNavigator _navigator;

    public ItemDeepLinkHandler(IReferenceNavigator navigator) => _navigator = navigator;

    public string Action => "item";

    public bool TryHandle(string subPath, IDiagnosticsSink? diag)
    {
        if (!PayloadPattern.IsMatch(subPath))
        {
            diag?.Info("DeepLink", $"Rejected: item payload '{subPath}' failed validation.");
            return false;
        }
        _navigator.Open(EntityRef.Item(subPath));
        return true;
    }
}
