using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using System.IO;
using FluentAssertions;
using Mithril.GameState.Sessions;
using Mithril.Shared.Reference;
using Smaug.Domain;
using Xunit;

namespace Smaug.Tests;

[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class PriceCalibrationReplayTests
{
    private static void SafeDeleteDir(string dir)
    {
        if (!Directory.Exists(dir)) return;
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    private static PriceCalibrationService BuildService(string dataDir, FakeSession? session = null) =>
        new(refData: new FakeRefData(), dataDir, session: session);

    private static DateTimeOffset Ts(int hour, int min) =>
        new(2026, 5, 11, hour, min, 0, TimeSpan.Zero);

    [Fact]
    public void Replay_OfSameSellInSameSession_DedupsToOneObservation()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("smaug_test");
        try
        {
            var session = new FakeSession("char|2026-05-11T12:25:04Z");
            var svc = BuildService(dir, session);

            // Live ingestion of one sell, then replay of the same (Mithril
            // relaunched mid-PG-session and the seed buffer re-emits).
            var ts = Ts(13, 0);
            svc.RecordObservation("NPC_Therese", "BottleOfWater", 100, "Neutral", 0, ts);
            svc.RecordObservation("NPC_Therese", "BottleOfWater", 100, "Neutral", 0, ts);

            svc.Data.Observations.Should().HaveCount(1,
                "second call has the same session + log-line timestamp → key collision");
            svc.Data.Observations[0].SessionId.Should().Be(session.Current!.SessionId);
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void NewSession_RecordsSeparateObservation()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("smaug_test");
        try
        {
            var session = new FakeSession("char|2026-05-11T12:25:04Z");
            var svc = BuildService(dir, session);

            svc.RecordObservation("NPC_Therese", "BottleOfWater", 100, "Neutral", 0, Ts(13, 0));
            session.Set("char|2026-05-11T14:00:00Z");
            svc.RecordObservation("NPC_Therese", "BottleOfWater", 100, "Neutral", 0, Ts(15, 0));

            svc.Data.Observations.Should().HaveCount(2);
            svc.Data.Observations[0].SessionId.Should().NotBe(svc.Data.Observations[1].SessionId);
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void Replay_AfterPersistAndReload_StillDedups()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("smaug_test");
        try
        {
            var session = new FakeSession("char|2026-05-11T12:25:04Z");
            var ts = Ts(13, 0);

            var first = BuildService(dir, session);
            first.RecordObservation("NPC_Therese", "BottleOfWater", 100, "Neutral", 0, ts);
            first.Data.Observations.Should().HaveCount(1);

            // Mithril relaunch: fresh service loads the observation from disk,
            // then the seed re-fires.
            var second = BuildService(dir, session);
            second.Data.Observations.Should().HaveCount(1);
            second.RecordObservation("NPC_Therese", "BottleOfWater", 100, "Neutral", 0, ts);
            second.Data.Observations.Should().HaveCount(1, "replay short-circuited on key collision");
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void V1Legacy_MigratesToV2_WithEmptySessionId()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("smaug_test");
        try
        {
            // Pre-v2 single-file shape (also exercises the split migration).
            File.WriteAllText(Path.Combine(dir, "calibration.json"), """
                {
                  "version": 1,
                  "observations": [
                    {
                      "npcKey": "NPC_Therese",
                      "internalName": "BottleOfWater",
                      "baseValue": 11,
                      "pricePaid": 100,
                      "favorTier": "Neutral",
                      "civicPrideLevel": 0,
                      "timestamp": "2026-04-20T00:00:00+00:00"
                    }
                  ]
                }
                """);

            var svc = new PriceCalibrationService(new FakeRefData(), dir);

            svc.Data.Observations.Should().HaveCount(1);
            svc.Data.Version.Should().Be(PriceCalibrationService.CurrentSchemaVersion);
            svc.Data.Observations[0].SessionId.Should().Be("");
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    private sealed class FakeSession : IGameSessionService
    {
        public GameSession? Current { get; private set; }
        public event EventHandler<GameSession>? SessionStarted;
        public FakeSession(string sessionId) { Set(sessionId); }
        public void Set(string sessionId)
        {
            Current = new GameSession(sessionId, "char",
                new DateTime(2026, 5, 11, 12, 25, 4, DateTimeKind.Utc), TimeSpan.Zero);
            SessionStarted?.Invoke(this, Current);
        }
        public IDisposable Subscribe(Action<GameSession> handler)
        {
            if (Current is not null) handler(Current);
            return new Sub();
        }
        private sealed class Sub : IDisposable { public void Dispose() { } }
    }

    private sealed class FakeRefData : IReferenceDataService
    {
        public IReadOnlyList<string> Keys { get; } = ["items"];
        public IReadOnlyDictionary<long, Item> Items { get; }
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; }
        public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Item>());
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> Quests { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> QuestsByInternalName { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        public FakeRefData()
        {
            var water = new Item
            {
                Id = 1, Name = "BottleOfWater", InternalName = "Bottle of Water",
                MaxStackSize = 10, IconId = 0,
                Keywords = [new ItemKeyword("Drink", 0)],
                Value = 11m,
            };
            Items = new Dictionary<long, Item> { [1] = water };
            ItemsByInternalName = new Dictionary<string, Item>(StringComparer.Ordinal) { ["BottleOfWater"] = water };
        }

        public ReferenceFileSnapshot GetSnapshot(string key) => new("items", ReferenceFileSource.Bundled, "test", null, 1);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
