using System.Linq;
using Elrond.Domain;
using Elrond.Services;
using Elrond.ViewModels;
using FluentAssertions;
using Mithril.Shared.Character;
using Mithril.Shared.Reference;
using Mithril.Shared.Storage;
using Xunit;

namespace Elrond.Tests;

/// <summary>
/// Coverage for the hierarchy-aware skill picker tree (#129).
///
/// These tests exercise <see cref="SkillAdvisorViewModel.BuildSkillTree"/> and
/// <see cref="SkillAdvisorViewModel.SelectSkillFromDeepLink"/> through the
/// public observable surface — wrapping the engine + reference data in fakes
/// so we can stage parent/child relationships and active-character state
/// without spinning up the file-IO layer.
/// </summary>
public class SkillAdvisorViewModelTests
{
    [Fact]
    public void BuildSkillTree_GroupsAugmentationChildren_UnderHeaderOnlyParent()
    {
        // The two AugmentBrewing leaves both list "Augmentation" as their parent —
        // they should collapse into a single header-only Augmentation node.
        var (vm, _, _, _) = MakeFixture(
            characterSkills: new Dictionary<string, CharacterSkill>
            {
                ["ArmorAugmentBrewing"] = new(20, 0, 100, 200),
                ["WeaponAugmentBrewing"] = new(15, 0, 50, 150),
            },
            register: r =>
            {
                r.AddSkill("Augmentation", parents: []);
                r.AddSkill("ArmorAugmentBrewing", parents: ["Augmentation"]);
                r.AddSkill("WeaponAugmentBrewing", parents: ["Augmentation"]);
                r.AddRecipe(rewardSkill: "ArmorAugmentBrewing");
                r.AddRecipe(rewardSkill: "WeaponAugmentBrewing");
            });

        vm.SkillTreeRoots.Should().ContainSingle(
            because: "both leaves share Augmentation as their parent — only the parent is a root");
        var augmentation = vm.SkillTreeRoots[0];
        augmentation.Key.Should().Be("Augmentation");
        augmentation.IsHeaderOnly.Should().BeTrue();
        augmentation.IsSelectable.Should().BeFalse();
        augmentation.Children.Should().HaveCount(2);
        augmentation.Children.Select(c => c.Key).Should().Equal(
            ["ArmorAugmentBrewing", "WeaponAugmentBrewing"],
            because: "children sort alphabetically by display name");
    }

    [Fact]
    public void BuildSkillTree_OmitsSkillsCharacterDoesNotKnow()
    {
        var (vm, _, _, _) = MakeFixture(
            characterSkills: new Dictionary<string, CharacterSkill>
            {
                ["Cooking"] = new(10, 0, 0, 100),
                // Mycology missing — character has not learned it.
            },
            register: r =>
            {
                r.AddSkill("Cooking", parents: []);
                r.AddSkill("Mycology", parents: []);
                r.AddRecipe(rewardSkill: "Cooking");
                r.AddRecipe(rewardSkill: "Mycology");
            });

        vm.SkillTreeRoots.Should().ContainSingle();
        vm.SkillTreeRoots[0].Key.Should().Be("Cooking");
    }

    [Fact]
    public void BuildSkillTree_SortsChildrenAlphabeticallyByDisplayName()
    {
        // Display name (not Key) drives ordering. Keys are id-shaped; display names
        // can have a different alphabetical order.
        var (vm, _, _, _) = MakeFixture(
            characterSkills: new Dictionary<string, CharacterSkill>
            {
                ["ZebraSkill"] = new(1, 0, 0, 1),
                ["AlphaSkill"] = new(1, 0, 0, 1),
                ["MidSkill"] = new(1, 0, 0, 1),
            },
            register: r =>
            {
                r.AddSkill("ZebraSkill", parents: [], displayName: "Apple");
                r.AddSkill("AlphaSkill", parents: [], displayName: "Banana");
                r.AddSkill("MidSkill", parents: [], displayName: "Cherry");
                r.AddRecipe(rewardSkill: "ZebraSkill");
                r.AddRecipe(rewardSkill: "AlphaSkill");
                r.AddRecipe(rewardSkill: "MidSkill");
            });

        vm.SkillTreeRoots.Select(n => n.DisplayName).Should().Equal(["Apple", "Banana", "Cherry"]);
    }

    [Fact]
    public void SelectSkillFromDeepLink_KnownSkill_SetsSelectedSkill()
    {
        var (vm, _, _, _) = MakeFixture(
            characterSkills: new Dictionary<string, CharacterSkill>
            {
                ["Cooking"] = new(10, 0, 0, 100),
                ["Mycology"] = new(5, 0, 0, 50),
            },
            register: r =>
            {
                r.AddSkill("Cooking", parents: []);
                r.AddSkill("Mycology", parents: []);
                r.AddRecipe(rewardSkill: "Cooking");
                r.AddRecipe(rewardSkill: "Mycology");
            });

        vm.SelectSkillFromDeepLink("Mycology");

        vm.SelectedSkill.Should().Be("Mycology");
    }

    [Fact]
    public void SelectSkillFromDeepLink_BeforeCharacterLoaded_AppliesAfterCharacterArrives()
    {
        // Stage: no active character at deep-link time.
        var (vm, refData, characterSvc, _) = MakeFixture(
            characterSkills: null,
            register: r =>
            {
                r.AddSkill("Cooking", parents: []);
                r.AddRecipe(rewardSkill: "Cooking");
            });

        vm.SelectSkillFromDeepLink("Cooking");
        vm.SelectedSkill.Should().BeNull(because: "no character yet — the request should be stashed, not applied");

        // Character arrives.
        characterSvc.SetCharacter(new CharacterSnapshot(
            "TestChar", "TestServer", DateTimeOffset.UtcNow,
            new Dictionary<string, CharacterSkill> { ["Cooking"] = new(5, 0, 0, 100) },
            new Dictionary<string, int>(), new Dictionary<string, string>()));

        vm.SelectedSkill.Should().Be("Cooking",
            because: "the stashed request should apply after the character export landed");
    }

    [Fact]
    public void SelectSkillFromDeepLink_UnknownSkill_LeavesSelectionUnchanged()
    {
        var (vm, _, _, _) = MakeFixture(
            characterSkills: new Dictionary<string, CharacterSkill>
            {
                ["Cooking"] = new(10, 0, 0, 100),
            },
            register: r =>
            {
                r.AddSkill("Cooking", parents: []);
                r.AddRecipe(rewardSkill: "Cooking");
            });

        var initialSelection = vm.SelectedSkill;
        vm.SelectSkillFromDeepLink("DoesNotExist");

        vm.SelectedSkill.Should().Be(initialSelection,
            because: "an unknown skill key should not steal a valid selection");
    }

    // ── Fixture ──────────────────────────────────────────────────────────

    private static (SkillAdvisorViewModel vm, FakeRefData refData, FakeActiveCharacterService characterSvc, ElrondSettings settings)
        MakeFixture(
            IReadOnlyDictionary<string, CharacterSkill>? characterSkills,
            Action<FakeRefData> register)
    {
        var refData = new FakeRefData();
        register(refData);

        var characterSvc = new FakeActiveCharacterService();
        if (characterSkills is not null)
        {
            characterSvc.SetCharacter(new CharacterSnapshot(
                "TestChar", "TestServer", DateTimeOffset.UtcNow,
                characterSkills, new Dictionary<string, int>(), new Dictionary<string, string>()));
        }

        var settings = new ElrondSettings();
        var engine = new SkillAdvisorEngine(refData);
        var simulator = new LevelingSimulator(refData, engine);
        var vm = new SkillAdvisorViewModel(engine, simulator, characterSvc, refData, settings);
        return (vm, refData, characterSvc, settings);
    }

    private sealed class FakeActiveCharacterService : IActiveCharacterService
    {
        public IReadOnlyList<CharacterSnapshot> Characters { get; private set; } = [];
        public IReadOnlyList<ReportFileInfo> StorageReports { get; } = [];
        public string? ActiveCharacterName => ActiveCharacter?.Name;
        public string? ActiveServer => ActiveCharacter?.Server;
        public CharacterSnapshot? ActiveCharacter { get; private set; }
        public ReportFileInfo? ActiveStorageReport => null;
        public StorageReport? ActiveStorageContents => null;

        public void SetCharacter(CharacterSnapshot snapshot)
        {
            ActiveCharacter = snapshot;
            Characters = [snapshot];
            ActiveCharacterChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SetActiveCharacter(string name, string server) { }
        public void Refresh() { }

        public event EventHandler? ActiveCharacterChanged;
        public event EventHandler? CharacterExportsChanged { add { } remove { } }
        public event EventHandler? StorageReportsChanged { add { } remove { } }

        public void Dispose() { }
    }

    private sealed class FakeRefData : IReferenceDataService
    {
        private readonly Dictionary<string, RecipeEntry> _recipes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RecipeEntry> _recipesByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SkillEntry> _skills = new(StringComparer.Ordinal);
        private readonly Dictionary<string, XpTableEntry> _xpTables = new(StringComparer.Ordinal);

        public IReadOnlyList<string> Keys { get; } = ["items", "recipes", "skills", "xptables"];
        public IReadOnlyDictionary<long, ItemEntry> Items { get; } = new Dictionary<long, ItemEntry>();
        public IReadOnlyDictionary<string, ItemEntry> ItemsByInternalName { get; } = new Dictionary<string, ItemEntry>();
        public ItemKeywordIndex KeywordIndex { get; } = ItemKeywordIndex.Empty;
        public IReadOnlyDictionary<string, RecipeEntry> Recipes => _recipes;
        public IReadOnlyDictionary<string, RecipeEntry> RecipesByInternalName => _recipesByName;
        public IReadOnlyDictionary<string, SkillEntry> Skills => _skills;
        public IReadOnlyDictionary<string, XpTableEntry> XpTables => _xpTables;
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, QuestEntry> Quests { get; } = new Dictionary<string, QuestEntry>();
        public IReadOnlyDictionary<string, QuestEntry> QuestsByInternalName { get; } = new Dictionary<string, QuestEntry>();
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }

        public void AddSkill(string key, string[] parents, string? displayName = null)
            => _skills[key] = new SkillEntry(
                Key: key, DisplayName: displayName ?? key, Id: 0, Combat: false,
                XpTable: "TypicalNoncombatSkill", MaxBonusLevels: 0, Parents: parents,
                Rewards: new Dictionary<string, SkillRewardEntry>());

        private int _recipeSerial;
        public void AddRecipe(string rewardSkill)
        {
            var id = ++_recipeSerial;
            var key = $"recipe_{id}";
            var name = $"Test Recipe {id}";
            var entry = new RecipeEntry(
                Key: key, Name: name, InternalName: name, IconId: 0,
                Skill: rewardSkill, SkillLevelReq: 1,
                RewardSkill: rewardSkill, RewardSkillXp: 10, RewardSkillXpFirstTime: 20,
                RewardSkillXpDropOffLevel: null, RewardSkillXpDropOffPct: null, RewardSkillXpDropOffRate: null,
                Ingredients: [], ResultItems: []);
            _recipes[key] = entry;
            _recipesByName[name] = entry;
        }
    }
}
