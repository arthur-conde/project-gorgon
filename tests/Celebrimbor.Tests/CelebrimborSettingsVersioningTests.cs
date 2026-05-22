using System.Text.Json;
using Celebrimbor.Domain;
using FluentAssertions;
using Xunit;

namespace Celebrimbor.Tests;

/// <summary>
/// #208 hygiene: CelebrimborSettings carries a schema version (identity Migrate,
/// no data loss). The leveling plan is NOT stored here — that's a per-character
/// <see cref="LevelingPlanState"/> store (see <see cref="LevelingPlanStateTests"/>).
/// </summary>
public class CelebrimborSettingsVersioningTests
{
    // Exercise the *actual* persistence path: JsonSettingsStore deserializes via
    // the generated JsonTypeInfo<T> (which carries the camelCase naming policy).
    private static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo<CelebrimborSettings> Ti
        = CelebrimborSettingsJsonContext.Default.CelebrimborSettings;

    [Fact]
    public void LegacyUnversionedJson_LoadsWithoutDataLoss()
    {
        // A settings.json written before the version stamp — no schemaVersion.
        const string legacy = """
        {
          "knownRecipesOnly": true,
          "expansionDepth": 3,
          "craftList": [ { "recipeInternalName": "ForgeNail", "quantity": 7 } ],
          "onHandOverrides": [ { "itemInternalName": "Iron", "quantity": 12 } ]
        }
        """;

        var loaded = JsonSerializer.Deserialize(legacy, Ti)!;

        // Per the codebase's IVersionedState pattern (cf. ArwenFavorState /
        // PlayerQuestJournalState), SchemaVersion initialises to CurrentVersion, so an
        // omitted field loads as v1 — migration *safety* is that Migrate tolerates
        // that (identity) and no legacy data is dropped, NOT a 0 sentinel.
        loaded.SchemaVersion.Should().Be(CelebrimborSettings.CurrentVersion);
        loaded.KnownRecipesOnly.Should().BeTrue();
        loaded.ExpansionDepth.Should().Be(3);
        loaded.CraftList.Should().ContainSingle().Which.RecipeInternalName.Should().Be("ForgeNail");
        loaded.OnHandOverrides.Should().ContainSingle().Which.Quantity.Should().Be(12);
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

        migrated.Should().BeSameAs(legacy, because: "identity passthrough — no data loss");
        migrated.CraftList.Should().ContainSingle().Which.RecipeInternalName.Should().Be("X");
    }
}
