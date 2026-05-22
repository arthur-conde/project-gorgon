using Mithril.WorldSim;

namespace Mithril.GameState.Chat;

/// <summary>
/// Passthrough folder for <see cref="PlayerChatLineFrame"/> frames (#603).
/// Surfaces every applied frame on the ChatWorld bus as
/// <see cref="ChatPlayerLineObserved"/>. No state retained — the upstream
/// producer has already filtered to <see cref="ChatChannelKind.PlayerChat"/>
/// and aggregated continuations, so the folder is a thin re-emit.
///
/// <para>Per #644's per-folder producer pattern: the sibling
/// <see cref="Producers.PlayerChatLineProducer"/> re-tails the chat source and
/// emits this folder's typed frames; the folder routes them onto the world's
/// bus through <see cref="Apply"/>'s returned change events. The view layer
/// (<see cref="WordsOfPower.WordOfPowerView"/>) subscribes to the
/// <see cref="ChatPlayerLineObserved"/> channel for chat-side spent-code
/// detection.</para>
/// </summary>
public sealed class PlayerChatLineLogService : IFolder<PlayerChatLineFrame>, IPlayerChatLineLog
{
    public IReadOnlyList<IChangeEvent> Apply(Frame<PlayerChatLineFrame> frame, IWorldClock clock)
    {
        _ = clock;
        var p = frame.Payload;
        return new IChangeEvent[]
        {
            new ChatPlayerLineObserved(p.Channel, p.Speaker, p.Text, frame.Timestamp.UtcDateTime),
        };
    }
}
