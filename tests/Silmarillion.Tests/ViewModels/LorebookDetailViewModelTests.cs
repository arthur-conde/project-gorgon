using System.Windows.Input;
using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Npcs;
using Mithril.Reference.Models.Quests;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;
using Silmarillion.ViewModels;
using Xunit;
using LorebookPoco = Mithril.Reference.Models.Misc.Lorebook;

namespace Silmarillion.Tests.ViewModels;

public sealed class LorebookDetailViewModelTests
{
    [Fact]
    public void Header_ResolvesTitle_CategorySubtitle_AndDivergentFooter()
    {
        var refData = new FakeReferenceData();
        refData.AddLorebook("Book_101", new LorebookPoco
        {
            Category = "Stories", InternalName = "TheWastedWishes",
            Title = "The Wasted Wishes", Text = "<h1>x</h1>body",
        });
        var vm = BuildDetail(refData, "TheWastedWishes", categoryDisplay: "Stories");

        vm.DisplayName.Should().Be("The Wasted Wishes");
        vm.InternalName.Should().Be("TheWastedWishes");
        vm.EnvelopeKey.Should().Be("Book_101");
        // Footer shows BOTH identifiers because they diverge.
        vm.FooterText.Should().Be("Book_101 / TheWastedWishes");
        vm.CategorySubtitle.Should().Be("from Stories");
    }

    [Fact]
    public void FooterSegments_DivergentKeyAndName_AreTwoIndependentSegments()
    {
        var refData = new FakeReferenceData();
        refData.AddLorebook("Book_101", new LorebookPoco
        {
            Category = "Stories", InternalName = "TheWastedWishes",
            Title = "The Wasted Wishes", Text = "x",
        });
        var vm = BuildDetail(refData, "TheWastedWishes");

        // Each is its own atomic copyable identifier (no " / " mashup).
        vm.FooterSegments.Should().Equal("Book_101", "TheWastedWishes");
        // Back-compat: joined FooterText preserved for non-UI consumers.
        vm.FooterText.Should().Be("Book_101 / TheWastedWishes");
    }

    [Fact]
    public void FooterSegments_KeyEqualsName_IsSingleSegment()
    {
        var refData = new FakeReferenceData();
        refData.AddLorebook("SameName", new LorebookPoco
        {
            Category = "Stories", InternalName = "SameName",
            Title = "Same", Text = "x",
        });
        var vm = BuildDetail(refData, "SameName");

        vm.FooterSegments.Should().ContainSingle().Which.Should().Be("SameName");
        vm.FooterText.Should().Be("SameName");
    }

    [Fact]
    public void AreaChip_ResolvesFromFirstMatchingKeyword()
    {
        var refData = new FakeReferenceData();
        refData.AddArea(new AreaEntry("AreaSerbule", "Serbule", "Serbule"));
        refData.AddLorebook("Book_1", new LorebookPoco
        {
            Category = "Stories", InternalName = "B", Title = "B",
            Keywords = new[] { "AreaSerbule" }, Text = "x",
        });
        var vm = BuildDetail(refData, "B", areaKey: "AreaSerbule");

        vm.HasArea.Should().BeTrue();
        vm.AreaChip!.DisplayName.Should().Be("Serbule");
        vm.AreaChip.Reference.Kind.Should().Be(EntityKind.Area);
        vm.AreaChip.Reference.InternalName.Should().Be("AreaSerbule");
    }

    [Fact]
    public void NullText_RendersExternalGuidePlaceholder()
    {
        var refData = new FakeReferenceData();
        refData.AddLorebook("Book_155", new LorebookPoco
        {
            Category = "GuideProgram", InternalName = "VolunteerGuide", Title = "Volunteer Guide",
            Text = null,
        });
        var vm = BuildDetail(refData, "VolunteerGuide");

        vm.HasBody.Should().BeFalse();
        vm.BodyText.Should().BeNull();
    }

    [Fact]
    public void Body_PassedThroughForFormattedTextRendering()
    {
        var refData = new FakeReferenceData();
        refData.AddLorebook("Book_1", new LorebookPoco
        {
            Category = "Stories", InternalName = "B", Title = "B",
            Text = "<h1>The Title</h1>Body prose.",
        });
        var vm = BuildDetail(refData, "B");

        vm.HasBody.Should().BeTrue();
        // The VM passes raw markup; FormattedText (covered by its own tests) renders it.
        vm.BodyText.Should().Be("<h1>The Title</h1>Body prose.");
    }

    // ── #318 Gate-C: bestowing-items popup-from-index ──

    [Fact]
    public void BestowingItems_PopupMembershipEqualsIndex_DistinctCount_NoHistoryPush()
    {
        var refData = new FakeReferenceData();
        refData.AddLorebook("Book_101", new LorebookPoco
        {
            Category = "Stories", InternalName = "TheWastedWishes", Title = "The Wasted Wishes", Text = "x",
        });
        var indexed = new[]
        {
            new Item { InternalName = "ItemA", Name = "Item A", IconId = 1 },
            new Item { InternalName = "ItemB", Name = "Item B", IconId = 2 },
        };
        refData.SetBestowing("TheWastedWishes", indexed);

        var captured = (Popup: (ProvenancePopupViewModel?)null, Click: (ICommand?)null);
        var prior = LorebookDetailViewModel.ProvenancePopupOpener;
        LorebookDetailViewModel.ProvenancePopupOpener = (vm, click) => captured = (vm, click);
        try
        {
            var nav = new RecordingNavigator();
            var detail = BuildDetail(refData, "TheWastedWishes", navigator: nav);

            detail.HasBestowingItems.Should().BeTrue();
            // Count == distinct index members.
            detail.BestowingItemsTotal.Should().Be(2);
            detail.BestowingItemsPopup!.TotalCount.Should().Be(2);

            // Single-reason → the popup collapses to a flat list (no provenance sections).
            detail.BestowingItemsPopup.IsFlat.Should().BeTrue();
            detail.BestowingItemsPopup.Sections.Should().ContainSingle();

            // Membership == the index collection: same Item objects, same order.
            detail.BestowingItemsPopup.FlatChips.Select(c => c.Reference.InternalName)
                .Should().Equal("ItemA", "ItemB");
            detail.BestowingItemsPopup.FlatChips.Select(c => c.Reference.InternalName)
                .Should().Equal(indexed.Select(i => i.InternalName));

            // ToQueryCommand is NOT wired for this surface (no query derivation by design).
            detail.BestowingItemsPopup.ToQueryCommand.Should().BeNull();
            detail.BestowingItemsPopup.HasToQuery.Should().BeFalse();

            // Opening the popup pushes no navigator history (non-navigating contract #229).
            detail.ShowBestowingItemsPopupCommand.Execute(null);
            captured.Popup.Should().BeSameAs(detail.BestowingItemsPopup);
            nav.OpenCalls.Should().Be(0, "opening a provenance popup is not a navigation");
        }
        finally
        {
            LorebookDetailViewModel.ProvenancePopupOpener = prior;
        }
    }

    [Fact]
    public void BestowingItems_EmptyIndex_ProducesNoAffordance()
    {
        var refData = new FakeReferenceData();
        refData.AddLorebook("Book_1", new LorebookPoco
        {
            Category = "Stories", InternalName = "Lonely", Title = "Lonely", Text = "x",
        });
        // No SetBestowing → ItemsBestowingLorebook has no entry for "Lonely".
        var detail = BuildDetail(refData, "Lonely");

        detail.HasBestowingItems.Should().BeFalse();
        detail.BestowingItemsPopup.Should().BeNull();
        detail.BestowingItemsTotal.Should().Be(0);
        detail.ShowBestowingItemsPopupCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void BestowingItems_CountIsDistinctIndexMembers_NotReDerived()
    {
        // The index is already dedup'd by item; the popup renders exactly those members.
        var refData = new FakeReferenceData();
        refData.AddLorebook("Book_1", new LorebookPoco
        {
            Category = "Stories", InternalName = "Book", Title = "Book", Text = "x",
        });
        refData.SetBestowing("Book",
            new Item { InternalName = "Only", Name = "Only Item" });

        var detail = BuildDetail(refData, "Book");

        detail.BestowingItemsTotal.Should().Be(1);
        detail.BestowingItemsPopup!.TotalCount.Should().Be(1);
        detail.BestowingItemsPopup.FlatChips.Should().ContainSingle()
            .Which.Reference.InternalName.Should().Be("Only");
    }

    // ── Real-data sanity walk (cookbook *Verification ladder* — load-bearing per #298) ──

    [Fact]
    public void RealBundledLorebooks_DiverseMarkupEntries_ProjectSensibly()
    {
        var bundled = System.IO.Path.Combine(AppContext.BaseDirectory, "Reference", "BundledData");
        if (!System.IO.File.Exists(System.IO.Path.Combine(bundled, "lorebooks.json"))) return;

        var refData = BuildRealRefData(bundled);
        if (refData is null) return;

        var tabVm = new LorebooksTabViewModel(
            refData,
            new Silmarillion.Navigation.SilmarillionReferenceNavigator(
                new[] { (IReferenceKindTarget)new StubKindTarget(EntityKind.Area) }),
            new ReferenceDataEntityNameResolver(refData),
            new SilmarillionSettings());

        // Book_101 "The Wasted Wishes" — a short story with <h1> + an area keyword.
        var wastedWishes = tabVm.AllLorebooks.FirstOrDefault(b => b.InternalName == "TheWastedWishes");
        wastedWishes.Should().NotBeNull("Book_101 is a stable entry in bundled lorebooks.json");
        tabVm.SelectedLorebook = wastedWishes;
        var d1 = tabVm.DetailViewModel!;
        d1.DisplayName.Should().NotBeNullOrEmpty();
        d1.DisplayName.Should().NotContain("(unknown)");
        d1.EnvelopeKey.Should().Be("Book_101");
        d1.FooterText.Should().Contain("/", "envelope key and InternalName diverge for lorebooks");
        d1.HasBody.Should().BeTrue();
        d1.BodyText.Should().Contain("<h1>", "raw markup is passed to FormattedText (which renders it)");
        d1.HasArea.Should().BeTrue("Book_101 carries the AreaSerbule keyword");
        d1.AreaChip!.DisplayName.Should().NotStartWith("Area",
            "the area key should resolve to a friendly name (Serbule), not the raw key");

        // A null-Text book (GuideProgram) → external-guide placeholder, not a blank pane.
        var nullText = tabVm.AllLorebooks
            .FirstOrDefault(b => string.IsNullOrEmpty(b.Book.Text));
        if (nullText is not null)
        {
            tabVm.SelectedLorebook = nullText;
            tabVm.DetailViewModel!.HasBody.Should().BeFalse();
            tabVm.DetailViewModel!.BodyText.Should().BeNull();
        }

        // Every projected row has a non-empty title and a resolved category display.
        tabVm.AllLorebooks.Should().OnlyContain(r => !string.IsNullOrEmpty(r.Title));
        tabVm.AllLorebooks.Should().OnlyContain(r => !string.IsNullOrEmpty(r.CategoryDisplayTitle));
        // The category groups resolve via the lorebookinfo.json sidecar (e.g. "The Gods").
        tabVm.CategoryGroups.Select(g => g.CategoryDisplayTitle).Should().Contain("The Gods");
    }

    private static IReferenceDataService? BuildRealRefData(string bundled)
    {
        try
        {
            var cacheDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
            System.IO.Directory.CreateDirectory(cacheDir);
            using var http = new System.Net.Http.HttpClient(new ThrowingHttpHandler());
            return new ReferenceDataService(cacheDir, http, bundledDir: bundled);
        }
        catch
        {
            return null;
        }
    }

    private sealed class ThrowingHttpHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP must not be called in this test");
    }

    private sealed class StubKindTarget : IReferenceKindTarget
    {
        public StubKindTarget(EntityKind kind) => Kind = kind;
        public EntityKind Kind { get; }
        public int TabIndex => 0;
        public bool TrySelectByInternalName(string internalName) => true;
        public bool TryOpenInWindow() => false;
    }

    private static LorebookDetailViewModel BuildDetail(
        FakeReferenceData refData,
        string internalName,
        string categoryDisplay = "Stories",
        string? areaKey = null,
        IReferenceNavigator? navigator = null)
    {
        var book = refData.LorebooksByInternalName[internalName];
        var row = new LorebookListRow(
            Book: book,
            InternalName: internalName,
            Title: book.Title ?? internalName,
            CategoryDisplayTitle: categoryDisplay,
            CategoryKey: book.Category ?? "",
            AreaKey: areaKey,
            HasText: !string.IsNullOrEmpty(book.Text),
            LocationHint: book.LocationHint);
        var nav = navigator ?? new RecordingNavigator();
        return new LorebookDetailViewModel(
            row, refData, nav, new ReferenceDataEntityNameResolver(refData),
            new SilmarillionSettings(), openEntityCommand: null);
    }

    private sealed class RecordingNavigator : IReferenceNavigator
    {
        public int OpenCalls { get; private set; }
        public bool CanOpen(EntityRef reference) => false;
        public void Open(EntityRef reference) => OpenCalls++;
        public void Back() { }
        public void Forward() { }
        public EntityRef? Current => null;
        public bool CanGoBack => false;
        public bool CanGoForward => false;
        public event EventHandler<NavigatedEventArgs>? Navigated { add { } remove { } }
    }

    private sealed class FakeReferenceData : IReferenceDataService
    {
        private readonly Dictionary<string, LorebookPoco> _lorebooks = new(StringComparer.Ordinal);
        private readonly Dictionary<string, LorebookPoco> _byInternalName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<Item>> _bestowing = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AreaEntry> _areas = new(StringComparer.Ordinal);

        public void AddLorebook(string envelopeKey, LorebookPoco book)
        {
            _lorebooks[envelopeKey] = book;
            if (book.InternalName is not null) _byInternalName[book.InternalName] = book;
        }

        public void SetBestowing(string bookInternalName, params Item[] items) =>
            _bestowing[bookInternalName] = items;

        public void AddArea(AreaEntry area) => _areas[area.Key] = area;

        public IReadOnlyDictionary<string, LorebookPoco> Lorebooks => _lorebooks;
        public IReadOnlyDictionary<string, LorebookPoco> LorebooksByInternalName => _byInternalName;
        public IReadOnlyDictionary<string, IReadOnlyList<Item>> ItemsBestowingLorebook => _bestowing;
        public IReadOnlyDictionary<string, AreaEntry> Areas => _areas;

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
        public IReadOnlyDictionary<string, Quest> QuestsByInternalName { get; } = new Dictionary<string, Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>();

        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
