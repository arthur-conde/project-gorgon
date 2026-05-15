using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.Navigation;

public sealed class SilmarillionReferenceNavigatorTests
{
    [Fact]
    public void Initial_CurrentIsNull_AndStacksEmpty()
    {
        var nav = new SilmarillionReferenceNavigator(NavTargets());

        nav.Current.Should().BeNull();
        nav.CanGoBack.Should().BeFalse();
        nav.CanGoForward.Should().BeFalse();
    }

    [Fact]
    public void Open_SetsCurrent_AndFiresNavigatedWithOpenKind()
    {
        var nav = new SilmarillionReferenceNavigator(NavTargets());
        NavigatedEventArgs? captured = null;
        nav.Navigated += (_, e) => captured = e;

        nav.Open(EntityRef.Item("Tomato"));

        nav.Current.Should().Be(EntityRef.Item("Tomato"));
        captured.Should().NotBeNull();
        captured!.Kind.Should().Be(NavigationKind.Open);
        captured.Previous.Should().BeNull();
        captured.Current.Should().Be(EntityRef.Item("Tomato"));
    }

    [Fact]
    public void Open_AfterFirstOpen_PushesPreviousToBackStack()
    {
        var nav = new SilmarillionReferenceNavigator(NavTargets());
        nav.Open(EntityRef.Item("Tomato"));
        nav.Open(EntityRef.Recipe("MakeSalsa"));

        nav.Current.Should().Be(EntityRef.Recipe("MakeSalsa"));
        nav.CanGoBack.Should().BeTrue();
        nav.CanGoForward.Should().BeFalse();
    }

    [Fact]
    public void Open_ClearsForwardStack()
    {
        var nav = new SilmarillionReferenceNavigator(NavTargets());
        nav.Open(EntityRef.Item("A"));
        nav.Open(EntityRef.Item("B"));
        nav.Back();
        nav.CanGoForward.Should().BeTrue();

        nav.Open(EntityRef.Item("C"));

        nav.CanGoForward.Should().BeFalse();
    }

    [Fact]
    public void Back_PopsBackStack_PushesForward_FiresNavigatedWithBackKind()
    {
        var nav = new SilmarillionReferenceNavigator(NavTargets());
        nav.Open(EntityRef.Item("A"));
        nav.Open(EntityRef.Item("B"));
        NavigatedEventArgs? captured = null;
        nav.Navigated += (_, e) => captured = e;

        nav.Back();

        nav.Current.Should().Be(EntityRef.Item("A"));
        nav.CanGoBack.Should().BeFalse();
        nav.CanGoForward.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.Kind.Should().Be(NavigationKind.Back);
        captured.Previous.Should().Be(EntityRef.Item("B"));
        captured.Current.Should().Be(EntityRef.Item("A"));
    }

    [Fact]
    public void Forward_PopsForwardStack_PushesBack_FiresNavigatedWithForwardKind()
    {
        var nav = new SilmarillionReferenceNavigator(NavTargets());
        nav.Open(EntityRef.Item("A"));
        nav.Open(EntityRef.Item("B"));
        nav.Back();
        NavigatedEventArgs? captured = null;
        nav.Navigated += (_, e) => captured = e;

        nav.Forward();

        nav.Current.Should().Be(EntityRef.Item("B"));
        nav.CanGoBack.Should().BeTrue();
        nav.CanGoForward.Should().BeFalse();
        captured.Should().NotBeNull();
        captured!.Kind.Should().Be(NavigationKind.Forward);
    }

    [Fact]
    public void Back_WhenStackEmpty_IsNoOp_AndDoesNotFireNavigated()
    {
        var nav = new SilmarillionReferenceNavigator(NavTargets());
        var fired = false;
        nav.Navigated += (_, _) => fired = true;

        nav.Back();

        fired.Should().BeFalse();
        nav.Current.Should().BeNull();
    }

    [Fact]
    public void Forward_WhenStackEmpty_IsNoOp_AndDoesNotFireNavigated()
    {
        var nav = new SilmarillionReferenceNavigator(NavTargets());
        nav.Open(EntityRef.Item("A"));
        var fired = false;
        nav.Navigated += (_, _) => fired = true;

        nav.Forward();

        fired.Should().BeFalse();
    }

    [Fact]
    public void OpenSameEntityTwice_StillPushesToBackStack()
    {
        // "Open same item again" is still a history step. User can hit Back to undo.
        var nav = new SilmarillionReferenceNavigator(NavTargets());
        nav.Open(EntityRef.Item("A"));
        nav.Open(EntityRef.Item("A"));

        nav.CanGoBack.Should().BeTrue();
    }

    [Fact]
    public void CanOpen_RegisteredKind_ReturnsTrue()
    {
        var nav = new SilmarillionReferenceNavigator(NavTargets());

        nav.CanOpen(EntityRef.Item("anything")).Should().BeTrue();
        nav.CanOpen(EntityRef.Recipe("anything")).Should().BeTrue();
    }

    [Theory]
    [InlineData(EntityKind.Ability)]
    [InlineData(EntityKind.Npc)]
    [InlineData(EntityKind.Quest)]
    [InlineData(EntityKind.Lorebook)]
    [InlineData(EntityKind.Area)]
    [InlineData(EntityKind.PlayerTitle)]
    [InlineData(EntityKind.StorageVault)]
    [InlineData(EntityKind.Effect)]
    public void CanOpen_UnregisteredKind_ReturnsFalse(EntityKind kind)
    {
        var nav = new SilmarillionReferenceNavigator(NavTargets());

        nav.CanOpen(new EntityRef(kind, "anything")).Should().BeFalse();
    }

    [Fact]
    public void Constructor_DuplicateKind_Throws()
    {
        var targets = new IReferenceKindTarget[]
        {
            new StubTarget(EntityKind.Item),
            new StubTarget(EntityKind.Item),
        };
        var act = () => new SilmarillionReferenceNavigator(targets);
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*Duplicate IReferenceKindTarget*Item*");
    }

    [Fact]
    public void Back_PreservesDeepHistory_WhenMultipleEntitiesOnStack()
    {
        var nav = new SilmarillionReferenceNavigator(NavTargets());
        nav.Open(EntityRef.Item("A"));
        nav.Open(EntityRef.Recipe("B"));
        nav.Open(EntityRef.Item("C"));
        nav.Open(EntityRef.Recipe("D"));

        nav.Back();
        nav.Back();
        nav.Back();

        nav.Current.Should().Be(EntityRef.Item("A"));
        nav.CanGoBack.Should().BeFalse();
        nav.CanGoForward.Should().BeTrue();
    }

    [Fact]
    public void Forward_AfterMultipleBacks_RestoresIntermediateState()
    {
        var nav = new SilmarillionReferenceNavigator(NavTargets());
        nav.Open(EntityRef.Item("A"));
        nav.Open(EntityRef.Item("B"));
        nav.Open(EntityRef.Item("C"));

        nav.Back();   // C → B
        nav.Back();   // B → A
        nav.Forward();// A → B

        nav.Current.Should().Be(EntityRef.Item("B"));
        nav.CanGoBack.Should().BeTrue();
        nav.CanGoForward.Should().BeTrue();
    }

    // Open_RecipeIngredientKeyword_SwitchesToRecipesTab_AndSetsQueryText and
    // Open_RecipeIngredientKeyword_ClearsResidualSelectedRow were removed in #318 slice 4
    // (surface 2): the synthetic RecipeIngredientKeyword kind / its kind target / its
    // mithril://silmarillion/RecipeIngredientKeyword/… route are deleted. The item-detail
    // "Used as" surface is now a provenance popup fed RecipesByIngredientKeywordWithReason
    // directly (covered in ItemsTabViewModelTests; route-rejection covered below).

    // #318 slice 4 (surface 3): the recipe-detail keyword surface is now a provenance
    // popup fed IReferenceDataService.ItemsByRecipeKeywordSlotWithReason directly; the
    // EntityKind.ItemKeyword synthetic kind + ItemKeywordKindTarget were deleted, so the
    // three Open_ItemKeyword_* navigator tests (query-string re-derivation behaviour) are
    // removed with them. The popup-from-index path is covered by
    // RecipesTabViewModelTests (KeywordSlot_* — membership == index, no-history) and
    // ReferenceDataServiceRecipeCrossLinkIndexTests (Gate C). The deep-link route is
    // covered by SilmarillionDeepLinkHandlerTests.TryHandle_RetiredItemKeywordRoute_IsRejected.

    [Fact]
    public void Open_Effect_SwitchesToEffectsTab_AndSelects()
    {
        var refData = new FakeReferenceData();
        refData.AddEffect(new Mithril.Reference.Models.Effects.Effect { InternalName = "effect_10003", Name = "Sticky!", IconId = 42 });
        var settings = new Silmarillion.SilmarillionSettings();
        var effectsVm = new EffectsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData), settings);
        var effectsTarget = new EffectsKindTarget(effectsVm);

        var targets = new IReferenceKindTarget[]
        {
            new StubTarget(EntityKind.Item),
            new StubTarget(EntityKind.Recipe),
            effectsTarget,
        };
        var nav = new SilmarillionReferenceNavigator(targets);
        var silmarillionVm = new SilmarillionViewModel(new ITabViewModel[] { effectsVm }, nav, targets);

        nav.Open(EntityRef.Effect("effect_10003"));

        silmarillionVm.SelectedTabIndex.Should().Be(5);
        effectsVm.SelectedRow.Should().NotBeNull();
        effectsVm.SelectedRow!.EnvelopeKey.Should().Be("effect_10003");
    }

    [Fact]
    public void Open_EffectKeyword_SwitchesToEffectsTab_AndSetsQueryText()
    {
        var refData = new FakeReferenceData();
        var settings = new Silmarillion.SilmarillionSettings();
        var effectsVm = new EffectsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData), settings);
        var keywordTarget = new EffectKeywordKindTarget(effectsVm);

        var targets = new IReferenceKindTarget[]
        {
            new StubTarget(EntityKind.Item),
            new StubTarget(EntityKind.Recipe),
            keywordTarget,
        };
        var nav = new SilmarillionReferenceNavigator(targets);
        var silmarillionVm = new SilmarillionViewModel(new ITabViewModel[] { effectsVm }, nav, targets);

        nav.Open(EntityRef.EffectKeyword("FrostShard"));

        silmarillionVm.SelectedTabIndex.Should().Be(5);
        effectsVm.QueryText.Should().Be("Keywords CONTAINS \"FrostShard\"");
    }

    // (Removed) Open_AbilityByEffectKeyword_… — the AbilityByEffectKeyword synthetic kind
    // + its kind target were deleted in #318. The effect→abilities 1:N surface now opens a
    // provenance popup fed the index directly (no navigator round-trip, no query
    // re-derivation). Popup-level coverage lives in EffectsTabViewModelTests.

    [Fact]
    public void Open_Area_SwitchesToAreasTab_AndSelects()
    {
        var refData = new FakeReferenceData();
        refData.AddArea(new AreaEntry("AreaSerbule", "Serbule", "Serbule"));
        var settings = new Silmarillion.SilmarillionSettings();
        var areasVm = new AreasTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData), settings);
        var areasTarget = new AreasKindTarget(areasVm);

        var targets = new IReferenceKindTarget[]
        {
            new StubTarget(EntityKind.Item),
            new StubTarget(EntityKind.Recipe),
            areasTarget,
        };
        var nav = new SilmarillionReferenceNavigator(targets);
        var silmarillionVm = new SilmarillionViewModel(new ITabViewModel[] { areasVm }, nav, targets);

        nav.Open(EntityRef.Area("AreaSerbule"));

        silmarillionVm.SelectedTabIndex.Should().Be(6);
        areasVm.SelectedArea.Should().NotBeNull();
        areasVm.SelectedArea!.Key.Should().Be("AreaSerbule");
    }

    [Fact]
    public void Open_Lorebook_SwitchesToLorebooksTab_AndSelects()
    {
        var refData = new FakeReferenceData();
        refData.AddLorebook("Book_101", new Mithril.Reference.Models.Misc.Lorebook
        {
            Category = "Stories", InternalName = "TheWastedWishes",
            Title = "The Wasted Wishes", Text = "<h1>The Wasted Wishes</h1>body",
        });
        var settings = new Silmarillion.SilmarillionSettings();
        var lorebooksVm = new LorebooksTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData), settings);
        var lorebooksTarget = new LorebooksKindTarget(lorebooksVm);

        var targets = new IReferenceKindTarget[]
        {
            new StubTarget(EntityKind.Item),
            new StubTarget(EntityKind.Recipe),
            lorebooksTarget,
        };
        var nav = new SilmarillionReferenceNavigator(targets);
        var silmarillionVm = new SilmarillionViewModel(new ITabViewModel[] { lorebooksVm }, nav, targets);

        // Deep-link form mithril://silmarillion/lorebook/TheWastedWishes flows through the
        // target-agnostic handler → EntityRef.Lorebook → this kind target.
        nav.Open(EntityRef.Lorebook("TheWastedWishes"));

        silmarillionVm.SelectedTabIndex.Should().Be(7);
        lorebooksVm.SelectedLorebook.Should().NotBeNull();
        lorebooksVm.SelectedLorebook!.InternalName.Should().Be("TheWastedWishes");
    }

    [Fact]
    public void Open_Lorebook_UnknownName_DoesNotSelect()
    {
        var refData = new FakeReferenceData();
        var settings = new Silmarillion.SilmarillionSettings();
        var lorebooksVm = new LorebooksTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData), settings);
        var targets = new IReferenceKindTarget[]
        {
            new StubTarget(EntityKind.Item),
            new LorebooksKindTarget(lorebooksVm),
        };
        var nav = new SilmarillionReferenceNavigator(targets);

        nav.Open(EntityRef.Lorebook("NoSuchBook"));

        lorebooksVm.SelectedLorebook.Should().BeNull();
    }

    [Fact]
    public void Constructor_DuplicateGuard_TripsForLorebook()
    {
        // Verifies the duplicate-registration guard still trips with the new kind in the mix.
        var dup = new IReferenceKindTarget[]
        {
            new StubTarget(EntityKind.Lorebook),
            new StubTarget(EntityKind.Lorebook),
        };
        Action act = () => new SilmarillionReferenceNavigator(dup);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate IReferenceKindTarget*Lorebook*");
    }

    [Fact]
    public void Open_PlayerTitle_SwitchesToPlayerTitlesTab_AndSelects()
    {
        var refData = new FakeReferenceData();
        refData.AddPlayerTitle("Title_5018", new Mithril.Reference.Models.Misc.PlayerTitle
        {
            Title = "<color=#00cc00>Warsmith</color>", Tooltip = "Earned by completing a quest.",
        });
        var playerTitlesVm = new PlayerTitlesTabViewModel(refData);
        var playerTitlesTarget = new PlayerTitlesKindTarget(playerTitlesVm);

        var targets = new IReferenceKindTarget[]
        {
            new StubTarget(EntityKind.Item),
            new StubTarget(EntityKind.Recipe),
            playerTitlesTarget,
        };
        var nav = new SilmarillionReferenceNavigator(targets);
        var silmarillionVm = new SilmarillionViewModel(new ITabViewModel[] { playerTitlesVm }, nav, targets);

        // Deep-link form mithril://silmarillion/playertitle/Title_5018 flows through
        // the target-agnostic handler → EntityRef.PlayerTitle → this kind target.
        // Selection key is the "Title_N" envelope key (the POCO has no InternalName).
        nav.Open(EntityRef.PlayerTitle("Title_5018"));

        silmarillionVm.SelectedTabIndex.Should().Be(8);
        playerTitlesVm.SelectedTitle.Should().NotBeNull();
        playerTitlesVm.SelectedTitle!.EnvelopeKey.Should().Be("Title_5018");
    }

    [Fact]
    public void Open_PlayerTitle_UnknownKey_DoesNotSelect()
    {
        var refData = new FakeReferenceData();
        var playerTitlesVm = new PlayerTitlesTabViewModel(refData);
        var targets = new IReferenceKindTarget[]
        {
            new StubTarget(EntityKind.Item),
            new PlayerTitlesKindTarget(playerTitlesVm),
        };
        var nav = new SilmarillionReferenceNavigator(targets);

        nav.Open(EntityRef.PlayerTitle("Title_999999"));

        playerTitlesVm.SelectedTitle.Should().BeNull();
    }

    [Fact]
    public void Constructor_DuplicateGuard_TripsForPlayerTitle()
    {
        var dup = new IReferenceKindTarget[]
        {
            new StubTarget(EntityKind.PlayerTitle),
            new StubTarget(EntityKind.PlayerTitle),
        };
        Action act = () => new SilmarillionReferenceNavigator(dup);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate IReferenceKindTarget*PlayerTitle*");
    }

    [Fact]
    public void Constructor_DuplicateGuard_TripsForEffectAndEffectKeyword()
    {
        // Verifies the duplicate-registration guard still trips with the new kinds in the mix.
        var dupEffect = new IReferenceKindTarget[]
        {
            new StubTarget(EntityKind.Effect),
            new StubTarget(EntityKind.Effect),
        };
        Action act1 = () => new SilmarillionReferenceNavigator(dupEffect);
        act1.Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate IReferenceKindTarget*Effect*");

        var dupEffectKeyword = new IReferenceKindTarget[]
        {
            new StubTarget(EntityKind.EffectKeyword),
            new StubTarget(EntityKind.EffectKeyword),
        };
        Action act2 = () => new SilmarillionReferenceNavigator(dupEffectKeyword);
        act2.Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate IReferenceKindTarget*EffectKeyword*");
    }

    private static IEnumerable<IReferenceKindTarget> NavTargets() => new IReferenceKindTarget[]
    {
        new StubTarget(EntityKind.Item),
        new StubTarget(EntityKind.Recipe),
    };

    private sealed class StubTarget : IReferenceKindTarget
    {
        public StubTarget(EntityKind kind) => Kind = kind;
        public EntityKind Kind { get; }
        public int TabIndex => 0;
        public bool TrySelectByInternalName(string internalName) => true;
        public bool TryOpenInWindow() => false;
    }

    private sealed class FakeReferenceData : IReferenceDataService
    {
        private readonly Dictionary<string, Recipe> _recipes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Recipe> _recipesByInternalName = new(StringComparer.Ordinal);
        private readonly Dictionary<long, Item> _itemsById = new();
        private readonly Dictionary<string, Item> _itemsByInternalName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Mithril.Reference.Models.Effects.Effect> _effects = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Mithril.Reference.Models.Abilities.Ability> _abilitiesByInternalName = new(StringComparer.Ordinal);

        public void AddRecipe(Recipe recipe)
        {
            _recipes[recipe.Key] = recipe;
            if (recipe.InternalName is not null) _recipesByInternalName[recipe.InternalName] = recipe;
        }

        public void AddItem(Item item)
        {
            _itemsById[item.Id] = item;
            if (item.InternalName is not null) _itemsByInternalName[item.InternalName] = item;
        }

        public void AddEffect(Mithril.Reference.Models.Effects.Effect effect)
        {
            if (effect.InternalName is not null) _effects[effect.InternalName] = effect;
        }

        public void AddAbility(Mithril.Reference.Models.Abilities.Ability ability)
        {
            if (ability.InternalName is not null) _abilitiesByInternalName[ability.InternalName] = ability;
        }

        private readonly Dictionary<string, AreaEntry> _areas = new(StringComparer.Ordinal);
        public void AddArea(AreaEntry area) => _areas[area.Key] = area;

        private readonly Dictionary<string, Mithril.Reference.Models.Misc.Lorebook> _lorebooks = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Mithril.Reference.Models.Misc.Lorebook> _lorebooksByName = new(StringComparer.Ordinal);
        public void AddLorebook(string envelopeKey, Mithril.Reference.Models.Misc.Lorebook book)
        {
            _lorebooks[envelopeKey] = book;
            if (book.InternalName is not null) _lorebooksByName[book.InternalName] = book;
        }
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Misc.Lorebook> Lorebooks => _lorebooks;
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Misc.Lorebook> LorebooksByInternalName => _lorebooksByName;

        private readonly Dictionary<string, Mithril.Reference.Models.Misc.PlayerTitle> _playerTitles = new(StringComparer.Ordinal);
        public void AddPlayerTitle(string envelopeKey, Mithril.Reference.Models.Misc.PlayerTitle title)
            => _playerTitles[envelopeKey] = title;
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Misc.PlayerTitle> PlayerTitles => _playerTitles;

        public IReadOnlyList<string> Keys => Array.Empty<string>();
        public IReadOnlyDictionary<long, Item> Items => _itemsById;
        public IReadOnlyDictionary<string, Item> ItemsByInternalName => _itemsByInternalName;
        public ItemKeywordIndex KeywordIndex => new(_itemsById);
        public IReadOnlyDictionary<string, Recipe> Recipes => _recipes;
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName => _recipesByInternalName;
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, AreaEntry> Areas => _areas;
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> Quests { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> QuestsByInternalName { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Effects.Effect> Effects => _effects;
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Effects.Effect> EffectsByInternalName => _effects;
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Abilities.Ability> AbilitiesByInternalName => _abilitiesByInternalName;
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
