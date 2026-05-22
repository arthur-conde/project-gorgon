using FluentAssertions;
using Mithril.WorldSim.Player.Tests.TestSupport;
using Xunit;

namespace Mithril.WorldSim.Player.Tests;

public sealed class PlayerWorldTests
{
    private static DateTimeOffset Ts(int sec) => new(2026, 1, 1, 12, 0, sec, TimeSpan.Zero);

    // ── Drain order ──────────────────────────────────────────────────────

    [Fact]
    public async Task Merger_drains_single_producer_frames_in_timestamp_order()
    {
        var producer = new StubFrameProducer<string>(
            priority: 0,
            modeAware: false,
            new Frame<string>(Ts(1), "a"),
            new Frame<string>(Ts(2), "b"),
            new Frame<string>(Ts(3), "c"));
        producer.Complete();

        var folder = new RecordingFolder<string>();
        var world = new PlayerWorld();
        world.RegisterProducer(producer);
        world.RegisterFolder(folder);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await world.StartMerger(cts.Token);

        folder.Applied.Select(a => a.Frame.Payload).Should().Equal("a", "b", "c");
        folder.Applied.Select(a => a.ClockNow).Should().Equal(Ts(1), Ts(2), Ts(3));
        folder.Applied.Select(a => a.ClockFrame).Should().Equal(1L, 2L, 3L);
    }

    [Fact]
    public async Task Merger_drains_two_producers_in_timestamp_order_across_producers()
    {
        var a = new StubFrameProducer<string>(
            priority: 0, modeAware: false,
            new Frame<string>(Ts(1), "a1"),
            new Frame<string>(Ts(3), "a3"));
        var b = new StubFrameProducer<string>(
            priority: 1, modeAware: false,
            new Frame<string>(Ts(2), "b2"),
            new Frame<string>(Ts(4), "b4"));
        a.Complete();
        b.Complete();

        var folder = new RecordingFolder<string>();
        var world = new PlayerWorld();
        world.RegisterProducer(a);
        world.RegisterProducer(b);
        world.RegisterFolder(folder);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await world.StartMerger(cts.Token);

        folder.Applied.Select(x => x.Frame.Payload).Should().Equal("a1", "b2", "a3", "b4");
    }

    [Fact]
    public async Task Producer_priority_breaks_timestamp_ties()
    {
        // Same timestamp on every frame — only producer priority decides order.
        var hi = new StubFrameProducer<string>(
            priority: 0, modeAware: false,  // lower priority dispatches first
            new Frame<string>(Ts(5), "hi-1"),
            new Frame<string>(Ts(5), "hi-2"));
        var lo = new StubFrameProducer<string>(
            priority: 5, modeAware: false,
            new Frame<string>(Ts(5), "lo-1"),
            new Frame<string>(Ts(5), "lo-2"));
        hi.Complete();
        lo.Complete();

        var folder = new RecordingFolder<string>();
        var world = new PlayerWorld();
        world.RegisterProducer(hi);
        world.RegisterProducer(lo);
        world.RegisterFolder(folder);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await world.StartMerger(cts.Token);

        // Within the same timestamp: every priority-0 frame fires before any
        // priority-5 frame. Within a single producer the producer's own
        // emission order is preserved (the IFrameProducer contract).
        folder.Applied.Select(x => x.Frame.Payload).Should().Equal(
            "hi-1", "hi-2", "lo-1", "lo-2");
    }

    [Fact]
    public async Task Producer_registration_order_breaks_priority_ties()
    {
        // Same timestamp AND same priority — only registration order decides.
        var first = new StubFrameProducer<string>(
            priority: 0, modeAware: false,
            new Frame<string>(Ts(7), "first"));
        var second = new StubFrameProducer<string>(
            priority: 0, modeAware: false,
            new Frame<string>(Ts(7), "second"));
        first.Complete();
        second.Complete();

        var folder = new RecordingFolder<string>();
        var world = new PlayerWorld();
        world.RegisterProducer(first);
        world.RegisterProducer(second);
        world.RegisterFolder(folder);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await world.StartMerger(cts.Token);

        folder.Applied.Select(x => x.Frame.Payload).Should().Equal("first", "second");
    }

    // ── Mode transition ──────────────────────────────────────────────────

    [Fact]
    public async Task World_with_no_mode_aware_producers_starts_Live()
    {
        var producer = new StubFrameProducer<string>(
            priority: 0, modeAware: false,
            new Frame<string>(Ts(1), "x"));
        producer.Complete();

        var folder = new RecordingFolder<string>();
        var world = new PlayerWorld();
        world.RegisterProducer(producer);
        world.RegisterFolder(folder);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await world.StartMerger(cts.Token);

        world.Clock.Mode.Should().Be(WorldMode.Live);
        folder.Applied.Single().Mode.Should().Be(WorldMode.Live);
    }

    [Fact]
    public async Task World_with_mode_aware_producer_starts_Replaying()
    {
        var producer = new StubFrameProducer<string>(
            priority: 0, modeAware: true);
        // Replay-only frames; never flip to live; channel stays open so the
        // merger keeps blocking until the cancellation token cuts it.
        producer.PostReplay(new Frame<string>(Ts(1), "x"));
        producer.PostReplay(new Frame<string>(Ts(2), "y"));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var folder = new RecordingFolder<string>();
        var world = new PlayerWorld();
        world.RegisterProducer(producer);
        world.RegisterFolder(folder);

        var run = world.StartMerger(cts.Token);
        try { await run; }
        catch (OperationCanceledException) { /* expected */ }

        world.Clock.Mode.Should().Be(WorldMode.Replaying);
        folder.Applied.Should().NotBeEmpty();
        folder.Applied.Should().AllSatisfy(a => a.Mode.Should().Be(WorldMode.Replaying));
    }

    [Fact]
    public async Task Mode_flips_to_Live_when_ReachedLive_completes_and_emits_ModeChanged_on_bus()
    {
        var producer = new StubFrameProducer<string>(
            priority: 0, modeAware: true);

        var folder = new RecordingFolder<string>();
        var world = new PlayerWorld();
        world.RegisterProducer(producer);
        world.RegisterFolder(folder);

        var modeChanges = new List<Frame<ModeChanged>>();
        using var _sub = world.Bus.Subscribe<ModeChanged>(modeChanges.Add);

        // Two replay frames followed by one live frame. The stub signals
        // ReachedLive inline at the moment it yields the live entry —
        // matching WorldClockTickProducer's L1-envelope behaviour.
        producer.PostReplay(new Frame<string>(Ts(10), "r1"));
        producer.PostReplay(new Frame<string>(Ts(11), "r2"));
        producer.PostLive(new Frame<string>(Ts(12), "L1"));
        producer.Complete();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await world.StartMerger(cts.Token);

        // World caught up at the timestamp of the last replay frame (Ts(11)) —
        // the mode flip is observed between dispatches, so the bus emission's
        // `At` is the world clock right after the last replay frame applied.
        modeChanges.Should().HaveCount(1);
        modeChanges[0].Payload.From.Should().Be(WorldMode.Replaying);
        modeChanges[0].Payload.To.Should().Be(WorldMode.Live);
        modeChanges[0].Payload.At.Should().Be(Ts(11));
        modeChanges[0].Timestamp.Should().Be(Ts(11));

        // The first live frame (L1) is dispatched in Live mode; replay
        // frames before it dispatched in Replaying mode.
        var byPayload = folder.Applied.ToDictionary(a => a.Frame.Payload);
        byPayload["r1"].Mode.Should().Be(WorldMode.Replaying);
        byPayload["r2"].Mode.Should().Be(WorldMode.Replaying);
        byPayload["L1"].Mode.Should().Be(WorldMode.Live);

        world.Clock.Mode.Should().Be(WorldMode.Live);
    }

    [Fact]
    public async Task World_with_pre_completed_mode_aware_producer_starts_Live_without_emitting_ModeChanged()
    {
        // Pre-completing ReachedLive before StartAsync is the synchronous
        // analogue of "L1 seed is empty" — there is no Replaying phase to
        // transition out of. The world starts Live and emits no ModeChanged.
        var producer = new StubFrameProducer<string>(
            priority: 0, modeAware: true);
        producer.SignalReachedLive();
        producer.PostLive(new Frame<string>(Ts(1), "x"));
        producer.Complete();

        var folder = new RecordingFolder<string>();
        var world = new PlayerWorld();
        world.RegisterProducer(producer);
        world.RegisterFolder(folder);

        var modeChanges = new List<Frame<ModeChanged>>();
        using var sub = world.Bus.Subscribe<ModeChanged>(modeChanges.Add);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await world.StartMerger(cts.Token);

        world.Clock.Mode.Should().Be(WorldMode.Live);
        modeChanges.Should().BeEmpty();
        folder.Applied.Single().Mode.Should().Be(WorldMode.Live);
    }

    [Fact]
    public async Task World_does_not_emit_ModeChanged_when_first_frame_is_already_live()
    {
        // L1 with an empty initial replay snapshot: the producer signals
        // ReachedLive inline with the first envelope (which is live). The
        // world flips before any frame applies — Clock.Frame == 0 at the
        // flip — so no ModeChanged is emitted. Consumers see Mode = Live
        // from the start.
        var producer = new StubFrameProducer<string>(
            priority: 0, modeAware: true);
        producer.PostLive(new Frame<string>(Ts(5), "live-1"));
        producer.PostLive(new Frame<string>(Ts(6), "live-2"));
        producer.Complete();

        var folder = new RecordingFolder<string>();
        var world = new PlayerWorld();
        world.RegisterProducer(producer);
        world.RegisterFolder(folder);

        var modeChanges = new List<Frame<ModeChanged>>();
        using var sub = world.Bus.Subscribe<ModeChanged>(modeChanges.Add);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await world.StartMerger(cts.Token);

        modeChanges.Should().BeEmpty();
        world.Clock.Mode.Should().Be(WorldMode.Live);
        folder.Applied.Should().AllSatisfy(a => a.Mode.Should().Be(WorldMode.Live));
        folder.Applied.Select(a => a.Frame.Payload).Should().Equal("live-1", "live-2");
    }

    // ── Bus delivery order ──────────────────────────────────────────────

    [Fact]
    public async Task Bus_delivers_composer_emissions_in_resolution_order()
    {
        // Two composers cascade: one subscribed to a folder-emitted change,
        // one subscribed to the first composer's emission. The test asserts
        // the bus subscribers see the cascading domain frames in the order
        // they were emitted.
        var producer = new StubFrameProducer<string>(
            priority: 0, modeAware: false,
            new Frame<string>(Ts(1), "go"));
        producer.Complete();

        var folder = new EmittingFolder();
        var firstComposer = new TransformingComposer<TriggerEvent, FirstDomainFrame>(
            t => new FirstDomainFrame(t.Tag + "->1"));
        var secondComposer = new TransformingComposer<FirstDomainFrame, SecondDomainFrame>(
            f => new SecondDomainFrame(f.Tag + "->2"));

        var world = new PlayerWorld();
        world.RegisterProducer(producer);
        world.RegisterFolder(folder);
        world.RegisterComposer(firstComposer);
        world.RegisterComposer(secondComposer);

        var observed = new List<string>();
        using var s1 = world.Bus.Subscribe<FirstDomainFrame>(f => observed.Add("first:" + f.Payload.Tag));
        using var s2 = world.Bus.Subscribe<SecondDomainFrame>(f => observed.Add("second:" + f.Payload.Tag));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await world.StartMerger(cts.Token);

        // First composer fires on TriggerEvent → emits FirstDomainFrame.
        // Second composer (subscribed to FirstDomainFrame) cascades →
        // SecondDomainFrame. Bus observes them in emission order.
        observed.Should().Equal("first:go->1", "second:go->1->2");
    }

    [Fact]
    public async Task Bus_delivers_to_multiple_subscribers_in_registration_order()
    {
        var producer = new StubFrameProducer<string>(
            priority: 0, modeAware: false,
            new Frame<string>(Ts(1), "ping"));
        producer.Complete();

        var folder = new EmittingFolder();
        var composer = new TransformingComposer<TriggerEvent, FirstDomainFrame>(
            t => new FirstDomainFrame(t.Tag));

        var world = new PlayerWorld();
        world.RegisterProducer(producer);
        world.RegisterFolder(folder);
        world.RegisterComposer(composer);

        var observed = new List<string>();
        using var s1 = world.Bus.Subscribe<FirstDomainFrame>(f => observed.Add("A:" + f.Payload.Tag));
        using var s2 = world.Bus.Subscribe<FirstDomainFrame>(f => observed.Add("B:" + f.Payload.Tag));
        using var s3 = world.Bus.Subscribe<FirstDomainFrame>(f => observed.Add("C:" + f.Payload.Tag));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await world.StartMerger(cts.Token);

        observed.Should().Equal("A:ping", "B:ping", "C:ping");
    }

    // ── Registration discipline ─────────────────────────────────────────

    [Fact]
    public void Registering_two_folders_for_the_same_payload_type_throws()
    {
        var world = new PlayerWorld();
        world.RegisterFolder(new RecordingFolder<string>());

        var act = () => world.RegisterFolder(new RecordingFolder<string>());
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already registered*");
    }

    [Fact]
    public async Task Registrations_after_start_throw()
    {
        var producer = new StubFrameProducer<string>(
            priority: 0, modeAware: false);
        producer.Complete();

        var world = new PlayerWorld();
        world.RegisterProducer(producer);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await world.StartMerger(cts.Token);

        var folderAct = () => world.RegisterFolder(new RecordingFolder<string>());
        folderAct.Should().Throw<InvalidOperationException>()
            .WithMessage("*Cannot register*after StartAsync*");

        var producerAct = () => world.RegisterProducer(new StubFrameProducer<int>(modeAware: false));
        producerAct.Should().Throw<InvalidOperationException>();

        var composerAct = () => world.RegisterComposer(new TransformingComposer<TriggerEvent, FirstDomainFrame>(_ => new FirstDomainFrame("")));
        composerAct.Should().Throw<InvalidOperationException>();
    }

    // ── Test composer + folder used above ────────────────────────────────

    private sealed record TriggerEvent(string Tag) : IChangeEvent;
    private sealed record FirstDomainFrame(string Tag);
    private sealed record SecondDomainFrame(string Tag);

    /// <summary>
    /// Folder that emits a single <see cref="TriggerEvent"/> for every applied
    /// frame, carrying the frame's string payload. Lets the cascade test fire
    /// a chain without depending on a real game-state folder.
    /// </summary>
    private sealed class EmittingFolder : IFolder<string>
    {
        public IReadOnlyList<IChangeEvent> Apply(Frame<string> frame, IWorldClock clock)
            => new IChangeEvent[] { new TriggerEvent(frame.Payload) };
    }

    /// <summary>
    /// Composer that observes one event type and emits one frame per event,
    /// with the payload computed by the supplied projection. Lets the cascade
    /// test chain composers without needing a real domain-specific composer.
    /// </summary>
    private sealed class TransformingComposer<TIn, TOut> : IComposer
        where TIn : class
        where TOut : notnull
    {
        private readonly Func<TIn, TOut> _project;

        public TransformingComposer(Func<TIn, TOut> project) => _project = project;

        public IReadOnlyCollection<Type> Subscribes => new[] { typeof(TIn) };

        public IReadOnlyList<IFrame> Observe(object eventPayload, IWorldClock clock)
        {
            if (eventPayload is not TIn input) return Array.Empty<IFrame>();
            return new IFrame[] { new Frame<TOut>(clock.Now, _project(input)) };
        }
    }
}
