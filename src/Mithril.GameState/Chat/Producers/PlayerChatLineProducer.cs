using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Mithril.WorldSim;
using Mithril.WorldSim.Chat.Producers;

namespace Mithril.GameState.Chat.Producers;

/// <summary>
/// World-simulator producer that adapts the chat <see cref="IChatLogReplaySource"/>
/// into the world's pull-based <see cref="IFrameProducer{TPayload}"/> contract
/// for the player-chat folder (#603). Aggregates continuation lines into their
/// parent message and classifies the channel; only
/// <see cref="ChatChannelKind.PlayerChat"/> lines yield frame emissions —
/// <see cref="ChatChannelKind.Status"/> + <see cref="ChatChannelKind.NpcChatter"/>
/// (and any other system bucket the classifier later distinguishes) drain at
/// this boundary.
///
/// <para><b>Multi-line aggregation.</b> A logical chat message may span
/// multiple physical lines when embedded entity references (<c>[Item: …]</c>,
/// <c>[Recipe: …]</c>, …) are serialised as continuation lines with no
/// timestamp / channel prefix. The producer carries a small state machine:
/// each <see cref="ChatChannelClassifier.TryParse(string, out ChatLineParts)"/>
/// success flushes any pending message and starts a new one; each parse
/// failure (no prefix) appends to the pending message's text with a literal
/// newline separator. The source-end emits the final pending message
/// (when the enumeration completes); a fresh prefixed line is the only
/// flush signal during the stream.</para>
///
/// <para><b>Per-folder producer pattern (#644).</b> Each chat-side folder
/// owns a producer that re-tails the source and emits a typed payload only
/// when its parser matches. <see cref="ChatLogProducer"/> remains for its
/// banner-side-effect role; the chat-inventory and player-chat producers
/// own their own filtered surfaces.</para>
///
/// <para><b>Mode awareness.</b> <see cref="ReachedLive"/> completes the moment
/// the producer reads the first non-replay envelope from the chat source —
/// same shape as <see cref="ChatLogProducer"/>, irrespective of whether that
/// envelope is itself a player-chat line.</para>
/// </summary>
public sealed class PlayerChatLineProducer
    : IFrameProducer<PlayerChatLineFrame>, IModeAwareFrameProducer<PlayerChatLineFrame>
{
    private readonly IChatLogReplaySource _source;
    private readonly IDiagnosticsSink? _diag;
    private readonly TaskCompletionSource _reachedLive = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PlayerChatLineProducer(
        IChatLogReplaySource source,
        IDiagnosticsSink? diag = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _diag = diag;
    }

    /// <summary>
    /// Priority 0 — matches the other chat-side producers. Distinct frame
    /// types mean producers never share a folder slot.
    /// </summary>
    public int Priority => 0;

    public Task ReachedLive => _reachedLive.Task;

    public async IAsyncEnumerable<Frame<PlayerChatLineFrame>> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<Frame<PlayerChatLineFrame>>(
            new UnboundedChannelOptions { SingleReader = true });

        _diag?.Info("GameState.Chat.PlayerChat",
            "PlayerChatLineProducer subscribing to chat replay source for player-chat lines");

        var pump = Task.Run(async () =>
        {
            // Pending message state. The parts are captured at the moment of
            // the parent prefixed line; continuation lines append to Text.
            DateTimeOffset pendingTimestamp = default;
            ChatLineParts pendingParts = default;
            bool havePending = false;
            ChatChannelKind pendingKind = ChatChannelKind.PlayerChat;

            try
            {
                await foreach (var envelope in _source.SubscribeWithReplayMarkerAsync(ct).ConfigureAwait(false))
                {
                    if (!envelope.IsReplay)
                    {
                        _reachedLive.TrySetResult();
                    }

                    var line = envelope.Payload.Line;
                    if (ChatChannelClassifier.TryParse(line, out var parts))
                    {
                        // A new prefixed line flushes the pending message
                        // and starts a fresh one.
                        if (havePending && pendingKind == ChatChannelKind.PlayerChat)
                        {
                            Flush(channel.Writer, pendingTimestamp, pendingParts);
                        }
                        pendingTimestamp = envelope.Payload.Timestamp;
                        pendingParts = parts;
                        pendingKind = ChatChannelClassifier.Classify(parts.Channel);
                        havePending = true;
                    }
                    else
                    {
                        // Continuation line — append to the pending message's
                        // text with a newline. If no pending message exists
                        // (leading unprefixed line in a freshly-rotated file)
                        // the line is dropped.
                        if (havePending)
                        {
                            var trimmed = line is null ? string.Empty : line.TrimEnd('\r');
                            pendingParts = pendingParts with
                            {
                                Text = pendingParts.Text.Length == 0
                                    ? trimmed
                                    : pendingParts.Text + "\n" + trimmed,
                            };
                        }
                    }
                }

                // Source completed — flush a final pending player-chat message
                // so consumers see every logical message that was in flight.
                if (havePending && pendingKind == ChatChannelKind.PlayerChat)
                {
                    Flush(channel.Writer, pendingTimestamp, pendingParts);
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

    private static void Flush(
        ChannelWriter<Frame<PlayerChatLineFrame>> writer,
        DateTimeOffset timestamp,
        ChatLineParts parts)
    {
        var payload = new PlayerChatLineFrame(parts.Channel, parts.Speaker, parts.Text);
        _ = writer.TryWrite(new Frame<PlayerChatLineFrame>(timestamp, payload));
    }
}
