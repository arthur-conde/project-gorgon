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
public sealed class PlayerQuestJournalServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _charactersDir;

    public PlayerQuestJournalServiceTests()
    {
        _dir = TestPaths.CreateTempDir("quests_service");
        _charactersDir = Path.Combine(_dir, "characters");
        Directory.CreateDirectory(_charactersDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private (PlayerQuestJournalService svc, ScriptedStream stream, FakeActiveCharacterService active,
             FakeReferenceData refData, PerCharacterView<PlayerQuestJournalState> view)
        Build(IReadOnlyList<(string Key, Mithril.Reference.Models.Quests.Quest Quest)>? quests = null,
              string character = "Arthur", string server = "Kwatoxi")
    {
        var refData = new FakeReferenceData(quests ?? []);
        var active = new FakeActiveCharacterService();
        active.SetActiveCharacter(character, server);

        var store = new PerCharacterStore<PlayerQuestJournalState>(_charactersDir, "quests.json",
            PlayerQuestJournalStateJsonContext.Default.PlayerQuestJournalState);
        var view = new PerCharacterView<PlayerQuestJournalState>(active, store);

        var stream = new ScriptedStream(Array.Empty<string>());
        var svc = new PlayerQuestJournalService(
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
    public async Task BulkJournalLoad_InfersAbandonedFromDroppedEntries_StampedOnLogTimestamp()
    {
        // The Abandoned event here is a real inference from a real log event:
        // ProcessLoadQuests is PG's authoritative snapshot of the journal, so
        // anything that was active before and is absent from the new snapshot
        // has been abandoned (or completed off-session, but Abandoned is the
        // conservative observation given no ProcessCompleteQuest preceded).
        // Timestamps come from the log line itself — no wall-clock leak. This
        // is the inference that #607 explicitly preserves while retiring the
        // character-switch synthesis path.
        var quests = new[]
        {
            QuestFactory.Repeatable("quest_1", "Q1", "Q1", TimeSpan.FromHours(1)),
            QuestFactory.Repeatable("quest_2", "Q2", "Q2", TimeSpan.FromHours(1)),
            QuestFactory.Repeatable("quest_3", "Q3", "Q3", TimeSpan.FromHours(1)),
        };
        var firstLoadTs = new DateTime(2026, 5, 18, 10, 0, 0, DateTimeKind.Utc);
        var secondLoadTs = new DateTime(2026, 5, 18, 11, 0, 0, DateTimeKind.Utc);
        var (svc, stream, _, _, _) = Build(quests);
        var runTask = svc.StartAsync(CancellationToken.None);
        try
        {
            // Pre-seed: Q1 + Q2 are active.
            stream.Push(new RawLogLine(firstLoadTs,
                "LocalPlayer: ProcessLoadQuests(1, TransitionalQuestState[], [], [1,2,])"));
            await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));

            var live = new List<QuestEvent>();
            using var sub = svc.Subscribe(live.Add);
            live.Clear(); // discard the Subscribe replay so we only see the next bulk diff

            // New bulk load: Q1 stays, Q2 leaves, Q3 arrives → 1 Abandoned + 1 Accepted,
            // both stamped on the log-line timestamp (not wall-clock).
            stream.Push(new RawLogLine(secondLoadTs,
                "LocalPlayer: ProcessLoadQuests(1, TransitionalQuestState[], [], [1,3,])"));
            await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));

            live.Should().Contain(e =>
                e.Kind == QuestEventKind.Abandoned
                && e.InternalName == "Q2"
                && e.Timestamp == secondLoadTs);
            live.Should().Contain(e =>
                e.Kind == QuestEventKind.Accepted
                && e.InternalName == "Q3"
                && e.Timestamp == secondLoadTs);
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
    public async Task CharacterSwitch_DoesNotSynthesizeEvents()
    {
        // Pre-#607 this service listened for PerCharacterView.CurrentChanged and
        // synthesized Abandoned/Accepted/Completed events stamped with
        // _time.GetUtcNow() so live subscribers could mirror the swap without
        // re-subscribing. Under the world-sim's per-character scope the path
        // collapses: character B's ledger was always character B's; binding
        // the UI to it fires no events on character A's service instance.
        // This regression guards the absence of that synthesis.
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
            var bobStore = new PerCharacterStore<PlayerQuestJournalState>(_charactersDir, "quests.json",
                PlayerQuestJournalStateJsonContext.Default.PlayerQuestJournalState);
            var bobView = new PerCharacterView<PlayerQuestJournalState>(bobActive, bobStore);
            var bobStream = new ScriptedStream(Array.Empty<string>());
            var bobSvc = new PlayerQuestJournalService(bobStream.Driver, new QuestJournalLoadParser(),
                new QuestAcceptedParser(refData), new QuestCompletedParser(refData), bobView, refData);
            try
            {
                bobStream.Push("[10:00:00] LocalPlayer: ProcessBook(\"New Quest: <<<quest_2_Name>>>\", \"\", \"\", \"\", \"\", False, False, False, False, False, \"\")");
                await RunUntilDrainedAsync(bobSvc, bobStream);
            }
            finally { await StopAsync(bobSvc); }
        }

        var (svc, stream, active, _, _) = Build(quests, character: "Arthur");
        try
        {
            stream.Push("[10:00:00] LocalPlayer: ProcessBook(\"New Quest: <<<quest_1_Name>>>\", \"\", \"\", \"\", \"\", False, False, False, False, False, \"\")");
            await RunUntilDrainedAsync(svc, stream);

            var live = new List<QuestEvent>();
            using var sub = svc.Subscribe(live.Add);
            live.Clear(); // discard Subscribe replay

            // Flip the active-character service. Pre-#607 this would have
            // fanned out Abandoned(Q1) + Accepted(Q2) synthesised events;
            // post-#607 nothing happens — the UI is expected to re-resolve
            // the per-character service for the new character instead.
            active.SetActiveCharacter("Bob", "Kwatoxi");

            live.Should().BeEmpty("character switch must not synthesise events on the existing service");
            svc.ActiveQuests.Should().ContainKey("Q1").And.NotContainKey("Q2",
                "the service is bound to Arthur's hydrated state; Bob's ledger is not its concern");
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

    private static async Task RunUntilDrainedAsync(PlayerQuestJournalService svc, ScriptedStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = svc.StartAsync(cts.Token);
        await stream.WaitForDrainAsync(cts.Token);
        await cts.CancelAsync();
        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
        _ = runTask;
    }

    private static async Task StopAsync(PlayerQuestJournalService svc)
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

        // Second pass — same input split across the replay→live boundary
        // so the test exercises the production FromSessionStart shape (first
        // half drains as IsReplay=true envelopes, then the rest is live).
        // Uses a fresh character so persistence doesn't merge with the first
        // pass's saved state.
        {
            var (svc, stream, _, _, _) = Build(quests, character: "ReplayUser2");
            try
            {
                stream.Driver.PushReplay(TestLogEnvelopeFactory.MakeLocalPlayer(
                    "ProcessBook(\"New Quest: <<<quest_1_Name>>>\", \"\", \"\", \"\", \"\", False, False, False, False, False, \"\")",
                    new DateTime(2026, 5, 18, 10, 0, 0, DateTimeKind.Utc)));
                stream.Driver.PushReplay(TestLogEnvelopeFactory.MakeLocalPlayer(
                    "ProcessBook(\"New Quest: <<<quest_2_Name>>>\", \"\", \"\", \"\", \"\", False, False, False, False, False, \"\")",
                    new DateTime(2026, 5, 18, 10, 1, 0, DateTimeKind.Utc)));

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var runTask = svc.StartAsync(cts.Token);
                await stream.WaitForDrainAsync(cts.Token);

                // Tail pushed live, AFTER subscription is up.
                stream.Driver.PushLive(TestLogEnvelopeFactory.MakeLocalPlayer(
                    "ProcessCompleteQuest(123, 1)",
                    new DateTime(2026, 5, 18, 10, 2, 0, DateTimeKind.Utc)));
                await stream.WaitForDrainAsync(cts.Token);

                svc.ActiveQuests.Keys.Should().BeEquivalentTo(firstActive.Keys);
                svc.CompletionHistory.Keys.Should().BeEquivalentTo(firstCompleted.Keys);

                await cts.CancelAsync();
                try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
                _ = runTask;
            }
            finally { await StopAsync(svc); }
        }
    }

    /// <summary>
    /// Post-#550 PR 2: adapter that lets these tests keep scripting full
    /// <c>[ts] LocalPlayer: …</c> Player.log lines while the
    /// service-under-test consumes <see cref="LocalPlayerLogLine"/>s via
    /// the L1 driver. Stripping is delegated to
    /// <see cref="TestLogEnvelopeFactory"/> so all four producer-tests share
    /// one strip semantics.
    /// </summary>
    private sealed class ScriptedStream : IDisposable
    {
        public TestLogStreamDriver Driver { get; } = new();

        public ScriptedStream(params string[] lines)
            : this(lines.Select(l => new RawLogLine(DateTime.UtcNow, l)).ToArray()) { }

        public ScriptedStream(params RawLogLine[] lines)
        {
            foreach (var line in lines) Driver.PushLive(TestLogEnvelopeFactory.FromRawLine(line));
        }

        public void Push(string line) =>
            Driver.PushLive(TestLogEnvelopeFactory.FromRawLine(new RawLogLine(DateTime.UtcNow, line)));
        public void Push(RawLogLine line) =>
            Driver.PushLive(TestLogEnvelopeFactory.FromRawLine(line));

        public Task WaitForDrainAsync(CancellationToken ct) =>
            Driver.DrainLocalPlayerAsync().WaitAsync(ct);
        public Task WaitForDrainAsync(TimeSpan timeout) =>
            Driver.DrainLocalPlayerAsync(timeout);

        public void Dispose() => Driver.Dispose();
    }
}
