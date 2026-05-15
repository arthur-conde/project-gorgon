using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Npcs;
using Mithril.Reference.Models.Quests;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Silmarillion.ViewModels;
using Xunit;
using PlayerTitlePoco = Mithril.Reference.Models.Misc.PlayerTitle;

namespace Silmarillion.Tests.ViewModels;

public sealed class PlayerTitlesTabViewModelTests
{
    [Fact]
    public void BuildsRows_StripsColorMarkup_AndDerivesFacets()
    {
        var refData = new FakeReferenceData();
        refData.AddTitle("Title_5018", new PlayerTitlePoco
        {
            Title = "<color=#00cc00>Warsmith</color>",
            Tooltip = "Earned by completing a quest.",
            AccountWide = true,
        });

        var vm = MakeVm(refData);

        var row = vm.AllTitles.Should().ContainSingle().Subject;
        row.EnvelopeKey.Should().Be("Title_5018");
        row.DisplayTitle.Should().Be("Warsmith", "the cosmetic <color> span is stripped (#248 Option A)");
        row.HasTooltip.Should().BeTrue();
        row.IsObtainable.Should().BeTrue("no Lint_* keyword");
        row.AccountWide.Should().BeTrue();
        row.SoulWide.Should().BeFalse();
    }

    [Fact]
    public void IsObtainable_IsFalse_ForLintNotObtainableFamily()
    {
        var refData = new FakeReferenceData();
        refData.AddTitle("Title_101", new PlayerTitlePoco
        {
            Title = "<color=white>Content Creator</color>",
            Keywords = new[] { "Lint_NotObtainable" },
        });
        refData.AddTitle("Title_1", new PlayerTitlePoco { Title = "<color=cyan>Game Admin</color>" });

        var vm = MakeVm(refData);

        vm.AllTitles.Single(t => t.EnvelopeKey == "Title_101").IsObtainable.Should().BeFalse();
        vm.AllTitles.Single(t => t.EnvelopeKey == "Title_1").IsObtainable.Should()
            .BeTrue("no keywords at all → obtainable; the long tail is a facet, not hidden");
    }

    [Fact]
    public void EmptyTitle_FallsBackToEnvelopeKeyForDisplay()
    {
        var refData = new FakeReferenceData();
        refData.AddTitle("Title_42", new PlayerTitlePoco { Title = null });

        var vm = MakeVm(refData);

        vm.AllTitles.Single().DisplayTitle.Should().Be("Title_42");
    }

    [Fact]
    public void Rows_AreSortedByCleanDisplayTitle()
    {
        var refData = new FakeReferenceData();
        refData.AddTitle("Title_3", new PlayerTitlePoco { Title = "<color=red>Zelot</color>" });
        refData.AddTitle("Title_1", new PlayerTitlePoco { Title = "<color=red>Apprentice</color>" });

        var vm = MakeVm(refData);

        vm.AllTitles.Select(t => t.DisplayTitle).Should().ContainInOrder("Apprentice", "Zelot");
    }

    [Fact]
    public void Selection_BuildsDetailViewModel_WithCleanHeader()
    {
        var refData = new FakeReferenceData();
        refData.AddTitle("Title_11", new PlayerTitlePoco { Title = "<color=cyan>Volunteer Guide</color>" });

        var vm = MakeVm(refData);
        vm.SelectedTitle = vm.AllTitles.Single();

        vm.DetailViewModel.Should().NotBeNull();
        vm.DetailViewModel!.DisplayName.Should().Be("Volunteer Guide");
        vm.DetailViewModel.FooterText.Should().Be("Title_11");
    }

    [Fact]
    public void FileUpdated_PlayerTitles_RebuildsList_WithoutDroppingSelection()
    {
        var refData = new FakeReferenceData();
        refData.AddTitle("Title_1", new PlayerTitlePoco { Title = "<color=cyan>Admin</color>" });

        var vm = MakeVm(refData);
        vm.SelectedTitle = vm.AllTitles.Single();

        // A playertitles.json refresh adds Tooltip text to the selected title.
        refData.AddTitle("Title_1", new PlayerTitlePoco
        {
            Title = "<color=cyan>Admin</color>", Tooltip = "Now documented.",
        });
        refData.RaiseFileUpdated("playertitles");

        vm.SelectedTitle.Should().NotBeNull();
        vm.SelectedTitle!.EnvelopeKey.Should().Be("Title_1");
        vm.DetailViewModel!.HasTooltip.Should().BeTrue();
        vm.DetailViewModel.Tooltip.Should().Be("Now documented.");
    }

    [Fact]
    public void FileUpdated_OtherFile_IsIgnored()
    {
        var refData = new FakeReferenceData();
        refData.AddTitle("Title_1", new PlayerTitlePoco { Title = "<color=cyan>Admin</color>" });
        var vm = MakeVm(refData);
        var before = vm.AllTitles;

        refData.RaiseFileUpdated("items");

        vm.AllTitles.Should().BeSameAs(before, "only 'playertitles' triggers a rebuild");
    }

    [Fact]
    public void QueryFiltering_Facets_AreExpressibleOnTheReflectedSchema()
    {
        PlayerTitlesTabViewModel.SchemaSnapshot.Select(c => c.Name)
            .Should().Contain(new[] { "DisplayTitle", "IsObtainable", "HasTooltip", "AccountWide", "SoulWide" });
    }

    private static PlayerTitlesTabViewModel MakeVm(FakeReferenceData refData) =>
        new PlayerTitlesTabViewModel(refData);

    private sealed class FakeReferenceData : IReferenceDataService
    {
        private readonly Dictionary<string, PlayerTitlePoco> _titles = new(StringComparer.Ordinal);

        public void AddTitle(string envelopeKey, PlayerTitlePoco title) => _titles[envelopeKey] = title;
        public void RaiseFileUpdated(string fileKey) => FileUpdated?.Invoke(this, fileKey);

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
        public event EventHandler<string>? FileUpdated;
    }
}
