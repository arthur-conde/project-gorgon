using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Celebrimbor.Domain;
using Mithril.Shared.Reference;
using Mithril.Shared.Sharing;

namespace Celebrimbor.Services;

/// <summary>
/// Human-readable plain-text format for sharing craft lists via clipboard.
/// One entry per line: "RecipeInternalName x Quantity". Comments start with #.
/// </summary>
public static partial class CraftListFormat
{
    /// <summary>Serialize a craft list to the plain-text share format.</summary>
    public static string Serialize(IReadOnlyList<CraftListEntry> entries, DateTimeOffset? stampedAt = null)
    {
        var sb = new StringBuilder();
        var stamp = (stampedAt ?? DateTimeOffset.Now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        sb.Append("# Celebrimbor craft list · ").Append(stamp).Append(" · ").Append(entries.Count).AppendLine(" recipes");

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.RecipeInternalName) || entry.Quantity <= 0) continue;
            sb.Append(entry.RecipeInternalName).Append(" x ").Append(entry.Quantity).AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parse a craft list from plain text. Unknown recipe names and malformed
    /// quantities are collected into <see cref="ParseResult.Warnings"/> rather
    /// than throwing.
    /// </summary>
    public static ParseResult Parse(string text, IReferenceDataService refData)
    {
        var entries = new List<CraftListEntry>();
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return new ParseResult(entries, warnings);

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var match = LinePattern().Match(line);
            if (!match.Success)
            {
                warnings.Add($"Could not parse line: \"{line}\"");
                continue;
            }

            var name = match.Groups["name"].Value.Trim();
            if (!int.TryParse(match.Groups["qty"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var qty) || qty <= 0)
            {
                warnings.Add($"Invalid quantity on line: \"{line}\"");
                continue;
            }

            if (!refData.RecipesByInternalName.ContainsKey(name))
            {
                warnings.Add($"Unknown recipe: \"{name}\"");
                continue;
            }

            entries.Add(new CraftListEntry { RecipeInternalName = name, Quantity = qty });
        }

        return new ParseResult(entries, warnings);
    }

    /// <summary>Merge a parsed list into an existing list, summing quantities on duplicate recipe names.</summary>
    public static List<CraftListEntry> MergeAppend(IEnumerable<CraftListEntry> existing, IEnumerable<CraftListEntry> incoming)
    {
        var merged = existing.Select(e => new CraftListEntry { RecipeInternalName = e.RecipeInternalName, Quantity = e.Quantity }).ToList();
        foreach (var entry in incoming)
        {
            var hit = merged.FirstOrDefault(e => string.Equals(e.RecipeInternalName, entry.RecipeInternalName, StringComparison.Ordinal));
            if (hit is not null) hit.Quantity += entry.Quantity;
            else merged.Add(new CraftListEntry { RecipeInternalName = entry.RecipeInternalName, Quantity = entry.Quantity });
        }
        return merged;
    }

    // ---- Share-link encoding ------------------------------------------------
    //
    // mithril://list/<base64url> deep links embed a compressed copy of the plain-text share
    // format. The envelope (version byte + gzip + base64url) lives in
    // <see cref="ShareCodec"/> so other modules (Pippin progress, …) share one
    // implementation.

    /// <summary>
    /// Encodes a craft list into the base64url payload for a <c>mithril://list/…</c> deep link.
    /// Returns just the payload — callers prepend the scheme/host.
    /// </summary>
    public static string EncodeShareLink(IReadOnlyList<CraftListEntry> entries, DateTimeOffset? stampedAt = null)
        => ShareCodec.EncodePayload(Serialize(entries, stampedAt));

    /// <summary>
    /// Decodes a base64url deep-link payload into the plain-text share format, then parses it.
    /// Returns a <see cref="ParseResult"/> with a single synthesized warning on malformed input
    /// rather than throwing, so deep-link handlers can surface the error to the user without
    /// crashing.
    /// </summary>
    public static ParseResult DecodeShareLink(string base64UrlPayload, IReferenceDataService refData)
    {
        if (!ShareCodec.TryDecodePayload(base64UrlPayload, out var text, out var error))
            return new ParseResult([], [error!]);
        return Parse(text, refData);
    }

    [GeneratedRegex(@"^\s*(?<name>[^\d#].*?)\s*[x×X]\s*(?<qty>-?\d+)\s*$")]
    private static partial Regex LinePattern();
}

public sealed record ParseResult(IReadOnlyList<CraftListEntry> Entries, IReadOnlyList<string> Warnings);
