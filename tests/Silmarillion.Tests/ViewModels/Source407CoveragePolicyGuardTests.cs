using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Mithril.Shared.Reference;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.ViewModels;

/// <summary>
/// #407 coverage-policy regression guard (spirit-analogue of the Phase-6
/// <c>DetailViewGrammarConformanceTests</c>, but for the coverage axis). Over the
/// real bundled corpus, projects every item that has declared sources through the
/// actual <see cref="ItemsTabViewModel"/> and asserts the ratified policy holds:
/// <b>no entity appears under both "Sources" and its dedicated reverse header
/// ("Produced by" / "Awarded by") for the same item.</b> xunit never renders the
/// pane, so the duplication compiles + passes without this — it would silently rot
/// back. Declared-only residue (no reverse twin) is expected to survive and is
/// explicitly allowed.
/// </summary>
public sealed class Source407CoveragePolicyGuardTests
{
    [Fact]
    public void NoEntity_AppearsUnderBoth_Sources_AndItsReverseHeader_ForTheSameItem()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Reference", "BundledData");
        if (!File.Exists(Path.Combine(bundled, "sources_items.json"))) return;

        var refData = BuildRealRefData(bundled);
        if (refData is null) return;

        // Kinds registered so Recipe/Quest sources resolve to a real EntityRef — the
        // assertion compares references, so they must be non-null on both sides.
        var nav = new SilmarillionReferenceNavigator(new IReferenceKindTarget[]
        {
            new GuardKindTarget(EntityKind.Recipe),
            new GuardKindTarget(EntityKind.Quest),
            new GuardKindTarget(EntityKind.Npc),
        });
        var vm = new ItemsTabViewModel(refData, nav, new ReferenceDataEntityNameResolver(refData));

        // Precise corpus: only items that actually declare sources can exhibit the
        // class. ~6155 items in v470 — bounded and fast (one index build, O(sources)
        // per select).
        var itemsWithSources = refData.ItemSources.Keys
            .Select(n => refData.ItemsByInternalName.TryGetValue(n, out var it) ? it : null)
            .Where(it => it is not null)
            .ToList();

        itemsWithSources.Should().NotBeEmpty("bundled sources_items.json ships ~6155 sourced items");

        var violations = new List<string>();

        foreach (var item in itemsWithSources)
        {
            vm.SelectedItem = null;
            vm.SelectedItem = item;
            var d = vm.DetailViewModel;
            if (d is null) continue;

            var reverseRefs = new HashSet<EntityRef>(
                d.ProducedByRecipes.Select(c => c.Reference)
                    .Concat(d.AwardedByQuests.Select(c => c.Reference)));

            foreach (var src in d.Sources)
            {
                if (src.EntityReference is not { } r) continue;
                if (r.Kind is not (EntityKind.Recipe or EntityKind.Quest)) continue;
                if (reverseRefs.Contains(r))
                    violations.Add($"{item!.InternalName}: {r.Kind} '{r.InternalName}' is under BOTH Sources and its reverse header");
            }
        }

        violations.Should().BeEmpty(
            "the #407 ratified policy (docs/silmarillion-field-coverage.md §#407) suppresses a "
            + "declared Recipe/Quest Sources row per (item,entity) edge when the same entity is "
            + "already under its reverse header; declared-only residue survives but must not also "
            + "appear under a reverse header. First few: "
            + string.Join(" | ", violations.Take(5)));
    }

    private static IReferenceDataService? BuildRealRefData(string bundled)
    {
        try
        {
            var cacheDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(cacheDir);
            using var http = new HttpClient(new ThrowingHttpHandler());
            return new ReferenceDataService(cacheDir, http, bundledDir: bundled);
        }
        catch
        {
            return null;
        }
    }

    private sealed class ThrowingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP must not be called in this test");
    }

    private sealed class GuardKindTarget : IReferenceKindTarget
    {
        public GuardKindTarget(EntityKind kind) => Kind = kind;
        public EntityKind Kind { get; }
        public int TabIndex => 0;
        public bool TrySelectByInternalName(string internalName) => true;
        public bool TryOpenInWindow() => false;
    }
}
