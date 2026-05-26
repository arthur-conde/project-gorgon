using Arda.Abstractions.Logs;
using Arda.Dispatch;

namespace Arda.World.Player.Internal;

/// <summary>
/// Thin dispatch adapter routing <c>ProcessVendorAddItem</c> to
/// <see cref="Npc.OnVendorAddItem"/> for NPC-key and favor-tier enrichment.
/// </summary>
internal sealed class VendorAddItemHandler(Npc npc) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
        => npc.OnVendorAddItem(args, sourceLog, metadata);
}
