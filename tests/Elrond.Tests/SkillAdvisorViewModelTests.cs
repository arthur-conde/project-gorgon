using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using System.Linq;
using Elrond.Domain;
using Elrond.Services;
using Elrond.ViewModels;
using FluentAssertions;
using Mithril.Leveling;
using Mithril.Planning;
using Mithril.Shared.Character;
using Mithril.Shared.Crafting;
using Mithril.Shared.Reference;
using Mithril.Shared.Storage;
using Xunit;

namespace Elrond.Tests;

/// <summary>
/// Coverage for the flat skill picker (#129).
///
/// These tests exercise <see cref="SkillAdvisorViewModel.BuildSkillNodes"/> and
/// <see cref="SkillAdvisorViewModel.SelectSkillFromDeepLink"/> through the
/// public observable surface — wrapping the engine + reference data in fakes
/// so we can stage active-character state without spinning up the file-IO layer.
/// </summary>
public class SkillAdvisorViewModelTests
{
    [Fact]
    public void BuildSkillNodes_OmitsSkillsCharacterDoesNotKnow()
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

        vm.SkillNodes.Should().ContainSingle();
        vm.SkillNodes[0].Key.Should().Be("Cooking");
    }

    [Fact]
    public void BuildSkillNodes_SortsAlphabeticallyByDisplayName()
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

        vm.SkillNodes.Select(n => n.DisplayName).Should().Equal(["Apple", "Banana", "Cherry"]);
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

    // ── Query box ↔ sort chips ───────────────────────────────────────────

    [Fact]
    public void DefaultQueryText_SeedsEffectiveXpDescending()
    {
        var (vm, _, _, _) = MakeFixture(
            characterSkills: new Dictionary<string, CharacterSkill> { ["Cooking"] = new(10, 0, 0, 100) },
            register: r => { r.AddSkill("Cooking", parents: []); r.AddRecipe(rewardSkill: "Cooking"); });

        // No persisted settings → default seed. The ORDER BY clause drives
        // ListCollectionView.CustomSort (not SortDescriptions, which are mutex with
        // CustomSort in WPF). Inspect CustomSort instead.
        vm.QueryText.Should().Be("ORDER BY EffectiveXp DESC");
        ((System.Windows.Data.ListCollectionView)vm.RecipesView).CustomSort.Should().NotBeNull();
    }

    [Fact]
    public void SettingQueryText_DrivesCustomSort()
    {
        var (vm, _, _, _) = MakeFixture(
            characterSkills: new Dictionary<string, CharacterSkill> { ["Cooking"] = new(10, 0, 0, 100) },
            register: r => { r.AddSkill("Cooking", parents: []); r.AddRecipe(rewardSkill: "Cooking"); });

        vm.QueryText = "ORDER BY RecipeName";

        ((System.Windows.Data.ListCollectionView)vm.RecipesView).CustomSort.Should().NotBeNull();
        vm.QueryError.Should().BeNull();
    }

    [Fact]
    public void ToggleChipCommand_AppendsOrderByClause()
    {
        var (vm, _, _, _) = MakeFixture(
            characterSkills: new Dictionary<string, CharacterSkill> { ["Cooking"] = new(10, 0, 0, 100) },
            register: r => { r.AddSkill("Cooking", parents: []); r.AddRecipe(rewardSkill: "Cooking"); });

        // Start from a clean slate so the toggle result is unambiguous.
        vm.QueryText = "";
        vm.ToggleChipCommand.Execute("EffectiveXp");

        vm.QueryText.Should().Be("ORDER BY EffectiveXp DESC");
    }

    [Fact]
    public void ToggleChipCommand_Twice_FlipsThenRemoves()
    {
        var (vm, _, _, _) = MakeFixture(
            characterSkills: new Dictionary<string, CharacterSkill> { ["Cooking"] = new(10, 0, 0, 100) },
            register: r => { r.AddSkill("Cooking", parents: []); r.AddRecipe(rewardSkill: "Cooking"); });

        vm.QueryText = "";
        vm.ToggleChipCommand.Execute("RecipeName");      // append ascending (RecipeName default asc)
        vm.QueryText.Should().Be("ORDER BY RecipeName");

        vm.ToggleChipCommand.Execute("RecipeName");      // flip to descending
        vm.QueryText.Should().Be("ORDER BY RecipeName DESC");

        vm.ToggleChipCommand.Execute("RecipeName");      // remove
        vm.QueryText.Should().Be("");
    }

    [Fact]
    public void Chips_ReflectActiveOrderState()
    {
        var (vm, _, _, _) = MakeFixture(
            characterSkills: new Dictionary<string, CharacterSkill> { ["Cooking"] = new(10, 0, 0, 100) },
            register: r => { r.AddSkill("Cooking", parents: []); r.AddRecipe(rewardSkill: "Cooking"); });

        vm.QueryText = "ORDER BY Complexity";

        var complexity = vm.Chips.Single(c => c.Key.Id == "Complexity");
        complexity.IsActive.Should().BeTrue();
        complexity.OrderIndex.Should().Be(0);
        vm.Chips.Single(c => c.Key.Id == "RecipeName").IsActive.Should().BeFalse();
    }

    [Fact]
    public void InvalidQuery_SetsQueryErrorAndKeepsPreviousSort()
    {
        var (vm, _, _, _) = MakeFixture(
            characterSkills: new Dictionary<string, CharacterSkill> { ["Cooking"] = new(10, 0, 0, 100) },
            register: r => { r.AddSkill("Cooking", parents: []); r.AddRecipe(rewardSkill: "Cooking"); });

        vm.QueryText = "ORDER BY RecipeName";
        var lcv = (System.Windows.Data.ListCollectionView)vm.RecipesView;
        var goodSort = lcv.CustomSort;
        goodSort.Should().NotBeNull();

        vm.QueryText = "LevelRequired >>> 5";   // malformed

        vm.QueryError.Should().NotBeNull();
        // Previous sort retained — controller never received a new parsed order, so
        // CustomSort is unchanged.
        lcv.CustomSort.Should().BeSameAs(goodSort);
    }

    [Fact]
    public void QueryText_MigratesFromLegacyActiveSortKeys()
    {
        var refData = new FakeRefData();
        refData.AddSkill("Cooking", parents: []);
        refData.AddRecipe(rewardSkill: "Cooking");
        var characterSvc = new FakeActiveCharacterService();
        characterSvc.SetCharacter(new CharacterSnapshot(
            "C", "S", DateTimeOffset.UtcNow,
            new Dictionary<string, CharacterSkill> { ["Cooking"] = new(10, 0, 0, 100) },
            new Dictionary<string, int>(), new Dictionary<string, string>()));

        var settings = new ElrondSettings
        {
            ActiveSortKeys =
            [
                new("EffectiveXp", System.ComponentModel.ListSortDirection.Descending),
                new("RecipeName",  System.ComponentModel.ListSortDirection.Ascending),
            ],
        };
        var engine = new SkillAdvisorEngine(refData);
        var vm = new SkillAdvisorViewModel(engine, characterSvc, refData, settings,
            Gen(refData, characterSvc));

        vm.QueryText.Should().Be("ORDER BY EffectiveXp DESC, RecipeName");
        settings.ActiveSortKeys.Should().BeEmpty("legacy field cleared after migration");
        settings.LastQueryText.Should().Be("ORDER BY EffectiveXp DESC, RecipeName");
    }

    // ── Send to craft list (#224) ────────────────────────────────────────

    [Fact]
    public void SendToCraftList_NoTargetRegistered_CommandDisabled()
    {
        var (vm, _, _, _) = MakeFixture(
            characterSkills: new Dictionary<string, CharacterSkill> { ["Cooking"] = new(10, 0, 0, 100) },
            register: r => { r.AddSkill("Cooking", parents: []); r.AddRecipe(rewardSkill: "Cooking"); });

        vm.SelectedSkill = "Cooking";
        vm.SelectedRecipe = vm.Analysis!.Recipes.First();

        vm.IsCraftListImportAvailable.Should().BeFalse();
        vm.SendToCraftListCommand.CanExecute(null).Should().BeFalse(
            because: "no craft-list import target (Celebrimbor) was registered");
    }

    [Fact]
    public void SendToCraftList_NoSelection_CommandDisabled()
    {
        var vm = MakeVmWithImportTarget(out _, out var importTarget);

        vm.SelectedRecipe.Should().BeNull();
        vm.IsCraftListImportAvailable.Should().BeTrue();
        vm.SendToCraftListCommand.CanExecute(null).Should().BeFalse(
            because: "no recipe is selected");
        importTarget.LastRecipes.Should().BeNull();
    }

    [Fact]
    public void SendToCraftList_SendsSelectedRecipeAtQuantityOne()
    {
        var vm = MakeVmWithImportTarget(out _, out var importTarget);

        vm.SelectedSkill = "Cooking";
        var recipe = vm.Analysis!.Recipes.First();
        vm.SelectedRecipe = recipe;

        vm.SendToCraftListCommand.CanExecute(null).Should().BeTrue();
        vm.SendToCraftListCommand.Execute(null);

        importTarget.LastRecipes.Should().ContainSingle();
        importTarget.LastRecipes![0].RecipeInternalName.Should().Be(recipe.InternalName);
        importTarget.LastRecipes![0].Quantity.Should().Be(1,
            because: "v1 sends quantity 1; the user dials in counts in Celebrimbor");
        importTarget.LastSource.Should().Contain(recipe.RecipeName);
    }

    [Fact]
    public void Ctor_DoesNotResolveImportTarget_DiCycleRegressionGuard()
    {
        // The #359 deadlock: the VM factory resolved ICraftListImportTarget eagerly at
        // construction, closing a DI cycle (→ Celebrimbor → IModuleActivator →
        // ShellViewModel → eager ActivateModule → back here) that MS.DI turns into a
        // silent UI-thread hang. Construction must NEVER invoke the accessor — only the
        // explicit Send click may. An accessor that throws if touched proves it.
        var refData = new FakeRefData();
        refData.AddSkill("Cooking", parents: []);
        refData.AddRecipe(rewardSkill: "Cooking");
        var characterSvc = new FakeActiveCharacterService();
        characterSvc.SetCharacter(new CharacterSnapshot(
            "C", "S", DateTimeOffset.UtcNow,
            new Dictionary<string, CharacterSkill> { ["Cooking"] = new(10, 0, 0, 100) },
            new Dictionary<string, int>(), new Dictionary<string, string>()));
        var engine = new SkillAdvisorEngine(refData);

        var act = () => new SkillAdvisorViewModel(
            engine, characterSvc, refData, new ElrondSettings(),
            Gen(refData, characterSvc),
            () => throw new InvalidOperationException("import target resolved during construction"));

        act.Should().NotThrow(
            because: "the VM ctor must not resolve the import target — that edge is the #359 DI cycle");
    }

    // #228 PR-B/B2: SkillAdvisorViewModel now hosts the Generate-plan child VM.
    private static GenerateLevelingPlanViewModel Gen(IReferenceDataService refData, IActiveCharacterService chr)
        => new(chr, new CrossSkillPlanner(refData, new LevelingMath(refData), new RecipeExpander(refData)), refData);

    private static SkillAdvisorViewModel MakeVmWithImportTarget(
        out FakeRefData refData, out FakeCraftListImportTarget importTarget)
    {
        refData = new FakeRefData();
        refData.AddSkill("Cooking", parents: []);
        refData.AddRecipe(rewardSkill: "Cooking");
        var characterSvc = new FakeActiveCharacterService();
        characterSvc.SetCharacter(new CharacterSnapshot(
            "C", "S", DateTimeOffset.UtcNow,
            new Dictionary<string, CharacterSkill> { ["Cooking"] = new(10, 0, 0, 100) },
            new Dictionary<string, int>(), new Dictionary<string, string>()));
        var engine = new SkillAdvisorEngine(refData);
        importTarget = new FakeCraftListImportTarget();
        var captured = importTarget;
        return new SkillAdvisorViewModel(engine, characterSvc, refData, new ElrondSettings(),
            Gen(refData, characterSvc),
            () => captured);
    }

    private sealed class FakeCraftListImportTarget : Mithril.Shared.Modules.ICraftListImportTarget
    {
        public IReadOnlyList<Mithril.Shared.Modules.CraftListImportEntry>? LastRecipes { get; private set; }
        public string? LastSource { get; private set; }
        public void ImportFromLinkPayload(string base64UrlPayload) { }
        public void ImportRecipes(IReadOnlyList<Mithril.Shared.Modules.CraftListImportEntry> recipes, string source)
        {
            LastRecipes = recipes;
            LastSource = source;
        }
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
        var vm = new SkillAdvisorViewModel(engine, characterSvc, refData, settings,
            Gen(refData, characterSvc));
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
        private readonly Dictionary<string, Recipe> _recipes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Recipe> _recipesByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SkillEntry> _skills = new(StringComparer.Ordinal);
        private readonly Dictionary<string, XpTableEntry> _xpTables = new(StringComparer.Ordinal);

        public IReadOnlyList<string> Keys { get; } = ["items", "recipes", "skills", "xptables"];
        public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>();
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>();
        public ItemKeywordIndex KeywordIndex { get; } = ItemKeywordIndex.Empty;
        public IReadOnlyDictionary<string, Recipe> Recipes => _recipes;
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName => _recipesByName;
        public IReadOnlyDictionary<string, SkillEntry> Skills => _skills;
        public IReadOnlyDictionary<string, XpTableEntry> XpTables => _xpTables;
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> Quests { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> QuestsByInternalName { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
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
            var entry = new Recipe
            {
                Key = key,
                Name = name,
                InternalName = name,
                IconId = 0,
                Skill = rewardSkill,
                SkillLevelReq = 1,
                RewardSkill = rewardSkill,
                RewardSkillXp = 10,
                RewardSkillXpFirstTime = 20,
                Ingredients = [],
                ResultItems = [],
            };
            _recipes[key] = entry;
            _recipesByName[name] = entry;
        }
    }
}
