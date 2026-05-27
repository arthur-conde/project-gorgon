using Arda.Abstractions.Logs;
using Arda.Dispatch;

namespace Arda.World.Player.Internal;

/// <summary>
/// Thin dispatch adapter routing <c>ProcessVendorScreen</c> to
/// <see cref="Npc.OnVendorScreen"/> for NPC-key enrichment.
/// </summary>
internal sealed class VendorScreenHandler(Npc npc) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
        => npc.OnVendorScreen(args, verb, sourceLog, metadata);
}
