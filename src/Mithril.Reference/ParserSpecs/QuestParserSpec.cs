using System.Collections.Generic;
using Mithril.Reference.Models;
using Mithril.Reference.Models.Quests;
using Mithril.Reference.Serialization;

namespace Mithril.Reference.ParserSpecs;

/// <summary>
/// <see cref="IParserSpec"/> for <c>quests.json</c>. Discovered by
/// <see cref="ParserRegistry"/> via reflection — no test boilerplate needed.
/// </summary>
public sealed class QuestParserSpec : IParserSpec
{
    public string FileName => "quests.json";

    /// <summary>
    /// Bundled file shipped 2981 quests. Set the floor lower so a few additions
    /// don't fail the gate, but high enough that silent truncation by a buggy
    /// converter trips the test.
    /// </summary>
    public int MinimumEntryCount => 2900;

    public object Parse(string json) => ReferenceDeserializer.ParseQuests(json);

    public int CountEntries(object parsed)
        => ((IReadOnlyDictionary<string, Quest>)parsed).Count;

    public IEnumerable<UnknownReport> EnumerateUnknowns(object parsed)
    {
        var quests = (IReadOnlyDictionary<string, Quest>)parsed;
        foreach (var pair in quests)
        {
            var key = pair.Key;
            var quest = pair.Value;
            if (quest.Requirements is { } topReqs)
                foreach (var unknown in WalkRequirements(topReqs, $"{key}/Requirements"))
                    yield return unknown;

            if (quest.RequirementsToSustain is { } sustainReqs)
                foreach (var unknown in WalkRequirements(sustainReqs, $"{key}/RequirementsToSustain"))
                    yield return unknown;

            if (quest.Rewards is { } rewards)
                for (var i = 0; i < rewards.Count; i++)
                {
                    if (rewards[i] is IUnknownDiscriminator u)
                        yield return new UnknownReport(
                            $"{key}/Rewards[{i}]",
                            u.DiscriminatorValue,
                            nameof(QuestReward));
                }

            if (quest.Objectives is { } objectives)
                for (var i = 0; i < objectives.Count; i++)
                {
                    if (objectives[i].Requirements is { } objReqs)
                        foreach (var unknown in WalkRequirements(
                            objReqs, $"{key}/Objectives[{i}]/Requirements"))
                            yield return unknown;
                }
        }
    }

    private static IEnumerable<UnknownReport> WalkRequirements(
        IReadOnlyList<QuestRequirement> requirements,
        string pathPrefix)
    {
        for (var i = 0; i < requirements.Count; i++)
        {
            var req = requirements[i];
            var path = $"{pathPrefix}[{i}]";

            if (req is IUnknownDiscriminator u)
                yield return new UnknownReport(path, u.DiscriminatorValue, nameof(QuestRequirement));

            // Nested Or-list requirements
            if (req is OrRequirement or && or.List is { } orList)
                foreach (var unknown in WalkRequirements(orList, $"{path}/List"))
                    yield return unknown;
        }
    }
}
