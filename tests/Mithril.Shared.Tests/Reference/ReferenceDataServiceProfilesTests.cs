using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Reference;

[Trait("Category", "FileIO")]
[Collection("FileIO")]
public class ReferenceDataServiceProfilesTests : IDisposable
{
    private readonly string _root;
    private readonly string _cacheDir;
    private readonly string _bundledDir;

    public ReferenceDataServiceProfilesTests()
    {
        _root = Mithril.TestSupport.TestPaths.CreateTempDir("mithril-profile-tests");
        _cacheDir = Path.Combine(_root, "cache");
        _bundledDir = Path.Combine(_root, "bundled");
        Directory.CreateDirectory(_cacheDir);
        Directory.CreateDirectory(_bundledDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static HttpClient NoHttp() => new(new ThrowingHandler("HTTP must not be called"));

    [Fact]
    public void Profiles_LoadsFromBundledFile()
    {
        // Bundled tsysprofiles is a flat dict; smallest viable seed.
        File.WriteAllText(Path.Combine(_bundledDir, "tsysprofiles.json"), """
            { "TestPool": ["FakePower1", "FakePower2"], "Empty": [] }
            """);
        // ReferenceDataService needs items.json present (other loaders must run too).
        File.WriteAllText(Path.Combine(_bundledDir, "items.json"), "{}");

        var svc = new ReferenceDataService(_cacheDir, NoHttp(), bundledDir: _bundledDir);

        svc.Profiles.Should().ContainKey("TestPool");
        svc.Profiles["TestPool"].Should().Equal("FakePower1", "FakePower2");
        svc.Profiles.Should().ContainKey("Empty");
        svc.Profiles["Empty"].Should().BeEmpty();
        svc.GetSnapshot("tsysprofiles").Source.Should().Be(ReferenceFileSource.Bundled);
    }

    [Fact]
    public void TSysProfile_RoundTripsThroughItemsJsonParse()
    {
        File.WriteAllText(Path.Combine(_bundledDir, "items.json"), """
            { "item_42": { "Name": "Augment", "InternalName": "TestAugment", "TSysProfile": "WeaponPool" } }
            """);

        var svc = new ReferenceDataService(_cacheDir, NoHttp(), bundledDir: _bundledDir);

        svc.ItemsByInternalName.Should().ContainKey("TestAugment");
        svc.ItemsByInternalName["TestAugment"].TSysProfile.Should().Be("WeaponPool");
    }

    [Fact]
    public void TSysProfile_NullByDefault_WhenJsonOmitsField()
    {
        File.WriteAllText(Path.Combine(_bundledDir, "items.json"), """
            { "item_1": { "Name": "Ordinary", "InternalName": "Ordinary" } }
            """);

        var svc = new ReferenceDataService(_cacheDir, NoHttp(), bundledDir: _bundledDir);

        svc.ItemsByInternalName["Ordinary"].TSysProfile.Should().BeNull();
    }

    [Fact]
    public void RealBundledProfilesJson_ParsesAndContainsKnownProfiles()
    {
        var realBundled = Path.Combine(AppContext.BaseDirectory, "Reference", "BundledData");
        if (!File.Exists(Path.Combine(realBundled, "tsysprofiles.json"))) return;

        var svc = new ReferenceDataService(_cacheDir, NoHttp(), bundledDir: realBundled);
        svc.Profiles.Count.Should().BeGreaterThan(0);
        // "All" is the canonical catch-all profile per BundledData/INDEX.md.
        svc.Profiles.Should().ContainKey("All");
    }

    private sealed class ThrowingHandler(string message) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException(message);
    }
}
