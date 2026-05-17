using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Silmarillion.Tests.Views;

/// <summary>
/// Phase-6 conformance guardrail for the #404 visual grammar. Every Silmarillion
/// <c>*DetailView.xaml</c> renders entity references through the shared Phase-4
/// primitives (<c>Link</c> / <c>SetRef</c> / <c>FactTable</c> / <c>FactFooter</c>);
/// the legacy bordered-box entity chips (<c>EntityChip</c> / <c>ItemSourceChip</c>)
/// are the exact "raw bordered box for an entity reference" the #404 program
/// removed (the Fact↔Link↔Set-reference collision).
/// <para>
/// xunit never instantiates XAML and the bordered-box chips compile + pass every
/// VM test fine, so a re-introduced <c>EntityChip</c> in a detail view would ship
/// the original debt back silently. This source-scan is the cheapest layer that
/// fails the build the moment a detail view regresses to the legacy control —
/// the Phase-6 "flip the visual-debt note to resolved, and keep it resolved"
/// guarantee (see <c>docs/silmarillion-field-coverage.md</c> §"Visual grammar
/// (#404) — RESOLVED" and <c>docs/silmarillion-visual-grammar.md</c>).
/// </para>
/// </summary>
public sealed class DetailViewGrammarConformanceTests
{
    // Matches a legacy entity-reference chip element regardless of the xmlns
    // alias the view happens to use (c: / wpf: / …): "<{alias}:EntityChip" or
    // "<{alias}:ItemSourceChip". The shared grammar primitives (Link/SetRef/
    // FactTable/FactFooter) are intentionally NOT matched — they are the
    // sanctioned replacements.
    private static readonly Regex LegacyEntityChip =
        new(@"<[A-Za-z0-9_]+:(EntityChip|ItemSourceChip)\b", RegexOptions.Compiled);

    [Fact]
    public void NoSilmarillionDetailView_RendersAnEntityReferenceAsALegacyBorderedChip()
    {
        var viewsDir = FindViewsDir();
        if (viewsDir is null) return; // source tree not reachable (packaged run) — skip, don't false-fail

        var detailViews = Directory
            .EnumerateFiles(viewsDir, "*DetailView.xaml", SearchOption.AllDirectories)
            .OrderBy(p => p)
            .ToList();

        // Sanity: the scan must actually see the detail views (a glob that
        // matched nothing would make this guard vacuously pass forever).
        detailViews.Should().NotBeEmpty(
            "the Silmarillion detail views must be discoverable for the guardrail to be meaningful");

        var offenders = detailViews
            .Where(xaml => LegacyEntityChip.IsMatch(File.ReadAllText(xaml)))
            .Select(Path.GetFileName)
            .OrderBy(n => n)
            .ToList();

        offenders.Should().BeEmpty(
            "#404 is RESOLVED — every detail view must render entity references through the "
            + "shared grammar primitives (c:Link / c:SetRef / c:FactTable / c:FactFooter), "
            + "never the legacy bordered-box EntityChip/ItemSourceChip. A view in this list "
            + "has regressed to the pre-#404 debt; migrate it per "
            + "docs/silmarillion-visual-grammar.md.");
    }

    /// <summary>
    /// Walk up from the test bin dir to the repo root (marked by <c>Mithril.slnx</c>),
    /// then resolve <c>src/Silmarillion.Module/Views</c>. Returns null if not found
    /// (mirrors <see cref="TabViewCodeBehindGuardTests"/>'s convention).
    /// </summary>
    private static string? FindViewsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mithril.slnx")))
            dir = dir.Parent;

        if (dir is null) return null;
        var views = Path.Combine(dir.FullName, "src", "Silmarillion.Module", "Views");
        return Directory.Exists(views) ? views : null;
    }
}
