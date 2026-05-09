using System.IO;
using System.Text.Json.Nodes;
using FluentAssertions;
using Gandalf.Domain;
using Gandalf.Services;
using Xunit;

namespace Gandalf.Tests;

[Trait("Category", "FileIO")]
[Collection("FileIO")]
public class GandalfAreaFlattenMigrationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _defsPath;

    public GandalfAreaFlattenMigrationTests()
    {
        _dir = Mithril.TestSupport.TestPaths.CreateTempDir("gandalf_area_flatten");
        _defsPath = Path.Combine(_dir, "definitions.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static FakeRefData StandardAreas() => new FakeRefData(
        ("AreaSerbule", "Serbule"),
        ("AreaEltibule", "Eltibule"),
        ("AreaCasino", "Red Wing Casino"));

    private static string V2Payload(params (string Region, string Map)[] timers)
    {
        var arr = new JsonArray();
        for (var i = 0; i < timers.Length; i++)
        {
            var (region, map) = timers[i];
            arr.Add(new JsonObject
            {
                ["id"] = $"id-{i}",
                ["name"] = $"Timer {i}",
                ["kind"] = 0,
                ["duration"] = "01:00:00",
                ["recurring"] = false,
                ["region"] = region,
                ["map"] = map,
                ["soundFilePath"] = null,
            });
        }
        var root = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["timers"] = arr,
        };
        return root.ToJsonString();
    }

    private void Seed(string json) => File.WriteAllText(_defsPath, json);

    private JsonObject ReadDefs()
    {
        var node = JsonNode.Parse(File.ReadAllText(_defsPath));
        return node!.AsObject();
    }

    private void Run(FakeRefData? refData = null)
    {
        var migration = new GandalfAreaFlattenMigration(_defsPath, refData ?? StandardAreas());
        migration.Run();
    }

    [Fact]
    public void Region_only_matching_areas_json_resolves_to_FriendlyName_and_AreaKey()
    {
        Seed(V2Payload(("Serbule", "")));
        Run();

        var obj = ReadDefs();
        ((int)obj["schemaVersion"]!).Should().Be(GandalfDefinitions.Version);
        var t = obj["timers"]!.AsArray()[0]!.AsObject();
        ((string?)t["area"]).Should().Be("Serbule");
        ((string?)t["areaKey"]).Should().Be("AreaSerbule");
        t.ContainsKey("region").Should().BeFalse("legacy region key is removed");
        t.ContainsKey("map").Should().BeFalse("legacy map key is removed");
    }

    [Fact]
    public void Region_matches_when_map_is_a_sublocation_unknown_to_areas_json()
    {
        Seed(V2Payload(("Serbule", "Hogan's Keep")));
        Run();

        var t = ReadDefs()["timers"]!.AsArray()[0]!.AsObject();
        ((string?)t["area"]).Should().Be("Serbule");
        ((string?)t["areaKey"]).Should().Be("AreaSerbule");
    }

    [Fact]
    public void Both_unknown_falls_back_to_concat_with_null_AreaKey()
    {
        Seed(V2Payload(("MyHouse", "Statehome")));
        Run();

        var t = ReadDefs()["timers"]!.AsArray()[0]!.AsObject();
        ((string?)t["area"]).Should().Be("MyHouse > Statehome");
        ((string?)t["areaKey"]).Should().BeNull();
    }

    [Fact]
    public void Region_and_map_equal_collapse_to_single_value()
    {
        Seed(V2Payload(("Serbule", "Serbule")));
        Run();

        var t = ReadDefs()["timers"]!.AsArray()[0]!.AsObject();
        ((string?)t["area"]).Should().Be("Serbule");
        ((string?)t["areaKey"]).Should().Be("AreaSerbule");
    }

    [Fact]
    public void Map_only_unknown_passes_through_with_null_AreaKey()
    {
        Seed(V2Payload(("", "Hogan's Keep")));
        Run();

        var t = ReadDefs()["timers"]!.AsArray()[0]!.AsObject();
        ((string?)t["area"]).Should().Be("Hogan's Keep");
        ((string?)t["areaKey"]).Should().BeNull();
    }

    [Fact]
    public void Map_only_matching_areas_json_resolves_to_FriendlyName_and_AreaKey()
    {
        // Region is empty, so we fall through to checking Map; "Eltibule" is known.
        Seed(V2Payload(("", "Eltibule")));
        Run();

        var t = ReadDefs()["timers"]!.AsArray()[0]!.AsObject();
        ((string?)t["area"]).Should().Be("Eltibule");
        ((string?)t["areaKey"]).Should().Be("AreaEltibule");
    }

    [Fact]
    public void Both_blank_yields_empty_area_and_null_AreaKey()
    {
        Seed(V2Payload(("", "")));
        Run();

        var t = ReadDefs()["timers"]!.AsArray()[0]!.AsObject();
        ((string?)t["area"]).Should().Be("");
        ((string?)t["areaKey"]).Should().BeNull();
    }

    [Fact]
    public void V3_payload_is_idempotent()
    {
        // Already-migrated v3 file must not be touched.
        var v3 = """
        {
          "schemaVersion": 3,
          "timers": [
            { "id": "x", "name": "Already", "kind": 0, "duration": "01:00:00",
              "recurring": false, "area": "Serbule", "areaKey": "AreaSerbule",
              "soundFilePath": null }
          ]
        }
        """;
        Seed(v3);
        var before = File.GetLastWriteTimeUtc(_defsPath);

        Run();

        // Schema unchanged + content untouched (allow tiny clock skew on slow CI).
        var obj = ReadDefs();
        ((int)obj["schemaVersion"]!).Should().Be(3);
        var t = obj["timers"]!.AsArray()[0]!.AsObject();
        ((string?)t["area"]).Should().Be("Serbule");
        ((string?)t["areaKey"]).Should().Be("AreaSerbule");
        t.ContainsKey("region").Should().BeFalse();
    }

    [Fact]
    public void Missing_file_is_a_noop()
    {
        File.Exists(_defsPath).Should().BeFalse();
        Run();
        File.Exists(_defsPath).Should().BeFalse();
    }

    [Fact]
    public void Case_insensitive_FriendlyName_match_canonicalizes_casing()
    {
        // User typed "serbule" lowercase — migration must recognize it and stamp
        // the canonical "Serbule" casing.
        Seed(V2Payload(("serbule", "")));
        Run();

        var t = ReadDefs()["timers"]!.AsArray()[0]!.AsObject();
        ((string?)t["area"]).Should().Be("Serbule");
        ((string?)t["areaKey"]).Should().Be("AreaSerbule");
    }
}
