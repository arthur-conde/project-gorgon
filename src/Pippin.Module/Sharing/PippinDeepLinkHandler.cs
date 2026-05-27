using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using Mithril.Shared.Modules;

namespace Pippin.Sharing;

/// <summary>Handles <c>mithril://pippin/&lt;base64url&gt;</c> shared-progress imports.</summary>
public sealed class PippinDeepLinkHandler : IDeepLinkHandler
{
    // Pippin payloads are larger than list because they encode the full-catalogue progress dump.
    private static readonly Regex PayloadPattern = new("^[A-Za-z0-9_-]{1,16384}$", RegexOptions.Compiled);

    private readonly IPippinShareImportTarget _target;

    public PippinDeepLinkHandler(IPippinShareImportTarget target) => _target = target;

    public string Action => "pippin";

    public bool TryHandle(string subPath, ILogger? logger)
    {
        if (!PayloadPattern.IsMatch(subPath))
        {
            logger?.LogInformation($"Rejected: pippin payload (len={subPath.Length}) failed validation.");
            return false;
        }
        _target.ImportFromLinkPayload(subPath);
        return true;
    }
}
