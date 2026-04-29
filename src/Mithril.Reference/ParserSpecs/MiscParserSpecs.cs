using System.Collections.Generic;
using System.Linq;
using Mithril.Reference.Models;
using Mithril.Reference.Models.Misc;
using Mithril.Reference.Serialization;

namespace Mithril.Reference.ParserSpecs;

// One file for the Tier-2 misc spec implementations. Each is short — POCO
// + parser entry + count gate; only StorageVault carries an unknown-walker
// because it has a polymorphic Requirements field.

public sealed class XpTableParserSpec : IParserSpec
{
    public string FileName => "xptables.json";
    public int MinimumEntryCount => 50;
    public object Parse(string json) => ReferenceDeserializer.ParseXpTables(json);
    public int CountEntries(object parsed) => ((IReadOnlyDictionary<string, XpTable>)parsed).Count;
    public IEnumerable<UnknownReport> EnumerateUnknowns(object parsed) => Enumerable.Empty<UnknownReport>();
}

public sealed class AreaParserSpec : IParserSpec
{
    public string FileName => "areas.json";
    public int MinimumEntryCount => 30;
    public object Parse(string json) => ReferenceDeserializer.ParseAreas(json);
    public int CountEntries(object parsed) => ((IReadOnlyDictionary<string, Area>)parsed).Count;
    public IEnumerable<UnknownReport> EnumerateUnknowns(object parsed) => Enumerable.Empty<UnknownReport>();
}

public sealed class AttributeParserSpec : IParserSpec
{
    public string FileName => "attributes.json";
    public int MinimumEntryCount => 1800;
    public object Parse(string json) => ReferenceDeserializer.ParseAttributes(json);
    public int CountEntries(object parsed) => ((IReadOnlyDictionary<string, AttributeDef>)parsed).Count;
    public IEnumerable<UnknownReport> EnumerateUnknowns(object parsed) => Enumerable.Empty<UnknownReport>();
}

public sealed class PlayerTitleParserSpec : IParserSpec
{
    public string FileName => "playertitles.json";
    public int MinimumEntryCount => 650;
    public object Parse(string json) => ReferenceDeserializer.ParsePlayerTitles(json);
    public int CountEntries(object parsed) => ((IReadOnlyDictionary<string, PlayerTitle>)parsed).Count;
    public IEnumerable<UnknownReport> EnumerateUnknowns(object parsed) => Enumerable.Empty<UnknownReport>();
}

public sealed class LorebookParserSpec : IParserSpec
{
    public string FileName => "lorebooks.json";
    public int MinimumEntryCount => 60;
    public object Parse(string json) => ReferenceDeserializer.ParseLorebooks(json);
    public int CountEntries(object parsed) => ((IReadOnlyDictionary<string, Lorebook>)parsed).Count;
    public IEnumerable<UnknownReport> EnumerateUnknowns(object parsed) => Enumerable.Empty<UnknownReport>();
}

public sealed class ItemUsesParserSpec : IParserSpec
{
    public string FileName => "itemuses.json";
    public int MinimumEntryCount => 1100;
    public object Parse(string json) => ReferenceDeserializer.ParseItemUses(json);
    public int CountEntries(object parsed) => ((IReadOnlyDictionary<string, ItemUses>)parsed).Count;
    public IEnumerable<UnknownReport> EnumerateUnknowns(object parsed) => Enumerable.Empty<UnknownReport>();
}

public sealed class LorebookInfoParserSpec : IParserSpec
{
    public string FileName => "lorebookinfo.json";

    /// <summary>This file's "envelope" is a single object with one root key (Categories), not a dictionary of entries.</summary>
    public int MinimumEntryCount => 1;

    public object Parse(string json) => ReferenceDeserializer.ParseLorebookInfo(json);

    public int CountEntries(object parsed)
        => ((LorebookInfo)parsed).Categories?.Count ?? 0;

    public IEnumerable<UnknownReport> EnumerateUnknowns(object parsed) => Enumerable.Empty<UnknownReport>();
}

public sealed class TsysProfilesParserSpec : IParserSpec
{
    public string FileName => "tsysprofiles.json";
    public int MinimumEntryCount => 35;
    public object Parse(string json) => ReferenceDeserializer.ParseTsysProfiles(json);
    public int CountEntries(object parsed) => ((IReadOnlyDictionary<string, IReadOnlyList<string>>)parsed).Count;
    public IEnumerable<UnknownReport> EnumerateUnknowns(object parsed) => Enumerable.Empty<UnknownReport>();
}

public sealed class DirectedGoalParserSpec : IParserSpec
{
    public string FileName => "directedgoals.json";
    public int MinimumEntryCount => 40;
    public object Parse(string json) => ReferenceDeserializer.ParseDirectedGoals(json);
    public int CountEntries(object parsed) => ((IReadOnlyList<DirectedGoal>)parsed).Count;
    public IEnumerable<UnknownReport> EnumerateUnknowns(object parsed) => Enumerable.Empty<UnknownReport>();
}

public sealed class LandmarkParserSpec : IParserSpec
{
    public string FileName => "landmarks.json";
    public int MinimumEntryCount => 30;
    public object Parse(string json) => ReferenceDeserializer.ParseLandmarks(json);
    public int CountEntries(object parsed) => ((IReadOnlyDictionary<string, IReadOnlyList<Landmark>>)parsed).Count;
    public IEnumerable<UnknownReport> EnumerateUnknowns(object parsed) => Enumerable.Empty<UnknownReport>();
}

public sealed class SkillParserSpec : IParserSpec
{
    public string FileName => "skills.json";
    public int MinimumEntryCount => 175;
    public object Parse(string json) => ReferenceDeserializer.ParseSkills(json);
    public int CountEntries(object parsed) => ((IReadOnlyDictionary<string, Skill>)parsed).Count;
    public IEnumerable<UnknownReport> EnumerateUnknowns(object parsed) => Enumerable.Empty<UnknownReport>();
}

public sealed class TsysClientInfoParserSpec : IParserSpec
{
    public string FileName => "tsysclientinfo.json";
    public int MinimumEntryCount => 1900;
    public object Parse(string json) => ReferenceDeserializer.ParseTsysClientInfo(json);
    public int CountEntries(object parsed) => ((IReadOnlyDictionary<string, PowerProfile>)parsed).Count;
    public IEnumerable<UnknownReport> EnumerateUnknowns(object parsed) => Enumerable.Empty<UnknownReport>();
}

public sealed class StorageVaultParserSpec : IParserSpec
{
    public string FileName => "storagevaults.json";
    public int MinimumEntryCount => 85;
    public object Parse(string json) => ReferenceDeserializer.ParseStorageVaults(json);
    public int CountEntries(object parsed) => ((IReadOnlyDictionary<string, StorageVault>)parsed).Count;

    public IEnumerable<UnknownReport> EnumerateUnknowns(object parsed)
    {
        var vaults = (IReadOnlyDictionary<string, StorageVault>)parsed;
        foreach (var pair in vaults)
        {
            var key = pair.Key;
            var vault = pair.Value;
            if (vault.Requirements is { } reqs)
                for (var i = 0; i < reqs.Count; i++)
                {
                    if (reqs[i] is IUnknownDiscriminator u)
                        yield return new UnknownReport(
                            $"{key}/Requirements[{i}]",
                            u.DiscriminatorValue,
                            nameof(StorageRequirement));
                }
        }
    }
}
