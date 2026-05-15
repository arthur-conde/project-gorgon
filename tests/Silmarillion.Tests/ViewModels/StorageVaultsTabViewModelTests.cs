using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Npcs;
using Mithril.Reference.Models.Quests;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Silmarillion.ViewModels;
using Xunit;
using StorageVaultPoco = Mithril.Reference.Models.Misc.StorageVault;

namespace Silmarillion.Tests.ViewModels;

public sealed class StorageVaultsTabViewModelTests
{
    [Fact]
    public void BuildsRows_ProjectsDisplayName_AreaKey_Grouping_SlotSummary()
    {
        var refData = new FakeReferenceData();
        refData.AddVault("NPC_CharlesThompson", new StorageVaultPoco
        {
            Area = "AreaSerbule",
            Grouping = "AreaSerbule",
            NpcFriendlyName = "Charles Thompson",
            Levels = new Dictionary<string, int> { ["Friends"] = 32, ["SoulMates"] = 64 },
        });

        var vm = MakeVm(refData);

        var row = vm.AllVaults.Should().ContainSingle().Subject;
        row.EnvelopeKey.Should().Be("NPC_CharlesThompson");
        row.DisplayName.Should().Be("Charles Thompson");
        row.AreaKey.Should().Be("AreaSerbule");
        row.Grouping.Should().Be("AreaSerbule");
        row.IsAccountWide.Should().BeFalse();
        row.SlotSummary.Should().Be("up to 64 slots");
    }

    [Fact]
    public void AccountWideDerivation_FromStarPrefix()
    {
        var refData = new FakeReferenceData();
        refData.AddVault("*AccountStorage_Serbule", new StorageVaultPoco
        {
            Area = "AreaSerbule",
            HasAssociatedNpc = false,
            NpcFriendlyName = "Serbule Transfer Chest",
            NumSlots = 0,
        });

        var row = MakeVm(refData).AllVaults.Single();

        row.IsAccountWide.Should().BeTrue();
        row.EnvelopeKey.Should().Be("*AccountStorage_Serbule");
        row.SlotSummary.Should().Be("transfer", "NumSlots:0 with no Levels is a transfer chest");
    }

    [Fact]
    public void GroupingFacet_IsExposedOnReflectedSchema()
    {
        // The query box parses against StorageVaultListRow's reflected schema; the facets
        // must be public properties so e.g. 'IsAccountWide = true' is a known column.
        StorageVaultsTabViewModel.SchemaSnapshot.Select(c => c.Name)
            .Should().Contain(new[] { "DisplayName", "AreaKey", "Grouping", "IsAccountWide", "SlotSummary" });
    }

    [Fact]
    public void FileUpdated_StorageVaults_RebuildsList_PreservingSelection_IncludingStarKey()
    {
        var refData = new FakeReferenceData();
        refData.AddVault("*AccountStorage_Serbule", new StorageVaultPoco
        {
            NpcFriendlyName = "Serbule Transfer Chest", NumSlots = 0,
        });

        var vm = MakeVm(refData);
        vm.SelectedVault = vm.AllVaults.Single();
        vm.DetailViewModel.Should().NotBeNull();

        // A CDN refresh swaps the source dictionary; the same '*'-prefixed key must round-trip.
        refData.AddVault("*AccountStorage_Serbule", new StorageVaultPoco
        {
            NpcFriendlyName = "Serbule Transfer Chest", NumSlots = 0, Area = "AreaSerbule",
        });
        refData.RaiseFileUpdated("storagevaults");

        vm.SelectedVault.Should().NotBeNull();
        vm.SelectedVault!.EnvelopeKey.Should().Be("*AccountStorage_Serbule");
        vm.SelectedVault.AreaKey.Should().Be("AreaSerbule", "the row was rebuilt from the fresh snapshot");
        vm.DetailViewModel.Should().NotBeNull();
    }

    [Fact]
    public void FileUpdated_UnrelatedFile_IsIgnored()
    {
        var refData = new FakeReferenceData();
        refData.AddVault("NPC_A", new StorageVaultPoco { NpcFriendlyName = "A", NumSlots = 1 });
        var vm = MakeVm(refData);
        var before = vm.AllVaults;

        refData.RaiseFileUpdated("items");

        vm.AllVaults.Should().BeSameAs(before);
    }

    private static StorageVaultsTabViewModel MakeVm(FakeReferenceData refData)
    {
        var nav = new Silmarillion.Navigation.SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        return new StorageVaultsTabViewModel(
            refData, nav, new ReferenceDataEntityNameResolver(refData), new SilmarillionSettings());
    }

    private sealed class FakeReferenceData : IReferenceDataService
    {
        private Dictionary<string, StorageVaultPoco> _vaults = new(StringComparer.Ordinal);

        public void AddVault(string envelopeKey, StorageVaultPoco vault) => _vaults[envelopeKey] = vault;
        public void RaiseFileUpdated(string fileKey) => FileUpdated?.Invoke(this, fileKey);

        public IReadOnlyDictionary<string, StorageVaultPoco> StorageVaults => _vaults;

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
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Quest> Quests { get; } = new Dictionary<string, Quest>();
        public IReadOnlyDictionary<string, Quest> QuestsByInternalName { get; } = new Dictionary<string, Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>();

        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated;
    }
}
