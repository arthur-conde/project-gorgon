using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Mithril.Shared.Logging;
using Mithril.WorldSim.Chat.Producers;

namespace Mithril.WorldSim.Chat.Tests.TestSupport;

/// <summary>
/// In-memory <see cref="IChatLogReplaySource"/> for tests. Mirrors the
/// behaviour the real <see cref="ChatLogReplaySource"/> exposes: posted
/// envelopes carry an <c>IsReplay</c> bit that drives the producer's
/// <c>ReachedLive</c> signal on the first <c>IsReplay = false</c> entry —
/// matching <see cref="LogEnvelope{T}.IsReplay"/>'s "true until the first
/// live envelope; never re-armed" contract.
/// </summary>
internal sealed class StubChatLogReplaySource : IChatLogReplaySource
{
    private readonly Channel<LogEnvelope<RawLogLine>> _channel = Channel.CreateUnbounded<LogEnvelope<RawLogLine>>(
        new UnboundedChannelOptions { SingleReader = true });

    public void PostReplay(RawLogLine line) => _channel.Writer.TryWrite(new LogEnvelope<RawLogLine>(line, IsReplay: true));
    public void PostLive(RawLogLine line) => _channel.Writer.TryWrite(new LogEnvelope<RawLogLine>(line, IsReplay: false));
    public void Complete() => _channel.Writer.TryComplete();

    public async IAsyncEnumerable<LogEnvelope<RawLogLine>> SubscribeWithReplayMarkerAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var e in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return e;
        }
    }
}
