using FluentAssertions;
using Mithril.GameState.Quests;
using Mithril.GameState.Quests.Parsing;
using Mithril.GameState.Tests.TestSupport;
using Mithril.Shared.Logging;
using Mithril.TestSupport;
using Xunit;

namespace Mithril.GameState.Tests.Quests;

/// <summary>
/// Post-#718 the quest journal is the active-character active set only — no
/// persistence, no completion-history map, no on-subscribe replay of historic
/// completions. Tests exercise the fold (ProcessLoadQuests / ProcessBook /
/// ProcessCompleteQuest) and Subscribe's narrowed replay contract.
/// </summary>
public sealed class PlayerQuestJournalServiceTests
{
    private (PlayerQuestJournalService svc, ScriptedStream stream, FakeReferenceData refData)
        Build(IReadOnlyList<(string Key, Mithril.Reference.Models.Quests.Quest Quest)>? quests = null)
    {
        var refData = new FakeReferenceData(quests ?? []);

        var stream = new ScriptedStream(Array.Empty<string>());
        var svc = new PlayerQuestJournalService(
            stream.Driver,
            new QuestJournalLoadParser(),
            new QuestAcceptedParser(refData),
            new QuestCompletedParser(refData),
            refData);
        return (svc, stream, refData);
    }

    [Fact]
    public async Task SubscribeAfterMutation_ReplaysActiveAsAcceptedOnly_NoCompletedReplay()
    {
        // Post-#718 contract change: Subscribe replays only the current
        // active set (as PlayerQuestAccepted), NOT historic completions.
        // Cross-session completion anchors are owned by module-side ledgers
        // (Gandalf's DerivedTimerProgressService), not the journal.
        var quests = new[]
        {
            QuestFactory.Repeatable("quest_1", "Q1", "Quest 1", TimeSpan.FromHours(1)),
            QuestFactory.Repeatable("quest_2", "Q2", "Quest 2", TimeSpan.FromHours(1)),
        };
        var (svc, stream, _) = Build(quests);
        try
        {
            stream.Push("[10:00:00] LocalPlayer: ProcessBook(\"New Quest: <<<quest_1_Name>>>\", \"\", \"\", \"\", \"\", False, False, False, False, False, \"\")");
            stream.Push("[10:01:00] LocalPlayer: ProcessBook(\"New Quest: <<<quest_2_Name>>>\", \"\", \"\", \"\", \"\", False, False, False, False, False, \"\")");
            stream.Push("[10:02:00] LocalPlayer: ProcessCompleteQuest(123, 1)");
            await RunUntilDrainedAsync(svc, stream);

            var replayed = new List<PlayerQuestEvent>();
            using var sub = svc.Subscribe(replayed.Add);

            // Q2 is still active → replays as PlayerQuestAccepted. Q1 was
            // completed but only the current-session active map is replayed
            // on subscribe; no PlayerQuestCompleted appears.
            replayed.Should().ContainSingle()
                .Which.Should().BeOfType<PlayerQuestAccepted>()
                .Which.InternalName.Should().Be("Q2");
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
        var (svc, stream, _) = Build(quests);
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
        var (svc, stream, _) = Build(quests);
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
        // The PlayerQuestAbandoned event here is a real inference from a real
        // log event: ProcessLoadQuests is PG's authoritative snapshot of the
        // journal, so anything that was active before and is absent from the
        // new snapshot has been abandoned (or completed off-session, but
        // Abandoned is the conservative observation given no
        // ProcessCompleteQuest preceded). Timestamps come from the log line
        // itself — no wall-clock leak.
        var quests = new[]
        {
            QuestFactory.Repeatable("quest_1", "Q1", "Q1", TimeSpan.FromHours(1)),
            QuestFactory.Repeatable("quest_2", "Q2", "Q2", TimeSpan.FromHours(1)),
            QuestFactory.Repeatable("quest_3", "Q3", "Q3", TimeSpan.FromHours(1)),
        };
        var firstLoadTs = new DateTime(2026, 5, 18, 10, 0, 0, DateTimeKind.Utc);
        var secondLoadTs = new DateTime(2026, 5, 18, 11, 0, 0, DateTimeKind.Utc);
        var (svc, stream, _) = Build(quests);
        var runTask = svc.StartAsync(CancellationToken.None);
        try
        {
            // Pre-seed: Q1 + Q2 are active.
            stream.Push(new RawLogLine(firstLoadTs,
                "LocalPlayer: ProcessLoadQuests(1, TransitionalQuestState[], [], [1,2,])"));
            await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));

            var live = new List<PlayerQuestEvent>();
            using var sub = svc.Subscribe(live.Add);
            live.Clear(); // discard the Subscribe replay so we only see the next bulk diff

            // New bulk load: Q1 stays, Q2 leaves, Q3 arrives → 1 Abandoned + 1 Accepted,
            // both stamped on the log-line timestamp (not wall-clock).
            stream.Push(new RawLogLine(secondLoadTs,
                "LocalPlayer: ProcessLoadQuests(1, TransitionalQuestState[], [], [1,3,])"));
            await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));

            live.OfType<PlayerQuestAbandoned>()
                .Should().ContainSingle(a => a.InternalName == "Q2" && a.Timestamp == secondLoadTs);
            live.OfType<PlayerQuestAccepted>()
                .Should().ContainSingle(a => a.InternalName == "Q3" && a.Timestamp == secondLoadTs);
            live.OfType<PlayerQuestAccepted>()
                .Should().NotContain(a => a.InternalName == "Q1");
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
        var (svc, stream, _) = Build(quests);
        var runTask = svc.StartAsync(CancellationToken.None);
        try
        {
            var live = new List<PlayerQuestEvent>();
            using var sub = svc.Subscribe(live.Add);

            stream.Push("[10:00:00] LocalPlayer: ProcessBook(\"New Quest: <<<quest_25212_Name>>>\", \"\", \"\", \"\", \"\", False, False, False, False, False, \"\")");
            stream.Push("[10:01:00] LocalPlayer: ProcessBook(\"New Quest: <<<quest_25212_Name>>>\", \"\", \"\", \"\", \"\", False, False, False, False, False, \"\")");
            await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));

            // Second accept for already-active quest is a no-op (no event re-fired).
            live.OfType<PlayerQuestAccepted>()
                .Should().ContainSingle().Which.InternalName.Should().Be("Quest_Sample_25212");
            svc.ActiveQuests.Should().ContainKey("Quest_Sample_25212");
        }
        finally { await StopAsync(svc); _ = runTask; }
    }

    [Fact]
    public async Task Completed_ProcessCompleteQuestRemovesFromActiveAndFiresCompletedForward()
    {
        var ts = new DateTime(2026, 4, 30, 12, 34, 56, DateTimeKind.Utc);
        var quests = new[]
        {
            QuestFactory.Repeatable("quest_14003", "Quest_Sample_14003", "Sample", TimeSpan.FromHours(20)),
        };
        var (svc, stream, _) = Build(quests);
        var runTask = svc.StartAsync(CancellationToken.None);
        try
        {
            var live = new List<PlayerQuestEvent>();
            using var sub = svc.Subscribe(live.Add);

            stream.Push(new RawLogLine(ts.AddMinutes(-10), "LocalPlayer: ProcessBook(\"New Quest: <<<quest_14003_Name>>>\", \"\", \"\", \"\", \"\", False, False, False, False, False, \"\")"));
            stream.Push(new RawLogLine(ts, "LocalPlayer: ProcessCompleteQuest(8298169, 14003)"));
            await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));

            svc.ActiveQuests.Should().NotContainKey("Quest_Sample_14003");
            live.OfType<PlayerQuestCompleted>()
                .Should().ContainSingle()
                .Which.Should().Match<PlayerQuestCompleted>(c =>
                    c.InternalName == "Quest_Sample_14003" && c.Timestamp == ts);
        }
        finally { await StopAsync(svc); _ = runTask; }
    }

    [Fact]
    public async Task Completed_DuplicateLineIsIdempotent()
    {
        var ts = new DateTime(2026, 4, 30, 12, 34, 56, DateTimeKind.Utc);
        var quests = new[]
        {
            QuestFactory.Repeatable("quest_14003", "Quest_Sample_14003", "Sample", TimeSpan.FromHours(20)),
        };
        var (svc, stream, _) = Build(quests);
        var runTask = svc.StartAsync(CancellationToken.None);
        try
        {
            var live = new List<PlayerQuestEvent>();
            using var sub = svc.Subscribe(live.Add);

            stream.Push(new RawLogLine(ts, "LocalPlayer: ProcessCompleteQuest(123, 14003)"));
            stream.Push(new RawLogLine(ts, "LocalPlayer: ProcessCompleteQuest(123, 14003)"));
            await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));

            // Same timestamp = same observation. Only one Completed event.
            live.OfType<PlayerQuestCompleted>().Should().ContainSingle();
        }
        finally { await StopAsync(svc); _ = runTask; }
    }

    [Fact]
    public async Task Completed_DoesNotReplayOnSubscribe()
    {
        // The defining post-#718 narrowing: a Completed event observed during
        // the current session is fired forward to live subscribers but is NOT
        // replayed on subsequent subscriptions. Cross-session completion
        // anchors are not the journal's concern.
        var ts = new DateTime(2026, 4, 30, 12, 34, 56, DateTimeKind.Utc);
        var quests = new[]
        {
            QuestFactory.Repeatable("quest_14003", "Quest_Sample_14003", "Sample", TimeSpan.FromHours(20)),
        };
        var (svc, stream, _) = Build(quests);
        var runTask = svc.StartAsync(CancellationToken.None);
        try
        {
            stream.Push(new RawLogLine(ts, "LocalPlayer: ProcessCompleteQuest(123, 14003)"));
            await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));

            // Attach a fresh subscriber AFTER the completion fired. It must
            // NOT see PlayerQuestCompleted as a replay.
            var lateReplay = new List<PlayerQuestEvent>();
            using var sub = svc.Subscribe(lateReplay.Add);

            lateReplay.OfType<PlayerQuestCompleted>().Should().BeEmpty(
                "post-#718 the journal does not replay historic completions on subscribe");
            lateReplay.OfType<PlayerQuestAccepted>().Should().BeEmpty(
                "the completed quest is no longer in the active set, so no Accepted replay either");
        }
        finally { await StopAsync(svc); _ = runTask; }
    }

    [Fact]
    public async Task Subscribe_DisposedHandlerStopsReceivingEvents()
    {
        var quests = new[]
        {
            QuestFactory.Repeatable("quest_1", "Q1", "Q1", TimeSpan.FromHours(1)),
        };
        var (svc, stream, _) = Build(quests);
        var runTask = svc.StartAsync(CancellationToken.None);
        try
        {
            var live = new List<PlayerQuestEvent>();
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
    /// identical active dicts on both runs.
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
        {
            var (svc, stream, _) = Build(quests);
            try
            {
                stream.Push("[10:00:00] LocalPlayer: ProcessBook(\"New Quest: <<<quest_1_Name>>>\", \"\", \"\", \"\", \"\", False, False, False, False, False, \"\")");
                stream.Push("[10:01:00] LocalPlayer: ProcessBook(\"New Quest: <<<quest_2_Name>>>\", \"\", \"\", \"\", \"\", False, False, False, False, False, \"\")");
                stream.Push("[10:02:00] LocalPlayer: ProcessCompleteQuest(123, 1)");
                await RunUntilDrainedAsync(svc, stream);
                firstActive = svc.ActiveQuests;
            }
            finally { await StopAsync(svc); }
        }

        // Second pass — same input split across the replay→live boundary so
        // the test exercises the production FromSessionStart shape (first
        // half drains as IsReplay=true envelopes, then the rest is live).
        {
            var (svc, stream, _) = Build(quests);
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
    /// service-under-test consumes <see cref="LocalPlayerLogLine"/>s via the
    /// L1 driver. Stripping is delegated to <see cref="TestLogEnvelopeFactory"/>
    /// so all producer-tests share one strip semantics.
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
