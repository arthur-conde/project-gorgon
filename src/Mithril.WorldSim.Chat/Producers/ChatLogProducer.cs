using System.Runtime.CompilerServices;
using Mithril.Shared.Logging;
using Mithril.WorldSim.Chat.Internal;

namespace Mithril.WorldSim.Chat.Producers;

/// <summary>
/// Bridges <see cref="IChatLogReplaySource"/> into the world simulator as an
/// <see cref="IFrameProducer{TPayload}"/> over <see cref="RawLogLine"/>. The
/// replay-vs-live boundary on the envelope's <see cref="LogEnvelope{T}.IsReplay"/>
/// flag drives the chat world's <see cref="WorldMode"/> transition via
/// <see cref="IModeAwareFrameProducer{TPayload}"/> — <see cref="ReachedLive"/>
/// completes the moment the producer reads the first non-replay envelope from
/// the underlying source.
///
/// <para><b>Banner side-effect.</b> As envelopes flow through, the producer
/// parses each line via <see cref="ChatLoginBannerParser"/> and updates the
/// injected <see cref="ChatSessionService"/> on every hit. This makes the
/// chat-side <c>(Server, Character)</c> scope (principle 7) observable via
/// <see cref="IChatSessionService"/> without a Phase 1+ folder needing to land
/// first. Banner observations are mode-agnostic — replay banners update
/// <see cref="IChatSessionService.Current"/> identically to live banners
/// (principle 12 — state derivation is mode-agnostic; only side-effect-
/// emitting consumers gate on Live).</para>
///
/// <para>The frame's payload is the <see cref="RawLogLine"/> itself; future
/// chat folders (chat-inventory mirror #602, chat-WoP-spent #603) consume
/// <see cref="Frame{TPayload}"/> for <see cref="RawLogLine"/> and dispatch by
/// inspecting <see cref="RawLogLine.Line"/>. Phase 0 ships no folders so
/// frames flow through the merger / clock only.</para>
/// </summary>
public sealed class ChatLogProducer : IFrameProducer<RawLogLine>, IModeAwareFrameProducer<RawLogLine>
{
    private readonly IChatLogReplaySource _source;
    private readonly ChatSessionService _session;
    private readonly TaskCompletionSource _reachedLive = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ChatLogProducer(IChatLogReplaySource source, ChatSessionService session)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <summary>
    /// Public ctor for DI — resolves the concrete session service via
    /// <see cref="IChatSessionService"/> downcast (the DI extension registers
    /// the same instance under both interfaces, so the downcast is safe by
    /// construction).
    /// </summary>
    public ChatLogProducer(IChatLogReplaySource source, IChatSessionService session)
        : this(source, AsConcrete(session))
    {
    }

    private static ChatSessionService AsConcrete(IChatSessionService session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session is ChatSessionService concrete) return concrete;
        throw new InvalidOperationException(
            $"ChatLogProducer requires the concrete {nameof(ChatSessionService)} " +
            "(which is what the DI extension registers under IChatSessionService). " +
            "A custom IChatSessionService implementation isn't supported — the " +
            "producer needs write access via the concrete service's Update method.");
    }

    /// <summary>
    /// The single producer registered on the chat world today carries priority 0.
    /// Future producers (e.g., a chat-side filesystem-reconcile producer) would
    /// declare strictly-higher numeric priorities so this producer wins
    /// timestamp ties for chat-line-derived events.
    /// </summary>
    public int Priority => 0;

    public Task ReachedLive => _reachedLive.Task;

    public async IAsyncEnumerable<Frame<RawLogLine>> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var envelope in _source.SubscribeWithReplayMarkerAsync(ct).ConfigureAwait(false))
        {
            // Signal "reached live" BEFORE yielding the first live frame so
            // the world flips Mode → Live before dispatching it. The source
            // contract guarantees IsReplay flips false-onward only once and
            // never re-arms, so TrySetResult is idempotent past that boundary.
            if (!envelope.IsReplay)
            {
                _reachedLive.TrySetResult();
            }

            // Update session service on banner observations — both replay
            // (so consumers observing replay drain see scope updates) and
            // live (so PG re-login mid-session refreshes scope).
            if (ChatLoginBannerParser.TryParse(envelope.Payload.Line, out var banner))
            {
                _session.Update(new ChatSession(
                    Server: banner.Server,
                    Character: banner.Character,
                    At: envelope.Payload.Timestamp,
                    Offset: banner.Offset));
            }

            yield return new Frame<RawLogLine>(
                envelope.Payload.Timestamp,
                envelope.Payload);
        }

        // Source ended without ever flipping (degenerate — replay-only file
        // tail with no live channel). Mark live anyway so the world isn't
        // stuck in Replaying forever after exhaustion.
        _reachedLive.TrySetResult();
    }
}
