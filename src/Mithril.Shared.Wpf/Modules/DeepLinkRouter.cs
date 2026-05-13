using System.Text.RegularExpressions;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;

namespace Mithril.Shared.Modules;

/// <summary>
/// Default <see cref="IDeepLinkRouter"/> implementation. Current actions:
/// <list type="bullet">
///   <item><c>mithril://item/&lt;internalName&gt;</c> — opens the item via
///         <see cref="IReferenceNavigator.Open"/> (Silmarillion's master-detail; popup
///         <see cref="IItemDetailPresenter"/> is no longer involved).</item>
///   <item><c>mithril://recipe/&lt;internalName&gt;</c> — opens the recipe via
///         <see cref="IReferenceNavigator.Open"/>.</item>
///   <item><c>mithril://list/&lt;base64url&gt;</c> — hands the payload to
///         <see cref="ICraftListImportTarget"/> (Celebrimbor).</item>
///   <item><c>mithril://pippin/&lt;base64url&gt;</c> — hands the payload to
///         <see cref="IPippinShareImportTarget"/> (Pippin shared progress).</item>
///   <item><c>mithril://legolas/&lt;base64url&gt;</c> — hands the payload to
///         <see cref="ILegolasShareImportTarget"/> (Legolas survey report).</item>
///   <item><c>mithril://elrond/&lt;skillKey&gt;</c> — hands the payload to
///         <see cref="IElrondSkillImportTarget"/> (Elrond skill advisor).</item>
/// </list>
/// Each action defines its own payload grammar so item names stay strict while the craft-list
/// payload can hold a much longer base64url blob. Unknown actions are logged and dropped.
/// </summary>
public sealed class DeepLinkRouter : IDeepLinkRouter
{
    private const string Scheme = "mithril";

    // Item/Recipe internal names in the reference data are ASCII identifiers; tighten the grammar
    // so we refuse anything that could confuse downstream lookups or smuggle separators.
    private static readonly Regex EntityPayloadPattern = new("^[A-Za-z0-9_]{1,128}$", RegexOptions.Compiled);

    // mithril://list, pippin, and legolas all carry a base64url-encoded gzip payload.
    // Base64url uses [A-Za-z0-9_-]; cap length well above any plausible compressed payload
    // so we fail fast on anything pathological rather than handing it to the decoder.
    private static readonly Regex ListPayloadPattern = new("^[A-Za-z0-9_-]{1,8192}$", RegexOptions.Compiled);
    private static readonly Regex PippinPayloadPattern = new("^[A-Za-z0-9_-]{1,16384}$", RegexOptions.Compiled);
    // Legolas payloads are smaller than Pippin's full-catalog progress dumps — a survey
    // run is at most a couple dozen items + timestamps. 8192 is plenty.
    private static readonly Regex LegolasPayloadPattern = new("^[A-Za-z0-9_-]{1,8192}$", RegexOptions.Compiled);

    private readonly IReferenceNavigator _navigator;
    private readonly ICraftListImportTarget? _listImport;
    private readonly IPippinShareImportTarget? _pippinImport;
    private readonly ILegolasShareImportTarget? _legolasImport;
    private readonly IElrondSkillImportTarget? _elrondImport;
    private readonly IDiagnosticsSink? _diag;

    public DeepLinkRouter(
        IReferenceNavigator navigator,
        ICraftListImportTarget? listImport = null,
        IPippinShareImportTarget? pippinImport = null,
        ILegolasShareImportTarget? legolasImport = null,
        IElrondSkillImportTarget? elrondImport = null,
        IDiagnosticsSink? diag = null)
    {
        _navigator = navigator;
        _listImport = listImport;
        _pippinImport = pippinImport;
        _legolasImport = legolasImport;
        _elrondImport = elrondImport;
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
                if (!EntityPayloadPattern.IsMatch(payload))
                {
                    _diag?.Info("DeepLink", $"Rejected: item payload '{payload}' failed validation.");
                    return false;
                }
                _navigator.Open(EntityRef.Item(payload));
                return true;

            case "recipe":
                if (!EntityPayloadPattern.IsMatch(payload))
                {
                    _diag?.Info("DeepLink", $"Rejected: recipe payload '{payload}' failed validation.");
                    return false;
                }
                _navigator.Open(EntityRef.Recipe(payload));
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

            case "pippin":
                if (!PippinPayloadPattern.IsMatch(payload))
                {
                    _diag?.Info("DeepLink", $"Rejected: pippin payload (len={payload.Length}) failed validation.");
                    return false;
                }
                if (_pippinImport is null)
                {
                    _diag?.Info("DeepLink", "Rejected: no pippin import target registered.");
                    return false;
                }
                _pippinImport.ImportFromLinkPayload(payload);
                return true;

            case "legolas":
                if (!LegolasPayloadPattern.IsMatch(payload))
                {
                    _diag?.Info("DeepLink", $"Rejected: legolas payload (len={payload.Length}) failed validation.");
                    return false;
                }
                if (_legolasImport is null)
                {
                    _diag?.Info("DeepLink", "Rejected: no legolas import target registered.");
                    return false;
                }
                _legolasImport.ImportFromLinkPayload(payload);
                return true;

            case "elrond":
                // Skill keys are id-shaped (matches SkillEntry.Key, recipes' RewardSkill);
                // reuse the strict item pattern. Hyphens/spaces are NOT permitted — the
                // human-readable display name is never on the wire.
                if (!EntityPayloadPattern.IsMatch(payload))
                {
                    _diag?.Info("DeepLink", $"Rejected: elrond payload '{payload}' failed validation.");
                    return false;
                }
                if (_elrondImport is null)
                {
                    _diag?.Info("DeepLink", "Rejected: no elrond import target registered.");
                    return false;
                }
                _elrondImport.ImportFromLinkPayload(payload);
                return true;

            default:
                _diag?.Info("DeepLink", $"Rejected: unknown action '{action}'.");
                return false;
        }
    }
}
