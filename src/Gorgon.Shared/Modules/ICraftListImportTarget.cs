namespace Gorgon.Shared.Modules;

/// <summary>
/// Optional target that handles <c>gorgon://list/&lt;payload&gt;</c> deep links. Implemented by
/// Celebrimbor to decode the share payload, activate its tab, and run the existing
/// Append/Replace/Cancel import dialog. The router treats it as optional — if no module has
/// registered a handler, list links are logged and dropped.
/// </summary>
public interface ICraftListImportTarget
{
    /// <summary>
    /// Decodes <paramref name="base64UrlPayload"/> (the path segment from the deep link) and
    /// prompts the user to import it. All errors are logged and swallowed — callers are OS
    /// activation handlers that mustn't throw.
    /// </summary>
    void ImportFromLinkPayload(string base64UrlPayload);
}
