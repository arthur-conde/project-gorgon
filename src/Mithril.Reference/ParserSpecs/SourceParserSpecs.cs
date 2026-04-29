using System.Collections.Generic;
using Mithril.Reference.Models;
using Mithril.Reference.Models.Sources;
using Mithril.Reference.Serialization;

namespace Mithril.Reference.ParserSpecs;

/// <summary>
/// Shared base for the three sources_*.json files. Each subclass declares
/// just the file name and minimum entry count; parse + unknown-walk behaviour
/// is identical across them.
/// </summary>
public abstract class SourcesParserSpecBase : IParserSpec
{
    public abstract string FileName { get; }
    public abstract int MinimumEntryCount { get; }

    public object Parse(string json) => ReferenceDeserializer.ParseSources(json);

    public int CountEntries(object parsed)
        => ((IReadOnlyDictionary<string, SourceEnvelope>)parsed).Count;

    public IEnumerable<UnknownReport> EnumerateUnknowns(object parsed)
    {
        var envelopes = (IReadOnlyDictionary<string, SourceEnvelope>)parsed;
        foreach (var pair in envelopes)
        {
            var key = pair.Key;
            var envelope = pair.Value;
            if (envelope.entries is { } entries)
                for (var i = 0; i < entries.Count; i++)
                {
                    if (entries[i] is IUnknownDiscriminator u)
                        yield return new UnknownReport(
                            $"{key}/entries[{i}]",
                            u.DiscriminatorValue,
                            nameof(SourceEntry));
                }
        }
    }
}

public sealed class SourcesItemsParserSpec : SourcesParserSpecBase
{
    public override string FileName => "sources_items.json";
    public override int MinimumEntryCount => 9500;  // bundled: 9612
}

public sealed class SourcesRecipesParserSpec : SourcesParserSpecBase
{
    public override string FileName => "sources_recipes.json";
    public override int MinimumEntryCount => 4300;  // bundled: 4388
}

public sealed class SourcesAbilitiesParserSpec : SourcesParserSpecBase
{
    public override string FileName => "sources_abilities.json";
    public override int MinimumEntryCount => 3900;  // bundled: 4032
}
