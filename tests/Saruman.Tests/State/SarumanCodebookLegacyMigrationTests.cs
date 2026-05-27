using System.IO;
using System.Text.Json;
using FluentAssertions;
using Saruman.State;
using Xunit;

namespace Saruman.Tests.State;

[Collection("FileIO")]
public sealed class SarumanCodebookLegacyMigrationTests : IDisposable
{
    private readonly string _root;

    public SarumanCodebookLegacyMigrationTests()
    {
        _root = Mithril.TestSupport.TestPaths.CreateTempDir("saruman-legacy");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void NoFiles_ReturnsEmpty()
    {
        var migration = new SarumanCodebookLegacyMigration(_root);

        var entries = migration.RecoverAll();

        entries.Should().BeEmpty();
    }

    [Fact]
    public void V1SarumanJson_RecoversCodebook_WithServer()
    {
        WriteV1Saruman("Emraell Laeth", "Kwatoxi", new
        {
            schemaVersion = 1,
            codebook = new Dictionary<string, object>
            {
                ["CHUCKMRYJ"] = new { code = "CHUCKMRYJ", effectName = "Fast Swimmer", description = "Swim fast!", firstDiscoveredAt = "2026-04-23T10:26:56Z", discoveryCount = 1, state = 0 },
                ["VYLLTRIS"] = new { code = "VYLLTRIS", effectName = "Teleport", description = "Teleport somewhere.", firstDiscoveredAt = "2026-04-23T14:13:52Z", discoveryCount = 1, state = 1, spentAt = "2026-04-24T03:54:41Z" },
            }
        });

        var migration = new SarumanCodebookLegacyMigration(_root);
        var entries = migration.RecoverAll();

        entries.Should().HaveCount(2);

        var fast = entries.Single(e => e.Code == "CHUCKMRYJ");
        fast.Server.Should().Be("Kwatoxi");
        fast.Effect.Should().Be("Fast Swimmer");
        fast.LastSpentAt.Should().BeNull();

        var tele = entries.Single(e => e.Code == "VYLLTRIS");
        tele.Server.Should().Be("Kwatoxi");
        tele.LastSpentAt.Should().NotBeNull();
    }

    [Fact]
    public void V1SarumanJson_SkipsIfAlreadyV2()
    {
        WriteV1Saruman("Arthur", "Kwatoxi", new
        {
            schemaVersion = 2,
            spentOverrides = Array.Empty<string>(),
        });

        var migration = new SarumanCodebookLegacyMigration(_root);
        var entries = migration.RecoverAll();

        entries.Should().BeEmpty();
    }

    [Fact]
    public void SplitFiles_RecoversWithServer()
    {
        var slug = "Arthur_Kwatoxi";
        var charDir = Path.Combine(_root, slug);
        Directory.CreateDirectory(charDir);

        File.WriteAllText(Path.Combine(charDir, "wop-discovery.json"), Serialize(new
        {
            schemaVersion = 1,
            discoveries = new Dictionary<string, object>
            {
                ["TESTCODE"] = new { code = "TESTCODE", effectName = "Fast Swim", description = "Go fast", discoveredAt = "2026-01-15T10:00:00+00:00" },
            }
        }));

        var migration = new SarumanCodebookLegacyMigration(_root);
        var entries = migration.RecoverAll();

        entries.Should().HaveCount(1);
        entries[0].Server.Should().Be("Kwatoxi");
        entries[0].Effect.Should().Be("Fast Swim");
    }

    [Fact]
    public void V1SarumanJson_TakesPriority_OverSplitFiles()
    {
        var slug = "Arthur_Kwatoxi";
        var charDir = Path.Combine(_root, slug);
        Directory.CreateDirectory(charDir);

        File.WriteAllText(Path.Combine(charDir, "saruman.json"), Serialize(new
        {
            schemaVersion = 1,
            codebook = new Dictionary<string, object>
            {
                ["FROMV1"] = new { code = "FROMV1", effectName = "V1 Source", description = "", firstDiscoveredAt = "2026-04-01T00:00:00Z", discoveryCount = 1, state = 0 },
            }
        }));
        File.WriteAllText(Path.Combine(charDir, "wop-discovery.json"), Serialize(new
        {
            schemaVersion = 1,
            discoveries = new Dictionary<string, object>
            {
                ["FROMSPLIT"] = new { code = "FROMSPLIT", effectName = "Split Source", description = "", discoveredAt = "2026-05-01T00:00:00+00:00" },
            }
        }));

        var migration = new SarumanCodebookLegacyMigration(_root);
        var entries = migration.RecoverAll();

        entries.Should().Contain(e => e.Code == "FROMV1");
        entries.Should().NotContain(e => e.Code == "FROMSPLIT");
    }

    [Fact]
    public void MultipleCharacterDirs_MergesAll()
    {
        WriteV1Saruman("Arthur", "Kwatoxi", new
        {
            schemaVersion = 1,
            codebook = new Dictionary<string, object>
            {
                ["CODE1"] = new { code = "CODE1", effectName = "E1", firstDiscoveredAt = "2026-04-01T00:00:00Z", discoveryCount = 1, state = 0 },
            }
        });
        WriteV1Saruman("Bilbo", "Kwatoxi", new
        {
            schemaVersion = 1,
            codebook = new Dictionary<string, object>
            {
                ["CODE2"] = new { code = "CODE2", effectName = "E2", firstDiscoveredAt = "2026-04-02T00:00:00Z", discoveryCount = 1, state = 0 },
            }
        });

        var migration = new SarumanCodebookLegacyMigration(_root);
        var entries = migration.RecoverAll();

        entries.Should().HaveCount(2);
        entries.Should().Contain(e => e.Code == "CODE1" && e.Server == "Kwatoxi");
        entries.Should().Contain(e => e.Code == "CODE2" && e.Server == "Kwatoxi");
    }

    private void WriteV1Saruman(string character, string server, object data)
    {
        var slug = $"{character}_{server}";
        var dir = Path.Combine(_root, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "saruman.json"), Serialize(data));
    }

    private static string Serialize(object data) => JsonSerializer.Serialize(data, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    });
}
