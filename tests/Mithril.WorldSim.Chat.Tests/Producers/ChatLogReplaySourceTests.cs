using System.IO;
using FluentAssertions;
using Mithril.Shared.Game;
using Mithril.Shared.Logging;
using Mithril.WorldSim.Chat.Producers;
using Xunit;

namespace Mithril.WorldSim.Chat.Tests.Producers;

public sealed class ChatLogReplaySourceTests : IDisposable
{
    private readonly string _gameRoot;
    private readonly string _chatDir;

    public ChatLogReplaySourceTests()
    {
        _gameRoot = Path.Combine(Path.GetTempPath(), $"mithril-chatreplay-{Guid.NewGuid():N}");
        _chatDir = Path.Combine(_gameRoot, "ChatLogs");
        Directory.CreateDirectory(_chatDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_gameRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private GameConfig Config(double pollSeconds = 0.25) => new()
    {
        GameRoot = _gameRoot,
        PollIntervalSeconds = pollSeconds,
    };

    private void Write(string fileName, string content)
        => File.WriteAllText(Path.Combine(_chatDir, fileName), content);

    private void Append(string fileName, string content)
        => File.AppendAllText(Path.Combine(_chatDir, fileName), content);

    [Fact]
    public async Task Empty_directory_yields_no_envelopes_in_replay_phase()
    {
        var source = new ChatLogReplaySource(Config(pollSeconds: 0.25));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));

        var envelopes = await CollectAsync(source, cts.Token);

        envelopes.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_directory_yields_no_envelopes()
    {
        // Point at a directory that doesn't exist — source must yield no
        // frames (logged diagnostic) rather than throwing.
        var config = new GameConfig
        {
            GameRoot = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}"),
            PollIntervalSeconds = 0.25,
        };
        var source = new ChatLogReplaySource(config);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var envelopes = await CollectAsync(source, cts.Token);

        envelopes.Should().BeEmpty();
    }

    [Fact]
    public async Task No_banner_skips_replay_phase_and_emits_only_appended_lines_live()
    {
        // Existing files have no banner — replay phase is empty. Appends
        // post-attach come through as IsReplay=false.
        Write("chat-Status.log",
            "26-05-19 21:00:01\t[Status] pre-existing line 1\n" +
            "26-05-19 21:00:02\t[Status] pre-existing line 2\n");

        var source = new ChatLogReplaySource(Config(pollSeconds: 0.25));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));

        var collector = CollectAsync(source, cts.Token);

        await Task.Delay(500);  // let the source enter live mode
        Append("chat-Status.log", "26-05-19 21:00:03\t[Status] appended\n");

        var envelopes = await collector;
        envelopes.Where(e => e.IsReplay).Should().BeEmpty();
        envelopes.Where(e => !e.IsReplay)
            .Select(e => e.Payload.Line)
            .Should().Contain(l => l.Contains("appended"));
    }

    [Fact]
    public async Task Most_recent_banner_anchors_replay_and_emits_at_or_after_lines()
    {
        // Banner line at 21:00:02 — pre-banner line should NOT replay;
        // banner + post-banner lines should.
        Write("chat-Status.log",
            "26-05-19 21:00:00\t[Status] pre-banner-A\n" +
            "26-05-19 21:00:01\t[Status] pre-banner-B\n" +
            "26-05-19 21:00:02\t**** Logged In As Emraell. Server Laeth. Timezone Offset 01:00:00.\n" +
            "26-05-19 21:00:03\t[Status] post-banner-A\n" +
            "26-05-19 21:00:04\t[Status] post-banner-B\n");

        var source = new ChatLogReplaySource(Config());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));

        var envelopes = await CollectAsync(source, cts.Token);

        var replayed = envelopes.Where(e => e.IsReplay).Select(e => e.Payload.Line).ToList();
        replayed.Should().HaveCount(3);
        replayed[0].Should().Contain("Logged In As Emraell");
        replayed[1].Should().Contain("post-banner-A");
        replayed[2].Should().Contain("post-banner-B");

        // pre-banner lines must NOT appear at all
        envelopes.Should().NotContain(e => e.Payload.Line.Contains("pre-banner"));
    }

    [Fact]
    public async Task Most_recent_banner_chosen_globally_across_multiple_files()
    {
        // Three channel files. The newer banner is in the second file; the
        // older banner in the first file. The seek anchor must be the newer
        // one, even though its file isn't the lex-first file in the directory.
        Write("chat-Trade.log",
            "26-05-19 20:00:00\t**** Logged In As Old. Server X. Timezone Offset 01:00:00.\n" +
            "26-05-19 20:00:01\t[Trade] old-session-line\n");
        Write("chat-Status.log",
            "26-05-19 21:00:00\t**** Logged In As New. Server X. Timezone Offset 01:00:00.\n" +
            "26-05-19 21:00:01\t[Status] new-session-line\n");
        Write("chat-General.log",
            "26-05-19 21:00:02\t[General] also-new-session\n" +
            "26-05-19 20:30:00\t[General] mid-session-orphan\n");

        var source = new ChatLogReplaySource(Config());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));

        var envelopes = await CollectAsync(source, cts.Token);

        var replayed = envelopes.Where(e => e.IsReplay).Select(e => e.Payload.Line).ToList();

        // The newer banner anchors the replay window to 21:00:00 onward.
        replayed.Should().Contain(l => l.Contains("Logged In As New"));
        replayed.Should().Contain(l => l.Contains("new-session-line"));
        replayed.Should().Contain(l => l.Contains("also-new-session"));

        // Older session lines (before the newer banner's timestamp) must NOT
        // appear in the replay set.
        replayed.Should().NotContain(l => l.Contains("Logged In As Old"));
        replayed.Should().NotContain(l => l.Contains("old-session-line"));
        replayed.Should().NotContain(l => l.Contains("mid-session-orphan"));
    }

    [Fact]
    public async Task Replay_envelopes_carry_IsReplay_true_and_live_envelopes_carry_IsReplay_false()
    {
        Write("chat-Status.log",
            "26-05-19 21:00:00\t**** Logged In As Em. Server Laeth. Timezone Offset 01:00:00.\n" +
            "26-05-19 21:00:01\t[Status] replay-line\n");

        var source = new ChatLogReplaySource(Config(pollSeconds: 0.25));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
        var collector = CollectAsync(source, cts.Token);

        await Task.Delay(500);
        Append("chat-Status.log", "26-05-19 21:00:05\t[Status] live-line\n");

        var envelopes = await collector;
        var replayLines = envelopes.Where(e => e.IsReplay).Select(e => e.Payload.Line).ToList();
        var liveLines = envelopes.Where(e => !e.IsReplay).Select(e => e.Payload.Line).ToList();

        replayLines.Should().Contain(l => l.Contains("Logged In As Em"));
        replayLines.Should().Contain(l => l.Contains("replay-line"));
        liveLines.Should().Contain(l => l.Contains("live-line"));
    }

    [Fact]
    public async Task Replay_emits_lines_in_timestamp_order_across_files()
    {
        // Two files share the post-banner window; their lines should
        // interleave by timestamp in the replay stream.
        Write("chat-A.log",
            "26-05-19 21:00:00\t**** Logged In As Em. Server Laeth. Timezone Offset 01:00:00.\n" +
            "26-05-19 21:00:02\t[A] a-at-2\n" +
            "26-05-19 21:00:05\t[A] a-at-5\n");
        Write("chat-B.log",
            "26-05-19 21:00:03\t[B] b-at-3\n" +
            "26-05-19 21:00:04\t[B] b-at-4\n");

        var source = new ChatLogReplaySource(Config());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));

        var envelopes = await CollectAsync(source, cts.Token);
        var replayLines = envelopes.Where(e => e.IsReplay).Select(e => e.Payload.Line).ToList();

        // Banner at :00, [A] at :02, [B] at :03, [B] at :04, [A] at :05.
        replayLines.Should().HaveCount(5);
        replayLines[0].Should().Contain("Logged In As Em");
        replayLines[1].Should().Contain("a-at-2");
        replayLines[2].Should().Contain("b-at-3");
        replayLines[3].Should().Contain("b-at-4");
        replayLines[4].Should().Contain("a-at-5");
    }

    // ── #640: live-poll merge orders appended frames across files ─────

    [Fact]
    public async Task Live_phase_merges_appended_lines_across_files_by_timestamp()
    {
        // Two files exist at attach (both with the same banner). After the
        // source enters live mode, both files receive appends with
        // interleaved timestamps before the next poll. The subscriber must
        // see the appended frames in strictly non-decreasing timestamp
        // order, not in file-enumeration order. Pre-#640 the iteration was
        // foreach(tailers) → emit-each-tailer's-batch, which leaked
        // filesystem-listing order.
        Write("chat-A.log",
            "26-05-19 21:00:00\t**** Logged In As Em. Server Laeth. Timezone Offset 01:00:00.\n");
        Write("chat-B.log",
            "26-05-19 21:00:00\t**** Logged In As Em. Server Laeth. Timezone Offset 01:00:00.\n");

        var source = new ChatLogReplaySource(Config(pollSeconds: 0.25));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2000));
        var collector = CollectAsync(source, cts.Token);

        // Let the source enter live mode before appending.
        await Task.Delay(500);

        // Interleave appends across two files. File A gets t=10 and t=30;
        // file B gets t=20 and t=40. With the bug, the subscriber sees
        // 10,30,20,40 (A drained, then B drained). With the fix, the
        // subscriber sees 10,20,30,40.
        Append("chat-A.log", "26-05-19 21:00:10\t[A] a-at-10\n");
        Append("chat-B.log", "26-05-19 21:00:20\t[B] b-at-20\n");
        Append("chat-A.log", "26-05-19 21:00:30\t[A] a-at-30\n");
        Append("chat-B.log", "26-05-19 21:00:40\t[B] b-at-40\n");

        var envelopes = await collector;
        var liveTimestamps = envelopes
            .Where(e => !e.IsReplay)
            .Select(e => e.Payload.Timestamp)
            .ToList();

        liveTimestamps.Should().HaveCountGreaterThanOrEqualTo(4,
            "all four appended lines should have been observed within the cancellation window");

        // Strictly non-decreasing across the full live stream — the merge
        // step must order across files within each poll cycle.
        for (var i = 1; i < liveTimestamps.Count; i++)
        {
            liveTimestamps[i].Should().BeOnOrAfter(liveTimestamps[i - 1],
                $"live-phase output must be timestamp-ordered, but index {i} ({liveTimestamps[i]:O}) precedes index {i - 1} ({liveTimestamps[i - 1]:O})");
        }

        var liveLines = envelopes.Where(e => !e.IsReplay)
            .Select(e => e.Payload.Line).ToList();
        liveLines.Should().Contain(l => l.Contains("a-at-10"));
        liveLines.Should().Contain(l => l.Contains("b-at-20"));
        liveLines.Should().Contain(l => l.Contains("a-at-30"));
        liveLines.Should().Contain(l => l.Contains("b-at-40"));
    }

    [Fact]
    public async Task Live_phase_single_file_emits_appends_in_write_order()
    {
        // Single-file steady state: the merge step must not perturb the
        // write order when there is only one tailer.
        Write("chat-Status.log",
            "26-05-19 21:00:00\t**** Logged In As Em. Server Laeth. Timezone Offset 01:00:00.\n");

        var source = new ChatLogReplaySource(Config(pollSeconds: 0.25));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
        var collector = CollectAsync(source, cts.Token);

        await Task.Delay(500);
        Append("chat-Status.log",
            "26-05-19 21:00:10\t[Status] first\n" +
            "26-05-19 21:00:20\t[Status] second\n" +
            "26-05-19 21:00:30\t[Status] third\n");

        var envelopes = await collector;
        var liveLines = envelopes.Where(e => !e.IsReplay)
            .Select(e => e.Payload.Line).ToList();

        liveLines.Should().HaveCount(3);
        liveLines[0].Should().Contain("first");
        liveLines[1].Should().Contain("second");
        liveLines[2].Should().Contain("third");
    }

    // ── #639: session-shared clock offset across files ────────────────

    [Fact]
    public async Task Session_banner_offset_applies_to_files_without_a_banner_of_their_own()
    {
        // Cross-machine replay: chat written on UTC-7 (alt machine), replayed
        // on a UTC+1 host. The banner is in file A; file B is a channel that
        // only carries post-banner content (no banner of its own). Without
        // a shared session-canonical offset (#639), file B's clock would
        // fall through to the host's fallback TZ (UTC+1 here) and mis-stamp
        // every B-line by 8 hours. Test asserts B's lines fold via the
        // banner's UTC-7 offset, not the UTC+1 fallback.
        Write("chat-A.log",
            "26-05-19 09:36:04\t**** Logged In As Praxi. Server Laeth. Timezone Offset -07:00:00.\n" +
            "26-05-19 09:36:05\t[Status] a-after-banner\n");
        Write("chat-B.log",
            "26-05-19 09:36:06\t[General] b-after-banner\n" +
            "26-05-19 09:36:07\t[General] b-also-after-banner\n");

        var replayHostTz = FixedOffsetZone(TimeSpan.FromHours(1));
        var source = new ChatLogReplaySource(Config(), fallbackTz: replayHostTz);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));

        var envelopes = await CollectAsync(source, cts.Token);
        var replayLines = envelopes.Where(e => e.IsReplay).ToList();

        // All replayed lines (from both files) must carry the banner's
        // UTC-7 offset — the canonical session offset.
        replayLines.Should().NotBeEmpty();
        replayLines.Should().AllSatisfy(e =>
            e.Payload.Timestamp.Offset.Should().Be(TimeSpan.FromHours(-7),
                $"every chat line in the session folds via the banner offset, not the host fallback TZ; got {e.Payload.Line}"));

        // The B-file lines are 09:36:06 local; folded via UTC-7 ⇒ 16:36:06 UTC.
        // (Mis-folding via the UTC+1 host TZ would give 08:36:06 UTC — 8h off.)
        var bLine = replayLines.Single(e => e.Payload.Line.Contains("b-after-banner"));
        bLine.Payload.Timestamp.UtcDateTime
            .Should().Be(new DateTime(2026, 5, 19, 16, 36, 6, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Replay_envelopes_are_deterministic_across_runs_for_identical_input()
    {
        // Principle 9: identical input directories produce identical event
        // streams. Pre-#639, a chat file without a banner stamped via the
        // host TZ — replay determinism was conditional on the host. With
        // the session-shared offset, the input directory fully determines
        // the output timestamps.
        Write("chat-A.log",
            "26-05-19 09:36:04\t**** Logged In As Praxi. Server Laeth. Timezone Offset -07:00:00.\n" +
            "26-05-19 09:36:05\t[Status] a-line\n");
        Write("chat-B.log",
            "26-05-19 09:36:06\t[General] b-line\n");

        var hostTz = FixedOffsetZone(TimeSpan.FromHours(1));

        var run1 = await RunReplay(hostTz);
        var run2 = await RunReplay(hostTz);

        var seq1 = run1.Where(e => e.IsReplay)
            .Select(e => (e.Payload.Line, e.Payload.Timestamp.UtcDateTime, e.Payload.Timestamp.Offset))
            .ToList();
        var seq2 = run2.Where(e => e.IsReplay)
            .Select(e => (e.Payload.Line, e.Payload.Timestamp.UtcDateTime, e.Payload.Timestamp.Offset))
            .ToList();

        seq2.Should().BeEquivalentTo(seq1, opts => opts.WithStrictOrdering());

        async Task<List<LogEnvelope<RawLogLine>>> RunReplay(TimeZoneInfo tz)
        {
            var src = new ChatLogReplaySource(Config(), fallbackTz: tz);
            using var c = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            return await CollectAsync(src, c.Token);
        }
    }

    private static TimeZoneInfo FixedOffsetZone(TimeSpan offset)
    {
        // CreateCustomTimeZone returns a fresh instance per call, so the id
        // only needs to be human-readable, not globally unique.
        var id = $"FixedOffsetTestZone_{offset.Ticks}";
        return TimeZoneInfo.CreateCustomTimeZone(id, offset, id, id);
    }

    private static async Task<List<LogEnvelope<RawLogLine>>> CollectAsync(
        IChatLogReplaySource source, CancellationToken ct)
    {
        var envelopes = new List<LogEnvelope<RawLogLine>>();
        try
        {
            await foreach (var e in source.SubscribeWithReplayMarkerAsync(ct))
            {
                envelopes.Add(e);
            }
        }
        catch (OperationCanceledException) { /* expected on poll-timeout */ }
        return envelopes;
    }
}
