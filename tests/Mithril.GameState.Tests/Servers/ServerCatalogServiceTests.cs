using FluentAssertions;
using Mithril.GameState.Servers;
using Mithril.GameState.Servers.Parsing;
using Mithril.GameState.Tests.TestSupport;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Xunit;

namespace Mithril.GameState.Tests.Servers;

public sealed class ServerCatalogServiceTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 5, 21, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Real Player.log payload, envelope-stripped per L0.5
    /// (SystemSignalKind.Servers eats the "Servers: " prefix).
    /// </summary>
    private const string RealStrippedPayload =
        """[ { "AllowGuests" : true, "Port" : 9002, "Description" : "<b>Laeth - \"Time and Space\"</b>\nA brand new world!", "Url" : "s4.projectgorgon.com", "ID" : "s4", "Name" : "Laeth" }, { "AllowGuests" : true, "Port" : 9002, "Description" : "<b>Arisetsu - \"Hope and Warmth\"</b>\nPlay on the original.", "Url" : "s0.projectgorgon.com", "ID" : "s0", "Name" : "Arisetsu" } ]""";

    private const string UpdatedPayload =
        """[ { "AllowGuests" : true, "Port" : 9002, "Description" : "<b>Laeth - \"Time and Space\"</b>\nA brand new world!", "Url" : "s4.projectgorgon.com", "ID" : "s4", "Name" : "Laeth" } ]""";

    private static ServerCatalogService NewService(
        TestLogStreamDriver driver, IDiagnosticsSink? diag = null) =>
        new(driver, new ServerCatalogParser(), diag);

    private static SystemSignalLogLine MakeServersSignal(string strippedPayload) =>
        new(Stamp, SystemSignalKind.Servers, strippedPayload,
            Sequence: 0, ReadMonotonicTicks: 0);

    [Fact]
    public async Task Cold_start_catalog_is_empty()
    {
        using var driver = new TestLogStreamDriver();
        var svc = NewService(driver);
        try
        {
            // Push an unrelated SystemSignal so the pump has something to drain.
            driver.PushLive(new SystemSignalLogLine(
                Stamp, SystemSignalKind.AreaLoading, "LOADING LEVEL AreaSerbule",
                Sequence: 0, ReadMonotonicTicks: 0));
            await RunUntilDrainedAsync(svc, driver);

            svc.All.Should().BeEmpty();
            svc.Get("s0.projectgorgon.com").Should().BeNull();
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Servers_signal_populates_catalog_and_lookup_resolves()
    {
        using var driver = new TestLogStreamDriver();
        var svc = NewService(driver);
        try
        {
            driver.PushLive(MakeServersSignal(RealStrippedPayload));
            await RunUntilDrainedAsync(svc, driver);

            svc.All.Should().HaveCount(2);
            svc.Get("s4.projectgorgon.com").Should().NotBeNull();
            svc.Get("s4.projectgorgon.com")!.Name.Should().Be("Laeth");
            svc.Get("s0.projectgorgon.com").Should().NotBeNull();
            svc.Get("s0.projectgorgon.com")!.Id.Should().Be("s0");
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Get_is_case_insensitive_on_url()
    {
        // PG canonicalizes hostnames to lowercase, but a future consumer
        // might pass an uppercased form (e.g. raw EVENT line). The lookup
        // should still resolve.
        using var driver = new TestLogStreamDriver();
        var svc = NewService(driver);
        try
        {
            driver.PushLive(MakeServersSignal(RealStrippedPayload));
            await RunUntilDrainedAsync(svc, driver);

            svc.Get("S4.ProjectGorgon.com").Should().NotBeNull();
            svc.Get("S4.ProjectGorgon.com")!.Id.Should().Be("s4");
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Get_returns_null_for_unknown_url()
    {
        using var driver = new TestLogStreamDriver();
        var svc = NewService(driver);
        try
        {
            driver.PushLive(MakeServersSignal(RealStrippedPayload));
            await RunUntilDrainedAsync(svc, driver);

            svc.Get("s9.projectgorgon.com").Should().BeNull();
            svc.Get("").Should().BeNull();
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Second_emission_replaces_catalog_atomically()
    {
        // PG-restart-while-Mithril-runs: a fresh Servers: line arrives;
        // the new catalog wins. Older entries that aren't in the new payload
        // disappear; lookups for them return null again.
        using var driver = new TestLogStreamDriver();
        var svc = NewService(driver);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = svc.StartAsync(cts.Token);
        try
        {
            driver.PushLive(MakeServersSignal(RealStrippedPayload));
            await driver.DrainSystemAsync().WaitAsync(cts.Token);
            svc.All.Should().HaveCount(2);

            driver.PushLive(MakeServersSignal(UpdatedPayload));
            await driver.DrainSystemAsync().WaitAsync(cts.Token);

            svc.All.Should().ContainSingle().Which.Id.Should().Be("s4");
            svc.Get("s0.projectgorgon.com").Should().BeNull(
                "the second emission omitted s0, so the prior entry must be evicted");
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
    public async Task Other_system_signals_are_ignored()
    {
        using var driver = new TestLogStreamDriver();
        var svc = NewService(driver);
        try
        {
            driver.PushLive(new SystemSignalLogLine(
                Stamp, SystemSignalKind.AreaLoading, "LOADING LEVEL AreaSerbule", 0, 0));
            driver.PushLive(new SystemSignalLogLine(
                Stamp, SystemSignalKind.LoginBanner, "Logged in as character Emraell. Time UTC=...", 0, 0));
            driver.PushLive(new SystemSignalLogLine(
                Stamp, SystemSignalKind.SessionLifecycle, "EVENT(Ok): playing", 0, 0));
            await RunUntilDrainedAsync(svc, driver);

            svc.All.Should().BeEmpty();
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Malformed_servers_payload_leaves_catalog_unchanged_and_warns()
    {
        using var driver = new TestLogStreamDriver();
        var diag = new DiagnosticsSink();
        var svc = NewService(driver, diag);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = svc.StartAsync(cts.Token);
        try
        {
            // First: real payload populates the catalog.
            driver.PushLive(MakeServersSignal(RealStrippedPayload));
            await driver.DrainSystemAsync().WaitAsync(cts.Token);
            svc.All.Should().HaveCount(2);

            // Then: a malformed line. Catalog must stay populated (the prior
            // good view); a warn diagnostic surfaces the failure.
            driver.PushLive(MakeServersSignal("[ totally not json"));
            await driver.DrainSystemAsync().WaitAsync(cts.Token);

            svc.All.Should().HaveCount(2, "the malformed payload must NOT clear the prior catalog");
            diag.Snapshot().Should().Contain(e =>
                e.Level == DiagnosticLevel.Warn
                && e.Category == "GameState.ServerCatalog"
                && e.Message.Contains("Failed to parse"));
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
    public async Task Replay_envelope_is_processed_same_as_live()
    {
        // Production attaches with ReplayMode.FromSessionStart, so the Servers:
        // line typically arrives as IsReplay=true. The service must treat it
        // identically.
        using var driver = new TestLogStreamDriver();
        driver.PushReplay(MakeServersSignal(RealStrippedPayload));
        var svc = NewService(driver);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var runTask = svc.StartAsync(cts.Token);
            await driver.DrainSystemAsync().WaitAsync(cts.Token);

            svc.All.Should().HaveCount(2);
            svc.Get("s4.projectgorgon.com")!.Name.Should().Be("Laeth");

            await cts.CancelAsync();
            try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
            _ = runTask;
        }
        finally { svc.Dispose(); }
    }

    private static async Task RunUntilDrainedAsync(ServerCatalogService svc, TestLogStreamDriver driver)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = svc.StartAsync(cts.Token);
        await driver.DrainSystemAsync().WaitAsync(cts.Token);
        await cts.CancelAsync();
        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
        _ = runTask;
    }

    private static async Task StopAsync(ServerCatalogService svc)
    {
        try { await svc.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { /* test cleanup */ }
        svc.Dispose();
    }
}
