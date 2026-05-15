using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Npcs;
using Mithril.Reference.Models.Quests;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Xunit;
using PlayerTitlePoco = Mithril.Reference.Models.Misc.PlayerTitle;

namespace Silmarillion.Tests.Navigation;

public sealed class PlayerTitlesKindTargetTests
{
    [Fact]
    public void Kind_IsPlayerTitle()
    {
        var (target, _, _) = BuildTarget();
        target.Kind.Should().Be(EntityKind.PlayerTitle);
    }

    [Fact]
    public void TabIndex_IsEight()
    {
        // Items=0 … Lorebooks=7, PlayerTitles=8 — ninth tab. Must match TabOrder.
        var (target, _, _) = BuildTarget();
        target.TabIndex.Should().Be(8);
    }

    [Fact]
    public void TrySelectByInternalName_KnownEnvelopeKey_SelectsOnTabVm_ReturnsTrue()
    {
        // The selection contract IS the "Title_N" envelope key — the POCO carries
        // no separate InternalName (unlike Lorebook/Recipe).
        var (target, vm, _) = BuildTarget(("Title_5018", "<color=#00cc00>Warsmith</color>"));

        var ok = target.TrySelectByInternalName("Title_5018");

        ok.Should().BeTrue();
        vm.SelectedTitle.Should().NotBeNull();
        vm.SelectedTitle!.EnvelopeKey.Should().Be("Title_5018");
        vm.DetailViewModel.Should().NotBeNull();
        vm.DetailViewModel!.DisplayName.Should().Be("Warsmith");
    }

    [Fact]
    public void TrySelectByInternalName_DisplayTitle_DoesNotMatch_ReturnsFalse()
    {
        var (target, vm, _) = BuildTarget(("Title_5018", "<color=#00cc00>Warsmith</color>"));

        target.TrySelectByInternalName("Warsmith").Should().BeFalse();
        vm.SelectedTitle.Should().BeNull();
    }

    [Fact]
    public void TrySelectByInternalName_UnknownKey_ReturnsFalse_VmUnchanged()
    {
        var (target, vm, _) = BuildTarget();
        vm.SelectedTitle.Should().BeNull();

        target.TrySelectByInternalName("Title_999999").Should().BeFalse();
        vm.SelectedTitle.Should().BeNull();
    }

    [Fact]
    public void TrySelectByInternalName_ClearsResidualQueryText()
    {
        var (target, vm, _) = BuildTarget(("Title_11", "<color=cyan>Volunteer Guide</color>"));
        vm.QueryText = "IsObtainable = true";

        target.TrySelectByInternalName("Title_11").Should().BeTrue();

        vm.QueryText.Should().BeEmpty(because: "the kind target clears any prior filter before selecting");
        vm.SelectedTitle!.EnvelopeKey.Should().Be("Title_11");
    }

    [Fact]
    public void TryOpenInWindow_NoDetailSelected_ReturnsFalse()
    {
        var (target, _, _) = BuildTarget();
        target.TryOpenInWindow().Should().BeFalse();
    }

    private static (PlayerTitlesKindTarget Target, PlayerTitlesTabViewModel Vm, FakeReferenceData RefData)
        BuildTarget(params (string EnvelopeKey, string Title)[] titles)
    {
        var refData = new FakeReferenceData();
        foreach (var (key, title) in titles)
            refData.AddTitle(key, new PlayerTitlePoco { Title = title });
        var vm = new PlayerTitlesTabViewModel(refData);
        var target = new PlayerTitlesKindTarget(vm);
        return (target, vm, refData);
    }

    private sealed class FakeReferenceData : IReferenceDataService
    {
        private readonly Dictionary<string, PlayerTitlePoco> _titles = new(StringComparer.Ordinal);

        public void AddTitle(string envelopeKey, PlayerTitlePoco title) => _titles[envelopeKey] = title;

        public IReadOnlyDictionary<string, PlayerTitlePoco> PlayerTitles => _titles;

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
