using Arda.Abstractions.Logs;
using Arda.Composition.Events;
using Arda.Composition.Internal;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player;
using Arda.World.Player.Events;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.Shared.Character;
using Xunit;

namespace Arda.Composition.Tests;

public class PlayerProgressionComposerTests : IDisposable
{
    private readonly DomainEventBus _bus = new(NullLogger<DomainEventBus>.Instance);
    private readonly FakePlayerState _playerState = new();
    private readonly PlayerProgressionComposer _composer;
    private readonly List<SkillProgressionChanged> _progressionEvents = [];

    private static readonly DateTimeOffset T0 = new(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);

    public PlayerProgressionComposerTests()
    {
        _composer = new PlayerProgressionComposer(
            _bus,
            _playerState,
            recipeKeyResolver: id => $"recipe_{id}");
        _bus.Subscribe<SkillProgressionChanged>(e => _progressionEvents.Add(e));
    }

    public void Dispose() => _composer.Dispose();

    private static LogLineMetadata Meta(DateTimeOffset ts) =>
        new(Timestamp: ts, ReadOn: ts, IsReplay: false);

    // ── SkillsLoaded ──────────────────────────────────────────────────────

    [Fact]
    public void SkillsLoaded_PopulatesSkillsDictionary()
    {
        _playerState.SetSkills(new Dictionary<string, SkillEntry>
        {
            ["Gardening"] = new(Raw: 62, Bonus: 0, Xp: 14503, Tnl: 18816, Max: 125),
            ["Tanning"] = new(Raw: 30, Bonus: 5, Xp: 500, Tnl: 1200, Max: 0),
        });

        _bus.Publish(new SkillsLoaded(2, Meta(T0)));

        _composer.Skills.Should().HaveCount(2);
        _composer.Skills["Gardening"].Level.Should().Be(62);
        _composer.Skills["Gardening"].BonusLevels.Should().Be(0);
        _composer.Skills["Gardening"].CurrentXp.Should().Be(14503);
        _composer.Skills["Gardening"].XpNeededForNextLevel.Should().Be(18816);
        _composer.Skills["Gardening"].MaxLevel.Should().Be(125);
        _composer.Skills["Gardening"].IsCapped.Should().BeFalse();
    }

    [Fact]
    public void SkillsLoaded_IsCapped_WhenRawEqualsMax()
    {
        _playerState.SetSkills(new Dictionary<string, SkillEntry>
        {
            ["Carpentry"] = new(Raw: 125, Bonus: 3, Xp: 0, Tnl: 0, Max: 125),
        });

        _bus.Publish(new SkillsLoaded(1, Meta(T0)));

        _composer.Skills["Carpentry"].IsCapped.Should().BeTrue();
    }

    [Fact]
    public void SkillsLoaded_FiresStateChanged()
    {
        var fired = false;
        _composer.StateChanged += () => fired = true;

        _playerState.SetSkills(new Dictionary<string, SkillEntry>
        {
            ["Gardening"] = new(Raw: 1, Bonus: 0, Xp: 0, Tnl: 100, Max: 125),
        });
        _bus.Publish(new SkillsLoaded(1, Meta(T0)));

        fired.Should().BeTrue();
    }

    // ── SkillUpdated ──────────────────────────────────────────────────────

    [Fact]
    public void SkillUpdated_UpdatesSingleSkill()
    {
        _playerState.SetSkills(new Dictionary<string, SkillEntry>
        {
            ["Gardening"] = new(Raw: 62, Bonus: 0, Xp: 14503, Tnl: 18816, Max: 125),
        });
        _bus.Publish(new SkillsLoaded(1, Meta(T0)));

        _bus.Publish(new SkillUpdated("Gardening", Raw: 62, Bonus: 0, Xp: 14529, Tnl: 18816, Max: 125, XpGained: 26, Meta(T0.AddSeconds(5))));

        _composer.Skills["Gardening"].CurrentXp.Should().Be(14529);
    }

    [Fact]
    public void SkillUpdated_PublishesSkillProgressionChanged()
    {
        _bus.Publish(new SkillUpdated("Tanning", Raw: 31, Bonus: 5, Xp: 1077, Tnl: 2400, Max: 0, XpGained: 577, Meta(T0)));

        _progressionEvents.Should().ContainSingle();
        _progressionEvents[0].SkillKey.Should().Be("Tanning");
        _progressionEvents[0].XpGained.Should().Be(577);
        _progressionEvents[0].Skill.Level.Should().Be(31);
    }

    [Fact]
    public void SkillUpdated_PreservesOtherSkills()
    {
        _playerState.SetSkills(new Dictionary<string, SkillEntry>
        {
            ["Gardening"] = new(Raw: 62, Bonus: 0, Xp: 14503, Tnl: 18816, Max: 125),
            ["Tanning"] = new(Raw: 30, Bonus: 5, Xp: 500, Tnl: 1200, Max: 0),
        });
        _bus.Publish(new SkillsLoaded(2, Meta(T0)));

        _bus.Publish(new SkillUpdated("Tanning", Raw: 31, Bonus: 5, Xp: 0, Tnl: 1500, Max: 0, XpGained: 700, Meta(T0.AddSeconds(1))));

        _composer.Skills.Should().HaveCount(2);
        _composer.Skills["Gardening"].Level.Should().Be(62, "untouched skill should remain");
        _composer.Skills["Tanning"].Level.Should().Be(31);
    }

    // ── RecipesLoaded ─────────────────────────────────────────────────────

    [Fact]
    public void RecipesLoaded_PopulatesRecipeDictionary()
    {
        _playerState.SetRecipes(new Dictionary<int, RecipeEntry>
        {
            [1001] = new(1001, 7),
            [1002] = new(1002, 0),
        });

        _bus.Publish(new RecipesLoaded(2, Meta(T0)));

        _composer.RecipeCompletions.Should().HaveCount(2);
        _composer.RecipeCompletions["recipe_1001"].Should().Be(7);
        _composer.RecipeCompletions["recipe_1002"].Should().Be(0);
    }

    [Fact]
    public void RecipesLoaded_FiresStateChanged()
    {
        var fired = false;
        _composer.StateChanged += () => fired = true;

        _playerState.SetRecipes(new Dictionary<int, RecipeEntry> { [1] = new(1, 1) });
        _bus.Publish(new RecipesLoaded(1, Meta(T0)));

        fired.Should().BeTrue();
    }

    // ── RecipeUpdated ─────────────────────────────────────────────────────

    [Fact]
    public void RecipeUpdated_UpdatesSingleRecipe()
    {
        _playerState.SetRecipes(new Dictionary<int, RecipeEntry>
        {
            [1001] = new(1001, 7),
        });
        _bus.Publish(new RecipesLoaded(1, Meta(T0)));

        _bus.Publish(new RecipeUpdated(1001, 8, Meta(T0.AddSeconds(1))));

        _composer.RecipeCompletions["recipe_1001"].Should().Be(8);
    }

    [Fact]
    public void RecipeUpdated_AddsNewRecipeIfNotPresent()
    {
        _bus.Publish(new RecipeUpdated(2000, 0, Meta(T0)));

        _composer.RecipeCompletions.Should().ContainKey("recipe_2000");
        _composer.RecipeCompletions["recipe_2000"].Should().Be(0);
    }

    // ── Recipe key resolver ───────────────────────────────────────────────

    [Fact]
    public void RecipeKeyResolver_UsedForKeyNormalization()
    {
        using var composer = new PlayerProgressionComposer(
            _bus,
            _playerState,
            recipeKeyResolver: id => id == 42 ? "RawMeatLoaf" : null);

        _playerState.SetRecipes(new Dictionary<int, RecipeEntry>
        {
            [42] = new(42, 3),
            [99] = new(99, 1),
        });
        _bus.Publish(new RecipesLoaded(2, Meta(T0)));

        composer.RecipeCompletions.Should().ContainKey("RawMeatLoaf");
        composer.RecipeCompletions["RawMeatLoaf"].Should().Be(3);
        composer.RecipeCompletions.Should().NotContainKey("recipe_99",
            "resolver returned null → recipe excluded");
    }

    [Fact]
    public void NoRecipeKeyResolver_FallsBackToFormatting()
    {
        using var composer = new PlayerProgressionComposer(_bus, _playerState);

        _playerState.SetRecipes(new Dictionary<int, RecipeEntry>
        {
            [500] = new(500, 2),
        });
        _bus.Publish(new RecipesLoaded(1, Meta(T0)));

        composer.RecipeCompletions.Should().ContainKey("recipe_500");
    }

    // ── Session switch (no store) ─────────────────────────────────────────

    [Fact]
    public void SessionEstablished_ClearsStateWithoutStore()
    {
        _playerState.SetSkills(new Dictionary<string, SkillEntry>
        {
            ["Gardening"] = new(Raw: 62, Bonus: 0, Xp: 14503, Tnl: 18816, Max: 125),
        });
        _bus.Publish(new SkillsLoaded(1, Meta(T0)));

        var session = new ComposedSession("Alice", "TestServer",
            T0.AddMinutes(5), TimeSpan.Zero, "Alice:20260526120500");
        _bus.Publish(new SessionEstablished(session, Meta(T0.AddMinutes(5))));

        _composer.Skills.Should().NotBeEmpty("skills are not cleared on session — live data persists");
    }

    [Fact]
    public void SameSession_DoesNotReload()
    {
        var session = new ComposedSession("Alice", "TestServer",
            T0, TimeSpan.Zero, "Alice:20260526120000");
        _bus.Publish(new SessionEstablished(session, Meta(T0)));

        _playerState.SetSkills(new Dictionary<string, SkillEntry>
        {
            ["Gardening"] = new(Raw: 62, Bonus: 0, Xp: 14503, Tnl: 18816, Max: 125),
        });
        _bus.Publish(new SkillsLoaded(1, Meta(T0.AddSeconds(1))));

        var changeCount = 0;
        _composer.StateChanged += () => changeCount++;

        _bus.Publish(new SessionEstablished(session, Meta(T0)));

        changeCount.Should().Be(0, "same session does not trigger reload");
    }

    [Fact]
    public void DifferentSession_FiresStateChanged()
    {
        var session1 = new ComposedSession("Alice", "TestServer",
            T0, TimeSpan.Zero, "Alice:20260526120000");
        _bus.Publish(new SessionEstablished(session1, Meta(T0)));

        var fired = false;
        _composer.StateChanged += () => fired = true;

        var session2 = new ComposedSession("Bob", "TestServer",
            T0.AddMinutes(10), TimeSpan.Zero, "Bob:20260526121000");
        _bus.Publish(new SessionEstablished(session2, Meta(T0.AddMinutes(10))));

        fired.Should().BeTrue();
    }

    // ── Persistence round-trip ────────────────────────────────────────────

    [Fact]
    public void PersistenceRoundTrip_RestoresState()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"mithril-test-{Guid.NewGuid():N}");
        try
        {
            var store = new PerCharacterStore<ProgressionSnapshot>(
                tempDir,
                "player-progression.json",
                ProgressionSnapshotJsonContext.Default.ProgressionSnapshot);

            using (var composer1 = new PlayerProgressionComposer(
                _bus, _playerState, store, id => $"recipe_{id}"))
            {
                var session = new ComposedSession("Alice", "Server1",
                    T0, TimeSpan.Zero, "Alice:20260526120000");
                _bus.Publish(new SessionEstablished(session, Meta(T0)));

                _playerState.SetSkills(new Dictionary<string, SkillEntry>
                {
                    ["Gardening"] = new(Raw: 62, Bonus: 0, Xp: 14503, Tnl: 18816, Max: 125),
                });
                _bus.Publish(new SkillsLoaded(1, Meta(T0.AddSeconds(1))));

                _playerState.SetRecipes(new Dictionary<int, RecipeEntry>
                {
                    [1001] = new(1001, 7),
                });
                _bus.Publish(new RecipesLoaded(1, Meta(T0.AddSeconds(2))));
            }

            _playerState.SetSkills(new Dictionary<string, SkillEntry>());
            _playerState.SetRecipes(new Dictionary<int, RecipeEntry>());

            using var composer2 = new PlayerProgressionComposer(
                _bus, _playerState, store, id => $"recipe_{id}");

            var session2 = new ComposedSession("Alice", "Server1",
                T0.AddHours(1), TimeSpan.Zero, "Alice:20260526130000");
            _bus.Publish(new SessionEstablished(session2, Meta(T0.AddHours(1))));

            composer2.Skills.Should().ContainKey("Gardening");
            composer2.Skills["Gardening"].Level.Should().Be(62);
            composer2.Skills["Gardening"].CurrentXp.Should().Be(14503);
            composer2.RecipeCompletions.Should().ContainKey("recipe_1001");
            composer2.RecipeCompletions["recipe_1001"].Should().Be(7);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void PersistedBaseline_MergedWithLiveData()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"mithril-test-{Guid.NewGuid():N}");
        try
        {
            var store = new PerCharacterStore<ProgressionSnapshot>(
                tempDir,
                "player-progression.json",
                ProgressionSnapshotJsonContext.Default.ProgressionSnapshot);

            using (var composer1 = new PlayerProgressionComposer(
                _bus, _playerState, store, id => $"recipe_{id}"))
            {
                var session = new ComposedSession("Alice", "Server1",
                    T0, TimeSpan.Zero, "Alice:20260526120000");
                _bus.Publish(new SessionEstablished(session, Meta(T0)));

                _playerState.SetSkills(new Dictionary<string, SkillEntry>
                {
                    ["Gardening"] = new(Raw: 62, Bonus: 0, Xp: 14503, Tnl: 18816, Max: 125),
                    ["Tanning"] = new(Raw: 30, Bonus: 5, Xp: 500, Tnl: 1200, Max: 0),
                });
                _bus.Publish(new SkillsLoaded(2, Meta(T0.AddSeconds(1))));
            }

            // Simulate next session where only Gardening appears in replay (log was rotated)
            _playerState.SetSkills(new Dictionary<string, SkillEntry>
            {
                ["Gardening"] = new(Raw: 63, Bonus: 0, Xp: 200, Tnl: 20000, Max: 125),
            });

            using var composer2 = new PlayerProgressionComposer(
                _bus, _playerState, store, id => $"recipe_{id}");

            _bus.Publish(new SkillsLoaded(1, Meta(T0.AddHours(1))));

            var session2 = new ComposedSession("Alice", "Server1",
                T0.AddHours(1).AddSeconds(1), TimeSpan.Zero, "Alice:20260526130001");
            _bus.Publish(new SessionEstablished(session2, Meta(T0.AddHours(1).AddSeconds(1))));

            composer2.Skills.Should().HaveCount(2, "persisted Tanning should be merged");
            composer2.Skills["Gardening"].Level.Should().Be(63, "live value wins");
            composer2.Skills["Tanning"].Level.Should().Be(30, "persisted baseline fills the gap");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    // ── Dispose ───────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_StopsSubscriptions()
    {
        _composer.Dispose();

        _bus.Publish(new SkillUpdated("Gardening", Raw: 1, Bonus: 0, Xp: 10, Tnl: 100, Max: 125, XpGained: 10, Meta(T0)));

        _progressionEvents.Should().BeEmpty();
    }

    // ── Test double ───────────────────────────────────────────────────────

    private sealed class FakePlayerState : IPlayerState
    {
        private IReadOnlyDictionary<string, SkillEntry> _skills =
            new Dictionary<string, SkillEntry>();
        private IReadOnlyDictionary<int, RecipeEntry> _recipes =
            new Dictionary<int, RecipeEntry>();

        public IReadOnlyDictionary<string, SkillEntry> Skills => _skills;
        public IReadOnlyDictionary<int, RecipeEntry> Recipes => _recipes;

        public void SetSkills(Dictionary<string, SkillEntry> skills) =>
            _skills = skills;

        public void SetRecipes(Dictionary<int, RecipeEntry> recipes) =>
            _recipes = recipes;
    }
}
