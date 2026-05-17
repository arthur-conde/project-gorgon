using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Silmarillion.Tests.Views;

/// <summary>
/// Phase-6 conformance guardrail for the #404 visual grammar. Every migrated
/// detail view renders entity references through the shared Phase-4 primitives
/// (<c>Link</c> / <c>SetRef</c> / <c>FactTable</c> / <c>FactFooter</c>); the
/// legacy bordered-box entity chips (<c>EntityChip</c> / <c>ItemSourceChip</c>)
/// are the exact "raw bordered box for an entity reference" the #404 program
/// removed (the Fact↔Link↔Set-reference collision).
/// <para>
/// xunit never instantiates XAML and the bordered-box chips compile + pass every
/// VM test fine, so a re-introduced <c>EntityChip</c> in a detail view would ship
/// the original debt back silently. This source-scan is the cheapest layer that
/// fails the build the moment a detail view regresses to the legacy control —
/// the Phase-6 "flip the visual-debt note to resolved, and keep it resolved"
/// guarantee (see <c>docs/silmarillion-field-coverage.md</c> §"Visual grammar
/// (#404)" and <c>docs/silmarillion-visual-grammar.md</c>).
/// </para>
/// <para>
/// <b>Scope:</b> the nine Silmarillion <em>tab</em> detail panes
/// (<c>src/Silmarillion.Module/Views/*DetailView.xaml</c>) <b>and</b> the shared
/// cross-module <c>src/Mithril.Shared.Wpf/ItemDetailView.xaml</c>. The shared
/// item-detail pane was deliberately out of #404 Phase-5 scope (Phase-5
/// anti-goal #3 forbade editing shared <c>Mithril.Shared.Wpf</c> primitives) and
/// was migrated by its own gated follow-up (#424); it is now covered here so it
/// cannot silently regress to the pre-#404 boxed-chip grammar either.
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
    public void NoMigratedDetailView_RendersAnEntityReferenceAsALegacyBorderedChip()
    {
        var repoRoot = FindRepoRoot();
        if (repoRoot is null) return; // source tree not reachable (packaged run) — skip, don't false-fail

        var detailViews = EnumerateGuardedViews(repoRoot)
            .OrderBy(p => p)
            .ToList();

        // Sanity: the scan must actually see the detail views (a glob that
        // matched nothing would make this guard vacuously pass forever).
        detailViews.Should().NotBeEmpty(
            "the migrated detail views must be discoverable for the guardrail to be meaningful");

        // Sanity: the shared item-detail pane (#424) is explicitly part of the
        // guarded set — if the path moves, fail loudly rather than silently
        // dropping coverage of the highest-blast-radius pane.
        detailViews.Select(Path.GetFileName)
            .Should().Contain("ItemDetailView.xaml",
                "#424 brought the shared Mithril.Shared.Wpf/ItemDetailView into the "
                + "Phase-6 guardrail scope; losing it would let the cross-module pane "
                + "silently regress.");

        var offenders = detailViews
            .Where(xaml => LegacyEntityChip.IsMatch(File.ReadAllText(xaml)))
            .Select(Path.GetFileName)
            .OrderBy(n => n)
            .ToList();

        offenders.Should().BeEmpty(
            "#404 is RESOLVED — every migrated detail view (the nine Silmarillion tab "
            + "panes + the shared ItemDetailView, #424) must render entity references "
            + "through the shared grammar primitives (c:Link / c:SetRef / c:FactTable / "
            + "c:FactFooter), never the legacy bordered-box EntityChip/ItemSourceChip. A "
            + "view in this list has regressed to the pre-#404 debt; migrate it per "
            + "docs/silmarillion-visual-grammar.md.");
    }

    /// <summary>
    /// The full guarded set: every <c>*DetailView.xaml</c> under
    /// <c>src/Silmarillion.Module/Views</c> plus the shared cross-module
    /// <c>src/Mithril.Shared.Wpf/ItemDetailView.xaml</c> (#424). Missing
    /// directories/files are skipped silently (a packaged run); the
    /// not-empty + must-contain assertions catch a genuine scope loss.
    /// </summary>
    private static IEnumerable<string> EnumerateGuardedViews(string repoRoot)
    {
        var silmarillionViews = Path.Combine(repoRoot, "src", "Silmarillion.Module", "Views");
        if (Directory.Exists(silmarillionViews))
        {
            foreach (var v in Directory.EnumerateFiles(
                         silmarillionViews, "*DetailView.xaml", SearchOption.AllDirectories))
                yield return v;
        }

        var sharedItemDetail = Path.Combine(
            repoRoot, "src", "Mithril.Shared.Wpf", "ItemDetailView.xaml");
        if (File.Exists(sharedItemDetail))
            yield return sharedItemDetail;
    }

    /// <summary>
    /// Walk up from the test bin dir to the repo root (marked by
    /// <c>Mithril.slnx</c>). Returns null if not found (mirrors
    /// <see cref="TabViewCodeBehindGuardTests"/>'s convention).
    /// </summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mithril.slnx")))
            dir = dir.Parent;

        return dir?.FullName;
    }
}
