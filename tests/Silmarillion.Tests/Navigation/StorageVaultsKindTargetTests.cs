using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Npcs;
using Mithril.Reference.Models.Quests;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Xunit;
using StorageVaultPoco = Mithril.Reference.Models.Misc.StorageVault;

namespace Silmarillion.Tests.Navigation;

public sealed class StorageVaultsKindTargetTests
{
    [Fact]
    public void Kind_IsStorageVault()
    {
        var (target, _, _) = BuildTarget();
        target.Kind.Should().Be(EntityKind.StorageVault);
    }

    [Fact]
    public void TabIndex_IsEight()
    {
        // Items=0 … Lorebooks=7, StorageVaults=8 — ninth tab. Must match TabOrder.
        var (target, _, _) = BuildTarget();
        target.TabIndex.Should().Be(8);
    }

    [Fact]
    public void TrySelectByInternalName_KnownVault_SelectsOnTabVm_ReturnsTrue()
    {
        var (target, vm, _) = BuildTarget(
            ("NPC_CharlesThompson", "Charles Thompson"));

        var ok = target.TrySelectByInternalName("NPC_CharlesThompson");

        ok.Should().BeTrue();
        vm.SelectedVault.Should().NotBeNull();
        vm.SelectedVault!.EnvelopeKey.Should().Be("NPC_CharlesThompson");
        vm.DetailViewModel.Should().NotBeNull();
        vm.DetailViewModel!.DisplayName.Should().Be("Charles Thompson");
    }

    [Fact]
    public void TrySelectByInternalName_StarPrefixedAccountWideKey_Selects()
    {
        // The '*'-prefixed account-wide envelope key must round-trip through the kind
        // target unchanged (it's the literal selection / deep-link contract).
        var (target, vm, _) = BuildTarget(
            ("*AccountStorage_Serbule", "Serbule Transfer Chest"));

        target.TrySelectByInternalName("*AccountStorage_Serbule").Should().BeTrue();

        vm.SelectedVault.Should().NotBeNull();
        vm.SelectedVault!.EnvelopeKey.Should().Be("*AccountStorage_Serbule");
        vm.SelectedVault.IsAccountWide.Should().BeTrue();
    }

    [Fact]
    public void TrySelectByInternalName_UnknownVault_ReturnsFalse_VmUnchanged()
    {
        var (target, vm, _) = BuildTarget();
        vm.SelectedVault.Should().BeNull();

        target.TrySelectByInternalName("NPC_DoesNotExist").Should().BeFalse();
        vm.SelectedVault.Should().BeNull();
    }

    [Fact]
    public void TrySelectByInternalName_ClearsResidualQueryText()
    {
        var (target, vm, _) = BuildTarget(("NPC_A", "A"));
        vm.QueryText = "IsAccountWide = true";

        target.TrySelectByInternalName("NPC_A").Should().BeTrue();

        vm.QueryText.Should().BeEmpty(because: "the kind target clears any prior filter before selecting");
        vm.SelectedVault!.EnvelopeKey.Should().Be("NPC_A");
    }

    [Fact]
    public void TryOpenInWindow_NoDetailSelected_ReturnsFalse()
    {
        var (target, _, _) = BuildTarget();
        target.TryOpenInWindow().Should().BeFalse();
    }

    private static (StorageVaultsKindTarget Target, StorageVaultsTabViewModel Vm, FakeReferenceData RefData)
        BuildTarget(params (string EnvelopeKey, string FriendlyName)[] vaults)
    {
        var refData = new FakeReferenceData();
        foreach (var (key, name) in vaults)
            refData.AddVault(key, new StorageVaultPoco { NpcFriendlyName = name, NumSlots = 10 });
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var settings = new SilmarillionSettings();
        var vm = new StorageVaultsTabViewModel(refData, nav, new ReferenceDataEntityNameResolver(refData), settings);
        var target = new StorageVaultsKindTarget(vm);
        return (target, vm, refData);
    }

    private sealed class FakeReferenceData : IReferenceDataService
    {
        private readonly Dictionary<string, StorageVaultPoco> _vaults = new(StringComparer.Ordinal);
        public void AddVault(string envelopeKey, StorageVaultPoco vault) => _vaults[envelopeKey] = vault;
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
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
