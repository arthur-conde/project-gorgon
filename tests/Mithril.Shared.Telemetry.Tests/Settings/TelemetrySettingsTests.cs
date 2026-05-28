using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using Mithril.Shared.Telemetry.Settings;
using Xunit;

namespace Mithril.Shared.Telemetry.Tests.Settings;

public class TelemetrySettingsTests
{
    [Fact]
    public void Defaults_have_export_disabled()
    {
        var s = new TelemetrySettings();
        s.EnableOtlpExport.Should().BeFalse();
        s.SchemaVersion.Should().Be(1);
    }

    [Fact]
    public void Round_trips_through_JsonSerializer_with_source_gen_context()
    {
        var s = new TelemetrySettings
        {
            EnableOtlpExport = true,
            Endpoint = "http://localhost:5341/ingest/otlp/v1/traces",
            Protocol = OtlpProtocol.HttpProtobuf,
            ServiceName = "mithril-dev",
            Headers = new() { ["X-Seq-ApiKey"] = "dpapi:Zm9v" },
            TagExports = new() { ["module.id"] = true, ["source"] = false },
        };
        var json = JsonSerializer.Serialize(s, TelemetrySettingsJsonContext.Default.TelemetrySettings);
        var round = JsonSerializer.Deserialize<TelemetrySettings>(json, TelemetrySettingsJsonContext.Default.TelemetrySettings);
        round.Should().BeEquivalentTo(s);
    }

    [Fact]
    public void Migrate_from_v0_passthrough_sets_v1()
    {
        var legacy = new TelemetrySettings { SchemaVersion = 0 };
        var migrated = TelemetrySettings.Migrate(legacy);
        migrated.SchemaVersion.Should().Be(1);
    }
}
