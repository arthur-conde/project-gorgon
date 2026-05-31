using System.Text.Json;
using FluentAssertions;
using Mithril.Shell;
using Xunit;

namespace Mithril.Shell.Tests;

/// <summary>
/// #957 (#208): ShellSettings became schema-versioned. Pins the v1 invariants — a
/// fresh instance is current, <see cref="ShellSettings.Migrate"/> is an identity
/// passthrough, and the version round-trips through the source-gen JSON context.
/// </summary>
public sealed class ShellSettingsVersioningTests
{
    [Fact]
    public void Current_version_is_one_and_fresh_instances_are_current()
    {
        ShellSettings.CurrentVersion.Should().Be(1);
        new ShellSettings().SchemaVersion.Should().Be(ShellSettings.CurrentVersion);
    }

    [Fact]
    public void Migrate_is_an_identity_passthrough()
    {
        var s = new ShellSettings { GameRoot = @"C:\Games\PG", SidebarWidth = 333 };

        var migrated = ShellSettings.Migrate(s);

        migrated.Should().BeSameAs(s);
        migrated.GameRoot.Should().Be(@"C:\Games\PG");
        migrated.SidebarWidth.Should().Be(333);
    }

    [Fact]
    public void Legacy_unversioned_json_loads_as_current_shape()
    {
        // A pre-#957 shell.json never carried a schemaVersion key. STJ leaves the
        // property at its initializer (= current), so the loader treats it as v1 (it
        // IS the v1 shape) — no spurious migration, no throw.
        var loaded = JsonSerializer.Deserialize(
            """{ "gameRoot": "C:/PG", "sidebarWidth": 280 }""",
            ShellSettingsJsonContext.Default.ShellSettings)!;

        loaded.SchemaVersion.Should().Be(ShellSettings.CurrentVersion);
        loaded.GameRoot.Should().Be("C:/PG");
    }

    [Fact]
    public void Schema_version_round_trips_through_json()
    {
        var written = JsonSerializer.Serialize(
            new ShellSettings(), ShellSettingsJsonContext.Default.ShellSettings);

        written.Should().Contain($"\"schemaVersion\": {ShellSettings.CurrentVersion}");
    }
}
