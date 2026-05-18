using System.Threading.Channels;
using FluentAssertions;
using Mithril.GameState.Skills;
using Mithril.GameState.Skills.Parsing;
using Mithril.Shared.Logging;
using Mithril.Shared.Reference;
using Mithril.TestSupport;
using Xunit;

namespace Mithril.GameState.Tests.Skills;

public sealed class PlayerSkillStateServiceTests
{
    private const string LoadLine =
        "[08:22:21] LocalPlayer: ProcessLoadSkills(" +
        "{type=Toolcrafting,raw=15,bonus=0,xp=26,tnl=680,max=50}, " +
        "{type=Tanning,raw=50,bonus=3,xp=0,tnl=5280,max=50}, " +
        "{type=Augmentation,raw=0,bonus=2,xp=0,tnl=1,max=0})";

    private static PlayerSkillStateService NewService(ScriptedStream stream)
        => new(stream, new SkillLogParser());

    private static PlayerSkillStateService NewService(ScriptedStream stream, IReferenceDataService refData)
        => new(stream, new SkillLogParser(), refData);

    private static SkillEntry SkillRef(string key, string display, string xpTable, int maxBonus = 25)
        => new(key, display, Id: 0, Combat: false, XpTable: xpTable, MaxBonusLevels: maxBonus,
               Parents: [], Rewards: new Dictionary<string, SkillRewardEntry>());

    private static FakeReferenceData RefDataWith(params SkillEntry[] skills)
    {
        var f = new FakeReferenceData();
        foreach (var s in skills) f.SkillsRaw[s.Key] = s;
        return f;
    }

    [Fact]
    public void Cold_start_is_Empty_with_no_measurement()
    {
        var svc = NewService(new ScriptedStream());
        svc.Current.Should().BeSameAs(PlayerSkillSnapshot.Empty);
        svc.Current.Source.Should().Be(SkillStateSource.None);
        svc.Current.MeasuredAt.Should().BeNull();
        svc.Current.Skills.Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessLoadSkills_populates_full_snapshot_with_caveat_flags()
    {
        var stream = new ScriptedStream(new RawLogLine(Ts(8, 22, 21), LoadLine));
        var svc = NewService(stream);
        await RunUntilDrainedAsync(svc, stream);

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
    public async Task ProcessLoadSkills_is_a_wholesale_replace_not_a_merge()
    {
        var stream = new ScriptedStream(
            new RawLogLine(Ts(8, 0, 0),
                "LocalPlayer: ProcessLoadSkills({type=Sword,raw=10,bonus=0,xp=1,tnl=2,max=50})"),
            new RawLogLine(Ts(9, 0, 0),
                "LocalPlayer: ProcessLoadSkills({type=Cooking,raw=20,bonus=0,xp=1,tnl=2,max=50})"));
        var svc = NewService(stream);
        await RunUntilDrainedAsync(svc, stream);

        svc.Current.Skills.Keys.Should().Equal("Cooking"); // Sword gone
        svc.Current.MeasuredAt.Should().Be(Ts(9, 0, 0));
    }

    [Fact]
    public async Task ProcessUpdateSkill_upserts_one_skill_keeping_the_rest()
    {
        var stream = new ScriptedStream(
            new RawLogLine(Ts(8, 22, 21), LoadLine),
            new RawLogLine(Ts(8, 30, 0),
                "LocalPlayer: ProcessUpdateSkill({type=Toolcrafting,raw=16,bonus=0,xp=5,tnl=700,max=50}, True, 4, 0, 0)"));
        var svc = NewService(stream);
        await RunUntilDrainedAsync(svc, stream);

        svc.Current.Skills.Should().HaveCount(3); // Tanning + Augmentation untouched
        svc.Current.TryGet("Toolcrafting", out var tool).Should().BeTrue();
        tool.Level.Should().Be(16);
        tool.XpTowardNextLevel.Should().Be(5);
        svc.Current.MeasuredAt.Should().Be(Ts(8, 30, 0));
    }

    [Fact]
    public async Task ProcessUpdateSkill_before_any_snapshot_yields_partial_state()
    {
        var stream = new ScriptedStream(new RawLogLine(Ts(8, 30, 0),
            "LocalPlayer: ProcessUpdateSkill({type=NatureAppreciation,raw=26,bonus=2,xp=315,tnl=1350,max=50}, True, 110, 0, 0)"));
        var svc = NewService(stream);
        await RunUntilDrainedAsync(svc, stream);

        svc.Current.Source.Should().Be(SkillStateSource.LiveLog);
        svc.Current.Skills.Keys.Should().Equal("NatureAppreciation");
    }

    [Fact]
    public async Task Subscribe_replays_current_then_delivers_live_changes()
    {
        var stream = new ScriptedStream(new RawLogLine(Ts(8, 22, 21), LoadLine));
        var svc = NewService(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await svc.StartAsync(cts.Token);
        await stream.WaitForDrainAsync(cts.Token);

        var seen = new List<PlayerSkillSnapshot>();
        using (svc.Subscribe(seen.Add))
        {
            seen.Should().HaveCount(1); // replay of current
            seen[0].Skills.Should().HaveCount(3);

            stream.Push("LocalPlayer: ProcessUpdateSkill({type=Sword,raw=2,bonus=0,xp=1,tnl=9,max=50}, True, 1, 0, 0)");
            await stream.WaitForDrainAsync(cts.Token);
        }

        seen.Should().HaveCount(2);
        seen[1].TryGet("Sword", out _).Should().BeTrue();

        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Disposed_subscription_stops_receiving()
    {
        var stream = new ScriptedStream(new RawLogLine(Ts(8, 22, 21), LoadLine));
        var svc = NewService(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await svc.StartAsync(cts.Token);
        await stream.WaitForDrainAsync(cts.Token);

        var seen = new List<PlayerSkillSnapshot>();
        var sub = svc.Subscribe(seen.Add);
        sub.Dispose();

        stream.Push("LocalPlayer: ProcessUpdateSkill({type=Sword,raw=2,bonus=0,xp=1,tnl=9,max=50}, True, 1, 0, 0)");
        await stream.WaitForDrainAsync(cts.Token);

        seen.Should().HaveCount(1); // only the replay; no live event after dispose

        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task SubscribeChanges_has_no_replay_then_delivers_Delta_with_XpGained()
    {
        var stream = new ScriptedStream(new RawLogLine(Ts(8, 22, 21), LoadLine));
        var svc = NewService(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await svc.StartAsync(cts.Token);
        await stream.WaitForDrainAsync(cts.Token);

        var changes = new List<SkillChange>();
        using (svc.SubscribeChanges(changes.Add))
        {
            changes.Should().BeEmpty(); // no replay — a change is an event, not state

            stream.Push("LocalPlayer: ProcessUpdateSkill({type=Toolcrafting,raw=16,bonus=0,xp=5,tnl=700,max=50}, True, 4, 0, 0)");
            await stream.WaitForDrainAsync(cts.Token);
        }

        changes.Should().HaveCount(1);
        var c = changes[0];
        c.Kind.Should().Be(SkillChangeKind.Delta);
        c.SkillKey.Should().Be("Toolcrafting");
        c.Previous!.Value.Level.Should().Be(15); // from LoadLine
        c.Current.Level.Should().Be(16);
        c.XpGained.Should().Be(4);

        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Delta_for_never_seen_skill_has_null_Previous()
    {
        var stream = new ScriptedStream();
        var svc = NewService(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await svc.StartAsync(cts.Token);

        var changes = new List<SkillChange>();
        using (svc.SubscribeChanges(changes.Add))
        {
            stream.Push("LocalPlayer: ProcessUpdateSkill({type=Sword,raw=2,bonus=0,xp=1,tnl=9,max=50}, True, 1, 0, 0)");
            await stream.WaitForDrainAsync(cts.Token);
        }

        changes.Should().ContainSingle();
        changes[0].Previous.Should().BeNull();
        changes[0].Current.Level.Should().Be(2);

        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task SnapshotReplace_emits_only_skills_that_actually_changed()
    {
        var stream = new ScriptedStream(new RawLogLine(Ts(8, 0, 0),
            "LocalPlayer: ProcessLoadSkills({type=Sword,raw=10,bonus=0,xp=1,tnl=2,max=50})"));
        var svc = NewService(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await svc.StartAsync(cts.Token);
        await stream.WaitForDrainAsync(cts.Token);

        var changes = new List<SkillChange>();
        using (svc.SubscribeChanges(changes.Add))
        {
            // Re-sync: Sword identical (no-op), Cooking new.
            stream.Push("LocalPlayer: ProcessLoadSkills(" +
                        "{type=Sword,raw=10,bonus=0,xp=1,tnl=2,max=50}, " +
                        "{type=Cooking,raw=20,bonus=0,xp=3,tnl=4,max=50})");
            await stream.WaitForDrainAsync(cts.Token);
        }

        changes.Should().ContainSingle(); // Sword unchanged → suppressed
        changes[0].Kind.Should().Be(SkillChangeKind.SnapshotReplace);
        changes[0].SkillKey.Should().Be("Cooking");
        changes[0].Previous.Should().BeNull();
        changes[0].XpGained.Should().Be(0); // snapshot is not a gain event

        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Capped_tick_emits_IsCapped_transition_then_skill_goes_silent()
    {
        var stream = new ScriptedStream(new RawLogLine(Ts(8, 0, 0),
            "LocalPlayer: ProcessLoadSkills({type=Sword,raw=49,bonus=0,xp=10,tnl=20,max=50})"));
        var svc = NewService(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await svc.StartAsync(cts.Token);
        await stream.WaitForDrainAsync(cts.Token);

        var changes = new List<SkillChange>();
        using (svc.SubscribeChanges(changes.Add))
        {
            // The capping tick: raw reaches max. PG then emits no further
            // ProcessUpdateSkill for Sword — modelled by a subsequent unrelated
            // skill update producing no Sword change.
            stream.Push("LocalPlayer: ProcessUpdateSkill({type=Sword,raw=50,bonus=0,xp=0,tnl=20,max=50}, True, 5, 0, 0)");
            stream.Push("LocalPlayer: ProcessUpdateSkill({type=Cooking,raw=3,bonus=0,xp=1,tnl=9,max=50}, True, 1, 0, 0)");
            await stream.WaitForDrainAsync(cts.Token);
        }

        var swordChanges = changes.Where(c => c.SkillKey == "Sword").ToList();
        swordChanges.Should().ContainSingle();
        swordChanges[0].Previous!.Value.IsCapped.Should().BeFalse(); // 49 < 50
        swordChanges[0].Current.IsCapped.Should().BeTrue();          // 50 == 50
        // No further Sword event despite later activity — the "goes silent" contract.
        changes.Count(c => c.SkillKey == "Sword").Should().Be(1);

        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Real_Tailoring_level_up_emits_level_up_SkillChange_with_gross_XpGained()
    {
        // The captured Tailoring level-up, fed through the service. Asserts the
        // level-up is detectable purely from raw incrementing (no dedicated
        // log line) and XpGained is the gross 160 (chat-matched), not split.
        var stream = new ScriptedStream(new RawLogLine(Ts(12, 38, 57),
            "[12:38:57] LocalPlayer: ProcessUpdateSkill({type=Tailoring,raw=9,bonus=2,xp=199,tnl=210,max=50}, True, 160, 0, 0)"));
        var svc = NewService(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await svc.StartAsync(cts.Token);
        await stream.WaitForDrainAsync(cts.Token);

        var changes = new List<SkillChange>();
        using (svc.SubscribeChanges(changes.Add))
        {
            stream.Push("[12:39:02] LocalPlayer: ProcessUpdateSkill({type=Tailoring,raw=10,bonus=2,xp=149,tnl=420,max=50}, True, 160, 0, 0)");
            await stream.WaitForDrainAsync(cts.Token);
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

        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
    }

    // ── #470: reference-data enrichment ───────────────────────────────────

    private const string ProxyVsAuthLine =
        "LocalPlayer: ProcessLoadSkills(" +
        // log proxy (max==0) → not trainable, but reference says it IS:
        "{type=Foo,raw=0,bonus=2,xp=0,tnl=1,max=0}, " +
        // log proxy (max>0) → trainable, but reference says umbrella:
        "{type=Bar,raw=10,bonus=0,xp=1,tnl=2,max=50})";

    [Fact]
    public async Task Reference_enrichment_makes_IsTrainable_authoritative_over_the_log_proxy()
    {
        var refData = RefDataWith(
            SkillRef("Foo", "Foocraft", xpTable: "TypicalNoncombatSkill", maxBonus: 25),
            SkillRef("Bar", "Bar (umbrella)", xpTable: "None", maxBonus: 125));
        var stream = new ScriptedStream(new RawLogLine(Ts(8, 0, 0), ProxyVsAuthLine));
        var svc = NewService(stream, refData);
        await RunUntilDrainedAsync(svc, stream);

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

        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Without_reference_data_falls_back_to_the_verified_log_proxy()
    {
        var stream = new ScriptedStream(new RawLogLine(Ts(8, 0, 0), ProxyVsAuthLine));
        var svc = NewService(stream); // no refData
        await RunUntilDrainedAsync(svc, stream);

        svc.Current.TryGet("Foo", out var foo).Should().BeTrue();
        foo.Reference.Should().BeNull();
        foo.DisplayName.Should().BeNull();
        foo.IsTrainable.Should().BeFalse(); // proxy: max==0

        svc.Current.TryGet("Bar", out var bar).Should().BeTrue();
        bar.IsTrainable.Should().BeTrue();  // proxy: max>0

        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Skill_absent_from_catalog_falls_back_to_proxy()
    {
        // refData present but doesn't know "Foo"/"Bar" → null Reference, proxy used.
        var stream = new ScriptedStream(new RawLogLine(Ts(8, 0, 0), ProxyVsAuthLine));
        var svc = NewService(stream, RefDataWith(SkillRef("Unrelated", "Unrelated", "None")));
        await RunUntilDrainedAsync(svc, stream);

        svc.Current.TryGet("Foo", out var foo).Should().BeTrue();
        foo.Reference.Should().BeNull();
        foo.IsTrainable.Should().BeFalse(); // proxy fallback

        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Real_skill_delta_carries_authoritative_DisplayName_and_Reference()
    {
        var refData = RefDataWith(
            SkillRef("Tailoring", "Tailoring", xpTable: "TypicalNoncombatSkill", maxBonus: 25));
        var stream = new ScriptedStream(new RawLogLine(Ts(12, 39, 2),
            "[12:39:02] LocalPlayer: ProcessUpdateSkill({type=Tailoring,raw=10,bonus=2,xp=149,tnl=420,max=50}, True, 160, 0, 0)"));
        var svc = NewService(stream, refData);
        await RunUntilDrainedAsync(svc, stream);

        svc.Current.TryGet("Tailoring", out var t).Should().BeTrue();
        t.DisplayName.Should().Be("Tailoring");
        t.IsTrainable.Should().BeTrue();
        t.Reference!.XpTable.Should().Be("TypicalNoncombatSkill");
        t.Level.Should().Be(10); // log progression untouched by enrichment
    }

    private static DateTime Ts(int h, int m, int s) => new(2026, 5, 18, h, m, s, DateTimeKind.Utc);

    private static async Task RunUntilDrainedAsync(PlayerSkillStateService svc, ScriptedStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await svc.StartAsync(cts.Token);
        await stream.WaitForDrainAsync(cts.Token);
        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
    }

    private sealed class ScriptedStream : IPlayerLogStream
    {
        private readonly Channel<RawLogLine> _channel = Channel.CreateUnbounded<RawLogLine>();
        private long _pending;
        private TaskCompletionSource _drained = NewDrainTcs();

        public ScriptedStream(params RawLogLine[] lines)
        {
            if (lines.Length == 0)
            {
                _drained.TrySetResult();
                return;
            }
            Interlocked.Add(ref _pending, lines.Length);
            foreach (var line in lines) _channel.Writer.TryWrite(line);
        }

        public void Push(string line)
        {
            Interlocked.Increment(ref _pending);
            Interlocked.Exchange(ref _drained, NewDrainTcs());
            _channel.Writer.TryWrite(new RawLogLine(DateTime.UtcNow, line));
        }

        public Task WaitForDrainAsync(CancellationToken ct) => _drained.Task.WaitAsync(ct);
        public Task WaitForDrainAsync(TimeSpan timeout) => _drained.Task.WaitAsync(timeout);

        public async IAsyncEnumerable<RawLogLine> SubscribeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var line))
                {
                    yield return line;
                    if (Interlocked.Decrement(ref _pending) == 0)
                        _drained.TrySetResult();
                }
            }
        }

        private static TaskCompletionSource NewDrainTcs() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
