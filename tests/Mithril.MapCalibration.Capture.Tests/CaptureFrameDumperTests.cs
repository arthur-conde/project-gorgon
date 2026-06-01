using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Mithril.MapCalibration.Capture;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// #966 Task 3: the capture-frame PNG dump. Exercises the WPF/WIC encode path
/// (NOT System.Drawing — decoder-free guard #921) and the CaptureService wiring
/// behind the OFF-by-default debug flag.
/// </summary>
public sealed class CaptureFrameDumperTests
{
    [Fact]
    public void DumpColor_writes_a_readable_png()
    {
        var px = new byte[16 * 12 * 4];
        for (int i = 0; i < px.Length; i++) px[i] = (byte)(i % 251);
        var frame = new CapturedFrame(16, 12, px);

        var path = new CaptureFrameDumper(null).DumpColor(frame, "unit-color");

        path.Should().NotBeNull();
        File.Exists(path!).Should().BeTrue();
        // PNG magic number — proves we wrote a real PNG, not garbage.
        var header = new byte[8];
        using (var fs = File.OpenRead(path!)) { _ = fs.Read(header, 0, 8); }
        header.Should().StartWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }); // \x89 P N G
        File.Delete(path!);
    }

    [Fact]
    public void DumpGray_writes_a_readable_png()
    {
        var gray = new GrayImage(10, 8, new byte[10 * 8]);
        for (int i = 0; i < gray.Pixels.Length; i++) gray.Pixels[i] = (byte)(i * 3);

        var path = new CaptureFrameDumper(null).DumpGray(gray, "unit-gray");

        path.Should().NotBeNull();
        File.Exists(path!).Should().BeTrue();
        File.Delete(path!);
    }

    [Fact]
    public async Task CaptureService_does_not_dump_when_the_flag_is_off()
    {
        var px = new byte[8 * 8 * 4]; Array.Fill(px, (byte)180);
        // Unique bbox → unique tag, so this assertion can't race another test's dumps.
        var svc = new CaptureService(new FakeCapture(new CapturedFrame(8, 8, px)),
            new FakeBlanker(), new CaptureValidation(), null,
            new CaptureDiagnosticsOptions { DumpCaptureFrames = false });

        (await svc.CaptureMapAsync(new CaptureRect(111, 222, 8, 8), default)).Should().NotBeNull();

        ListDumps("map-111x222-8x8").Should().BeEmpty("the dump must stay off by default");
    }

    [Fact]
    public async Task CaptureService_dumps_the_validated_frame_when_the_flag_is_on()
    {
        var px = new byte[8 * 8 * 4]; Array.Fill(px, (byte)180);
        var svc = new CaptureService(new FakeCapture(new CapturedFrame(8, 8, px)),
            new FakeBlanker(), new CaptureValidation(), null,
            new CaptureDiagnosticsOptions { DumpCaptureFrames = true, DumpGrayFrames = true });

        var marker = $"map-3x4-8x8"; // bbox-derived tag (X=3,Y=4,W=8,H=8)
        var existingBefore = ListDumps(marker);

        (await svc.CaptureMapAsync(new CaptureRect(3, 4, 8, 8), default)).Should().NotBeNull();

        var after = ListDumps(marker);
        after.Length.Should().BeGreaterThan(existingBefore.Length,
            "a color (and gray) dump must be written when the flag is on");

        foreach (var f in after) { try { File.Delete(f); } catch { /* best-effort cleanup */ } }
    }

    private static string[] ListDumps(string marker) =>
        Directory.Exists(CaptureFrameDumper.DumpDirectory)
            ? Directory.GetFiles(CaptureFrameDumper.DumpDirectory, $"{marker}-*.png")
            : Array.Empty<string>();

    private sealed class FakeBlanker : IOverlayBlanker
    {
        public Task<IAsyncDisposable> BlankAsync() =>
            Task.FromResult<IAsyncDisposable>(new Noop());

        private sealed class Noop : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeCapture(CapturedFrame frame) : IScreenCapture
    {
        public CapturedFrame? Capture(CaptureRect rect) => frame;
    }
}
