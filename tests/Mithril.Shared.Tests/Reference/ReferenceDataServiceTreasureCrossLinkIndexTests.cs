using System.IO;
using System.Net.Http;
using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Reference;

/// <summary>
/// #435: verifies the Treasure-System data layer — <see cref="PowerEntry.Prefix"/> /
/// <see cref="PowerEntry.EnvelopeKey"/> capture and the
/// <see cref="IReferenceDataService.ProfilesByPower"/> /
/// <see cref="IReferenceDataService.ItemsByTSysProfile"/> reverse-view indices — built
/// against real tsysclientinfo / tsysprofiles / items fixtures through the bundled-load
/// plumbing (canonical pattern: <see cref="ReferenceDataServiceRecipeCrossLinkIndexTests"/>).
/// </summary>
[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class ReferenceDataServiceTreasureCrossLinkIndexTests : IDisposable
{
    private readonly string _root;
    private readonly string _cacheDir;
    private readonly string _bundledDir;

    public ReferenceDataServiceTreasureCrossLinkIndexTests()
    {
        _root = Mithril.TestSupport.TestPaths.CreateTempDir("mithril-treasure-crosslink-tests");
        _cacheDir = Path.Combine(_root, "cache");
        _bundledDir = Path.Combine(_root, "bundled");
        Directory.CreateDirectory(_cacheDir);
        Directory.CreateDirectory(_bundledDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static HttpClient NeverCallHttp() =>
        new(new ThrowingHandler("HTTP must not be called in this test"));

    private void Write(string name, string json)
    {
        File.WriteAllText(Path.Combine(_bundledDir, $"{name}.json"), json);
        File.WriteAllText(Path.Combine(_bundledDir, $"{name}.meta.json"), "{\"cdnVersion\":\"v1\",\"source\":0}");
    }

    [Fact]
    public void Power_CapturesPrefixAndEnvelopeKey_AndProfileReverseIndicesBuild()
    {
        Write("tsysclientinfo", """
        {
          "power_1001": {
            "InternalName": "SwordBoost",
            "Prefix": "Swordsman's",
            "Suffix": "of Swordsmanship",
            "Skill": "Sword",
            "Slots": ["Head", "MainHand"],
            "Tiers": {
              "id_1": { "EffectDescs": ["{BOOST_SKILL_SWORD}{5}"], "MaxLevel": 20, "MinLevel": 1, "MinRarity": "Uncommon", "SkillLevelPrereq": 1 }
            }
          },
          "power_1002": {
            "InternalName": "BardMaxHealth",
            "Suffix": "of the Bard",
            "Skill": "Bard",
            "Slots": ["Chest"],
            "Tiers": {
              "id_1": { "EffectDescs": ["{MAX_HEALTH}{10}"], "MaxLevel": 40, "MinLevel": 1, "MinRarity": "Uncommon", "SkillLevelPrereq": 1 }
            }
          }
        }
        """);
        Write("tsysprofiles", """
        { "Sword": ["SwordBoost", "BardMaxHealth"], "Chest": ["BardMaxHealth"] }
        """);
        Write("items", """
        {
          "item_1": { "Name": "Iron Sword", "InternalName": "IronSword", "TSysProfile": "Sword" },
          "item_2": { "Name": "Plain Boots", "InternalName": "PlainBoots" }
        }
        """);
        Write("recipes", """
        {
          "recipe_1": {
            "Name": "Forge Iron Sword", "InternalName": "ForgeIronSword", "Skill": "Blacksmithing",
            "Ingredients": [], "ResultItems": [ { "ItemCode": 1, "StackSize": 1 } ]
          }
        }
        """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        // PowerEntry now carries the affix prefix + the power_NNNN envelope key.
        svc.Powers.Should().ContainKey("SwordBoost");
        var sword = svc.Powers["SwordBoost"];
        sword.Prefix.Should().Be("Swordsman's");
        sword.Suffix.Should().Be("of Swordsmanship");
        sword.EnvelopeKey.Should().Be("power_1001");

        // Prefix-absent power: Prefix null (only-present-parts affix rule downstream).
        svc.Powers["BardMaxHealth"].Prefix.Should().BeNull();

        // ProfilesByPower = inverse of tsysprofiles (authoritative join → Confirmed edge).
        svc.ProfilesByPower["SwordBoost"].Should().BeEquivalentTo(new[] { "Sword" });
        svc.ProfilesByPower["BardMaxHealth"].Should().BeEquivalentTo(new[] { "Sword", "Chest" });

        // ItemsByTSysProfile = items whose TSysProfile names the profile.
        svc.ItemsByTSysProfile.Should().ContainKey("Sword");
        svc.ItemsByTSysProfile["Sword"].Should().Contain("IronSword");
        svc.ItemsByTSysProfile.Should().NotContainKey("Chest", "no item declares TSysProfile=Chest");

        // Power → Recipe provenance chain end-to-end: SwordBoost ∈ Sword pool;
        // IronSword.TSysProfile=Sword; ForgeIronSword produces IronSword.
        svc.RecipesByProducedItem["IronSword"].Should().ContainSingle()
            .Which.InternalName.Should().Be("ForgeIronSword");
    }

    private sealed class ThrowingHandler(string message) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message);
    }
}
