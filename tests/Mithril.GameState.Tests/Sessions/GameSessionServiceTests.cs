using FluentAssertions;
using Mithril.GameState.Sessions;
using Mithril.GameState.Tests.TestSupport;
using Mithril.Shared.Logging;
using Xunit;

namespace Mithril.GameState.Tests.Sessions;

public sealed class GameSessionServiceTests
{
    private const int TsPrefixLen = 11; // length of "[HH:MM:SS] "

    private const string BannerEmraell =
        "[12:25:04] Logged in as character Emraell. Time UTC=05/11/2026 12:25:04. Timezone Offset 01:00:00";

    private const string BannerSecond =
        "[14:00:00] Logged in as character Emraell. Time UTC=05/11/2026 14:00:00. Timezone Offset 01:00:00";

    private const string BannerOtherCharacter =
        "[14:00:00] Logged in as character Frodo. Time UTC=05/11/2026 14:00:00. Timezone Offset 01:00:00";

    [Fact]
    public async Task First_banner_populates_Current_and_raises_SessionStarted_and_pushes_to_anchor()
    {
        using var driver = new TestLogStreamDriver();
        var anchor = new SessionAnchor();
        var svc = new GameSessionService(driver, anchor);
        try
        {
            GameSession? captured = null;
            svc.SessionStarted += (_, s) => captured = s;

            driver.PushLive(MakeBanner(BannerEmraell));
            await RunUntilDrainedAsync(svc, driver);

            svc.Current.Should().NotBeNull();
            svc.Current!.CharacterName.Should().Be("Emraell");
            // Shared anchor was pushed-to so the sequencer (Mithril.Shared) can
            // re-anchor without depending on GameSessionService directly.
            anchor.LoggedInUtc.Should().Be(new DateTime(2026, 5, 11, 12, 25, 4, DateTimeKind.Utc));
            captured.Should().NotBeNull();
            captured!.SessionId.Should().Be(svc.Current.SessionId);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Replaying_same_banner_does_not_re_fire()
    {
        using var driver = new TestLogStreamDriver();
        var anchor = new SessionAnchor();
        var svc = new GameSessionService(driver, anchor);
        try
        {
            var startedCount = 0;
            var anchorChangedCount = 0;
            svc.SessionStarted += (_, _) => startedCount++;
            anchor.AnchorChanged += (_, _) => anchorChangedCount++;

            driver.PushLive(MakeBanner(BannerEmraell));
            driver.PushLive(MakeBanner(BannerEmraell));
            driver.PushLive(MakeBanner(BannerEmraell));
            await RunUntilDrainedAsync(svc, driver);

            startedCount.Should().Be(1);
            anchorChangedCount.Should().Be(1);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Second_banner_with_new_login_mints_new_session()
    {
        using var driver = new TestLogStreamDriver();
        var svc = new GameSessionService(driver);
        try
        {
            var sessions = new List<GameSession>();
            svc.SessionStarted += (_, s) => sessions.Add(s);

            driver.PushLive(MakeBanner(BannerEmraell));
            driver.PushLive(MakeBanner(BannerSecond));
            await RunUntilDrainedAsync(svc, driver);

            sessions.Should().HaveCount(2);
            sessions[0].LoggedInUtc.Should().Be(new DateTime(2026, 5, 11, 12, 25, 4, DateTimeKind.Utc));
            sessions[1].LoggedInUtc.Should().Be(new DateTime(2026, 5, 11, 14, 0, 0, DateTimeKind.Utc));
            sessions[1].SessionId.Should().NotBe(sessions[0].SessionId);
            svc.Current!.SessionId.Should().Be(sessions[1].SessionId);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Different_character_mints_new_session_id()
    {
        using var driver = new TestLogStreamDriver();
        var svc = new GameSessionService(driver);
        try
        {
            var sessions = new List<GameSession>();
            svc.SessionStarted += (_, s) => sessions.Add(s);

            driver.PushLive(MakeBanner(BannerEmraell));
            driver.PushLive(MakeBanner(BannerOtherCharacter));
            await RunUntilDrainedAsync(svc, driver);

            sessions.Should().HaveCount(2);
            sessions[0].CharacterName.Should().Be("Emraell");
            sessions[1].CharacterName.Should().Be("Frodo");
            sessions[1].SessionId.Should().NotBe(sessions[0].SessionId);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Subscribe_after_session_observed_replays_current_synchronously()
    {
        using var driver = new TestLogStreamDriver();
        var svc = new GameSessionService(driver);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = svc.StartAsync(cts.Token);
        try
        {
            driver.PushLive(MakeBanner(BannerEmraell));
            await driver.DrainSystemAsync();

            var replayed = new List<GameSession>();
            using var sub = svc.Subscribe(replayed.Add);

            replayed.Should().HaveCount(1);
            replayed[0].SessionId.Should().Be(svc.Current!.SessionId);

            // Live event still arrives without re-subscribing — the service is
            // still running and a second banner mints + delivers a new session.
            driver.PushLive(MakeBanner(BannerSecond));
            await driver.DrainSystemAsync();

            replayed.Should().HaveCount(2);
            replayed[1].SessionId.Should().NotBe(replayed[0].SessionId);
        }
        finally
        {
            await cts.CancelAsync();
            try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
            _ = runTask;
            svc.Dispose();
        }
    }

    [Fact]
    public async Task Subscribe_with_no_current_session_does_not_invoke_handler()
    {
        using var driver = new TestLogStreamDriver();
        var svc = new GameSessionService(driver);
        try
        {
            // Service started but no banner yet.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var runTask = svc.StartAsync(cts.Token);

            var seen = 0;
            using var sub = svc.Subscribe(_ => seen++);

            seen.Should().Be(0);
            await cts.CancelAsync();
            _ = runTask;
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Non_LoginBanner_system_signals_do_not_affect_session()
    {
        // Post-L0.5: GameSessionService only sees system-signal-classified lines.
        // Other Kinds (AreaLoading, PlayerAdded, SessionLifecycle) flow past
        // without affecting session state.
        using var driver = new TestLogStreamDriver();
        var svc = new GameSessionService(driver);
        try
        {
            var starts = 0;
            svc.SessionStarted += (_, _) => starts++;

            driver.PushLive(MakeSignal(SystemSignalKind.AreaLoading, "LOADING LEVEL AreaSerbule"));
            driver.PushLive(MakeSignal(SystemSignalKind.PlayerAdded, "ProcessAddPlayer(1, 2, \"\", \"Emraell\")"));
            driver.PushLive(MakeSignal(SystemSignalKind.SessionLifecycle, "EVENT(Ok): playing"));
            driver.PushLive(MakeBanner(BannerEmraell));
            driver.PushLive(MakeSignal(SystemSignalKind.SessionLifecycle, "EVENT(Ok): sessionUpdate, playTime=60"));
            await RunUntilDrainedAsync(svc, driver);

            starts.Should().Be(1);
            svc.Current!.CharacterName.Should().Be("Emraell");
        }
        finally { await StopAsync(svc); }
    }

    /// <summary>
    /// Byte-equivalence regression test (#550 PR 2): the producer is a
    /// state-rebuilder, so feeding the same backlog twice (cold start, then
    /// session-replay drain of the very same lines) must yield identical
    /// state on both runs.
    /// </summary>
    [Fact]
    public async Task Replay_idempotence_byte_equivalence_under_L1()
    {
        // First pass — feed two distinct banners, capture state.
        string firstId, secondId;
        DateTime firstLogin, secondLogin;
        string firstChar, secondChar;
        {
            using var driver = new TestLogStreamDriver();
            var svc = new GameSessionService(driver);
            try
            {
                driver.PushLive(MakeBanner(BannerEmraell));
                driver.PushLive(MakeBanner(BannerSecond));
                await RunUntilDrainedAsync(svc, driver);
                svc.Current.Should().NotBeNull();
                secondId = svc.Current!.SessionId;
                secondChar = svc.Current.CharacterName;
                secondLogin = svc.Current.LoggedInUtc;

                // Capture the first session via SessionStarted ordering
                var allSessions = new List<GameSession>();
                using var sub = svc.Subscribe(allSessions.Add);
                // After subscribe-replay, only the *current* session is replayed
                // (semantically that IS the producer's exposed state). The
                // intermediate first session is not part of state.
                firstId = secondId; // by design: Current is last-seen
                firstChar = secondChar;
                firstLogin = secondLogin;
            }
            finally { await StopAsync(svc); }
        }

        // Second pass — same input replayed (FromSessionStart): final state
        // must match the first pass exactly.
        {
            using var driver = new TestLogStreamDriver();
            var svc = new GameSessionService(driver);
            try
            {
                driver.PushReplay(MakeBanner(BannerEmraell));
                driver.PushReplay(MakeBanner(BannerSecond));
                await RunUntilDrainedAsync(svc, driver);
                svc.Current.Should().NotBeNull();
                svc.Current!.SessionId.Should().Be(secondId);
                svc.Current.CharacterName.Should().Be(secondChar);
                svc.Current.LoggedInUtc.Should().Be(secondLogin);
            }
            finally { await StopAsync(svc); }
        }
    }

    private static async Task RunUntilDrainedAsync(GameSessionService svc, TestLogStreamDriver driver)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = svc.StartAsync(cts.Token);
        await driver.DrainSystemAsync();
        await cts.CancelAsync();
        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
        _ = runTask;
    }

    private static async Task StopAsync(GameSessionService svc)
    {
        try { await svc.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { /* test cleanup */ }
        svc.Dispose();
    }

    private static SystemSignalLogLine MakeBanner(string fullLine)
    {
        var data = fullLine.Length > TsPrefixLen && fullLine[0] == '['
            ? fullLine.Substring(TsPrefixLen)
            : fullLine;
        return new SystemSignalLogLine(
            DateTimeOffset.UtcNow, SystemSignalKind.LoginBanner, data,
            Sequence: 0, ReadMonotonicTicks: 0);
    }

    private static SystemSignalLogLine MakeSignal(SystemSignalKind kind, string data) =>
        new(DateTimeOffset.UtcNow, kind, data, Sequence: 0, ReadMonotonicTicks: 0);
}
