using System.Text.RegularExpressions;
using Gorgon.Shared.Diagnostics;
using Gorgon.Shared.Wpf;

namespace Gorgon.Shared.Modules;

/// <summary>
/// Default <see cref="IDeepLinkRouter"/> implementation. Current actions:
/// <list type="bullet">
///   <item><c>gorgon://item/&lt;internalName&gt;</c> — opens <see cref="IItemDetailPresenter"/>.</item>
/// </list>
/// Future actions (recipes, craft-list import) plug in via the switch in <see cref="Handle"/>.
/// </summary>
public sealed class DeepLinkRouter : IDeepLinkRouter
{
    private const string Scheme = "gorgon";

    // InternalNames in the reference data are ASCII identifiers; tighten the grammar so we
    // refuse anything that could confuse downstream lookups or smuggle separators.
    private static readonly Regex PayloadPattern = new("^[A-Za-z0-9_]{1,128}$", RegexOptions.Compiled);

    private readonly IItemDetailPresenter _itemDetail;
    private readonly IDiagnosticsSink? _diag;

    public DeepLinkRouter(IItemDetailPresenter itemDetail, IDiagnosticsSink? diag = null)
    {
        _itemDetail = itemDetail;
        _diag = diag;
    }

    public bool Handle(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return false;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            _diag?.Info("DeepLink", $"Rejected: not a well-formed URI: '{uri}'.");
            return false;
        }
        if (!string.Equals(parsed.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            _diag?.Info("DeepLink", $"Rejected: scheme '{parsed.Scheme}' is not 'gorgon'.");
            return false;
        }

        // gorgon://item/CraftedLeatherBoots5 → Host="item", AbsolutePath="/CraftedLeatherBoots5"
        var action = parsed.Host.ToLowerInvariant();
        var payload = parsed.AbsolutePath.TrimStart('/');
        if (!PayloadPattern.IsMatch(payload))
        {
            _diag?.Info("DeepLink", $"Rejected: payload '{payload}' failed validation.");
            return false;
        }

        switch (action)
        {
            case "item":
                _itemDetail.Show(payload);
                return true;
            default:
                _diag?.Info("DeepLink", $"Rejected: unknown action '{action}'.");
                return false;
        }
    }
}
