using System.Collections.Generic;
using System.Linq;
using Mithril.Reference.Models;
using Mithril.Reference.Models.Abilities;
using Mithril.Reference.Models.Effects;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Misc;
using Mithril.Reference.Serialization;

namespace Mithril.Reference.ParserSpecs;

public sealed class AbilityParserSpec : IParserSpec
{
    public string FileName => "abilities.json";
    public int MinimumEntryCount => 5800;
    public object Parse(string json) => ReferenceDeserializer.ParseAbilities(json);
    public int CountEntries(object parsed) => ((IReadOnlyDictionary<string, Ability>)parsed).Count;

    public IEnumerable<UnknownReport> EnumerateUnknowns(object parsed)
    {
        var abilities = (IReadOnlyDictionary<string, Ability>)parsed;
        foreach (var pair in abilities)
        {
            var key = pair.Key;
            var ability = pair.Value;
            if (ability.SpecialCasterRequirements is { } reqs)
                for (var i = 0; i < reqs.Count; i++)
                {
                    if (reqs[i] is IUnknownDiscriminator u)
                        yield return new UnknownReport(
                            $"{key}/SpecialCasterRequirements[{i}]",
                            u.DiscriminatorValue,
                            nameof(AbilitySpecialCasterRequirement));
                }
        }
    }
}

public sealed class EffectParserSpec : IParserSpec
{
    public string FileName => "effects.json";
    public int MinimumEntryCount => 22000;
    public object Parse(string json) => ReferenceDeserializer.ParseEffects(json);
    public int CountEntries(object parsed) => ((IReadOnlyDictionary<string, Effect>)parsed).Count;
    public IEnumerable<UnknownReport> EnumerateUnknowns(object parsed) => Enumerable.Empty<UnknownReport>();
}

public sealed class ItemsRawParserSpec : IParserSpec
{
    public string FileName => "items_raw.json";
    public int MinimumEntryCount => 10500;
    public object Parse(string json) => ReferenceDeserializer.ParseItemsRaw(json);
    public int CountEntries(object parsed) => ((IReadOnlyDictionary<string, Item>)parsed).Count;
    public IEnumerable<UnknownReport> EnumerateUnknowns(object parsed) => Enumerable.Empty<UnknownReport>();
}

public sealed class AdvancementTableParserSpec : IParserSpec
{
    public string FileName => "advancementtables.json";
    public int MinimumEntryCount => 400;
    public object Parse(string json) => ReferenceDeserializer.ParseAdvancementTables(json);

    public int CountEntries(object parsed)
        => ((IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>)parsed).Count;

    public IEnumerable<UnknownReport> EnumerateUnknowns(object parsed) => Enumerable.Empty<UnknownReport>();
}

public sealed class AiParserSpec : IParserSpec
{
    public string FileName => "ai.json";
    public int MinimumEntryCount => 400;
    public object Parse(string json) => ReferenceDeserializer.ParseAi(json);
    public int CountEntries(object parsed) => ((IReadOnlyDictionary<string, AiBehavior>)parsed).Count;
    public IEnumerable<UnknownReport> EnumerateUnknowns(object parsed) => Enumerable.Empty<UnknownReport>();
}

public sealed class AbilityKeywordParserSpec : IParserSpec
{
    public string FileName => "abilitykeywords.json";
    public int MinimumEntryCount => 25;
    public object Parse(string json) => ReferenceDeserializer.ParseAbilityKeywords(json);
    public int CountEntries(object parsed) => ((IReadOnlyList<AbilityKeyword>)parsed).Count;
    public IEnumerable<UnknownReport> EnumerateUnknowns(object parsed) => Enumerable.Empty<UnknownReport>();
}

public sealed class AbilityDynamicDotParserSpec : IParserSpec
{
    public string FileName => "abilitydynamicdots.json";
    public int MinimumEntryCount => 2;
    public object Parse(string json) => ReferenceDeserializer.ParseAbilityDynamicDots(json);
    public int CountEntries(object parsed) => ((IReadOnlyList<AbilityDynamicDot>)parsed).Count;
    public IEnumerable<UnknownReport> EnumerateUnknowns(object parsed) => Enumerable.Empty<UnknownReport>();
}

public sealed class AbilityDynamicSpecialValueParserSpec : IParserSpec
{
    public string FileName => "abilitydynamicspecialvalues.json";
    public int MinimumEntryCount => 4;
    public object Parse(string json) => ReferenceDeserializer.ParseAbilityDynamicSpecialValues(json);
    public int CountEntries(object parsed) => ((IReadOnlyList<AbilityDynamicSpecialValue>)parsed).Count;
    public IEnumerable<UnknownReport> EnumerateUnknowns(object parsed) => Enumerable.Empty<UnknownReport>();
}

public sealed class StringsAllParserSpec : IParserSpec
{
    public string FileName => "strings_all.json";
    public int MinimumEntryCount => 175000;
    public object Parse(string json) => ReferenceDeserializer.ParseStringsAll(json);
    public int CountEntries(object parsed) => ((IReadOnlyDictionary<string, string>)parsed).Count;
    public IEnumerable<UnknownReport> EnumerateUnknowns(object parsed) => Enumerable.Empty<UnknownReport>();
}
