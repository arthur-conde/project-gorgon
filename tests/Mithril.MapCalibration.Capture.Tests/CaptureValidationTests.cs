using System;
using FluentAssertions;
using Mithril.MapCalibration.Capture;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class CaptureValidationTests
{
    [Fact]
    public void ToGray_uses_bt601_luma()
    {
        // single white pixel BGRA = (255,255,255,255) → gray 255
        var f = new CapturedFrame(1, 1, new byte[] { 255, 255, 255, 255 });
        f.ToGray().Pixels[0].Should().Be(255);
    }

    [Fact]
    public void Validate_rejects_black_frame()
    {
        var black = new CapturedFrame(8, 8, new byte[8 * 8 * 4]); // all zero
        new CaptureValidation().Validate(black, new CaptureRect(0, 0, 8, 8), out var reason)
            .Should().BeFalse();
        reason.Should().Contain("black");
    }

    [Fact]
    public void Validate_rejects_size_mismatch()
    {
        var f = new CapturedFrame(8, 8, new byte[8 * 8 * 4]);
        new CaptureValidation().Validate(f, new CaptureRect(0, 0, 16, 16), out var reason)
            .Should().BeFalse();
        reason.Should().Contain("size");
    }

    [Fact]
    public void Validate_accepts_a_non_black_correct_size_frame()
    {
        var px = new byte[8 * 8 * 4];
        Array.Fill(px, (byte)200);
        new CaptureValidation().Validate(new CapturedFrame(8, 8, px), new CaptureRect(0, 0, 8, 8), out _)
            .Should().BeTrue();
    }
}
