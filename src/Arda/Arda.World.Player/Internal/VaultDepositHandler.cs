using Arda.Abstractions.Logs;
using Arda.Dispatch;

namespace Arda.World.Player.Internal;

/// <summary>
/// Thin dispatch adapter routing <c>ProcessAddToStorageVault</c> to
/// <see cref="Vault.OnAddToStorageVault"/>.
/// </summary>
internal sealed class VaultDepositHandler(Vault vault) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
        => vault.OnAddToStorageVault(args, verb, sourceLog, metadata);
}
