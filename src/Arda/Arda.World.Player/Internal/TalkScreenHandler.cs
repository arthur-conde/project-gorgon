using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Tier 2 passthrough marker handler for <c>ProcessTalkScreen</c>.
/// Emits <see cref="TalkScreenFrame"/> as a bracket-discrimination signal.
/// Primary consumer: Gandalf (LootBracketTracker — TalkScreen inside a bracket means "not loot").
/// </summary>
internal sealed class TalkScreenHandler(IDomainEventPublisher bus) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        bus.Publish(new TalkScreenFrame(metadata));
    }
}
