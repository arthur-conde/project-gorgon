using System.Text.Json;
using FluentAssertions;
using Legolas.Domain;

namespace Legolas.Tests.Settings;

public class LegolasSettingsMigrationTests
{
    [Fact]
    public void Migrates_v1_pinPending_into_outer_stroke_and_center_fill()
    {
        var json = """
            {
              "colors": {
                "pinPending": "#FF00FF00",
                "pinFinalized": "#FFFF8800",
                "playerMarker": "#FFAA00AA",
                "routeLine": "#FFFFD700",
                "bearingWedgeFill": "#33FFFF80",
                "bearingWedgeStroke": "#55FFFF80"
              }
            }
            """;
        var loaded = JsonSerializer.Deserialize(json, LegolasSettingsJsonContext.Default.LegolasSettings)!;

        // SchemaVersion missing in v1 JSON → C# default of 1; Migrate should
        // detect the gap and run the upgrade.
        loaded.SchemaVersion.Should().Be(1);
        loaded.Colors.LegacyPinPending.Should().Be("#FF00FF00");
        loaded.Colors.LegacyPinFinalized.Should().Be("#FFFF8800");
        loaded.Colors.LegacyPlayerMarker.Should().Be("#FFAA00AA");

        var migrated = LegolasSettings.Migrate(loaded);

        migrated.PinStyle.Outer.StrokeColor.Should().Be("#FF00FF00");
        migrated.PinStyle.Center.FillColor.Should().Be("#FF00FF00");
        migrated.ActivePinStyle.Color.Should().Be("#FFFF8800");
        migrated.PlayerPinStyle.Center.FillColor.Should().Be("#FFAA00AA");
        migrated.Colors.LegacyPinPending.Should().BeNull();
        migrated.Colors.LegacyPinFinalized.Should().BeNull();
        migrated.Colors.LegacyPlayerMarker.Should().BeNull();
    }

    [Fact]
    public void Migrating_a_v2_instance_is_a_no_op()
    {
        var fresh = new LegolasSettings();
        fresh.PinStyle.Outer.StrokeColor = "#FFAA0000"; // user customisation
        fresh.PinStyle.Center.FillColor = "#FF00AA00";
        fresh.ActivePinStyle.Color = "#FF0000AA";

        var migrated = LegolasSettings.Migrate(fresh);

        // SchemaVersion was already current → no copy / no overwrite of customisations.
        migrated.PinStyle.Outer.StrokeColor.Should().Be("#FFAA0000");
        migrated.PinStyle.Center.FillColor.Should().Be("#FF00AA00");
        migrated.ActivePinStyle.Color.Should().Be("#FF0000AA");
    }

    [Fact]
    public void V1_blob_with_only_pinPending_leaves_active_pin_color_at_default()
    {
        var json = """
            {
              "colors": {
                "pinPending": "#FF00FF00"
              }
            }
            """;
        var loaded = JsonSerializer.Deserialize(json, LegolasSettingsJsonContext.Default.LegolasSettings)!;
        var defaultActiveColor = new LegolasActivePinStyle().Color;
        var defaultPlayerCenterFill = LegolasPinStyle.PlayerDefaults().Center.FillColor;

        var migrated = LegolasSettings.Migrate(loaded);

        migrated.PinStyle.Outer.StrokeColor.Should().Be("#FF00FF00");
        migrated.PinStyle.Center.FillColor.Should().Be("#FF00FF00");
        migrated.ActivePinStyle.Color.Should().Be(defaultActiveColor);
        migrated.PlayerPinStyle.Center.FillColor.Should().Be(defaultPlayerCenterFill);
    }

    [Fact]
    public void V1_blob_with_only_playerMarker_migrates_into_player_pin_center_fill()
    {
        var json = """
            {
              "colors": {
                "playerMarker": "#FF4488FF"
              }
            }
            """;
        var loaded = JsonSerializer.Deserialize(json, LegolasSettingsJsonContext.Default.LegolasSettings)!;

        var migrated = LegolasSettings.Migrate(loaded);

        migrated.PlayerPinStyle.Center.FillColor.Should().Be("#FF4488FF");
        // Outer player defaults preserved (white stroke from PlayerDefaults factory).
        migrated.PlayerPinStyle.Outer.StrokeColor.Should().Be("#FFFFFFFF");
    }

    [Fact]
    public void Migrated_settings_round_trip_through_json_drop_legacy_fields()
    {
        var v1 = """
            {
              "colors": {
                "pinPending": "#FF00FF00",
                "pinFinalized": "#FFFF8800",
                "playerMarker": "#FFAA00AA"
              }
            }
            """;
        var loaded = JsonSerializer.Deserialize(v1, LegolasSettingsJsonContext.Default.LegolasSettings)!;
        var migrated = LegolasSettings.Migrate(loaded);
        migrated.SchemaVersion = LegolasSettings.CurrentVersion;

        var written = JsonSerializer.Serialize(migrated, LegolasSettingsJsonContext.Default.LegolasSettings);

        // Legacy field keys should not reappear in saved JSON now that they
        // were cleared.
        written.Should().NotContain("pinPending");
        written.Should().NotContain("pinFinalized");
        written.Should().NotContain("playerMarker");
        written.Should().Contain("\"schemaVersion\": 2");
    }
}
