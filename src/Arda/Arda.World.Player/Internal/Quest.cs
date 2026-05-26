using System.Globalization;
using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Simplified quest signal handler. Watches <c>ProcessBook</c> for the
/// <c>"New Quest: &lt;&lt;&lt;quest_NNNNN_Name&gt;&gt;&gt;"</c> pattern and emits
/// <see cref="QuestOffered"/> with the raw quest ID. Only fires for the
/// "New Quest" prefix — other ProcessBook calls (e.g. lore books) are ignored.
/// </summary>
internal sealed class Quest : IFrameHandler
{
    private static ReadOnlySpan<char> NewQuestPrefix => "\"New Quest: <<<quest_";

    private readonly IDomainEventPublisher _bus;

    public Quest(IDomainEventPublisher bus) => _bus = bus;

    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        // Args format: ("New Quest: <<<quest_NNNNN_Name>>>", ...)
        // Fast-path reject: check for the unique prefix
        var inner = SpanHelpers.StripParens(args);
        if (inner.IsEmpty)
            return;

        if (!inner.StartsWith(NewQuestPrefix, StringComparison.Ordinal))
            return;

        // Extract quest ID: everything between "quest_" and "_Name"
        var afterPrefix = inner[NewQuestPrefix.Length..];
        var nameIdx = afterPrefix.IndexOf("_Name");
        if (nameIdx <= 0)
            return;

        var idSpan = afterPrefix[..nameIdx];
        if (!int.TryParse(idSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var questId))
            return;

        _bus.Publish(new QuestOffered(questId, metadata));
    }
}
