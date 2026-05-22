namespace Mithril.WorldSim.Chat;

/// <summary>
/// Read-side surface for the chat world's self-identified <see cref="ChatSession"/>
/// — the <c>(Server, Character)</c> pair derived from the most recent chat banner
/// observed by the world's producer (principle 7 — chat self-scopes independently
/// of Player.log via its own intra-source signals). Updated by
/// <see cref="Producers.ChatLogProducer"/> as banner lines flow through; consumers
/// read <see cref="Current"/> for snapshot state or subscribe via
/// <see cref="Subscribe"/> for change notifications.
///
/// <para>This is the Phase 0 chat-side analog of <c>IGameSessionService</c> on
/// the Player.log world. Mode-agnostic — banners observed during the world's
/// <see cref="WorldMode.Replaying"/> drain update <see cref="Current"/>
/// identically to live banners (state derivation is mode-agnostic per
/// principle 12; only side-effect-emitting consumers gate on
/// <see cref="WorldMode.Live"/>).</para>
/// </summary>
public interface IChatSessionService
{
    /// <summary>
    /// Most recently observed chat session, or <c>null</c> if no banner has
    /// been observed since attach. <c>null</c> is also the value before the
    /// world's producer starts emitting frames.
    /// </summary>
    ChatSession? Current { get; }

    /// <summary>
    /// Subscribe to <see cref="Current"/> changes. The handler fires on every
    /// new banner observation — including re-banner observations for the same
    /// <c>(Server, Character)</c> when PG re-logs into the same character mid-
    /// session (the timestamps differ so the <see cref="ChatSession"/> records
    /// are distinct). Handlers fire synchronously on the world's dispatch
    /// thread; subscribers must not block.
    /// </summary>
    IDisposable Subscribe(Action<ChatSession> handler);
}
