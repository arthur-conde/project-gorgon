namespace Mithril.Shared.Modules;

/// <summary>
/// Optional target that handles <c>mithril://pippin/&lt;payload&gt;</c> deep links.
/// Implemented by Pippin to decode the share payload, activate its tab, and present a
/// read-only view of another player's Gourmand progress. The router treats it as
/// optional — if no module has registered a handler, pippin links are logged and
/// dropped.
/// </summary>
public interface IPippinShareImportTarget
{
    /// <summary>
    /// Decodes <paramref name="base64UrlPayload"/> (the path segment from the deep link)
    /// and shows the read-only progress view. All errors are logged and swallowed —
    /// callers are OS activation handlers that mustn't throw.
    /// </summary>
    void ImportFromLinkPayload(string base64UrlPayload);
}
