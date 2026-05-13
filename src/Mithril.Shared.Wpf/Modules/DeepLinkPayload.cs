using System.Text.RegularExpressions;

namespace Mithril.Shared.Modules;

/// <summary>
/// Shared validation helpers for <c>mithril://</c> deep-link payloads. Centralises
/// the rules so they stay in sync as more handlers ship.
/// </summary>
public static class DeepLinkPayload
{
    private static readonly Regex InternalNamePattern = new("^[A-Za-z0-9_]{1,128}$", RegexOptions.Compiled);

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="name"/> matches the reference-data
    /// internal-name grammar: 1–128 ASCII characters from <c>[A-Za-z0-9_]</c>. The cap
    /// guards downstream lookups against unbounded inputs; the alphabet refuses anything
    /// that could smuggle URI separators or whitespace.
    /// </summary>
    public static bool IsValidInternalName(string name) => InternalNamePattern.IsMatch(name);
}
