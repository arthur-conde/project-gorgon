using FluentAssertions;
using Mithril.GameState.Servers;
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

    private const string ConnectS4 = "connected, url=s4.projectgorgon.com, port=9002";
    private const string ConnectS0 = "connected, url=s0.projectgorgon.com, port=9002";

    private static readonly ServerEntry Laeth = new(
        "s4", "Laeth", "s4.projectgorgon.com", 9002, "Laeth desc");
    private static readonly ServerEntry Arisetsu = new(
        "s0", "Arisetsu", "s0.projectgorgon.com", 9002, "Arisetsu desc");

    [Fact]
    public async Task First_banner_populates_Current_and_raises_SessionStarted_and_pushes_to_anchor()
    {
        using var driver = new TestLogStreamDriver();
        var anchor = new SessionAnchor();
        var svc = new GameSessionService(driver, anchor: anchor);
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
        var svc = new GameSessionService(driver, anchor: anchor);
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

    private static SystemSignalLogLine MakeConnect(string body) =>
        new(DateTimeOffset.UtcNow, SystemSignalKind.ConnectionEvent, body,
            Sequence: 0, ReadMonotonicTicks: 0);

    // --- #611: Server identity (ConnectionEvent + catalog join) ---

    [Fact]
    public async Task Connect_then_banner_resolves_Server_from_catalog()
    {
        using var driver = new TestLogStreamDriver();
        var catalog = new FakeServerCatalog(Laeth, Arisetsu);
        var svc = new GameSessionService(driver, catalog);
        try
        {
            driver.PushLive(MakeConnect(ConnectS4));
            driver.PushLive(MakeBanner(BannerEmraell));
            await RunUntilDrainedAsync(svc, driver);

            svc.Current.Should().NotBeNull();
            svc.Current!.Server.Should().NotBeNull();
            svc.Current.Server!.Id.Should().Be("s4");
            svc.Current.Server.Name.Should().Be("Laeth");
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Banner_without_preceding_connect_publishes_Server_null()
    {
        // Cold-start mid-PG-session: L0 seed seeks past the preamble, so
        // the EVENT(Ok): connected line never reaches L0.5 and the catalog
        // is empty. The banner arrives, gets minted with Server = null.
        using var driver = new TestLogStreamDriver();
        var catalog = new FakeServerCatalog(); // empty catalog
        var svc = new GameSessionService(driver, catalog);
        try
        {
            driver.PushLive(MakeBanner(BannerEmraell));
            await RunUntilDrainedAsync(svc, driver);

            svc.Current.Should().NotBeNull();
            svc.Current!.Server.Should().BeNull();
            svc.Current.CharacterName.Should().Be("Emraell");
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Connect_with_unknown_url_publishes_Server_null()
    {
        // Catalog is populated but the connect URL isn't in it (e.g. a new
        // PG server that's not yet in the corpus, or a catalog that failed
        // to parse). Server resolution returns null; the session is still
        // minted with the rest of its banner fields intact.
        using var driver = new TestLogStreamDriver();
        var catalog = new FakeServerCatalog(Arisetsu); // s0 only, no s4
        var svc = new GameSessionService(driver, catalog);
        try
        {
            driver.PushLive(MakeConnect(ConnectS4));
            driver.PushLive(MakeBanner(BannerEmraell));
            await RunUntilDrainedAsync(svc, driver);

            svc.Current.Should().NotBeNull();
            svc.Current!.Server.Should().BeNull();
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task SessionStarted_payload_carries_resolved_Server()
    {
        using var driver = new TestLogStreamDriver();
        var catalog = new FakeServerCatalog(Laeth);
        var svc = new GameSessionService(driver, catalog);
        try
        {
            GameSession? captured = null;
            svc.SessionStarted += (_, s) => captured = s;

            driver.PushLive(MakeConnect(ConnectS4));
            driver.PushLive(MakeBanner(BannerEmraell));
            await RunUntilDrainedAsync(svc, driver);

            captured.Should().NotBeNull();
            captured!.Server.Should().NotBeNull();
            captured.Server!.Name.Should().Be("Laeth");
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Subscribe_replay_carries_resolved_Server()
    {
        // Late subscriber observes Current via replay — Server must be on
        // the replayed session (not just live deliveries).
        using var driver = new TestLogStreamDriver();
        var catalog = new FakeServerCatalog(Laeth);
        var svc = new GameSessionService(driver, catalog);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = svc.StartAsync(cts.Token);
        try
        {
            driver.PushLive(MakeConnect(ConnectS4));
            driver.PushLive(MakeBanner(BannerEmraell));
            await driver.DrainSystemAsync();

            GameSession? replayed = null;
            using var sub = svc.Subscribe(s => replayed = s);

            replayed.Should().NotBeNull();
            replayed!.Server.Should().NotBeNull();
            replayed.Server!.Id.Should().Be("s4");
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
    public async Task Pending_connect_is_consumed_by_banner_not_reused()
    {
        // A connect-then-banner pair publishes one session with Server set.
        // A subsequent banner (PG re-login same Mithril run) without its
        // own connect must publish Server = null — the prior connect was
        // for the prior session and is one-shot per banner.
        using var driver = new TestLogStreamDriver();
        var catalog = new FakeServerCatalog(Laeth);
        var svc = new GameSessionService(driver, catalog);
        try
        {
            var sessions = new List<GameSession>();
            svc.SessionStarted += (_, s) => sessions.Add(s);

            driver.PushLive(MakeConnect(ConnectS4));
            driver.PushLive(MakeBanner(BannerEmraell));
            driver.PushLive(MakeBanner(BannerSecond)); // re-login, no new connect
            await RunUntilDrainedAsync(svc, driver);

            sessions.Should().HaveCount(2);
            sessions[0].Server.Should().NotBeNull();
            sessions[0].Server!.Id.Should().Be("s4");
            sessions[1].Server.Should().BeNull("the connect URL is one-shot per banner");
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Connect_after_banner_does_not_retroactively_attach_to_current()
    {
        // PG always emits connect before banner. A reverse-order observation
        // (defensive guard) does NOT mutate the already-published session —
        // _current is captured-by-value once the banner publishes.
        using var driver = new TestLogStreamDriver();
        var catalog = new FakeServerCatalog(Laeth);
        var svc = new GameSessionService(driver, catalog);
        try
        {
            driver.PushLive(MakeBanner(BannerEmraell));
            driver.PushLive(MakeConnect(ConnectS4));
            await RunUntilDrainedAsync(svc, driver);

            svc.Current.Should().NotBeNull();
            svc.Current!.Server.Should().BeNull(
                "connect arriving after the banner doesn't retroactively populate");
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Most_recent_connect_wins_when_multiple_arrive_before_banner()
    {
        // Defensive: if two EVENT(Ok): connected lines arrive without an
        // intervening banner (PG re-emit, or a corner-case L0 replay),
        // the last one wins — it's the one the next session connected on.
        using var driver = new TestLogStreamDriver();
        var catalog = new FakeServerCatalog(Laeth, Arisetsu);
        var svc = new GameSessionService(driver, catalog);
        try
        {
            driver.PushLive(MakeConnect(ConnectS4));
            driver.PushLive(MakeConnect(ConnectS0));
            driver.PushLive(MakeBanner(BannerEmraell));
            await RunUntilDrainedAsync(svc, driver);

            svc.Current.Should().NotBeNull();
            svc.Current!.Server!.Id.Should().Be("s0");
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task No_catalog_injected_publishes_Server_null_without_throwing()
    {
        // Defensive: if the service is constructed without an
        // IServerCatalogService (DI race, test scaffolding), connect
        // observations are stashed but resolution skips — Server stays null,
        // no throw.
        using var driver = new TestLogStreamDriver();
        var svc = new GameSessionService(driver, serverCatalog: null);
        try
        {
            driver.PushLive(MakeConnect(ConnectS4));
            driver.PushLive(MakeBanner(BannerEmraell));
            await RunUntilDrainedAsync(svc, driver);

            svc.Current.Should().NotBeNull();
            svc.Current!.Server.Should().BeNull();
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Malformed_connect_payload_does_not_break_subsequent_banner()
    {
        // A garbled connect line is dropped (parser returns null), the
        // pending URL stays empty, the next banner publishes with Server
        // = null. Verifies the malformed-input containment path.
        using var driver = new TestLogStreamDriver();
        var catalog = new FakeServerCatalog(Laeth);
        var svc = new GameSessionService(driver, catalog);
        try
        {
            driver.PushLive(MakeConnect("totally not a connect payload"));
            driver.PushLive(MakeBanner(BannerEmraell));
            await RunUntilDrainedAsync(svc, driver);

            svc.Current.Should().NotBeNull();
            svc.Current!.Server.Should().BeNull();
            svc.Current.CharacterName.Should().Be("Emraell");
        }
        finally { await StopAsync(svc); }
    }

    private sealed class FakeServerCatalog : IServerCatalogService
    {
        private readonly Dictionary<string, ServerEntry> _byUrl;
        private readonly IReadOnlyCollection<ServerEntry> _all;

        public FakeServerCatalog(params ServerEntry[] entries)
        {
            _byUrl = entries.ToDictionary(e => e.Url, StringComparer.OrdinalIgnoreCase);
            _all = entries;
        }

        public ServerEntry? Get(string url) =>
            !string.IsNullOrEmpty(url) && _byUrl.TryGetValue(url, out var e) ? e : null;

        public IReadOnlyCollection<ServerEntry> All => _all;
    }
}
