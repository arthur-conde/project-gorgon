using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Mithril.WorldSim.Player.Tests.TestSupport;

/// <summary>
/// In-memory <see cref="IFrameProducer{TPayload}"/> for tests. Each posted
/// entry carries a frame plus an <c>IsReplay</c> bit mimicking the real
/// L1 envelope's behaviour: the producer signals
/// <see cref="IModeAwareFrameProducer{TPayload}.ReachedLive"/> the moment it
/// yields the first non-replay entry, *before* the yield returns control to
/// the world's merger. This matches
/// <see cref="Mithril.WorldSim.Player.Producers.ClassifiedPlayerLogProducer"/>'s
/// signalling shape exactly, so tests exercise the same boundary the
/// production producer drives.
/// </summary>
internal sealed class StubFrameProducer<T> : IFrameProducer<T>, IModeAwareFrameProducer<T>
{
    private readonly Channel<Entry> _channel = Channel.CreateUnbounded<Entry>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _reachedLive = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool _modeAware;

    public StubFrameProducer(int priority = 0, bool modeAware = true, params Frame<T>[] initialLiveFrames)
    {
        Priority = priority;
        _modeAware = modeAware;
        // Convenience: bare frame ctors are treated as live for simple drain
        // / priority / order tests that don't care about mode boundaries.
        foreach (var f in initialLiveFrames)
        {
            _channel.Writer.TryWrite(new Entry(f, IsReplay: false));
        }
    }

    public int Priority { get; }

    Task IModeAwareFrameProducer<T>.ReachedLive
        => _modeAware ? _reachedLive.Task : Task.CompletedTask;

    public bool IsModeAware => _modeAware;

    /// <summary>
    /// Post a frame as a replay-phase entry. The producer does NOT signal
    /// <see cref="IModeAwareFrameProducer{TPayload}.ReachedLive"/> when
    /// yielding this entry.
    /// </summary>
    public void PostReplay(Frame<T> frame)
        => _channel.Writer.TryWrite(new Entry(frame, IsReplay: true));

    /// <summary>
    /// Post a frame as a live-phase entry. The first live entry yielded
    /// triggers the
    /// <see cref="IModeAwareFrameProducer{TPayload}.ReachedLive"/> signal
    /// immediately before the yield — matching the production
    /// <see cref="Mithril.WorldSim.Player.Producers.ClassifiedPlayerLogProducer"/>
    /// behaviour against the L1 envelope.
    /// </summary>
    public void PostLive(Frame<T> frame)
        => _channel.Writer.TryWrite(new Entry(frame, IsReplay: false));

    /// <summary>
    /// Force-signal
    /// <see cref="IModeAwareFrameProducer{TPayload}.ReachedLive"/> without
    /// yielding any frame.
    /// </summary>
    public void SignalReachedLive() => _reachedLive.TrySetResult();

    public void Complete() => _channel.Writer.TryComplete();

    public async IAsyncEnumerable<Frame<T>> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var entry in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (!entry.IsReplay)
            {
                _reachedLive.TrySetResult();
            }
            yield return entry.Frame;
        }
        // Stream ended — degenerate live-only completion path.
        _reachedLive.TrySetResult();
    }

    private readonly record struct Entry(Frame<T> Frame, bool IsReplay);
}
