using Arda.Abstractions.Logs;
using Arda.Dispatch;

namespace Arda.World.Player.Internal;

/// <summary>
/// Thin dispatch adapter routing <c>ProcessEndInteraction</c> to
/// <see cref="Npc.OnEndInteraction"/>. Registered before the passthrough
/// <see cref="EndInteractionHandler"/> so subscribers see <c>InteractionEnded</c>
/// against a cleaned Npc state.
/// </summary>
internal sealed class NpcEndInteractionHandler(Npc npc) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
        => npc.OnEndInteraction(args, sourceLog, metadata);
}
