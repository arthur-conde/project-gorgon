using FluentAssertions;
using Mithril.MapCalibration.Capture;
using Mithril.Shell.DependencyInjection;
using Xunit;

namespace Mithril.Shell.Tests;

/// <summary>
/// #966 Task 3: the Settings → Diagnostics capture-frame-dump checkboxes flip
/// <see cref="ShellSettings.DumpCalibrationCaptureFrames"/> /
/// <see cref="ShellSettings.DumpCalibrationGrayFrames"/>, which must mirror onto the
/// live <see cref="CaptureDiagnosticsOptions"/> singleton the capture seam reads —
/// without re-resolving the DI graph. Exercises the exact seed + PropertyChanged
/// wiring <c>ShellComposition</c> registers (<see cref="ShellComposition.MirrorCaptureDiagnostics"/>).
/// </summary>
public sealed class CaptureDiagnosticsMirrorTests
{
    [Fact]
    public void Seeds_the_options_from_current_settings()
    {
        var settings = new ShellSettings
        {
            DumpCalibrationCaptureFrames = true,
            DumpCalibrationGrayFrames = true,
        };

        var options = ShellComposition.MirrorCaptureDiagnostics(settings);

        options.DumpCaptureFrames.Should().BeTrue();
        options.DumpGrayFrames.Should().BeTrue();
    }

    [Fact]
    public void Defaults_are_off_when_settings_are_off()
    {
        var options = ShellComposition.MirrorCaptureDiagnostics(new ShellSettings());

        options.DumpCaptureFrames.Should().BeFalse();
        options.DumpGrayFrames.Should().BeFalse();
    }

    [Fact]
    public void Flipping_the_color_dump_setting_mirrors_onto_the_singleton()
    {
        var settings = new ShellSettings();
        var options = ShellComposition.MirrorCaptureDiagnostics(settings);

        settings.DumpCalibrationCaptureFrames = true;
        options.DumpCaptureFrames.Should().BeTrue("flipping the setting must mirror live onto the options POCO");

        settings.DumpCalibrationCaptureFrames = false;
        options.DumpCaptureFrames.Should().BeFalse("turning the setting back off must mirror too");
    }

    [Fact]
    public void Flipping_the_gray_dump_setting_mirrors_onto_the_singleton()
    {
        var settings = new ShellSettings();
        var options = ShellComposition.MirrorCaptureDiagnostics(settings);

        settings.DumpCalibrationGrayFrames = true;
        options.DumpGrayFrames.Should().BeTrue();

        settings.DumpCalibrationGrayFrames = false;
        options.DumpGrayFrames.Should().BeFalse();
    }
}
