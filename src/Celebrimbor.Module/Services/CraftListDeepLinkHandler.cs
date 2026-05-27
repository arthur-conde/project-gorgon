using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using Mithril.Shared.Modules;

namespace Celebrimbor.Services;

/// <summary>Handles <c>mithril://list/&lt;base64url&gt;</c> craft-list imports.</summary>
public sealed class CraftListDeepLinkHandler : IDeepLinkHandler
{
    // base64url alphabet = [A-Za-z0-9_-]. Length cap matches the pre-registry behaviour.
    private static readonly Regex PayloadPattern = new("^[A-Za-z0-9_-]{1,8192}$", RegexOptions.Compiled);

    private readonly ICraftListImportTarget _target;

    public CraftListDeepLinkHandler(ICraftListImportTarget target) => _target = target;

    public string Action => "list";

    public bool TryHandle(string subPath, ILogger? logger)
    {
        if (!PayloadPattern.IsMatch(subPath))
        {
            logger?.LogInformation($"Rejected: list payload (len={subPath.Length}) failed validation.");
            return false;
        }
        _target.ImportFromLinkPayload(subPath);
        return true;
    }
}
