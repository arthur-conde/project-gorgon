using Arda.Abstractions.Logs;
using Arda.Dispatch;

namespace Arda.World.Player.Internal;

/// <summary>
/// Thin dispatch adapter routing <c>ProcessDeleteItem</c> to <see cref="Inventory.OnDeleteItem"/>.
/// </summary>
internal sealed class DeleteItemHandler(Inventory inventory) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
        => inventory.OnDeleteItem(args, sourceLog, metadata);
}
