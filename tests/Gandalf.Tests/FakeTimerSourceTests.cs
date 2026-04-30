using FluentAssertions;
using Gandalf.Domain;
using Gandalf.ViewModels;
using Xunit;

namespace Gandalf.Tests;

/// <summary>
/// Demonstrates that <see cref="ITimerSource"/> is an open contract — a non-user
/// implementation can drive the same row-rendering pipeline (<see cref="TimerRow"/>
/// → <see cref="TimerItemViewModel"/>) the User feed uses, with no coupling to
/// <c>UserTimerSource</c> or its underlying services.
///
/// The full <see cref="TimerListViewModel"/> requires a WPF Dispatcher
/// (DispatcherTimer + CollectionViewSource); we exercise the projection path it
/// drives instead — that's where the cross-source contract actually applies.
/// </summary>
public class FakeTimerSourceTests
{
    private sealed class FakeTimerSource : ITimerSource
    {
        public string SourceId { get; }
        public IReadOnlyList<TimerCatalogEntry> Catalog { get; set; } = [];
        public IReadOnlyDictionary<string, TimerProgressEntry> Progress { get; set; } =
            new Dictionary<string, TimerProgressEntry>(StringComparer.Ordinal);

        public event EventHandler? CatalogChanged;
        public event EventHandler? ProgressChanged;
        public event EventHandler<TimerReadyEventArgs>? TimerReady;

        public FakeTimerSource(string sourceId = "tests.fake") => SourceId = sourceId;

        public void RaiseCatalogChanged() => CatalogChanged?.Invoke(this, EventArgs.Empty);
        public void RaiseProgressChanged() => ProgressChanged?.Invoke(this, EventArgs.Empty);
        public void RaiseTimerReady(TimerReadyEventArgs e) => TimerReady?.Invoke(this, e);
    }

    [Fact]
    public void Catalog_only_row_projects_to_idle_state()
    {
        var source = new FakeTimerSource
        {
            Catalog = [new TimerCatalogEntry("k1", "Chest", "Goblin Dungeon", TimeSpan.FromHours(3), SourceMetadata: null)],
        };

        var row = BuildRow(source, "k1");
        var vm = new TimerItemViewModel(row);

        vm.State.Should().Be(TimerState.Idle);
        vm.Name.Should().Be("Chest");
        vm.GroupKey.Should().Be("Goblin Dungeon");
        vm.ShowStartButton.Should().BeTrue();
        vm.ShowProgressBar.Should().BeFalse();
    }

    [Fact]
    public void Running_row_reports_remaining_and_fraction()
    {
        var source = new FakeTimerSource
        {
            Catalog = [new TimerCatalogEntry("k1", "Olugax", "Sun Vale", TimeSpan.FromHours(2), SourceMetadata: null)],
            Progress = new Dictionary<string, TimerProgressEntry>(StringComparer.Ordinal)
            {
                ["k1"] = new TimerProgressEntry("k1", DateTimeOffset.UtcNow - TimeSpan.FromHours(1), DismissedAt: null),
            },
        };

        var row = BuildRow(source, "k1");
        var vm = new TimerItemViewModel(row);

        vm.State.Should().Be(TimerState.Running);
        vm.Fraction.Should().BeApproximately(0.5, 0.01);
        vm.ShowStartButton.Should().BeFalse();
        vm.ShowProgressBar.Should().BeTrue();
    }

    [Fact]
    public void Past_due_row_reports_done()
    {
        var source = new FakeTimerSource
        {
            Catalog = [new TimerCatalogEntry("k1", "Quest", "Serbule", TimeSpan.FromMinutes(30), SourceMetadata: null)],
            Progress = new Dictionary<string, TimerProgressEntry>(StringComparer.Ordinal)
            {
                ["k1"] = new TimerProgressEntry("k1", DateTimeOffset.UtcNow - TimeSpan.FromHours(1), DismissedAt: null),
            },
        };

        var row = BuildRow(source, "k1");
        var vm = new TimerItemViewModel(row);

        vm.State.Should().Be(TimerState.Done);
        vm.Fraction.Should().Be(1.0);
        vm.ShowRestartButton.Should().BeTrue();
    }

    [Fact]
    public void Dismissed_row_reads_idle_so_it_hides_from_default_view()
    {
        var source = new FakeTimerSource
        {
            Catalog = [new TimerCatalogEntry("k1", "Chest", "Goblin Dungeon", TimeSpan.FromHours(3), SourceMetadata: null)],
            Progress = new Dictionary<string, TimerProgressEntry>(StringComparer.Ordinal)
            {
                // StartedAt set + DismissedAt set = "active row, hidden until next observation"
                ["k1"] = new TimerProgressEntry("k1",
                    DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10),
                    DismissedAt: DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5)),
            },
        };

        var row = BuildRow(source, "k1");
        var vm = new TimerItemViewModel(row);

        vm.State.Should().Be(TimerState.Idle);
    }

    [Fact]
    public void Source_metadata_is_carried_through_for_consumers_to_pattern_match()
    {
        var meta = new { Zone = "Goblin Dungeon", Internal = "GoblinStaticChest1" };
        var source = new FakeTimerSource
        {
            Catalog = [new TimerCatalogEntry("k1", "Chest", "Goblin Dungeon", TimeSpan.FromHours(3), SourceMetadata: meta)],
        };

        var row = BuildRow(source, "k1");
        row.Catalog.SourceMetadata.Should().BeSameAs(meta);
    }

    [Fact]
    public void TimerReady_event_carries_source_id_and_metadata()
    {
        var source = new FakeTimerSource(sourceId: "tests.fake");
        var captured = new List<TimerReadyEventArgs>();
        source.TimerReady += (_, e) => captured.Add(e);

        source.RaiseTimerReady(new TimerReadyEventArgs
        {
            SourceId = source.SourceId,
            Key = "k1",
            DisplayName = "Chest",
            ReadyAt = DateTimeOffset.UtcNow,
            SourceMetadata = "anything",
        });

        captured.Should().HaveCount(1);
        captured[0].SourceId.Should().Be("tests.fake");
        captured[0].Key.Should().Be("k1");
        captured[0].SourceMetadata.Should().Be("anything");
    }

    private static TimerRow BuildRow(ITimerSource source, string key)
    {
        var entry = source.Catalog.Single(c => c.Key == key);
        source.Progress.TryGetValue(key, out var p);
        return new TimerRow(entry, p);
    }
}
