using System.IO;
using System.Net.Http;
using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Reference;

[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class ReferenceDataServiceAbilityCrossLinkIndexTests : IDisposable
{
    private readonly string _root;
    private readonly string _cacheDir;
    private readonly string _bundledDir;

    public ReferenceDataServiceAbilityCrossLinkIndexTests()
    {
        _root = Mithril.TestSupport.TestPaths.CreateTempDir("mithril-ref-ability-crosslink-tests");
        _cacheDir = Path.Combine(_root, "cache");
        _bundledDir = Path.Combine(_root, "bundled");
        Directory.CreateDirectory(_cacheDir);
        Directory.CreateDirectory(_bundledDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static HttpClient NeverCallHttp() => new(new ThrowingHandler());

    private void WriteFixture(string abilitiesJson, string? sourcesAbilitiesJson = null, string? npcsJson = null)
    {
        File.WriteAllText(Path.Combine(_bundledDir, "abilities.json"), abilitiesJson);
        File.WriteAllText(Path.Combine(_bundledDir, "abilities.meta.json"), "{\"cdnVersion\":\"v1\",\"source\":0}");
        if (sourcesAbilitiesJson is not null)
        {
            File.WriteAllText(Path.Combine(_bundledDir, "sources_abilities.json"), sourcesAbilitiesJson);
            File.WriteAllText(Path.Combine(_bundledDir, "sources_abilities.meta.json"), "{\"cdnVersion\":\"v1\",\"source\":0}");
        }
        if (npcsJson is not null)
        {
            File.WriteAllText(Path.Combine(_bundledDir, "npcs.json"), npcsJson);
            File.WriteAllText(Path.Combine(_bundledDir, "npcs.meta.json"), "{\"cdnVersion\":\"v1\",\"source\":0}");
        }
    }

    [Fact]
    public void AbilitiesBySkill_GroupsByAbilitySkill()
    {
        WriteFixture(
            abilitiesJson: """
            {
              "ability_1": { "InternalName": "SwordSlash", "Name": "Sword Slash", "Skill": "Sword", "Level": 1, "IconID": 1 },
              "ability_2": { "InternalName": "SwordParry", "Name": "Sword Parry", "Skill": "Sword", "Level": 2, "IconID": 2 },
              "ability_3": { "InternalName": "Punch", "Name": "Punch", "Skill": "Unarmed", "Level": 1, "IconID": 3 }
            }
            """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.AbilitiesBySkill.Should().ContainKey("Sword");
        svc.AbilitiesBySkill["Sword"].Should().HaveCount(2);
        svc.AbilitiesBySkill["Sword"].Select(a => a.InternalName)
            .Should().BeEquivalentTo(["SwordSlash", "SwordParry"]);
        svc.AbilitiesBySkill["Unarmed"].Should().ContainSingle()
            .Which.InternalName.Should().Be("Punch");
    }

    [Fact]
    public void AbilitiesUpgradingFrom_IndexesByUpgradeOfInternalName()
    {
        WriteFixture(
            abilitiesJson: """
            {
              "ability_1": { "InternalName": "SwordSlash", "Name": "Sword Slash", "Skill": "Sword", "Level": 1, "IconID": 1 },
              "ability_2": { "InternalName": "SwordSlash2", "Name": "Sword Slash 2", "Skill": "Sword", "Level": 12, "IconID": 2, "UpgradeOf": "SwordSlash" },
              "ability_3": { "InternalName": "SwordSlash3", "Name": "Sword Slash 3", "Skill": "Sword", "Level": 25, "IconID": 3, "UpgradeOf": "SwordSlash" }
            }
            """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.AbilitiesUpgradingFrom.Should().ContainKey("SwordSlash");
        svc.AbilitiesUpgradingFrom["SwordSlash"].Should().HaveCount(2);
        svc.AbilitiesUpgradingFrom["SwordSlash"].Select(a => a.InternalName)
            .Should().BeEquivalentTo(["SwordSlash2", "SwordSlash3"]);
    }

    [Fact]
    public void AbilitiesInGroup_IndexesByAbilityGroup()
    {
        WriteFixture(
            abilitiesJson: """
            {
              "ability_1": { "InternalName": "ManyCuts", "Name": "Many Cuts", "Skill": "Sword", "Level": 3, "IconID": 1, "AbilityGroup": "ManyCutsGroup" },
              "ability_2": { "InternalName": "ManyCuts2", "Name": "Many Cuts 2", "Skill": "Sword", "Level": 15, "IconID": 2, "AbilityGroup": "ManyCutsGroup" },
              "ability_3": { "InternalName": "Punch", "Name": "Punch", "Skill": "Unarmed", "Level": 1, "IconID": 3 }
            }
            """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.AbilitiesInGroup.Should().ContainKey("ManyCutsGroup");
        svc.AbilitiesInGroup["ManyCutsGroup"].Should().HaveCount(2);
        svc.AbilitiesInGroup["ManyCutsGroup"].Select(a => a.InternalName)
            .Should().BeEquivalentTo(["ManyCuts", "ManyCuts2"]);
    }

    [Fact]
    public void AbilitiesTaughtByNpc_DerivedFromTrainingSources()
    {
        WriteFixture(
            abilitiesJson: """
            {
              "ability_1": { "InternalName": "WoundingShot", "Name": "Wounding Shot", "Skill": "Archery", "Level": 1, "IconID": 1 }
            }
            """,
            sourcesAbilitiesJson: """
            {
              "ability_1": {
                "entries": [
                  { "type": "Skill", "skill": "Archery" },
                  { "type": "Training", "npc": "NPC_Flia" }
                ]
              }
            }
            """,
            npcsJson: """
            {
              "NPC_Flia": { "Name": "Flia", "AreaName": "AreaSerbule" }
            }
            """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.AbilitiesTaughtByNpc.Should().ContainKey("NPC_Flia");
        svc.AbilitiesTaughtByNpc["NPC_Flia"].Should().ContainSingle()
            .Which.InternalName.Should().Be("WoundingShot");
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("HTTP must not be called in this test");
    }
}
