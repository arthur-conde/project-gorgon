using System.IO;
using FluentAssertions;
using Gandalf.Domain;
using Gandalf.Services;
using Mithril.Shared.Character;
using Xunit;

namespace Gandalf.Tests;

[Trait("Category", "FileIO")]
[Collection("FileIO")]
public class QuestSourceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _charactersDir;

    public QuestSourceTests()
    {
        _dir = Mithril.TestSupport.TestPaths.CreateTempDir("gandalf_quest_source");
        _charactersDir = Path.Combine(_dir, "characters");
        Directory.CreateDirectory(_charactersDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private (QuestSource src, DerivedTimerProgressService derived, FakeReferenceData refData, ManualTime time)
        Build(params Mithril.Shared.Reference.QuestEntry[] quests)
    {
        var active = new FakeActiveCharacterService();
        active.SetActiveCharacter("Arthur", "Kwatoxi");
        var time = new ManualTime(new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc));

        var derivedStore = new PerCharacterStore<DerivedProgress>(_charactersDir, "gandalf-derived.json",
            DerivedProgressJsonContext.Default.DerivedProgress);
        var derivedView = new PerCharacterView<DerivedProgress>(active, derivedStore);
        var derived = new DerivedTimerProgressService(derivedView, time);

        var refData = new FakeReferenceData(quests);
        var src = new QuestSource(derived, refData, time);
        return (src, derived, refData, time);
    }

    [Fact]
    public void SourceId_is_stable()
    {
        var (src, derived, _, _) = Build();
        try
        {
            src.SourceId.Should().Be("gandalf.quest");
            QuestSource.Id.Should().Be(src.SourceId);
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Catalog_includes_repeatable_quests_with_reuse_duration()
    {
        var quest = QuestEntryFactory.Repeatable("quest_1", "Q1", "Daily Pet Care", TimeSpan.FromHours(20),
            location: "Serbule");
        var (src, derived, _, _) = Build(quest);
        try
        {
            src.Catalog.Should().HaveCount(1);
            src.Catalog[0].DisplayName.Should().Be("Daily Pet Care");
            src.Catalog[0].Duration.Should().Be(TimeSpan.FromHours(20));
            src.Catalog[0].Region.Should().Be("Serbule");
            src.Catalog[0].SourceMetadata.Should().BeOfType<QuestCatalogPayload>();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Catalog_excludes_non_repeatable_quests()
    {
        var nonRep = QuestEntryFactory.NonRepeatable("quest_x", "QX", "One-Off Story Quest");
        var (src, derived, _, _) = Build(nonRep);
        try
        {
            src.Catalog.Should().BeEmpty();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Catalog_filter_drops_QuestCompletedRecently_gated_quests()
    {
        var gated = QuestEntryFactory.Repeatable("quest_g", "QG", "Gated", TimeSpan.FromHours(1),
            requirements: QuestEntryFactory.TimeGate("QuestCompletedRecently"));
        var ok = QuestEntryFactory.Repeatable("quest_ok", "QOK", "Ungated", TimeSpan.FromHours(1));

        var (src, derived, _, _) = Build(gated, ok);
        try
        {
            src.Catalog.Should().HaveCount(1);
            src.Catalog[0].DisplayName.Should().Be("Ungated");
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Catalog_filter_drops_MinDelayAfterFirstCompletion_prefixed_requirements()
    {
        var gated = QuestEntryFactory.Repeatable("quest_g", "QG", "Gated", TimeSpan.FromHours(1),
            requirements: QuestEntryFactory.TimeGate("MinDelayAfterFirstCompletion_Hours"));

        var (src, derived, _, _) = Build(gated);
        try
        {
            src.Catalog.Should().BeEmpty();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void OnQuestCompleted_anchors_progress_to_log_timestamp()
    {
        var quest = QuestEntryFactory.Repeatable("quest_1", "Q1", "Daily", TimeSpan.FromHours(20));
        var (src, derived, _, time) = Build(quest);
        try
        {
            // Quest completed 5 hours ago.
            var fiveHoursAgo = time.GetUtcNow().UtcDateTime - TimeSpan.FromHours(5);
            src.OnQuestCompleted("Q1", fiveHoursAgo);

            var key = QuestSource.QuestKey("Q1");
            src.Progress.Should().ContainKey(key);
            src.Progress[key].StartedAt.Should().Be(new DateTimeOffset(fiveHoursAgo, TimeSpan.Zero));
            // 20h cooldown started 5h ago — 15h remaining.
            var remaining = quest.ReuseHours!.Value * TimeSpan.FromHours(1)
                            - (time.GetUtcNow() - src.Progress[key].StartedAt);
            remaining.Should().Be(TimeSpan.FromHours(15));
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void OnQuestCompleted_drops_quest_from_pending()
    {
        var quest = QuestEntryFactory.Repeatable("quest_1", "Q1", "Daily", TimeSpan.FromHours(20));
        var (src, derived, _, time) = Build(quest);
        try
        {
            src.OnQuestLoaded("Q1");
            src.PendingInternalNames.Should().Contain("Q1");

            src.OnQuestCompleted("Q1", time.GetUtcNow().UtcDateTime);
            src.PendingInternalNames.Should().NotContain("Q1");
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void OnQuestCompleted_for_unknown_quest_is_a_noop()
    {
        var (src, derived, _, time) = Build();
        try
        {
            src.OnQuestCompleted("UnknownQuest", time.GetUtcNow().UtcDateTime);
            src.Progress.Should().BeEmpty();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void TimerReady_fires_when_completion_is_anchored_past_the_cooldown()
    {
        var quest = QuestEntryFactory.Repeatable("quest_1", "Q1", "Daily", TimeSpan.FromHours(1));
        var (src, derived, _, time) = Build(quest);
        try
        {
            var captured = new List<TimerReadyEventArgs>();
            src.TimerReady += (_, e) => captured.Add(e);

            // Completed 2 hours ago — already past the 1h cooldown when we observe it.
            var twoHoursAgo = time.GetUtcNow().UtcDateTime - TimeSpan.FromHours(2);
            src.OnQuestCompleted("Q1", twoHoursAgo);

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
        var (src, derived, refData, _) = Build();
        try
        {
            src.Catalog.Should().BeEmpty();

            var raised = false;
            src.CatalogChanged += (_, _) => raised = true;

            // Stage new data, then raise the event the way the real service would.
            refData.SetQuests([QuestEntryFactory.Repeatable("quest_1", "Q1", "Late Arrival", TimeSpan.FromHours(2))]);
            refData.RaiseQuestsUpdated();

            raised.Should().BeTrue();
            src.Catalog.Should().HaveCount(1);
            src.Catalog[0].DisplayName.Should().Be("Late Arrival");
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Pending_initially_empty()
    {
        var (src, derived, _, _) = Build();
        try
        {
            src.PendingInternalNames.Should().BeEmpty();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void OnQuestLoaded_adds_to_pending()
    {
        var (src, derived, _, _) = Build();
        try
        {
            src.OnQuestLoaded("Q1");
            src.OnQuestLoaded("Q2");

            src.PendingInternalNames.Should().BeEquivalentTo("Q1", "Q2");
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
