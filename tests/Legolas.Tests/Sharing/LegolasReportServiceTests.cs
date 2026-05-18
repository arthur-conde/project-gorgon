using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Sharing;
using Legolas.ViewModels;
using Mithril.Shared.Reference;

namespace Legolas.Tests.Sharing;

public class LegolasReportServiceTests
{
    private static readonly DateTimeOffset SessionStart = new(2026, 5, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SessionEnd   = new(2026, 5, 6, 12, 8, 30, TimeSpan.Zero);

    /// <summary>
    /// Minimal in-process clock for tests. Microsoft has a richer
    /// <c>Microsoft.Extensions.TimeProvider.Testing</c> package, but the report
    /// service only calls <see cref="TimeProvider.GetUtcNow"/>; this fake covers it
    /// without taking the dep.
    /// </summary>
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) { _now = start; }
        public override DateTimeOffset GetUtcNow() => _now;
        public void Set(DateTimeOffset when) => _now = when;
    }

    private static (LegolasReportService report, SurveyFlowController flow, SessionState session, LegolasSettings settings, FakeClock clock) BuildSut()
    {
        var clock = new FakeClock(SessionStart);
        var session = new SessionState();
        var settings = new LegolasSettings();
        var flow = new SurveyFlowController(session, settings, clock);
        var report = new LegolasReportService(flow, session, clock, activeChar: null);
        return (report, flow, session, settings, clock);
    }

    [Fact]
    public void Snapshot_taken_at_Done_captures_surveys_and_items()
    {
        var (report, flow, session, settings, clock) = BuildSut();
        settings.AutoResetWhenAllCollected = false;

        // Start the session: anchor → Ready. Clock is at SessionStart for the
        // first survey landing — that's the Ready→Listening edge that stamps
        // StartedAt now (the previous design stamped on AwaitingPosition→Listening,
        // but the Ready-state redesign moved it so every cycle re-stamps).
        // #454: FSM starts in Listening — the first survey add stamps StartedAt.
        var s1 = new SurveyItemViewModel(Survey.Create("Diamond", new MetreOffset(50, 30), 0));
        var s2 = new SurveyItemViewModel(Survey.Create("Coal", new MetreOffset(10, 0), 1));
        session.Surveys.Add(s1);
        session.Surveys.Add(s2);
        flow.OptimizeRoute();

        // Advance clock + record items + collect.
        clock.Set(SessionEnd);
        session.CollectedItems["Diamond"] = 3;
        session.CollectedItems["Coal"] = 5;

        // Marking the last one fires AllCollected → Transitioned(Done) → snapshot built.
        s1.UpdateModel(s1.Model with { Collected = true });
        s2.UpdateModel(s2.Model with { Collected = true });

        report.LatestReport.Should().NotBeNull();
        var p = report.LatestReport!;
        p.StartedAt.Should().Be(SessionStart);
        p.CompletedAt.Should().Be(SessionEnd);
        p.SurveyCount.Should().Be(2);
        // Without IReferenceDataService injected, all items end up under UnknownByName
        // (display-name keys preserved). Resolution is exercised in the next test.
        p.CollectedItemsByInternalName.Should().BeEmpty();
        p.UnknownByName.Should().NotBeNull();
        p.UnknownByName!.Should().ContainKey("Diamond").WhoseValue.Should().Be(3);
        p.UnknownByName!.Should().ContainKey("Coal").WhoseValue.Should().Be(5);
    }

    [Fact]
    public void Snapshot_resolves_display_names_to_InternalName_when_refdata_available()
    {
        var clock = new FakeClock(SessionStart);
        var session = new SessionState();
        var settings = new LegolasSettings();
        var flow = new SurveyFlowController(session, settings, clock);
        var refData = new StubRefData(
            new Item { Id = 1, Name = "Diamond", InternalName = "RawGem_Diamond", MaxStackSize = 100, IconId = 42, Keywords = [] },
            new Item { Id = 2, Name = "Mystery", InternalName = "Item_Unknown",   MaxStackSize = 100, IconId = 0,  Keywords = [] });
        var report = new LegolasReportService(flow, session, clock, activeChar: null, refData: refData);
        settings.AutoResetWhenAllCollected = false;

        // #454: FSM starts in Listening — the first survey add stamps StartedAt.
        var s1 = new SurveyItemViewModel(Survey.Create("Diamond", new MetreOffset(50, 30), 0));
        session.Surveys.Add(s1);
        flow.OptimizeRoute();
        clock.Set(SessionEnd);
        session.CollectedItems["Diamond"] = 3;
        session.CollectedItems["NotInCatalog"] = 1;
        s1.UpdateModel(s1.Model with { Collected = true });

        var p = report.LatestReport!;
        p.CollectedItemsByInternalName.Should().ContainKey("RawGem_Diamond")
            .WhoseValue.Should().Be(3);
        p.UnknownByName.Should().NotBeNull();
        p.UnknownByName!.Should().ContainKey("NotInCatalog").WhoseValue.Should().Be(1);
    }

    [Fact]
    public void Snapshot_survives_AutoReset_clearing_session()
    {
        // The whole point of the at-transition snapshot: AutoResetWhenAllCollected
        // calls Reset() *immediately* after Done fires, which empties Surveys and
        // CollectedItems on the SessionState. The snapshot, taken inside the
        // Transitioned event handler, captures the unmodified state before Reset.
        var (report, flow, session, settings, clock) = BuildSut();
        settings.AutoResetWhenAllCollected = true;

        // #454: FSM starts in Listening — the first survey add stamps StartedAt.
        var s1 = new SurveyItemViewModel(Survey.Create("Diamond", new MetreOffset(50, 30), 0));
        session.Surveys.Add(s1);
        flow.OptimizeRoute();
        clock.Set(SessionEnd);
        session.CollectedItems["Diamond"] = 3;
        s1.UpdateModel(s1.Model with { Collected = true });

        // Auto-reset has cleared the live session…
        session.Surveys.Should().BeEmpty();
        session.CollectedItems.Should().BeEmpty();
        // …but the snapshot kept the data. Without IReferenceDataService it lands
        // in UnknownByName keyed by display name; that's exercised separately in
        // the resolution test.
        report.LatestReport.Should().NotBeNull();
        report.LatestReport!.SurveyCount.Should().Be(1);
        report.LatestReport.UnknownByName!["Diamond"].Should().Be(3);
    }

    [Fact]
    public void Second_cycle_after_AutoReset_reports_real_elapsed_time()
    {
        // Regression: the original bug (Emraell's "0s elapsed" payload) was that
        // StartedAt was stamped only on AwaitingPosition→Listening, so after
        // auto-reset returned the FSM to Listening with StartedAt wiped, the next
        // run never re-stamped. The Ready-state redesign moves the stamp onto the
        // Ready→Listening edge (first-survey-arrival), which fires on every cycle.
        var (report, flow, session, settings, clock) = BuildSut();
        settings.AutoResetWhenAllCollected = true;

        // #454: FSM starts in Listening — the first survey add stamps StartedAt.

        // Cycle 1: surveys arrive at SessionStart, all collected at SessionEnd,
        // auto-reset fires.
        var s1 = new SurveyItemViewModel(Survey.Create("Diamond", new MetreOffset(50, 30), 0));
        session.Surveys.Add(s1);
        clock.Set(SessionEnd);
        session.CollectedItems["Diamond"] = 1;
        s1.UpdateModel(s1.Model with { Collected = true });

        // Cycle 2: clock advances another 10 minutes before any survey arrives.
        var cycle2Start = SessionEnd.AddMinutes(10);
        var cycle2End = cycle2Start.AddMinutes(7);
        clock.Set(cycle2Start);
        session.CollectedItems.Clear();
        session.CollectedItems["Coal"] = 2;
        var s2 = new SurveyItemViewModel(Survey.Create("Coal", new MetreOffset(10, 0), 0));
        session.Surveys.Add(s2);
        clock.Set(cycle2End);
        s2.UpdateModel(s2.Model with { Collected = true });

        // The latest report is from cycle 2. Its elapsed must reflect the cycle-2
        // clock advance, not collapse to ~0 (the pre-fix symptom).
        var p = report.LatestReport!;
        p.StartedAt.Should().Be(cycle2Start);
        p.CompletedAt.Should().Be(cycle2End);
        (p.CompletedAt - p.StartedAt).Should().Be(TimeSpan.FromMinutes(7));
    }

    [Fact]
    public void Motherlode_mode_does_not_snapshot()
    {
        var (report, flow, session, settings, _) = BuildSut();
        settings.AutoResetWhenAllCollected = false;
        session.Mode = SessionMode.Motherlode;

        // #454: FSM starts in Listening — the first survey add stamps StartedAt.
        var s1 = new SurveyItemViewModel(Survey.Create("Diamond", new MetreOffset(50, 30), 0));
        session.Surveys.Add(s1);
        flow.OptimizeRoute();
        s1.UpdateModel(s1.Model with { Collected = true });

        // FSM still hits Done, but the report service skips Motherlode runs in v1.
        flow.CurrentState.Should().Be(SurveyFlowState.Done);
        report.LatestReport.Should().BeNull();
    }

    [Fact]
    public void ReportGenerated_event_fires_on_Done()
    {
        var (report, flow, session, settings, _) = BuildSut();
        settings.AutoResetWhenAllCollected = false;
        LegolasSharePayload? captured = null;
        report.ReportGenerated += p => captured = p;

        // #454: FSM starts in Listening — the first survey add stamps StartedAt.
        var s1 = new SurveyItemViewModel(Survey.Create("Diamond", new MetreOffset(50, 30), 0));
        session.Surveys.Add(s1);
        flow.OptimizeRoute();
        s1.UpdateModel(s1.Model with { Collected = true });

        captured.Should().NotBeNull();
        captured!.SurveyCount.Should().Be(1);
    }

    [Theory]
    [InlineData(15, "15s")]
    [InlineData(75, "1m 15s")]
    [InlineData(3725, "1h 2m 5s")]
    [InlineData(0, "0s")]
    public void FormatElapsed_renders_human_readable(int totalSeconds, string expected)
    {
        var s = LegolasReportService.FormatElapsed(TimeSpan.FromSeconds(totalSeconds));
        s.Should().Be(expected);
    }

    [Fact]
    public void BuildSummary_resolves_display_names_when_refdata_provided()
    {
        var payload = new LegolasSharePayload
        {
            CharacterName = "Argothian",
            StartedAt = SessionStart,
            CompletedAt = SessionEnd,
            SurveyCount = 4,
            CollectedItemsByInternalName = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "RawGem_Diamond", 3 },
                { "Coal",           7 },
            },
        };
        var refData = new StubRefData(
            new Item { Id = 1, Name = "Diamond", InternalName = "RawGem_Diamond", MaxStackSize = 100, IconId = 1, Keywords = [] });

        var summary = LegolasReportService.BuildSummary(payload, refData);
        summary.Should().Contain("Argothian");
        summary.Should().Contain("Surveys collected: 4");
        summary.Should().Contain("Diamond ×3");      // resolved display name
        summary.Should().Contain("Coal ×7");          // unresolved → falls back to InternalName
        summary.Should().Contain("8m 30s");
    }

    [Fact]
    public void BuildSummary_falls_back_to_InternalName_without_refdata()
    {
        var payload = new LegolasSharePayload
        {
            CharacterName = "Argothian",
            StartedAt = SessionStart,
            CompletedAt = SessionEnd,
            SurveyCount = 2,
            CollectedItemsByInternalName = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "RawGem_Diamond", 3 },
            },
        };

        var summary = LegolasReportService.BuildSummary(payload);
        summary.Should().Contain("RawGem_Diamond ×3");
    }

    [Fact]
    public void BuildSummary_anonymous_payload_uses_anonymous_label()
    {
        var payload = new LegolasSharePayload
        {
            CharacterName = null,
            StartedAt = SessionStart,
            CompletedAt = SessionEnd,
            SurveyCount = 1,
        };
        LegolasReportService.BuildSummary(payload).Should().Contain("Anonymous");
    }

    /// <summary>
    /// Minimal IReferenceDataService stub. Only <see cref="Items"/> and
    /// <see cref="ItemsByInternalName"/> are populated — the report service uses
    /// the former to build a display-name index and the latter for InternalName
    /// → display-name resolution in <c>BuildSummary</c>. Other interface members
    /// return empty/no-op since the report path doesn't touch them.
    /// </summary>
    private sealed class StubRefData : IReferenceDataService
    {
        public StubRefData(params Item[] items)
        {
            var byId = new Dictionary<long, Item>();
            var byInternalName = new Dictionary<string, Item>(StringComparer.Ordinal);
            foreach (var i in items)
            {
                byId[i.Id] = i;
                if (!string.IsNullOrEmpty(i.InternalName))
                    byInternalName[i.InternalName!] = i;
            }
            Items = byId;
            ItemsByInternalName = byInternalName;
        }

        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, Item> Items { get; }
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; }
        public ItemKeywordIndex KeywordIndex => ItemKeywordIndex.Empty;
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> Quests { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> QuestsByInternalName { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public event EventHandler<string>? FileUpdated;
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        private void Suppress() => FileUpdated?.Invoke(this, "");
    }
}
