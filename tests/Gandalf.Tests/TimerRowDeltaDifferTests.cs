using FluentAssertions;
using Gandalf.Domain;
using Xunit;

namespace Gandalf.Tests;

public class TimerRowDeltaDifferTests
{
    private static TimerCatalogEntry Entry(string key, TimeSpan duration, string? region = "R") =>
        new(key, $"Display {key}", region, duration, SourceMetadata: null);

    private static TimerProgressEntry Progress(string key, DateTimeOffset startedAt) =>
        new(key, startedAt, DismissedAt: null);

    private static IReadOnlyDictionary<string, TimerCatalogEntry> CatalogMap(params TimerCatalogEntry[] entries) =>
        entries.ToDictionary(e => e.Key, StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, TimerProgressEntry> ProgressMap(params TimerProgressEntry[] entries) =>
        entries.ToDictionary(e => e.Key, StringComparer.Ordinal);

    private static readonly DateTimeOffset Anchor =
        new(2026, 5, 9, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Empty_to_empty_produces_no_deltas()
    {
        var deltas = TimerRowDeltaDiffer.Diff(CatalogMap(), CatalogMap(), ProgressMap(), ProgressMap());
        deltas.Should().BeEmpty();
    }

    [Fact]
    public void New_catalog_entry_emits_added_delta_with_progress_attached()
    {
        var newCat = CatalogMap(Entry("k", TimeSpan.FromHours(1)));
        var newProg = ProgressMap(Progress("k", Anchor));

        var deltas = TimerRowDeltaDiffer.Diff(CatalogMap(), newCat, ProgressMap(), newProg);

        deltas.Should().HaveCount(1);
        deltas[0].Key.Should().Be("k");
        deltas[0].Kind.Should().Be(TimerRowChangeKind.Added);
        deltas[0].Catalog.Should().Be(newCat["k"]);
        deltas[0].Progress.Should().Be(newProg["k"]);
    }

    [Fact]
    public void Removed_catalog_entry_emits_removed_delta_with_null_payload()
    {
        var oldCat = CatalogMap(Entry("k", TimeSpan.FromHours(1)));
        var oldProg = ProgressMap(Progress("k", Anchor));

        var deltas = TimerRowDeltaDiffer.Diff(oldCat, CatalogMap(), oldProg, ProgressMap());

        deltas.Should().HaveCount(1);
        deltas[0].Key.Should().Be("k");
        deltas[0].Kind.Should().Be(TimerRowChangeKind.Removed);
        deltas[0].Catalog.Should().BeNull();
        deltas[0].Progress.Should().BeNull();
    }

    [Fact]
    public void Catalog_duration_change_emits_catalog_changed_delta()
    {
        var oldCat = CatalogMap(Entry("k", TimeSpan.FromHours(1)));
        var newCat = CatalogMap(Entry("k", TimeSpan.FromHours(3)));

        var deltas = TimerRowDeltaDiffer.Diff(oldCat, newCat, ProgressMap(), ProgressMap());

        deltas.Should().ContainSingle();
        deltas[0].Kind.Should().Be(TimerRowChangeKind.CatalogChanged);
        deltas[0].Catalog!.Duration.Should().Be(TimeSpan.FromHours(3));
    }

    [Fact]
    public void Progress_change_alone_emits_progress_changed_delta()
    {
        var cat = CatalogMap(Entry("k", TimeSpan.FromHours(1)));
        var oldProg = ProgressMap();
        var newProg = ProgressMap(Progress("k", Anchor));

        var deltas = TimerRowDeltaDiffer.Diff(cat, cat, oldProg, newProg);

        deltas.Should().ContainSingle();
        deltas[0].Kind.Should().Be(TimerRowChangeKind.ProgressChanged);
        deltas[0].Progress!.StartedAt.Should().Be(Anchor);
    }

    [Fact]
    public void Catalog_change_subsumes_progress_change_into_one_delta()
    {
        // When both catalog and progress shifted in the same diff window, emit
        // one CatalogChanged delta carrying both pieces — receivers see the
        // latest state regardless of which kind they branch on. Avoids
        // duplicate deltas for the same key in one batch.
        var oldCat = CatalogMap(Entry("k", TimeSpan.FromHours(1)));
        var newCat = CatalogMap(Entry("k", TimeSpan.FromHours(3)));
        var oldProg = ProgressMap();
        var newProg = ProgressMap(Progress("k", Anchor));

        var deltas = TimerRowDeltaDiffer.Diff(oldCat, newCat, oldProg, newProg);

        deltas.Should().ContainSingle();
        deltas[0].Kind.Should().Be(TimerRowChangeKind.CatalogChanged);
        deltas[0].Catalog!.Duration.Should().Be(TimeSpan.FromHours(3));
        deltas[0].Progress!.StartedAt.Should().Be(Anchor);
    }

    [Fact]
    public void Mixed_batch_yields_multiple_deltas_in_iteration_order()
    {
        // Calibration-overlay-style scenario: many rows mutate at once.
        var oldCat = CatalogMap(
            Entry("a", TimeSpan.FromHours(1)),
            Entry("b", TimeSpan.FromHours(2)));
        var newCat = CatalogMap(
            Entry("b", TimeSpan.FromHours(2)),                    // unchanged
            Entry("c", TimeSpan.FromHours(3)));                   // added
        // 'a' removed, 'c' added.

        var deltas = TimerRowDeltaDiffer.Diff(oldCat, newCat, ProgressMap(), ProgressMap());

        deltas.Should().HaveCount(2);
        deltas.Should().Contain(d => d.Key == "c" && d.Kind == TimerRowChangeKind.Added);
        deltas.Should().Contain(d => d.Key == "a" && d.Kind == TimerRowChangeKind.Removed);
    }
}
