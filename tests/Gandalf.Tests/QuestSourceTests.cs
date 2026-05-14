using System.IO;
using FluentAssertions;
using Gandalf.Domain;
using Gandalf.Services;
using Mithril.Shared.Character;
using Mithril.TestSupport;
using Xunit;

namespace Gandalf.Tests;

/// <summary>
/// Post-#155 QuestSource is a pure projector over <see cref="Mithril.GameState.Quests.IQuestService"/>
/// + <see cref="DerivedTimerProgressService"/>. Catalog =
/// <c>ActiveQuests ∪ keys-with-progress</c>; ingestion (the old
/// <c>OnQuestJournalLoaded</c> / <c>OnQuestAccepted</c> / <c>OnQuestCompleted</c>
/// entry points) lives in QuestService now and is exercised via
/// <see cref="FakeQuestService"/> here.
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
             FakeQuestService questSvc, ManualTime time)
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
        var questSvc = new FakeQuestService();
        var src = new QuestSource(derived, refData, questSvc, time);
        return (src, derived, refData, questSvc, time);
    }

    [Fact]
    public void SourceId_is_stable()
    {
        var (src, derived, _, _, _) = Build();
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
        // Even with reference data full of repeatable quests, the catalog
        // projects only ActiveQuests ∪ keys-with-progress — not the universe.
        // Regression guard for the relevance-predicate wart that #155 retired.
        var quests = Enumerable.Range(0, 100)
            .Select(i => QuestFactory.Repeatable($"quest_{i}", $"Q{i}", $"Quest {i}", TimeSpan.FromHours(1)))
            .ToArray();
        var (src, derived, _, _, _) = Build(quests);
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
        var (src, derived, _, questSvc, time) = Build(quest);
        try
        {
            questSvc.RaiseAccepted("Q1", time.GetUtcNow().UtcDateTime);

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
        var (src, derived, _, questSvc, time) = Build(nonRep);
        try
        {
            questSvc.RaiseAccepted("QX", time.GetUtcNow().UtcDateTime);
            src.Catalog.Should().BeEmpty();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Catalog_admits_active_quests_with_any_requirement_type_when_Reuse_is_present()
    {
        // The game is the authoritative gate; QuestSource does not re-evaluate
        // QuestCompletedRecently / MinDelayAfterFirstCompletion / etc.
        var recentlyGated = QuestFactory.Repeatable("quest_a", "QA", "Recently-gated daily",
            TimeSpan.FromHours(20),
            requirements: QuestFactory.TimeGate("QuestCompletedRecently"));
        var firstDelayGated = QuestFactory.Repeatable("quest_b", "QB", "First-delay-gated daily",
            TimeSpan.FromHours(20),
            requirements: QuestFactory.TimeGate("MinDelayAfterFirstCompletion_Hours"));
        var plain = QuestFactory.Repeatable("quest_c", "QC", "Plain daily", TimeSpan.FromHours(20));

        var (src, derived, _, questSvc, time) = Build(recentlyGated, firstDelayGated, plain);
        try
        {
            questSvc.RaiseAccepted("QA", time.GetUtcNow().UtcDateTime);
            questSvc.RaiseAccepted("QB", time.GetUtcNow().UtcDateTime);
            questSvc.RaiseAccepted("QC", time.GetUtcNow().UtcDateTime);

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
        var (src, derived, _, questSvc, time) = Build(quest);
        try
        {
            // Quest completed 5 hours ago.
            var fiveHoursAgo = time.GetUtcNow().UtcDateTime - TimeSpan.FromHours(5);
            questSvc.RaiseCompleted("Q1", fiveHoursAgo);

            var key = QuestSource.QuestKey("Q1");
            src.Progress.Should().ContainKey(key);
            src.Progress[key].StartedAt.Should().Be(new DateTimeOffset(fiveHoursAgo, TimeSpan.Zero));
            // 20h cooldown started 5h ago — 15h remaining.
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
        var (src, derived, _, questSvc, time) = Build(quest);
        try
        {
            var completedAt = time.GetUtcNow().UtcDateTime;
            questSvc.RaiseCompleted("Q1", completedAt);

            var key = QuestSource.QuestKey("Q1");
            derived.Dismiss(QuestSource.Id, key);
            src.Progress[key].DismissedAt.Should().NotBeNull();

            // Same Completed event re-fires (Subscribe replay on a fresh
            // subscriber, or QuestService dispatching a duplicate). Dismissal
            // must survive — clearing it would resurrect a row the user X'd.
            questSvc.RaiseCompleted("Q1", completedAt);
            src.Progress[key].DismissedAt.Should().NotBeNull(
                "replay of the same StartedAt must not undo the user's dismissal");
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Genuine_re_completion_after_dismissal_resurrects_the_row()
    {
        var quest = QuestFactory.Repeatable("quest_1", "Q1", "Daily", TimeSpan.FromHours(20));
        var (src, derived, _, questSvc, time) = Build(quest);
        try
        {
            questSvc.RaiseCompleted("Q1", time.GetUtcNow().UtcDateTime);
            var key = QuestSource.QuestKey("Q1");
            derived.Dismiss(QuestSource.Id, key);

            // A genuine re-completion after the cooldown — different timestamp,
            // not a replay. The row should resurrect with a fresh clock.
            time.Advance(TimeSpan.FromHours(21));
            questSvc.RaiseCompleted("Q1", time.GetUtcNow().UtcDateTime);

            src.Progress[key].StartedAt.Should().Be(time.GetUtcNow());
            src.Progress[key].DismissedAt.Should().BeNull();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Completed_event_for_unknown_quest_is_a_noop()
    {
        var (src, derived, _, questSvc, time) = Build();
        try
        {
            questSvc.RaiseCompleted("UnknownQuest", time.GetUtcNow().UtcDateTime);
            src.Progress.Should().BeEmpty();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void TimerReady_fires_when_completion_is_anchored_past_the_cooldown()
    {
        var quest = QuestFactory.Repeatable("quest_1", "Q1", "Daily", TimeSpan.FromHours(1));
        var (src, derived, _, questSvc, time) = Build(quest);
        try
        {
            var captured = new List<TimerReadyEventArgs>();
            src.TimerReady += (_, e) => captured.Add(e);

            // Completed 2 hours ago — already past the 1h cooldown when we observe it.
            var twoHoursAgo = time.GetUtcNow().UtcDateTime - TimeSpan.FromHours(2);
            questSvc.RaiseCompleted("Q1", twoHoursAgo);

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
        var (src, derived, refData, questSvc, time) = Build();
        try
        {
            src.Catalog.Should().BeEmpty();

            var deltas = new List<TimerRowDelta>();
            src.RowsChanged += (_, e) => deltas.AddRange(e.Deltas);

            // Stage new reference data and accept a quest backed by it. The
            // FileUpdated rebuild will then have something to project.
            var late = QuestFactory.Repeatable("quest_1", "Q1", "Late Arrival", TimeSpan.FromHours(2));
            refData.SetQuests([late]);
            refData.RaiseQuestsUpdated();
            questSvc.RaiseAccepted("Q1", time.GetUtcNow().UtcDateTime);

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
        var (src, derived, _, questSvc, time) = Build(quest);
        try
        {
            var batches = new List<IReadOnlyList<TimerRowDelta>>();
            src.RowsChanged += (_, e) => batches.Add(e.Deltas);

            questSvc.RaiseAccepted("Quest_WO_50208", time.GetUtcNow().UtcDateTime);

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
        var (src, derived, _, questSvc, time) = Build(quest);
        try
        {
            questSvc.RaiseAccepted("Q1", time.GetUtcNow().UtcDateTime);
            var batches = new List<IReadOnlyList<TimerRowDelta>>();
            src.RowsChanged += (_, e) => batches.Add(e.Deltas);

            questSvc.RaiseAbandoned("Q1", time.GetUtcNow().UtcDateTime);

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
        // Catalog = ActiveQuests ∪ keys-with-progress. Completing a quest
        // drops it from the active set BUT _derived has the cooldown row, so
        // the union keeps it visible until the user dismisses.
        var quest = QuestFactory.Repeatable("quest_1", "Q1", "Daily", TimeSpan.FromHours(2));
        var (src, derived, _, questSvc, time) = Build(quest);
        try
        {
            questSvc.RaiseAccepted("Q1", time.GetUtcNow().UtcDateTime);
            src.Catalog.Should().HaveCount(1);

            questSvc.RaiseCompleted("Q1", time.GetUtcNow().UtcDateTime);

            // Active set dropped Q1 (Completed implies removal), but derived
            // progress now anchors the cooldown — so Q1 stays in the catalog.
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
