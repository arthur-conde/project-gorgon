using System.IO;
using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Reference;

[Trait("Category", "FileIO")]
[Collection("FileIO")]
public class AreaCatalogParseTests : IDisposable
{
    private readonly string _root;
    private readonly string _cacheDir;
    private readonly string _bundledDir;

    public AreaCatalogParseTests()
    {
        _root = Mithril.TestSupport.TestPaths.CreateTempDir("mithril-area-tests");
        _cacheDir = Path.Combine(_root, "cache");
        _bundledDir = Path.Combine(_root, "bundled");
        Directory.CreateDirectory(_cacheDir);
        Directory.CreateDirectory(_bundledDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Areas_load_from_bundled_with_short_name_fallback()
    {
        File.WriteAllText(Path.Combine(_bundledDir, "areas.json"), """
            {
              "AreaSerbule": { "FriendlyName": "Serbule", "ShortFriendlyName": "Serbule" },
              "AreaSunVale": { "FriendlyName": "Sun Vale" },
              "AreaCave1": { "FriendlyName": "Dungeons Beneath Eltibule", "ShortFriendlyName": "Beneath Eltibule" }
            }
            """);

        var svc = new ReferenceDataService(_cacheDir, new System.Net.Http.HttpClient(new ThrowingHandler()), bundledDir: _bundledDir);

        svc.Areas.Should().HaveCount(3);
        svc.Areas["AreaSerbule"].FriendlyName.Should().Be("Serbule");
        svc.Areas["AreaSerbule"].ShortFriendlyName.Should().Be("Serbule");
        // Missing ShortFriendlyName falls back to FriendlyName.
        svc.Areas["AreaSunVale"].FriendlyName.Should().Be("Sun Vale");
        svc.Areas["AreaSunVale"].ShortFriendlyName.Should().Be("Sun Vale");
        svc.Areas["AreaCave1"].ShortFriendlyName.Should().Be("Beneath Eltibule");
    }

    private sealed class ThrowingHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("HTTP must not be called in this test");
    }
}
