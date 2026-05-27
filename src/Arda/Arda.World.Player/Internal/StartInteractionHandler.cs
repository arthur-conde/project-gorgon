using Arda.Abstractions.Logs;
using Arda.Dispatch;

namespace Arda.World.Player.Internal;

/// <summary>
/// Thin dispatch adapter routing <c>ProcessStartInteraction</c> to <see cref="Npc.OnStartInteraction"/>.
/// </summary>
internal sealed class StartInteractionHandler(Npc npc) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
        => npc.OnStartInteraction(args, verb, sourceLog, metadata);
}
