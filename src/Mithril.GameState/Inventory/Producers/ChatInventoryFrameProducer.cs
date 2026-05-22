using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Mithril.WorldSim;
using Mithril.WorldSim.Chat.Producers;

namespace Mithril.GameState.Inventory.Producers;

/// <summary>
/// World-simulator producer that adapts the chat <see cref="IChatLogReplaySource"/>
/// into the world's pull-based <see cref="IFrameProducer{TPayload}"/> contract
/// for the chat-side inventory folder (#602). Parses every chat line via
/// <see cref="InventoryStatusChatParser"/>; only <c>[Status] X xN added to
/// inventory.</c> matches yield frame emissions.
///
/// <para><b>Per-folder producer pattern (#644).</b> The chat world already
/// has a sibling <see cref="ChatLogProducer"/> that emits
/// <see cref="Frame{RawLogLine}"/> — but the world enforces one folder per
/// payload type, and #603 (Saruman chat-WoP) will own its own filter over the
/// same raw stream. Both folders cannot share the <c>RawLogLine</c> slot, so
/// the per-folder producer pattern ratified by #644 applies: each chat-side
/// folder owns a producer that re-tails the source and emits a typed payload
/// only when its parser matches. <see cref="ChatLogProducer"/> remains for its
/// banner-side-effect role (chat session identification) and for any future
/// folders that want the full <see cref="RawLogLine"/> stream verbatim.</para>
///
/// <para><b>Mode awareness.</b> <see cref="ReachedLive"/> completes the moment
/// the producer reads the first non-replay envelope from the chat source —
/// same shape as <see cref="ChatLogProducer"/>, irrespective of whether that
/// envelope is itself an inventory line. Chat-side inventory observations can
/// be sparse; we mustn't stall the world's mode flip waiting for one.</para>
/// </summary>
public sealed class ChatInventoryFrameProducer
    : IFrameProducer<ChatInventoryObservationFrame>, IModeAwareFrameProducer<ChatInventoryObservationFrame>
{
    private readonly IChatLogReplaySource _source;
    private readonly IDiagnosticsSink? _diag;
    private readonly TaskCompletionSource _reachedLive = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ChatInventoryFrameProducer(
        IChatLogReplaySource source,
        IDiagnosticsSink? diag = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _diag = diag;
    }

    /// <summary>
    /// Producer priority for the chat world's merger tie-breaking. Matches
    /// <see cref="ChatLogProducer"/>'s priority (0) — both producers derive
    /// from the same chat source and share ordering rights. Distinct frame
    /// types mean the two producers never share a folder slot.
    /// </summary>
    public int Priority => 0;

    public Task ReachedLive => _reachedLive.Task;

    public async IAsyncEnumerable<Frame<ChatInventoryObservationFrame>> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<Frame<ChatInventoryObservationFrame>>(
            new UnboundedChannelOptions { SingleReader = true });

        _diag?.Info("GameState.Inventory.Chat",
            "ChatInventoryFrameProducer subscribing to chat replay source for [Status] inventory observations");

        // Pump in a dedicated task so the iterator returns the channel reader
        // immediately. Same shape as SkillFrameProducer's L1 callback path,
        // adapted for the chat source's IAsyncEnumerable surface.
        var pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var envelope in _source.SubscribeWithReplayMarkerAsync(ct).ConfigureAwait(false))
                {
                    if (!envelope.IsReplay)
                    {
                        _reachedLive.TrySetResult();
                    }

                    var parsed = InventoryStatusChatParser.TryParse(envelope.Payload.Line);
                    if (parsed is null) continue;

                    _ = channel.Writer.TryWrite(new Frame<ChatInventoryObservationFrame>(
                        envelope.Payload.Timestamp,
                        new ChatInventoryObservationFrame(parsed.Value.DisplayName, parsed.Value.Count)));
                }
            }
            catch (OperationCanceledException) { /* expected on host stop */ }
            finally
            {
                channel.Writer.TryComplete();
                _reachedLive.TrySetResult();
            }
        }, ct);

        try
        {
            await foreach (var frame in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return frame;
            }
        }
        finally
        {
            try { await pump.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
    }
}
