using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Gandalf.Domain;
using Gandalf.Services;
using Gandalf.ViewModels;
using Xunit;

namespace Gandalf.Tests;

public class TimerSourceBinderTests
{
    private static readonly DateTimeOffset Anchor =
        new(2026, 5, 9, 14, 30, 0, TimeSpan.Zero);

    private static TimerCatalogEntry Entry(string key, string region = "R", TimeSpan? duration = null) =>
        new(key, $"Display {key}", region, duration ?? TimeSpan.FromHours(1), SourceMetadata: null);

    private static TimerProgressEntry Started(string key, DateTimeOffset at) =>
        new(key, at, DismissedAt: null);

    [Fact]
    public void Initial_sync_materialises_all_catalog_rows_when_no_relevance_filter()
    {
        var source = new RecordingFakeSource("test")
        {
            Catalog = [Entry("a"), Entry("b"), Entry("c")],
        };
        var target = new ObservableCollection<TimerItemViewModel>();

        using var binder = new TimerSourceBinder(source, target, new ManualTime(Anchor));

        target.Should().HaveCount(3);
        binder.ByKey.Keys.Should().BeEquivalentTo(["a", "b", "c"]);
    }

    [Fact]
    public void Initial_sync_filters_out_irrelevant_rows()
    {
        var source = new RecordingFakeSource("test")
        {
            Catalog = [Entry("relevant"), Entry("filtered")],
        };
        var target = new ObservableCollection<TimerItemViewModel>();

        using var binder = new TimerSourceBinder(
            source, target, new ManualTime(Anchor),
            isRelevant: (entry, _) => entry.Key == "relevant");

        target.Should().ContainSingle();
        binder.ByKey.Keys.Should().BeEquivalentTo(["relevant"]);
    }

    [Fact]
    public void Added_delta_materialises_the_row()
    {
        var source = new RecordingFakeSource("test");
        var target = new ObservableCollection<TimerItemViewModel>();
        using var binder = new TimerSourceBinder(source, target, new ManualTime(Anchor));

        var entry = Entry("k");
        source.Catalog = [entry];
        source.RaiseRowsChanged([
            new TimerRowDelta("k", TimerRowChangeKind.Added, entry, Progress: null),
        ]);

        target.Should().ContainSingle();
        target[0].Key.Should().Be("k");
    }

    [Fact]
    public void Added_delta_filtered_out_does_not_materialise()
    {
        var source = new RecordingFakeSource("test");
        var target = new ObservableCollection<TimerItemViewModel>();
        using var binder = new TimerSourceBinder(
            source, target, new ManualTime(Anchor),
            isRelevant: (e, _) => e.Region == "Visible");

        source.RaiseRowsChanged([
            new TimerRowDelta("k", TimerRowChangeKind.Added, Entry("k", region: "Hidden"), null),
        ]);

        target.Should().BeEmpty();
    }

    [Fact]
    public void ProgressChanged_delta_updates_existing_row_in_place()
    {
        var entry = Entry("k", duration: TimeSpan.FromHours(2));
        var source = new RecordingFakeSource("test") { Catalog = [entry] };
        var target = new ObservableCollection<TimerItemViewModel>();
        using var binder = new TimerSourceBinder(source, target, new ManualTime(Anchor));

        var vmBefore = target[0];
        vmBefore.State.Should().Be(TimerState.Idle);

        source.RaiseRowsChanged([
            new TimerRowDelta("k", TimerRowChangeKind.ProgressChanged, entry, Started("k", Anchor)),
        ]);

        target.Should().ContainSingle();
        target[0].Should().BeSameAs(vmBefore, "ProgressChanged updates the existing VM in place — no churn");
        target[0].State.Should().Be(TimerState.Running);
    }

    [Fact]
    public void Removed_delta_drops_the_row()
    {
        var entry = Entry("k");
        var source = new RecordingFakeSource("test") { Catalog = [entry] };
        var target = new ObservableCollection<TimerItemViewModel>();
        using var binder = new TimerSourceBinder(source, target, new ManualTime(Anchor));

        source.RaiseRowsChanged([
            new TimerRowDelta("k", TimerRowChangeKind.Removed, Catalog: null, Progress: null),
        ]);

        target.Should().BeEmpty();
        binder.ByKey.Should().BeEmpty();
    }

    [Fact]
    public void ProgressChanged_can_drop_a_row_when_relevance_flips_off()
    {
        // QuestSource pattern: a quest with progress is relevant; once
        // dismissed and removed from journal, it becomes irrelevant and
        // the binder must drop it from the collection.
        var entry = Entry("k");
        var source = new RecordingFakeSource("test") { Catalog = [entry] };
        var target = new ObservableCollection<TimerItemViewModel>();
        var inJournal = true;
        using var binder = new TimerSourceBinder(
            source, target, new ManualTime(Anchor),
            isRelevant: (_, p) => p is { DismissedAt: null } || inJournal);

        target.Should().HaveCount(1);

        // Mark dismissed AND drop from journal.
        inJournal = false;
        var dismissedProgress = new TimerProgressEntry("k", Anchor, DismissedAt: Anchor + TimeSpan.FromMinutes(10));
        source.RaiseRowsChanged([
            new TimerRowDelta("k", TimerRowChangeKind.ProgressChanged, entry, dismissedProgress),
        ]);

        target.Should().BeEmpty();
    }

    [Fact]
    public void ProgressChanged_can_add_a_row_when_relevance_flips_on()
    {
        var entry = Entry("k");
        var source = new RecordingFakeSource("test") { Catalog = [entry] };
        var target = new ObservableCollection<TimerItemViewModel>();
        using var binder = new TimerSourceBinder(
            source, target, new ManualTime(Anchor),
            isRelevant: (_, p) => p is not null);  // only relevant once started

        target.Should().BeEmpty("not relevant until progress exists");

        source.RaiseRowsChanged([
            new TimerRowDelta("k", TimerRowChangeKind.ProgressChanged, entry, Started("k", Anchor)),
        ]);

        target.Should().ContainSingle();
        target[0].Key.Should().Be("k");
    }

    [Fact]
    public void RefreshRequired_fires_when_State_changes_on_existing_row()
    {
        var entry = Entry("k", duration: TimeSpan.FromMinutes(30));
        var source = new RecordingFakeSource("test") { Catalog = [entry] };
        var target = new ObservableCollection<TimerItemViewModel>();
        using var binder = new TimerSourceBinder(source, target, new ManualTime(Anchor));

        var refreshes = 0;
        binder.RefreshRequired += (_, _) => refreshes++;

        // Idle → Running (progress arrives).
        source.RaiseRowsChanged([
            new TimerRowDelta("k", TimerRowChangeKind.ProgressChanged, entry, Started("k", Anchor)),
        ]);

        refreshes.Should().Be(1);
    }

    [Fact]
    public void RefreshRequired_fires_when_GroupKey_changes_via_CatalogChanged()
    {
        // Calibration-overlay scenario: a defeat row's Region flips from
        // "Defeats" to "Tagamogi". The binder must signal Refresh because
        // the CollectionView groups by GroupKey (= Region).
        var oldEntry = Entry("k", region: "Defeats");
        var source = new RecordingFakeSource("test") { Catalog = [oldEntry] };
        var target = new ObservableCollection<TimerItemViewModel>();
        using var binder = new TimerSourceBinder(source, target, new ManualTime(Anchor));

        var refreshes = 0;
        binder.RefreshRequired += (_, _) => refreshes++;

        var newEntry = Entry("k", region: "Tagamogi");
        source.Catalog = [newEntry];
        source.RaiseRowsChanged([
            new TimerRowDelta("k", TimerRowChangeKind.CatalogChanged, newEntry, Progress: null),
        ]);

        refreshes.Should().Be(1);
        target[0].GroupKey.Should().Be("Tagamogi");
    }

    [Fact]
    public void RefreshRequired_fires_at_most_once_per_batched_RowsChanged()
    {
        // Calibration overlay applied: ~hundreds of rows mutate at once,
        // arriving as a single batch. The binder must call host's Refresh
        // exactly once for the entire batch, not N times.
        var entries = Enumerable.Range(0, 50)
            .Select(i => Entry($"k{i}", region: "Old"))
            .ToArray();
        var source = new RecordingFakeSource("test") { Catalog = entries };
        var target = new ObservableCollection<TimerItemViewModel>();
        using var binder = new TimerSourceBinder(source, target, new ManualTime(Anchor));

        var refreshes = 0;
        binder.RefreshRequired += (_, _) => refreshes++;

        var newEntries = entries
            .Select(e => Entry(e.Key, region: "New"))
            .ToArray();
        source.Catalog = newEntries;
        var deltas = newEntries
            .Select(e => new TimerRowDelta(e.Key, TimerRowChangeKind.CatalogChanged, e, Progress: null))
            .ToList();
        source.RaiseRowsChanged(deltas);

        refreshes.Should().Be(1, "one batch of N deltas must call RefreshRequired exactly once");
    }

    [Fact]
    public void Removed_delta_for_unknown_key_is_a_noop()
    {
        var source = new RecordingFakeSource("test");
        var target = new ObservableCollection<TimerItemViewModel>();
        using var binder = new TimerSourceBinder(source, target, new ManualTime(Anchor));

        var refreshes = 0;
        binder.RefreshRequired += (_, _) => refreshes++;

        source.RaiseRowsChanged([
            new TimerRowDelta("ghost", TimerRowChangeKind.Removed, null, null),
        ]);

        target.Should().BeEmpty();
        refreshes.Should().Be(0);
    }

    [Fact]
    public void RecheckRelevance_adds_rows_that_became_relevant()
    {
        // Models the QuestSource pending-set pattern: a quest is in catalog
        // but not yet rendered because it's not in the journal. Player
        // accepts the quest (pending now contains it) → host VM calls
        // RecheckRelevance() → binder iterates catalog, finds the row is
        // newly relevant, materialises it.
        var entries = new[] { Entry("a"), Entry("b") };
        var source = new RecordingFakeSource("test") { Catalog = entries };
        var target = new ObservableCollection<TimerItemViewModel>();
        var pending = new HashSet<string>(StringComparer.Ordinal);

        using var binder = new TimerSourceBinder(
            source, target, new ManualTime(Anchor),
            isRelevant: (e, _) => pending.Contains(e.Key));

        target.Should().BeEmpty();

        pending.Add("a");
        binder.RecheckRelevance();

        target.Should().ContainSingle();
        target[0].Key.Should().Be("a");
    }

    [Fact]
    public void RecheckRelevance_removes_rows_that_became_irrelevant()
    {
        var entries = new[] { Entry("a"), Entry("b") };
        var source = new RecordingFakeSource("test") { Catalog = entries };
        var target = new ObservableCollection<TimerItemViewModel>();
        var pending = new HashSet<string>(StringComparer.Ordinal) { "a", "b" };

        using var binder = new TimerSourceBinder(
            source, target, new ManualTime(Anchor),
            isRelevant: (e, _) => pending.Contains(e.Key));

        target.Should().HaveCount(2);

        pending.Remove("a");
        binder.RecheckRelevance();

        target.Should().ContainSingle();
        target[0].Key.Should().Be("b");
    }

    [Fact]
    public void Disposed_binder_stops_applying_deltas()
    {
        var entry = Entry("k");
        var source = new RecordingFakeSource("test") { Catalog = [entry] };
        var target = new ObservableCollection<TimerItemViewModel>();
        var binder = new TimerSourceBinder(source, target, new ManualTime(Anchor));

        binder.Dispose();

        source.RaiseRowsChanged([
            new TimerRowDelta("k", TimerRowChangeKind.Removed, null, null),
        ]);

        target.Should().HaveCount(1, "events arriving after Dispose must not mutate the target");
    }

    private sealed class RecordingFakeSource : ITimerSource
    {
        public string SourceId { get; }
        public IReadOnlyList<TimerCatalogEntry> Catalog { get; set; } = [];
        public IReadOnlyDictionary<string, TimerProgressEntry> Progress { get; set; } =
            new Dictionary<string, TimerProgressEntry>(StringComparer.Ordinal);

        public bool TryGetProgress(string key, [NotNullWhen(true)] out TimerProgressEntry? progress)
        {
            if (Progress.TryGetValue(key, out var p)) { progress = p; return true; }
            progress = null;
            return false;
        }

        public event EventHandler? CatalogChanged;
        public event EventHandler? ProgressChanged;
        public event EventHandler<TimerReadyEventArgs>? TimerReady;
        public event EventHandler<TimerRowsChangedEventArgs>? RowsChanged;

        public RecordingFakeSource(string sourceId) => SourceId = sourceId;

        public void RaiseCatalogChanged() => CatalogChanged?.Invoke(this, EventArgs.Empty);
        public void RaiseProgressChanged() => ProgressChanged?.Invoke(this, EventArgs.Empty);
        public void RaiseTimerReady(TimerReadyEventArgs e) => TimerReady?.Invoke(this, e);
        public void RaiseRowsChanged(IReadOnlyList<TimerRowDelta> deltas) =>
            RowsChanged?.Invoke(this, new TimerRowsChangedEventArgs { Deltas = deltas });
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualTime(DateTimeOffset utcStart) => _now = utcStart;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
