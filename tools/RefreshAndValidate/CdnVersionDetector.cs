using System.Net.Http;
using System.Text.RegularExpressions;

namespace Mithril.Tools.RefreshAndValidate;

/// <summary>
/// CDN root returns an HTML meta-refresh page rather than an HTTP redirect, e.g.
/// <c>&lt;meta http-equiv="refresh" content="2; URL=http://cdn.projectgorgon.com/v469/data/index.html"&gt;</c>.
/// We GET the body and regex out the version segment.
/// <para>
/// IMPORTANT: this is duplicated from <c>Mithril.Shared.Reference.CdnVersionDetector</c>
/// because that project targets <c>net10.0-windows</c> (UseWPF=true) and this tool
/// is cross-platform. <see cref="FallbackVersion"/> mirrors
/// <c>Mithril.Shared.Reference.ReferenceDataService.FallbackCdnVersion</c> for the
/// same reason. When bumping the fallback version, update both places.
/// </para>
/// </summary>
internal static partial class CdnVersionDetector
{
    public const string FallbackVersion = "v469";

    [GeneratedRegex(@"/(v\d+)/", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRx();

    public static async Task<string?> TryDetectAsync(HttpClient http, string root, CancellationToken ct = default)
    {
        try
        {
            using var resp = await http.GetAsync(root, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(ct);
            var m = VersionRx().Match(body);
            return m.Success ? m.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }
}
