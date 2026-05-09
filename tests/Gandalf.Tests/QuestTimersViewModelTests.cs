using System.IO;
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
/// catalog. Post-#155 this is structural: QuestSource.Catalog IS the active
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
             FakeQuestService questSvc, ManualTime time)
        Build(params QuestEntry[] quests)
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
        var questSvc = new FakeQuestService();
        var src = new QuestSource(derived, refData, questSvc, time);
        var vm = new QuestTimersViewModel(src, derived, time);
        return (vm, src, derived, questSvc, time);
    }

    [Fact]
    public void Empty_state_does_not_materialize_full_catalog()
    {
        // Synthesize a ~500-quest reference universe (still 100x larger than
        // what should ever appear in the VM). Regression guard for the freeze
        // bug — even with a vast universe, an empty active set + no progress
        // means an empty catalog → empty VM.
        var quests = Enumerable.Range(0, 500)
            .Select(i => QuestEntryFactory.Repeatable($"k{i}", $"Q{i}", $"Quest {i}", TimeSpan.FromHours(1)))
            .ToArray();

        var (vm, src, derived, _, _) = Build(quests);
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
        var q1 = QuestEntryFactory.Repeatable("k1", "Q1", "Quest 1", TimeSpan.FromHours(1));
        var q2 = QuestEntryFactory.Repeatable("k2", "Q2", "Quest 2", TimeSpan.FromHours(1));
        var q3 = QuestEntryFactory.Repeatable("k3", "Q3", "Quest 3", TimeSpan.FromHours(1));

        var (vm, src, derived, questSvc, time) = Build(q1, q2, q3);
        try
        {
            questSvc.RaiseAccepted("Q1", time.GetUtcNow().UtcDateTime);
            questSvc.RaiseAccepted("Q3", time.GetUtcNow().UtcDateTime);

            vm.Timers.Should().HaveCount(2);
            vm.Timers.Should().OnlyContain(t => t.State == TimerState.Idle);
            vm.Timers.Select(t => t.Name).Should().BeEquivalentTo(["Quest 1", "Quest 3"]);
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Recently_completed_quest_appears_as_running()
    {
        var q = QuestEntryFactory.Repeatable("k1", "Q1", "Daily", TimeSpan.FromHours(1));
        var (vm, src, derived, questSvc, time) = Build(q);
        try
        {
            // StartedAt = now → 1h cooldown still ticking.
            questSvc.RaiseCompleted("Q1", time.GetUtcNow().UtcDateTime);

            vm.Timers.Should().HaveCount(1);
            vm.Timers[0].State.Should().Be(TimerState.Running);
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Quest_completed_long_ago_appears_as_done()
    {
        var q = QuestEntryFactory.Repeatable("k1", "Q1", "Daily", TimeSpan.FromHours(1));
        var (vm, src, derived, questSvc, time) = Build(q);
        try
        {
            // StartedAt = 2h ago → 1h cooldown elapsed → Done.
            questSvc.RaiseCompleted("Q1", time.GetUtcNow().UtcDateTime - TimeSpan.FromHours(2));

            vm.Timers.Should().HaveCount(1);
            vm.Timers[0].State.Should().Be(TimerState.Done);
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Dismissed_row_disappears_from_visible_set()
    {
        var q = QuestEntryFactory.Repeatable("k1", "Q1", "Daily", TimeSpan.FromHours(1));
        var (vm, src, derived, questSvc, time) = Build(q);
        try
        {
            questSvc.RaiseCompleted("Q1", time.GetUtcNow().UtcDateTime);
            vm.Timers.Should().HaveCount(1);

            derived.Dismiss(QuestSource.Id, QuestSource.QuestKey("Q1"));

            vm.Timers.Should().BeEmpty();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Re_loading_a_dismissed_quest_re_adds_it_as_pending()
    {
        var q = QuestEntryFactory.Repeatable("k1", "Q1", "Daily", TimeSpan.FromHours(1));
        var (vm, src, derived, questSvc, time) = Build(q);
        try
        {
            questSvc.RaiseCompleted("Q1", time.GetUtcNow().UtcDateTime);
            derived.Dismiss(QuestSource.Id, QuestSource.QuestKey("Q1"));
            vm.Timers.Should().BeEmpty();

            questSvc.RaiseAccepted("Q1", time.GetUtcNow().UtcDateTime);

            vm.Timers.Should().HaveCount(1);
            vm.Timers[0].State.Should().Be(TimerState.Idle);
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    // The "Tick does not Refresh on text-only changes" regression test moved
    // out of this VM with the timer-model redesign — the per-tab tick now
    // lives in TimerDisplayScheduler. Coverage is preserved via
    // TimerDisplaySchedulerTests.Slow_tick_does_not_fire_RefreshRequired_for_text_only_changes
    // and the binder's once-per-batch RefreshRequired guarantee
    // (TimerSourceBinderTests.RefreshRequired_fires_at_most_once_per_batched_RowsChanged).

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualTime(DateTime utcStart) => _now = new DateTimeOffset(utcStart, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
