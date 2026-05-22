using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FluentAssertions;
using Mithril.Shared.Logging;
using Mithril.WorldSim.Player.Internal;
using Mithril.WorldSim.Player.Producers;
using Xunit;

namespace Mithril.WorldSim.Player.Tests;

/// <summary>
/// End-to-end tests for the reshaped clock-tick path (#655): producer + folder
/// wired into a real <see cref="PlayerWorld"/>. Verifies that the public-bus
/// surface (<see cref="CalendarTimeAdvanced"/>) flows correctly, the world
/// clock still advances at the source-stream cadence, and the
/// Replaying → Live mode transition is preserved across the reshape.
/// </summary>
public sealed class WorldClockTickIntegrationTests
{
    private static DateTimeOffset Ts(int sec) => new(2026, 1, 1, 12, 0, sec, TimeSpan.Zero);

    [Fact]
    public async Task World_clock_advances_at_source_stream_cadence_even_with_no_other_folders()
    {
        // Three envelopes at three timestamps — the clock must visit each one
        // in turn. This is the load-bearing property of the reshape: the
        // clock-tick payload exists precisely so the clock can't stall during
        // stretches of source lines that no other folder cares about.
        var stream = new ScriptedClassifiedStream(
            new LogEnvelope<IClassifiedPlayerLogLine>(new ScriptedLine(Ts(1)), IsReplay: false),
            new LogEnvelope<IClassifiedPlayerLogLine>(new ScriptedLine(Ts(2)), IsReplay: false),
            new LogEnvelope<IClassifiedPlayerLogLine>(new ScriptedLine(Ts(3)), IsReplay: false));
        stream.Complete();

        var (world, producer) = BuildWorld(stream);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await world.StartMerger(cts.Token);

        world.Clock.Now.Should().Be(Ts(3));
        world.Clock.Frame.Should().Be(3L);
        _ = producer;
    }

    [Fact]
    public async Task Bus_publishes_one_CalendarTimeAdvanced_per_wall_clock_second_dedup()
    {
        // Five envelopes spanning three distinct seconds (1, 1, 2, 2, 3) —
        // three CalendarTimeAdvanced events on the bus.
        var stream = new ScriptedClassifiedStream(
            new LogEnvelope<IClassifiedPlayerLogLine>(new ScriptedLine(Ts(1)), IsReplay: false),
            new LogEnvelope<IClassifiedPlayerLogLine>(new ScriptedLine(Ts(1)), IsReplay: false),
            new LogEnvelope<IClassifiedPlayerLogLine>(new ScriptedLine(Ts(2)), IsReplay: false),
            new LogEnvelope<IClassifiedPlayerLogLine>(new ScriptedLine(Ts(2)), IsReplay: false),
            new LogEnvelope<IClassifiedPlayerLogLine>(new ScriptedLine(Ts(3)), IsReplay: false));
        stream.Complete();

        var (world, _) = BuildWorld(stream);

        var ticks = new List<Frame<CalendarTimeAdvanced>>();
        using var _sub = world.Bus.Subscribe<CalendarTimeAdvanced>(ticks.Add);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await world.StartMerger(cts.Token);

        ticks.Should().HaveCount(3);
        ticks.Select(t => t.Payload.Now).Should().Equal(Ts(1), Ts(2), Ts(3));
        ticks.Select(t => t.Timestamp).Should().Equal(Ts(1), Ts(2), Ts(3));
    }

    [Fact]
    public async Task Mode_transition_Replaying_to_Live_is_preserved_after_reshape()
    {
        // Two replay envelopes, then one live envelope. The mode flip happens
        // between dispatches (per the existing PlayerWorld behaviour); the
        // CalendarTimeAdvanced emitted for the first live tick must carry
        // Mode = Live, the prior ticks Mode = Replaying. This is the property
        // that lets Gandalf's scheduler-collapse alarms (migration item #12)
        // suppress side effects during drain.
        var stream = new ScriptedClassifiedStream(
            new LogEnvelope<IClassifiedPlayerLogLine>(new ScriptedLine(Ts(10)), IsReplay: true),
            new LogEnvelope<IClassifiedPlayerLogLine>(new ScriptedLine(Ts(11)), IsReplay: true),
            new LogEnvelope<IClassifiedPlayerLogLine>(new ScriptedLine(Ts(12)), IsReplay: false));
        stream.Complete();

        var (world, _) = BuildWorld(stream);

        var ticks = new List<Frame<CalendarTimeAdvanced>>();
        var modeChanges = new List<Frame<ModeChanged>>();
        using var _t = world.Bus.Subscribe<CalendarTimeAdvanced>(ticks.Add);
        using var _m = world.Bus.Subscribe<ModeChanged>(modeChanges.Add);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await world.StartMerger(cts.Token);

        ticks.Should().HaveCount(3);
        ticks[0].Payload.Should().Be(new CalendarTimeAdvanced(Ts(10), WorldMode.Replaying));
        ticks[1].Payload.Should().Be(new CalendarTimeAdvanced(Ts(11), WorldMode.Replaying));
        ticks[2].Payload.Should().Be(new CalendarTimeAdvanced(Ts(12), WorldMode.Live));

        modeChanges.Should().HaveCount(1);
        modeChanges[0].Payload.From.Should().Be(WorldMode.Replaying);
        modeChanges[0].Payload.To.Should().Be(WorldMode.Live);
    }

    [Fact]
    public async Task Bus_emits_no_CalendarTimeAdvanced_when_stream_carries_no_envelopes()
    {
        // Empty stream → no ticks. The producer's degenerate "complete
        // ReachedLive on stream end" path still fires (verified in the
        // producer unit tests), so the world is unblocked.
        var stream = new ScriptedClassifiedStream();
        stream.Complete();

        var (world, _) = BuildWorld(stream);

        var ticks = new List<Frame<CalendarTimeAdvanced>>();
        using var _sub = world.Bus.Subscribe<CalendarTimeAdvanced>(ticks.Add);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await world.StartMerger(cts.Token);

        ticks.Should().BeEmpty();
        world.Clock.Frame.Should().Be(0L);
    }

    private static (PlayerWorld World, WorldClockTickProducer Producer) BuildWorld(
        IClassifiedPlayerLogStream stream)
    {
        var producer = new WorldClockTickProducer(stream);
        var world = new PlayerWorld();
        world.RegisterProducer(producer);
        world.RegisterFolder(new WorldClockTickFolder());
        return (world, producer);
    }

    // ── Test stream ──────────────────────────────────────────────────────

    private sealed record ScriptedLine(
        DateTimeOffset Timestamp,
        string Data = "",
        long Sequence = 0,
        long ReadMonotonicTicks = 0,
        string? Raw = null) : IClassifiedPlayerLogLine;

    private sealed class ScriptedClassifiedStream : IClassifiedPlayerLogStream
    {
        private readonly Channel<LogEnvelope<IClassifiedPlayerLogLine>> _channel
            = Channel.CreateUnbounded<LogEnvelope<IClassifiedPlayerLogLine>>(
                new UnboundedChannelOptions { SingleReader = true });

        public ScriptedClassifiedStream(params LogEnvelope<IClassifiedPlayerLogLine>[] initial)
        {
            foreach (var e in initial)
            {
                _channel.Writer.TryWrite(e);
            }
        }

        public void Complete() => _channel.Writer.TryComplete();

        public async IAsyncEnumerable<LogEnvelope<IClassifiedPlayerLogLine>>
            SubscribeWithReplayMarkerAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var e in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return e;
            }
        }
    }
}
