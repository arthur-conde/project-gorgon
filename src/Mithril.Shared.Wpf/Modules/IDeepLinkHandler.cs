using Microsoft.Extensions.Logging;
using Mithril.Shared.Diagnostics;

namespace Mithril.Shared.Modules;

/// <summary>
/// A single mithril:// scheme dispatch handler. The <see cref="DeepLinkRouter"/>
/// looks handlers up by <see cref="Action"/> (the URI host segment) and delegates
/// payload parsing and dispatch to each implementation.
///
/// Replaces the per-action <c>switch</c> the router used pre-#229. Each handler
/// owns its payload grammar (regex, length cap, segment-splitting), its dependency
/// null-checks, and its diagnostic messages.
/// </summary>
public interface IDeepLinkHandler
{
    /// <summary>The first path segment after <c>mithril://</c>. Must be lowercase ASCII.</summary>
    string Action { get; }

    /// <summary>
    /// Handle the remainder of the URI path (everything after the host segment,
    /// with the leading '/' stripped). Implementations own their payload grammar
    /// and any per-handler diagnostic messages. Return false for validation
    /// failure or missing dependency; true on successful dispatch.
    /// </summary>
    bool TryHandle(string subPath, ILogger? logger);
}
