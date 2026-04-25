using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using Gorgon.Shared.Reference;
using Xunit;

namespace Gorgon.Shared.Tests;

[Trait("Category", "FileIO")]
public sealed class CommunityCalibrationServiceTests : IDisposable
{
    private readonly string _cacheDir;

    public CommunityCalibrationServiceTests()
    {
        _cacheDir = Gorgon.TestSupport.TestPaths.CreateTempDir("gorgon-cc-tests");
    }

    public void Dispose()
    {
        try { Directory.Delete(_cacheDir, recursive: true); } catch { }
    }

    private static HttpClient NeverCallHttp() =>
        new(new ThrowingHandler("HTTP must not be called in this test"));

    [Fact]
    public void NoCache_NoNetwork_StartsEmpty()
    {
        var svc = new CommunityCalibrationService(_cacheDir, NeverCallHttp());

        svc.SamwiseRates.Should().BeNull();
        svc.ArwenRates.Should().BeNull();
        svc.SmaugRates.Should().BeNull();
        svc.Keys.Should().BeEquivalentTo("samwise", "arwen", "smaug");
    }

    [Fact]
    public void LoadsCachedSamwiseOnConstruction()
    {
        var payload = """
            {
              "schemaVersion": 1,
              "module": "samwise",
              "rates": { "Onion": { "avgSeconds": 60, "sampleCount": 5, "minSeconds": 50, "maxSeconds": 70 } }
            }
            """;
        File.WriteAllText(Path.Combine(_cacheDir, "samwise.json"), payload);

        var svc = new CommunityCalibrationService(_cacheDir, NeverCallHttp());

        svc.SamwiseRates.Should().NotBeNull();
        svc.SamwiseRates!.Rates.Should().ContainKey("Onion");
        svc.SamwiseRates.Rates["Onion"].AvgSeconds.Should().Be(60);
    }

    [Fact]
    public void RejectsMismatchedSchemaVersion()
    {
        // Arwen cache file claims schemaVersion 1 (Samwise's version), not 2 — must be rejected.
        var payload = """
            {
              "schemaVersion": 1,
              "module": "arwen",
              "itemRates": { "NPC_X|Item": { "rate": 0.1, "sampleCount": 1, "minRate": 0.1, "maxRate": 0.1 } }
            }
            """;
        File.WriteAllText(Path.Combine(_cacheDir, "arwen.json"), payload);

        var svc = new CommunityCalibrationService(_cacheDir, NeverCallHttp());

        svc.ArwenRates.Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_FetchesAndCaches()
    {
        var payload = """
            {
              "schemaVersion": 1,
              "module": "samwise",
              "rates": { "Carrot": { "avgSeconds": 3600, "sampleCount": 12, "minSeconds": 3500, "maxSeconds": 3700 } }
            }
            """;
        var handler = new RoutingHandler(req =>
        {
            req.RequestUri!.AbsoluteUri.Should()
                .Be("https://raw.githubusercontent.com/arthur-conde/gorgon-calibration/main/aggregated/samwise.json");
            return Respond(payload);
        });

        var fileUpdatedKey = "";
        var svc = new CommunityCalibrationService(_cacheDir, new HttpClient(handler));
        svc.FileUpdated += (_, key) => fileUpdatedKey = key;

        await svc.RefreshAsync("samwise");

        svc.SamwiseRates.Should().NotBeNull();
        svc.SamwiseRates!.Rates.Should().ContainKey("Carrot");
        fileUpdatedKey.Should().Be("samwise");

        // Cache file written atomically
        var cachePath = Path.Combine(_cacheDir, "samwise.json");
        var metaPath = Path.Combine(_cacheDir, "samwise.meta.json");
        var dirContents = Directory.Exists(_cacheDir)
            ? string.Join(",", Directory.GetFileSystemEntries(_cacheDir))
            : "<dir missing>";
        File.Exists(cachePath).Should().BeTrue(because: $"wrote to {cachePath}; cacheDir contents: [{dirContents}]");

        // Meta sidecar also written
        File.Exists(metaPath).Should().BeTrue(because: $"wrote to {metaPath}; cacheDir contents: [{dirContents}]");
    }

    [Fact]
    public async Task RefreshAsync_NetworkFailure_KeepsCachedData()
    {
        // Seed cache
        var cached = """
            {
              "schemaVersion": 1,
              "module": "samwise",
              "rates": { "Onion": { "avgSeconds": 60, "sampleCount": 5, "minSeconds": 50, "maxSeconds": 70 } }
            }
            """;
        File.WriteAllText(Path.Combine(_cacheDir, "samwise.json"), cached);

        var handler = new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var svc = new CommunityCalibrationService(_cacheDir, new HttpClient(handler));

        svc.SamwiseRates.Should().NotBeNull(); // loaded from cache

        await svc.RefreshAsync("samwise"); // 404 — should not clobber cache

        svc.SamwiseRates.Should().NotBeNull();
        svc.SamwiseRates!.Rates.Should().ContainKey("Onion");
    }

    [Fact]
    public async Task RefreshAsync_MalformedBody_KeepsCachedData()
    {
        var cached = """
            {
              "schemaVersion": 1, "module": "samwise",
              "rates": { "Onion": { "avgSeconds": 60, "sampleCount": 5, "minSeconds": 50, "maxSeconds": 70 } }
            }
            """;
        File.WriteAllText(Path.Combine(_cacheDir, "samwise.json"), cached);

        var handler = new RoutingHandler(_ => Respond("not json at all"));
        var svc = new CommunityCalibrationService(_cacheDir, new HttpClient(handler));

        await svc.RefreshAsync("samwise");

        svc.SamwiseRates!.Rates.Should().ContainKey("Onion");
    }

    [Fact]
    public void ClearCache_DeletesFilesAndClearsMemory()
    {
        var payload = """
            { "schemaVersion": 2, "module": "arwen", "itemRates": { "k": { "rate": 0.1, "sampleCount": 1, "minRate": 0.1, "maxRate": 0.1 } } }
            """;
        File.WriteAllText(Path.Combine(_cacheDir, "arwen.json"), payload);

        var svc = new CommunityCalibrationService(_cacheDir, NeverCallHttp());
        svc.ArwenRates.Should().NotBeNull();

        svc.ClearCache();

        svc.ArwenRates.Should().BeNull();
        File.Exists(Path.Combine(_cacheDir, "arwen.json")).Should().BeFalse();
    }

    [Fact]
    public async Task RefreshAsync_RejectsNetworkPayloadWithMismatchedSchemaVersion()
    {
        var payload = """
            {
              "schemaVersion": 99,
              "module": "samwise",
              "rates": { "Carrot": { "avgSeconds": 3600, "sampleCount": 12, "minSeconds": 3500, "maxSeconds": 3700 } }
            }
            """;
        var handler = new RoutingHandler(_ => Respond(payload));
        var svc = new CommunityCalibrationService(_cacheDir, new HttpClient(handler));

        await svc.RefreshAsync("samwise");

        svc.SamwiseRates.Should().BeNull();
        // Cache also shouldn't be written
        File.Exists(Path.Combine(_cacheDir, "samwise.json")).Should().BeFalse();
    }

    private static HttpResponseMessage Respond(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

    private sealed class ThrowingHandler(string message) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException(message);
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(route(request));
    }
}
