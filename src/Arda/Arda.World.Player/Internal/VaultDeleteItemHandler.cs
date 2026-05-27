using Arda.Abstractions.Logs;
using Arda.Dispatch;

namespace Arda.World.Player.Internal;

/// <summary>
/// Thin dispatch adapter routing <c>ProcessDeleteItem</c> to <see cref="Vault.OnDeleteItem"/>
/// for vault deposit correlation. Registered after <see cref="DeleteItemHandler"/> and
/// <see cref="NpcDeleteItemHandler"/> (Inventory and NPC run first).
/// </summary>
internal sealed class VaultDeleteItemHandler(Vault vault) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
        => vault.OnDeleteItem(args, sourceLog, metadata);
}
