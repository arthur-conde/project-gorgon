using System.IO;
using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.World.Player;
using Arda.World.Player.Events;
using FluentAssertions;
using Gandalf.Domain;
using Gandalf.Services;
using Mithril.Shared.Character;
using Mithril.TestSupport;
using Xunit;

namespace Gandalf.Tests;

/// <summary>
/// Post-migration QuestSource is a pure projector over Arda's
/// <see cref="IQuestState"/> + domain events via <see cref="IDomainEventSubscriber"/>
/// + <see cref="DerivedTimerProgressService"/>. Catalog =
/// active quests (resolved to InternalName via reference data) ∪ keys-with-progress.
/// </summary>
[Trait("Category", "FileIO")]
[Collection("FileIO")]
public class QuestSourceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _charactersDir;

    public QuestSourceTests()
    {
        _dir = TestPaths.CreateTempDir("gandalf_quest_source");
        _charactersDir = Path.Combine(_dir, "characters");
        Directory.CreateDirectory(_charactersDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private (QuestSource src, DerivedTimerProgressService derived, FakeReferenceData refData,
             FakeQuestState questState, TestDomainEventBus bus, ManualTime time)
        Build(params (string Key, Mithril.Reference.Models.Quests.Quest Quest)[] quests)
    {
        var active = new FakeActiveCharacterService();
        active.SetActiveCharacter("Arthur", "Kwatoxi");
        var time = new ManualTime(new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc));

        var derivedStore = new PerCharacterStore<DerivedProgress>(_charactersDir, "gandalf-derived.json",
            DerivedProgressJsonContext.Default.DerivedProgress);
        var derivedView = new PerCharacterView<DerivedProgress>(active, derivedStore);
        var derived = new DerivedTimerProgressService(derivedView, time);

        var refData = new FakeReferenceData(quests);
        var questState = new FakeQuestState();
        var bus = new TestDomainEventBus();
        var src = new QuestSource(derived, refData, questState, bus, time);
        return (src, derived, refData, questState, bus, time);
    }

    /// <summary>
    /// Parses the numeric quest ID from a CDN key like "quest_123".
    /// </summary>
    private static int ParseQuestId(string cdnKey) =>
        int.Parse(cdnKey.AsSpan("quest_".Length));

    private static LogLineMetadata Meta(DateTime utc) =>
        new(new DateTimeOffset(utc, TimeSpan.Zero), ReadOn: DateTimeOffset.UtcNow, IsReplay: false);

    [Fact]
    public void SourceId_is_stable()
    {
        var (src, derived, _, _, _, _) = Build();
        try
        {
            src.SourceId.Should().Be("gandalf.quest");
            QuestSource.Id.Should().Be(src.SourceId);
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Catalog_is_empty_when_no_quests_active_and_no_progress()
    {
        var quests = Enumerable.Range(0, 100)
            .Select(i => QuestFactory.Repeatable($"quest_{i}", $"Q{i}", $"Quest {i}", TimeSpan.FromHours(1)))
            .ToArray();
        var (src, derived, _, _, _, _) = Build(quests);
        try
        {
            src.Catalog.Should().BeEmpty();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Catalog_includes_active_quests_with_reuse_duration()
    {
        var quest = QuestFactory.Repeatable("quest_1", "Q1", "Daily Pet Care", TimeSpan.FromHours(20),
            location: "Serbule");
        var (src, derived, _, questState, bus, time) = Build(quest);
        try
        {
            var id = ParseQuestId("quest_1");
            questState.Accept(id, time.GetUtcNow());
            bus.Publish(new QuestAccepted(id, Meta(time.GetUtcNow().UtcDateTime)));

            src.Catalog.Should().HaveCount(1);
            src.Catalog[0].DisplayName.Should().Be("Daily Pet Care");
            src.Catalog[0].Duration.Should().Be(TimeSpan.FromHours(20));
            src.Catalog[0].Region.Should().Be("Serbule");
            src.Catalog[0].SourceMetadata.Should().BeOfType<QuestCatalogPayload>();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Catalog_excludes_non_repeatable_quests_even_when_active()
    {
        var nonRep = QuestFactory.NonRepeatable("quest_x", "QX", "One-Off Story Quest");
        var (src, derived, _, questState, bus, time) = Build(nonRep);
        try
        {
            questState.Accept(99, time.GetUtcNow());
            bus.Publish(new QuestAccepted(99, Meta(time.GetUtcNow().UtcDateTime)));
            src.Catalog.Should().BeEmpty();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Catalog_admits_active_quests_with_any_requirement_type_when_Reuse_is_present()
    {
        var recentlyGated = QuestFactory.Repeatable("quest_10", "QA", "Recently-gated daily",
            TimeSpan.FromHours(20),
            requirements: QuestFactory.TimeGate("QuestCompletedRecently"));
        var firstDelayGated = QuestFactory.Repeatable("quest_11", "QB", "First-delay-gated daily",
            TimeSpan.FromHours(20),
            requirements: QuestFactory.TimeGate("MinDelayAfterFirstCompletion_Hours"));
        var plain = QuestFactory.Repeatable("quest_12", "QC", "Plain daily", TimeSpan.FromHours(20));

        var (src, derived, _, questState, bus, time) = Build(recentlyGated, firstDelayGated, plain);
        try
        {
            var ts = time.GetUtcNow();
            questState.Accept(10, ts);
            questState.Accept(11, ts);
            questState.Accept(12, ts);
            bus.Publish(new QuestAccepted(10, Meta(ts.UtcDateTime)));
            bus.Publish(new QuestAccepted(11, Meta(ts.UtcDateTime)));
            bus.Publish(new QuestAccepted(12, Meta(ts.UtcDateTime)));

            src.Catalog.Should().HaveCount(3);
            src.Catalog.Select(c => c.DisplayName).Should().BeEquivalentTo(
                "Recently-gated daily", "First-delay-gated daily", "Plain daily");
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Completed_event_anchors_progress_to_log_timestamp()
    {
        var quest = QuestFactory.Repeatable("quest_1", "Q1", "Daily", TimeSpan.FromHours(20));
        var (src, derived, _, questState, bus, time) = Build(quest);
        try
        {
            var fiveHoursAgo = time.GetUtcNow().UtcDateTime - TimeSpan.FromHours(5);
            questState.Complete(ParseQuestId("quest_1"));
            bus.Publish(new QuestCompleted(ParseQuestId("quest_1"), Meta(fiveHoursAgo)));

            var key = QuestSource.QuestKey("Q1");
            src.Progress.Should().ContainKey(key);
            src.Progress[key].StartedAt.Should().Be(new DateTimeOffset(fiveHoursAgo, TimeSpan.Zero));
            var remaining = quest.Quest.ReuseTime_Hours!.Value * TimeSpan.FromHours(1)
                            - (time.GetUtcNow() - src.Progress[key].StartedAt);
            remaining.Should().Be(TimeSpan.FromHours(15));
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Replay_of_dismissed_quest_completion_preserves_dismissal()
    {
        var quest = QuestFactory.Repeatable("quest_1", "Q1", "Daily", TimeSpan.FromHours(20));
        var (src, derived, _, questState, bus, time) = Build(quest);
        try
        {
            var completedAt = time.GetUtcNow().UtcDateTime;
            questState.Complete(ParseQuestId("quest_1"));
            bus.Publish(new QuestCompleted(ParseQuestId("quest_1"), Meta(completedAt)));

            var key = QuestSource.QuestKey("Q1");
            derived.Dismiss(QuestSource.Id, key);
            src.Progress[key].DismissedAt.Should().NotBeNull();

            bus.Publish(new QuestCompleted(ParseQuestId("quest_1"), Meta(completedAt)));
            src.Progress[key].DismissedAt.Should().NotBeNull(
                "replay of the same StartedAt must not undo the user's dismissal");
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Genuine_re_completion_after_dismissal_resurrects_the_row()
    {
        var quest = QuestFactory.Repeatable("quest_1", "Q1", "Daily", TimeSpan.FromHours(20));
        var (src, derived, _, questState, bus, time) = Build(quest);
        try
        {
            questState.Complete(ParseQuestId("quest_1"));
            bus.Publish(new QuestCompleted(ParseQuestId("quest_1"), Meta(time.GetUtcNow().UtcDateTime)));
            var key = QuestSource.QuestKey("Q1");
            derived.Dismiss(QuestSource.Id, key);

            time.Advance(TimeSpan.FromHours(21));
            bus.Publish(new QuestCompleted(ParseQuestId("quest_1"), Meta(time.GetUtcNow().UtcDateTime)));

            src.Progress[key].StartedAt.Should().Be(time.GetUtcNow());
            src.Progress[key].DismissedAt.Should().BeNull();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Completed_event_for_unknown_quest_is_a_noop()
    {
        var (src, derived, _, _, bus, time) = Build();
        try
        {
            bus.Publish(new QuestCompleted(99999, Meta(time.GetUtcNow().UtcDateTime)));
            src.Progress.Should().BeEmpty();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void TimerReady_fires_when_completion_is_anchored_past_the_cooldown()
    {
        var quest = QuestFactory.Repeatable("quest_1", "Q1", "Daily", TimeSpan.FromHours(1));
        var (src, derived, _, questState, bus, time) = Build(quest);
        try
        {
            var captured = new List<TimerReadyEventArgs>();
            src.TimerReady += (_, e) => captured.Add(e);

            var twoHoursAgo = time.GetUtcNow().UtcDateTime - TimeSpan.FromHours(2);
            questState.Complete(ParseQuestId("quest_1"));
            bus.Publish(new QuestCompleted(ParseQuestId("quest_1"), Meta(twoHoursAgo)));

            captured.Should().HaveCount(1);
            captured[0].SourceId.Should().Be("gandalf.quest");
            captured[0].DisplayName.Should().Be("Daily");
            captured[0].SourceMetadata.Should().BeOfType<QuestCatalogPayload>();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Reference_data_FileUpdated_quests_rebuilds_catalog()
    {
        var (src, derived, refData, questState, bus, time) = Build();
        try
        {
            src.Catalog.Should().BeEmpty();

            var deltas = new List<TimerRowDelta>();
            src.RowsChanged += (_, e) => deltas.AddRange(e.Deltas);

            var late = QuestFactory.Repeatable("quest_1", "Q1", "Late Arrival", TimeSpan.FromHours(2));
            refData.SetQuests([late]);
            refData.RaiseQuestsUpdated();
            questState.Accept(ParseQuestId("quest_1"), time.GetUtcNow());
            bus.Publish(new QuestAccepted(ParseQuestId("quest_1"), Meta(time.GetUtcNow().UtcDateTime)));

            deltas.Should().Contain(d => d.Kind == TimerRowChangeKind.Added && d.Key == QuestSource.QuestKey("Q1"));
            src.Catalog.Should().HaveCount(1);
            src.Catalog[0].DisplayName.Should().Be("Late Arrival");
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Accepted_event_emits_RowsChanged_with_an_Added_delta()
    {
        var quest = QuestFactory.Repeatable("quest_50208", "Quest_WO_50208", "Work Order 50208", TimeSpan.FromHours(2));
        var (src, derived, _, questState, bus, time) = Build(quest);
        try
        {
            var batches = new List<IReadOnlyList<TimerRowDelta>>();
            src.RowsChanged += (_, e) => batches.Add(e.Deltas);

            questState.Accept(ParseQuestId("quest_50208"), time.GetUtcNow());
            bus.Publish(new QuestAccepted(ParseQuestId("quest_50208"), Meta(time.GetUtcNow().UtcDateTime)));

            batches.SelectMany(b => b)
                .Should().ContainSingle(d =>
                    d.Key == QuestSource.QuestKey("Quest_WO_50208")
                    && d.Kind == TimerRowChangeKind.Added);
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Abandoned_event_for_uncompleted_quest_emits_a_Removed_delta()
    {
        var quest = QuestFactory.Repeatable("quest_1", "Q1", "Daily", TimeSpan.FromHours(2));
        var (src, derived, _, questState, bus, time) = Build(quest);
        try
        {
            questState.Accept(ParseQuestId("quest_1"), time.GetUtcNow());
            bus.Publish(new QuestAccepted(ParseQuestId("quest_1"), Meta(time.GetUtcNow().UtcDateTime)));

            var batches = new List<IReadOnlyList<TimerRowDelta>>();
            src.RowsChanged += (_, e) => batches.Add(e.Deltas);

            questState.Remove(ParseQuestId("quest_1"));
            bus.Publish(new QuestsLoaded(0, Meta(time.GetUtcNow().UtcDateTime)));

            batches.SelectMany(b => b)
                .Should().ContainSingle(d =>
                    d.Key == QuestSource.QuestKey("Q1")
                    && d.Kind == TimerRowChangeKind.Removed);
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Completed_event_keeps_row_visible_via_keys_with_progress_after_active_set_drops()
    {
        var quest = QuestFactory.Repeatable("quest_1", "Q1", "Daily", TimeSpan.FromHours(2));
        var (src, derived, _, questState, bus, time) = Build(quest);
        try
        {
            questState.Accept(ParseQuestId("quest_1"), time.GetUtcNow());
            bus.Publish(new QuestAccepted(ParseQuestId("quest_1"), Meta(time.GetUtcNow().UtcDateTime)));
            src.Catalog.Should().HaveCount(1);

            questState.Complete(ParseQuestId("quest_1"));
            bus.Publish(new QuestCompleted(ParseQuestId("quest_1"), Meta(time.GetUtcNow().UtcDateTime)));

            src.Catalog.Should().HaveCount(1);
            src.Catalog[0].DisplayName.Should().Be("Daily");
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualTime(DateTime utcStart) => _now = new DateTimeOffset(utcStart, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}

/// <summary>
/// Minimal <see cref="IQuestState"/> fake for tests. Mutate the active set
/// with <see cref="Accept"/>/<see cref="Complete"/>/<see cref="Remove"/>,
/// then publish domain events on <see cref="TestDomainEventBus"/> for
/// QuestSource to react to.
/// </summary>
internal sealed class FakeQuestState : IQuestState
{
    private readonly Dictionary<int, QuestEntry> _active = new();

    public IReadOnlyDictionary<int, QuestEntry> ActiveQuests => _active;

    public void Accept(int questId, DateTimeOffset addedAt) =>
        _active[questId] = new QuestEntry(questId, addedAt);

    public void Complete(int questId) => _active.Remove(questId);
    public void Remove(int questId) => _active.Remove(questId);
}
