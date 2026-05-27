using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using Mithril.Shared.Modules;

namespace Legolas.Sharing;

/// <summary>Handles <c>mithril://legolas/&lt;base64url&gt;</c> survey-report imports.</summary>
public sealed class LegolasDeepLinkHandler : IDeepLinkHandler
{
    // Legolas payloads are bounded by a single survey run (≤ a couple dozen items + timestamps).
    private static readonly Regex PayloadPattern = new("^[A-Za-z0-9_-]{1,8192}$", RegexOptions.Compiled);

    private readonly ILegolasShareImportTarget _target;

    public LegolasDeepLinkHandler(ILegolasShareImportTarget target) => _target = target;

    public string Action => "legolas";

    public bool TryHandle(string subPath, ILogger? logger)
    {
        if (!PayloadPattern.IsMatch(subPath))
        {
            logger?.LogInformation($"Rejected: legolas payload (len={subPath.Length}) failed validation.");
            return false;
        }
        _target.ImportFromLinkPayload(subPath);
        return true;
    }
}
