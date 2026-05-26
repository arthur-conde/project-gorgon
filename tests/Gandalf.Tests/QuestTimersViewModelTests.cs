using System.IO;
using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player;
using Arda.World.Player.Events;
using FluentAssertions;
using Gandalf.Domain;
using Gandalf.Services;
using Gandalf.ViewModels;
using Mithril.Shared.Reference;
using Mithril.TestSupport;
using Xunit;

namespace Gandalf.Tests;

/// <summary>
/// Verifies the Quests tab VM only materializes rows the player cares about
/// (in journal or cooling/done) — not the full ~2,000 repeatable-quest
/// catalog. Post-migration this is structural: QuestSource.Catalog IS the active
/// set + keys-with-progress, so the VM doesn't need a relevance predicate.
/// </summary>
[Trait("Category", "FileIO")]
[Collection("FileIO")]
public class QuestTimersViewModelTests : IDisposable
{
    private readonly string _dir;
    private readonly string _charactersDir;

    public QuestTimersViewModelTests()
    {
        _dir = TestPaths.CreateTempDir("gandalf_quest_vm");
        _charactersDir = Path.Combine(_dir, "characters");
        Directory.CreateDirectory(_charactersDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private (QuestTimersViewModel vm, QuestSource src, DerivedTimerProgressService derived,
             FakeQuestState questState, TestDomainEventBus bus, ManualTime time)
        Build(params (string Key, Mithril.Reference.Models.Quests.Quest Quest)[] quests)
    {
        var active = new FakeActiveCharacterService();
        active.SetActiveCharacter("Arthur", "Kwatoxi");
        var time = new ManualTime(new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc));

        var derivedStore = new Mithril.Shared.Character.PerCharacterStore<DerivedProgress>(
            _charactersDir, "gandalf-derived.json",
            DerivedProgressJsonContext.Default.DerivedProgress);
        var derivedView = new Mithril.Shared.Character.PerCharacterView<DerivedProgress>(active, derivedStore);
        var derived = new DerivedTimerProgressService(derivedView, time);

        var refData = new FakeReferenceData(quests);
        var questState = new FakeQuestState();
        var bus = new TestDomainEventBus();
        var src = new QuestSource(derived, refData, questState, bus, time);
        var vm = new QuestTimersViewModel(src, derived, time);
        return (vm, src, derived, questState, bus, time);
    }

    private static int ParseQuestId(string cdnKey) =>
        int.Parse(cdnKey.AsSpan("quest_".Length));

    private static LogLineMetadata Meta(DateTime utc) =>
        new(new DateTimeOffset(utc, TimeSpan.Zero), ReadOn: DateTimeOffset.UtcNow, IsReplay: false);

    [Fact]
    public void Empty_state_does_not_materialize_full_catalog()
    {
        var quests = Enumerable.Range(0, 500)
            .Select(i => QuestFactory.Repeatable($"quest_{i}", $"Q{i}", $"Quest {i}", TimeSpan.FromHours(1)))
            .ToArray();

        var (vm, src, derived, _, _, _) = Build(quests);
        try
        {
            src.Catalog.Should().BeEmpty();
            vm.Timers.Should().BeEmpty();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Pending_quests_show_as_idle_rows()
    {
        var q1 = QuestFactory.Repeatable("quest_1", "Q1", "Quest 1", TimeSpan.FromHours(1));
        var q2 = QuestFactory.Repeatable("quest_2", "Q2", "Quest 2", TimeSpan.FromHours(1));
        var q3 = QuestFactory.Repeatable("quest_3", "Q3", "Quest 3", TimeSpan.FromHours(1));

        var (vm, src, derived, questState, bus, time) = Build(q1, q2, q3);
        try
        {
            var ts = time.GetUtcNow();
            questState.Accept(1, ts);
            questState.Accept(3, ts);
            bus.Publish(new QuestAccepted(1, Meta(ts.UtcDateTime)));
            bus.Publish(new QuestAccepted(3, Meta(ts.UtcDateTime)));

            vm.Timers.Should().HaveCount(2);
            vm.Timers.Should().OnlyContain(t => t.State == TimerState.Idle);
            vm.Timers.Select(t => t.Name).Should().BeEquivalentTo(["Quest 1", "Quest 3"]);
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Recently_completed_quest_appears_as_running()
    {
        var q = QuestFactory.Repeatable("quest_1", "Q1", "Daily", TimeSpan.FromHours(1));
        var (vm, src, derived, questState, bus, time) = Build(q);
        try
        {
            questState.Complete(1);
            bus.Publish(new QuestCompleted(1, Meta(time.GetUtcNow().UtcDateTime)));

            vm.Timers.Should().HaveCount(1);
            vm.Timers[0].State.Should().Be(TimerState.Running);
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Quest_completed_long_ago_appears_as_done()
    {
        var q = QuestFactory.Repeatable("quest_1", "Q1", "Daily", TimeSpan.FromHours(1));
        var (vm, src, derived, questState, bus, time) = Build(q);
        try
        {
            questState.Complete(1);
            bus.Publish(new QuestCompleted(1, Meta(time.GetUtcNow().UtcDateTime - TimeSpan.FromHours(2))));

            vm.Timers.Should().HaveCount(1);
            vm.Timers[0].State.Should().Be(TimerState.Done);
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Dismissed_row_disappears_from_visible_set()
    {
        var q = QuestFactory.Repeatable("quest_1", "Q1", "Daily", TimeSpan.FromHours(1));
        var (vm, src, derived, questState, bus, time) = Build(q);
        try
        {
            questState.Complete(1);
            bus.Publish(new QuestCompleted(1, Meta(time.GetUtcNow().UtcDateTime)));
            vm.Timers.Should().HaveCount(1);

            derived.Dismiss(QuestSource.Id, QuestSource.QuestKey("Q1"));

            vm.Timers.Should().BeEmpty();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Re_loading_a_dismissed_quest_re_adds_it_as_pending()
    {
        var q = QuestFactory.Repeatable("quest_1", "Q1", "Daily", TimeSpan.FromHours(1));
        var (vm, src, derived, questState, bus, time) = Build(q);
        try
        {
            questState.Complete(1);
            bus.Publish(new QuestCompleted(1, Meta(time.GetUtcNow().UtcDateTime)));
            derived.Dismiss(QuestSource.Id, QuestSource.QuestKey("Q1"));
            vm.Timers.Should().BeEmpty();

            questState.Accept(1, time.GetUtcNow());
            bus.Publish(new QuestAccepted(1, Meta(time.GetUtcNow().UtcDateTime)));

            vm.Timers.Should().HaveCount(1);
            vm.Timers[0].State.Should().Be(TimerState.Idle);
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
