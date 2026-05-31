using System.Threading.Tasks;
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// Task 26 (#914): the auto-attempt trigger. On area-change, fire the engine iff
/// a bbox is set AND the game is focused AND the area is uncalibrated OR carries
/// only a BundledBaseline. NEVER overwrite an existing UserRefinement/AutoCapture
/// on the auto path (the manual hotkey always attempts). Debounce repeats.
/// </summary>
public sealed class AutoCalibrationTriggerTests
{
    private const string Area = "AreaEltibule";

    [Fact]
    public async Task Does_not_attempt_when_no_bbox()
    {
        var engine = new SpyAutoCalibrationEngine();
        var trigger = Build(engine, bbox: null, focused: true);
        await trigger.OnAreaChangedAsync(Area);
        engine.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Attempts_when_bbox_present_and_focused_and_uncalibrated()
    {
        var engine = new SpyAutoCalibrationEngine();
        var trigger = Build(engine, bbox: new CaptureRect(0, 0, 64, 64), focused: true);
        await trigger.OnAreaChangedAsync(Area);
        engine.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Does_not_attempt_when_game_unfocused()
    {
        var engine = new SpyAutoCalibrationEngine();
        var trigger = Build(engine, bbox: new CaptureRect(0, 0, 64, 64), focused: false);
        await trigger.OnAreaChangedAsync(Area);
        engine.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Attempts_when_only_a_bundled_baseline_exists()
    {
        var engine = new SpyAutoCalibrationEngine();
        var svc = new FakeCalibrationService();
        svc.Seed(Area, new AreaCalibration(1, 0, 0, 0, 4, 3) { Source = CalibrationSource.BundledBaseline });
        var trigger = Build(engine, bbox: new CaptureRect(0, 0, 64, 64), focused: true, service: svc);
        await trigger.OnAreaChangedAsync(Area);
        engine.Calls.Should().Be(1, "a bundled baseline is upgradeable by the auto path");
    }

    [Fact]
    public async Task Does_not_overwrite_an_existing_user_refinement()
    {
        var engine = new SpyAutoCalibrationEngine();
        var svc = new FakeCalibrationService();
        svc.Seed(Area, new AreaCalibration(1, 0, 0, 0, 6, 0.5) { Source = CalibrationSource.UserRefinement });
        var trigger = Build(engine, bbox: new CaptureRect(0, 0, 64, 64), focused: true, service: svc);
        await trigger.OnAreaChangedAsync(Area);
        engine.Calls.Should().Be(0, "the auto path never displaces a user refinement");
    }

    [Fact]
    public async Task Does_not_overwrite_an_existing_auto_capture()
    {
        var engine = new SpyAutoCalibrationEngine();
        var svc = new FakeCalibrationService();
        svc.Seed(Area, new AreaCalibration(1, 0, 0, 0, 6, 0.5) { Source = CalibrationSource.AutoCapture });
        var trigger = Build(engine, bbox: new CaptureRect(0, 0, 64, 64), focused: true, service: svc);
        await trigger.OnAreaChangedAsync(Area);
        engine.Calls.Should().Be(0, "an existing auto-capture is not re-attempted on every zone-in");
    }

    [Fact]
    public async Task Debounces_repeated_area_events()
    {
        var engine = new SpyAutoCalibrationEngine();
        var trigger = Build(engine, bbox: new CaptureRect(0, 0, 64, 64), focused: true);
        await trigger.OnAreaChangedAsync(Area);
        await trigger.OnAreaChangedAsync(Area); // immediate repeat of the SAME area
        engine.Calls.Should().Be(1, "a repeated area-changed for the same area is debounced");
    }

    private static AutoCalibrationTrigger Build(
        SpyAutoCalibrationEngine engine, CaptureRect? bbox, bool focused, FakeCalibrationService? service = null)
        => new(
            new FakeDomainEventSubscriber(),
            engine,
            new FakeRegionProvider(bbox),
            new FakeWindowLocator(focused ? new GameWindow(1, new CaptureRect(0, 0, 1920, 1080)) : null),
            service ?? new FakeCalibrationService(),
            logger: null);
}
