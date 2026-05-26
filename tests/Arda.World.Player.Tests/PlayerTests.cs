using System.Collections.Frozen;
using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Arda.World.Player.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.World.Player.Tests;

public class PlayerTests
{
    private readonly SpyEventBus _bus = new();
    private readonly Internal.Player _player;

    public PlayerTests()
    {
        var pool = new InternPool(FrozenDictionary<string, string>.Empty);
        _player = new Internal.Player(_bus, pool);
    }

    private static LogLineMetadata Meta() =>
        new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false);

    private void DispatchLoadSkills(string skillArgs)
    {
        var line = $"LocalPlayer: ProcessLoadSkills({skillArgs})";
        _player.LoadSkillsHandler.Handle($"({skillArgs})".AsSpan(), line, Meta());
    }

    private void DispatchUpdateSkill(string skillArgs)
    {
        var line = $"LocalPlayer: ProcessUpdateSkill({skillArgs})";
        _player.UpdateSkillHandler.Handle($"({skillArgs})".AsSpan(), line, Meta());
    }

    private void DispatchLoadRecipes(string recipeArgs)
    {
        var line = $"LocalPlayer: ProcessLoadRecipes({recipeArgs})";
        _player.LoadRecipesHandler.Handle($"({recipeArgs})".AsSpan(), line, Meta());
    }

    private void DispatchUpdateRecipe(string recipeArgs)
    {
        var line = $"LocalPlayer: ProcessUpdateRecipe({recipeArgs})";
        _player.UpdateRecipeHandler.Handle($"({recipeArgs})".AsSpan(), line, Meta());
    }

    // ── LoadSkills ───────────────────────────────────────────────────────

    [Fact]
    public void LoadSkills_ParsesMultipleEntries()
    {
        DispatchLoadSkills(
            "{type=Toolcrafting,raw=15,bonus=0,xp=26,tnl=680,max=50}, " +
            "{type=Tanning,raw=50,bonus=3,xp=0,tnl=5280,max=50}, " +
            "{type=Surveying,raw=44,bonus=3,xp=246,tnl=2000,max=50}");

        _player.Skills.Should().HaveCount(3);
        _player.Skills["Toolcrafting"].Should().Be(new SkillEntry(15, 0, 26, 680, 50));
        _player.Skills["Tanning"].Should().Be(new SkillEntry(50, 3, 0, 5280, 50));
        _player.Skills["Surveying"].Should().Be(new SkillEntry(44, 3, 246, 2000, 50));

        _bus.Published<SkillsLoaded>().Should().ContainSingle()
            .Which.Count.Should().Be(3);
    }

    [Fact]
    public void LoadSkills_SingleEntry()
    {
        DispatchLoadSkills("{type=Performance_Dance,raw=24,bonus=0,xp=225,tnl=600,max=65}");

        _player.Skills.Should().ContainKey("Performance_Dance");
        _player.Skills["Performance_Dance"].Should().Be(new SkillEntry(24, 0, 225, 600, 65));
    }

    [Fact]
    public void LoadSkills_ReplacesExistingState()
    {
        DispatchLoadSkills("{type=Tanning,raw=50,bonus=3,xp=0,tnl=5280,max=50}");
        _player.Skills.Should().HaveCount(1);

        DispatchLoadSkills("{type=Surveying,raw=10,bonus=0,xp=0,tnl=100,max=50}");

        _player.Skills.Should().HaveCount(1);
        _player.Skills.Should().ContainKey("Surveying");
        _player.Skills.Should().NotContainKey("Tanning");
    }

    // ── UpdateSkill ─────────────────────────────────────────────────────

    [Fact]
    public void UpdateSkill_ExtractsXpDelta()
    {
        DispatchLoadSkills("{type=Surveying,raw=44,bonus=3,xp=200,tnl=2000,max=50}");
        _bus.Clear();

        DispatchUpdateSkill("{type=Surveying,raw=44,bonus=3,xp=246,tnl=2000,max=50}, True, 25, 0, 0");

        _player.Skills["Surveying"].Xp.Should().Be(246);
        var evt = _bus.Published<SkillUpdated>().Should().ContainSingle().Which;
        evt.SkillKey.Should().Be("Surveying");
        evt.XpGained.Should().Be(25);
        evt.Raw.Should().Be(44);
    }

    [Fact]
    public void UpdateSkill_AddsNewSkillIfNotPreviouslyLoaded()
    {
        DispatchUpdateSkill("{type=Fishing,raw=1,bonus=0,xp=10,tnl=50,max=50}, True, 10, 0, 0");

        _player.Skills.Should().ContainKey("Fishing");
        _player.Skills["Fishing"].Should().Be(new SkillEntry(1, 0, 10, 50, 50));
    }

    // ── LoadRecipes ─────────────────────────────────────────────────────

    [Fact]
    public void LoadRecipes_ParsesParallelArrays()
    {
        DispatchLoadRecipes("[1,1026,1027,], [7,607,255,]");

        _player.Recipes.Should().HaveCount(3);
        _player.Recipes[1].Count.Should().Be(7);
        _player.Recipes[1026].Count.Should().Be(607);
        _player.Recipes[1027].Count.Should().Be(255);

        _bus.Published<RecipesLoaded>().Should().ContainSingle()
            .Which.Count.Should().Be(3);
    }

    [Fact]
    public void LoadRecipes_HandlesNoTrailingComma()
    {
        DispatchLoadRecipes("[100,200], [5,10]");

        _player.Recipes.Should().HaveCount(2);
        _player.Recipes[100].Count.Should().Be(5);
        _player.Recipes[200].Count.Should().Be(10);
    }

    [Fact]
    public void LoadRecipes_ReplacesExistingState()
    {
        DispatchLoadRecipes("[1,2,3], [10,20,30]");
        _player.Recipes.Should().HaveCount(3);

        DispatchLoadRecipes("[99], [1]");
        _player.Recipes.Should().HaveCount(1);
        _player.Recipes.Should().ContainKey(99);
        _player.Recipes.Should().NotContainKey(1);
    }

    // ── UpdateRecipe ────────────────────────────────────────────────────

    [Fact]
    public void UpdateRecipe_SetsCount()
    {
        DispatchUpdateRecipe("21000, 2");

        _player.Recipes[21000].Count.Should().Be(2);
        var evt = _bus.Published<RecipeUpdated>().Should().ContainSingle().Which;
        evt.RecipeId.Should().Be(21000);
        evt.Count.Should().Be(2);
    }

    [Fact]
    public void UpdateRecipe_ZeroCount_MeansJustLearned()
    {
        DispatchUpdateRecipe("5000, 0");

        _player.Recipes[5000].Count.Should().Be(0);
    }

    // ── Interning ───────────────────────────────────────────────────────

    [Fact]
    public void SkillKey_IsInterned_AcrossRepeatedUpdates()
    {
        DispatchLoadSkills("{type=Surveying,raw=44,bonus=3,xp=200,tnl=2000,max=50}");
        var first = _player.Skills.Keys.Single();

        DispatchUpdateSkill("{type=Surveying,raw=44,bonus=3,xp=246,tnl=2000,max=50}, True, 25, 0, 0");
        var evt = _bus.Published<SkillUpdated>().Last();

        ReferenceEquals(first, evt.SkillKey).Should().BeTrue(
            "InternPool should return the same string instance for repeated skill keys");
    }

    // ── SpyEventBus (shared with MapTests) ──────────────────────────────

    private sealed class SpyEventBus : IDomainEventBus
    {
        private readonly Dictionary<Type, List<object>> _published = [];

        public IDisposable Subscribe<T>(Action<T> handler) where T : struct
            => new NoopDisposable();

        public void Publish<T>(T domainEvent) where T : struct
        {
            if (!_published.TryGetValue(typeof(T), out var list))
            {
                list = [];
                _published[typeof(T)] = list;
            }
            list.Add(domainEvent);
        }

        public List<T> Published<T>() where T : struct
        {
            if (_published.TryGetValue(typeof(T), out var list))
                return list.Cast<T>().ToList();
            return [];
        }

        public void Clear() => _published.Clear();

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
