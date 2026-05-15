using System.Text.RegularExpressions;

namespace Mithril.Shared.Modules;

/// <summary>
/// Shared validation helpers for <c>mithril://</c> deep-link payloads. Centralises
/// the rules so they stay in sync as more handlers ship.
/// </summary>
public static class DeepLinkPayload
{
    private static readonly Regex InternalNamePattern = new("^[A-Za-z0-9_]{1,128}$", RegexOptions.Compiled);

    private static readonly Regex EnvelopeKeyPattern = new(@"^\*?[A-Za-z0-9_]{1,128}$", RegexOptions.Compiled);

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="name"/> matches the reference-data
    /// internal-name grammar: 1–128 ASCII characters from <c>[A-Za-z0-9_]</c>. The cap
    /// guards downstream lookups against unbounded inputs; the alphabet refuses anything
    /// that could smuggle URI separators or whitespace.
    /// </summary>
    public static bool IsValidInternalName(string name) => InternalNamePattern.IsMatch(name);

    /// <summary>
    /// Like <see cref="IsValidInternalName"/> but additionally permits a single optional
    /// leading <c>'*'</c>. Some envelope keys (StorageVault account-wide / transfer-chest
    /// entries, e.g. <c>"*AccountStorage_Serbule"</c>) carry a meaningful <c>'*'</c> prefix
    /// that the bare internal-name grammar would reject. The <c>'*'</c> still can't smuggle
    /// URI separators or whitespace, so the URI-safety intent of the base grammar holds.
    /// The module-scoped <c>mithril://silmarillion/&lt;kind&gt;/&lt;key&gt;</c> handler uses
    /// this so a deep link to a <c>'*'</c>-prefixed key round-trips
    /// (URL-encoded as <c>%2A</c>; <see cref="System.Uri.AbsolutePath"/> decodes it back).
    /// </summary>
    public static bool IsValidEnvelopeKey(string name) => EnvelopeKeyPattern.IsMatch(name);
}
