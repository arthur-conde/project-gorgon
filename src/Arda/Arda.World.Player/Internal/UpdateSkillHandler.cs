using Arda.Abstractions.Logs;
using Arda.Dispatch;

namespace Arda.World.Player.Internal;

/// <summary>
/// Thin dispatch adapter routing <c>ProcessUpdateSkill</c> to <see cref="Player.OnUpdateSkill"/>.
/// </summary>
internal sealed class UpdateSkillHandler(Player player) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
        => player.OnUpdateSkill(args, sourceLog, metadata);
}
