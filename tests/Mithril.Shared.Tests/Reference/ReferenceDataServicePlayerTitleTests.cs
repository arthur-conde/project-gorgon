using System.IO;
using System.Net.Http;
using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Reference;

/// <summary>
/// Synthetic-fixture round-trip for the #248 player-title service plumbing:
/// <see cref="IReferenceDataService.PlayerTitles"/> keyed by the <c>"Title_N"</c>
/// envelope key (the only identifier the POCO carries), the snapshot, the Keys
/// entry, and the refresh trigger (cache-reconstruction proxy for RefreshAsync).
/// There is no cross-link index — the Quest→title linkage is unstructured (#248).
/// </summary>
[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class ReferenceDataServicePlayerTitleTests : IDisposable
{
    private readonly string _root;
    private readonly string _cacheDir;
    private readonly string _bundledDir;

    public ReferenceDataServicePlayerTitleTests()
    {
        _root = Mithril.TestSupport.TestPaths.CreateTempDir("mithril-ref-playertitle-tests");
        _cacheDir = Path.Combine(_root, "cache");
        _bundledDir = Path.Combine(_root, "bundled");
        Directory.CreateDirectory(_cacheDir);
        Directory.CreateDirectory(_bundledDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static HttpClient NeverCallHttp() =>
        new(new ThrowingHandler("HTTP must not be called in this test"));

    private const string PlayerTitles = """
        {
          "Title_1":     { "Title": "<color=cyan>Game Admin</color>" },
          "Title_101":   { "Keywords": [ "Lint_NotObtainable" ], "Title": "<color=white>Content Creator</color>" },
          "Title_5018":  { "Title": "<color=#00cc00>Warsmith</color>", "Tooltip": "Earned by completing a quest." },
          "Title_15001": { "Keywords": [ "PlayerBestowedTitle" ], "Title": "<color=yellow>Insane</color>", "Tooltip": "Bestowed by a Grand Duke.", "AccountWide": true }
        }
        """;

    private void WriteBundled(string playerTitlesJson) =>
        File.WriteAllText(Path.Combine(_bundledDir, "playertitles.json"), playerTitlesJson);

    [Fact]
    public void PlayerTitles_KeyedOnEnvelopeKey_RoundTripsPocoFields()
    {
        WriteBundled(PlayerTitles);
        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.PlayerTitles.Should().HaveCount(4);
        svc.PlayerTitles.Should().ContainKey("Title_5018");
        var warsmith = svc.PlayerTitles["Title_5018"];
        warsmith.Title.Should().Be("<color=#00cc00>Warsmith</color>",
            "the service round-trips the raw POCO; <color> stripping is a UI projection concern");
        warsmith.Tooltip.Should().Be("Earned by completing a quest.");

        var insane = svc.PlayerTitles["Title_15001"];
        insane.AccountWide.Should().BeTrue();
        insane.Keywords.Should().ContainSingle().Which.Should().Be("PlayerBestowedTitle");
    }

    [Fact]
    public void PlayerTitles_IsInKeysList_AndHasSnapshot()
    {
        WriteBundled(PlayerTitles);
        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.Keys.Should().Contain("playertitles");
        var snap = svc.GetSnapshot("playertitles");
        snap.Key.Should().Be("playertitles");
        snap.EntryCount.Should().Be(4);
    }

    [Fact]
    public void RefreshPlayerTitles_RebuildsFromCache()
    {
        // Cache-reconstruction proxy for the RefreshAsync("playertitles") path: a
        // fresh service over a rewritten cache file exercises the same
        // ParseAndSwapPlayerTitles a live refresh would, without an HTTP round-trip.
        WriteBundled("""{ "Title_1": { "Title": "<color=cyan>Admin</color>" } }""");
        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);
        svc.PlayerTitles.Should().HaveCount(1);

        File.WriteAllText(Path.Combine(_cacheDir, "playertitles.json"), PlayerTitles);
        File.WriteAllText(Path.Combine(_cacheDir, "playertitles.meta.json"), "{\"cdnVersion\":\"v2\",\"source\":1}");
        var refreshed = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        refreshed.PlayerTitles.Should().HaveCount(4);
        refreshed.PlayerTitles.Should().ContainKey("Title_15001");
    }

    [Fact]
    public void DefaultInterfaceFallback_IsEmpty_NotNull()
    {
        // The non-rippling default: a test fake that doesn't override PlayerTitles
        // sees an empty (not null) dictionary.
        IReferenceDataService bare = new BareFake();
        bare.PlayerTitles.Should().NotBeNull().And.BeEmpty();
    }

    private sealed class BareFake : IReferenceDataService
    {
        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, Mithril.Reference.Models.Items.Item> Items { get; } = new Dictionary<long, Mithril.Reference.Models.Items.Item>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Items.Item> ItemsByInternalName { get; } = new Dictionary<string, Mithril.Reference.Models.Items.Item>();
        public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Mithril.Reference.Models.Items.Item>());
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Recipes.Recipe> Recipes { get; } = new Dictionary<string, Mithril.Reference.Models.Recipes.Recipe>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Recipes.Recipe> RecipesByInternalName { get; } = new Dictionary<string, Mithril.Reference.Models.Recipes.Recipe>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Npcs.Npc> NpcsByInternalName { get; } = new Dictionary<string, Mithril.Reference.Models.Npcs.Npc>();
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> Quests { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> QuestsByInternalName { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>();
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }

    private sealed class ThrowingHandler(string message) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message);
    }
}
