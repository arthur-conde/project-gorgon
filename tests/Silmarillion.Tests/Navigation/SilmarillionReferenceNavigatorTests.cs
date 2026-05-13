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
    [InlineData(EntityKind.Landmark)]
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

    [Fact]
    public void Open_RecipeIngredientKeyword_SwitchesToRecipesTab_AndSetsQueryText()
    {
        // Arrange: build a real RecipeIngredientKeywordKindTarget backed by a
        // RecipesTabViewModel so we can assert on both SelectedTabIndex and QueryText.
        var refData = new FakeReferenceData();
        var recipesVm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));
        var keywordTarget = new RecipeIngredientKeywordKindTarget(recipesVm);

        var targets = new IReferenceKindTarget[]
        {
            new StubTarget(EntityKind.Item),
            new StubTarget(EntityKind.Recipe),
            keywordTarget,
        };
        var nav = new SilmarillionReferenceNavigator(targets);
        var silmarillionVm = new SilmarillionViewModel(items: null!, recipes: recipesVm, nav, targets);

        // Act
        nav.Open(EntityRef.RecipeIngredientKeyword("Crystal"));

        // Assert
        silmarillionVm.SelectedTabIndex.Should().Be(1);
        silmarillionVm.Recipes.QueryText.Should().Be("IngredientKeywords CONTAINS \"Crystal\"");
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
        public IReadOnlyList<string> Keys => Array.Empty<string>();
        public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>();
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>();
        public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Item>());
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, QuestEntry> Quests { get; } = new Dictionary<string, QuestEntry>();
        public IReadOnlyDictionary<string, QuestEntry> QuestsByInternalName { get; } = new Dictionary<string, QuestEntry>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>();
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
