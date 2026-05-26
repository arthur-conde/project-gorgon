using System.Globalization;
using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Stateful quest journal handler. Maintains the player's active quest
/// dictionary. Handles its own verbs:
/// <list type="bullet">
/// <item><c>ProcessBook("New Quest: ...")</c> — single quest accept</item>
/// <item><c>ProcessLoadQuests</c> — bulk snapshot replace on login/zone transition</item>
/// <item><c>ProcessCompleteQuest</c> — single quest removed on turn-in</item>
/// </list>
/// Session-scoped — resets on character switch.
/// </summary>
internal sealed class Quest : IFrameHandler, IQuestState
{
    private static ReadOnlySpan<char> NewQuestPrefix => "\"New Quest: <<<quest_";

    private readonly IDomainEventPublisher _bus;
    private readonly Dictionary<int, QuestEntry> _quests = new();

    public IReadOnlyDictionary<int, QuestEntry> ActiveQuests => _quests;

    public Quest(IDomainEventPublisher bus) => _bus = bus;

    internal IFrameHandler LoadQuestsHandler => new LoadQuestsVerb(this);
    internal IFrameHandler CompleteQuestHandler => new CompleteQuestVerb(this);

    internal void Reset() => _quests.Clear();

    /// <summary>
    /// Handles <c>ProcessBook</c> — extracts quest ID from "New Quest" prefix.
    /// </summary>
    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        var inner = SpanHelpers.StripParens(args);
        if (inner.IsEmpty)
            return;

        if (!inner.StartsWith(NewQuestPrefix, StringComparison.Ordinal))
            return;

        var afterPrefix = inner[NewQuestPrefix.Length..];
        var nameIdx = afterPrefix.IndexOf("_Name");
        if (nameIdx <= 0)
            return;

        var idSpan = afterPrefix[..nameIdx];
        if (!int.TryParse(idSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var questId))
            return;

        var ts = metadata.Timestamp ?? metadata.ReadOn;
        _quests[questId] = new QuestEntry(questId, ts);

        _bus.Publish(new QuestOffered(questId, metadata));
        _bus.Publish(new QuestAccepted(questId, metadata));
    }

    /// <summary>
    /// <c>ProcessLoadQuests(charEntityId, TransitionalQuestState[], [workOrderIds,...], [regularIds,...])</c>
    /// </summary>
    private void OnLoadQuests(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        _quests.Clear();

        var tok = new ArgTokenizer(args);
        tok.SkipOpen();
        tok.Skip(1); // charEntityId
        tok.Skip(1); // TransitionalQuestState[]

        var workOrderIds = tok.NextBracketedSpan();
        var regularIds = tok.NextBracketedSpan();

        var ts = metadata.Timestamp ?? metadata.ReadOn;

        ParseAndAddQuests(workOrderIds, ts);
        ParseAndAddQuests(regularIds, ts);

        _bus.Publish(new QuestsLoaded(_quests.Count, metadata));
    }

    /// <summary>
    /// <c>ProcessCompleteQuest(charEntityId, questId)</c>
    /// </summary>
    private void OnCompleteQuest(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        var tok = new ArgTokenizer(args);
        tok.SkipOpen();
        tok.Skip(1); // charEntityId

        var questId = tok.NextInt();
        _quests.Remove(questId);
        _bus.Publish(new QuestCompleted(questId, metadata));
    }

    private void ParseAndAddQuests(ReadOnlySpan<char> span, DateTimeOffset ts)
    {
        foreach (var range in span.Split(','))
        {
            var token = span[range].Trim();
            if (token.IsEmpty) continue;
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                _quests[id] = new QuestEntry(id, ts);
        }
    }

    private sealed class LoadQuestsVerb(Quest owner) : IFrameHandler
    {
        public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
            => owner.OnLoadQuests(args, sourceLog, metadata);
    }

    private sealed class CompleteQuestVerb(Quest owner) : IFrameHandler
    {
        public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
            => owner.OnCompleteQuest(args, sourceLog, metadata);
    }
}
