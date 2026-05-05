using System.ComponentModel;
using System.Text.Json;
using Elrond.Domain;
using FluentAssertions;
using Xunit;

namespace Elrond.Tests;

public class ElrondSettingsPersistenceTests
{
    private static readonly JsonSerializerOptions Options =
        new(ElrondSettingsJsonContext.Default.ElrondSettings.Options);

    [Fact]
    public void RoundTrip_PreservesActiveSortKeys()
    {
        var original = new ElrondSettings
        {
            LastSkill = "Cooking",
            ActiveSortKeys =
            [
                new("EffectiveXp", ListSortDirection.Descending),
                new("RecipeName",  ListSortDirection.Ascending),
            ],
        };

        var json = JsonSerializer.Serialize(original, ElrondSettingsJsonContext.Default.ElrondSettings);
        var restored = JsonSerializer.Deserialize(json, ElrondSettingsJsonContext.Default.ElrondSettings);

        restored.Should().NotBeNull();
        restored!.ActiveSortKeys.Should().HaveCount(2);
        restored.ActiveSortKeys[0].Id.Should().Be("EffectiveXp");
        restored.ActiveSortKeys[0].Direction.Should().Be(ListSortDirection.Descending);
        restored.ActiveSortKeys[1].Id.Should().Be("RecipeName");
        restored.ActiveSortKeys[1].Direction.Should().Be(ListSortDirection.Ascending);
    }

    [Fact]
    public void RoundTrip_PreservesActiveFilterIdsAndPersistenceFlag()
    {
        var original = new ElrondSettings
        {
            ActiveFilterIds = ["ShowUnknown", "CraftableOnly"],
            HasPersistedFilters = true,
        };

        var json = JsonSerializer.Serialize(original, ElrondSettingsJsonContext.Default.ElrondSettings);
        var restored = JsonSerializer.Deserialize(json, ElrondSettingsJsonContext.Default.ElrondSettings);

        restored!.ActiveFilterIds.Should().Equal("ShowUnknown", "CraftableOnly");
        restored.HasPersistedFilters.Should().BeTrue();
    }

    [Fact]
    public void RoundTrip_EmptyFilters_DistinguishedByPersistenceFlag()
    {
        // Empty list with HasPersistedFilters=true means the user intentionally
        // disabled all filters — VM must respect that and not snap back to defaults.
        var original = new ElrondSettings
        {
            ActiveFilterIds = [],
            HasPersistedFilters = true,
        };

        var json = JsonSerializer.Serialize(original, ElrondSettingsJsonContext.Default.ElrondSettings);
        var restored = JsonSerializer.Deserialize(json, ElrondSettingsJsonContext.Default.ElrondSettings);

        restored!.ActiveFilterIds.Should().BeEmpty();
        restored.HasPersistedFilters.Should().BeTrue();
    }

    [Fact]
    public void Deserializing_LegacySchema_LeavesNewFieldsAtDefaults()
    {
        // Pre-popup settings file (the schema seen on disk for #97-era users).
        // It has "lastSkill" + "lastGoalLevel" only; no activeSortKeys / activeFilterIds.
        const string legacyJson = """
            {
              "lastSkill": "Carpentry",
              "lastGoalLevel": 15
            }
            """;

        var restored = JsonSerializer.Deserialize(legacyJson, ElrondSettingsJsonContext.Default.ElrondSettings);

        restored.Should().NotBeNull();
        restored!.LastSkill.Should().Be("Carpentry");
        restored.LastGoalLevel.Should().Be(15);
        restored.ActiveSortKeys.Should().BeEmpty();
        restored.ActiveFilterIds.Should().BeEmpty();
        restored.HasPersistedFilters.Should().BeFalse();
        restored.SortKey.Should().BeNull();
        restored.SortDescending.Should().BeNull();
        restored.ViewMode.Should().Be("Rows");
    }

    [Fact]
    public void Deserializing_PreviousMigrationSchema_PreservesLegacySortFields()
    {
        // Mid-migration schema: an upgrading user's file with the old SortKey +
        // SortDescending pair set, no ActiveSortKeys yet. The VM ctor consumes
        // these on first launch and clears them.
        const string mid = """
            {
              "lastSkill": "Cooking",
              "sortKey": "EffectiveXp",
              "sortDescending": true
            }
            """;

        var restored = JsonSerializer.Deserialize(mid, ElrondSettingsJsonContext.Default.ElrondSettings);

        restored!.SortKey.Should().Be("EffectiveXp");
        restored.SortDescending.Should().BeTrue();
        restored.ActiveSortKeys.Should().BeEmpty();
    }
}
