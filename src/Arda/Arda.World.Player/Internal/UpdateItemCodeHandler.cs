using Arda.Abstractions.Logs;
using Arda.Dispatch;

namespace Arda.World.Player.Internal;

/// <summary>
/// Thin dispatch adapter routing <c>ProcessUpdateItemCode</c> to <see cref="Inventory.OnUpdateItemCode"/>.
/// </summary>
internal sealed class UpdateItemCodeHandler(Inventory inventory) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
        => inventory.OnUpdateItemCode(args, sourceLog, metadata);
}
