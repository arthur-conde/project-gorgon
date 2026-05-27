using Arda.Abstractions.Logs;
using Arda.Dispatch;

namespace Arda.World.Player.Internal;

/// <summary>
/// Thin dispatch adapter routing <c>ProcessShowStorageVault</c> to <see cref="Vault.OnShowStorageVault"/>.
/// </summary>
internal sealed class VaultShowHandler(Vault vault) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
        => vault.OnShowStorageVault(args, verb, sourceLog, metadata);
}
