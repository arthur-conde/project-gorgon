using Arda.Abstractions.Logs;
using Arda.Dispatch;

namespace Arda.World.Player.Internal;

/// <summary>
/// Side-channel notifying <see cref="Npc"/> that a vault session has opened
/// so item deletes are treated as deposits, not gift attempts. Registered
/// alongside <see cref="VaultShowHandler"/>.
/// </summary>
internal sealed class NpcVaultOpenedHandler(Npc npc) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
        => npc.OnVaultOpened();
}
