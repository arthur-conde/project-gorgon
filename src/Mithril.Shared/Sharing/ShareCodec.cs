using System.IO;
using System.IO.Compression;
using System.Text;

namespace Mithril.Shared.Sharing;

/// <summary>
/// Compact, version-prefixed binary envelope used by <c>mithril://*</c> deep links to
/// embed module-specific payloads in URLs. The envelope is one version byte followed by
/// gzip-compressed UTF-8 text; the whole thing is base64url-encoded so the result is
/// URL-safe without any percent-escaping.
///
/// Originally lived in Celebrimbor for craft-list sharing; lifted to Mithril.Shared so
/// other modules (Pippin progress, future…) can share one implementation.
/// </summary>
public static class ShareCodec
{
    private const byte EnvelopeVersion = 1;

    /// <summary>
    /// Hard upper bound on the decompressed payload. Pathological or malicious links
    /// won't pin the UI thread or blow the heap.
    /// </summary>
    public const int MaxDecompressedBytes = 256 * 1024;

    /// <summary>
    /// Encodes <paramref name="text"/> into the base64url envelope payload for a deep
    /// link. Callers prepend the scheme/host (e.g. <c>mithril://pippin/</c>).
    /// </summary>
    public static string EncodePayload(string text)
    {
        var textBytes = Encoding.UTF8.GetBytes(text ?? string.Empty);

        using var ms = new MemoryStream();
        ms.WriteByte(EnvelopeVersion);
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(textBytes, 0, textBytes.Length);

        return Base64UrlEncode(ms.ToArray());
    }

    /// <summary>
    /// Decodes <paramref name="base64UrlPayload"/> back to its plain-text form. Returns
    /// false (with a human-readable <paramref name="error"/>) for any malformed input —
    /// callers are deep-link handlers that mustn't throw.
    /// </summary>
    public static bool TryDecodePayload(string? base64UrlPayload, out string text, out string? error)
    {
        text = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(base64UrlPayload))
        {
            error = "Share link is empty.";
            return false;
        }

        byte[] bytes;
        try { bytes = Base64UrlDecode(base64UrlPayload); }
        catch (FormatException)
        {
            error = "Share link is not valid base64url.";
            return false;
        }

        if (bytes.Length < 2)
        {
            error = "Share link payload is too short.";
            return false;
        }
        if (bytes[0] != EnvelopeVersion)
        {
            error = $"Share link version {bytes[0]} is not supported (expected {EnvelopeVersion}).";
            return false;
        }

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
                {
                    error = "Share link expands past the 256 KB safety cap.";
                    return false;
                }
                bounded.Write(buf, 0, n);
            }
            text = Encoding.UTF8.GetString(bounded.ToArray());
            return true;
        }
        catch (InvalidDataException)
        {
            error = "Share link is not valid gzip data.";
            return false;
        }
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
}
