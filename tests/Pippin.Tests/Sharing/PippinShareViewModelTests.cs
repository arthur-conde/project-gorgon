using Mithril.Reference.Models.Items;
using System.Windows.Media.Imaging;
using FluentAssertions;
using Mithril.Shared.Icons;
using Mithril.Shared.Reference;
using Pippin.Domain;
using Pippin.Sharing;
using Xunit;

namespace Pippin.Tests.Sharing;

/// <summary>
/// Pure-data tests for the share-card and full-grid view models. Exercises the data
/// composition path (payload + catalog + cache) without invoking
/// <c>RenderTargetBitmap</c> — the actual bitmap render needs a WPF Dispatcher and
/// is verified manually per the plan.
/// </summary>
public class PippinShareViewModelTests
{
    private static FoodCatalog CreateCatalog(params (long Id, string InternalName, string Name, int IconId)[] foods)
    {
        var dict = new Dictionary<long, Item>();
        foreach (var (id, internalName, name, iconId) in foods)
            dict[id] = new Item { Id = id, Name = name, InternalName = internalName, MaxStackSize = 1, IconId = iconId, Keywords = [], FoodDesc = "Level 0 Snack" };
        return new FoodCatalog(new StubReferenceData(dict));
    }

    private static PippinSharePayload BuildPayload(
        string? characterName = "Argothian",
        Dictionary<string, int>? eaten = null,
        Dictionary<string, int>? unknown = null) => new()
    {
        CharacterName = characterName,
        EatenFoodsByInternalName = new Dictionary<string, int>(eaten ?? new(), StringComparer.Ordinal),
        UnknownByName = unknown,
        LastReportTime = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero),
    };

    // ---- Card VM ------------------------------------------------------------

    [Fact]
    public void Card_named_payload_uses_character_name_as_title()
    {
        var catalog = CreateCatalog((1, "FoodApple", "Apple", 0));
        var vm = new PippinShareCardViewModel(BuildPayload("Argothian"), catalog, gourmandLevel: 33, new NoopIconCache());

        vm.CharacterTitle.Should().Be("Argothian");
        vm.HasIdentity.Should().BeTrue();
        vm.GourmandLevelText.Should().Be("Gourmand Lv 33");
    }

    [Fact]
    public void Card_anonymous_payload_falls_back_to_module_title()
    {
        var catalog = CreateCatalog((1, "FoodApple", "Apple", 0));
        var vm = new PippinShareCardViewModel(BuildPayload(characterName: null), catalog, gourmandLevel: 0, new NoopIconCache());

        vm.CharacterTitle.Should().Be("Pippin · Gourmand");
        vm.HasIdentity.Should().BeFalse();
        vm.GourmandLevelText.Should().Be("Gourmand", "level 0 hides the level number");
    }

    [Fact]
    public void Card_completion_text_includes_count_total_and_percent()
    {
        var catalog = CreateCatalog(
            (1, "FoodApple", "Apple", 0),
            (2, "FoodBacon", "Bacon", 0),
            (3, "FoodCake",  "Cake",  0),
            (4, "FoodDate",  "Date",  0));
        var payload = BuildPayload(eaten: new Dictionary<string, int> { ["FoodApple"] = 5 });

        var vm = new PippinShareCardViewModel(payload, catalog, gourmandLevel: 10, new NoopIconCache());

        vm.EatenCount.Should().Be(1);
        vm.TotalCount.Should().Be(4);
        vm.CompletionText.Should().Contain("1 / 4").And.Contain("25%");
        vm.CompletionRatio.Should().Be(0.25);
        vm.BarFillPixelWidth.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Card_top_eaten_picks_highest_counts_and_resolves_names()
    {
        var catalog = CreateCatalog(
            (1, "FoodApple", "Apple Pie",      0),
            (2, "FoodBacon", "Bacon",          0),
            (3, "FoodCake",  "Steamed Cake",   0));
        var payload = BuildPayload(eaten: new Dictionary<string, int>
        {
            ["FoodApple"] = 5,
            ["FoodBacon"] = 12,
            ["FoodCake"]  = 3,
        });

        var vm = new PippinShareCardViewModel(payload, catalog, gourmandLevel: 0, new NoopIconCache());

        vm.TopEaten.Should().HaveCount(3);
        vm.TopEaten[0].Name.Should().Be("Bacon");
        vm.TopEaten[0].Count.Should().Be(12);
        vm.TopEaten[1].Name.Should().Be("Apple Pie");
        vm.TopEaten[2].Name.Should().Be("Steamed Cake");
    }

    [Fact]
    public void Card_top_eaten_falls_back_to_internal_name_when_catalog_missing_entry()
    {
        var catalog = CreateCatalog((1, "FoodApple", "Apple", 0));
        var payload = BuildPayload(eaten: new Dictionary<string, int>
        {
            ["FoodMystery"] = 7,
        });

        var vm = new PippinShareCardViewModel(payload, catalog, gourmandLevel: 0, new NoopIconCache());

        vm.TopEaten.Should().ContainSingle()
          .Which.Name.Should().Be("FoodMystery", "no catalog match → render the internal name verbatim");
    }

    // ---- Full-grid VM -------------------------------------------------------

    [Fact]
    public void FullGrid_includes_catalog_rows_unknowns_and_sender_only_internal_names()
    {
        var catalog = CreateCatalog(
            (1, "FoodApple", "Apple", 0),
            (2, "FoodBacon", "Bacon", 0));
        var payload = BuildPayload(
            eaten: new Dictionary<string, int>
            {
                ["FoodBacon"]   = 2,
                ["FoodMystery"] = 1, // sender-only InternalName, recipient catalog doesn't know it
            },
            unknown: new Dictionary<string, int> { ["Mystery Stew"] = 4 });

        var vm = new PippinFullGridViewModel(payload, catalog, gourmandLevel: 33, new NoopIconCache());

        vm.Rows.Should().HaveCount(4, "two catalog rows + one orphan + one sender-only InternalName");
        vm.Rows.Select(r => r.Name).Should().Contain(new[] { "Apple", "Bacon", "Mystery Stew", "FoodMystery" });
    }

    [Fact]
    public void FullGrid_marks_eaten_rows_and_count()
    {
        var catalog = CreateCatalog(
            (1, "FoodApple", "Apple", 0),
            (2, "FoodBacon", "Bacon", 0));
        var payload = BuildPayload(eaten: new Dictionary<string, int> { ["FoodBacon"] = 7 });

        var vm = new PippinFullGridViewModel(payload, catalog, gourmandLevel: 0, new NoopIconCache());

        var apple = vm.Rows.Single(r => r.Name == "Apple");
        var bacon = vm.Rows.Single(r => r.Name == "Bacon");
        apple.IsEaten.Should().BeFalse();
        apple.EatenCount.Should().Be(0);
        bacon.IsEaten.Should().BeTrue();
        bacon.EatenCount.Should().Be(7);
    }

    [Fact]
    public void FullGrid_anonymous_payload_omits_identity_subtitle()
    {
        var catalog = CreateCatalog((1, "FoodApple", "Apple", 0));
        var vm = new PippinFullGridViewModel(BuildPayload(characterName: null), catalog, 0, new NoopIconCache());

        vm.HasIdentity.Should().BeFalse();
        vm.CharacterTitle.Should().Be("Pippin · Gourmand");
    }

    // ---- Stubs --------------------------------------------------------------

    /// <summary>Returns null icons; the VM tolerates them and the off-screen renderer would draw blanks.</summary>
    private sealed class NoopIconCache : IIconCacheService
    {
        public BitmapImage GetOrLoadIcon(int iconId) => new();
        public event EventHandler<int>? IconReady;
        public int CachedCount => 0;
        public long CacheSizeBytes => 0;
        public Task ClearCacheAsync() => Task.CompletedTask;
        public Task DownloadAllAsync(IProgress<(int completed, int total)> progress, CancellationToken ct = default) => Task.CompletedTask;
        public bool IsCached(int iconId) => false;
        public IReadOnlyList<int> GetUncachedIcons(IEnumerable<int> iconIds) => iconIds.Where(i => i > 0).Distinct().ToList();
        public Task PreloadAsync(IEnumerable<int> iconIds, IProgress<(int completed, int total)>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
        private void Suppress() => IconReady?.Invoke(this, 0);
    }

    private sealed class StubReferenceData : IReferenceDataService
    {
        public StubReferenceData(Dictionary<long, Item> items) { Items = items; }

        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, Item> Items { get; }
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>();
        public ItemKeywordIndex KeywordIndex => ItemKeywordIndex.Empty;
        public IReadOnlyDictionary<string, RecipeEntry> Recipes { get; } = new Dictionary<string, RecipeEntry>();
        public IReadOnlyDictionary<string, RecipeEntry> RecipesByInternalName { get; } = new Dictionary<string, RecipeEntry>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, QuestEntry> Quests { get; } = new Dictionary<string, QuestEntry>();
        public IReadOnlyDictionary<string, QuestEntry> QuestsByInternalName { get; } = new Dictionary<string, QuestEntry>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public event EventHandler<string>? FileUpdated;
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        private void Suppress() => FileUpdated?.Invoke(this, "");
    }
}
