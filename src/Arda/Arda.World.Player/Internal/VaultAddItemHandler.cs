using Arda.Abstractions.Logs;
using Arda.Dispatch;

namespace Arda.World.Player.Internal;

/// <summary>
/// Thin dispatch adapter routing <c>ProcessAddItem</c> to <see cref="Vault.OnAddItem"/>
/// for vault withdrawal correlation. Registered after <see cref="AddItemHandler"/>
/// (Inventory runs first, then Vault stashes the pending withdrawal candidate).
/// </summary>
internal sealed class VaultAddItemHandler(Vault vault) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
        => vault.OnAddItem(args, sourceLog, metadata);
}
