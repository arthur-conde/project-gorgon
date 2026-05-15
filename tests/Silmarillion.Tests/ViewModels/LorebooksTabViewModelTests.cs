using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Npcs;
using Mithril.Reference.Models.Quests;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Silmarillion.ViewModels;
using Xunit;
using LorebookPoco = Mithril.Reference.Models.Misc.Lorebook;
using LorebookCategoryInfo = Mithril.Reference.Models.Misc.LorebookCategoryInfo;

namespace Silmarillion.Tests.ViewModels;

public sealed class LorebooksTabViewModelTests
{
    [Fact]
    public void BuildsRows_ResolvesCategoryDisplayTitleFromSidecar()
    {
        var refData = new FakeReferenceData();
        refData.AddCategory("Gods", new LorebookCategoryInfo { Title = "The Gods" });
        refData.AddLorebook("Book_103", new LorebookPoco
        {
            Category = "Gods",
            InternalName = "TheChaliceSagaVol1",
            Title = "The Chalice Saga Vol 1",
            Keywords = new[] { "AreaSerbule" },
            Text = "<h1>x</h1>body",
        });

        var vm = MakeVm(refData);

        var row = vm.AllLorebooks.Should().ContainSingle().Subject;
        row.InternalName.Should().Be("TheChaliceSagaVol1");
        row.Title.Should().Be("The Chalice Saga Vol 1");
        row.CategoryDisplayTitle.Should().Be("The Gods");
        row.CategoryKey.Should().Be("Gods");
        row.AreaKey.Should().Be("AreaSerbule");
        row.HasText.Should().BeTrue();
    }

    [Fact]
    public void BuildsRows_FallsBackToRawCategoryKey_WhenSidecarMissing()
    {
        var refData = new FakeReferenceData();
        refData.AddLorebook("Book_1", new LorebookPoco
        {
            Category = "NotInSidecar",
            InternalName = "Mystery",
            Title = "Mystery",
        });

        var vm = MakeVm(refData);

        vm.AllLorebooks.Single().CategoryDisplayTitle.Should().Be("NotInSidecar");
        vm.AllLorebooks.Single().HasText.Should().BeFalse();
    }

    [Fact]
    public void GroupProjection_GroupsByCategoryDisplayTitle()
    {
        var refData = new FakeReferenceData();
        refData.AddCategory("Gods", new LorebookCategoryInfo { Title = "The Gods" });
        refData.AddCategory("Stories", new LorebookCategoryInfo { Title = "Stories" });
        refData.AddLorebook("Book_1", new LorebookPoco { Category = "Gods", InternalName = "A", Title = "Aaa" });
        refData.AddLorebook("Book_2", new LorebookPoco { Category = "Gods", InternalName = "B", Title = "Bbb" });
        refData.AddLorebook("Book_3", new LorebookPoco { Category = "Stories", InternalName = "C", Title = "Ccc" });

        var vm = MakeVm(refData);

        vm.CategoryGroups.Should().HaveCount(2);
        var gods = vm.CategoryGroups.Single(g => g.CategoryDisplayTitle == "The Gods");
        gods.Rows.Should().HaveCount(2);
        gods.Heading.Should().Be("The Gods (2)");
        vm.CategoryGroups.Single(g => g.CategoryDisplayTitle == "Stories").Rows.Should().ContainSingle();
    }

    [Fact]
    public void FileUpdated_LorebookInfo_RebuildsCategoryDisplayTitle_WithoutDroppingSelection()
    {
        var refData = new FakeReferenceData();
        refData.AddLorebook("Book_1", new LorebookPoco { Category = "Gods", InternalName = "A", Title = "Aaa" });

        var vm = MakeVm(refData);
        vm.SelectedLorebook = vm.AllLorebooks.Single();
        vm.AllLorebooks.Single().CategoryDisplayTitle.Should().Be("Gods", "no sidecar yet");

        // Sidecar arrives via a CDN refresh of lorebookinfo.json.
        refData.AddCategory("Gods", new LorebookCategoryInfo { Title = "The Gods" });
        refData.RaiseFileUpdated("lorebookinfo");

        vm.AllLorebooks.Single().CategoryDisplayTitle.Should().Be("The Gods");
        vm.SelectedLorebook.Should().NotBeNull();
        vm.SelectedLorebook!.InternalName.Should().Be("A");
        vm.DetailViewModel.Should().NotBeNull();
    }

    [Fact]
    public void FileUpdated_Items_RebuildsBestowingItemsOnDetail_WithoutDroppingSelection()
    {
        var refData = new FakeReferenceData();
        refData.AddLorebook("Book_101", new LorebookPoco
        {
            Category = "Stories", InternalName = "TheWastedWishes", Title = "The Wasted Wishes", Text = "x",
        });

        var vm = MakeVm(refData);
        vm.SelectedLorebook = vm.AllLorebooks.Single();
        vm.DetailViewModel!.HasBestowingItems.Should().BeFalse("no item bestows it yet");

        // An item that bestows the book arrives via an items.json refresh.
        refData.SetBestowing("TheWastedWishes", new Item { InternalName = "BookItem", Name = "Book Item" });
        refData.RaiseFileUpdated("items");

        vm.SelectedLorebook.Should().NotBeNull();
        vm.DetailViewModel!.HasBestowingItems.Should().BeTrue();
        vm.DetailViewModel!.BestowingItemsTotal.Should().Be(1);
    }

    [Fact]
    public void QueryFiltering_ByCategoryKey_IsExpressibleOnTheReflectedSchema()
    {
        // The query box parses against LorebookListRow's reflected schema; CategoryKey must
        // be a public property so 'CategoryKey = "Gods"' is a known column.
        LorebooksTabViewModel.SchemaSnapshot.Select(c => c.Name)
            .Should().Contain(new[] { "Title", "CategoryKey", "CategoryDisplayTitle", "AreaKey", "HasText" });
    }

    private static LorebooksTabViewModel MakeVm(FakeReferenceData refData)
    {
        var nav = new Silmarillion.Navigation.SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        return new LorebooksTabViewModel(
            refData, nav, new ReferenceDataEntityNameResolver(refData), new SilmarillionSettings());
    }

    private sealed class FakeReferenceData : IReferenceDataService
    {
        private readonly Dictionary<string, LorebookPoco> _lorebooks = new(StringComparer.Ordinal);
        private readonly Dictionary<string, LorebookPoco> _byInternalName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, LorebookCategoryInfo> _categories = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<Item>> _bestowing = new(StringComparer.Ordinal);

        public void AddLorebook(string envelopeKey, LorebookPoco book)
        {
            _lorebooks[envelopeKey] = book;
            if (book.InternalName is not null) _byInternalName[book.InternalName] = book;
        }

        public void AddCategory(string key, LorebookCategoryInfo info) => _categories[key] = info;

        public void SetBestowing(string bookInternalName, params Item[] items) =>
            _bestowing[bookInternalName] = items;

        public void RaiseFileUpdated(string fileKey) => FileUpdated?.Invoke(this, fileKey);

        public IReadOnlyDictionary<string, LorebookPoco> Lorebooks => _lorebooks;
        public IReadOnlyDictionary<string, LorebookPoco> LorebooksByInternalName => _byInternalName;
        public IReadOnlyDictionary<string, LorebookCategoryInfo> LorebookCategories => _categories;
        public IReadOnlyDictionary<string, IReadOnlyList<Item>> ItemsBestowingLorebook => _bestowing;

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
