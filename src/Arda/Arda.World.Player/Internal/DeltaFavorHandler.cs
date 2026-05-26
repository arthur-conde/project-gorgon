using Arda.Abstractions.Logs;
using Arda.Dispatch;

namespace Arda.World.Player.Internal;

/// <summary>
/// Thin dispatch adapter routing <c>ProcessDeltaFavor</c> to <see cref="Npc.OnDeltaFavor"/>
/// for gift correlation. Registered alongside other Npc-routed handlers
/// (<see cref="StartInteractionHandler"/>, <see cref="NpcDeleteItemHandler"/>).
/// </summary>
internal sealed class DeltaFavorHandler(Npc npc) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
        => npc.OnDeltaFavor(args, sourceLog, metadata);
}
