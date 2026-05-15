using System.IO;
using System.Net.Http;
using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Reference;

[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class ReferenceDataServiceAreaCrossLinkIndexTests : IDisposable
{
    private readonly string _root;
    private readonly string _cacheDir;
    private readonly string _bundledDir;

    public ReferenceDataServiceAreaCrossLinkIndexTests()
    {
        _root = Mithril.TestSupport.TestPaths.CreateTempDir("mithril-ref-area-crosslink-tests");
        _cacheDir = Path.Combine(_root, "cache");
        _bundledDir = Path.Combine(_root, "bundled");
        Directory.CreateDirectory(_cacheDir);
        Directory.CreateDirectory(_bundledDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static HttpClient NeverCallHttp() => new(new ThrowingHandler());

    private void WriteFixture(string? landmarksJson = null, string? npcsJson = null, string? areasJson = null)
    {
        if (landmarksJson is not null)
        {
            File.WriteAllText(Path.Combine(_bundledDir, "landmarks.json"), landmarksJson);
            File.WriteAllText(Path.Combine(_bundledDir, "landmarks.meta.json"), "{\"cdnVersion\":\"v1\",\"source\":0}");
        }
        if (npcsJson is not null)
        {
            File.WriteAllText(Path.Combine(_bundledDir, "npcs.json"), npcsJson);
            File.WriteAllText(Path.Combine(_bundledDir, "npcs.meta.json"), "{\"cdnVersion\":\"v1\",\"source\":0}");
        }
        if (areasJson is not null)
        {
            File.WriteAllText(Path.Combine(_bundledDir, "areas.json"), areasJson);
            File.WriteAllText(Path.Combine(_bundledDir, "areas.meta.json"), "{\"cdnVersion\":\"v1\",\"source\":0}");
        }
    }

    [Fact]
    public void Landmarks_ParseAndAreaKey()
    {
        WriteFixture(landmarksJson: """
        {
          "AreaSerbule": [
            { "Name": "To Eltibule", "Type": "Portal", "Desc": "Return to Eltibule", "Loc": "x:0 y:0 z:0" }
          ],
          "AreaEltibule": [
            { "Name": "Pillar of Statehelm", "Type": "MeditationPillar", "Combo": "4017", "Loc": "x:1 y:2 z:3" },
            { "Name": "Platform A", "Type": "TeleportationPlatform", "Loc": "x:4 y:5 z:6" }
          ]
        }
        """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.Landmarks.Should().ContainKeys("AreaSerbule", "AreaEltibule");
        svc.Landmarks["AreaSerbule"].Should().ContainSingle()
            .Which.Name.Should().Be("To Eltibule");
        svc.Landmarks["AreaEltibule"].Should().HaveCount(2);
    }

    [Fact]
    public void Landmark_PocoExtension_TypeAndCombo_ReadFromJson()
    {
        // Regression net for the POCO extension landed alongside this index — confirms the
        // Type / Combo properties bind to the JSON. Pair-of-eyes for the LandmarkFieldCoverageTests
        // in the Mithril.Reference.Tests project; this test exercises the same fields via the
        // full ReferenceDataService load path so a future refactor of either the POCO or the
        // service's deserialization wiring surfaces here.
        WriteFixture(landmarksJson: """
        {
          "AreaEltibule": [
            { "Name": "Pillar A", "Type": "MeditationPillar", "Combo": "4017", "Loc": "x:0 y:0 z:0" },
            { "Name": "Portal A",  "Type": "Portal", "Desc": "Return", "Loc": "x:1 y:1 z:1" }
          ]
        }
        """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        var landmarks = svc.Landmarks["AreaEltibule"];
        landmarks.Should().HaveCount(2);
        landmarks[0].Type.Should().Be("MeditationPillar");
        landmarks[0].Combo.Should().Be("4017");
        landmarks[1].Type.Should().Be("Portal");
        landmarks[1].Combo.Should().BeNull("Combo is meditation-pillar-specific and should be null on portals.");
        landmarks[1].Desc.Should().Be("Return");
    }

    [Fact]
    public void NpcsByArea_GroupsByNpcAreaName()
    {
        WriteFixture(npcsJson: """
        {
          "NPC_Joeh": { "Name": "Joeh", "AreaName": "AreaSerbule", "AreaFriendlyName": "Serbule" },
          "NPC_Marna": { "Name": "Marna", "AreaName": "AreaSerbule", "AreaFriendlyName": "Serbule" },
          "NPC_Norbert": { "Name": "Norbert", "AreaName": "AreaEltibule", "AreaFriendlyName": "Eltibule" },
          "NPC_Wandering": { "Name": "Wandering" }
        }
        """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.NpcsByArea.Should().ContainKey("AreaSerbule");
        svc.NpcsByArea["AreaSerbule"].Select(n => n.Key)
            .Should().BeEquivalentTo(["NPC_Joeh", "NPC_Marna"]);
        svc.NpcsByArea["AreaEltibule"].Should().ContainSingle()
            .Which.Key.Should().Be("NPC_Norbert");
        svc.NpcsByArea.Values.SelectMany(v => v).Select(n => n.Key)
            .Should().NotContain("NPC_Wandering",
                "NPCs without an AreaName should not appear under any key.");
    }

    [Fact]
    public void RefreshNpcsOrAreas_RebuildsNpcsByArea()
    {
        // Cross-trigger matrix verification: either npcs.json or areas.json reloading must
        // rebuild the NpcsByArea index (since either side could mutate the group key set).
        WriteFixture(
            npcsJson: """
            {
              "NPC_Joeh": { "Name": "Joeh", "AreaName": "AreaSerbule" }
            }
            """,
            areasJson: """
            {
              "AreaSerbule": { "FriendlyName": "Serbule" }
            }
            """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);
        svc.NpcsByArea["AreaSerbule"].Select(n => n.Key)
            .Should().BeEquivalentTo(["NPC_Joeh"]);

        // Trigger 1: write a new npcs.json to the cache and reload. The npcs-side
        // ParseAndSwapNpcs must rebuild NpcsByArea.
        File.WriteAllText(Path.Combine(_cacheDir, "npcs.json"), """
        {
          "NPC_Joeh": { "Name": "Joeh", "AreaName": "AreaSerbule" },
          "NPC_Marna": { "Name": "Marna", "AreaName": "AreaSerbule" }
        }
        """);
        File.WriteAllText(Path.Combine(_cacheDir, "npcs.meta.json"), "{\"cdnVersion\":\"v2\",\"source\":1}");

        var afterNpcsRefresh = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);
        afterNpcsRefresh.NpcsByArea["AreaSerbule"].Select(n => n.Key)
            .Should().BeEquivalentTo(["NPC_Joeh", "NPC_Marna"]);

        // Trigger 2: write a new areas.json to the cache and reload. The areas-side
        // ParseAndSwapAreas must also rebuild the index (defensive — even though the
        // implementation doesn't gate the index on a known-area set today, a future
        // refactor that does would silently break without this trigger).
        File.WriteAllText(Path.Combine(_cacheDir, "areas.json"), """
        {
          "AreaSerbule": { "FriendlyName": "Serbule Hills" }
        }
        """);
        File.WriteAllText(Path.Combine(_cacheDir, "areas.meta.json"), "{\"cdnVersion\":\"v2\",\"source\":1}");

        var afterAreasRefresh = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);
        afterAreasRefresh.NpcsByArea["AreaSerbule"].Should().HaveCount(2,
            "an areas-only refresh still has both NPCs from the npcs cache, and the index must be rebuilt to surface them.");
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("HTTP must not be called in this test");
    }
}
