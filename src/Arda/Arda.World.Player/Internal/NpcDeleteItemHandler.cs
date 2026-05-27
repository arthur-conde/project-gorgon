using Arda.Abstractions.Logs;
using Arda.Dispatch;

namespace Arda.World.Player.Internal;

/// <summary>
/// Thin dispatch adapter routing <c>ProcessDeleteItem</c> to <see cref="Npc.OnDeleteItem"/>
/// for gift correlation. Registered alongside <see cref="DeleteItemHandler"/> (Inventory)
/// — multiple handlers per verb is explicitly supported by the dispatch model.
/// </summary>
internal sealed class NpcDeleteItemHandler(Npc npc) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
        => npc.OnDeleteItem(args, sourceLog, metadata);
}
