using System.Text.Json;
using Celebrimbor.Domain;
using FluentAssertions;
using Mithril.Planning;
using Xunit;

namespace Celebrimbor.Tests;

/// <summary>
/// #228: CelebrimborSettings became a versioned persisted root so it can carry
/// ActivePlan. These pin the migration contract — legacy unversioned files must
/// load without data loss, and the plan snapshot must round-trip through the
/// source-gen JSON context.
/// </summary>
public class CelebrimborSettingsVersioningTests
{
    // Exercise the *actual* persistence path: JsonSettingsStore deserializes via
    // the generated JsonTypeInfo<T> (which carries the camelCase naming policy).
    private static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo<CelebrimborSettings> Ti
        = CelebrimborSettingsJsonContext.Default.CelebrimborSettings;

    [Fact]
    public void LegacyUnversionedJson_LoadsWithoutDataLoss_AndNoActivePlan()
    {
        // A settings.json written before #228 — no schemaVersion, no activePlan.
        const string legacy = """
        {
          "knownRecipesOnly": true,
          "expansionDepth": 3,
          "craftList": [ { "recipeInternalName": "ForgeNail", "quantity": 7 } ],
          "onHandOverrides": [ { "itemInternalName": "Iron", "quantity": 12 } ]
        }
        """;

        var loaded = JsonSerializer.Deserialize(legacy, Ti)!;

        // Per the codebase's IVersionedState pattern (cf. ArwenFavorState),
        // SchemaVersion is initialised to CurrentVersion, so an omitted field
        // loads as v1 — the migration *safety* is that Migrate tolerates that
        // (identity) and no legacy data is dropped, NOT a 0 sentinel.
        loaded.SchemaVersion.Should().Be(CelebrimborSettings.CurrentVersion);
        loaded.KnownRecipesOnly.Should().BeTrue();
        loaded.ExpansionDepth.Should().Be(3);
        loaded.CraftList.Should().ContainSingle().Which.RecipeInternalName.Should().Be("ForgeNail");
        loaded.OnHandOverrides.Should().ContainSingle().Which.Quantity.Should().Be(12);
        loaded.ActivePlan.Should().BeNull(because: "legacy files have no plan; the new field defaults null");
    }

    [Fact]
    public void Migrate_IsIdentity_AndCurrentVersionIsOne()
    {
        CelebrimborSettings.CurrentVersion.Should().Be(1);

        var legacy = new CelebrimborSettings
        {
            SchemaVersion = 0,
            CraftList = [new CraftListEntry { RecipeInternalName = "X", Quantity = 2 }],
        };

        var migrated = CelebrimborSettings.Migrate(legacy);

        migrated.Should().BeSameAs(legacy, because: "v1 only adds nullable plan state — identity passthrough, no data loss");
        migrated.CraftList.Should().ContainSingle().Which.RecipeInternalName.Should().Be("X");
        // The loader (AddMithrilVersionedSettings) stamps CurrentVersion after Migrate;
        // Migrate itself must not require/produce a particular SchemaVersion.
    }

    [Fact]
    public void ActivePlan_RoundTripsThroughSourceGenContext()
    {
        var settings = new CelebrimborSettings
        {
            CraftList = [new CraftListEntry { RecipeInternalName = "Keep", Quantity = 1 }],
            ActivePlan = new PersistedPlan
            {
                Skill = "Smithing",
                StartLevel = 10,
                GoalLevel = 25,
                TotalCrafts = 42,
                CurrentPhaseIndex = 1,
                Phases =
                [
                    new PersistedPlanPhase { PhaseIndex = 0, RecipeInternalName = "ForgeBar", RecipeName = "Forge Bar", PredictedCrafts = 20, XpPerCraft = 30, LevelAtStart = 10, LevelAtEnd = 18 },
                    new PersistedPlanPhase { PhaseIndex = 1, RecipeInternalName = "ForgePlate", RecipeName = "Forge Plate", PredictedCrafts = 22, XpPerCraft = 90, LevelAtStart = 18, LevelAtEnd = 25 },
                ],
                Unlocks = [new PersistedSkillUnlock { AtLevel = 18, RecipeInternalName = "ForgePlate", RecipeName = "Forge Plate", XpPerCraftAtUnlock = 90, Reason = "Reaches Smithing 18" }],
                Sourcing = [new PersistedSourcingEntry { ItemInternalName = "Coal", Mode = SourcingMode.SupplyExternally }],
            },
        };

        var json = JsonSerializer.Serialize(settings, Ti);
        var back = JsonSerializer.Deserialize(json, Ti)!;

        back.ActivePlan.Should().NotBeNull();
        back.ActivePlan!.Skill.Should().Be("Smithing");
        back.ActivePlan.CurrentPhaseIndex.Should().Be(1);
        back.ActivePlan.Phases.Should().HaveCount(2);
        back.ActivePlan.Phases[1].RecipeInternalName.Should().Be("ForgePlate");
        back.ActivePlan.Unlocks.Should().ContainSingle().Which.AtLevel.Should().Be(18);
        back.ActivePlan.Sourcing.Should().ContainSingle();
        back.ActivePlan.Sourcing[0].Mode.Should().Be(SourcingMode.SupplyExternally);
        back.ActivePlan.ToSkillTarget().Should().Be(new SkillTarget("Smithing", 25));
        back.ActivePlan.ToSourcingPolicy().For("Coal").Should().Be(SourcingMode.SupplyExternally);
        back.CraftList.Should().ContainSingle(because: "legacy craft-list data coexists with the plan");
    }
}
