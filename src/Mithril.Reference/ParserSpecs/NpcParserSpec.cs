using System.Collections.Generic;
using Mithril.Reference.Models;
using Mithril.Reference.Models.Npcs;
using Mithril.Reference.Serialization;

namespace Mithril.Reference.ParserSpecs;

/// <summary>
/// <see cref="IParserSpec"/> for <c>npcs.json</c>.
/// </summary>
public sealed class NpcParserSpec : IParserSpec
{
    public string FileName => "npcs.json";

    /// <summary>Bundled file shipped 338 NPCs; floor leaves a small headroom.</summary>
    public int MinimumEntryCount => 320;

    public object Parse(string json) => ReferenceDeserializer.ParseNpcs(json);

    public int CountEntries(object parsed)
        => ((IReadOnlyDictionary<string, Npc>)parsed).Count;

    public IEnumerable<UnknownReport> EnumerateUnknowns(object parsed)
    {
        var npcs = (IReadOnlyDictionary<string, Npc>)parsed;
        foreach (var pair in npcs)
        {
            var key = pair.Key;
            var npc = pair.Value;
            if (npc.Services is { } services)
                for (var i = 0; i < services.Count; i++)
                {
                    if (services[i] is IUnknownDiscriminator u)
                        yield return new UnknownReport(
                            $"{key}/Services[{i}]",
                            u.DiscriminatorValue,
                            nameof(NpcService));
                }
        }
    }
}
