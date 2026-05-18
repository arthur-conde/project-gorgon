using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Media.Imaging;
using FluentAssertions;
using Mithril.Shared.Icons;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Icons;

[Trait("Category", "FileIO")]
public sealed class IconCachePreloadTests : IDisposable
{
    private readonly string _cacheDir;

    public IconCachePreloadTests()
    {
        _cacheDir = Mithril.TestSupport.TestPaths.CreateTempDir("mithril-icon-cache");
    }

    public void Dispose()
    {
        try { Directory.Delete(_cacheDir, recursive: true); } catch { }
    }

    private IconCacheService CreateService(HttpMessageHandler? handler = null)
    {
        var http = new HttpClient(handler ?? new RecordingHandler(_ => MakeOkPng()));
        var refData = new EmptyReferenceData();
        var settings = new IconSettings();
        return new IconCacheService(_cacheDir, http, refData, diag: null, settings);
    }

    [Fact]
    public void IsCached_returns_false_for_nonpositive_ids()
    {
        var svc = CreateService();
        svc.IsCached(0).Should().BeFalse();
        svc.IsCached(-1).Should().BeFalse();
    }

    [Fact]
    public void IsCached_returns_false_for_unknown_id()
        => CreateService().IsCached(42).Should().BeFalse();

    [Fact]
    public void IsCached_returns_true_when_file_already_on_disk()
    {
        File.WriteAllBytes(Path.Combine(_cacheDir, "icon_99.png"), MakePngBytes());
        CreateService().IsCached(99).Should().BeTrue();
    }

    [Fact]
    public void GetUncachedIcons_filters_dedupes_and_drops_nonpositive()
    {
        File.WriteAllBytes(Path.Combine(_cacheDir, "icon_10.png"), MakePngBytes());
        var svc = CreateService();

        var result = svc.GetUncachedIcons(new[] { 10, 20, 20, 30, 0, -5, 10 });

        result.Should().Equal(20, 30);
    }

    [Fact]
    public async Task PreloadAsync_with_empty_input_reports_zero_total()
    {
        var svc = CreateService();
        var seen = new List<(int completed, int total)>();
        await svc.PreloadAsync(Array.Empty<int>(), new TestProgress(seen));

        seen.Should().ContainSingle().Which.Should().Be((0, 0));
    }

    [Fact]
    public async Task PreloadAsync_skips_already_cached_ids()
    {
        File.WriteAllBytes(Path.Combine(_cacheDir, "icon_10.png"), MakePngBytes());
        var requests = new List<int>();
        var handler = new RecordingHandler(req =>
        {
            // Record the requested icon id from the URL path
            var id = int.Parse(System.IO.Path.GetFileNameWithoutExtension(req.RequestUri!.AbsolutePath).Replace("icon_", ""));
            requests.Add(id);
            return MakeOkPng();
        });

        var svc = CreateService(handler);
        await svc.PreloadAsync(new[] { 10 }); // already cached

        requests.Should().BeEmpty("ids already on disk must not trigger a network round-trip");
    }

    [Fact]
    public async Task PreloadAsync_downloads_missing_ids_and_reports_progress()
    {
        var handler = new RecordingHandler(_ => MakeOkPng());
        var svc = CreateService(handler);

        var seen = new List<(int completed, int total)>();
        await svc.PreloadAsync(new[] { 11, 12, 13 }, new TestProgress(seen));

        File.Exists(Path.Combine(_cacheDir, "icon_11.png")).Should().BeTrue();
        File.Exists(Path.Combine(_cacheDir, "icon_12.png")).Should().BeTrue();
        File.Exists(Path.Combine(_cacheDir, "icon_13.png")).Should().BeTrue();

        // First report is (0, 3), final is (3, 3); intermediate counts can arrive in any order
        seen.First().Should().Be((0, 3));
        seen.Last().Should().Be((3, 3));
    }

    [Fact]
    public void GetOrLoadIconDeferred_does_not_block_the_caller_on_a_disk_decode()
    {
        // Icon present on disk. The synchronous GetOrLoadIcon would decode it
        // inline; the deferred variant must hand back the placeholder instead
        // and resolve off-thread.
        File.WriteAllBytes(Path.Combine(_cacheDir, "icon_77.png"), MakePngBytes());
        var svc = CreateService();

        var placeholder = svc.GetOrLoadIconDeferred(0); // id <= 0 ⇒ shared placeholder
        var first = svc.GetOrLoadIconDeferred(77);

        first.Should().BeSameAs(placeholder,
            "the deferred path must not synchronously decode an on-disk icon — it returns the placeholder and resolves off-thread");
    }

    [Fact]
    public async Task GetOrLoadIconDeferred_resolves_an_on_disk_icon_into_the_memory_cache_off_thread()
    {
        File.WriteAllBytes(Path.Combine(_cacheDir, "icon_88.png"), MakePngBytes());
        var svc = CreateService();

        var placeholder = svc.GetOrLoadIconDeferred(0);
        svc.GetOrLoadIconDeferred(88); // queues the background decode

        BitmapImage? resolved = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var cur = svc.GetOrLoadIconDeferred(88);
            if (!ReferenceEquals(cur, placeholder)) { resolved = cur; break; }
            await Task.Delay(25);
        }

        resolved.Should().NotBeNull("the background task should have decoded the on-disk PNG into the memory cache");
        resolved!.IsFrozen.Should().BeTrue("an image crossing back to the UI thread must be frozen");
    }

    private static HttpResponseMessage MakeOkPng() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(MakePngBytes()),
    };

    /// <summary>1×1 transparent PNG header — enough to satisfy WriteAllBytes; never decoded by the cache during PreloadAsync.</summary>
    private static byte[] MakePngBytes() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41,
        0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
        0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82,
    ];

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_respond(request));
    }

    private sealed class TestProgress : IProgress<(int completed, int total)>
    {
        private readonly List<(int, int)> _list;
        public TestProgress(List<(int, int)> list) => _list = list;
        public void Report((int completed, int total) value)
        {
            lock (_list) _list.Add(value);
        }
    }

    private sealed class EmptyReferenceData : IReferenceDataService
    {
        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>();
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>();
        public ItemKeywordIndex KeywordIndex => ItemKeywordIndex.Empty;
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> Quests { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> QuestsByInternalName { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public event EventHandler<string>? FileUpdated;
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        private void Suppress() => FileUpdated?.Invoke(this, "");
    }
}
