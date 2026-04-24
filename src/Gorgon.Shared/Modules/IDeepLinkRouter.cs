namespace Gorgon.Shared.Modules;

/// <summary>
/// Parses and dispatches <c>gorgon://</c> activation URIs forwarded from the OS (custom
/// URI scheme) or from a second instance. Strict whitelist: scheme must be <c>gorgon</c>,
/// the action segment must be a registered handler, and the payload must match the
/// expected shape. Anything else is logged and dropped.
/// </summary>
public interface IDeepLinkRouter
{
    /// <summary>Dispatches <paramref name="uri"/>. Returns true when handled, false for no-op.</summary>
    bool Handle(string? uri);
}
