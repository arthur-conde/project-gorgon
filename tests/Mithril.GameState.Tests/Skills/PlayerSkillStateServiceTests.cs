using FluentAssertions;
using Mithril.GameState.Skills;
using Mithril.GameState.Skills.Parsing;
using Mithril.Shared.Reference;
using Mithril.TestSupport;
using Mithril.WorldSim;
using Xunit;

namespace Mithril.GameState.Tests.Skills;

/// <summary>
/// Folder-level tests for <see cref="PlayerSkillStateService"/>. Post-#618
/// (Phase 1 of the world-sim migration) the service is an
/// <see cref="IFolder{TPayload}"/> for <see cref="SkillFrame"/>; the world's
/// merger drives <see cref="PlayerSkillStateService.Apply"/> per applied frame.
/// These tests drive <c>Apply</c> directly with synthetic
/// <see cref="SkillFrame"/> payloads — the producer + world wiring is covered
/// separately in <see cref="SkillFolderEndToEndTests"/>. Behaviour expectations
/// (snapshot semantics, change-event emission, reference enrichment) are
/// preserved from the pre-migration test suite.
/// </summary>
public sealed class PlayerSkillStateServiceTests
{
    // The original parser-fed test lines, preserved so we can re-parse them
    // via SkillLogParser when convenient (e.g. real captures) and feed the
    // parsed events as SkillFrame payloads.
    private const string LoadLine =
        "ProcessLoadSkills(" +
        "{type=Toolcrafting,raw=15,bonus=0,xp=26,tnl=680,max=50}, " +
        "{type=Tanning,raw=50,bonus=3,xp=0,tnl=5280,max=50}, " +
        "{type=Augmentation,raw=0,bonus=2,xp=0,tnl=1,max=0})";

    private static PlayerSkillStateService NewService() => new();
    private static PlayerSkillStateService NewService(IReferenceDataService refData) => new(refData);

    private static SkillEntry SkillRef(string key, string display, string xpTable, int maxBonus = 25)
        => new(key, display, Id: 0, Combat: false, XpTable: xpTable, MaxBonusLevels: maxBonus,
               Parents: [], Rewards: new Dictionary<string, SkillRewardEntry>());

    private static FakeReferenceData RefDataWith(params SkillEntry[] skills)
    {
        var f = new FakeReferenceData();
        foreach (var s in skills) f.SkillsRaw[s.Key] = s;
        return f;
    }

    private static DateTime Ts(int h, int m, int s) => new(2026, 5, 18, h, m, s, DateTimeKind.Utc);

    /// <summary>
    /// Parse a real log line via <see cref="SkillLogParser"/> and apply it to
    /// the folder as a <see cref="SkillFrame"/>. Keeps the test surface
    /// close to the pre-migration "feed a line through the L1 driver" path:
    /// the producer in production code does exactly this projection, so
    /// driving the folder through it exercises the same final shape with
    /// none of the async / lifetime ceremony.
    /// </summary>
    private static IReadOnlyList<IChangeEvent> ApplyLine(PlayerSkillStateService svc, DateTime timestamp, string line)
    {
        var parser = new SkillLogParser();
        var evt = parser.TryParse(line, timestamp);
        SkillFrame payload = evt switch
        {
            SkillsSnapshotEvent snap => new SkillsSnapshotFrame(snap.Skills),
            SkillProgressUpdateEvent upd => new SkillProgressUpdateFrame(upd.Skill, upd.XpGained),
            _ => throw new InvalidOperationException(
                $"Test line did not parse as a skill event: {line}"),
        };
        var frame = new Frame<SkillFrame>(new DateTimeOffset(timestamp, TimeSpan.Zero), payload);
        return ((IFolder<SkillFrame>)svc).Apply(frame, NoopClock.Instance);
    }

    [Fact]
    public void Cold_start_is_Empty_with_no_measurement()
    {
        var svc = NewService();
        svc.Current.Should().BeSameAs(PlayerSkillSnapshot.Empty);
        svc.Current.Source.Should().Be(SkillStateSource.None);
        svc.Current.MeasuredAt.Should().BeNull();
        svc.Current.Skills.Should().BeEmpty();
    }

    [Fact]
    public void ProcessLoadSkills_populates_full_snapshot_with_caveat_flags()
    {
        var svc = NewService();
        ApplyLine(svc, Ts(8, 22, 21), LoadLine);

        var cur = svc.Current;
        cur.Source.Should().Be(SkillStateSource.LiveLog);
        cur.MeasuredAt.Should().Be(Ts(8, 22, 21));
        cur.Skills.Should().HaveCount(3);

        cur.TryGet("Tanning", out var tanning).Should().BeTrue();
        tanning.IsTrainable.Should().BeTrue();
        tanning.IsCapped.Should().BeTrue(); // raw == max == 50

        cur.TryGet("Augmentation", out var aug).Should().BeTrue();
        aug.IsTrainable.Should().BeFalse(); // max == 0 pseudo-skill
        aug.IsCapped.Should().BeFalse();

        cur.TryGet("Toolcrafting", out var tool).Should().BeTrue();
        tool.IsCapped.Should().BeFalse();
        tool.Level.Should().Be(15);
        tool.BonusLevels.Should().Be(0);
    }

    [Fact]
    public void ProcessLoadSkills_is_a_wholesale_replace_not_a_merge()
    {
        var svc = NewService();
        ApplyLine(svc, Ts(8, 0, 0),
            "ProcessLoadSkills({type=Sword,raw=10,bonus=0,xp=1,tnl=2,max=50})");
        ApplyLine(svc, Ts(9, 0, 0),
            "ProcessLoadSkills({type=Cooking,raw=20,bonus=0,xp=1,tnl=2,max=50})");

        svc.Current.Skills.Keys.Should().Equal("Cooking"); // Sword gone
        svc.Current.MeasuredAt.Should().Be(Ts(9, 0, 0));
    }

    [Fact]
    public void ProcessUpdateSkill_upserts_one_skill_keeping_the_rest()
    {
        var svc = NewService();
        ApplyLine(svc, Ts(8, 22, 21), LoadLine);
        ApplyLine(svc, Ts(8, 30, 0),
            "ProcessUpdateSkill({type=Toolcrafting,raw=16,bonus=0,xp=5,tnl=700,max=50}, True, 4, 0, 0)");

        svc.Current.Skills.Should().HaveCount(3); // Tanning + Augmentation untouched
        svc.Current.TryGet("Toolcrafting", out var tool).Should().BeTrue();
        tool.Level.Should().Be(16);
        tool.XpTowardNextLevel.Should().Be(5);
        svc.Current.MeasuredAt.Should().Be(Ts(8, 30, 0));
    }

    [Fact]
    public void ProcessUpdateSkill_before_any_snapshot_yields_partial_state()
    {
        var svc = NewService();
        ApplyLine(svc, Ts(8, 30, 0),
            "ProcessUpdateSkill({type=NatureAppreciation,raw=26,bonus=2,xp=315,tnl=1350,max=50}, True, 110, 0, 0)");

        svc.Current.Source.Should().Be(SkillStateSource.LiveLog);
        svc.Current.Skills.Keys.Should().Equal("NatureAppreciation");
    }

    [Fact]
    public void Subscribe_replays_current_then_delivers_live_changes()
    {
        var svc = NewService();
        ApplyLine(svc, Ts(8, 22, 21), LoadLine);

        var seen = new List<PlayerSkillSnapshot>();
        using (svc.Subscribe(seen.Add))
        {
            seen.Should().HaveCount(1); // replay of current
            seen[0].Skills.Should().HaveCount(3);

            ApplyLine(svc, Ts(8, 30, 0),
                "ProcessUpdateSkill({type=Sword,raw=2,bonus=0,xp=1,tnl=9,max=50}, True, 1, 0, 0)");
        }

        seen.Should().HaveCount(2);
        seen[1].TryGet("Sword", out _).Should().BeTrue();
    }

    [Fact]
    public void Disposed_subscription_stops_receiving()
    {
        var svc = NewService();
        ApplyLine(svc, Ts(8, 22, 21), LoadLine);

        var seen = new List<PlayerSkillSnapshot>();
        var sub = svc.Subscribe(seen.Add);
        sub.Dispose();

        ApplyLine(svc, Ts(8, 30, 0),
            "ProcessUpdateSkill({type=Sword,raw=2,bonus=0,xp=1,tnl=9,max=50}, True, 1, 0, 0)");

        seen.Should().HaveCount(1); // only the replay; no live event after dispose
    }

    [Fact]
    public void SubscribeChanges_has_no_replay_then_delivers_Delta_with_XpGained()
    {
        var svc = NewService();
        ApplyLine(svc, Ts(8, 22, 21), LoadLine);

        var changes = new List<SkillChange>();
        using (svc.SubscribeChanges(changes.Add))
        {
            changes.Should().BeEmpty(); // no replay — a change is an event, not state

            ApplyLine(svc, Ts(8, 30, 0),
                "ProcessUpdateSkill({type=Toolcrafting,raw=16,bonus=0,xp=5,tnl=700,max=50}, True, 4, 0, 0)");
        }

        changes.Should().HaveCount(1);
        var c = changes[0];
        c.Kind.Should().Be(SkillChangeKind.Delta);
        c.SkillKey.Should().Be("Toolcrafting");
        c.Previous!.Value.Level.Should().Be(15); // from LoadLine
        c.Current.Level.Should().Be(16);
        c.XpGained.Should().Be(4);
    }

    [Fact]
    public void Delta_for_never_seen_skill_has_null_Previous()
    {
        var svc = NewService();

        var changes = new List<SkillChange>();
        using (svc.SubscribeChanges(changes.Add))
        {
            ApplyLine(svc, Ts(8, 30, 0),
                "ProcessUpdateSkill({type=Sword,raw=2,bonus=0,xp=1,tnl=9,max=50}, True, 1, 0, 0)");
        }

        changes.Should().ContainSingle();
        changes[0].Previous.Should().BeNull();
        changes[0].Current.Level.Should().Be(2);
    }

    [Fact]
    public void SnapshotReplace_emits_only_skills_that_actually_changed()
    {
        var svc = NewService();
        ApplyLine(svc, Ts(8, 0, 0),
            "ProcessLoadSkills({type=Sword,raw=10,bonus=0,xp=1,tnl=2,max=50})");

        var changes = new List<SkillChange>();
        using (svc.SubscribeChanges(changes.Add))
        {
            // Re-sync: Sword identical (no-op), Cooking new.
            ApplyLine(svc, Ts(8, 30, 0),
                "ProcessLoadSkills(" +
                "{type=Sword,raw=10,bonus=0,xp=1,tnl=2,max=50}, " +
                "{type=Cooking,raw=20,bonus=0,xp=3,tnl=4,max=50})");
        }

        changes.Should().ContainSingle(); // Sword unchanged → suppressed
        changes[0].Kind.Should().Be(SkillChangeKind.SnapshotReplace);
        changes[0].SkillKey.Should().Be("Cooking");
        changes[0].Previous.Should().BeNull();
        changes[0].XpGained.Should().Be(0); // snapshot is not a gain event
    }

    [Fact]
    public void Capped_tick_emits_IsCapped_transition_then_skill_goes_silent()
    {
        var svc = NewService();
        ApplyLine(svc, Ts(8, 0, 0),
            "ProcessLoadSkills({type=Sword,raw=49,bonus=0,xp=10,tnl=20,max=50})");

        var changes = new List<SkillChange>();
        using (svc.SubscribeChanges(changes.Add))
        {
            ApplyLine(svc, Ts(8, 30, 0),
                "ProcessUpdateSkill({type=Sword,raw=50,bonus=0,xp=0,tnl=20,max=50}, True, 5, 0, 0)");
            ApplyLine(svc, Ts(8, 31, 0),
                "ProcessUpdateSkill({type=Cooking,raw=3,bonus=0,xp=1,tnl=9,max=50}, True, 1, 0, 0)");
        }

        var swordChanges = changes.Where(c => c.SkillKey == "Sword").ToList();
        swordChanges.Should().ContainSingle();
        swordChanges[0].Previous!.Value.IsCapped.Should().BeFalse(); // 49 < 50
        swordChanges[0].Current.IsCapped.Should().BeTrue();          // 50 == 50
        changes.Count(c => c.SkillKey == "Sword").Should().Be(1);
    }

    [Fact]
    public void Real_Tailoring_level_up_emits_level_up_SkillChange_with_gross_XpGained()
    {
        var svc = NewService();
        ApplyLine(svc, Ts(12, 38, 57),
            "ProcessUpdateSkill({type=Tailoring,raw=9,bonus=2,xp=199,tnl=210,max=50}, True, 160, 0, 0)");

        var changes = new List<SkillChange>();
        using (svc.SubscribeChanges(changes.Add))
        {
            ApplyLine(svc, Ts(12, 39, 2),
                "ProcessUpdateSkill({type=Tailoring,raw=10,bonus=2,xp=149,tnl=420,max=50}, True, 160, 0, 0)");
        }

        changes.Should().ContainSingle();
        var c = changes[0];
        c.Kind.Should().Be(SkillChangeKind.Delta);
        c.SkillKey.Should().Be("Tailoring");
        c.Previous!.Value.Level.Should().Be(9);
        c.Current.Level.Should().Be(10);
        (c.Previous!.Value.Level < c.Current.Level).Should().BeTrue(); // level-up signal
        c.Current.XpTowardNextLevel.Should().Be(149);
        c.XpGained.Should().Be(160); // gross gain across the rollover
    }

    // ── #470: reference-data enrichment ───────────────────────────────────

    private const string ProxyVsAuthLine =
        "ProcessLoadSkills(" +
        "{type=Foo,raw=0,bonus=2,xp=0,tnl=1,max=0}, " +
        "{type=Bar,raw=10,bonus=0,xp=1,tnl=2,max=50})";

    [Fact]
    public void Reference_enrichment_makes_IsTrainable_authoritative_over_the_log_proxy()
    {
        var refData = RefDataWith(
            SkillRef("Foo", "Foocraft", xpTable: "TypicalNoncombatSkill", maxBonus: 25),
            SkillRef("Bar", "Bar (umbrella)", xpTable: "None", maxBonus: 125));
        var svc = NewService(refData);
        ApplyLine(svc, Ts(8, 0, 0), ProxyVsAuthLine);

        svc.Current.TryGet("Foo", out var foo).Should().BeTrue();
        foo.MaxLevel.Should().Be(0);          // log proxy would say not trainable…
        foo.IsTrainable.Should().BeTrue();    // …reference (XpTable != None) overrides
        foo.DisplayName.Should().Be("Foocraft");
        foo.Reference!.XpTable.Should().Be("TypicalNoncombatSkill");

        svc.Current.TryGet("Bar", out var bar).Should().BeTrue();
        bar.MaxLevel.Should().Be(50);         // log proxy would say trainable…
        bar.IsTrainable.Should().BeFalse();   // …reference (XpTable == None) overrides
        bar.DisplayName.Should().Be("Bar (umbrella)");
        bar.Reference!.MaxBonusLevels.Should().Be(125);
    }

    [Fact]
    public void Without_reference_data_falls_back_to_the_verified_log_proxy()
    {
        var svc = NewService();
        ApplyLine(svc, Ts(8, 0, 0), ProxyVsAuthLine);

        svc.Current.TryGet("Foo", out var foo).Should().BeTrue();
        foo.Reference.Should().BeNull();
        foo.DisplayName.Should().BeNull();
        foo.IsTrainable.Should().BeFalse(); // proxy: max==0

        svc.Current.TryGet("Bar", out var bar).Should().BeTrue();
        bar.IsTrainable.Should().BeTrue();  // proxy: max>0
    }

    [Fact]
    public void Skill_absent_from_catalog_falls_back_to_proxy()
    {
        var svc = NewService(RefDataWith(SkillRef("Unrelated", "Unrelated", "None")));
        ApplyLine(svc, Ts(8, 0, 0), ProxyVsAuthLine);

        svc.Current.TryGet("Foo", out var foo).Should().BeTrue();
        foo.Reference.Should().BeNull();
        foo.IsTrainable.Should().BeFalse(); // proxy fallback
    }

    [Fact]
    public void Real_skill_delta_carries_authoritative_DisplayName_and_Reference()
    {
        var refData = RefDataWith(
            SkillRef("Tailoring", "Tailoring", xpTable: "TypicalNoncombatSkill", maxBonus: 25));
        var svc = NewService(refData);
        ApplyLine(svc, Ts(12, 39, 2),
            "ProcessUpdateSkill({type=Tailoring,raw=10,bonus=2,xp=149,tnl=420,max=50}, True, 160, 0, 0)");

        svc.Current.TryGet("Tailoring", out var t).Should().BeTrue();
        t.DisplayName.Should().Be("Tailoring");
        t.IsTrainable.Should().BeTrue();
        t.Reference!.XpTable.Should().Be("TypicalNoncombatSkill");
        t.Level.Should().Be(10); // log progression untouched by enrichment
    }

    /// <summary>
    /// Replay idempotence — a folder is a state-rebuilder, so feeding the
    /// same payload twice (cold start, then re-applied with identical
    /// content) must produce identical snapshot state and no spurious
    /// change events on the second application. Replaces the pre-migration
    /// L1-replay byte-equivalence test (which exercised the L1 driver's
    /// replay→live split — now covered by
    /// <see cref="SkillFolderEndToEndTests"/>'s producer + world test).
    /// </summary>
    [Fact]
    public void Apply_is_idempotent_under_identical_re_emission()
    {
        var svc1 = NewService();
        ApplyLine(svc1, Ts(8, 22, 21), LoadLine);
        var firstPass = svc1.Current.Skills;
        var firstMeasured = svc1.Current.MeasuredAt!.Value;

        var svc2 = NewService();
        ApplyLine(svc2, Ts(8, 22, 21), LoadLine);
        // Second application of the SAME content: assert state stays equal
        // AND no per-skill change events fire (SnapshotReplace path skips
        // unchanged projections by construction).
        var changes = new List<SkillChange>();
        using (svc2.SubscribeChanges(changes.Add))
        {
            ApplyLine(svc2, Ts(8, 22, 21), LoadLine);
        }

        svc2.Current.Skills.Should().BeEquivalentTo(firstPass);
        svc2.Current.MeasuredAt!.Value.Should().Be(firstMeasured);
        changes.Should().BeEmpty(
            "a re-emission with identical content must produce no per-skill change events");
    }

    /// <summary>
    /// Apply returns the same <see cref="SkillChange"/> set that legacy
    /// <see cref="IPlayerSkillState.SubscribeChanges"/> subscribers see —
    /// pinning the contract that the world's bus emissions and the legacy
    /// channel deliver identical content.
    /// </summary>
    [Fact]
    public void Apply_returns_change_events_equal_to_legacy_SubscribeChanges_deliveries()
    {
        var svc = NewService();
        ApplyLine(svc, Ts(8, 0, 0),
            "ProcessLoadSkills({type=Sword,raw=10,bonus=0,xp=1,tnl=2,max=50})");

        var legacy = new List<SkillChange>();
        using var _ = svc.SubscribeChanges(legacy.Add);

        var returned = ApplyLine(svc, Ts(8, 30, 0),
            "ProcessUpdateSkill({type=Sword,raw=11,bonus=0,xp=2,tnl=3,max=50}, True, 50, 0, 0)");

        returned.Should().HaveCount(1);
        legacy.Should().HaveCount(1);
        returned[0].Should().BeOfType<SkillChange>();
        ((SkillChange)returned[0]).Should().Be(legacy[0]);
    }

    /// <summary>
    /// Stand-in clock for direct folder tests — Apply never reads the clock
    /// surface (the folder uses the frame's own timestamp), so a zero-valued
    /// stub is sufficient to satisfy the parameter.
    /// </summary>
    private sealed class NoopClock : IWorldClock
    {
        public static readonly NoopClock Instance = new();
        public DateTimeOffset Now => DateTimeOffset.MinValue;
        public long Frame => 0;
        public WorldMode Mode => WorldMode.Live;
    }
}
