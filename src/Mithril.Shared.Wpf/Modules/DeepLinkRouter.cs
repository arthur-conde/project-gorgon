using Microsoft.Extensions.Logging;

namespace Mithril.Shared.Modules;

/// <summary>
/// Default <see cref="IDeepLinkRouter"/>. Builds a per-action lookup from injected
/// <see cref="IDeepLinkHandler"/>s and delegates payload parsing + dispatch to the
/// matching handler. Each handler owns its payload grammar; the router only
/// validates the scheme and finds the right handler.
/// </summary>
public sealed class DeepLinkRouter : IDeepLinkRouter
{
    private const string Scheme = "mithril";

    private readonly IReadOnlyDictionary<string, IDeepLinkHandler> _handlers;
    private readonly ILogger? _logger;

    public DeepLinkRouter(IEnumerable<IDeepLinkHandler> handlers, ILogger? logger = null)
    {
        // Fail-loud on duplicate Action registrations — that's a DI ordering bug,
        // not graceful-degradation territory.
        var byAction = new Dictionary<string, IDeepLinkHandler>(StringComparer.Ordinal);
        foreach (var h in handlers)
        {
            var key = h.Action.ToLowerInvariant();
            if (byAction.ContainsKey(key))
                throw new InvalidOperationException(
                    $"Duplicate IDeepLinkHandler registration for action '{key}': " +
                    $"{byAction[key].GetType().FullName} and {h.GetType().FullName}.");
            byAction[key] = h;
        }
        _handlers = byAction;
        _logger = logger;
    }

    public bool Handle(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return false;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            _logger?.LogInformation($"Rejected: not a well-formed URI: '{uri}'.");
            return false;
        }
        if (!string.Equals(parsed.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogInformation($"Rejected: scheme '{parsed.Scheme}' is not 'mithril'.");
            return false;
        }

        var action = parsed.Host.ToLowerInvariant();
        var subPath = parsed.AbsolutePath.TrimStart('/');

        if (!_handlers.TryGetValue(action, out var handler))
        {
            _logger?.LogInformation($"Rejected: unknown action '{action}'.");
            return false;
        }
        return handler.TryHandle(subPath, _logger);
    }
}
