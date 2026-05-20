using System.IO;
using System.Threading.Channels;
using FluentAssertions;
using Mithril.GameState.Quests;
using Mithril.GameState.Quests.Parsing;
using Mithril.GameState.Tests.TestSupport;
using Mithril.Shared.Character;
using Mithril.Shared.Logging;
using Mithril.Shared.Reference;
using Mithril.TestSupport;
using Xunit;

namespace Mithril.GameState.Tests.Quests;

[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class QuestServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _charactersDir;

    public QuestServiceTests()
    {
        _dir = TestPaths.CreateTempDir("quests_service");
        _charactersDir = Path.Combine(_dir, "characters");
        Directory.CreateDirectory(_charactersDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private (QuestService svc, ScriptedStream stream, FakeActiveCharacterService active,
             FakeReferenceData refData, PerCharacterView<QuestServiceState> view)
        Build(IReadOnlyList<(string Key, Mithril.Reference.Models.Quests.Quest Quest)>? quests = null,
              string character = "Arthur", string server = "Kwatoxi")
    {
        var refData = new FakeReferenceData(quests ?? []);
        var active = new FakeActiveCharacterService();
        active.SetActiveCharacter(character, server);

        var store = new PerCharacterStore<QuestServiceState>(_charactersDir, "quests.json",
            QuestServiceStateJsonContext.Default.QuestServiceState);
        var view = new PerCharacterView<QuestServiceState>(active, store);

        var stream = new ScriptedStream(Array.Empty<string>());
        var svc = new QuestService(
            stream.Driver,
            new QuestJournalLoadParser(),
            new QuestAcceptedParser(refData),
            new QuestCompletedParser(refData),
            view,
            refData);
        return (svc, stream, active, refData, view);
    }

    [Fact]
    public async Task SubscribeAfterMutation_ReplaysActiveAsAcceptedAndHistoryAsCompleted()
    {
        var quests = new[]
        {
            QuestFactory.Repeatable("quest_1", "Q1", "Quest 1", TimeSpan.FromHours(1)),
            QuestFactory.Repeatable("quest_2", "Q2", "Quest 2", TimeSpan.FromHours(1)),
        };
        var (svc, stream, _, _, _) = Build(quests);
        try
        {
            stream.Push("[10:00:00] LocalPlayer: ProcessBook(\"New Quest: <<<quest_1_Name>>>\", \"\", \"\", \"\", \"\", False, False, False, False, False, \"\")");
            stream.Push("[10:01:00] LocalPlayer: ProcessBook(\"New Quest: <<<quest_2_Name>>>\", \"\", \"\", \"\", \"\", False, False, False, False, False, \"\")");
            stream.Push("[10:02:00] LocalPlayer: ProcessCompleteQuest(123, 1)");
            await RunUntilDrainedAsync(svc, stream);

            var replayed = new List<QuestEvent>();
            using var sub = svc.Subscribe(replayed.Add);

            // Q2 is still active → replays as Accepted; Q1 is in completion
            // history → replays as Completed.
            replayed.Should().HaveCount(2);
            replayed.Should().Contain(e => e.Kind == QuestEventKind.Accepted && e.InternalName == "Q2");
            replayed.Should().Contain(e => e.Kind == QuestEventKind.Completed && e.InternalName == "Q1");
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task BulkJournalLoad_ResolvesIdsAndPopulatesActiveSet()
    {
        var quests = new[]
        {
            QuestFactory.Repeatable("quest_50208", "Quest_WO_50208", "Work Order 50208", TimeSpan.FromHours(20)),
            QuestFactory.Repeatable("quest_3", "Quest_Reg_3", "Regular 3", TimeSpan.FromHours(1)),
        };
        var (svc, stream, _, _, _) = Build(quests);
        try
        {
            stream.Push("[10:00:00] LocalPlayer: ProcessLoadQuests(8285856, TransitionalQuestState[], [50208,], [3,])");
            await RunUntilDrainedAsync(svc, stream);

            svc.ActiveQuests.Should().ContainKeys("Quest_WO_50208", "Quest_Reg_3");
            svc.ActiveQuests.Should().HaveCount(2);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task BulkJournalLoad_DropsUnknownIdsSilently()
    {
        var quests = new[]
        {
            QuestFactory.Repeatable("quest_50208", "Quest_WO_50208", "WO 50208", TimeSpan.FromHours(1)),
        };
        var (svc, stream, _, _, _) = Build(quests);
        try
        {
            stream.Push("[10:00:00] LocalPlayer: ProcessLoadQuests(123, TransitionalQuestState[], [50208, 999999,], [])");
            await RunUntilDrainedAsync(svc, stream);

            svc.ActiveQuests.Should().ContainSingle().Which.Key.Should().Be("Quest_WO_50208");
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task BulkJournalLoad_FiresDiffEventsAgainstPriorActiveSet()
    {
        var quests = new[]
        {
            QuestFactory.Repeatable("quest_1", "Q1", "Q1", TimeSpan.FromHours(1)),
            QuestFactory.Repeatable("quest_2", "Q2", "Q2", TimeSpan.FromHours(1)),
            QuestFactory.Repeatable("quest_3", "Q3", "Q3", TimeSpan.FromHours(1)),
        };
        var (svc, stream, _, _, _) = Build(quests);
        var runTask = svc.StartAsync(CancellationToken.None);
        try
        {
            // Pre-seed: Q1 + Q2 are active.
            stream.Push("[10:00:00] LocalPlayer: ProcessLoadQuests(1, TransitionalQuestState[], [], [1,2,])");
            await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));

            var live = new List<QuestEvent>();
            using var sub = svc.Subscribe(live.Add);
            live.Clear(); // discard the Subscribe replay so we only see the next bulk diff

            // New bulk load: Q1 stays, Q2 leaves, Q3 arrives → 1 Abandoned + 1 Accepted.
            stream.Push("[11:00:00] LocalPlayer: ProcessLoadQuests(1, TransitionalQuestState[], [], [1,3,])");
            await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));

            live.Should().Contain(e => e.Kind == QuestEventKind.Abandoned && e.InternalName == "Q2");
            live.Should().Contain(e => e.Kind == QuestEventKind.Accepted && e.InternalName == "Q3");
            live.Should().NotContain(e => e.Kind == QuestEventKind.Accepted && e.InternalName == "Q1");
            svc.ActiveQuests.Should().ContainKeys("Q1", "Q3").And.NotContainKey("Q2");
        }
        finally { await StopAsync(svc); _ = runTask; }
    }

    [Fact]
    public async Task Accepted_ProcessBookAddsToActiveSetAndFiresAcceptedOnce()
    {
        var quests = new[]
        {
            QuestFactory.Repeatable("quest_25212", "Quest_Sample_25212", "Sample", TimeSpan.FromHours(1)),
        };
        var (svc, stream, _, _, _) = Build(quests);
        var runTask = svc.StartAsync(CancellationToken.None);
        try
        {
            var live = new List<QuestEvent>();
            using var sub = svc.Subscribe(live.Add);

            stream.Push("[10:00:00] LocalPlayer: ProcessBook(\"New Quest: <<<quest_25212_Name>>>\", \"\", \"\", \"\", \"\", False, False, False, False, False, \"\")");
            stream.Push("[10:01:00] LocalPlayer: ProcessBook(\"New Quest: <<<quest_25212_Name>>>\", \"\", \"\", \"\", \"\", False, False, False, False, False, \"\")");
            await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));

            // Second accept for already-active quest is a no-op (no event re-fired).
            live.Where(e => e.Kind == QuestEventKind.Accepted)
                .Should().ContainSingle().Which.InternalName.Should().Be("Quest_Sample_25212");
            svc.ActiveQuests.Should().ContainKey("Quest_Sample_25212");
        }
        finally { await StopAsync(svc); _ = runTask; }
    }

    [Fact]
    public async Task Completed_ProcessCompleteQuestRemovesFromActiveAndStampsHistory()
    {
        var ts = new DateTime(2026, 4, 30, 12, 34, 56, DateTimeKind.Utc);
        var quests = new[]
        {
            QuestFactory.Repeatable("quest_14003", "Quest_Sample_14003", "Sample", TimeSpan.FromHours(20)),
        };
        var (svc, stream, _, _, _) = Build(quests);
        try
        {
            stream.Push(new RawLogLine(ts.AddMinutes(-10), "LocalPlayer: ProcessBook(\"New Quest: <<<quest_14003_Name>>>\", \"\", \"\", \"\", \"\", False, False, False, False, False, \"\")"));
            stream.Push(new RawLogLine(ts, "LocalPlayer: ProcessCompleteQuest(8298169, 14003)"));
            await RunUntilDrainedAsync(svc, stream);

            svc.ActiveQuests.Should().NotContainKey("Quest_Sample_14003");
            svc.CompletionHistory.Should().ContainKey("Quest_Sample_14003");
            svc.CompletionHistory["Quest_Sample_14003"].LastCompletedAt
                .Should().Be(new DateTimeOffset(ts, TimeSpan.Zero));
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Completed_DuplicateLineIsIdempotent()
    {
        var ts = new DateTime(2026, 4, 30, 12, 34, 56, DateTimeKind.Utc);
        var quests = new[]
        {
            QuestFactory.Repeatable("quest_14003", "Quest_Sample_14003", "Sample", TimeSpan.FromHours(20)),
        };
        var (svc, stream, _, _, _) = Build(quests);
        var runTask = svc.StartAsync(CancellationToken.None);
        try
        {
            var live = new List<QuestEvent>();
            using var sub = svc.Subscribe(live.Add);

            stream.Push(new RawLogLine(ts, "LocalPlayer: ProcessCompleteQuest(123, 14003)"));
            stream.Push(new RawLogLine(ts, "LocalPlayer: ProcessCompleteQuest(123, 14003)"));
            await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));

            // Same timestamp = same observation. Only one Completed event.
            live.Where(e => e.Kind == QuestEventKind.Completed).Should().ContainSingle();
            svc.CompletionHistory["Quest_Sample_14003"].LastCompletedAt
                .Should().Be(new DateTimeOffset(ts, TimeSpan.Zero));
        }
        finally { await StopAsync(svc); _ = runTask; }
    }

    [Fact]
    public async Task Persistence_StateSurvivesServiceRestart()
    {
        var quests = new[]
        {
            QuestFactory.Repeatable("quest_1", "Q1", "Q1", TimeSpan.FromHours(1)),
            QuestFactory.Repeatable("quest_2", "Q2", "Q2", TimeSpan.FromHours(20)),
        };

        // First service: accept Q1, complete Q2.
        {
            var (svc, stream, _, _, _) = Build(quests);
            try
            {
                stream.Push("[10:00:00] LocalPlayer: ProcessBook(\"New Quest: <<<quest_1_Name>>>\", \"\", \"\", \"\", \"\", False, False, False, False, False, \"\")");
                stream.Push("[10:05:00] LocalPlayer: ProcessCompleteQuest(123, 2)");
                await RunUntilDrainedAsync(svc, stream);
            }
            finally { await StopAsync(svc); }
        }

        // Second service against same character dir: should reload.
        {
            var (svc, _, _, _, _) = Build(quests);
            try
            {
                svc.ActiveQuests.Should().ContainKey("Q1");
                svc.CompletionHistory.Should().ContainKey("Q2");
            }
            finally { await StopAsync(svc); }
        }
    }

    [Fact]
    public async Task CharacterSwitch_ReloadsStateAndFiresDiffToLiveSubscribers()
    {
        var quests = new[]
        {
            QuestFactory.Repeatable("quest_1", "Q1", "Q1", TimeSpan.FromHours(1)),
            QuestFactory.Repeatable("quest_2", "Q2", "Q2", TimeSpan.FromHours(1)),
        };

        // Pre-populate Bob's quests.json by running a short-lived service against him.
        {
            var refData = new FakeReferenceData(quests);
            var bobActive = new FakeActiveCharacterService();
            bobActive.SetActiveCharacter("Bob", "Kwatoxi");
            var bobStore = new PerCharacterStore<QuestServiceState>(_charactersDir, "quests.json",
                QuestServiceStateJsonContext.Default.QuestServiceState);
            var bobView = new PerCharacterView<QuestServiceState>(bobActive, bobStore);
            var bobStream = new ScriptedStream(Array.Empty<string>());
            var bobSvc = new QuestService(bobStream.Driver, new QuestJournalLoadParser(),
                new QuestAcceptedParser(refData), new QuestCompletedParser(refData), bobView, refData);
            try
            {
                bobStream.Push("[10:00:00] LocalPlayer: ProcessBook(\"New Quest: <<<quest_2_Name>>>\", \"\", \"\", \"\", \"\", False, False, False, False, False, \"\")");
                await RunUntilDrainedAsync(bobSvc, bobStream);
            }
            finally { await StopAsync(bobSvc); }
        }

        // Now run as Arthur, accept Q1, then switch to Bob → should see
        // Abandoned(Q1) + Accepted(Q2) on the live subscriber.
        var (svc, stream, active, _, _) = Build(quests, character: "Arthur");
        try
        {
            stream.Push("[10:00:00] LocalPlayer: ProcessBook(\"New Quest: <<<quest_1_Name>>>\", \"\", \"\", \"\", \"\", False, False, False, False, False, \"\")");
            await RunUntilDrainedAsync(svc, stream);

            var live = new List<QuestEvent>();
            using var sub = svc.Subscribe(live.Add);
            live.Clear(); // discard Subscribe replay

            active.SetActiveCharacter("Bob", "Kwatoxi");

            live.Should().Contain(e => e.Kind == QuestEventKind.Abandoned && e.InternalName == "Q1");
            live.Should().Contain(e => e.Kind == QuestEventKind.Accepted && e.InternalName == "Q2");
            svc.ActiveQuests.Should().ContainKey("Q2").And.NotContainKey("Q1");
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Subscribe_DisposedHandlerStopsReceivingEvents()
    {
        var quests = new[]
        {
            QuestFactory.Repeatable("quest_1", "Q1", "Q1", TimeSpan.FromHours(1)),
        };
        var (svc, stream, _, _, _) = Build(quests);
        var runTask = svc.StartAsync(CancellationToken.None);
        try
        {
            var live = new List<QuestEvent>();
            var sub = svc.Subscribe(live.Add);

            stream.Push("[10:00:00] LocalPlayer: ProcessBook(\"New Quest: <<<quest_1_Name>>>\", \"\", \"\", \"\", \"\", False, False, False, False, False, \"\")");
            await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));
            live.Should().HaveCount(1);

            sub.Dispose();
            sub.Dispose(); // idempotent

            stream.Push("[10:01:00] LocalPlayer: ProcessCompleteQuest(123, 1)");
            await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));
            live.Should().HaveCount(1, "the disposed subscription must not receive further events");
        }
        finally { await StopAsync(svc); _ = runTask; }
    }

    private static async Task RunUntilDrainedAsync(QuestService svc, ScriptedStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = svc.StartAsync(cts.Token);
        await stream.WaitForDrainAsync(cts.Token);
        await cts.CancelAsync();
        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
        _ = runTask;
    }

    private static async Task StopAsync(QuestService svc)
    {
        try { await svc.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { /* test cleanup */ }
        svc.Dispose();
    }

    /// <summary>
    /// Byte-equivalence regression test (#550 PR 2): the producer is a
    /// state-rebuilder, so feeding the same backlog twice (cold start, then
    /// a FromSessionStart replay of the very same lines) must yield
    /// identical active/completed dicts on both runs.
    /// </summary>
    [Fact]
    public async Task L1_replay_idempotence_byte_equivalence()
    {
        var quests = new[]
        {
            QuestFactory.Repeatable("quest_1", "Q1", "Quest 1", TimeSpan.FromHours(1)),
            QuestFactory.Repeatable("quest_2", "Q2", "Quest 2", TimeSpan.FromHours(1)),
        };
        IReadOnlyDictionary<string, QuestJournalEntry> firstActive;
        IReadOnlyDictionary<string, QuestCompletionState> firstCompleted;
        {
            var (svc, stream, _, _, _) = Build(quests, character: "ReplayUser1");
            try
            {
                stream.Push("[10:00:00] LocalPlayer: ProcessBook(\"New Quest: <<<quest_1_Name>>>\", \"\", \"\", \"\", \"\", False, False, False, False, False, \"\")");
                stream.Push("[10:01:00] LocalPlayer: ProcessBook(\"New Quest: <<<quest_2_Name>>>\", \"\", \"\", \"\", \"\", False, False, False, False, False, \"\")");
                stream.Push("[10:02:00] LocalPlayer: ProcessCompleteQuest(123, 1)");
                await RunUntilDrainedAsync(svc, stream);
                firstActive = svc.ActiveQuests;
                firstCompleted = svc.CompletionHistory;
            }
            finally { await StopAsync(svc); }
        }

        // Second pass — same lines surfaced as FromSessionStart replay (with
        // a fresh character so persistence doesn't merge with the first
        // pass's saved state).
        {
            var (svc, stream, _, _, _) = Build(quests, character: "ReplayUser2");
            try
            {
                stream.Driver.PushReplay(MakeLocal(
                    "ProcessBook(\"New Quest: <<<quest_1_Name>>>\", \"\", \"\", \"\", \"\", False, False, False, False, False, \"\")",
                    new DateTime(2026, 5, 18, 10, 0, 0, DateTimeKind.Utc)));
                stream.Driver.PushReplay(MakeLocal(
                    "ProcessBook(\"New Quest: <<<quest_2_Name>>>\", \"\", \"\", \"\", \"\", False, False, False, False, False, \"\")",
                    new DateTime(2026, 5, 18, 10, 1, 0, DateTimeKind.Utc)));
                stream.Driver.PushReplay(MakeLocal(
                    "ProcessCompleteQuest(123, 1)",
                    new DateTime(2026, 5, 18, 10, 2, 0, DateTimeKind.Utc)));
                await RunUntilDrainedAsync(svc, stream);
                svc.ActiveQuests.Keys.Should().BeEquivalentTo(firstActive.Keys);
                svc.CompletionHistory.Keys.Should().BeEquivalentTo(firstCompleted.Keys);
            }
            finally { await StopAsync(svc); }
        }
    }

    private static LocalPlayerLogLine MakeLocal(string dataNoEnvelope, DateTime ts) =>
        new(new DateTimeOffset(ts, TimeSpan.Zero), dataNoEnvelope,
            Sequence: 0, ReadMonotonicTicks: 0);

    /// <summary>
    /// Post-#550 PR 2: adapter that lets these tests keep scripting full
    /// <c>[ts] LocalPlayer: …</c> Player.log lines while the
    /// service-under-test consumes <see cref="LocalPlayerLogLine"/>s via
    /// the L1 driver.
    /// </summary>
    private sealed class ScriptedStream : IDisposable
    {
        private const int TsPrefixLen = 11; // "[HH:MM:SS] "
        private const string ActorToken = "LocalPlayer: ";

        public TestLogStreamDriver Driver { get; } = new();

        public ScriptedStream(params string[] lines)
            : this(lines.Select(l => new RawLogLine(DateTime.UtcNow, l)).ToArray()) { }

        public ScriptedStream(params RawLogLine[] lines)
        {
            foreach (var line in lines) Driver.PushLive(ToLocal(line));
        }

        public void Push(string line) => Driver.PushLive(ToLocal(new RawLogLine(DateTime.UtcNow, line)));
        public void Push(RawLogLine line) => Driver.PushLive(ToLocal(line));

        public Task WaitForDrainAsync(CancellationToken ct) =>
            Driver.DrainLocalPlayerAsync().WaitAsync(ct);
        public Task WaitForDrainAsync(TimeSpan timeout) =>
            Driver.DrainLocalPlayerAsync(timeout);

        public void Dispose() => Driver.Dispose();

        private static LocalPlayerLogLine ToLocal(RawLogLine raw)
        {
            var line = raw.Line;
            var idx = 0;
            if (line.Length > TsPrefixLen && line[0] == '[' && line[3] == ':' && line[6] == ':' && line[9] == ']')
                idx = TsPrefixLen;
            if (idx + ActorToken.Length <= line.Length
                && line.IndexOf(ActorToken, idx, StringComparison.Ordinal) == idx)
            {
                idx += ActorToken.Length;
            }
            var data = idx == 0 ? line : line.Substring(idx);
            return new LocalPlayerLogLine(
                raw.Timestamp, data, raw.Sequence, raw.ReadMonotonicTicks);
        }
    }
}
