using System.IO;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Mithril.Shared.Tests.Architecture;

/// <summary>
/// Guards the load-bearing map-calibration invariant (issue #921): no project
/// under <c>src/**</c> — the shipped product graph — may take a dependency on a
/// Unity-asset reader (<c>AssetsTools.NET</c>) or an image decoder
/// (<c>System.Drawing.Common</c>), directly or via the dev-only
/// <c>Mithril.MapCalibration.Tools.Common</c> tool library.
///
/// The detect→solve engine the product needs lives decoder-free in
/// <c>src/Mithril.MapCalibration</c> (it loads pre-baked icon templates BCL-only);
/// the heavy decoders are confined to <c>tools/</c> and the
/// <c>Mithril.MapCalibration.Tools.slnx</c> solution. This test fails red if any
/// shipped project re-introduces a decoder, so the boundary can't silently rot.
/// </summary>
public class ShippedGraphDecoderFreeTests
{
    // Substring match against PackageReference Include / ProjectReference Include.
    private static readonly string[] ForbiddenPackages =
    [
        "AssetsTools.NET",          // also covers AssetsTools.NET.Texture
        "System.Drawing.Common",
    ];

    private static readonly string[] ForbiddenProjects =
    [
        "Mithril.MapCalibration.Tools.Common",
    ];

    [Fact]
    public void No_src_project_references_a_decoder_or_the_tools_common_lib()
    {
        var srcRoot = Path.Combine(RepoRoot(), "src");
        var projects = Directory.GetFiles(srcRoot, "*.csproj", SearchOption.AllDirectories);

        projects.Should().NotBeEmpty("the src/ tree must contain shipped projects to guard");

        var violations = new List<string>();

        foreach (var csproj in projects)
        {
            var doc = XDocument.Load(csproj);
            var rel = Path.GetRelativePath(RepoRoot(), csproj).Replace('\\', '/');

            foreach (var include in Includes(doc, "PackageReference"))
            {
                foreach (var bad in ForbiddenPackages)
                {
                    if (include.Contains(bad, StringComparison.OrdinalIgnoreCase))
                        violations.Add($"{rel}: PackageReference '{include}' (matches forbidden '{bad}')");
                }
            }

            foreach (var include in Includes(doc, "ProjectReference"))
            {
                foreach (var bad in ForbiddenProjects)
                {
                    if (include.Contains(bad, StringComparison.OrdinalIgnoreCase))
                        violations.Add($"{rel}: ProjectReference '{include}' (matches forbidden '{bad}')");
                }
            }
        }

        violations.Should().BeEmpty(
            "the shipped product graph (src/**) must stay decoder-free (issue #921). " +
            "AssetsTools.NET / System.Drawing belong only to tools/ and " +
            "Mithril.MapCalibration.Tools.slnx, never the shipped app:\n" +
            string.Join("\n", violations));
    }

    // Element names are unqualified in SDK-style csproj (no default xmlns), so a
    // plain local-name match is sufficient and avoids namespace plumbing.
    private static IEnumerable<string> Includes(XDocument doc, string element) =>
        doc.Descendants()
            .Where(e => e.Name.LocalName == element)
            .Select(e => (string?)e.Attribute("Include"))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!);

    // Walk up from the test's bin dir to the folder that owns Mithril.slnx.
    // Deliberately inlined rather than reusing Tools.Common.RepoPaths — taking a
    // ProjectReference on Tools.Common here would re-create the very dependency
    // this test exists to forbid.
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12; i++)
        {
            if (File.Exists(Path.Combine(dir, "Mithril.slnx"))) return dir;
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        throw new InvalidOperationException(
            $"could not locate Mithril.slnx walking up from {AppContext.BaseDirectory}");
    }
}
