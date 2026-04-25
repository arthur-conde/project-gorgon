using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Celebrimbor.Domain;
using Mithril.Shared.Reference;

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
    // format. Format is: one version byte, then gzip(UTF-8 plain-text). v1 is the only version
    // today; the prefix reserves room to swap the compression or switch formats later without
    // invalidating existing pastes. Base64url keeps the URL free of the '+' and '/' characters
    // that would otherwise need percent-encoding.
    //
    // Cap on decoded size: we refuse payloads that decompress past a sane ceiling so a
    // pathological or malicious link can't pin the UI thread or blow the heap.

    private const byte ShareLinkVersion = 1;
    private const int MaxDecompressedBytes = 256 * 1024; // 256 KB of plain text is absurdly large

    /// <summary>
    /// Encodes a craft list into the base64url payload for a <c>mithril://list/…</c> deep link.
    /// Returns just the payload — callers prepend the scheme/host.
    /// </summary>
    public static string EncodeShareLink(IReadOnlyList<CraftListEntry> entries, DateTimeOffset? stampedAt = null)
    {
        var text = Serialize(entries, stampedAt);
        var textBytes = Encoding.UTF8.GetBytes(text);

        using var ms = new MemoryStream();
        ms.WriteByte(ShareLinkVersion);
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(textBytes, 0, textBytes.Length);

        return Base64UrlEncode(ms.ToArray());
    }

    /// <summary>
    /// Decodes a base64url deep-link payload into the plain-text share format, then parses it.
    /// Returns a <see cref="ParseResult"/> with a single synthesized warning on malformed input
    /// rather than throwing, so deep-link handlers can surface the error to the user without
    /// crashing.
    /// </summary>
    public static ParseResult DecodeShareLink(string base64UrlPayload, IReferenceDataService refData)
    {
        if (string.IsNullOrWhiteSpace(base64UrlPayload))
            return new ParseResult([], ["Share link is empty."]);

        byte[] bytes;
        try { bytes = Base64UrlDecode(base64UrlPayload); }
        catch (FormatException)
        {
            return new ParseResult([], ["Share link is not valid base64url."]);
        }

        if (bytes.Length < 2)
            return new ParseResult([], ["Share link payload is too short."]);
        if (bytes[0] != ShareLinkVersion)
            return new ParseResult([], [$"Share link version {bytes[0]} is not supported (expected {ShareLinkVersion})."]);

        string text;
        try
        {
            using var gz = new GZipStream(new MemoryStream(bytes, 1, bytes.Length - 1), CompressionMode.Decompress);
            using var bounded = new MemoryStream();
            var buf = new byte[4096];
            int total = 0, n;
            while ((n = gz.Read(buf, 0, buf.Length)) > 0)
            {
                total += n;
                if (total > MaxDecompressedBytes)
                    return new ParseResult([], ["Share link expands past the 256 KB safety cap."]);
                bounded.Write(buf, 0, n);
            }
            text = Encoding.UTF8.GetString(bounded.ToArray());
        }
        catch (InvalidDataException)
        {
            return new ParseResult([], ["Share link is not valid gzip data."]);
        }

        return Parse(text, refData);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
            case 1: throw new FormatException("Invalid base64url length.");
        }
        return Convert.FromBase64String(padded);
    }

    [GeneratedRegex(@"^\s*(?<name>[^\d#].*?)\s*[x×X]\s*(?<qty>-?\d+)\s*$")]
    private static partial Regex LinePattern();
}

public sealed record ParseResult(IReadOnlyList<CraftListEntry> Entries, IReadOnlyList<string> Warnings);
