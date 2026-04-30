using System.IO;
using FluentAssertions;
using Gandalf.Domain;
using Gandalf.Services;
using Gandalf.ViewModels;
using Mithril.Shared.Reference;
using Xunit;

namespace Gandalf.Tests;

/// <summary>
/// Verifies the Quests tab VM only materializes rows the player cares about
/// (pending in journal or cooling/done) — not the full ~2,000 repeatable-quest
/// catalog. The freeze bug was structural: a non-virtualizing WrapPanel asked
/// to render every catalog row.
/// </summary>
[Trait("Category", "FileIO")]
[Collection("FileIO")]
public class QuestTimersViewModelTests : IDisposable
{
    private readonly string _dir;
    private readonly string _charactersDir;

    public QuestTimersViewModelTests()
    {
        _dir = Mithril.TestSupport.TestPaths.CreateTempDir("gandalf_quest_vm");
        _charactersDir = Path.Combine(_dir, "characters");
        Directory.CreateDirectory(_charactersDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private (QuestTimersViewModel vm, QuestSource src, DerivedTimerProgressService derived, ManualTime time)
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
        var src = new QuestSource(derived, refData, time);
        var vm = new QuestTimersViewModel(src, derived);
        return (vm, src, derived, time);
    }

    [Fact]
    public void Empty_state_does_not_materialize_full_catalog()
    {
        // Synthesize a ~500-quest catalog (still 100x larger than what should
        // ever appear in the VM). Regression guard for the freeze bug.
        var quests = Enumerable.Range(0, 500)
            .Select(i => QuestEntryFactory.Repeatable($"k{i}", $"Q{i}", $"Quest {i}", TimeSpan.FromHours(1)))
            .ToArray();

        var (vm, src, derived, _) = Build(quests);
        try
        {
            src.Catalog.Should().HaveCount(500);
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

        var (vm, src, derived, _) = Build(q1, q2, q3);
        try
        {
            src.OnQuestLoaded("Q1");
            src.OnQuestLoaded("Q3");

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
        var (vm, src, derived, _) = Build(q);
        try
        {
            // StartedAt = now → 1h cooldown still ticking. State reads wall clock,
            // so anchor on real `DateTime.UtcNow` rather than the injected provider.
            src.OnQuestCompleted("Q1", DateTime.UtcNow);

            vm.Timers.Should().HaveCount(1);
            vm.Timers[0].State.Should().Be(TimerState.Running);
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Quest_completed_long_ago_appears_as_done()
    {
        var q = QuestEntryFactory.Repeatable("k1", "Q1", "Daily", TimeSpan.FromHours(1));
        var (vm, src, derived, _) = Build(q);
        try
        {
            // StartedAt = 2h ago → 1h cooldown elapsed → Done.
            src.OnQuestCompleted("Q1", DateTime.UtcNow - TimeSpan.FromHours(2));

            vm.Timers.Should().HaveCount(1);
            vm.Timers[0].State.Should().Be(TimerState.Done);
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Dismissed_row_disappears_from_visible_set()
    {
        var q = QuestEntryFactory.Repeatable("k1", "Q1", "Daily", TimeSpan.FromHours(1));
        var (vm, src, derived, time) = Build(q);
        try
        {
            src.OnQuestCompleted("Q1", time.GetUtcNow().UtcDateTime);
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
        var (vm, src, derived, time) = Build(q);
        try
        {
            src.OnQuestCompleted("Q1", time.GetUtcNow().UtcDateTime);
            derived.Dismiss(QuestSource.Id, QuestSource.QuestKey("Q1"));
            vm.Timers.Should().BeEmpty();

            src.OnQuestLoaded("Q1");

            vm.Timers.Should().HaveCount(1);
            vm.Timers[0].State.Should().Be(TimerState.Idle);
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Tick_does_not_refresh_view_when_no_state_changed()
    {
        var q = QuestEntryFactory.Repeatable("k1", "Q1", "Daily", TimeSpan.FromHours(10));
        var (vm, src, derived, time) = Build(q);
        try
        {
            src.OnQuestCompleted("Q1", time.GetUtcNow().UtcDateTime);
            var refreshes = 0;
            ((System.ComponentModel.ICollectionView)vm.TimersView).CollectionChanged += (_, _) => refreshes++;

            // Advance only seconds — still Running.
            time.Advance(TimeSpan.FromSeconds(5));
            vm.Tick();

            refreshes.Should().Be(0, "no state transition means no Refresh() — the freeze bug came from doing this every second");
            vm.Timers[0].State.Should().Be(TimerState.Running);
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
