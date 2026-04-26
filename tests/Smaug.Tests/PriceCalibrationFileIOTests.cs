using System.IO;
using System.Text.Json;
using FluentAssertions;
using Smaug.Domain;
using Xunit;

namespace Smaug.Tests;

[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class PriceCalibrationFileIOTests
{
    /// <summary>Best-effort recursive delete (matches the Arwen tests' pattern).</summary>
    private static void SafeDeleteDir(string dir)
    {
        if (!Directory.Exists(dir)) return;
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// File-IO tests don't exercise <see cref="PriceCalibrationService.RecordObservation"/>,
    /// so a null reference-data service is sufficient — Load/Save/Export only touch <c>_data</c>.
    /// </summary>
    private static PriceCalibrationService BuildService(string dataDir) =>
        new(refData: null!, dataDir);

    [Fact]
    public void SplitMigration_LegacySingleFile_LiftsObservationsAndWritesBackup()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("smaug_test");
        try
        {
            var legacyPath = Path.Combine(dir, "calibration.json");
            var observationsPath = Path.Combine(dir, "observations.json");
            File.WriteAllText(legacyPath, """
                {
                  "version": 1,
                  "observations": [
                    {
                      "npcKey": "NPC_Therese",
                      "internalName": "BottleOfWater",
                      "itemKeywords": [],
                      "keywordBucket": "",
                      "baseValue": 11,
                      "pricePaid": 100,
                      "favorTier": "Neutral",
                      "civicPrideLevel": 0,
                      "timestamp": "2026-04-20T00:00:00+00:00"
                    }
                  ]
                }
                """);
            File.Exists(observationsPath).Should().BeFalse();

            var svc = BuildService(dir);

            svc.Data.Observations.Should().HaveCount(1);
            File.Exists(observationsPath).Should().BeTrue();
            using (var obsDoc = JsonDocument.Parse(File.ReadAllBytes(observationsPath)))
            {
                obsDoc.RootElement.GetProperty("observations").GetArrayLength().Should().Be(1);
            }
            using (var aggDoc = JsonDocument.Parse(File.ReadAllBytes(legacyPath)))
            {
                aggDoc.RootElement.TryGetProperty("observations", out _).Should().BeFalse(
                    "calibration.json is now SmaugAggregatesData (rates only)");
            }
            File.Exists(legacyPath + ".split.bak").Should().BeTrue("one-shot pre-split snapshot");
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void SplitMigration_FreshInstall_NoFilesNoBackup()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("smaug_test");
        try
        {
            var svc = BuildService(dir);

            svc.Data.Observations.Should().BeEmpty();
            File.Exists(Path.Combine(dir, "calibration.json")).Should().BeFalse();
            File.Exists(Path.Combine(dir, "observations.json")).Should().BeFalse();
            File.Exists(Path.Combine(dir, "calibration.json.split.bak")).Should().BeFalse();
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void SplitMigration_PostSplitState_NoSecondBackup()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("smaug_test");
        try
        {
            var legacyPath = Path.Combine(dir, "calibration.json");
            File.WriteAllText(legacyPath, """
                {
                  "version": 1,
                  "observations": [
                    {
                      "npcKey": "NPC_Therese",
                      "internalName": "BottleOfWater",
                      "baseValue": 11,
                      "pricePaid": 100,
                      "favorTier": "Neutral",
                      "civicPrideLevel": 0,
                      "timestamp": "2026-04-20T00:00:00+00:00"
                    }
                  ]
                }
                """);

            BuildService(dir);
            var splitBakBytes = File.ReadAllBytes(legacyPath + ".split.bak");

            BuildService(dir);
            File.ReadAllBytes(legacyPath + ".split.bak").Should().Equal(splitBakBytes,
                "BackupBeforeSplit is one-shot and idempotent");
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void SplitMigration_DowngradeThenUpgrade_MergesWithDedup()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("smaug_test");
        try
        {
            var tsA = "2026-04-20T00:00:00+00:00";
            var tsB = "2026-04-21T00:00:00+00:00";
            var tsC = "2026-04-22T00:00:00+00:00";

            string ObsJson(long pricePaid, string ts) => $$"""
                {
                  "npcKey": "NPC_Therese",
                  "internalName": "BottleOfWater",
                  "baseValue": 11,
                  "pricePaid": {{pricePaid}},
                  "favorTier": "Neutral",
                  "civicPrideLevel": 0,
                  "timestamp": "{{ts}}"
                }
                """;

            // observations.json: post-split layout with sells A and B.
            File.WriteAllText(Path.Combine(dir, "observations.json"), $$"""
                {
                  "version": 1,
                  "observations": [
                    {{ObsJson(100, tsA)}},
                    {{ObsJson(110, tsB)}}
                  ]
                }
                """);

            // calibration.json: legacy single-file shape rewritten by a downgraded
            // build. Contains A (duplicate by NpcKey|InternalName|PricePaid|Timestamp)
            // and C (unique).
            File.WriteAllText(Path.Combine(dir, "calibration.json"), $$"""
                {
                  "version": 1,
                  "observations": [
                    {{ObsJson(100, tsA)}},
                    {{ObsJson(120, tsC)}}
                  ]
                }
                """);

            var svc = BuildService(dir);

            svc.Data.Observations.Should().HaveCount(3, "A is deduped, B and C are unique");
            var prices = svc.Data.Observations.Select(o => o.PricePaid).OrderBy(p => p).ToArray();
            prices.Should().Equal(100L, 110L, 120L);
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void Load_CorruptObservationsJson_QuarantinesAndDoesNotOverwrite()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("smaug_test");
        try
        {
            var observationsPath = Path.Combine(dir, "observations.json");
            File.WriteAllText(observationsPath, "{ this is not valid JSON ::");
            var corruptPath = observationsPath + ".corrupt.bak";

            var svc = BuildService(dir);

            svc.Data.Observations.Should().BeEmpty(
                "service starts fresh when observations.json is unreadable");
            File.Exists(corruptPath).Should().BeTrue(
                "the corrupt file is preserved for forensics, not silently overwritten");
            File.ReadAllText(corruptPath).Should().Contain("not valid JSON",
                "the corrupt file's contents must be the original bytes");
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void Load_CorruptObservationsJson_PreservesExistingQuarantine()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("smaug_test");
        try
        {
            var observationsPath = Path.Combine(dir, "observations.json");
            var corruptPath = observationsPath + ".corrupt.bak";

            // User already has a quarantine from a prior run; we should not clobber it.
            File.WriteAllText(corruptPath, "older corrupt observations");
            File.WriteAllText(observationsPath, "newer also-corrupt observations");

            BuildService(dir);

            File.ReadAllText(corruptPath).Should().Be("older corrupt observations",
                "an existing .corrupt.bak must not be overwritten by a fresh quarantine");
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void ExportCommunityJson_UsesWireSchemaVersion_IndependentOfLocalSchema()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("smaug_test");
        try
        {
            // Seed the service with at least one rate by writing legacy fixture data.
            File.WriteAllText(Path.Combine(dir, "calibration.json"), """
                {
                  "version": 1,
                  "observations": [
                    {
                      "npcKey": "NPC_Therese",
                      "internalName": "BottleOfWater",
                      "baseValue": 11,
                      "pricePaid": 100,
                      "favorTier": "Neutral",
                      "civicPrideLevel": 0,
                      "timestamp": "2026-04-20T00:00:00+00:00"
                    }
                  ]
                }
                """);

            var svc = BuildService(dir);
            var json = svc.ExportCommunityJson("a note");

            json.Should().Contain($"\"schemaVersion\": {PriceCalibrationService.CommunityWireSchemaVersion}",
                because: "wire schema is decoupled from local schema and pinned to what CommunityCalibrationService validates");
            json.Should().Contain("\"module\": \"smaug\"");
            json.Should().NotContain("\"observations\"",
                because: "community payload carries aggregated rates only");
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }
}
