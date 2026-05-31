using System.IO;
using FluentAssertions;
using Mithril.Shell;
using Xunit;

namespace Mithril.Shell.Tests;

/// <summary>
/// #957 carry-over: the retired <c>LegolasSettings.MapOverlay</c> (overlay window
/// position, DIUs, in <c>legolas/settings.json</c>) migrates into
/// <see cref="ShellSettings.MapCaptureBbox"/> (the one-rect capture frame, physical
/// px) so an upgrading user's overlay frame becomes the capture frame instead of
/// resetting to "no bbox". Pins the cross-file read, the DIU→physical conversion at
/// a given system scale, idempotency, and the don't-clobber / default gates.
/// </summary>
public sealed class MapCaptureRectCarryOverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mithril-957-" + Guid.NewGuid().ToString("N"));

    public MapCaptureRectCarryOverTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteLegolasJson(string json)
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Non_default_overlay_is_carried_into_an_unset_shell_at_100_percent()
    {
        var path = WriteLegolasJson(
            """{ "mapOverlay": { "left": 220, "top": 140, "width": 960, "height": 540 } }""");
        var shell = new ShellSettings();

        var carried = MapCaptureRectCarryOver.Apply(path, shell, dpiScale: 1.0);

        carried.Should().BeTrue();
        shell.MapCaptureBbox.Should().NotBeNull();
        shell.MapCaptureBbox!.Left.Should().Be(220);
        shell.MapCaptureBbox.Top.Should().Be(140);
        shell.MapCaptureBbox.Width.Should().Be(960);
        shell.MapCaptureBbox.Height.Should().Be(540);
    }

    [Fact]
    public void Overlay_is_converted_to_physical_pixels_at_a_non_100_percent_scale()
    {
        var path = WriteLegolasJson(
            """{ "mapOverlay": { "left": 200, "top": 100, "width": 400, "height": 300 } }""");
        var shell = new ShellSettings();

        var carried = MapCaptureRectCarryOver.Apply(path, shell, dpiScale: 1.5);

        carried.Should().BeTrue();
        // DIU · 1.5 → physical, edge-rounded (the same math the snip uses).
        shell.MapCaptureBbox!.Left.Should().Be(300);
        shell.MapCaptureBbox.Top.Should().Be(150);
        shell.MapCaptureBbox.Width.Should().Be(600);
        shell.MapCaptureBbox.Height.Should().Be(450);
    }

    [Fact]
    public void An_already_set_shell_rect_is_never_clobbered()
    {
        var path = WriteLegolasJson(
            """{ "mapOverlay": { "left": 220, "top": 140, "width": 960, "height": 540 } }""");
        var shell = new ShellSettings
        {
            MapCaptureBbox = new MapCaptureBbox { Left = 10, Top = 20, Width = 30, Height = 40 },
        };

        var carried = MapCaptureRectCarryOver.Apply(path, shell, dpiScale: 1.0);

        carried.Should().BeFalse();
        shell.MapCaptureBbox!.Left.Should().Be(10);
        shell.MapCaptureBbox.Width.Should().Be(30);
    }

    [Fact]
    public void Carry_over_is_idempotent_second_run_after_carrying_does_nothing()
    {
        var path = WriteLegolasJson(
            """{ "mapOverlay": { "left": 220, "top": 140, "width": 960, "height": 540 } }""");
        var shell = new ShellSettings();

        MapCaptureRectCarryOver.Apply(path, shell, 1.0).Should().BeTrue();
        // Shell rect is now set → the gate is closed.
        MapCaptureRectCarryOver.Apply(path, shell, 1.0).Should().BeFalse();
    }

    [Fact]
    public void Untouched_factory_default_overlay_is_not_carried()
    {
        // The retired MapOverlay initializer default: left=100, top=100, 800x600.
        var path = WriteLegolasJson(
            """{ "mapOverlay": { "left": 100, "top": 100, "width": 800, "height": 600 } }""");
        var shell = new ShellSettings();

        var carried = MapCaptureRectCarryOver.Apply(path, shell, 1.0);

        carried.Should().BeFalse("an untouched default overlay is no meaningful frame to migrate");
        shell.MapCaptureBbox.Should().BeNull();
    }

    [Fact]
    public void Degenerate_overlay_size_is_not_carried()
    {
        var path = WriteLegolasJson(
            """{ "mapOverlay": { "left": 100, "top": 100, "width": 0, "height": 600 } }""");
        var shell = new ShellSettings();

        MapCaptureRectCarryOver.Apply(path, shell, 1.0).Should().BeFalse();
        shell.MapCaptureBbox.Should().BeNull();
    }

    [Fact]
    public void Missing_overlay_key_is_a_no_op()
    {
        var path = WriteLegolasJson("""{ "clickThroughMap": true }""");
        var shell = new ShellSettings();

        MapCaptureRectCarryOver.Apply(path, shell, 1.0).Should().BeFalse();
        shell.MapCaptureBbox.Should().BeNull();
    }

    [Fact]
    public void Missing_legolas_file_is_a_no_op()
    {
        var shell = new ShellSettings();

        MapCaptureRectCarryOver.Apply(Path.Combine(_dir, "nope.json"), shell, 1.0)
            .Should().BeFalse();
        shell.MapCaptureBbox.Should().BeNull();
    }

    [Fact]
    public void Corrupt_legolas_file_is_a_no_op()
    {
        var path = WriteLegolasJson("{ not valid json");
        var shell = new ShellSettings();

        MapCaptureRectCarryOver.Apply(path, shell, 1.0).Should().BeFalse();
        shell.MapCaptureBbox.Should().BeNull();
    }
}
