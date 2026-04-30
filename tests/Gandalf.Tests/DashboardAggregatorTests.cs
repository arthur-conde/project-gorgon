using FluentAssertions;
using Gandalf.Domain;
using Gandalf.Services;
using Xunit;

namespace Gandalf.Tests;

public sealed class DashboardAggregatorTests
{
    private sealed class FakeSource : ITimerSource
    {
        public string SourceId { get; }
        public IReadOnlyList<TimerCatalogEntry> Catalog { get; set; } = [];
        public IReadOnlyDictionary<string, TimerProgressEntry> Progress { get; set; } =
            new Dictionary<string, TimerProgressEntry>(StringComparer.Ordinal);

        public event EventHandler? CatalogChanged;
        public event EventHandler? ProgressChanged;
        public event EventHandler<TimerReadyEventArgs>? TimerReady;

        public FakeSource(string sourceId) => SourceId = sourceId;

        public void RaiseProgressChanged() => ProgressChanged?.Invoke(this, EventArgs.Empty);
        public void RaiseCatalogChanged() => CatalogChanged?.Invoke(this, EventArgs.Empty);
        public void RaiseTimerReady() =>
            TimerReady?.Invoke(this, new TimerReadyEventArgs
            {
                SourceId = SourceId, Key = "_", DisplayName = "_", ReadyAt = DateTimeOffset.UtcNow,
            });
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualTime(DateTime utcStart) => _now = new DateTimeOffset(utcStart, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private static readonly DateTime Origin = new(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Idle_rows_project_with_state_idle_and_null_expiresAt()
    {
        var src = new FakeSource("gandalf.test")
        {
            Catalog = [new TimerCatalogEntry("k1", "Daily", "Serbule", TimeSpan.FromHours(1), null)],
        };
        var time = new ManualTime(Origin);

        using var agg = new DashboardAggregator([src], time);
        var summary = agg.Summaries.Single();
        summary.State.Should().Be(TimerState.Idle);
        summary.ExpiresAt.Should().BeNull();
    }

    [Fact]
    public void Cooling_rows_carry_expiresAt_at_StartedAt_plus_Duration()
    {
        var time = new ManualTime(Origin);
        var startedAt = time.GetUtcNow() - TimeSpan.FromMinutes(30);
        var src = new FakeSource("gandalf.test")
        {
            Catalog = [new TimerCatalogEntry("k1", "Daily", "Serbule", TimeSpan.FromHours(1), null)],
            Progress = new Dictionary<string, TimerProgressEntry>(StringComparer.Ordinal)
            {
                ["k1"] = new TimerProgressEntry("k1", startedAt, DismissedAt: null),
            },
        };

        using var agg = new DashboardAggregator([src], time);
        var summary = agg.Summaries.Single();
        summary.State.Should().Be(TimerState.Running);
        summary.ExpiresAt.Should().Be(startedAt + TimeSpan.FromHours(1));
    }

    [Fact]
    public void Past_due_rows_project_as_Done()
    {
        var time = new ManualTime(Origin);
        var startedAt = time.GetUtcNow() - TimeSpan.FromHours(2);
        var src = new FakeSource("gandalf.test")
        {
            Catalog = [new TimerCatalogEntry("k1", "Daily", "Serbule", TimeSpan.FromHours(1), null)],
            Progress = new Dictionary<string, TimerProgressEntry>(StringComparer.Ordinal)
            {
                ["k1"] = new TimerProgressEntry("k1", startedAt, DismissedAt: null),
            },
        };

        using var agg = new DashboardAggregator([src], time);
        var summary = agg.Summaries.Single();
        summary.State.Should().Be(TimerState.Done);
    }

    [Fact]
    public void Dismissed_rows_project_as_Idle_with_null_expiresAt()
    {
        var time = new ManualTime(Origin);
        var src = new FakeSource("gandalf.test")
        {
            Catalog = [new TimerCatalogEntry("k1", "Daily", "Serbule", TimeSpan.FromHours(1), null)],
            Progress = new Dictionary<string, TimerProgressEntry>(StringComparer.Ordinal)
            {
                ["k1"] = new TimerProgressEntry("k1",
                    StartedAt: time.GetUtcNow() - TimeSpan.FromHours(2),
                    DismissedAt: time.GetUtcNow() - TimeSpan.FromMinutes(5)),
            },
        };

        using var agg = new DashboardAggregator([src], time);
        var summary = agg.Summaries.Single();
        summary.State.Should().Be(TimerState.Idle);
        summary.ExpiresAt.Should().BeNull();
    }

    [Fact]
    public void Aggregates_across_multiple_sources_with_source_id_preserved()
    {
        var time = new ManualTime(Origin);
        var a = new FakeSource("gandalf.user")
        {
            Catalog = [new TimerCatalogEntry("u1", "User Timer", null, TimeSpan.FromMinutes(5), null)],
        };
        var b = new FakeSource("gandalf.quest")
        {
            Catalog = [new TimerCatalogEntry("q1", "Quest 1", "Serbule", TimeSpan.FromHours(20), null)],
        };

        using var agg = new DashboardAggregator([a, b], time);

        agg.Summaries.Should().HaveCount(2);
        agg.Summaries.Should().Contain(s => s.SourceId == "gandalf.user" && s.Key == "u1");
        agg.Summaries.Should().Contain(s => s.SourceId == "gandalf.quest" && s.Key == "q1");
    }

    [Fact]
    public void Updated_event_fires_when_a_source_raises_ProgressChanged()
    {
        var src = new FakeSource("gandalf.test");
        var time = new ManualTime(Origin);

        using var agg = new DashboardAggregator([src], time);
        var fired = 0;
        agg.Updated += (_, _) => fired++;

        src.RaiseProgressChanged();
        src.RaiseProgressChanged();

        fired.Should().Be(2);
    }

    [Fact]
    public void Updated_event_fires_when_a_source_raises_CatalogChanged()
    {
        var src = new FakeSource("gandalf.test");
        var time = new ManualTime(Origin);

        using var agg = new DashboardAggregator([src], time);
        var fired = 0;
        agg.Updated += (_, _) => fired++;

        src.RaiseCatalogChanged();

        fired.Should().Be(1);
    }

    [Fact]
    public void Recompute_picks_up_time_progression_without_a_source_event()
    {
        var time = new ManualTime(Origin);
        var startedAt = time.GetUtcNow();
        var src = new FakeSource("gandalf.test")
        {
            Catalog = [new TimerCatalogEntry("k1", "Daily", "Serbule", TimeSpan.FromMinutes(10), null)],
            Progress = new Dictionary<string, TimerProgressEntry>(StringComparer.Ordinal)
            {
                ["k1"] = new TimerProgressEntry("k1", startedAt, DismissedAt: null),
            },
        };

        using var agg = new DashboardAggregator([src], time);
        agg.Summaries.Single().State.Should().Be(TimerState.Running);

        // Time advances past the cooldown — no source event, but Recompute()
        // (called from the dashboard's 1Hz tick) reflects the transition.
        time.Advance(TimeSpan.FromMinutes(15));
        agg.Recompute();

        agg.Summaries.Single().State.Should().Be(TimerState.Done);
    }

    [Fact]
    public void Dispose_unsubscribes_so_aggregator_does_not_keep_sources_alive()
    {
        var src = new FakeSource("gandalf.test");
        var time = new ManualTime(Origin);

        var agg = new DashboardAggregator([src], time);
        var fired = 0;
        agg.Updated += (_, _) => fired++;

        agg.Dispose();
        src.RaiseProgressChanged();

        fired.Should().Be(0);
    }
}
