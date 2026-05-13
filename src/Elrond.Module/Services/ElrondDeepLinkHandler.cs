using System.Text.RegularExpressions;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Modules;

namespace Elrond.Services;

/// <summary>
/// Handles <c>mithril://elrond/&lt;skillKey&gt;</c>. Skill keys are id-shaped
/// (matches <c>SkillEntry.Key</c>); reuse the strict item-style pattern. Hyphens
/// and spaces are explicitly NOT permitted — the human-readable display name is
/// never on the wire.
/// </summary>
public sealed class ElrondDeepLinkHandler : IDeepLinkHandler
{
    private static readonly Regex PayloadPattern = new("^[A-Za-z0-9_]{1,128}$", RegexOptions.Compiled);

    private readonly IElrondSkillImportTarget _target;

    public ElrondDeepLinkHandler(IElrondSkillImportTarget target) => _target = target;

    public string Action => "elrond";

    public bool TryHandle(string subPath, IDiagnosticsSink? diag)
    {
        if (!PayloadPattern.IsMatch(subPath))
        {
            diag?.Info("DeepLink", $"Rejected: elrond payload '{subPath}' failed validation.");
            return false;
        }
        _target.ImportFromLinkPayload(subPath);
        return true;
    }
}
