using System.IO;
using FluentAssertions;
using Mithril.Shell;
using Xunit;

namespace Mithril.Shell.Tests;

/// <summary>
/// #919 carry-over: the two settings relocated from <c>LegolasSettings</c> to the
/// shared <see cref="ShellSettings"/> store must survive an upgrade — a user who
/// had a non-default <c>gameProcessName</c> / <c>calibrationGoodResidualPx</c> in
/// <c>legolas.json</c> keeps that value after the move.
/// </summary>
public sealed class GameConfigCarryOverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mithril-919-" + Guid.NewGuid().ToString("N"));

    public GameConfigCarryOverTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteLegolasJson(string json)
    {
        var path = Path.Combine(_dir, "legolas.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Non_default_legolas_values_are_carried_into_a_default_shell()
    {
        var path = WriteLegolasJson(
            """{ "gameProcessName": "PG_x64", "calibrationGoodResidualPx": 20.0 }""");
        var shell = new ShellSettings();

        var carried = GameConfigCarryOver.Apply(path, shell);

        carried.Should().BeTrue();
        shell.GameProcessName.Should().Be("PG_x64");
        shell.CalibrationGoodResidualPx.Should().Be(20.0);
    }

    [Fact]
    public void Default_legolas_values_are_not_carried()
    {
        var path = WriteLegolasJson(
            """{ "gameProcessName": "ProjectGorgon", "calibrationGoodResidualPx": 12.0 }""");
        var shell = new ShellSettings();

        var carried = GameConfigCarryOver.Apply(path, shell);

        carried.Should().BeFalse();
        shell.GameProcessName.Should().Be("ProjectGorgon");
        shell.CalibrationGoodResidualPx.Should().Be(12.0);
    }

    [Fact]
    public void An_already_customised_shell_value_is_never_clobbered()
    {
        var path = WriteLegolasJson(
            """{ "gameProcessName": "PG_x64", "calibrationGoodResidualPx": 20.0 }""");
        var shell = new ShellSettings
        {
            GameProcessName = "MyLauncher",
            CalibrationGoodResidualPx = 8.0,
        };

        var carried = GameConfigCarryOver.Apply(path, shell);

        carried.Should().BeFalse();
        shell.GameProcessName.Should().Be("MyLauncher");
        shell.CalibrationGoodResidualPx.Should().Be(8.0);
    }

    [Fact]
    public void Carry_over_is_idempotent_second_run_after_persisting_does_nothing()
    {
        var path = WriteLegolasJson(
            """{ "gameProcessName": "PG_x64", "calibrationGoodResidualPx": 20.0 }""");
        var shell = new ShellSettings();

        GameConfigCarryOver.Apply(path, shell).Should().BeTrue();
        // Second run: shell now holds the carried (non-default) values, so the
        // gate is closed and nothing is re-carried even though legolas.json is
        // unchanged.
        GameConfigCarryOver.Apply(path, shell).Should().BeFalse();
        shell.GameProcessName.Should().Be("PG_x64");
        shell.CalibrationGoodResidualPx.Should().Be(20.0);
    }

    [Fact]
    public void Partial_legolas_json_carries_only_the_present_non_default_key()
    {
        // Only the process name is non-default; the residual key is absent.
        var path = WriteLegolasJson("""{ "gameProcessName": "PG_x64" }""");
        var shell = new ShellSettings();

        var carried = GameConfigCarryOver.Apply(path, shell);

        carried.Should().BeTrue();
        shell.GameProcessName.Should().Be("PG_x64");
        shell.CalibrationGoodResidualPx.Should().Be(12.0, "absent key leaves the default");
    }

    [Fact]
    public void Missing_legolas_json_is_a_no_op()
    {
        var shell = new ShellSettings();

        var carried = GameConfigCarryOver.Apply(Path.Combine(_dir, "does-not-exist.json"), shell);

        carried.Should().BeFalse();
        shell.GameProcessName.Should().Be("ProjectGorgon");
        shell.CalibrationGoodResidualPx.Should().Be(12.0);
    }

    [Fact]
    public void Corrupt_legolas_json_is_a_no_op()
    {
        var path = WriteLegolasJson("{ this is not valid json");
        var shell = new ShellSettings();

        var carried = GameConfigCarryOver.Apply(path, shell);

        carried.Should().BeFalse();
        shell.GameProcessName.Should().Be("ProjectGorgon");
    }
}
