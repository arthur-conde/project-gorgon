using Celebrimbor.Services;
using FluentAssertions;
using Mithril.Planning;
using Mithril.Shared.Settings;
using Xunit;

namespace Celebrimbor.Tests;

/// <summary>
/// #228: the leveling-plan library is an independent, id-keyed, module-wide
/// store (NOT per-character) — multiple artifacts coexist, persisted on every
/// mutation, migrated on load.
/// </summary>
public class LevelingPlanStoreTests
{
    private static SavedLevelingPlan Plan(string skill, int goal)
        => new() { Skill = skill, GoalLevel = goal };

    [Fact]
    public void Upsert_AddsThenReplacesById_AndPersists()
    {
        var backing = new FakeStore();
        var store = new LevelingPlanStore(backing);

        var a = Plan("Smithing", 25);
        store.Upsert(a);
        store.All().Should().ContainSingle();
        backing.SaveCount.Should().Be(1);

        var b = Plan("Cooking", 40);
        store.Upsert(b);
        store.All().Should().HaveCount(2, because: "different ids coexist — not one-plan-per-module");

        // Same id ⇒ replace, not duplicate.
        var aPrime = new SavedLevelingPlan { Id = a.Id, Skill = "Smithing", GoalLevel = 50 };
        store.Upsert(aPrime);
        store.All().Should().HaveCount(2);
        store.Get(a.Id)!.GoalLevel.Should().Be(50);
        backing.SaveCount.Should().Be(3);
    }

    [Fact]
    public void Delete_RemovesById_AndOnlyPersistsOnChange()
    {
        var backing = new FakeStore();
        var store = new LevelingPlanStore(backing);
        var a = Plan("Smithing", 25);
        store.Upsert(a);
        backing.SaveCount.Should().Be(1);

        store.Delete("nope").Should().BeFalse();
        backing.SaveCount.Should().Be(1, because: "no change ⇒ no write");

        store.Delete(a.Id).Should().BeTrue();
        store.All().Should().BeEmpty();
        backing.SaveCount.Should().Be(2);
    }

    [Fact]
    public void Load_MigratesAndStampsCurrentVersion()
    {
        var legacy = new SavedLevelingPlanLibrary { SchemaVersion = 0, Plans = { Plan("Smithing", 25) } };
        var backing = new FakeStore(legacy);

        var store = new LevelingPlanStore(backing);

        store.All().Should().ContainSingle(because: "identity Migrate preserves plans");
        backing.Saved!.SchemaVersion.Should().Be(SavedLevelingPlanLibrary.CurrentVersion,
            because: "version mismatch on load ⇒ migrate + stamp + persist");
    }

    private sealed class FakeStore : ISettingsStore<SavedLevelingPlanLibrary>
    {
        private SavedLevelingPlanLibrary _current;
        public FakeStore(SavedLevelingPlanLibrary? seed = null) => _current = seed ?? new();
        public SavedLevelingPlanLibrary? Saved { get; private set; }
        public int SaveCount { get; private set; }
        public string FilePath => "(memory)";
        public SavedLevelingPlanLibrary Load() => _current;
        public Task<SavedLevelingPlanLibrary> LoadAsync(CancellationToken ct = default) => Task.FromResult(_current);
        public Task SaveAsync(SavedLevelingPlanLibrary value, CancellationToken ct = default) { Save(value); return Task.CompletedTask; }
        public void Save(SavedLevelingPlanLibrary value) { _current = value; Saved = value; SaveCount++; }
    }
}
