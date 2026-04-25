using System.Text.RegularExpressions;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Wpf;

namespace Mithril.Shared.Modules;

/// <summary>
/// Default <see cref="IDeepLinkRouter"/> implementation. Current actions:
/// <list type="bullet">
///   <item><c>mithril://item/&lt;internalName&gt;</c> — opens <see cref="IItemDetailPresenter"/>.</item>
///   <item><c>mithril://list/&lt;base64url&gt;</c> — hands the payload to
///         <see cref="ICraftListImportTarget"/> (Celebrimbor).</item>
/// </list>
/// Each action defines its own payload grammar so item names stay strict while the craft-list
/// payload can hold a much longer base64url blob. Unknown actions are logged and dropped.
/// </summary>
public sealed class DeepLinkRouter : IDeepLinkRouter
{
    private const string Scheme = "mithril";

    // Item internal names in the reference data are ASCII identifiers; tighten the grammar so
    // we refuse anything that could confuse downstream lookups or smuggle separators.
    private static readonly Regex ItemPayloadPattern = new("^[A-Za-z0-9_]{1,128}$", RegexOptions.Compiled);

    // mithril://list carries a base64url-encoded gzip payload. Base64url uses [A-Za-z0-9_-];
    // cap length well above any plausible compressed craft-list (~8KB url) so we fail fast on
    // anything pathological rather than handing it to the decoder.
    private static readonly Regex ListPayloadPattern = new("^[A-Za-z0-9_-]{1,8192}$", RegexOptions.Compiled);

    private readonly IItemDetailPresenter _itemDetail;
    private readonly ICraftListImportTarget? _listImport;
    private readonly IDiagnosticsSink? _diag;

    public DeepLinkRouter(
        IItemDetailPresenter itemDetail,
        ICraftListImportTarget? listImport = null,
        IDiagnosticsSink? diag = null)
    {
        _itemDetail = itemDetail;
        _listImport = listImport;
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
            _diag?.Info("DeepLink", $"Rejected: scheme '{parsed.Scheme}' is not 'mithril'.");
            return false;
        }

        // mithril://item/CraftedLeatherBoots5 → Host="item", AbsolutePath="/CraftedLeatherBoots5"
        var action = parsed.Host.ToLowerInvariant();
        var payload = parsed.AbsolutePath.TrimStart('/');

        switch (action)
        {
            case "item":
                if (!ItemPayloadPattern.IsMatch(payload))
                {
                    _diag?.Info("DeepLink", $"Rejected: item payload '{payload}' failed validation.");
                    return false;
                }
                _itemDetail.Show(payload);
                return true;

            case "list":
                if (!ListPayloadPattern.IsMatch(payload))
                {
                    _diag?.Info("DeepLink", $"Rejected: list payload (len={payload.Length}) failed validation.");
                    return false;
                }
                if (_listImport is null)
                {
                    _diag?.Info("DeepLink", "Rejected: no craft-list import target registered.");
                    return false;
                }
                _listImport.ImportFromLinkPayload(payload);
                return true;

            default:
                _diag?.Info("DeepLink", $"Rejected: unknown action '{action}'.");
                return false;
        }
    }
}
