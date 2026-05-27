using Arda.Abstractions.Logs;
using Arda.Dispatch;

namespace Arda.World.Player.Internal;

/// <summary>
/// Thin dispatch adapter routing <c>ProcessRemoveFromStorageVault</c> to
/// <see cref="Vault.OnRemoveFromStorageVault"/>.
/// </summary>
internal sealed class VaultWithdrawHandler(Vault vault) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
        => vault.OnRemoveFromStorageVault(args, verb, sourceLog, metadata);
}
