using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Misc;
using Mithril.Reference.Models.Npcs;
using Mithril.Reference.Models.Quests;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Xunit;
using StorageVaultPoco = Mithril.Reference.Models.Misc.StorageVault;

namespace Silmarillion.Tests.ViewModels;

public sealed class StorageVaultDetailViewModelTests
{
    [Fact]
    public void Header_Footer_AreaChip_Resolve()
    {
        var refData = new FakeReferenceData();
        refData.AddArea(new AreaEntry("AreaSerbule", "Serbule", "Serbule"));
        var vm = Build(refData, "NPC_CharlesThompson", new StorageVaultPoco
        {
            Area = "AreaSerbule", NpcFriendlyName = "Charles Thompson",
            HasAssociatedNpc = true, NumSlots = 24,
        });

        vm.DisplayName.Should().Be("Charles Thompson");
        vm.EnvelopeKey.Should().Be("NPC_CharlesThompson");
        vm.HasArea.Should().BeTrue();
        vm.AreaChip!.DisplayName.Should().Be("Serbule");
        vm.AreaChip.Reference.Kind.Should().Be(EntityKind.Area);
    }

    [Fact]
    public void OperatorNpcChip_OnlyWhenHasAssociatedNpc()
    {
        var refData = new FakeReferenceData();

        var withNpc = Build(refData, "NPC_CharlesThompson", new StorageVaultPoco
        {
            NpcFriendlyName = "Charles Thompson", HasAssociatedNpc = true, NumSlots = 1,
        });
        withNpc.HasOperatorNpc.Should().BeTrue();
        withNpc.OperatorNpcChip!.Reference.Kind.Should().Be(EntityKind.Npc);
        withNpc.OperatorNpcChip.Reference.InternalName.Should().Be("NPC_CharlesThompson");

        // Transfer chest — HasAssociatedNpc:false ⇒ no operator chip (dead reference otherwise).
        var transfer = Build(refData, "*AccountStorage_Serbule", new StorageVaultPoco
        {
            NpcFriendlyName = "Serbule Transfer Chest", HasAssociatedNpc = false, NumSlots = 0,
        });
        transfer.HasOperatorNpc.Should().BeFalse();
        transfer.OperatorNpcChip.Should().BeNull();
        transfer.IsAccountWide.Should().BeTrue();
    }

    [Fact]
    public void FavorTable_OrderedCanonically_NotDictOrder_ZeroSlotsFiltered()
    {
        var refData = new FakeReferenceData();
        // Deliberately out-of-order dict with a 0-slot universal-default tier.
        var vm = Build(refData, "NPC_X", new StorageVaultPoco
        {
            NpcFriendlyName = "X",
            Levels = new Dictionary<string, int>
            {
                ["SoulMates"] = 64,
                ["Despised"] = 0,        // universal default — filtered
                ["Friends"] = 32,
                ["CloseFriends"] = 40,
            },
        });

        vm.HasCapacityTable.Should().BeTrue();
        vm.CapacityRows.Select(r => r.Tier).Should()
            .ContainInOrder("Friends", "Close Friends", "Soul Mates");
        vm.CapacityRows.Should().NotContain(r => r.Tier.Contains("Despised"),
            "a 0-slot favor tier is the universal default and is filtered as noise");
        vm.CapacityRows.Single(r => r.Tier == "Soul Mates").Slots.Should().Be(64);
    }

    [Fact]
    public void FavorTable_HatedTier_SortsByFavorFloor_NotCollapsedToLast()
    {
        // #374: capacity rows now rank via the canonical Mithril.Reference.FavorTier
        // (#373), replacing a hand-rolled FavorOrder array that misspelled the -600 rung
        // "Hatred". A "Hated" Levels key must rank near the bottom of the ladder
        // (FavorTier.Hated = -3), not collapse to last as the old misspelling did.
        var refData = new FakeReferenceData();
        var vm = Build(refData, "NPC_H", new StorageVaultPoco
        {
            NpcFriendlyName = "H",
            Levels = new Dictionary<string, int>
            {
                ["SoulMates"] = 64,
                ["Hated"] = 8,
                ["Comfortable"] = 16,
            },
        });

        vm.CapacityRows.Select(r => r.Tier).Should()
            .ContainInOrder("Hated", "Comfortable", "Soul Mates");
    }

    [Fact]
    public void FlatSlots_WhenNoLevels_AndTransferChestDoesNotCrashOnZero()
    {
        var refData = new FakeReferenceData();

        var flat = Build(refData, "IvynsChest", new StorageVaultPoco
        {
            NpcFriendlyName = "Ivyn's Chest", NumSlots = 32,
        });
        flat.HasCapacityTable.Should().BeFalse();
        flat.HasFlatSlots.Should().BeTrue();
        flat.FlatSlots.Should().Be(32);

        var transfer = Build(refData, "*AccountStorage_Serbule", new StorageVaultPoco
        {
            NpcFriendlyName = "Serbule Transfer Chest", NumSlots = 0, HasAssociatedNpc = false,
        });
        transfer.HasCapacityTable.Should().BeFalse();
        transfer.HasFlatSlots.Should().BeFalse("NumSlots:0 carries no information");
    }

    [Fact]
    public void ScriptAtomicRange_RendersMinMax_WhenPresent()
    {
        var refData = new FakeReferenceData();
        var vm = Build(refData, "SerbuleCommunityChest", new StorageVaultPoco
        {
            NpcFriendlyName = "Serbule Dynamic Safebox",
            NumSlotsScriptAtomic = "SerbuleCommunityChestSize",
            NumSlotsScriptAtomicMinValue = 1,
            NumSlotsScriptAtomicMaxValue = 150,
        });

        vm.HasScriptAtomicRange.Should().BeTrue();
        vm.ScriptAtomicRange.Should().Be("Dynamic: 1–150 slots");
    }

    [Fact]
    public void EventLevels_RenderedOnlyWhenPresent_AndLabelled()
    {
        var refData = new FakeReferenceData();
        var none = Build(refData, "NPC_A", new StorageVaultPoco { NpcFriendlyName = "A", NumSlots = 1 });
        none.HasEventLevels.Should().BeFalse();

        var ev = Build(refData, "RiShinStorageChest", new StorageVaultPoco
        {
            NpcFriendlyName = "Ri-Shin Shrine Storage Chest",
            EventLevels = new Dictionary<string, int>
            {
                ["RiShinShrine_Storage1On"] = 25,
                ["RiShinShrine_Storage2On"] = 50,
            },
        });
        ev.HasEventLevels.Should().BeTrue();
        ev.EventLevelRows.Should().HaveCount(2);
        ev.EventLevelRows.Should().Contain(r => r.Tier == "RiShinShrine_Storage2On" && r.Slots == 50);
    }

    [Fact]
    public void Requirements_QuestCompleted_BecomesNavigableQuestChip()
    {
        var refData = new FakeReferenceData();
        refData.AddQuest("GoblinsArmpitRoaches", "Goblins' Armpit Roaches");
        var vm = Build(refData, "NPC_CynthiaRolfe", new StorageVaultPoco
        {
            NpcFriendlyName = "Cynthia Rolfe",
            Requirements = new StorageRequirement[]
            {
                new StorageQuestCompletedRequirement { T = "QuestCompleted", Quest = "GoblinsArmpitRoaches" },
            },
        });

        vm.HasQuestRequirements.Should().BeTrue();
        var chip = vm.QuestRequirementChips.Should().ContainSingle().Subject;
        chip.Reference.Kind.Should().Be(EntityKind.Quest);
        chip.Reference.InternalName.Should().Be("GoblinsArmpitRoaches");
        chip.DisplayName.Should().Be("Goblins' Armpit Roaches");
    }

    [Fact]
    public void Requirements_FlagsAndIdentityGates_RenderAsHumanLabels()
    {
        var refData = new FakeReferenceData();
        var vm = Build(refData, "Chest", new StorageVaultPoco
        {
            NpcFriendlyName = "Chest",
            Requirements = new StorageRequirement[]
            {
                new StorageInteractionFlagSetRequirement { T = "InteractionFlagSet", InteractionFlag = "Ivyn_Gave_Passcode" },
                new StorageServerRulesFlagSetRequirement { T = "ServerRulesFlagSet", Flag = "Some_Rule" },
                new StorageIsLongtimeAnimalRequirement { T = "IsLongtimeAnimal" },
                new StorageIsWardenRequirement { T = "IsWarden" },
            },
        });

        vm.HasRequirementLines.Should().BeTrue();
        vm.RequirementLines.Should().Contain(l => l.Contains("Ivyn Gave Passcode"));
        vm.RequirementLines.Should().Contain(l => l.Contains("Some Rule"));
        vm.RequirementLines.Should().Contain(l => l.Contains("long-time animal"));
        vm.RequirementLines.Should().Contain(l => l.Contains("Warden"));
    }

    [Fact]
    public void Requirements_Unknown_DegradesGracefully_NotCrashOrBlank()
    {
        var refData = new FakeReferenceData();
        var vm = Build(refData, "Chest", new StorageVaultPoco
        {
            NpcFriendlyName = "Chest",
            Requirements = new StorageRequirement[]
            {
                new UnknownStorageRequirement { T = "SomeFuturePgType", DiscriminatorValue = "SomeFuturePgType" },
            },
        });

        vm.HasRequirementLines.Should().BeTrue();
        vm.RequirementLines.Should().ContainSingle()
            .Which.Should().Be("(unrecognised requirement: SomeFuturePgType)");
    }

    [Fact]
    public void ItemKeywordTags_RenderedAsPlainTags_NotEntities()
    {
        var refData = new FakeReferenceData();
        var vm = Build(refData, "NPC_CharlesThompson", new StorageVaultPoco
        {
            NpcFriendlyName = "Charles Thompson",
            RequiredItemKeywords = new[] { "Alchemy", "Potion" },
            RequirementDescription = "Potions and Alchemy Ingredients",
        });

        vm.HasItemKeywordTags.Should().BeTrue();
        vm.ItemKeywordTags.Should().BeEquivalentTo(new[] { "Alchemy", "Potion" });
        vm.RequirementDescription.Should().Be("Potions and Alchemy Ingredients");
        vm.HasAccessRequirements.Should().BeTrue();
    }

    [Fact]
    public void Chips_Click_NavigatesViaOpenEntityCommand_ThreadedThroughTheTab()
    {
        // #340 regression: StorageVaultDetailView.xaml binds every chip's ClickCommand
        // to DataContext.OpenEntityCommand. If the tab does not thread its command into
        // the detail VM (every other tab does), the binding resolves to null and the
        // chip is a dead no-op despite rendering as navigable. Exercise the real tab →
        // detail wiring (BuildDetailViewModel), not the direct ctor.
        var refData = new FakeReferenceData();
        refData.AddArea(new AreaEntry("AreaSerbule", "Serbule", "Serbule"));
        refData.AddVault("NPC_CharlesThompson", new StorageVaultPoco
        {
            Area = "AreaSerbule", NpcFriendlyName = "Charles Thompson",
            HasAssociatedNpc = true, NumSlots = 24,
        });

        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var resolver = new ReferenceDataEntityNameResolver(refData);
        var settings = new SilmarillionSettings();
        var tab = new StorageVaultsTabViewModel(refData, nav, resolver, settings);
        tab.SelectedVault = tab.AllVaults.Single(r => r.EnvelopeKey == "NPC_CharlesThompson");

        var detail = tab.DetailViewModel!;
        detail.OpenEntityCommand.Should().NotBeNull(
            "the chip ClickCommand binds to DetailViewModel.OpenEntityCommand — a null " +
            "command makes the Area/Operator/Quest chips dead no-ops (#340)");

        detail.OpenEntityCommand!.Execute(detail.AreaChip!.Reference);

        nav.Current.Should().NotBeNull("clicking the Area chip should drive navigation");
        nav.Current!.Kind.Should().Be(EntityKind.Area);
        nav.Current.InternalName.Should().Be("AreaSerbule");
    }

    private static StorageVaultDetailViewModel Build(
        FakeReferenceData refData, string envelopeKey, StorageVaultPoco vault)
    {
        refData.AddVault(envelopeKey, vault);
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var resolver = new ReferenceDataEntityNameResolver(refData);
        var settings = new SilmarillionSettings();
        var tab = new StorageVaultsTabViewModel(refData, nav, resolver, settings);
        var row = tab.AllVaults.Single(r => r.EnvelopeKey == envelopeKey);
        return new StorageVaultDetailViewModel(row, refData, nav, resolver);
    }

    private sealed class FakeReferenceData : IReferenceDataService
    {
        private readonly Dictionary<string, StorageVaultPoco> _vaults = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AreaEntry> _areas = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Quest> _quests = new(StringComparer.Ordinal);

        public void AddVault(string envelopeKey, StorageVaultPoco vault) => _vaults[envelopeKey] = vault;
        public void AddArea(AreaEntry area) => _areas[area.Key] = area;
        public void AddQuest(string internalName, string name) =>
            _quests[internalName] = new Quest { InternalName = internalName, Name = name };

        public IReadOnlyDictionary<string, StorageVaultPoco> StorageVaults => _vaults;
        public IReadOnlyDictionary<string, AreaEntry> Areas => _areas;
        public IReadOnlyDictionary<string, Quest> QuestsByInternalName => _quests;

        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>();
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>();
        public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Item>());
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, Npc> NpcsByInternalName { get; } = new Dictionary<string, Npc>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Quest> Quests { get; } = new Dictionary<string, Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>();

        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
