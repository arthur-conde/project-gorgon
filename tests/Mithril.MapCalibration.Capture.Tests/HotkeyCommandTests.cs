using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Mithril.MapCalibration.Capture.Hotkeys;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// Task 25 (#914): the two hotkeys. Capture-&-calibrate respects the focus gate
/// (fire only with PG focused, spec §10) and invokes the engine; draw-map-bbox
/// does NOT respect the focus gate (the drag setup happens with the overlay
/// focused). Id / gate / wiring are unit-tested; the drag interaction itself is
/// WPF/manual-verify (Task 28).
/// </summary>
public sealed class HotkeyCommandTests
{
    [Fact]
    public async Task Capture_command_respects_focus_gate_and_invokes_the_engine()
    {
        var engine = new SpyAutoCalibrationEngine();
        var cmd = new CaptureCalibrateCommand(engine);

        cmd.RespectsFocusGate.Should().BeTrue();
        cmd.Id.Should().Be("mapcalibration.capture");
        cmd.Category.Should().Be("Map Calibration");
        cmd.DefaultBinding.Should().BeNull();

        await cmd.ExecuteAsync(default);
        engine.Calls.Should().Be(1);
    }

    [Fact]
    public void Draw_bbox_command_does_not_respect_focus_gate()
    {
        var cmd = new DrawMapBboxCommand(new SpyBboxDrawController());
        cmd.RespectsFocusGate.Should().BeFalse();
        cmd.Id.Should().Be("mapcalibration.draw_bbox");
        cmd.Category.Should().Be("Map Calibration");
    }

    [Fact]
    public async Task Draw_bbox_command_begins_the_draw_mode()
    {
        var controller = new SpyBboxDrawController();
        await new DrawMapBboxCommand(controller).ExecuteAsync(default);
        controller.BeginCalls.Should().Be(1);
    }
}

internal sealed class SpyAutoCalibrationEngine : IAutoCalibrationRunner
{
    public int Calls { get; private set; }
    public Task<AutoCalibrationOutcome> TryCalibrateCurrentAreaAsync(CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(new AutoCalibrationOutcome(true, "AreaSerbule", null));
    }
}

internal sealed class SpyBboxDrawController : IMapBboxDrawController
{
    public int BeginCalls { get; private set; }
    public void BeginDraw() => BeginCalls++;
}
