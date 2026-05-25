using Arda.Abstractions.Logs;
using Arda.Dispatch;

namespace Arda.World.Player.Internal;

/// <summary>
/// Thin dispatch adapter routing <c>ProcessLoadSkills</c> to <see cref="Player.OnLoadSkills"/>.
/// </summary>
internal sealed class LoadSkillsHandler(Player player) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
        => player.OnLoadSkills(args, sourceLog, metadata);
}
