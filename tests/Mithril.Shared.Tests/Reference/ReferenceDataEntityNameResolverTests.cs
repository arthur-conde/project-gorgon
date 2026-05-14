using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Npcs;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Reference;

public sealed class ReferenceDataEntityNameResolverTests
{
    [Fact]
    public void Item_ResolvesPocoName_WhenItemPresentWithName()
    {
        var refData = new ResolverStub
        {
            ItemsByInternalNameMap =
            {
                ["Tomato"] = new Item { Id = 1, InternalName = "Tomato", Name = "Tomato" },
            },
        };
        var resolver = new ReferenceDataEntityNameResolver(refData);

        resolver.Resolve(EntityRef.Item("Tomato")).Should().Be("Tomato");
    }

    [Fact]
    public void Item_FallsBackToInternalName_WhenItemAbsent()
    {
        var resolver = new ReferenceDataEntityNameResolver(new ResolverStub());

        resolver.Resolve(EntityRef.Item("UnknownItem")).Should().Be("UnknownItem");
    }

    [Fact]
    public void Item_FallsBackToInternalName_WhenPocoNameIsEmpty()
    {
        var refData = new ResolverStub
        {
            ItemsByInternalNameMap =
            {
                ["Nameless"] = new Item { Id = 2, InternalName = "Nameless", Name = "" },
            },
        };
        var resolver = new ReferenceDataEntityNameResolver(refData);

        resolver.Resolve(EntityRef.Item("Nameless")).Should().Be("Nameless");
    }

    [Fact]
    public void Recipe_ResolvesPocoName_WhenRecipePresentWithName()
    {
        var refData = new ResolverStub
        {
            RecipesByInternalNameMap =
            {
                ["BakeBread"] = new Recipe { Key = "r1", InternalName = "BakeBread", Name = "Bake Bread" },
            },
        };
        var resolver = new ReferenceDataEntityNameResolver(refData);

        resolver.Resolve(EntityRef.Recipe("BakeBread")).Should().Be("Bake Bread");
    }

    [Fact]
    public void Recipe_FallsBackToInternalName_WhenRecipeAbsent()
    {
        var resolver = new ReferenceDataEntityNameResolver(new ResolverStub());

        resolver.Resolve(EntityRef.Recipe("UnknownRecipe")).Should().Be("UnknownRecipe");
    }

    [Fact]
    public void Recipe_FallsBackToInternalName_WhenPocoNameIsEmpty()
    {
        var refData = new ResolverStub
        {
            RecipesByInternalNameMap =
            {
                ["Nameless"] = new Recipe { Key = "r2", InternalName = "Nameless", Name = "" },
            },
        };
        var resolver = new ReferenceDataEntityNameResolver(refData);

        resolver.Resolve(EntityRef.Recipe("Nameless")).Should().Be("Nameless");
    }

    [Fact]
    public void Npc_ResolvesPocoName_WhenNpcPresentWithName()
    {
        var refData = new ResolverStub
        {
            NpcsByInternalNameMap =
            {
                ["NPC_Joeh"] = new Npc { Name = "Joeh" },
            },
        };
        var resolver = new ReferenceDataEntityNameResolver(refData);

        resolver.Resolve(EntityRef.Npc("NPC_Joeh")).Should().Be("Joeh");
    }

    [Fact]
    public void Npc_StripsNpcPrefix_WhenNpcAbsent()
    {
        // Unnamed envelope keys (altars, placeholders without a Name field) fall back to a
        // leading "NPC_" strip so the chip still reads as "Joeh" rather than "NPC_Joeh".
        var resolver = new ReferenceDataEntityNameResolver(new ResolverStub());

        resolver.Resolve(EntityRef.Npc("NPC_Joeh")).Should().Be("Joeh");
    }

    [Fact]
    public void Npc_StripsNpcPrefix_WhenPocoNameIsEmpty()
    {
        var refData = new ResolverStub
        {
            NpcsByInternalNameMap =
            {
                ["NPC_Nameless"] = new Npc { Name = null },
            },
        };
        var resolver = new ReferenceDataEntityNameResolver(refData);

        resolver.Resolve(EntityRef.Npc("NPC_Nameless")).Should().Be("Nameless");
    }

    [Fact]
    public void Npc_LeavesEnvelopeKeyIntact_WhenNoNpcPrefix()
    {
        // "Altar_Druid", "SpiderPlaceholder" — envelope keys without the "NPC_" prefix pass
        // through verbatim. Confirms the strip is prefix-anchored, not a blanket "strip up to
        // first underscore" rule.
        var resolver = new ReferenceDataEntityNameResolver(new ResolverStub());

        resolver.Resolve(EntityRef.Npc("Altar_Druid")).Should().Be("Altar_Druid");
    }

    [Fact]
    public void UnknownKind_ReturnsInternalNameVerbatim()
    {
        // Kinds the resolver doesn't yet have a case for (e.g. Quest before Quests tab lands)
        // return InternalName unchanged so callers render *something* readable rather than empty.
        var resolver = new ReferenceDataEntityNameResolver(new ResolverStub());

        resolver.Resolve(EntityRef.Quest("quest_1234")).Should().Be("quest_1234");
    }

    private sealed class ResolverStub : IReferenceDataService
    {
        public Dictionary<string, Item> ItemsByInternalNameMap { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Recipe> RecipesByInternalNameMap { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Npc> NpcsByInternalNameMap { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>();
        public IReadOnlyDictionary<string, Item> ItemsByInternalName => ItemsByInternalNameMap;
        public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Item>());
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName => RecipesByInternalNameMap;
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, Npc> NpcsByInternalName => NpcsByInternalNameMap;
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, QuestEntry> Quests { get; } = new Dictionary<string, QuestEntry>();
        public IReadOnlyDictionary<string, QuestEntry> QuestsByInternalName { get; } = new Dictionary<string, QuestEntry>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
